// Piping Server pairs an outgoing POST and GET. Files use WebRTC unless both
// peers agree on the encrypted fallback. The public service keeps no share list.
import {encode, decode, to64, from64, deferred, sleep} from "./wire.js";

const relay = "https://ppng.io/passload-v2/";
const curve = {name:"ECDH", namedCurve:"P-256"};
const maxFrame = 2 * 1024 * 1024 + 256;
const random = n => crypto.getRandomValues(new Uint8Array(n));
const digest = async s => to64(new Uint8Array(await crypto.subtle.digest("SHA-256", encode(s))));

async function exchange(path, body, signal, timeout = 32_000, maximum = maxFrame) {
  const timer = new AbortController();
  const abort = () => timer.abort();
  if (signal.aborted) throw new Error("Partage arrêté.");
  signal.addEventListener("abort", abort, {once:true});
  const clock = setTimeout(abort, timeout);
  try {
    const response = await fetch(relay + path, {
      method:body ? "POST" : "GET", body, signal:timer.signal, credentials:"omit",
      headers:body ? {"Content-Type":"application/octet-stream"} : {},
      cache:"no-store", referrerPolicy:"no-referrer", redirect:"error"
    });
    if (!response.ok || !response.body) throw new Error("Le service de mise en relation est indisponible.");
    const reader = response.body.getReader();
    const chunks = [];
    let length = 0;
    for (;;) {
      const part = await reader.read();
      if (part.done) break;
      length += part.value.length;
      if (length > (body ? 8192 : maximum)) { await reader.cancel(); throw new Error("Réponse invalide."); }
      chunks.push(part.value);
    }
    const bytes = new Uint8Array(length);
    let index = 0;
    for (const block of chunks) { bytes.set(block,index); index += block.length; }
    if (body && decode(bytes).includes("[ERROR]")) throw new Error("La transmission a été interrompue.");
    return bytes;
  } finally {
    clearTimeout(clock); signal.removeEventListener("abort",abort);
    timer.abort();
  }
}

async function seal(key, label, bytes) {
  const nonce = random(12);
  const encrypted = new Uint8Array(await crypto.subtle.encrypt({name:"AES-GCM", iv:nonce, additionalData:encode(label)}, key, bytes));
  const packet = new Uint8Array(12 + encrypted.length);
  packet.set(nonce); packet.set(encrypted,12);
  return packet;
}
async function unseal(key, label, packet) {
  if (packet.length < 28) throw new Error("Trame incomplète.");
  return new Uint8Array(await crypto.subtle.decrypt({name:"AES-GCM", iv:packet.subarray(0,12), additionalData:encode(label)}, key, packet.subarray(12)));
}
async function channel(privateKey, otherPublic, salt, id) {
  const remote = await crypto.subtle.importKey("raw",from64(otherPublic),curve,false,[]);
  const shared = await crypto.subtle.deriveBits({name:"ECDH",public:remote},privateKey,256);
  const raw = await crypto.subtle.importKey("raw",shared,"HKDF",false,["deriveBits"]);
  const material = new Uint8Array(await crypto.subtle.deriveBits({name:"HKDF",hash:"SHA-256",salt,info:encode("passload/v2/"+id)},raw,512));
  const key = await crypto.subtle.importKey("raw",material.subarray(0,32),"AES-GCM",false,["encrypt","decrypt"]);
  return {id, key, path:to64(material.subarray(32)), sent:0, read:0, queue:Promise.resolve(), stop:new AbortController(), touched:Date.now()};
}

