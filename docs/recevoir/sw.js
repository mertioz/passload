const jobs = new Map();
self.addEventListener("install", event => event.waitUntil(self.skipWaiting()));
self.addEventListener("activate", event => event.waitUntil(self.clients.claim()));
self.addEventListener("message", event => {
  const {op, ticket, filename, size, mime} = event.data || {};
  const port = event.ports[0];
  if (op !== "download" || !port || !/^[0-9a-f-]{36}$/.test(ticket) || !event.source?.url || new URL(event.source.url).origin !== self.location.origin) return;
  const job = {port, filename:String(filename), size, mime};
  jobs.set(ticket, job);
  port.postMessage("ready");
  setTimeout(() => { if (jobs.delete(ticket)) { port.postMessage("cancel"); port.close(); } }, 60_000);
});
self.addEventListener("fetch", event => {
  if (!event.request.url.startsWith(self.registration.scope)) return;
  const ticket = event.request.url.slice(self.registration.scope.length).match(/^download\/([0-9a-f-]{36})$/)?.[1];
  if (!ticket) return;
  const job = jobs.get(ticket);
  if (!job) {
    event.respondWith(new Response("Téléchargement expiré.", {status:410})); return;
  }
  jobs.delete(ticket);
  const {port} = job;
  let pending;
  const stream = new ReadableStream({
    start(controller) {
      port.onmessage = event => {
        const {buffer, done, error} = event.data;
        if (error) { controller.error(new Error("Transfert interrompu.")); port.close(); }
        else {
          if (buffer?.byteLength) controller.enqueue(new Uint8Array(buffer));
          if (done) { controller.close(); port.postMessage("saved"); port.close(); }
        }
        pending?.(); pending = null;
      };
    },
    pull() { return new Promise(resolve => { pending = resolve; port.postMessage("pull"); }); },
    cancel() { port.postMessage("cancel"); port.close(); pending?.(); }
  });
  const cleanName = job.filename.replace(/[\u0000-\u001f\u007f\/\\]/g, "_") || "fichier";
  const headers = new Headers({
    "Content-Type":job.mime === "application/zip" ? job.mime : "application/octet-stream",
    "Content-Disposition":"attachment; filename=\"download\"; filename*=UTF-8''" + encodeURIComponent(cleanName).replace(/['()*]/g, c => "%" + c.charCodeAt(0).toString(16)),
    "X-Content-Type-Options":"nosniff", "Cache-Control":"no-store"
  });
  if (Number.isSafeInteger(job.size) && job.size >= 0) headers.set("Content-Length", String(job.size));
  event.respondWith(new Response(stream, {headers}));
});
