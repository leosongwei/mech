// The picture: every row the wire has told us about, and the state those rows are
// eased towards between snapshots.
//
// `absorb` decodes one snapshot frame - the five class blocks, the projectiles,
// the smoke, the minimap's contacts - into the maps and lists the rest of the
// client reads by name.  The rows themselves are laid out in `rows.js`; this file
// is the state they live in, and the one owner of what a frame did *not* mention.

import { wrapPi } from '../core/math.js';
import { CLASSES, decodeRow, measureAccel, newEntity, resetRow, ROWS } from './rows.js';
import { CLOCK, CONTACT_QUANT, CONTACT_STRIDE, HEADER, PROJ_STRIDE, SMOKE_STRIDE, setBuffer } from './wire.js';

export class View {
  constructor() {
    this.entities = new Map();
    this.projectiles = [];
    this.smoke = [];            // canisters in flight and deployed puffs
    this.smokeBy = new Map();   // the same rows, by id, for `absorb`'s own lookups
    // the minimap's own list: every unit on the field, updated at 3.75 Hz
    // (`CONTACT_EVERY`), independent of what the snapshot's tier sphere carries
    this.contacts = [];
    this.seq = 0;               // snapshots received, lets the HUD throttle work
    this.tick = 0;              // the server tick the newest frame belongs to
    this.gen = 0;               // the generation a row must carry to stay listed
  }

  // Decode one snapshot body.  The clock block has already been read (`Client`),
  // so `this.tick` is the tick this frame belongs to, and the rows are measured
  // against it.
  absorb(buf) {
    const dv = setBuffer(buf);
    this.seq += 1;
    const gen = ++this.gen;
    let off = HEADER;
    for (let cls = 0; cls < CLASSES.length; cls++) {
      const n = dv.getUint16(CLOCK + cls * 2, true);
      for (let k = 0; k < n; k++) {
        const id = dv.getUint16(off, true);
        let e = this.entities.get(id);
        if (!e) {
          e = newEntity(id);
          this.entities.set(id, e);
        }
        resetRow(e, cls);
        off = decodeRow(e, dv, off, cls);
        measureAccel(e, this.tick);
        e.gen = gen;
      }
    }

    // The class blocks end where the header's counts say they do.  A row width
    // that has drifted from `Netcode.ROWS` puts a different number here, and
    // everything behind it - the projectiles, the smoke, the minimap - would be
    // read off target and look like a decode bug somewhere else.
    let end = HEADER;
    for (let cls = 0; cls < CLASSES.length; cls++) {
      end += dv.getUint16(CLOCK + cls * 2, true) * ROWS[cls];
    }
    if (off !== end) {
      throw new Error(`snapshot: class rows end at ${off} B, the header counts ${end} B`);
    }

    const proj = [];
    const nProj = dv.getUint16(CLOCK + 10, true);
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
    const nSmoke = dv.getUint16(CLOCK + 12, true);
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
      const prev = this.smokeBy.get(id);
      smoke.push(prev
        ? { id, canister: !!(flags & 1), x: prev.x, y: prev.y, z: prev.z,
            tx: x, ty: y, tz: z, rx, ry, rz, t }
        : { id, canister: !!(flags & 1), x, y, z, tx: x, ty: y, tz: z, rx, ry, rz, t });
      off += SMOKE_STRIDE;
    }
    this.smoke = smoke;
    // the same map refilled, so a steady fifty puffs a second does not cost a map
    // and fifty pair arrays per frame
    this.smokeBy.clear();
    for (let k = 0; k < smoke.length; k++) this.smokeBy.set(smoke[k].id, smoke[k]);

    // the minimap block rides every 8th tick (`CONTACT_EVERY`), so an empty list
    // means "no new picture this frame" - keep the last one
    const nContacts = dv.getUint16(CLOCK + 14, true);
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

    // ...and the frame ends here.  A table whose widths are wrong reads every
    // block behind the rows off target and lands short of the last byte, so the
    // frame's own length is the second half of the check above.
    if (off !== buf.byteLength) {
      throw new Error(`snapshot: the frame is ${buf.byteLength} B, the blocks were read to ${off} B`);
    }

    // whatever the frame did not mention is gone (a tier edge, a death, a wreck
    // cleaned up): this is the only place that knows the field is a set of rows
    // and not a database.  A row carries the generation of the snapshot that last
    // named it, so the sweep is one compare per entity instead of a `Set` filled
    // and thrown away every frame.
    for (const [id, e] of this.entities) {
      if (e.gen !== gen) this.entities.delete(id);
    }
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
}
