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
    at(R, 'cyl', e, yaw, 0, 0, 4.16, -0.62, 0.52, 0.20, 0.52, METAL_D);
    at(R, 'box', e, yaw, 0, 0, 4.50, -0.62, 0.34, 0.56, 0.34, METAL_D);
    at(R, 'box', e, yaw, 0, 0, SIGHT_Y, -0.62, 0.56, 0.62, 0.62, METAL_L);
    at(R, 'box', e, yaw, 0, 0, SIGHT_Y + 0.32, -0.62, 0.18, 0.10, 0.54, METAL_D);
    // One big sight window with a pair of small ones stacked beside it, set
    // into the head's front face - flush, so the block still reads as one
    // armoured box.  The big lens is the gunner's channel, the pair the
    // wide/narrow backups; the asymmetry is what tells the head apart from a
    // plain box.
    at(R, 'cyl', e, yaw, FORWARD, 0.10, SIGHT_Y, -0.335, 0.36, 0.10, 0.36, GLASS);
    for (const dy of [-0.16, 0.16]) {
      at(R, 'cyl', e, yaw, FORWARD, -0.20, SIGHT_Y + dy, -0.335,
        0.16, 0.10, 0.16, GLASS);
    }
  }

  // High, close-in weapon stations; module indices remain 2 / 3.
  for (const side of [-1, 1]) {
    at(R, 'box', e, yaw, 0, side * 0.99, 3.55, 0.30, 0.34, 0.22, 0.86, METAL_D);
  }
  for (let arm = 0; arm < 2; arm++) armModel(R, e, arm, wdefs, flashes, 3.55, 1.43);
}

// One high-mounted weapon station. Every piece is placed `dist` metres out
// along the barrel, so pod, tubes and muzzle brake elevate as one assembly.
function armModel(R, e, arm, wdefs, flashes, shoulderY, lateral) {
  const side = arm === 0 ? -1 : 1;
  const mod = arm === 0 ? 2 : 3;
  const hp = e.hp || [1, 1, 1, 1];
  const alive = hp[mod] > 0;
  const col = alive ? METAL : DEAD;
  const p = alive ? e.pitch : -0.5;
  const yaw = e.yaw;
  const fac = facColor(e);
  const wid = e.weapon ? e.weapon[arm] : 0;
  const wd = wdefs[wid] || {};
  const kind = wd.kind || 'none';
  const lat = side * lateral;
  const put = (shape, meshPitch, dist, sx, sy, sz, colour, dlat = 0, dy = 0) =>
    at(R, shape, e, yaw, meshPitch, lat + dlat,
      shoulderY + dist * Math.sin(p) + dy * Math.cos(p),
      dist * Math.cos(p) - dy * Math.sin(p), sx, sy, sz, colour);

  // A plain station body for everything that is not a rocket nest: the nest
  // is a long box of its own and would only be swallowed by it.
  if (kind !== 'rocket') put('box', p, 0.25, 1.65, 0.70, 0.62, col);
  put('sphere', 0, 0, 0.66, 0.66, 0.66, alive ? METAL_L : DEAD);
  if (kind === 'bullet') {
    const len = 3.2;
    put('cyl', FORWARD + p, 0.90 + len * 0.5, 0.17, len, 0.17, [0.12, 0.13, 0.14]);
    put('cyl', FORWARD + p, 0.90 + len + 0.18, 0.28, 0.42, 0.28, [0.1, 0.1, 0.11]);
  } else if (kind === 'rocket') {
    // Forty rounds, forty cells: five across and eight high.  Each cell is a
    // six-walled tube running the launcher's whole length and poking out of
    // both end faces, so the mouth is a hexagon instead of a dot while the
    // sides of the nest stay a clean blue box.
    put('box', p, 0.60, 1.90, 1.26, 0.82, alive ? fac : DEAD);
    for (let row = 0; row < 8; row++) {
      for (let col = 0; col < 5; col++) {
        put('hex', FORWARD + p, 0.60, 0.135, 2.15, 0.135, [0.08, 0.08, 0.09],
          (col - 2) * 0.155, (row - 3.5) * 0.155);
      }
    }
  } else if (kind === 'saclos' || kind === 'top') {
    put('box', p, 0.95, 1.40, 0.80, 0.76, alive ? fac : DEAD);
    for (const q of [-1, 1]) {
      put('hex', FORWARD + p, 1.76, 0.24, 0.30, 0.24, [0.08, 0.08, 0.09], q * 0.22);
    }
  } else {
    put('cyl', FORWARD + p, 1.35, 0.18, 0.90, 0.18, [0.12, 0.12, 0.13]);
  }
  const fl = flashes && flashes.get(e.id);
  if (alive && fl && fl.arm === arm) {
    const tip = kind === 'bullet' ? 4.35 : kind === 'rocket' ? 1.80 : 2.15;
    put('sphere', p, tip, 0.75, 0.75, 0.75, [1.0, 0.72, 0.22]);
    put('cone', FORWARD + p, tip + 0.85, 0.58, 1.4, 0.58, [1.0, 0.88, 0.45]);
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
    put('cyl', -0.6, 0.32, 3.2, 0.32, [0.78, 0.78, 0.8]);
    put('cone', 1.4, 0.32, 1.1, 0.32, col);
    put('sphere', -2.4, 1.0, 1.0, 1.0, [1.0, 0.72, 0.25]);
  }
}
