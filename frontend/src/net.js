// WebSocket protocol client: compressed init, binary snapshots, JSON meta/fx.

const MAGIC_PING = 0x4d454350;  // "MECP": magic + tick only, no rows at all
const HEADER = 24;              // magic, tick, eight u16 counts
// A unit row is as wide as its *kind* needs (`Netcode.ROWS`): the classes are
// mech 74 B, apc 46 B, base 30 B, wreck/bunker 25 B, infantry/civilian/corpse
// 20 B, and the header carries one count per class in that order.
const ROWS = [74, 46, 30, 25, 20];
const PROJ_STRIDE = 32;
const SMOKE_STRIDE = 32;
const CONTACT_STRIDE = 6;       // minimap point: x, z, faction, etype
const CONTACT_QUANT = 4;        // metres per unit of the u16
const TICK_HZ = 30;

export class Net {
  constructor(url) {
    this.url = url;
    this.ws = null;
    this.entities = new Map();
    this.projectiles = [];
    this.smoke = [];            // canisters in flight and deployed puffs
    // the minimap's own list: every unit on the field, updated at 3.75 Hz
    // (`CONTACT_EVERY`), independent of what the snapshot's tier sphere carries
    this.contacts = [];
    this.meta = null;
    this.init = null;
    this.onFx = null;
    this.onMessage = null;
    this.seq = 0;               // snapshots received, lets the HUD throttle work
    this.tick = 0;              // the last applied snapshot's tick: the server
                                // wants it back (`st`) to know how far behind
                                // this pilot is, and skip frames instead of
                                // queueing them (`Server.Client.want`)
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
    this.tick = dv.getUint32(4, true);
    // A hold frame (8 B) is what a jammed connection gets instead of a
    // snapshot: the clock moves, the field does not.  Everything the client
    // had - entities, projectiles, smoke, contacts - stays exactly as it was,
    // and the ack it sends back is how the server learns it can resume.
    if (dv.getUint32(0, true) === MAGIC_PING) {
      this.held = true;
      return;
    }
    this.held = false;
    this.seq += 1;
    let off = HEADER;
    const seen = new Set();
    for (let cls = 0; cls < ROWS.length; cls++) {
      const n = dv.getUint16(8 + cls * 2, true);
      for (let k = 0; k < n; k++) {
        this._row(dv, off, cls, seen);
        off += ROWS[cls];
      }
    }

    const proj = [];
    const nProj = dv.getUint16(18, true);
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

    // smoke: a short-lived cloud and the canisters on their way to become one.
    // Positions are interpolated like the units, so a canister does not stutter
    // at the 15 Hz snapshot rate.
    const nSmoke = dv.getUint16(20, true);
    const smoke = [];
    for (let k = 0; k < nSmoke; k++) {
      const id = dv.getUint16(off, true);
      const flags = dv.getUint8(off + 2);
      const x = dv.getFloat32(off + 4, true);
      const y = dv.getFloat32(off + 8, true);
      const z = dv.getFloat32(off + 12, true);
      const rx = dv.getFloat32(off + 16, true);
      const ry = dv.getFloat32(off + 20, true);
      const rz = dv.getFloat32(off + 24, true);
      const t = dv.getFloat32(off + 28, true);
      const prev = this.smokeBy ? this.smokeBy.get(id) : null;
      smoke.push(prev
        ? { id, canister: !!(flags & 1), x: prev.x, y: prev.y, z: prev.z,
            tx: x, ty: y, tz: z, rx, ry, rz, t }
        : { id, canister: !!(flags & 1), x, y, z, tx: x, ty: y, tz: z, rx, ry, rz, t });
      off += SMOKE_STRIDE;
    }
    this.smoke = smoke;
    this.smokeBy = new Map(smoke.map((r) => [r.id, r]));

    // the minimap block rides every 8th tick (`CONTACT_EVERY`), so an empty
    // list means "no new picture this frame" - keep the last one
    const nContacts = dv.getUint16(22, true);
    if (nContacts > 0) {
      const contacts = new Array(nContacts);
      for (let k = 0; k < nContacts; k++) {
        contacts[k] = {
          x: dv.getUint16(off, true) * CONTACT_QUANT,
          z: dv.getUint16(off + 2, true) * CONTACT_QUANT,
          faction: dv.getUint8(off + 4),
          etype: dv.getUint8(off + 5),
        };
        off += CONTACT_STRIDE;
      }
      this.contacts = contacts;
    }

    // whatever the frame did not mention is gone (a tier edge, a death, a
    // wreck cleaned up): `net.js` is the only place that knows the field is a
    // set of rows and not a database
    for (const id of this.entities.keys()) {
      if (!seen.has(id)) this.entities.delete(id);
    }
  }

