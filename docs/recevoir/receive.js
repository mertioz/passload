import {Peer, deadline, deferred, decode, control, asControl} from "./wire.js";
import {Meeting} from "./pipe.js";
import {chooseDestination, saveRemote} from "./save.js";

const el = id => document.getElementById(id);
const invite = location.hash.match(/^#p2\.([A-Za-z0-9_-]{87})\.([A-Za-z0-9_-]{43})\.(\d{10,13})$/);
const roomId = invite?.[1], key = invite?.[2], until = Number(invite?.[3]);
const offline = "Le partage est arrêté, le lien a expiré ou le PC expéditeur est hors ligne.";
let current, busy = false, archiveEstimate = 0;
const show = text => { el("status").textContent = text; };
const formatSize = n => {
  if (!n) return "0 o";
  const i = Math.min(5, Math.floor(Math.log(n) / Math.log(1024)));
  return new Intl.NumberFormat("fr-FR", {maximumFractionDigits:1}).format(n / 1024 ** i) + " " + ["o","Ko","Mo","Go","To","Po"][i];
};

class Incoming {
  constructor(onDrop) { this.onDrop = onDrop; this.chosen = deferred(); this.nextId = 1; this.closed = false; }
  async connect(forceRelay) {
    const socket = await Meeting.create({room:roomId, key}, msg => {
      if (msg.op === "ready") this.peer = new Peer(socket, msg.peer, p => this.packet(p), () => this.drop());
    }, async (id, type, bytes) => {
      if (this.peer?.id !== id) return;
      if (type === 2) { if (this.peer.transport === "relay") this.packet(bytes); return; }
      const msg = JSON.parse(decode(bytes));
      if (msg.used && msg.used === this.peer.transport) {
        if (msg.used === "direct") socket.direct(id);
        this.chosen.resolve();
      }
      else await this.peer.negotiate(msg);
    }, () => this.drop());
    this.socket = socket;
    let ready;
    try { ready = await deadline(socket.ready.promise, 18_000, offline); }
    catch { throw new Error(offline); }
    this.canRelay = ready.relay;
    show("Recherche d’une connexion directe…");
    let direct = false;
    if (this.peer.pc && !(forceRelay && ready.relay)) {
      try {
        await deadline(this.peer.offer(), 6000, "Connexion directe indisponible.");
        direct = await deadline(this.peer.direct.promise, ready.relay ? 6_000 : 15_000, "Connexion directe indisponible.");
      } catch { /* Some browsers or networks cannot establish a data channel. */ }
    }
    if (!direct && !ready.relay) throw new Error("Les réseaux des deux appareils bloquent la connexion directe. Réessayez depuis un autre réseau.");
    const mode = direct ? "direct" : "relay";
    if (!direct) show("Ouverture du relais chiffré…");
    this.mode = mode;
    this.peer.select(mode);
    await socket.signal(ready.peer, {use:mode});
    await deadline(this.chosen.promise, 10_000, offline);
    return mode;
  }
  packet(bytes) {
    const req = this.request;
    if (!req || !bytes.length) return;
    if (bytes[0] === 2 || bytes[0] === 3) {
      if (!req.batch || bytes.length < 5 || bytes.length > 5 + 2 * 1024 * 1024 ||
        new DataView(bytes.buffer,bytes.byteOffset,bytes.byteLength).getUint32(1) !== req.id) return;
      req.batch.resolve({bytes:bytes.slice(5), done:bytes[0] === 3});
      return;
    }
    if (bytes[0] === 1) {
      if (bytes.length < 5 || new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength).getUint32(1) !== req.id || !req.batch) return;
      req.parts.push(bytes.subarray(5)); req.length += bytes.length - 5;
      if (req.length > 512 * 1024) this.drop();
      return;
    }
    const msg = asControl(bytes);
    if (msg.id !== req.id) return;
    if (msg.t === "start") req.started.resolve();
    if (msg.t === "error") {
      const error = new Error(msg.message || offline);
      req.started.reject(error); req.batch?.reject(error);
    }
    if (msg.t === "batch" && req.batch) {
      const block = new Uint8Array(req.length);
      let offset = 0;
      for (const part of req.parts) { block.set(part, offset); offset += part.length; }
      req.batch.resolve({bytes:block, done:msg.done === true});
    }
  }
  async open(item) {
    if (this.closed || this.request) throw new Error("Connexion indisponible.");
    const req = {id:this.nextId++, started:deferred()};
    req.started.promise.catch(() => {});
    this.request = req;
    const cancel = async () => {
      if (this.request !== req) return;
      this.request = null;
      await this.peer.send(control({t:"cancel", id:req.id})).catch(() => {});
    };
    try {
      await this.peer.send(control({t:"open", id:req.id, item}));
      await deadline(req.started.promise, 30_000, offline);
    } catch (e) { await cancel(); throw e; }
    return {cancel, next:async () => {
      if (this.closed || this.request !== req) throw new Error(offline);
      req.parts = []; req.length = 0; req.batch = deferred();
      req.batch.promise.catch(() => {});
      await this.peer.send(control({t:"pull", id:req.id}));
      const result = await deadline(req.batch.promise, 90_000, "Le transfert a été interrompu.");
      req.parts = []; req.batch = null;
      if (result.done) this.request = null;
      return result;
    }};
  }
  async manifest() {
    const source = await this.open("manifest");
    const parts = [];
    let length = 0;
    try {
      while (true) {
        const result = await source.next();
        length += result.bytes.length;
        if (length > 4 * 1024 * 1024) throw new Error("Liste de fichiers trop volumineuse.");
        parts.push(result.bytes);
        if (result.done) break;
      }
      return JSON.parse(await new Blob(parts).text());
    } finally { await source.cancel(); }
  }
  drop() {
    if (this.closed) return;
    this.closed = true;
    const err = new Error(offline);
    this.request?.started.reject(err); this.request?.batch?.reject(err);
    this.peer?.close(); this.socket?.close();
    this.onDrop();
  }
}
function enableButtons(enabled) { for (const button of el("files").querySelectorAll("button")) button.disabled = !enabled; }

