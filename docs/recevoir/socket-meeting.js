import {deferred, encode} from "./wire.js";

export class SocketMeeting {
  constructor(url, host, event, frame, offline) {
    url.protocol = url.protocol === "https:" ? "wss:" : "ws:";
    this.host = host;
    this.socket = new WebSocket(url);
    this.ready = deferred();
    this.ready.promise.catch(() => {});
    let pending = Promise.resolve();
    this.socket.onmessage = ({data}) => {
      pending = pending.then(async () => {
        const message = JSON.parse(data);
        if (message.op === "signal") {
          await frame(message.peer, 1, encode(JSON.stringify(message.signal)));
          return;
        }
        await event(message);
        if (message.op === "ready") this.ready.resolve({...message, relay:false});
      }).catch(() => this.close());
    };
    this.socket.onclose = () => {
      this.ready.reject(new Error("Le WebRTC du PC est indisponible."));
      offline();
    };
  }

  async signal(peer, signal) {
    if (this.socket.readyState !== WebSocket.OPEN) throw new Error("Connexion interrompue.");
    this.socket.send(JSON.stringify({peer, signal}));
  }

  close() {
    if (this.socket.readyState < WebSocket.CLOSING) this.socket.close();
  }
}