  // One unit row, in its class's own layout.  Every field the class does not
  // carry is reset first: entity ids are recycled, and a rifleman is not to
  // inherit the magazines of the mech whose id he took.
  _row(dv, off, cls, seen) {
    const id = dv.getUint16(off, true);
    let e = this.entities.get(id);
    if (!e) {
      e = { id, x: 0, y: 0, z: 0, yaw: 0, pitch: 0, turret: 0, leg: 0, syaw: 0, spitch: 0,
        vx: 0, vy: 0, vz: 0, hp: [0, 0, 0, 0], ammo: [0, 0], mag: [0, 0], weapon: [0, 0] };
      this.entities.set(id, e);
    }
    e.owner = 255;
    e.flags = 0;
    e.target = -1;
    e.pitch = 0; e.turret = 0; e.leg = 0; e.syaw = 0; e.spitch = 0;
    e.vx = 0; e.vy = 0; e.vz = 0;
    e.hp[0] = 0; e.hp[1] = 0; e.hp[2] = 0; e.hp[3] = 0;
    e.ammo[0] = 0; e.ammo[1] = 0; e.mag[0] = 0; e.mag[1] = 0;
    e.weapon[0] = 0; e.weapon[1] = 0;
    e.type = dv.getUint8(off + 2);
    e.faction = dv.getUint8(off + 3);
    const f32 = (o) => dv.getFloat32(o, true);
    const u16 = (o) => dv.getUint16(o, true);
    let p;                                  // where pos / yaw start
    if (cls === 0) {                        // mech: everything
      e.owner = dv.getUint8(off + 4);
      e.flags = dv.getUint8(off + 5);
      e.target = u16(off + 6) === 65535 ? -1 : u16(off + 6);
      p = off + 8;
      e.pitch = f32(off + 24);
      e.turret = f32(off + 28);
      e.leg = f32(off + 32);
      e.vx = f32(off + 36); e.vy = f32(off + 40); e.vz = f32(off + 44);
      e.hp[0] = u16(off + 48);
      e.hp[2] = u16(off + 50);              // left arm
      e.hp[3] = u16(off + 52);              // right arm
      e.ammo[0] = u16(off + 54); e.ammo[1] = u16(off + 56);
      e.mag[0] = u16(off + 58); e.mag[1] = u16(off + 60);
      e.weapon[0] = u16(off + 62); e.weapon[1] = u16(off + 64);
      e.syaw = f32(off + 66);
      e.spitch = f32(off + 70);
    } else if (cls === 1) {                 // apc: turret + velocity, no arms
      e.owner = dv.getUint8(off + 4);
      e.flags = dv.getUint8(off + 5);
      e.target = u16(off + 6) === 65535 ? -1 : u16(off + 6);
      p = off + 8;
      e.pitch = f32(off + 24);
      e.turret = f32(off + 28);
      e.vx = f32(off + 32); e.vy = f32(off + 36); e.vz = f32(off + 40);
      e.hp[0] = u16(off + 44);
    } else if (cls === 2) {                 // command base
      p = off + 4;
      e.pitch = f32(off + 20);
      e.turret = f32(off + 24);
      e.hp[0] = u16(off + 28);
    } else if (cls === 3) {                 // wreck / bunker
      e.flags = dv.getUint8(off + 4);        // bits 4-5: what the hulk was
      p = off + 5;
      e.leg = f32(off + 21);
    } else {                                // infantry / civilian / corpse
      p = off + 4;
    }
    e.tx = f32(p); e.ty = f32(p + 4); e.tz = f32(p + 8);
    e.tyaw = f32(p + 12);
    e.tpitch = e.pitch;
    e.tturret = e.turret;
    e.tleg = e.leg;
    e.tsyaw = e.syaw;
    e.tspitch = e.spitch;
    if (!e.init) {
      e.x = e.tx; e.y = e.ty; e.z = e.tz;
      e.yaw = e.tyaw; e.init = true;
    }
    // the authoritative position this snapshot carries, kept next to the
    // smoothed one: automation and the debug teleport loop read `px/pz` when
    // they want where the server says the hull is, not where it is drawn
    e.px = e.tx; e.py = e.ty; e.pz = e.tz;
    // a smoothed copy of the hull's own velocity, for the lead rings and the
    // speed readout: the wire is a 15 Hz sample and the marker does not need
    // to twitch with it
    if (e.mvx === undefined) {
      e.mvx = e.vx; e.mvy = e.vy; e.mvz = e.vz;
    } else {
      e.mvx += (e.vx - e.mvx) * 0.5;
      e.mvy += (e.vy - e.mvy) * 0.5;
      e.mvz += (e.vz - e.mvz) * 0.5;
    }
    seen.add(id);
    return e;
  }

  // smooth the render state towards the last snapshot
  interpolate(alpha) {
    for (const p of this.smoke) {
      p.x += (p.tx - p.x) * alpha;
      p.y += (p.ty - p.y) * alpha;
      p.z += (p.tz - p.z) * alpha;
    }
    for (const e of this.entities.values()) {
      e.x += (e.tx - e.x) * alpha;
      e.y += (e.ty - e.y) * alpha;
      e.z += (e.tz - e.z) * alpha;
      e.yaw += wrapPi(e.tyaw - e.yaw) * alpha;
      e.pitch += (e.tpitch - e.pitch) * alpha;
      e.turret += wrapPi(e.tturret - e.turret) * alpha;
      e.leg += wrapPi(e.tleg - e.leg) * alpha;
      e.syaw += wrapPi(e.tsyaw - e.syaw) * alpha;
      e.spitch += (e.tspitch - e.spitch) * alpha;
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
