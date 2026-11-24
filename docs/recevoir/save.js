// Browser download manager and service worker move one bounded block at a time.
import {deferred, deadline} from "./wire.js";

let registration;
export function prepareDownloads() {
  if (!navigator.serviceWorker) return;
  registration = navigator.serviceWorker.register(new URL("./sw.js",import.meta.url)).then(() => navigator.serviceWorker.ready);
  registration.catch(() => {});
}

export async function saveRemote(source, filename, size, mime, progress) {
  const ready = registration && await deadline(registration, 8000, "Téléchargement indisponible.").catch(() => null);
  if (ready?.active) {
    const port = new MessageChannel();
    const ticket = crypto.randomUUID();
    const ack = deferred(), finished = deferred();
    finished.promise.catch(() => {});
    let received = 0;
    let timer;
    const cancel = () => { clearTimeout(timer); void source.cancel(); port.port1.close(); };
    const arm = () => {
      clearTimeout(timer);
      timer = setTimeout(() => { cancel(); finished.reject(new Error("Le téléchargement a été interrompu.")); }, 120_000);
    };
    port.port1.onmessage = async e => {
      try {
        if (e.data === "ready") { ack.resolve(); return; }
        if (e.data === "cancel") throw new Error("Téléchargement annulé.");
        if (e.data === "saved") { finished.resolve(); return; }
        if (e.data !== "pull") return;
        arm();
        const {bytes, done} = await source.next();
        received += bytes.byteLength;
        if (size !== null && (received > size || (done && received !== size))) throw new Error("Téléchargement incomplet.");
        progress(received);
        port.port1.postMessage({buffer:bytes.buffer, done}, [bytes.buffer]);
      } catch (error) {
        port.port1.postMessage({error:true});
        finished.reject(error);
      }
    };
    ready.active.postMessage({op:"download", ticket, filename, size, mime}, [port.port2]);
    try {
      await deadline(ack.promise, 6000, "Le navigateur n’a pas préparé le téléchargement.");
      const a = document.createElement("a");
      a.href = new URL("./download/" + ticket,import.meta.url);
      // A normal navigation reaches the service worker. The attachment header starts the browser download.
      document.body.append(a); a.click(); a.remove();
      arm();
      await finished.promise;
      return;
    } finally { cancel(); }
  }
  // A browser which disables service workers can still save small transfers.
  const chunks = [];
  let count = 0;
  try {
    while (true) {
      const {bytes, done} = await source.next();
      count += bytes.length;
      if (count > 64 * 1024 * 1024) throw new Error("Ouvrez le lien dans une fenêtre autorisant les service workers pour enregistrer ce fichier volumineux.");
      chunks.push(bytes);
      progress(count);
      if (done) break;
    }
    if (size !== null && count !== size) throw new Error("Téléchargement incomplet.");
    const url = URL.createObjectURL(new Blob(chunks, {type:mime}));
    const a = document.createElement("a"); a.href = url; a.download = filename; a.click();
    setTimeout(() => URL.revokeObjectURL(url), 60_000);
  } finally { await source.cancel(); }
}
