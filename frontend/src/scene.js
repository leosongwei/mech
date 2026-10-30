// Builds the per-frame instance list from abstract primitives:
// a mech is boxes + cylinders + a sphere cockpit, an infantryman is a sphere
// head on a cone body with a cylinder rifle, a tree is a cone on a cylinder.
// Local frame: +X right, +Y up, +Z forward.

const METAL = [0.44, 0.46, 0.48];
const METAL_D = [0.28, 0.30, 0.33];
const METAL_L = [0.58, 0.60, 0.63];
const CONCRETE = [0.50, 0.51, 0.49];
const CONCRETE_D = [0.37, 0.38, 0.37];
const GLASS = [0.10, 0.19, 0.25];
const DEAD = [0.10, 0.10, 0.10];
const HULK = [0.13, 0.12, 0.11];    // burnt metal: wrecks and corpses
const SOOT = [0.09, 0.09, 0.09];

// The commander's panoramic sight stands on the roof with its optical axis on
// the machine's eye point (`defs.MECH_EYE`, mirrored to the client as
// `defs.mech.eye`): the cockpit camera sits exactly there, at the sight window,
// so the pilot's own head is left out of the draw list - it would be drawn
// round the camera - while every other machine keeps it.  This number and
// MECH_EYE move together.
const SIGHT_Y = 5.05;
// The pillar the head turns on, in the hull's own frame: `defs.MECH_SIGHT_Z`,
// the same pivot the server puts the pilot's eye on.  The two numbers move
// together, and the geometry only reads right if they do.
const SIGHT_Z = -0.62;

function dim(c, k) {
  return [c[0] * k, c[1] * k, c[2] * k];
}

export const FACTION_COLORS = {
  0: [0.84, 0.22, 0.16],
  1: [0.20, 0.44, 0.92],
  2: [0.62, 0.36, 0.85],
  3: [0.86, 0.86, 0.84],
};

function facColor(e) {
  return FACTION_COLORS[e.faction] || [0.8, 0.8, 0.8];
}

function at(R, shape, e, yaw, pitch, ox, oy, oz, sx, sy, sz, col, alpha) {
  const c = Math.cos(yaw), s = Math.sin(yaw);
  const x = e.x + (-s) * ox + c * oz;
  const z = e.z + c * ox + s * oz;
  R[shape](x, e.y + oy, z, yaw, pitch, sx, sy, sz,
    col[0], col[1], col[2], alpha === undefined ? 1 : alpha);
}

const FORWARD = -Math.PI / 2;   // rotates a Y-axis primitive onto +X

// A piece of the sight head.  `at` turns the *offset* with the same yaw it
// turns the mesh with, which is right for anything bolted to a hull - and
// wrong for the head, whose pivot is fixed to the hull while the head turns on
// it: the head must rotate on the pillar, not orbit the machine.  So the
// pivot's place comes from the hull's yaw and only the offset from the
// pivot - `right` / `up` / `fwd` in the head's own frame, pitch and all -
// follows the sight's.
function headAt(R, shape, e, hullYaw, syaw, spitch,
                 right, up, fwd, sx, sy, sz, col, meshPitch) {
  const ch = Math.cos(hullYaw), sh = Math.sin(hullYaw);
  const px = e.x + ch * SIGHT_Z, pz = e.z + sh * SIGHT_Z;   // the pillar
  const cp = Math.cos(spitch), sp = Math.sin(spitch);
  const out = fwd * cp - up * sp;         // out along the head's ground track
  const high = fwd * sp + up * cp;        // up the pitched head axis
  const cy = Math.cos(syaw), sn = Math.sin(syaw);
  const x = px + (-sn) * right + cy * out;
  const z = pz + cy * right + sn * out;
  R[shape](x, e.y + SIGHT_Y + high, z, syaw,
    meshPitch === undefined ? spitch : meshPitch, sx, sy, sz, col[0], col[1], col[2], 1);
}

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
const SPIN = new Map();         // "eid:arm" -> { a, t, fire }: the draw is stateless

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

