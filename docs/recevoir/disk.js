// OPFS is temporary storage on the recipient's device. A completed File is
// handed to the download manager only after all authenticated blocks arrive.
let directory, name, handle, sync, writable, offset = 0;

async function open() {
  const root = await navigator.storage.getDirectory();
  directory = await root.getDirectoryHandle("passload-transferts",{create:true});
  const cutoff = Date.now() - 8 * 86400_000;
  for await (const [entry] of directory.entries()) {
    const match = /^(\d{13})-[a-f0-9-]{36}\.part$/.exec(entry);
    if (match && Number(match[1]) < cutoff) await directory.removeEntry(entry).catch(()=>{});
  }
  name = Date.now() + "-" + crypto.randomUUID() + ".part";
  handle = await directory.getFileHandle(name,{create:true});
  if (typeof handle.createSyncAccessHandle === "function") sync = await handle.createSyncAccessHandle();
  else writable = await handle.createWritable();
}
async function finish() {
  if (sync) { sync.flush(); sync.close(); sync = null; }
  if (writable) { await writable.close(); writable = null; }
  const file = await handle.getFile();
  if (file.size !== offset) throw new Error("Fichier temporaire incomplet.");
  return {file, name};
}
async function discard() {
  if (sync) { sync.close(); sync = null; }
  if (writable) { await writable.abort().catch(()=>{}); writable = null; }
  if (name && directory) await directory.removeEntry(name).catch(()=>{});
}
self.onmessage = async e => {
  try {
    const {op,buffer} = e.data;
    if (op === "open") await open();
    if (op === "write") {
      const data = new Uint8Array(buffer);
      if (sync) {
        let pos = 0;
        while (pos < data.length) {
          const n = sync.write(data.subarray(pos),{at:offset + pos});
          if (!n) throw new Error("Espace disque insuffisant.");
          pos += n;
        }
      } else await writable.write(data);
      offset += data.length;
    }
    if (op === "discard") await discard();
    self.postMessage({ok:true, result:op === "finish" ? await finish() : null});
  } catch {
    await discard().catch(()=>{});
    self.postMessage({ok:false});
  }
};