async function download(file) {
  if (busy || !current || current.closed) return;
  busy = true; enableButtons(false);
  let source;
  try {
    const name = file?.name || "Passload.zip";
    const destination = await chooseDestination(name, file?.size ?? archiveEstimate);
    source = await current.open(file ? file.id : "zip");
    await saveRemote(source, name, file?.size ?? null, file ? "application/octet-stream" : "application/zip", bytes => show("Téléchargement · " + formatSize(bytes)), destination);
    show("Téléchargement terminé.");
  } catch (err) { show(err.name === "AbortError" ? "Téléchargement annulé." : err.message); }
  finally { await source?.cancel(); busy = false; if (current && !current.closed) enableButtons(true); }
}
async function connect(forceRelay = false) {
  if (!roomId || !key) { show("Ouvrez le lien complet envoyé par l’expéditeur."); return; }
  if (until <= Date.now()) { show("Ce lien a expiré."); return; }
  current?.drop(); el("retry").hidden = true; el("files").hidden = true;
  show("Connexion à l’expéditeur…");
  const client = new Incoming(() => {
    if (client === current) { show(offline); enableButtons(false); el("retry").hidden = false; }
  });
  current = client;
  try {
    const mode = await client.connect(forceRelay);
    const manifest = await client.manifest();
    if (!Array.isArray(manifest.files) || !manifest.files.length || manifest.files.some((f,i) =>
      f.id !== i || typeof f.name !== "string" || !Number.isSafeInteger(f.size) || f.size < 0)) throw new Error("Liste de fichiers invalide.");
    el("title").textContent = manifest.files.length === 1 ? "Un fichier partagé" : manifest.files.length + " fichiers partagés";
    archiveEstimate = manifest.files.reduce((total,file)=>total + file.size + 256, 0);
    el("list").replaceChildren();
    for (const file of manifest.files) {
      const row = document.createElement("li"), details = document.createElement("div");
      const name = document.createElement("b"), size = document.createElement("small"), action = document.createElement("button");
      name.textContent = file.name; size.textContent = formatSize(file.size);
      action.textContent = "Télécharger"; action.setAttribute("aria-label", "Télécharger " + file.name);
      action.onclick = () => void download(file);
      details.append(name,size); row.append(details,action); el("list").append(row);
    }
    el("zip").hidden = manifest.files.length < 2;
    el("expiry").textContent = "Expire le " + new Date(manifest.expiresAt).toLocaleString("fr-FR");
    el("files").hidden = false; enableButtons(true);
    show(mode === "direct" ? "Connexion directe avec l’expéditeur." : "Connexion par relais chiffré.");
  } catch (err) {
    client.drop();
    if (client.mode === "direct" && client.canRelay && !forceRelay) { await connect(true); return; }
    show(err.message || offline); el("retry").hidden = false;
  }
}
el("retry").onclick = () => void connect();
el("zip").onclick = () => void download(null);
window.addEventListener("pagehide", () => current?.drop());
void connect();