export function drawUnits(R, entities, projectiles, defs, flashes, ownId) {
  R.beginDynamic();
  for (const e of entities.values()) {
    switch (e.type) {
      case 1: infantry(R, e, false); break;
      case 2: infantry(R, e, true); break;
      case 3: apc(R, e, flashes); break;
      case 4: mech(R, e, defs, flashes, e.id === ownId); break;
      case 5: base(R, e); break;
      case 6: civilian(R, e); break;
      case 7: wreck(R, e); break;
      case 8: corpse(R, e); break;
      case 9: bunker(R, e); break;
      default: break;
    }
  }
  for (const p of projectiles) projectile(R, p, defs);
}

// Indestructible blockhouse on a ridge: a squat concrete drum with an
// observation deck and a red cap, so the zeroing reference reads at 2 km
function bunker(R, e) {
  const yaw = e.yaw;
  at(R, 'cyl', e, yaw, 0, 0, 4.0, 0, 11, 8, 11, CONCRETE);
  at(R, 'cyl', e, yaw, 0, 0, 9.3, 0, 8, 2.6, 8, CONCRETE_D);
  at(R, 'box', e, yaw, 0, 0, 10.9, 0, 3.4, 0.6, 3.4, [0.78, 0.18, 0.12]);
  for (let i = 0; i < 4; i++) {                 // firing slit on each face
    at(R, 'box', e, yaw + i * Math.PI / 2, 0, 0, 5.0, 5.5, 3.6, 1.0, 0.5, SOOT);
  }
}

// A hulk: the same silhouette as the living machine, burnt down and dark.
// The server encodes what it used to be in flags bits 4-5.
function wreck(R, e) {
  const kind = (e.flags >> 4) & 3;
  const yaw = e.yaw;
  const fac = dim(facColor(e), 0.32);
  if (kind === 1) {                       // APC
    at(R, 'box', e, yaw, 0.06, 0, 1.05, 0, 3.35, 0.9, 3.35, HULK);
    at(R, 'box', e, yaw, 0.06, 0, 0.45, 0, 3.1, 0.4, 3.1, SOOT);
    at(R, 'box', e, yaw, 0.06, 0, 1.7, -0.55, 2.4, 0.5, 2.4, fac);
  } else {                                // mech, collapsed on its plenums
    for (const side of [-1, 1]) {
      at(R, 'box', e, e.leg, 0, side * 1.32, 0.30, 0, 4.70, 0.46, 1.15, SOOT);
      at(R, 'box', e, e.leg, 0.35, side * 1.20, 0.78, 0.1, 1.0, 0.9, 0.75, HULK);
    }
    at(R, 'box', e, yaw, 0.12, 0, 1.20, -0.65, 4.05, 1.15, 1.60, HULK);
    at(R, 'box', e, yaw, 0.12, 0, 1.90, -0.85, 2.85, 0.50, 1.35, fac);
    at(R, 'nose', e, yaw, 0, 0.10, 1.55, 1.75, 2.10, 1.00, 1.25, HULK);
  }
}

// Infantry toppled over: the body lies along its last facing.
function corpse(R, e) {
  const fac = dim(facColor(e), 0.45);
  const yaw = e.yaw;
  at(R, 'cone', e, yaw, FORWARD, 0, 0.24, 0, 0.6, 1.55, 0.6, fac);
  at(R, 'sphere', e, yaw, 0, 0.95, 0.26, 0, 0.46, 0.4, 0.46, [0.32, 0.27, 0.22]);
  at(R, 'cyl', e, yaw, FORWARD, -0.4, 0.14, 0.1, 0.1, 1.1, 0.1, SOOT);
}

