import {text} from "./language.js";
import {Peer, deadline, deferred, decode, control, asControl} from "./wire.js";

const offline = text("Offline");

export class Incoming {
  constructor(createMeeting, onDrop, show = () => {}) {
    this.createMeeting = createMeeting;
    this.onDrop = onDrop;
    this.show = show;
    this.chosen = deferred();
    this.chosen.promise.catch(() => {});
    this.nextId = 1;
    this.closed = false;
  }
  async connect(forceRelay = false, directTimeout, allowRelay = true) {
    const socket = await this.createMeeting(msg => {
      if (msg.op === "ready") this.peer = new Peer(socket, msg.peer, p => this.packet(p), () => this.drop());
    }, async (id, type, bytes) => {
      if (this.peer?.id !== id) return;
      if (type === 2) { if (this.peer.transport === "relay") this.packet(bytes); return; }
      const msg = JSON.parse(decode(bytes));
      if (msg.used && msg.used === this.peer.transport) {
        if (msg.used === "direct") socket.direct?.(id);
        this.chosen.resolve();
      }
      else await this.peer.negotiate(msg);
    }, () => this.drop());
    this.socket = socket;
    let ready;
    try { ready = await deadline(socket.ready.promise, 18_000, offline); }
    catch { throw new Error(offline); }
    const relay = allowRelay && ready.relay;
    this.canRelay = relay;
    this.show(text("LookingForDirect"));
    let direct = false;
    if (this.peer.pc && !(forceRelay && relay)) {
      try {
        await deadline(this.peer.offer(), 6000, text("DirectUnavailable"));
        direct = await deadline(this.peer.direct.promise, directTimeout ?? (relay ? 6_000 : 15_000), text("DirectUnavailable"));
      } catch { /* Some browsers or networks cannot establish a data channel. */ }
    }
    if (!direct && !relay) throw new Error(text("DirectBlocked"));
    const mode = direct ? "direct" : "relay";
    if (!direct) this.show(text("OpeningRelay"));
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
      const error = new Error(text("TransferUnavailable"));
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
    if (this.closed || this.request) throw new Error(text("ConnectionUnavailable"));
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
    const confirm = async () => {
      if (!req.complete || req.confirmed || item === "manifest" || this.closed) return;
      req.confirmed = true;
      await deadline(this.peer.send(control({t:"received", id:req.id})), 2500, text("ConnectionUnavailable")).catch(() => {});
    };
    return {cancel, confirm, next:async () => {
      if (this.closed || this.request !== req) throw new Error(offline);
      req.parts = []; req.length = 0; req.batch = deferred();
      req.batch.promise.catch(() => {});
      await this.peer.send(control({t:"pull", id:req.id}));
      const result = await deadline(req.batch.promise, 90_000, text("TransferInterrupted"));
      req.parts = []; req.batch = null;
      if (result.done) {
        req.complete = true;
        this.request = null;
      }
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
        if (length > 4 * 1024 * 1024) throw new Error(text("ManifestTooLarge"));
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
    this.chosen.reject(err);
    this.request?.started.reject(err); this.request?.batch?.reject(err);
    this.peer?.close(); this.socket?.close();
    this.onDrop();
  }
}
