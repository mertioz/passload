const encoder = new TextEncoder();
const decoder = new TextDecoder();
export const encode = text => encoder.encode(text);
export const decode = bytes => decoder.decode(bytes);
export const to64 = bytes => btoa(String.fromCharCode(...bytes)).replace(/=/g,"").replace(/\+/g,"-").replace(/\//g,"_");
export const from64 = value => Uint8Array.from(atob(value.replace(/-/g,"+").replace(/_/g,"/")), c => c.charCodeAt(0));
export const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));
export function deferred() {
  let resolve, reject;
  const promise = new Promise((ok, no) => { resolve = ok; reject = no; });
  return {promise, resolve, reject};
}
export function deadline(promise, ms, message) {
  let timer;
  const timeout = new Promise((_, reject) => { timer = setTimeout(() => reject(new Error(message)), ms); });
  return Promise.race([promise, timeout]).finally(() => clearTimeout(timer));
}
export function control(message) {
  const json = encode(JSON.stringify(message));
  const packet = new Uint8Array(json.length + 1);
  packet.set(json, 1);
  return packet;
}
export function chunk(id, body) {
  const data = new Uint8Array(body.length + 5);
  data[0] = 1;
  new DataView(data.buffer).setUint32(1, id);
  data.set(body, 5);
  return data;
}
export const asControl = packet => JSON.parse(decode(packet.subarray(1)));

export class Peer {
  constructor(room, id, packet, disconnected) {
    this.room = room;
    this.id = id;
    this.packet = packet;
    this.disconnected = disconnected;
    this.direct = deferred();
    this.pendingIce = [];
    this.closed = false;
    this.transport = null;
    try {
      this.pc = new RTCPeerConnection({iceServers:[{urls:"stun:stun.l.google.com:19302"}]});
      this.pc.onicecandidate = e => { if (this.announced && e.candidate) room.signal(id, {ice:e.candidate.toJSON()}).catch(() => {}); };
      this.pc.ondatachannel = e => this.bind(e.channel);
    } catch { this.pc = null; }
  }
  bind(dc) {
    this.dc = dc;
    dc.binaryType = "arraybuffer";
    dc.onopen = () => this.direct.resolve(true);
    dc.onmessage = e => { if (this.transport === "direct") { this.room.touch(this.id); this.packet(new Uint8Array(e.data)); } };
    dc.onclose = () => {
      if (!this.closed && this.transport === "direct") { this.room.disconnected(this.id); this.disconnected(); }
    };
  }
  async offer() {
    if (!this.pc) return;
    this.bind(this.pc.createDataChannel("files", {ordered:true}));
    await this.pc.setLocalDescription(await this.pc.createOffer());
    await this.announce();
  }
  async announce() {
    // Bundle early candidates with the description; later candidates can still trickle in.
    const start = Date.now();
    while (!this.closed && this.pc.iceGatheringState !== "complete" && Date.now() - start < 900) await sleep(30);
    this.announced = true;
    await this.room.signal(this.id, {sdp:this.pc.localDescription.toJSON()});
  }
  async negotiate(message) {
    if (!this.pc) return;
    if (message.ice) {
      if (this.pc.remoteDescription) await this.pc.addIceCandidate(message.ice);
      else if (this.pendingIce.length < 200) this.pendingIce.push(message.ice);
    }
    if (message.sdp) {
      await this.pc.setRemoteDescription(message.sdp);
      for (const ice of this.pendingIce.splice(0)) await this.pc.addIceCandidate(ice);
      if (message.sdp.type === "offer") {
        await this.pc.setLocalDescription(await this.pc.createAnswer());
        await this.announce();
      }
    }
  }
  select(mode) {
    this.transport = mode;
    if (mode === "relay") this.pc?.close();
    if (!this.room.host && !this.heartbeat) {
      this.heartbeat = setInterval(() => {
        if (this.transport === "direct" && this.dc?.readyState === "open") {
          this.room.touch(this.id); this.dc.send(control({t:"ping",id:0}));
        } else this.room.signal(this.id,{alive:true}).catch(()=>{});
      }, 40_000);
    }
  }
  async send(data) {
    if (this.closed) throw new Error("Transfert interrompu.");
    if (this.transport === "relay") { await this.room.send(this.id, 2, data); return; }
    if (this.dc?.readyState !== "open") throw new Error("Connexion directe interrompue.");
    while (this.dc.bufferedAmount > 512 * 1024) {
      await sleep(10);
      if (this.closed || this.dc.readyState !== "open") throw new Error("Connexion interrompue.");
    }
    this.dc.send(data);
  }
  close() { this.closed = true; clearInterval(this.heartbeat); this.dc?.close(); this.pc?.close(); }
}