// sphere head, cone torso, cylinder weapon
function infantry(R, e, rocket) {
  const fac = facColor(e);
  const yaw = e.yaw;
  at(R, 'cone', e, yaw, 0, 0, 0.55, 0, 0.75, 1.15, 0.9, fac);
  at(R, 'sphere', e, yaw, 0, 0, 1.38, 0, 0.52, 0.52, 0.52, [0.45, 0.38, 0.30]);
  at(R, 'sphere', e, yaw, 0, 0, 1.62, 0, 0.56, 0.24, 0.56, [0.28, 0.30, 0.26]);
  if (rocket) {
    at(R, 'cyl', e, yaw, FORWARD, 0.22, 1.15, 0.55, 0.34, 2.1, 0.34, [0.26, 0.29, 0.20]);
    at(R, 'cone', e, yaw, FORWARD, 0.22, 1.15, 1.7, 0.30, 0.55, 0.30, [0.7, 0.25, 0.15]);
  } else {
    at(R, 'cyl', e, yaw, FORWARD, 0.24, 1.05, 0.5, 0.11, 1.15, 0.11, [0.10, 0.10, 0.10]);
  }
}

function civilian(R, e) {
  const fac = facColor(e);
  at(R, 'cone', e, e.yaw, 0, 0, 0.52, 0, 0.7, 1.05, 0.8, fac);
  at(R, 'sphere', e, e.yaw, 0, 0, 1.28, 0, 0.46, 0.5, 0.46, [0.58, 0.44, 0.36]);
}

function apc(R, e, flashes) {
  const fac = facColor(e);
  const yaw = e.yaw;
  const tYaw = e.turret;
  at(R, 'box', e, yaw, 0, 0, 1.35, 0, 3.4, 1.3, 3.4, METAL);
  at(R, 'box', e, yaw, 0, 0, 0.45, 0, 3.1, 0.5, 3.2, METAL_D);
  at(R, 'box', e, yaw, 0, 0, 1.7, -0.6, 2.6, 0.7, 2.6, fac);
  at(R, 'box', e, yaw, 0, 0, 1.55, 2.3, 1.4, 0.5, 2.2, GLASS);
  for (const s of [-1, 1]) {
    for (const f of [-1.15, 0, 1.15]) {
      at(R, 'sphere', e, yaw, 0, s * 1.75, 0.62, f, 1.25, 1.25, 1.25, [0.08, 0.08, 0.09]);
    }
  }
  // the turret's barrel and its flash go out along the *elevated* bore, not
  // along the hull's ground track (`at` lays an offset out in the yaw plane)
  const cp = Math.cos(e.pitch), sp = Math.sin(e.pitch);
  at(R, 'box', e, tYaw, e.pitch, 0, 2.85, 0, 2.1, 1.0, 2.3, METAL_L);
  at(R, 'cyl', e, tYaw, FORWARD + e.pitch, 0, 2.9 + 1.6 * sp, 1.6 * cp,
    0.26, 3.6, 0.26, [0.12, 0.12, 0.13]);
  const af = flashes && flashes.get(e.id);
  if (af) {
    at(R, 'sphere', e, tYaw, e.pitch, 0, 2.9 + 3.5 * sp, 3.5 * cp,
      0.8, 0.8, 0.8, [1.0, 0.85, 0.4]);
  }
}

