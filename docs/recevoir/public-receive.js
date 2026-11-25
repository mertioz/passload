import {Incoming} from "./incoming.js";
import {chooseDestination, saveRemote} from "./save.js";
import {SocketMeeting} from "./socket-meeting.js";

const status = document.querySelector("#status");
const show = message => { status.textContent = message; };
let client;
let busy = false;

async function connect() {
  if (typeof RTCPeerConnection !== "function" || typeof WebSocket !== "function") {
    show("Téléchargement HTTPS direct.");
    return false;
  }
  show("Connexion WebRTC…");
  const signal = new URL("../rtc", import.meta.url);
  client = new Incoming((...callbacks) => new SocketMeeting(signal, false, ...callbacks), () => {
    show("Téléchargement HTTPS direct.");
  });
  try {
    await client.connect(false, 14_000);
    show("Connexion WebRTC directe.");
    return true;
  } catch {
    client.drop();
    show("Téléchargement HTTPS direct.");
    return false;
  }
}

const connected = connect();
for (const link of document.querySelectorAll("a[data-item]")) {
  link.addEventListener("click", async event => {
    if (event.ctrlKey || event.metaKey || event.shiftKey || event.altKey || !client || client.closed) return;
    event.preventDefault();
    if (busy) return;
    busy = true;
    let file;
    try {
      if (!await connected || client.closed) {
        location.assign(link.href);
        return;
      }
      const zip = link.dataset.item === "zip";
      const name = link.dataset.name;
      const bytes = Number(link.dataset.size);
      const estimate = Number.isSafeInteger(bytes) ? bytes : 0;
      const destination = navigator.userActivation?.isActive ? await chooseDestination(name, estimate) : null;
      file = await client.open(zip ? "zip" : Number(link.dataset.item));
      await saveRemote(file, name, zip ? null : estimate, zip ? "application/zip" : "application/octet-stream",
        received => show("WebRTC · " + new Intl.NumberFormat("fr-FR", {maximumFractionDigits:1}).format(received / 1024) + " Ko reçus."), destination);
      show("Téléchargement WebRTC terminé.");
    } catch (error) {
      if (error.name === "AbortError") show("Téléchargement annulé.");
      else {
        client.drop();
        show("Connexion WebRTC interrompue. Téléchargement HTTPS direct.");
        location.assign(link.href);
      }
    } finally {
      await file?.cancel();
      busy = false;
    }
  });
}
window.addEventListener("pagehide", () => client?.drop());