export class Meeting {
  static async create(options, event, frame, offline) {
    const instance = new Meeting(options, event, frame, offline);
    instance.salt = from64(options.key);
    if (instance.salt.length !== 32 || from64(options.room).length !== 65) throw new Error("Lien incomplet.");
    instance.inviteKey = await crypto.subtle.importKey("raw",instance.salt,"AES-GCM",false,["encrypt","decrypt"]);
    instance.knock = await digest("passload/v2/rendezvous/" + options.room + "/" + options.key);
    setTimeout(() => instance.start().catch(error => {
      if (instance.stopped) return;
      instance.ready.reject(error); instance.close();
    }), 0);
    return instance;
  }
  constructor(options, event, frame, offline) {
    this.options = options; this.event = event; this.frame = frame; this.offline = offline;
    this.host = Boolean(options.owner);
    this.ready = deferred(); this.ready.promise.catch(() => {});
    this.peers = new Map(); this.stop = new AbortController(); this.stopped = false;
  }
  async start() {
    const signal = this.stop.signal;
    if (this.host) {
      this.privateKey = await crypto.subtle.importKey("pkcs8", from64(this.options.owner), curve, false, ["deriveBits"]);
      // Confirm the actual two-way public path before telling the desktop to copy a link.
      const test = "check/" + to64(random(24)), challenge = random(24);
      const [echo] = await Promise.all([exchange(test, null, signal, 9500, 128),exchange(test, challenge, signal, 9500)]);
      if (to64(echo) !== to64(challenge)) throw new Error("La mise en relation n’a pas pu être vérifiée.");
      void this.listen();
      this.ready.resolve({relay:this.options.relay});
      return;
    }
    const pair = await crypto.subtle.generateKey(curve,true,["deriveBits"]);
    const id = to64(random(18));
    const peer = await channel(pair.privateKey,this.options.room,this.salt,id);
    this.peers.set(id,peer);
    const publicGuest = to64(new Uint8Array(await crypto.subtle.exportKey("raw",pair.publicKey)));
    const introduction = await seal(this.inviteKey,"join/"+this.knock,encode(JSON.stringify({id,publicGuest})));
    const welcome = exchange(peer.path + "/hello",null,signal,16_000,2048);
    // Concurrent visitors can race for the next POST slot. Retry the same encrypted invitation.
    const introduce = async () => {
      const until = Date.now() + 14_000;
      for (;;) {
        try { await exchange(this.knock,introduction,signal,Math.max(100,until-Date.now())); return; }
        catch (e) {
          if (signal.aborted || Date.now() >= until) throw e;
          await sleep(180 + random(1)[0]);
        }
      }
    };
    const [answer] = await Promise.all([welcome,introduce()]);
    const info = JSON.parse(decode(await unseal(peer.key,"hello/"+id,answer)));
    if (!Number.isFinite(info.until) || info.until <= Date.now()) throw new Error("Ce lien a expiré.");
    this.relay = info.relay === true;
    const ready = {op:"ready",peer:id,relay:this.relay};
    await this.event(ready);
    void this.readPeer(peer);
    this.ready.resolve(ready);
  }
  async listen() {
    let failures = 0;
    while (!this.stopped && Date.now() < this.options.until) {
      const pending = Date.now();
      try {
        const packet = await exchange(this.knock,null,this.stop.signal,75_000,2048);
        failures = 0;
        const hello = JSON.parse(decode(await unseal(this.inviteKey,"join/"+this.knock,packet)));
        if (!/^[A-Za-z0-9_-]{24}$/.test(hello.id) || !/^[A-Za-z0-9_-]{87}$/.test(hello.publicGuest) || this.peers.has(hello.id)) continue;
        for (const p of this.peers.values()) if (Date.now()-p.touched > 150_000) this.leave(p);
        if (this.peers.size >= 12) continue;
        const p = await channel(this.privateKey,hello.publicGuest,this.salt,hello.id);
        if (this.stopped) return;
        this.peers.set(p.id,p);
        void this.accept(p).catch(() => this.leave(p));
      } catch {
        if (this.stopped) return;
        // An idle GET normally times out without a visitor. Reopen immediately in that case.
        if (Date.now()-pending < 55_000) {
          failures++;
          if (failures >= 4 && this.peers.size === 0) { this.close(); return; }
          await sleep(Math.min(3000, failures*450));
        }
      }
    }
  }
  async accept(peer) {
    await this.event({op:"joined",peer:peer.id});
    const welcome = await seal(peer.key,"hello/"+peer.id,encode(JSON.stringify({until:this.options.until,relay:this.options.relay})));
    await exchange(peer.path + "/hello",welcome,peer.stop.signal,15_000);
    await this.readPeer(peer);
  }
  async readPeer(p) {
    while (!this.stopped && !p.stop.signal.aborted && !p.direct) {
      const label = (this.host ? "g/" : "h/") + p.read;
      const start = Date.now();
      try {
        const bytes = await exchange(p.path+"/"+label,null,p.stop.signal,70_000);
        const plain = await unseal(p.key,label,bytes);
        p.read++; p.touched = Date.now();
        if (plain[0] === 3) { this.leave(p); return; }
        if (plain[0] === 1 || (plain[0] === 2 && (this.host ? this.options.relay : this.relay)))
          await this.frame(p.id,plain[0],plain.subarray(1));
      } catch {
        if (this.stopped || p.stop.signal.aborted) return;
        if (Date.now()-p.touched > 150_000) { this.leave(p); return; }
        if (Date.now()-start < 50_000) await sleep(500);
      }
    }
  }
  send(id, kind, body) {
    const peer = this.peers.get(id);
    if (!peer || this.stopped || (kind === 2 && !(this.host ? this.options.relay : this.relay))) return Promise.reject(new Error("Connexion indisponible."));
    peer.queue = peer.queue.then(async () => {
      const label = (this.host ? "h/" : "g/") + peer.sent++;
      const plain = new Uint8Array(body.length+1);
      plain[0] = kind; plain.set(body,1);
      const sealed = await seal(peer.key,label,plain);
      await exchange(peer.path+"/"+label,sealed,peer.stop.signal,35_000);
      peer.touched = Date.now();
    });
    return peer.queue;
  }
  signal(peer, message) {
    if (this.peers.get(peer)?.direct) return Promise.resolve();
    return this.send(peer,1,encode(JSON.stringify(message)));
  }
  direct(id) { const p = this.peers.get(id); if (p) p.direct = true; }
  disconnected(id) { const p = this.peers.get(id); if (p) this.leave(p); }
  touch(id) { const p = this.peers.get(id); if (p) p.touched = Date.now(); }
  leave(p) {
    if (!this.peers.delete(p.id)) return;
    p.stop.abort();
    if (this.host) this.event({op:"left",peer:p.id});
    else this.close();
  }
  close() {
    if (this.stopped) return;
    this.stopped = true; this.stop.abort();
    for (const p of this.peers.values()) {
      const label = (this.host ? "h/" : "g/") + p.sent;
      p.stop.abort();
      if (p.direct) continue;
      // Best-effort notice on a fresh bounded request. Revocation is enforced on the PC as well.
      void seal(p.key,label,Uint8Array.of(3)).then(bytes=>exchange(p.path+"/"+label,bytes,new AbortController().signal,2500)).catch(()=>{});
    }
    this.peers.clear();
    this.offline();
  }
}
