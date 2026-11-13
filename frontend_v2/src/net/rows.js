// The five per-class row layouts, as data.
//
// A unit row is as wide as its *kind* needs (`Netcode.ROWS`): the classes are mech
// 74 B, apc 46 B, base 30 B, wreck/bunker 25 B, infantry/civilian/corpse 20 B, and
// the header carries one count per class in that order.  The table below *is* the
// layout: no byte offset is written down anywhere, they fall out of the field order
// and the width of its type, so a new class is one entry and a width that drifts
// from the server cannot hide - the decode cursor misses the block behind it and
// `view.absorb` throws with both numbers.
//
// Every field in `EVERY_ROW` is carried by every class; every other field is
// optional and has a default (`CLEAR`), which is what `resetRow` writes back
// before a row is decoded: entity ids are recycled, and a rifleman is not to
// inherit the magazines of the mech whose id he took.

import { TICK_HZ } from '../core/config.js';

// the measured acceleration's two constants: how long a pull is remembered (a
// hull that stopped pulling reads as stopping in a quarter second, which is the
// scale of its own ramp) and the rate no machine can make, i.e. a hull that did
// not accelerate but moved (`measureAccel`)
const ACC_TAU = 0.25;           // s
const ACC_JUMP = 25;            // m/s^2, against a mech's 9.3 and an APC's 8

const SIZE = { u8: 1, u16: 2, f32: 4 };

export const CLASSES = [
  {
    name: 'mech',               // everything: hull, arms, magazines, the sight head
    bytes: 74,
    fields: [
      ['id', 'u16'], ['etype', 'u8'], ['faction', 'u8'],
      ['owner', 'u8'], ['flags', 'u8'], ['target', 'u16'],
      ['pos', 'f32', 3], ['yaw', 'f32'],
      ['pitch', 'f32'], ['turret', 'f32'], ['leg', 'f32'],
      ['vel', 'f32', 3],
      ['hp', 'u16'], ['arm_hp', 'u16', 2],      // left arm, then right
      ['ammo', 'u16', 2], ['mag', 'u16', 2], ['weapon', 'u16', 2],
      ['syaw', 'f32'], ['spitch', 'f32'],
    ],
  },
  {
    name: 'apc',                // turret and velocity, no arms and no sight head
    bytes: 46,
    fields: [
      ['id', 'u16'], ['etype', 'u8'], ['faction', 'u8'],
      ['owner', 'u8'], ['flags', 'u8'], ['target', 'u16'],
      ['pos', 'f32', 3], ['yaw', 'f32'],
      ['pitch', 'f32'], ['turret', 'f32'],
      ['vel', 'f32', 3], ['hp', 'u16'],
    ],
  },
  {
    name: 'base',               // a body, its gun, one hp pool
    bytes: 30,
    fields: [
      ['id', 'u16'], ['etype', 'u8'], ['faction', 'u8'],
      ['pos', 'f32', 3], ['yaw', 'f32'],
      ['pitch', 'f32'], ['turret', 'f32'], ['hp', 'u16'],
    ],
  },
  {
    name: 'wreck',              // a body, a heading, what it used to be (bits 4-5)
    bytes: 25,
    fields: [
      ['id', 'u16'], ['etype', 'u8'], ['faction', 'u8'], ['flags', 'u8'],
      ['pos', 'f32', 3], ['yaw', 'f32'], ['leg', 'f32'],
    ],
  },
  {
    name: 'infantry',           // infantry / civilian / corpse: a point and a heading
    bytes: 20,
    fields: [
      ['id', 'u16'], ['etype', 'u8'], ['faction', 'u8'],
      ['pos', 'f32', 3], ['yaw', 'f32'],
    ],
  },
];

export const ROWS = CLASSES.map((c) => c.bytes);

// The fields every class carries, so there is no default to fall back to.
const EVERY_ROW = new Set(['id', 'etype', 'faction', 'pos', 'yaw']);

