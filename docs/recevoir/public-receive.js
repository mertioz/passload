import {text, formatSize} from "./language.js";
import {Incoming} from "./incoming.js";
import {chooseDestination, saveRemote} from "./save.js";
import {SocketMeeting} from "./socket-meeting.js";

const status = document.querySelector("#status");
const show = message => {
  status.textContent = message;
  status.hidden = !message;
};
const httpsAvailable = () => { document.documentElement.dataset.transfer = "https"; };
let client;
let busy = false;

async function connect() {
  if (typeof RTCPeerConnection !== "function" || typeof WebSocket !== "function") {
    httpsAvailable();
    return false;
  }
  const signal = new URL("../rtc", import.meta.url);
  client = new Incoming((...callbacks) => new SocketMeeting(signal, false, ...callbacks), httpsAvailable);
  try {
    await client.connect(false, 14_000);
    document.documentElement.dataset.transfer = "webrtc";
    return true;
  } catch {
    client.drop();
    httpsAvailable();
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
      show(text("Preparing"));
      if (!await connected || client.closed) {
        show("");
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
        received => show(text("DownloadProgress", formatSize(received))), destination);
      await file.confirm?.();
      show(text("Downloaded"));
    } catch (error) {
      if (error.name === "AbortError") show(text("Canceled"));
      else {
        client.drop();
        show(text("Resuming"));
        location.assign(link.href);
      }
    } finally {
      await file?.cancel();
      busy = false;
    }
  });
}
window.addEventListener("pagehide", () => client?.drop());
