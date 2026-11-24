import {spawn} from "node:child_process";
import {mkdtemp, rm, writeFile} from "node:fs/promises";
import {tmpdir} from "node:os";
import {join,resolve} from "node:path";
import {createInterface} from "node:readline";
import {once} from "node:events";
import {chromium} from "playwright-core";
import {files} from "./fixtures.mjs";
import {testChannel} from "./ci-channel.mjs";

const folder = await mkdtemp(join(tmpdir(),"passload-source-"));
for (const [name,data] of files) await writeFile(join(folder,name),data);
const program = resolve("../Passload.Checks/bin/Release/net10.0/Passload.Checks.dll");
const source = spawn("dotnet",[program,"--browser-fixture",folder],{stdio:["pipe","pipe","inherit"]});
const output = createInterface({input:source.stdout});
const initialization = once(output,"line");
const browser = await chromium.launch({channel:"msedge",headless:true});
try {
  const [line] = await initialization;
  const init = JSON.parse(line);
  const page = await browser.newPage();
  await page.addInitScript(()=>{
    window.chrome ??= {};
    const listeners = [];
    window.chrome.webview = {
      addEventListener:(_type,callback)=>listeners.push(callback),
      postMessage:msg=>{window.fixtureState = msg.op;}
    };
    window.startFixture = message => listeners.forEach(f=>f({data:message}));
  });
  await page.goto(init.page);
  await page.waitForFunction(()=>window.fixtureState==="boot");
  await page.evaluate(message=>window.startFixture(message),init.message);
  await page.waitForFunction(()=>window.fixtureState==="online",null,{timeout:35_000});
  console.log("Source Windows en ligne. Partage de trois fichiers de démonstration.");
  await Promise.all(["chromium","firefox","webkit"].map(async name=>{
    await testChannel(name,"invite",init.link);
    console.log("Invitation de test reçue par "+name+".");
    await testChannel(name,"done");
  }));
} finally {
  await browser.close(); output.close();
  if (source.exitCode === null) { const done = once(source,"exit"); source.stdin.end("\n"); await done; }
  await rm(folder,{recursive:true,force:true});
}