// How one named field lands on the entity.  `k` is the element index of a
// repeated field: `pos` and `vel` are the same three floats whatever the class
// calls them, and `arm_hp` / `ammo` / `mag` / `weapon` are one per arm.
const PUT = {
  id: (e, dv, o) => { e.id = dv.getUint16(o, true); },
  etype: (e, dv, o) => { e.type = dv.getUint8(o); },
  faction: (e, dv, o) => { e.faction = dv.getUint8(o); },
  owner: (e, dv, o) => { e.owner = dv.getUint8(o); },
  flags: (e, dv, o) => { e.flags = dv.getUint8(o); },
  // nothing in the sights is 65535 on the wire and -1 on this side
  target: (e, dv, o) => { const t = dv.getUint16(o, true); e.target = t === 65535 ? -1 : t; },
  // the snapshot's position and heading are the *targets* the render state eases
  // towards (`view.interpolate`); the pose itself is kept next to them
  pos: (e, dv, o, k) => {
    const v = dv.getFloat32(o, true);
    if (k === 0) e.tx = v; else if (k === 1) e.ty = v; else e.tz = v;
  },
  yaw: (e, dv, o) => { e.tyaw = dv.getFloat32(o, true); },
  pitch: (e, dv, o) => { e.pitch = dv.getFloat32(o, true); },
  turret: (e, dv, o) => { e.turret = dv.getFloat32(o, true); },
  leg: (e, dv, o) => { e.leg = dv.getFloat32(o, true); },
  vel: (e, dv, o, k) => {
    const v = dv.getFloat32(o, true);
    if (k === 0) e.vx = v; else if (k === 1) e.vy = v; else e.vz = v;
  },
  // the hull pool and then the arms' (`arm_hp` writes into the same array); the
  // wire's column 1 is the torso's old slot and is always zero, so the writer
  // clears the slots behind it and no class has to say so
  hp: (e, dv, o) => {
    e.hp[0] = dv.getUint16(o, true);
    e.hp[1] = 0; e.hp[2] = 0; e.hp[3] = 0;
  },
  arm_hp: (e, dv, o, k) => { e.hp[2 + k] = dv.getUint16(o, true); },
  ammo: (e, dv, o, k) => { e.ammo[k] = dv.getUint16(o, true); },
  mag: (e, dv, o, k) => { e.mag[k] = dv.getUint16(o, true); },
  weapon: (e, dv, o, k) => { e.weapon[k] = dv.getUint16(o, true); },
  // where the crew is looking: `yaw` / `pitch` are the laid gun
  syaw: (e, dv, o) => { e.syaw = dv.getFloat32(o, true); },
  spitch: (e, dv, o) => { e.spitch = dv.getFloat32(o, true); },
};

// What a field reads as on a row of a class that does not carry it.
const CLEAR = {
  owner: (e) => { e.owner = 255; },
  flags: (e) => { e.flags = 0; },
  target: (e) => { e.target = -1; },
  pitch: (e) => { e.pitch = 0; },
  turret: (e) => { e.turret = 0; },
  leg: (e) => { e.leg = 0; },
  vel: (e) => { e.vx = 0; e.vy = 0; e.vz = 0; },
  hp: (e) => { e.hp[0] = 0; e.hp[1] = 0; e.hp[2] = 0; e.hp[3] = 0; },
  arm_hp: (e) => { e.hp[2] = 0; e.hp[3] = 0; },
  ammo: (e) => { e.ammo[0] = 0; e.ammo[1] = 0; },
  mag: (e) => { e.mag[0] = 0; e.mag[1] = 0; },
  weapon: (e) => { e.weapon[0] = 0; e.weapon[1] = 0; },
  syaw: (e) => { e.syaw = 0; },
  spitch: (e) => { e.spitch = 0; },
};

// the union of every name the table mentions, in the order it first appears
const NAMES = [];
for (const c of CLASSES) {
  for (const f of c.fields) if (!NAMES.includes(f[0])) NAMES.push(f[0]);
}

// Two checks at load, so a table that disagrees with the wire fails on the boot
// page instead of as a rifleman with a mech's magazines.
for (const c of CLASSES) {
  let bytes = 0;
  for (const f of c.fields) bytes += SIZE[f[1]] * (f[2] || 1);
  if (bytes !== c.bytes) {
    throw new Error(`row layout ${c.name}: fields add up to ${bytes} B, the class is ${c.bytes} B`);
  }
  const own = new Set(c.fields.map((f) => f[0]));
  for (const n of NAMES) {
    if (own.has(n) || CLEAR[n] || EVERY_ROW.has(n)) continue;
    throw new Error(`row layout ${c.name}: no default for ${n}`);
  }
  c.clear = NAMES.filter((n) => !own.has(n) && CLEAR[n]).map((n) => CLEAR[n]);
}

