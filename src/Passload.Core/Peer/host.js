import {Peer, deadline, decode, control, asControl, chunk} from "./wire.js";
import {Meeting} from "./pipe.js";
import {SocketMeeting} from "./socket-meeting.js";

const active = new Map();
const notify = (op, token, detail = "") => window.chrome.webview.postMessage({op, token, detail});
const MAX_BATCH = 512 * 1024;

class Outgoing {
  constructor(session, guest) {
    this.session = session;
    this.guest = guest;
    this.jobs = Promise.resolve();
    this.queued = 0;
    this.link = new Peer(session.socket, guest.peer, p => this.enqueue(p), () => this.close());
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
    if (cmd.t === "received") {
      if (this.completed?.id === cmd.id) {
        const {file} = this.completed;
        this.completed = null;
        if (!this.guest.preview) await this.reportDownload(file);
      }
      return;
    }
    if (cmd.t === "open") {
      this.stop();
      this.completed = null;
      const path = cmd.item === "manifest" ? "manifest" : cmd.item === "zip" ? "all.zip" :
        Number.isSafeInteger(cmd.item) && cmd.item >= 0 ? "files/" + cmd.item : null;
      if (!path) throw new Error("Fichier invalide.");
      this.file = cmd.item === "zip" ? -1 : cmd.item;
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
    const frameSize = 16 * 1024 - 5;
    while (!done && count < MAX_BATCH) {
      if (!this.rest?.length) {
        const part = await this.reader.read();
        if (part.done) { done = true; break; }
        this.rest = part.value;
        if (!this.rest.length) continue;
      }
      const n = Math.min(this.rest.length, frameSize, MAX_BATCH - count);
      await this.link.send(chunk(cmd.id, this.rest.subarray(0, n)));
      this.rest = this.rest.subarray(n);
      count += n;
      if (this.remaining !== null) { this.remaining -= n; done = this.remaining === 0; }
    }
    await this.link.send(control({t:"batch", id:cmd.id, done}));
    if (done) {
      if (Number.isInteger(this.file)) this.completed = {id:cmd.id, file:this.file};
      this.stop();
    }
  }
  async reportDownload(file) {
    let address = this.guest.address ?? null;
    if (this.session.options.room && this.link.pc) {
      try {
        const stats = await deadline(this.link.pc.getStats(), 700, "Adresse indisponible.");
        const transport = [...stats.values()].find(entry => entry.type === "transport" && entry.selectedCandidatePairId);
        const pair = transport && stats.get(transport.selectedCandidatePairId);
        address = pair && stats.get(pair.remoteCandidateId)?.address;
      } catch {}
    }
    window.chrome.webview.postMessage({op:"download", token:this.session.options.token, file, address:address ?? null});
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
  constructor(id, options) { this.id = id; this.options = options; this.peers = new Map(); this.live = true; this.once = false; this.retry = 0; }
  notify(op, detail) { if (!this.options.silent) notify(op, this.options.token, detail); }
  async connect() {
    if (!this.live || Date.now() >= this.options.until) return this.end();
    let socket;
    try {
      const create = this.options.room
        ? (...handlers) => Meeting.create({...this.options, relay:false}, ...handlers)
        : (...handlers) => new SocketMeeting(new URL("/engine/rtc/" + this.options.token, location.href), true, ...handlers);
      socket = await create(msg => {
        if (msg.op === "joined") {
          if (!this.peers.has(msg.peer)) this.peers.set(msg.peer, new Outgoing(this, msg));
        } else if (msg.op === "left") {
          this.peers.get(msg.peer)?.close(); this.peers.delete(msg.peer);
        }
      }, async (id, type, bytes) => {
        if (type !== 1) return;
        const stream = this.peers.get(id);
        if (!stream) return;
        const msg = JSON.parse(decode(bytes));
        if (msg.use === "direct") {
          stream.link.select("direct");
          await socket.signal(id, {used:"direct"});
          socket.direct?.(id);
        } else await stream.link.negotiate(msg);
      }, () => {
        if (socket !== this.socket || !this.live) return;
        for (const p of this.peers.values()) p.close();
        this.peers.clear();
        if (this.once) {
          this.notify("offline");
          this.schedule();
        }
      });
      if (!this.live) { socket.close(); return; }
      this.socket = socket;
      await deadline(socket.ready.promise, 12_000, "Le moteur WebRTC ne répond pas.");
      if (!this.live) return;
      this.once = true; this.retry = 0;
      this.notify("online");
    } catch {
      socket?.close();
      if (!this.live) return;
      if (this.once) this.schedule();
      else { this.notify("failed", "La connexion WebRTC est indisponible."); this.end(); }
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
    active.delete(this.id);
  }
}

window.chrome.webview.addEventListener("message", e => {
  const msg = e.data;
  if (msg.op === "stop") {
    active.get(msg.token)?.end();
    active.get(msg.token + ":page")?.end();
    return;
  }
  const id = msg.room ? msg.token + ":page" : msg.token;
  if (msg.op !== "start" || active.has(id)) return;
  if (typeof RTCPeerConnection !== "function") {
    if (!msg.silent) notify("failed", msg.token, "Le moteur WebRTC est indisponible.");
    return;
  }
  const session = new Session(id, msg);
  active.set(id, session);
  session.expiry = setTimeout(() => session.end(), Math.max(0, msg.until - Date.now()));
  void session.connect();
});
notify("boot", "");
