
import assert from "node:assert/strict";
import {execFileSync} from "node:child_process";
import {createHash, X509Certificate} from "node:crypto";
import {mkdtemp, readFile, rm} from "node:fs/promises";
import {isIP} from "node:net";
import {tmpdir} from "node:os";
import {join} from "node:path";
import {connect} from "node:tls";
import {chromium, firefox} from "playwright-core";

const link = new URL(process.env.PASSLOAD_BROWSER_TEST_LINK ??
  await readFile(process.env.PASSLOAD_SMOKE_OUTPUT, "utf8"));
assert.match(link.pathname, /^\/s\/[A-Za-z0-9_-]{43}$/);
assert(isIP(link.hostname.replace(/^\[|\]$/g, "")));
const expected = {
  "probe.txt": Buffer.from("Passload direct address smoke test."),
  "probe.bin": Buffer.alloc(1_048_713, Buffer.from(Array.from({length:256}, (_, index) => index))),
};
const sha256 = bytes => createHash("sha256").update(bytes);
const publicAddress = link.protocol === "https:";
const loopback = link.hostname === "127.0.0.1";
const args = [];
if (publicAddress) {
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

async function testReceiver(browser, name, webRtcBlocked, output) {
  const context = await browser.newContext({
    acceptDownloads:true,
    // The fixture certificate was pinned above. Firefox uses a fresh isolated context.
    ignoreHTTPSErrors:publicAddress && name === "firefox",
  });
  try {
    if (webRtcBlocked) {
      await context.addInitScript(() => Object.defineProperty(window, "RTCPeerConnection", {value:undefined}));
    } else {
      await context.addInitScript(() => {
        const Native = window.RTCPeerConnection;
        window.rtcProbe = { candidates:[], remote:[], iceErrors:[], state:[] };
        window.RTCPeerConnection = class extends Native {
          constructor(configuration) {
            super(configuration);
            this.addEventListener("icecandidate", ({candidate}) => {
              if (candidate) window.rtcProbe.candidates.push(candidate.type + "/" + candidate.protocol);
            });
            this.addEventListener("icecandidateerror", ({errorCode}) => window.rtcProbe.iceErrors.push(errorCode));
            this.addEventListener("iceconnectionstatechange", () => window.rtcProbe.state.push(this.iceConnectionState));
          }
          setRemoteDescription(description) {
            window.rtcProbe.remote.push(...[...description.sdp.matchAll(/ typ ([a-z]+)/g)].map(m => m[1]));
            return super.setRemoteDescription(description);
          }
          addIceCandidate(candidate) {
            if (candidate?.candidate) window.rtcProbe.remote.push(candidate.candidate.match(/ typ ([a-z]+)/)?.[1] ?? "unknown");
            return super.addIceCandidate(candidate);
          }
        };
      });
    }
    const page = await context.newPage();
    const signalling = [];
    page.on("websocket", socket => {
      socket.on("framereceived", ({payload}) => {
        if (typeof payload !== "string" || signalling.length >= 25) return;
        try {
          const {op, signal} = JSON.parse(payload);
          signalling.push(signal?.sdp?.type ?? (signal?.ice ? "ice" : signal?.used ?? op));
        } catch { signalling.push("invalid"); }
      });
      socket.on("socketerror", error => signalling.push(error.match(/\b[45]\d\d\b/)?.[0] ?? "socketerror"));
    });
    await page.goto(link.href, {waitUntil:"domcontentloaded"}).catch(() => {
      throw new Error("La page du PC expéditeur est inaccessible.");
    });
    const directStatus = "Connexion WebRTC directe.", httpsStatus = "Téléchargement HTTPS direct.";
    const status = page.locator("#status");
    await page.waitForFunction(modes => modes.includes(document.getElementById("status")?.textContent),
      [directStatus, httpsStatus], {timeout:40_000}).catch(async () => {
      throw new Error("Statut reçu · " + await status.textContent());
    });
    const webRtc = await status.textContent() === directStatus;
    if (webRtcBlocked) assert(!webRtc);
    else if (loopback) assert(webRtc, "WebRTC doit fonctionner en boucle locale.");
    else if (!webRtc) {
      assert(signalling.includes("ready") && signalling.includes("answer"), "La signalisation HTTPS doit aboutir avant le secours.");
      console.log(name + " · " + JSON.stringify(await page.evaluate(() => window.rtcProbe)));
    }
    if (webRtc) {
      // The browser cannot pass this check by masking a failed transfer with HTTPS.
      await context.route(/\/(?:files\/\d+|all\.zip)$/, route => route.abort());
    }
    for (const file of ["probe.txt", "Passload.zip"]) {
      const action = page.getByRole("link", {name:file.endsWith(".zip") ? "Tout télécharger en ZIP" : "Télécharger " + file, exact:true});
      const [download] = await Promise.all([page.waitForEvent("download", {timeout:30_000}), action.click()])
        .catch(async () => { throw new Error("Téléchargement indisponible · " + await status.textContent()); });
      const saved = join(output, file);
      await download.saveAs(saved);
      assert.equal(download.suggestedFilename(), file);
      if (!file.endsWith(".zip")) assert.deepEqual(await readFile(saved), expected[file]);
      else {
        const checkZip = "import sys,zipfile,hashlib,json\nwith zipfile.ZipFile(sys.argv[1]) as z: print(json.dumps({n:hashlib.sha256(z.read(n)).hexdigest() for n in z.namelist()}))";
        const hashes = JSON.parse(execFileSync(process.platform === "win32" ? "python" : "python3",
          ["-c", checkZip, saved], {encoding:"utf8"}));
        assert.deepEqual(hashes, Object.fromEntries(Object.entries(expected).map(([filename, bytes]) => [filename, sha256(bytes).digest("hex")])));
      }
      if (webRtc) await page.waitForFunction(() => document.getElementById("status")?.textContent === "Téléchargement WebRTC terminé.");
    }
    const mode = webRtc ? "WebRTC direct avec les téléchargements HTTPS bloqués"
      : webRtcBlocked ? "HTTPS de secours avec WebRTC désactivé" : "Secours HTTPS après négociation WebRTC";
    console.log(name + " · " + mode + ", fichier et ZIP identiques.");
  } finally { await context.close(); }
}

const output = await mkdtemp(join(tmpdir(), "passload-public-"));
try {
  const edge = process.platform === "win32";
  const engines = edge ? [["chromium",chromium]] : [["chromium",chromium],["firefox",firefox]];
  for (const [name, engine] of engines) {
    const options = name === "chromium" ? {headless:true, args, channel:edge ? "msedge" : undefined} : {headless:true};
    const browser = await engine.launch(options);
    try {
      await testReceiver(browser, name, false, output);
      await testReceiver(browser, name, true, output);
    } finally { await browser.close(); }
  }
} finally { await rm(output, {recursive:true, force:true}); }