function mech(R, e, defs, flashes, own) {
  const fac = facColor(e);
  const legYaw = e.leg;
  const yaw = e.yaw;
  const wdefs = defs || {};

  // --- legs ride on a turntable, not on the hull ----------------------
  // The upper body swings with the view while the feet keep the direction of
  // travel (the server turns them at 2.6 rad/s), so the two halves are bolted
  // to a pivot: a trapezoidal pedestal - a plain cuboid head-on - comes down
  // off the hull, a bearing ring and a yoke carry both legs.  Nothing else
  // bridges the joint, so the hull can swing right over its own feet.
  at(R, 'wedge', e, yaw, 0, 0, 1.88, -0.30, 2.30, 0.34, 1.44, METAL_D);
  at(R, 'cyl', e, legYaw, 0, 0, 1.63, -0.30, 1.12, 0.16, 1.12, METAL_L);
  at(R, 'box', e, legYaw, 0, 0, 1.40, -0.30, 1.05, 0.30, 2.60, METAL_D);

  // --- low duct legs / broad hover plenums (travel direction) ---------
  const legSegment = (a, b, width, depth, col, shape = 'box') => {
    const dy = b[1] - a[1], dz = b[2] - a[2];
    const len = Math.hypot(dy, dz);
    const pitch = Math.atan2(-dz, dy);
    at(R, shape, e, legYaw, pitch, (a[0] + b[0]) / 2, (a[1] + b[1]) / 2,
      (a[2] + b[2]) / 2, depth, len, width, col);
  };
  for (const side of [-1, 1]) {
    const hip = [side * 1.06, 1.38, -0.30];
    const knee = [side * 1.20, 0.98, 0.60];
    const ankle = [side * 1.28, 0.66, -0.02];
    const outer = (p) => [p[0] + side * 0.42, p[1], p[2] - 0.08];
    legSegment(hip, knee, 0.82, 1.00, METAL);
    legSegment(knee, ankle, 0.78, 0.90, METAL);
    legSegment(outer(hip), outer(knee), 0.28, 0.28, METAL_L, 'cyl');
    legSegment(outer(knee), outer(ankle), 0.25, 0.25, METAL_L, 'cyl');
    for (const p of [hip, knee, ankle]) {
      at(R, 'sphere', e, legYaw, 0, p[0], p[1], p[2], 0.60, 0.56, 0.60, METAL_D);
    }
    // A flat pressure chamber and a dark flexible skirt, not an upturned ski.
    at(R, 'box', e, legYaw, 0, side * 1.32, 0.34, 0, 4.60, 0.50, 1.20, METAL_D);
    at(R, 'cone', e, legYaw, FORWARD, side * 1.32, 0.34, 2.40, 0.50, 0.55, 1.20, METAL_D);
    at(R, 'cone', e, legYaw, -FORWARD, side * 1.32, 0.34, -2.40, 0.50, 0.55, 1.20, METAL_D);
    at(R, 'box', e, legYaw, 0, side * 1.32, 0.10, 0, 4.90, 0.18, 1.10, SOOT);
    at(R, 'box', e, legYaw, 0, side * 1.32, 0.61, 0, 3.65, 0.08, 0.82, METAL_L);
  }

  // --- narrow longitudinal hull / tandem vertical turbines ------------
  // One cuboid from the belly to the roof line: no separate raised deck, so
  // the hull's front cross-section is exactly what the cockpit grows out of.
  // The cockpit is that same block carried on to a short transverse line at
  // mid-height - roof and belly slope in (two trapezoids), the cheeks close
  // the sides (two triangles) - so it reads as a triangle from the side, a
  // trapezoid from above and an X head-on, never as a cone or as a smaller
  // block bolted onto the nose.
  at(R, 'box', e, yaw, 0, 0, 3.01, -1.10, 3.60, 2.12, 1.72, METAL);
  at(R, 'nose', e, yaw, 0, 0, 3.01, 1.425, 1.47, 2.12, 1.72, METAL);
  for (const side of [-1, 1]) {
    at(R, 'box', e, yaw, 0, side * 0.87, 3.00, -0.90, 2.30, 0.28, 0.05, fac);
  }
  for (const z of [-1.40, -2.40]) {
    at(R, 'cyl', e, yaw, 0, 0, 4.10, z, 1.04, 0.18, 1.04, METAL_D);
    at(R, 'cyl', e, yaw, 0, 0, 4.21, z, 0.78, 0.05, 0.78, SOOT);
    at(R, 'cone', e, yaw, 0, 0, 4.30, z, 0.18, 0.18, 0.18, METAL_L);
  }
  at(R, 'cyl', e, yaw, FORWARD, 0, 2.90, -2.98, 0.75, 0.60, 0.75, METAL_D);
  at(R, 'cyl', e, yaw, FORWARD, 0, 2.90, -3.30, 0.54, 0.06, 0.54, SOOT);

  // Tank-style roof hatch on the flat roof, commander's panoramic sight behind
  // it.  The pillar lifts the head half a hatch diameter clear of the roof
  // line; the lid hinges forward over the nose, so an open hatch can never
  // foul the sight.
  at(R, 'cyl', e, yaw, 0, 0, 4.15, 0.25, 0.86, 0.16, 0.86, METAL_D);
  at(R, 'cyl', e, yaw, 0, 0, 4.24, 0.25, 0.70, 0.05, 0.70, METAL_L);
  if (!own) {
    // The head is a free turret: it stands on its pillar (whose axis is the
    // sight's, `SIGHT_Z` behind the hull's) and answers the view while the hull
    // is still coming round, so it is drawn on the *sight's* angles - the
    // snapshot's `syaw` / `spitch`, not the hull's.  It turns about the pillar
    // and tilts about its own centre, which is the eye point: nothing here can
    // move the camera that sits in it.
    const syaw = e.syaw === undefined ? yaw : e.syaw;
    const spitch = e.spitch === undefined ? 0 : e.spitch;
    // the pedestal and the hinge are the hull's: the pillar is a post, and the
    // head turns on top of it
    at(R, 'cyl', e, yaw, 0, 0, 4.16, SIGHT_Z, 0.52, 0.20, 0.52, METAL_D);
    at(R, 'box', e, yaw, 0, 0, 4.50, SIGHT_Z, 0.34, 0.56, 0.34, METAL_D);
    headAt(R, 'box', e, yaw, syaw, spitch, 0, 0, 0, 0.56, 0.62, 0.62, METAL_L);
    headAt(R, 'box', e, yaw, syaw, spitch, 0, 0.32, 0, 0.18, 0.10, 0.54, METAL_D);
    // One big sight window with a pair of small ones stacked beside it, set
    // into the head's front face - flush, so the block still reads as one
    // armoured box.  The big lens is the gunner's channel, the pair the
    // wide/narrow backups; the asymmetry is what tells the head apart from a
    // plain box.  The windows ride the tilted face, not the yaw plane.
    // (the windows are cylinders lying along the optical axis: their mesh
    // pitch is the forward one, tilted by the head's own elevation)
    const bore = FORWARD + spitch;
    headAt(R, 'cyl', e, yaw, syaw, spitch, 0.10, 0, 0.285, 0.36, 0.10, 0.36, GLASS, bore);
    for (const dy of [-0.16, 0.16]) {
      headAt(R, 'cyl', e, yaw, syaw, spitch, -0.20, dy, 0.285,
        0.16, 0.10, 0.16, GLASS, bore);
    }
  }

  // High, close-in weapon stations; module indices remain 2 / 3.
  for (const side of [-1, 1]) {
    const wd = wdefs[e.weapon ? e.weapon[side < 0 ? 0 : 1] : 0] || {};
    if (wd.rack) {
      // a launcher of tubes is slung on the arm's own pivot, so its link leaves
      // the hull *there* and meets the body dead centre: a link 0.3 m further
      // forward grabs a launcher standing at its own elevation under its lower
      // edge, which reads as clipping.  Longer, too - it has to reach into the
      // body, not stop at its face.
      at(R, 'box', e, yaw, 0, side * 1.04, 3.55, 0, 0.26, 0.26, 0.46, METAL_D);
    } else {
      at(R, 'box', e, yaw, 0, side * 0.99, 3.55, 0.30, 0.30, 0.34, 0.22, METAL_D);
    }
  }
  for (let arm = 0; arm < 2; arm++) armModel(R, e, arm, wdefs, flashes, 3.55, 1.43);
}

