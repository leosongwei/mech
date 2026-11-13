// The arms: a 30 mm's plain box and tube, a 12.7 mm rotary gun, a 105 mm
// smoothbore, and every launcher that carries its rounds in a rack.
//
// The two gun barrels are described here rather than in the mech model because
// they are data about the weapon, not about the hull: the 30 mm's tube, the
// gatling's six barrels on their ring and the 105 mm's magazine-arm are three
// boxes in one place with one shoulder height, and a fourth kind of arm should
// be a branch in this file alone.
//
// `now` is the frame's clock in ms, threaded in by the caller: a model is a
// pure function of its inputs, and the gatling's spin is the one piece of the
// draw that has state at all.

import { MESH } from '../render/mesh-table.js';
import { FORWARD, at } from './helpers.js';
import { METAL, METAL_D, METAL_L, DEAD, facColor } from './palette.js';

// The 12.7 mm gun (`defs.WEAPONS[...]["gatling"]`) is a rotary one: six barrels
// on a ring round the bore, each half the 30 mm's bore and 60 % of its length,
// and the barrel that has turned up to the top of the ring is the one firing.
// The gun runs at 1800 rpm, which at 30 Hz is half a turn per frame - straight
// through the ring's own 60 degree symmetry, so it would strobe instead of
// turning.  The model therefore turns slower than the gun feeds; it only has to
// read as turning.
const GAT_RING = 0.17;          // m, radius of the circle the six tubes sit on
const GAT_LEN = 1.92;           // m, 60 % of the 30 mm's 3.2 m barrel
const GAT_BORE = 0.085;         // m, half the 30 mm's 0.17 m
const GAT_MUZZLE = 0.90 + GAT_LEN;
const GAT_COLLAR = 0.40;        // m the bearing ring sits behind the muzzles
// the bearing ring, as a diameter like every primitive scale here: the tubes'
// own circle is 2 * (GAT_RING + GAT_BORE / 2) = 0.425 across, and the ring
// carries a lip past it so the barrels are visibly enclosed
const GAT_HOOP = 0.56;
const GAT_SPIN = 12;            // rad/s of the drawn cluster
const GAT_COAST = 0.25;         // s it keeps turning after the last flash
export const SPIN = new Map();  // "eid:arm" -> { a, t, fire }: the draw is stateless

// The 105 mm smoothbore (`defs.WEAPONS[...]["heavy"]`) is literally the 30 mm's
// arm - the same box in the same place, the same tube out of its front - with
// the box doubled in every direction: 2.40 m long along the barrel, 1.40 m
// high and 0.80 m wide where the 30 mm's is 1.65 x 0.70 x 0.62.  That box is
// the magazine, autoloader and breech in one, and it is big enough to be the
// whole arm, so the shoulder ball is left out there.  Note the primitive's
// scale order: X is forward, so the box is (length, height, width).  The bore
// runs `GUN105_LEN` = 5 m out of the box's front face and ends at
// `GUN105_FACE + GUN105_LEN` = 6.45 m, the same number the weapon carries as
// its `reach`, so a round leaves the mouth of the barrel it was drawn on.
const GUN105_BOX = 0.25;        // m, centre of the box along the arm (as the 30 mm)
const GUN105_L = 2.40;          // m long, along the barrel
const GUN105_H = 1.40;          // m tall
const GUN105_W = 0.80;          // m across
const GUN105_FACE = GUN105_BOX + GUN105_L * 0.5;   // 1.45 m: the box's front face
const GUN105_BORE = 0.26;       // m of drawn barrel radius (a 105 mm bore)
const GUN105_LEN = 5.0;         // m of barrel exposed past the box
const GUN105_MUZZLE = GUN105_FACE + GUN105_LEN;    // 6.45 m: where it ends
// A launcher of tubes is drawn from its `rack` - the same description the
// server fires it from (`defs.rack`): lattice, bore, length, body.  Nothing
// about a pod or a rack lives in this file any more.
const TUBE_COL = [0.08, 0.08, 0.09];     // a loaded cell
const SPENT_TUBE = [0.03, 0.03, 0.04];   // a cell that has fired

// A launcher that carries its rounds in tubes draws one hexagonal cell per
// round on the grid the server fires them out of (`defs.rack_tube`): same
// columns, same rows, same pitch, so the mouth a round leaves is the mouth that
// was drawn.  The body is the box `defs.rack` derived for it - the lattice plus
// a frame, shorter than the tubes so every mouth stands proud of the face
// instead of being punched through a slab.
export function rack(put, p, spec, colour, flash) {
  // `bore` is the tube's outside diameter, and every primitive here scales as a
  // width, not a radius: drawing it at `bore / 2` put a tube a quarter of the
  // area on the face and left a hole's worth of empty box around it.
  const r = spec.bore;
  // a box scales on X = along the bore, Y = up, Z = across (`box_h` is the
  // height and `box_w` the width - a five-across / eight-high nest is taller
  // than it is wide, and the other way round it would cut its own tubes off)
  put(MESH.BOX, p, spec.centre, spec.box_len, spec.box_h, spec.box_w, colour);
  for (let row = 0; row < spec.rows; row++) {
    for (let col = 0; col < spec.cols; col++) {
      // the cells still loaded are drawn as bores; spent ones go dark, which is
      // the only way the rack itself can show it is running out
      const idx = row * spec.cols + col;
      const spent = flash && idx < flash.spent;
      put(MESH.HEX, FORWARD + p, spec.centre, r, spec.length, r,
        spent ? SPENT_TUBE : TUBE_COL,
        (col - (spec.cols - 1) / 2) * spec.pitch,
        (row - (spec.rows - 1) / 2) * spec.pitch);
    }
  }
}