// A row's own object: the render state the interpolation eases, the fields every
// class may write, and the two-slot arrays a mech or an APC fills.
export function newEntity(id) {
  return { id, x: 0, y: 0, z: 0, yaw: 0, pitch: 0, turret: 0, leg: 0, syaw: 0, spitch: 0,
    vx: 0, vy: 0, vz: 0, hp: [0, 0, 0, 0], ammo: [0, 0], mag: [0, 0], weapon: [0, 0] };
}

// Everything this class's layout does not write, back to its default.
export function resetRow(e, cls) {
  const clear = CLASSES[cls].clear;
  for (let i = 0; i < clear.length; i++) clear[i](e);
}

// One unit row, in its class's own layout; the offset of the row after it.
export function decodeRow(e, dv, off, cls) {
  const fields = CLASSES[cls].fields;
  for (let i = 0; i < fields.length; i++) {
    const f = fields[i];
    const size = SIZE[f[1]];
    const n = f[2] || 1;
    const put = PUT[f[0]];
    for (let k = 0; k < n; k++) put(e, dv, off + k * size, k);
    off += size * n;
  }
  e.tpitch = e.pitch;
  e.tturret = e.turret;
  e.tleg = e.leg;
  e.tsyaw = e.syaw;
  e.tspitch = e.spitch;
  if (!e.init) {
    e.x = e.tx; e.y = e.ty; e.z = e.tz;
    e.yaw = e.tyaw; e.init = true;
  }
  // the authoritative position this snapshot carries, kept next to the smoothed
  // one: automation and the debug teleport loop read `px/pz` when they want where
  // the server says the hull is, not where it is drawn
  e.px = e.tx; e.py = e.ty; e.pz = e.tz;
  // a smoothed copy of the hull's own velocity, for the lead rings and the speed
  // readout: the wire is a 15 Hz sample and the marker does not need to twitch
  // with it
  if (e.mvx === undefined) {
    e.mvx = e.vx; e.mvy = e.vy; e.mvz = e.vz;
  } else {
    e.mvx += (e.vx - e.mvx) * 0.5;
    e.mvy += (e.vy - e.mvy) * 0.5;
    e.mvz += (e.vz - e.mvz) * 0.5;
  }
  return off;
}

// How hard a hull is pulling is not on the wire and cannot be: nobody broadcasts
// how much throttle is down.  It is measured off the one thing that is sent - two
// velocity samples, differenced over the ticks between them - and that
// measurement is smoothed with a *time constant* rather than a fixed per-frame
// gain, so a lost or a held frame lengthens the baseline instead of making the
// estimator quicker or slower.  Two samples are the shortest baseline on purpose:
// a hull's pull changes on the scale of its own ramp (a mech reaches its limit in
// a second), so a trend fitted over half a second would still be reporting the
// turn it was in.  `game._leadMarkers` puts the result in the pre-aim ring as
// `ax` / `az`.
export function measureAccel(e, tick) {
  if (e.avt === undefined) {
    e.pvx = e.vx; e.pvz = e.vz; e.avt = tick; e.ax = 0; e.az = 0;
  } else if (tick > e.avt) {
    const dt = (tick - e.avt) / TICK_HZ;
    const sx = (e.vx - e.pvx) / dt, sz = (e.vz - e.pvz) / dt;
    e.pvx = e.vx; e.pvz = e.vz; e.avt = tick;
    // a jump no machine can make is not a pull: a respawn, a teleport, an id
    // handed back to a new hull.  Drop the history instead of nearly capping it,
    // so the ring does not carry a phantom acceleration for a few frames
    if (sx * sx + sz * sz > ACC_JUMP * ACC_JUMP) {
      e.ax = 0; e.az = 0;
    } else {
      const k = 1 - Math.exp(-dt / ACC_TAU);
      e.ax += (sx - e.ax) * k;
      e.az += (sz - e.az) * k;
    }
  }
}