// A launcher that carries its rounds in tubes draws one hexagonal cell per
// round on the grid the server fires them out of (`defs.rack_tube`): same
// columns, same rows, same pitch, so the mouth a round leaves is the mouth that
// was drawn.  The body is the box `defs.rack` derived for it - the lattice plus
// a frame, shorter than the tubes so every mouth stands proud of the face
// instead of being punched through a slab.
function rack(put, p, spec, colour, flash) {
  // `bore` is the tube's outside diameter, and every primitive here scales as a
  // width, not a radius: drawing it at `bore / 2` put a tube a quarter of the
  // area on the face and left a hole's worth of empty box around it.
  const r = spec.bore;
  // a box scales on X = along the bore, Y = up, Z = across (`box_h` is the
  // height and `box_w` the width - a five-across / eight-high nest is taller
  // than it is wide, and the other way round it would cut its own tubes off)
  put('box', p, spec.centre, spec.box_len, spec.box_h, spec.box_w, colour);
  for (let row = 0; row < spec.rows; row++) {
    for (let col = 0; col < spec.cols; col++) {
      // the cells still loaded are drawn as bores; spent ones go dark, which is
      // the only way the rack itself can show it is running out
      const idx = row * spec.cols + col;
      const spent = flash && idx < flash.spent;
      put('hex', FORWARD + p, spec.centre, r, spec.length, r,
        spent ? SPENT_TUBE : TUBE_COL,
        (col - (spec.cols - 1) / 2) * spec.pitch,
        (row - (spec.rows - 1) / 2) * spec.pitch);
    }
  }
}