// Which tube of a racked launcher the round that has just gone left: the
// bottom row first, left to right, the order `defs.rack_tube` gives the
// server.  `null` for a weapon that fires off the bore.
export function tubeOf(wd, e, arm) {
  const spec = wd && wd.rack;
  if (!spec) return null;
  const mag = (e.mag && e.mag[arm]) || 0;
  // cells already empty, and the one that has just gone: the snapshot's
  // magazine is the post-shot count, and the cells go bottom row first
  const spent = Math.min(spec.count, Math.max(0, spec.count - mag));
  const i = Math.max(0, spent - 1);
  return { right: (i % spec.cols - (spec.cols - 1) / 2) * spec.pitch,
           up: (Math.floor(i / spec.cols) - (spec.rows - 1) / 2) * spec.pitch,
           spent };
}

// One high-mounted weapon station. Every piece is placed `dist` metres out
// along the barrel, so pod, tubes and muzzle brake elevate as one assembly.
export function armModel(b, e, arm, wdefs, flashes, now, shoulderY, lateral) {
  const side = arm === 0 ? -1 : 1;
  const mod = arm === 0 ? 2 : 3;
  const hp = e.hp || [1, 1, 1, 1];
  const alive = hp[mod] > 0;
  const col = alive ? METAL : DEAD;
  const wid = e.weapon ? e.weapon[arm] : 0;
  const wd = wdefs[wid] || {};
  const kind = wd.kind || 'none';
  // how the launcher is hung (`defs.rack`, the same spec the server fires
  // from): bolted at its own quadrant, so it stands at 42 deg whatever the arm
  // does (the top attack), or carried on the arm, where its elevation is a
  // bias above the arm's aim (a beam rider's 10 deg soft launch) - and a rack
  // mounted flat follows the arm exactly, which is what lets the rocket nest
  // depress onto something close.
  const spec = wd.rack || null;
  const mount = spec ? spec.elev || 0 : 0;
  const p = alive ? (spec && spec.bolted ? mount : e.pitch + mount) : -0.5;
  const yaw = e.yaw;
  const fac = facColor(e);
  const fl = flashes && flashes.get(e.id);
  const flashing = !!(alive && fl && fl.arm === arm);
  const lat = side * lateral;
  const put = (mesh, meshPitch, dist, sx, sy, sz, colour, dlat = 0, dy = 0) =>
    at(b, mesh, e, yaw, meshPitch, lat + dlat,
      shoulderY + dist * Math.sin(p) + dy * Math.cos(p),
      dist * Math.cos(p) - dy * Math.sin(p), sx, sy, sz, colour);

  // A plain station body for everything that is not a rocket nest: the nest
  // is a long box of its own and would only be swallowed by it.  Every arm's
  // box is the same idea - the 105 mm's is simply longer, narrower and taller,
  // and on it the box is the whole magazine/autoloader/breech assembly.
  // A launcher that carries its rounds in tubes (`wd.rack`) is the whole arm in
  // the same way, and it has to be *seen* as tubes: the station body would
  // swallow the mouths at both ends and the shoulder ball would hide the rear
  // ones, so a racked arm draws neither and hangs off the bracket alone.
  const racked = !!wd.rack;
  const heavy = kind === 'bullet' && wd.heavy;
  if (kind !== 'rocket' && !racked) {
    // the same box for every arm, only the 105 mm's is bigger - and it is big
    // enough to fill the arm on its own, so the shoulder ball is left out
    // there (a 0.66 m ball out of a 0.40 m box would be a ball, not a breech)
    put(MESH.BOX, p, heavy ? GUN105_BOX : 0.25,
      heavy ? GUN105_L : 1.65, heavy ? GUN105_H : 0.70, heavy ? GUN105_W : 0.62, col);
  }
  if (!heavy && !racked) put(MESH.SPHERE, 0, 0, 0.66, 0.66, 0.66, alive ? METAL_L : DEAD);
  if (kind === 'bullet' && wd.gatling) {
    // The 12.7 mm is a rotary gun: six barrels on a ring round the bore, each
    // half the 30 mm's bore and 60 % of its length, turning while it feeds.
    // The angle lives here because the draw is stateless, and it is wound up
    // by the same `flashes` entry that lights the muzzle - the one bit of
    // "this arm is firing right now" the server already sends.  That entry
    // blinks at the meta rate, so the cluster coasts a moment after the last
    // round instead of stopping dead between two of them.
    const key = `${e.id}:${arm}`;
    const t = now / 1000;
    let sp = SPIN.get(key);
    if (!sp) SPIN.set(key, (sp = { a: 0, t, fire: -1 }));
    if (flashing) sp.fire = t;
    if (alive && t - sp.fire < GAT_COAST) {
      sp.a = (sp.a + GAT_SPIN * Math.min(0.1, t - sp.t)) % (Math.PI * 2);
    }
    sp.t = t;
    if (SPIN.size > 256) SPIN.clear();
    for (let i = 0; i < 6; i++) {
      const th = sp.a + i * (Math.PI / 3);
      put(MESH.CYL, FORWARD + p, 0.90 + GAT_LEN * 0.5, GAT_BORE, GAT_LEN, GAT_BORE,
        [0.12, 0.13, 0.14], GAT_RING * Math.sin(th), GAT_RING * Math.cos(th));
    }
    // the axle they turn on, a housing plate at the back and the bearing ring
    // near the muzzle that the barrels pass through - the ring is what reads
    // the cluster as a rotary gun rather than a bundle of tubes
    put(MESH.CYL, FORWARD + p, 0.90 + GAT_LEN * 0.45, 0.05, GAT_LEN * 0.9, 0.05, METAL_D);
    put(MESH.CYL, FORWARD + p, 0.90 + 0.10, GAT_HOOP, 0.10, GAT_HOOP, [0.1, 0.1, 0.11]);
    put(MESH.CYL, FORWARD + p, GAT_MUZZLE - GAT_COLLAR, GAT_HOOP, 0.16, GAT_HOOP, METAL_D);
  } else if (heavy) {
    // 5 m of barrel out of the box, the 30 mm's tube scaled up: a heavy
    // smoothbore with a thermal sleeve over the middle of it and a muzzle
    // collar at the end (the round leaves at `GUN105_MUZZLE`).
    put(MESH.CYL, FORWARD + p, GUN105_FACE + GUN105_LEN * 0.5, GUN105_BORE, GUN105_LEN,
      GUN105_BORE, [0.12, 0.13, 0.14]);
    put(MESH.CYL, FORWARD + p, GUN105_FACE + GUN105_LEN * 0.38, GUN105_BORE * 1.25,
      GUN105_LEN * 0.62, GUN105_BORE * 1.25, METAL_D);
    put(MESH.CYL, FORWARD + p, GUN105_MUZZLE - 0.11, GUN105_BORE * 1.3, 0.22,
      GUN105_BORE * 1.3, [0.1, 0.1, 0.11]);
  } else if (kind === 'bullet') {
    const len = 3.2;
    put(MESH.CYL, FORWARD + p, 0.90 + len * 0.5, 0.17, len, 0.17, [0.12, 0.13, 0.14]);
    put(MESH.CYL, FORWARD + p, 0.90 + len + 0.18, 0.28, 0.42, 0.28, [0.1, 0.1, 0.11]);
  } else if (wd.rack) {
    // Any launcher that carries its rounds in tubes: the rocket nest (5 x 8,
    // forty rounds) and the top-attack rack (2 x 2, four) are the same model
    // with different numbers in their `rack`.
    rack(put, p, wd.rack, alive ? fac : DEAD, tubeOf(wd, e, arm));
  } else {
    put(MESH.CYL, FORWARD + p, 1.35, 0.18, 0.90, 0.18, [0.12, 0.12, 0.13]);
  }
  if (alive && fl && fl.arm === arm) {
    // the gatling's flash comes out of the barrel that has just turned up to
    // the top of the ring, not out of the middle of the cluster
    const gat = kind === 'bullet' && wd.gatling;
    // a round leaves its own tube of the rack, and which one that is follows
    // from what is left in it - the server solves the launch point off the same
    // lattice (`defs.rack_tube`).  The snapshot's magazine is the post-shot
    // count, hence the +1.
    const cell = tubeOf(wd, e, arm);
    const right = cell ? cell.right : 0;
    const up = gat ? GAT_RING : cell ? cell.up : 0;
    const tip = gat ? GAT_MUZZLE - 0.10
      : heavy ? GUN105_MUZZLE - 0.15
        : kind === 'bullet' ? 4.35
          : wd.rack ? wd.rack.mouth - 0.05 : 2.15;
    const s = gat ? 0.42 : heavy ? 1.0 : 0.75;
    put(MESH.SPHERE, p, tip, s, s, s, [1.0, 0.72, 0.22], right, up);
    put(MESH.CONE, FORWARD + p, tip + (gat ? 0.55 : 0.85), s * 0.77,
      gat ? 0.9 : 1.4, s * 0.77, [1.0, 0.88, 0.45], right, up);
  }
}
