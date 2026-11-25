import {Peer, deadline, decode, control, asControl, chunk} from "./wire.js";
import {Meeting} from "./pipe.js";
import {SocketMeeting} from "./socket-meeting.js";

const active = new Map();
const notify = (op, token, detail = "") => window.chrome.webview.postMessage({op, token, detail});
const MAX_BATCH = 512 * 1024;

class Outgoing {
  constructor(session, peerId) {
    this.session = session;
    this.jobs = Promise.resolve();
    this.queued = 0;
    this.link = new Peer(session.socket, peerId, p => this.enqueue(p), () => this.close());
  }
  enqueue(packet) {
    if (packet[0] !== 0) return;
    const command = asControl(packet);
    if (command.t === "cancel" && command.id === this.id) { this.stop(); return; }
    if (this.link.closed || this.queued >= 3) return;
    this.queued++;
    this.jobs = this.jobs.then(() => this.handle(command)).catch(async () => {
      this.stop();
      await this.link.send(control({t:"error", id:command.id, message:"Le fichier est indisponible ou le transfert a été interrompu."})).catch(() => {});
    }).finally(() => { this.queued--; });
  }
  async handle(cmd) {
    if (!Number.isSafeInteger(cmd.id) || cmd.id < 1 || cmd.id > 0xffffffff || !this.session.live) return;
    if (cmd.t === "open") {
      this.stop();
      const path = cmd.item === "manifest" ? "manifest" : cmd.item === "zip" ? "all.zip" :
        Number.isSafeInteger(cmd.item) && cmd.item >= 0 ? "files/" + cmd.item : null;
      if (!path) throw new Error("Fichier invalide.");
      this.id = cmd.id;
      this.abort = new AbortController();
      const response = await fetch("/s/" + this.session.options.token + "/" + path, {signal:this.abort.signal, cache:"no-store", redirect:"error"});
      if (!response.ok || !response.body) throw new Error("Source indisponible.");
      this.reader = response.body.getReader();
      const length = response.headers.get("content-length");
      this.remaining = length === null ? null : Number(length);
      this.rest = null;
      await this.link.send(control({t:"start", id:cmd.id}));
      return;
    }
    if (cmd.t !== "pull" || cmd.id !== this.id || !this.reader) return;
    let count = 0;
    let done = this.remaining === 0;
    const buffered = this.link.transport === "relay" ? [] : null;
    const limit = buffered ? 2 * 1024 * 1024 : MAX_BATCH;
    const frameSize = buffered ? limit : 16 * 1024 - 5;
    while (!done && count < limit) {
      if (!this.rest?.length) {
        const part = await this.reader.read();
        if (part.done) { done = true; break; }
        this.rest = part.value;
        if (!this.rest.length) continue;
      }
      const n = Math.min(this.rest.length, frameSize, limit - count);
      const piece = this.rest.subarray(0,n);
      if (buffered) buffered.push(piece);
      else await this.link.send(chunk(cmd.id, piece));
      this.rest = this.rest.subarray(n);
      count += n;
      if (this.remaining !== null) { this.remaining -= n; done = this.remaining === 0; }
    }
    if (buffered) {
      const batch = new Uint8Array(5+count);
      batch[0] = done ? 3 : 2;
      new DataView(batch.buffer).setUint32(1, cmd.id);
      let offset = 5;
      for (const piece of buffered) { batch.set(piece,offset); offset += piece.length; }
      await this.link.send(batch);
    } else await this.link.send(control({t:"batch", id:cmd.id, done}));
    if (done) this.stop();
  }
  stop() {
    this.abort?.abort();
    this.reader?.cancel().catch(() => {});
    this.abort = this.reader = this.rest = null;
    this.id = 0;
  }
  close() { this.stop(); this.link.close(); }
}

class Session {
  constructor(options) { this.options = options; this.peers = new Map(); this.live = true; this.once = false; this.retry = 0; }
  async connect() {
    if (!this.live || Date.now() >= this.options.until) return this.end();
    let socket;
    try {
      const create = this.options.publicRtc
        ? (...callbacks) => new SocketMeeting(new URL("/engine/rtc/" + this.options.token, location.href), true, ...callbacks)
        : (...callbacks) => Meeting.create(this.options, ...callbacks);
      socket = await create(msg => {
        if (msg.op === "joined") {
          if (!this.peers.has(msg.peer)) this.peers.set(msg.peer, new Outgoing(this, msg.peer));
        } else if (msg.op === "left") {
          this.peers.get(msg.peer)?.close(); this.peers.delete(msg.peer);
        }
      }, async (id, type, bytes) => {
        const stream = this.peers.get(id);
        if (!stream) return;
        if (type === 2) {
          if (stream.link.transport === "relay" && this.options.relay) stream.enqueue(bytes);
          return;
        }
        const msg = JSON.parse(decode(bytes));
        if (msg.use === "direct" || (msg.use === "relay" && this.relay)) {
          stream.link.select(msg.use);
          await socket.signal(id, {used:msg.use});
          if (msg.use === "direct") socket.direct?.(id);
        } else await stream.link.negotiate(msg);
      }, () => {
        if (socket !== this.socket || !this.live) return;
        for (const p of this.peers.values()) p.close();
        this.peers.clear();
        if (this.once) {
          notify("offline", this.options.token);
          this.schedule();
        }
      });
      if (!this.live) { socket.close(); return; }
      this.socket = socket;
      const ready = await deadline(socket.ready.promise, 12_000, "Le service de connexion ne répond pas.");
      if (!this.live) return;
      this.relay = ready.relay;
      this.once = true; this.retry = 0;
      notify("online", this.options.token);
    } catch {
      socket?.close();
      if (!this.live) return;
      if (this.once) this.schedule();
      else { notify("failed", this.options.token, this.options.publicRtc ? "Le moteur WebRTC ne répond pas." : "Le service de connexion est injoignable. Réessayez après avoir vérifié Internet."); this.end(); }
    }
  }
  schedule() {
    if (this.timer || !this.live) return;
    this.timer = setTimeout(() => { this.timer = null; void this.connect(); }, Math.min(15_000, 750 * 2 ** Math.min(++this.retry, 5)));
  }
  end() {
    this.live = false;
    clearTimeout(this.timer);
    clearTimeout(this.expiry);
    for (const p of this.peers.values()) p.close();
    this.peers.clear();
    this.socket?.close();
    active.delete(this.options.token);
  }
}

window.chrome.webview.addEventListener("message", e => {
  const msg = e.data;
  if (msg.op === "stop") { active.get(msg.token)?.end(); return; }
  if (msg.op !== "start" || active.has(msg.token)) return;
  if (msg.publicRtc && typeof RTCPeerConnection !== "function") {
    notify("failed", msg.token, "Le moteur WebRTC est indisponible.");
    return;
  }
  const session = new Session(msg);
  active.set(msg.token, session);
  session.expiry = setTimeout(() => session.end(), Math.max(0, msg.until - Date.now()));
  void session.connect();
});
notify("boot", "");
