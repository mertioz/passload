import assert from "node:assert/strict";
import {execFileSync} from "node:child_process";
import {createHash, X509Certificate} from "node:crypto";
import {mkdtemp, readFile, rm} from "node:fs/promises";
import {isIP} from "node:net";
import {tmpdir} from "node:os";
import {join} from "node:path";
import {connect} from "node:tls";
import {chromium} from "playwright-core";

const link = new URL(process.env.PASSLOAD_BROWSER_TEST_LINK ??
  await readFile(process.env.PASSLOAD_SMOKE_OUTPUT, "utf8"));
assert.match(link.pathname, /^\/s\/[A-Za-z0-9_-]{43}$/);
assert(isIP(link.hostname.replace(/^\[|\]$/g, "")));
const expected = {
  "probe.txt": Buffer.from("Passload direct address smoke test."),
  "probe.bin": Buffer.alloc(1_048_713, Buffer.from(Array.from({length:256}, (_, index) => index))),
};
const sha256 = bytes => createHash("sha256").update(bytes);
const args = [];
if (link.protocol === "https:") {
  const fingerprint = link.hash.match(/^#sha256=([0-9a-f]{64})$/i)?.[1]?.toLowerCase();
  assert(fingerprint);
  const pin = await new Promise((resolve, reject) => {
    const socket = connect({
      host:link.hostname.replace(/^\[|\]$/g, ""),
      port:Number(link.port || 443), rejectUnauthorized:false, timeout:6000,
    }, () => {
      try {
        const cert = new X509Certificate(socket.getPeerCertificate().raw);
        assert.equal(sha256(cert.raw).digest("hex"), fingerprint);
        resolve(sha256(cert.publicKey.export({type:"spki", format:"der"})).digest("base64"));
      } catch (error) {
        reject(error);
      } finally { socket.destroy(); }
    });
    socket.once("error", reject);
    socket.once("timeout", () => socket.destroy(new Error("Connexion HTTPS indisponible.")));
  });
  args.push("--ignore-certificate-errors-spki-list=" + pin);
} else {
  assert(link.protocol === "http:" && link.hostname === "127.0.0.1", "Le test HTTP est réservé à la boucle locale.");
}

const output = await mkdtemp(join(tmpdir(), "passload-public-"));
const browser = await chromium.launch({
  headless:true, channel:process.platform === "win32" ? "msedge" : undefined, args,
});
try {
  for (const webRtcBlocked of [false, true]) {
    const context = await browser.newContext({acceptDownloads:true});
    try {
      if (webRtcBlocked) {
        await context.addInitScript(() => Object.defineProperty(window, "RTCPeerConnection", {value:undefined}));
      } else {
        // The browser cannot pass this check by falling back to the HTTPS attachments.
        await context.route(/\/(?:files\/\d+|all\.zip)$/, route => route.abort());
      }
      const page = await context.newPage();
      page.on("pageerror", error => console.log("Erreur JavaScript · " + error.name));
      await page.goto(link.href, {waitUntil:"domcontentloaded"}).catch(() => {
        throw new Error("La page du PC expéditeur est inaccessible.");
      });
      const status = webRtcBlocked ? "Téléchargement HTTPS direct." : "Connexion WebRTC directe.";
      await page.waitForFunction(value => document.getElementById("status")?.textContent === value, status, {timeout:25_000})
        .catch(async () => { throw new Error("Statut reçu · " + await page.locator("#status").textContent()); });
      for (const file of ["probe.txt", "Passload.zip"]) {
        const action = page.getByRole("link", {name:file.endsWith(".zip") ? "Tout télécharger en ZIP" : "Télécharger " + file, exact:true});
        const [download] = await Promise.all([page.waitForEvent("download", {timeout:25_000}), action.click()])
          .catch(async () => { throw new Error("Téléchargement indisponible · " + await page.locator("#status").textContent()); });
        const saved = join(output, file);
        await download.saveAs(saved);
        assert.equal(download.suggestedFilename(), file);
        if (!file.endsWith(".zip")) assert.deepEqual(await readFile(saved), expected[file]);
        else {
          const checkZip = "import sys,zipfile,hashlib,json\nwith zipfile.ZipFile(sys.argv[1]) as z: print(json.dumps({n:hashlib.sha256(z.read(n)).hexdigest() for n in z.namelist()}))";
          const hashes = JSON.parse(execFileSync(process.platform === "win32" ? "python" : "python3",
            ["-c", checkZip, saved], {encoding:"utf8"}));
          assert.deepEqual(hashes, Object.fromEntries(Object.entries(expected).map(([name, bytes]) => [name, sha256(bytes).digest("hex")])));
        }
        if (!webRtcBlocked) await page.waitForFunction(() => document.getElementById("status")?.textContent === "Téléchargement WebRTC terminé.");
      }
      console.log(webRtcBlocked ? "HTTPS de secours avec WebRTC désactivé, fichier et ZIP identiques."
        : "WebRTC direct avec les téléchargements HTTPS bloqués, fichier et ZIP identiques.");
    } finally { await context.close(); }
  }
} finally {
  await browser.close();
  await rm(output, {recursive:true, force:true});
}
