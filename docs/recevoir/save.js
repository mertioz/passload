const memoryLimit = 1024 * 1024;
let previous;

function createDiskWriter() {
  const worker = new Worker(new URL("./disk.js",import.meta.url));
  const call = (op, buffer) => new Promise((resolve,reject)=>{
    const timer = setTimeout(()=>reject(new Error("Le stockage local ne répond pas.")), 90_000);
    worker.onmessage = e => {
      clearTimeout(timer);
      if (e.data.ok) resolve(e.data.result);
      else reject(new Error("Le navigateur ne peut pas écrire sur le disque de cet appareil."));
    };
    worker.onerror = () => { clearTimeout(timer); reject(new Error("Le stockage local est indisponible.")); };
    worker.postMessage({op,buffer}, buffer ? [buffer] : []);
  });
  return {call, close:()=>worker.terminate()};
}

function present(blob, filename, temporaryName) {
  const url = URL.createObjectURL(blob);
  const action = document.getElementById("saved");
  action.href = url; action.download = filename;
  action.hidden = false; action.textContent = "Enregistrer à nouveau";
  action.click();
  const entry = {url};
  previous = entry;
  // Keep the snapshot alive while the browser copies it to Downloads.
  const retention = Math.min(8 * 86400_000,Math.max(10 * 60_000, blob.size / 512_000 * 1000));
  setTimeout(async () => {
    URL.revokeObjectURL(url);
    if (previous === entry) { action.hidden = true; action.removeAttribute("href"); }
    if (temporaryName) {
      try {
        const root = await navigator.storage.getDirectory();
        const directory = await root.getDirectoryHandle("passload-transferts");
        await directory.removeEntry(temporaryName);
      } catch { /* A later visit also cleans up old temporary snapshots. */ }
    }
  },retention);
}

export function chooseDestination(filename, estimate) {
  if (estimate >= 256 * 1024 * 1024 && typeof window.showSaveFilePicker === "function")
    return window.showSaveFilePicker({suggestedName:filename});
  return null;
}

export async function saveRemote(source, filename, size, mime, progress, destination) {
  let chunks = [], count = 0, disk, writable, completed = false, diskUnavailable = false;
  document.getElementById("saved").hidden = true;
  try {
    if (destination) writable = await destination.createWritable();
    while (true) {
      const {bytes,done} = await source.next();
      count += bytes.byteLength;
      if (size !== null && (count > size || (done && count !== size))) throw new Error("Téléchargement incomplet.");
      if (!writable && !disk && !diskUnavailable && count > memoryLimit) {
        try {
          disk = createDiskWriter();
          await disk.call("open");
        } catch {
          disk?.close(); disk = null; diskUnavailable = true;
        }
        if (disk) {
          for (const block of chunks) await disk.call("write",block);
          chunks = [];
        }
      }
      if (bytes.byteLength) {
        if (writable) await writable.write(bytes);
        else if (disk) await disk.call("write",bytes.buffer);
        else {
          if (count > 64 * 1024 * 1024) throw new Error("Le stockage temporaire de votre navigateur est indisponible. Ouvrez le lien dans une fenêtre normale pour enregistrer ce fichier volumineux.");
          chunks.push(bytes.buffer);
        }
      }
      progress(count);
      if (done) break;
    }
    if (writable) { await writable.close(); writable = null; }
    else if (disk) {
      const {file,name} = await disk.call("finish");
      if (file.size !== count) throw new Error("Téléchargement incomplet.");
      present(file,filename,name);
    } else present(new Blob(chunks,{type:mime}),filename,null);
    completed = true;
  } finally {
    if (!completed) {
      if (writable) await writable.abort().catch(()=>{});
      if (disk) await disk.call("discard").catch(()=>{});
    }
    disk?.close();
    await source.cancel();
  }
}
