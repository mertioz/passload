import {Incoming} from "./incoming.js";
import {Meeting} from "./pipe.js";
import {chooseDestination, saveRemote} from "./save.js";

const el = id => document.getElementById(id);
const invite = location.hash.match(/^#p2\.([A-Za-z0-9_-]{87})\.([A-Za-z0-9_-]{43})\.(\d{10,13})$/);
const roomId = invite?.[1], key = invite?.[2], until = Number(invite?.[3]);
const offline = "Le partage est arrêté, le lien a expiré ou le PC expéditeur est hors ligne.";
let current, busy = false, archiveEstimate = 0;
const show = text => {
  el("status").textContent = text;
  el("status").hidden = !text;
};
const formatSize = n => {
  if (!n) return "0 o";
  const i = Math.min(5, Math.floor(Math.log(n) / Math.log(1024)));
  return new Intl.NumberFormat("fr-FR", {maximumFractionDigits:1}).format(n / 1024 ** i) + " " + ["o","Ko","Mo","Go","To","Po"][i];
};

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
    await source.confirm?.();
    show("Téléchargement terminé.");
  } catch (err) { show(err.name === "AbortError" ? "Téléchargement annulé." : err.message); }
  finally { await source?.cancel(); busy = false; if (current && !current.closed) enableButtons(true); }
}
async function connect(forceRelay = false) {
  if (!roomId || !key) { show("Ouvrez le lien complet envoyé par l’expéditeur."); return; }
  if (until <= Date.now()) { show("Ce lien a expiré."); return; }
  current?.drop(); el("retry").hidden = true; el("files").hidden = true;
  delete document.documentElement.dataset.transfer;
  show("Connexion à l’expéditeur…");
  const client = new Incoming((...callbacks) => Meeting.create({room:roomId, key}, ...callbacks), () => {
    if (client === current) { show(offline); enableButtons(false); el("retry").hidden = false; }
  });
  current = client;
  try {
    const mode = await client.connect(forceRelay);
    const manifest = await client.manifest();
    if (!Array.isArray(manifest.files) || !manifest.files.length || manifest.files.some((f,i) =>
      f.id !== i || typeof f.name !== "string" || !Number.isSafeInteger(f.size) || f.size < 0)) throw new Error("Liste de fichiers invalide.");
    const title = manifest.name || (manifest.files.length === 1 ? "Un fichier partagé" : manifest.files.length + " fichiers partagés");
    el("title").textContent = document.title = title;
    const description = el("description");
    if (description) {
      description.textContent = manifest.description ?? "";
      description.hidden = !manifest.description;
    }
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
    document.documentElement.dataset.transfer = mode === "direct" ? "webrtc" : "relay";
    show("");
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
