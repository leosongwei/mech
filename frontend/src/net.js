// WebSocket protocol client: compressed init, binary snapshots, JSON meta/fx.

const HEADER = 12;
const UNIT_STRIDE = 64;
const PROJ_STRIDE = 32;

export class Net {
  constructor(url) {
    this.url = url;
    this.ws = null;
    this.entities = new Map();
    this.projectiles = [];
    this.meta = null;
    this.init = null;
    this.onFx = null;
    this.onMessage = null;
    this.ready = false;
  }

  connect() {
    return new Promise((resolve, reject) => {
      const ws = new WebSocket(this.url);
      ws.binaryType = 'arraybuffer';
      this.ws = ws;
      ws.onopen = () => { };
      ws.onerror = (e) => reject(e);
      ws.onclose = () => { this.ready = false; };
      ws.onmessage = (ev) => {
        if (typeof ev.data === 'string') {
          const msg = JSON.parse(ev.data);
          if (msg.t === 'init') {
            this.init = msg;
            this.ready = true;
            resolve(msg);
          } else if (msg.t === 'meta') {
            this.meta = msg;
          } else if (msg.t === 'fx') {
            if (this.onFx) this.onFx(msg.events);
          } else if (this.onMessage) {
            this.onMessage(msg);
          }
        } else {
          this._snapshot(ev.data);
        }
      };
    });
  }

  _snapshot(buf) {
    const dv = new DataView(buf);
    const nUnits = dv.getUint16(8, true);
    const nProj = dv.getUint16(10, true);
    const seen = new Set();
    let off = HEADER;
    for (let k = 0; k < nUnits; k++) {
      const id = dv.getUint16(off, true);
      const type = dv.getUint8(off + 2);
      const faction = dv.getUint8(off + 3);
      const owner = dv.getUint8(off + 4);
      const flags = dv.getUint8(off + 5);
      const target = dv.getUint16(off + 6, true);
      let e = this.entities.get(id);
      if (!e) {
        e = { id, x: 0, y: 0, z: 0, yaw: 0, pitch: 0, turret: 0, leg: 0, vx: 0, vz: 0, hp: [0, 0, 0, 0], ammo: [0, 0], mag: [0, 0], weapon: [0, 0] };
        this.entities.set(id, e);
      }
      e.type = type;
      e.faction = faction;
      e.owner = owner;
      e.flags = flags;
      e.target = target === 65535 ? -1 : target;
      e.tx = dv.getFloat32(off + 8, true);
      e.ty = dv.getFloat32(off + 12, true);
      e.tz = dv.getFloat32(off + 16, true);
      e.tyaw = dv.getFloat32(off + 20, true);
      e.tpitch = dv.getFloat32(off + 24, true);
      e.tturret = dv.getFloat32(off + 28, true);
      e.tleg = dv.getFloat32(off + 32, true);
      e.vx = dv.getFloat32(off + 36, true);
      e.vz = dv.getFloat32(off + 40, true);
      for (let i = 0; i < 4; i++) e.hp[i] = dv.getUint16(off + 44 + i * 2, true);
      e.ammo[0] = dv.getUint16(off + 52, true);
      e.ammo[1] = dv.getUint16(off + 54, true);
      e.mag[0] = dv.getUint16(off + 56, true);
      e.mag[1] = dv.getUint16(off + 58, true);
      e.weapon[0] = dv.getUint16(off + 60, true);
      e.weapon[1] = dv.getUint16(off + 62, true);
      if (!e.init) {
        e.x = e.tx; e.y = e.ty; e.z = e.tz;
        e.yaw = e.tyaw; e.pitch = e.tpitch; e.turret = e.tturret; e.leg = e.tleg;
        e.init = true;
      }
      seen.add(id);
      off += UNIT_STRIDE;
    }
    for (const id of this.entities.keys()) {
      if (!seen.has(id)) this.entities.delete(id);
    }

    const proj = [];
    for (let k = 0; k < nProj; k++) {
      proj.push({
        id: dv.getUint16(off, true),
        kind: dv.getUint8(off + 2),
        wid: dv.getUint8(off + 3),
        faction: dv.getUint8(off + 4),
        x: dv.getFloat32(off + 8, true),
        y: dv.getFloat32(off + 12, true),
        z: dv.getFloat32(off + 16, true),
        vx: dv.getFloat32(off + 20, true),
        vy: dv.getFloat32(off + 24, true),
        vz: dv.getFloat32(off + 28, true),
      });
      off += PROJ_STRIDE;
    }
    this.projectiles = proj;
  }

  // smooth the render state towards the last snapshot
  interpolate(alpha) {
    for (const e of this.entities.values()) {
      e.x += (e.tx - e.x) * alpha;
      e.y += (e.ty - e.y) * alpha;
      e.z += (e.tz - e.z) * alpha;
      e.yaw += wrapPi(e.tyaw - e.yaw) * alpha;
      e.pitch += (e.tpitch - e.pitch) * alpha;
      e.turret += wrapPi(e.tturret - e.turret) * alpha;
      e.leg += wrapPi(e.tleg - e.leg) * alpha;
    }
  }

  send(obj) {
    if (this.ws && this.ws.readyState === 1) this.ws.send(JSON.stringify(obj));
  }
}

function wrapPi(a) {
  while (a > Math.PI) a -= Math.PI * 2;
  while (a < -Math.PI) a += Math.PI * 2;
  return a;
}
