import assert from "node:assert/strict";
import {createHash} from "node:crypto";
import {execFileSync} from "node:child_process";
import {mkdtemp, readFile, rm} from "node:fs/promises";
import {tmpdir} from "node:os";
import {join} from "node:path";
import {chromium, firefox, webkit} from "playwright-core";
import {files} from "./fixtures.mjs";
import {testChannel} from "./ci-channel.mjs";

const name = process.env.PASSLOAD_BROWSER || "chromium";
const link = new URL(await testChannel(name,"invite"));
if (link.origin !== "https://mertioz.github.io" || link.pathname !== "/passload/recevoir/" || !link.hash.startsWith("#p2.")) throw new Error("Une invitation de test Passload est requise.");
const engine = {chromium,firefox,webkit}[name];
if (!engine) throw new Error("Navigateur de test inconnu.");
const output = await mkdtemp(join(tmpdir(),"passload-browser-"));
const browser = await engine.launch({headless:true});
const sha = data => createHash("sha256").update(data).digest("hex");
try {
  for (const forced of (name === "chromium" ? [false,true] : [false])) {
    const context = await browser.newContext({acceptDownloads:true});
    try {
      if (forced) await context.addInitScript(()=>Object.defineProperty(window,"RTCPeerConnection",{value:undefined}));
      const page = await context.newPage();
      await page.goto(link.href, {waitUntil:"domcontentloaded"});
      await page.locator("#zip:visible").waitFor({timeout:65_000}).catch(async () => {
        throw new Error("Réception indisponible. " + await page.locator("#status").innerText());
      });
      const mode = await page.locator("html").getAttribute("data-transfer");
      assert.equal(await page.locator("header").count(), 0);
      assert(await page.locator("#status").isHidden());
      console.log(name + (forced ? " · WebRTC bloqué · " : " · ") + mode);
      if (forced) assert.equal(mode,"relay");
      if (mode === "webrtc") await context.route("https://ppng.io/**", route => route.abort());
      assert.equal(await page.locator("#list li").count(),files.size);
      for (const [file,expected] of [...files,["Passload.zip",null]]) {
        const label = expected === null ? "Tout télécharger en ZIP" : "Télécharger "+file;
        const action = page.getByRole("button",{name:label,exact:true});
        const [download] = await Promise.all([page.waitForEvent("download",{timeout:60_000}),action.click()]);
        const target = join(output,file);
        try { await download.saveAs(target); }
        catch { throw new Error("Échec de l’enregistrement de "+file+". "+await page.locator("#status").innerText()); }
        assert.equal(download.suggestedFilename(),file);
        if (expected !== null) assert.equal(sha(await readFile(target)),sha(expected));
        else {
          const code = "import sys,zipfile,hashlib,json\nwith zipfile.ZipFile(sys.argv[1]) as z: print(json.dumps({n: hashlib.sha256(z.read(n)).hexdigest() for n in z.namelist()}))";
          const hashes = JSON.parse(execFileSync(process.platform === "win32" ? "python" : "python3",["-c",code,target],{encoding:"utf8"}));
          assert.deepEqual(hashes,Object.fromEntries([...files].map(([n,b])=>[n,sha(b)])));
        }
        await page.waitForFunction(()=>document.querySelector("#status").textContent==="Téléchargement terminé.",null,{timeout:25_000});
        console.log("Fichier vérifié · "+file);
      }
      console.log("Trois fichiers et les trois entrées du ZIP vérifiés par SHA-256.");
    } finally { await context.close(); }
  }
} finally {
  await browser.close(); await rm(output,{recursive:true,force:true});
  await testChannel(name,"done","finished");
}