// Which tube of a racked launcher the round that has just gone left: the
// bottom row first, left to right, the order `defs.rack_tube` gives the
// server.  `null` for a weapon that fires off the bore.
function tubeOf(wd, e, arm) {
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
function armModel(R, e, arm, wdefs, flashes, shoulderY, lateral) {
  const side = arm === 0 ? -1 : 1;
  const mod = arm === 0 ? 2 : 3;
  const hp = e.hp || [1, 1, 1, 1];
  const alive = hp[mod] > 0;
  const col = alive ? METAL : DEAD;
  const wid = e.weapon ? e.weapon[arm] : 0;
  const wd = wdefs[wid] || {};
  const kind = wd.kind || 'none';
  // a launcher bolted at its own quadrant (`rack.elev`) stands at that angle
  // and does not answer the cross: the tubes are the ones the round leaves
  // from, so the arm is drawn where the barrel really is, not where the sight
  // is looking.  Everything else elevates with the arm.
  const mount = wd.rack && wd.rack.elev;
  const p = alive ? (mount ? mount : e.pitch) : -0.5;
  const yaw = e.yaw;
  const fac = facColor(e);
  const fl = flashes && flashes.get(e.id);
  const flashing = !!(alive && fl && fl.arm === arm);
  const lat = side * lateral;
  const put = (shape, meshPitch, dist, sx, sy, sz, colour, dlat = 0, dy = 0) =>
    at(R, shape, e, yaw, meshPitch, lat + dlat,
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
    put('box', p, heavy ? GUN105_BOX : 0.25,
      heavy ? GUN105_L : 1.65, heavy ? GUN105_H : 0.70, heavy ? GUN105_W : 0.62, col);
  }
  if (!heavy && !racked) put('sphere', 0, 0, 0.66, 0.66, 0.66, alive ? METAL_L : DEAD);
  if (kind === 'bullet' && wd.gatling) {
    // The 12.7 mm is a rotary gun: six barrels on a ring round the bore, each
    // half the 30 mm's bore and 60 % of its length, turning while it feeds.
    // The angle lives here because the draw is stateless, and it is wound up
    // by the same `flashes` entry that lights the muzzle - the one bit of
    // "this arm is firing right now" the server already sends.  That entry
    // blinks at the meta rate, so the cluster coasts a moment after the last
    // round instead of stopping dead between two of them.
    const key = `${e.id}:${arm}`;
    const now = performance.now() / 1000;
    let sp = SPIN.get(key);
    if (!sp) SPIN.set(key, (sp = { a: 0, t: now, fire: -1 }));
    if (flashing) sp.fire = now;
    if (alive && now - sp.fire < GAT_COAST) {
      sp.a = (sp.a + GAT_SPIN * Math.min(0.1, now - sp.t)) % (Math.PI * 2);
    }
    sp.t = now;
    if (SPIN.size > 256) SPIN.clear();
    for (let i = 0; i < 6; i++) {
      const th = sp.a + i * (Math.PI / 3);
      put('cyl', FORWARD + p, 0.90 + GAT_LEN * 0.5, GAT_BORE, GAT_LEN, GAT_BORE,
        [0.12, 0.13, 0.14], GAT_RING * Math.sin(th), GAT_RING * Math.cos(th));
    }
    // the axle they turn on, a housing plate at the back and the bearing ring
    // near the muzzle that the barrels pass through - the ring is what reads
    // the cluster as a rotary gun rather than a bundle of tubes
    put('cyl', FORWARD + p, 0.90 + GAT_LEN * 0.45, 0.05, GAT_LEN * 0.9, 0.05, METAL_D);
    put('cyl', FORWARD + p, 0.90 + 0.10, GAT_HOOP, 0.10, GAT_HOOP, [0.1, 0.1, 0.11]);
    put('cyl', FORWARD + p, GAT_MUZZLE - GAT_COLLAR, GAT_HOOP, 0.16, GAT_HOOP, METAL_D);
  } else if (heavy) {
    // 5 m of barrel out of the box, the 30 mm's tube scaled up: a heavy
    // smoothbore with a thermal sleeve over the middle of it and a muzzle
    // collar at the end (the round leaves at `GUN105_MUZZLE`).
    put('cyl', FORWARD + p, GUN105_FACE + GUN105_LEN * 0.5, GUN105_BORE, GUN105_LEN,
      GUN105_BORE, [0.12, 0.13, 0.14]);
    put('cyl', FORWARD + p, GUN105_FACE + GUN105_LEN * 0.38, GUN105_BORE * 1.25,
      GUN105_LEN * 0.62, GUN105_BORE * 1.25, METAL_D);
    put('cyl', FORWARD + p, GUN105_MUZZLE - 0.11, GUN105_BORE * 1.3, 0.22,
      GUN105_BORE * 1.3, [0.1, 0.1, 0.11]);
  } else if (kind === 'bullet') {
    const len = 3.2;
    put('cyl', FORWARD + p, 0.90 + len * 0.5, 0.17, len, 0.17, [0.12, 0.13, 0.14]);
    put('cyl', FORWARD + p, 0.90 + len + 0.18, 0.28, 0.42, 0.28, [0.1, 0.1, 0.11]);
  } else if (wd.rack) {
    // Any launcher that carries its rounds in tubes: the rocket nest (5 x 8,
    // forty rounds) and the top-attack rack (2 x 2, four) are the same model
    // with different numbers in their `rack`.
    rack(put, p, wd.rack, alive ? fac : DEAD, tubeOf(wd, e, arm));
  } else if (kind === 'saclos') {
    put('box', p, 0.95, 1.40, 0.80, 0.76, alive ? fac : DEAD);
    for (const q of [-1, 1]) {
      put('hex', FORWARD + p, 1.76, 0.24, 0.30, 0.24, [0.08, 0.08, 0.09], q * 0.22);
    }
  } else {
    put('cyl', FORWARD + p, 1.35, 0.18, 0.90, 0.18, [0.12, 0.12, 0.13]);
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
    put('sphere', p, tip, s, s, s, [1.0, 0.72, 0.22], right, up);
    put('cone', FORWARD + p, tip + (gat ? 0.55 : 0.85), s * 0.77,
      gat ? 0.9 : 1.4, s * 0.77, [1.0, 0.88, 0.45], right, up);
  }
}

function base(R, e) {
  const fac = facColor(e);
  at(R, 'box', e, 0, 0, 0, 9, 0, 46, 18, 34, [0.34, 0.35, 0.38]);
  at(R, 'box', e, 0, 0, 0, 19, 0, 28, 4, 20, fac);
  at(R, 'cyl', e, 0, 0, 0, 26, 0, 6, 12, 6, [0.45, 0.46, 0.5]);
  at(R, 'cone', e, 0, 0, 0, 35, 0, 10, 10, 10, fac);
  for (const [sx, sz] of [[-1, -1], [1, -1], [-1, 1], [1, 1]]) {
    at(R, 'cyl', e, 0, 0, sx * 20, 13, sz * 15, 7, 26, 7, [0.40, 0.41, 0.44]);
    at(R, 'sphere', e, 0, 0, sx * 20, 27, sz * 15, 8, 5, 8, fac);
  }
  at(R, 'cyl', e, 0, 0, 0, 34, 0, 0.9, 16, 0.9, [0.6, 0.6, 0.62]);
  at(R, 'box', e, 0, 0, 0, 41, 3, 0.4, 3, 7, fac);
  at(R, 'sphere', e, 0, 0, 0, 45, 0, 2.5, 2.5, 2.5, [1.0, 0.35, 0.3]);
}

function projectile(R, p, defs) {
  const wd = (defs && defs[p.wid]) || null;
  const col = wd ? wd.color : [1, 0.8, 0.3];
  const speed = Math.hypot(p.vx, p.vy, p.vz);
  if (speed < 1e-3) return;
  const yaw = Math.atan2(p.vz, p.vx);
  const pitch = Math.asin(Math.max(-1, Math.min(1, p.vy / speed)));
  const fake = { x: p.x, y: p.y, z: p.z };
  if (p.kind === 0) {
    // tracer: a flat-shaded four-sided pyramid stretched along the flight
    // path, centred on the round and drawn unlit so it stays the weapon's
    // colour. the flat face leads and the point trails, like the streak the
    // eye keeps after the round has gone by. the bore sizes it, so a 12.7 mm
    // round reads thinner than a 30 mm cannon shell
    const cal = (wd && wd.cal) || 12.7;
    const len = Math.min(16, speed * 0.014);
    const wid = Math.max(0.08, cal * 0.011);
    at(R, 'pyramid', fake, yaw, FORWARD + pitch + Math.PI, 0, 0, 0, wid, len, wid, col);
  } else {
    // Every piece sits `d` metres along the round's own flight path, not along
    // its ground track: `at` only lays an offset out in the yaw plane, so a
    // rocket climbing away would keep its nose, body and flame in the air
    // where a level one would have them - the pieces come apart.
    const put = (shape, d, sx, sy, sz, colour) => at(R, shape, fake, yaw,
      FORWARD + pitch, 0, d * Math.sin(pitch), d * Math.cos(pitch), sx, sy, sz, colour);
    // The round is drawn at its own size when the table says it (`body`, the
    // (across, long) pair - a missile has to be the size of the mouth it leaves
    // from), and at the shared big body everything else flies as otherwise:
    // large enough to read at 2 km, which is what a tracer's shape is for.
    const size = (wd && wd.body) || [0.32, 3.2];
    const bd = size[0], bl = size[1];
    put('cyl', bl * -0.19, bd, bl, bd, [0.78, 0.78, 0.8]);
    put('cone', bl * 0.44, bd, bl * 0.34, bd, col);
    put('sphere', bl * -0.75, bd * 3.1, bd * 3.1, bd * 3.1, [1.0, 0.72, 0.25]);
  }
}
