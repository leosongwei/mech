// Builds the per-frame instance list from abstract primitives:
// a mech is boxes + cylinders + a sphere cockpit, an infantryman is a sphere
// head on a cone body with a cylinder rifle, a tree is a cone on a cylinder.
// Local frame: +X right, +Y up, +Z forward.

const METAL = [0.44, 0.46, 0.48];
const METAL_D = [0.28, 0.30, 0.33];
const METAL_L = [0.58, 0.60, 0.63];
const GLASS = [0.10, 0.19, 0.25];
const DEAD = [0.10, 0.10, 0.10];
const HULK = [0.13, 0.12, 0.11];    // burnt metal: wrecks and corpses
const SOOT = [0.09, 0.09, 0.09];

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

export function drawUnits(R, entities, projectiles, ownMechId, defs, flashes) {
  R.beginDynamic();
  for (const e of entities.values()) {
    switch (e.type) {
      case 1: infantry(R, e, false); break;
      case 2: infantry(R, e, true); break;
      case 3: apc(R, e, flashes); break;
      case 4: mech(R, e, defs, flashes, e.id === ownMechId); break;
      case 5: base(R, e); break;
      case 6: civilian(R, e); break;
      case 7: wreck(R, e); break;
      case 8: corpse(R, e); break;
      default: break;
    }
  }
  for (const p of projectiles) projectile(R, p, defs);
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
  } else {                                // mech, collapsed on its legs
    for (const side of [-1, 1]) {
      at(R, 'box', e, e.leg, 0, side * 1.05, 0.45, 0.3, 1.5, 0.7, 2.6, SOOT);
    }
    at(R, 'box', e, yaw, 0.18, 0, 1.1, 0, 2.6, 1.3, 2.9, HULK);
    at(R, 'box', e, yaw, 0.18, 0, 1.95, 0.4, 2.0, 0.5, 2.2, fac);
    at(R, 'sphere', e, yaw, 0, 0, 2.1, 0.75, 1.2, 0.7, 1.0, SOOT);
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
  at(R, 'box', e, tYaw, e.pitch, 0, 2.85, 0, 2.1, 1.0, 2.3, METAL_L);
  at(R, 'cyl', e, tYaw, FORWARD + e.pitch, 0, 2.9, 1.6, 0.26, 3.6, 0.26, [0.12, 0.12, 0.13]);
  const af = flashes && flashes.get(e.id);
  if (af) {
    at(R, 'sphere', e, tYaw, e.pitch, 0, 2.9, 3.5, 0.8, 0.8, 0.8, [1.0, 0.85, 0.4]);
  }
}

function mech(R, e, defs, flashes, firstPerson) {
  const fac = facColor(e);
  const legYaw = e.leg;
  const yaw = e.yaw;
  const wdefs = defs || {};

  // --- chassis / legs (follow the travel direction) -------------------
  for (const side of [-1, 1]) {
    at(R, 'cyl', e, legYaw, 0, side * 1.05, 1.55, 0, 1.15, 2.7, 1.15, METAL);
    at(R, 'box', e, legYaw, 0, side * 1.05, 0.3, 0.3, 1.5, 0.6, 2.6, METAL_D);
    at(R, 'sphere', e, legYaw, 0, side * 1.05, 2.6, 0, 1.25, 1.1, 1.25, METAL_L);
    at(R, 'cyl', e, legYaw, 0, side * 1.05, 3.0, 0, 0.85, 0.9, 0.85, METAL_D);
  }
  // the pelvis plate is what the cockpit sits on: from inside the pilot looks
  // straight past it and down onto their own legs
  if (!firstPerson) at(R, 'box', e, legYaw, 0, 0, 3.35, 0, 2.9, 0.9, 2.4, METAL_D);

  // --- torso (follows the mouse) --------------------------------------
  // from the cockpit the eye sits inside the hull, so the pilot only sees the
  // legs and the two weapon arms of their own machine
  if (!firstPerson) {
    at(R, 'box', e, yaw, 0, 0, 4.35, 0, 2.6, 1.9, 3.0, METAL);
    at(R, 'box', e, yaw, 0, 0, 4.45, -1.5, 2.2, 1.1, 0.5, fac);
    at(R, 'box', e, yaw, 0, 0, 5.15, 0.35, 1.5, 0.5, 1.9, GLASS);
    at(R, 'sphere', e, yaw, 0, 0, 5.15, 0.75, 1.35, 0.9, 1.1, [0.16, 0.22, 0.26]);
    at(R, 'box', e, yaw, 0, 0, 4.15, 1.95, 1.0, 0.7, 0.6, fac);
    for (const side of [-1, 1]) {
      at(R, 'box', e, yaw, 0, side * 1.35, 5.15, -0.3, 1.05, 0.55, 1.7, METAL_L);
      at(R, 'cone', e, yaw, FORWARD, side * 1.35, 5.15, 0.6, 0.45, 1.4, 0.45, [0.14, 0.14, 0.15]);
    }
  }

  // --- arms: left = module 2, right = module 3 ------------------------
  if (!firstPerson) {
    for (let arm = 0; arm < 2; arm++) armModel(R, e, arm, wdefs, flashes, 4.4, 1.95);
  }
}

// One arm of a mech.  Every piece is placed `dist` metres out along the
// barrel, so the whole assembly - pod, tube and muzzle brake - swings with
// the elevation instead of leaving the brake behind in the air.
// `shoulderY` is the pivot height above the base point, `lateral` the offset
// to the side.
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
  // dist along the barrel line, elevation p
  const put = (shape, meshPitch, dist, sx, sy, sz, colour, dlat) =>
    at(R, shape, e, yaw, meshPitch, lat + (dlat || 0), shoulderY + dist * Math.sin(p),
      dist * Math.cos(p), sx, sy, sz, colour);

  put('box', p, 0.7, 1.25, 1.4, 2.9, col);
  put('sphere', 0, 0.05, 1.35, 1.35, 1.35, alive ? METAL_L : DEAD);
  if (kind === 'bullet') {
    const len = 5.2;
    put('cyl', FORWARD + p, 1.35 + len * 0.5, 0.34, len, 0.34, [0.12, 0.13, 0.14]);
    put('cyl', FORWARD + p, 1.35 + len + 0.2, 0.55, 0.8, 0.55, [0.1, 0.1, 0.11]);
  } else if (kind === 'rocket' || kind === 'saclos' || kind === 'top') {
    put('box', p, 2.1, 1.15, 1.0, 2.6, alive ? fac : DEAD);
    for (const q of [-1, 1]) {
      // the tubes sit side by side inside the pod
      put('cyl', FORWARD + p, 2.9, 0.3, 1.6, 0.3, [0.1, 0.1, 0.11], q * 0.3);
    }
  } else {
    put('cyl', FORWARD + p, 2.15, 0.24, 1.6, 0.24, [0.12, 0.12, 0.13]);
  }
  // muzzle flash
  const fl = flashes && flashes.get(e.id);
  if (alive && fl && fl.arm === arm) {
    const tip = kind === 'bullet' ? 6.8 : 3.4;
    put('sphere', p, tip, 0.9, 0.9, 0.9, [1.0, 0.72, 0.22]);
    put('cone', FORWARD + p, tip + 1.1, 0.7, 1.8, 0.7, [1.0, 0.88, 0.45]);
  }
}

// First person: two compact gun pods hanging off the eye, so the pilot sees
// their own hands in the lower corners and nothing sits in their face.  The
// world model of the same machine is hidden, so this is the only own geometry
// on screen - sized by eye, not by the hull it replaces.
export function drawFirstPersonArms(R, own, defs, flashes, eye) {
  const hp = own.hp || [1, 1, 1, 1];
  const yaw = own.yaw;
  const fac = facColor(own);
  const e = { x: eye[0], y: eye[1], z: eye[2] };
  for (let arm = 0; arm < 2; arm++) {
    const side = arm === 0 ? -1 : 1;
    const alive = hp[arm === 0 ? 2 : 3] > 0;
    const col = alive ? METAL : DEAD;
    const p = alive ? own.pitch : -0.5;
    const wd = (defs && defs[own.weapon ? own.weapon[arm] : 0]) || {};
    const kind = wd.kind || 'none';
    const L = side * 1.3;                        // out to the side of the eye
    const D = 0.95;                              // and below it
    // `out` metres out along the barrel, elevated by p
    const put = (shape, meshPitch, out, sx, sy, sz, colour) =>
      at(R, shape, e, yaw, meshPitch, L, -D + out * Math.sin(p), out * Math.cos(p),
        sx, sy, sz, colour);

    put('box', p, 3.0, 0.44, 0.38, 1.1, col);                    // mount
    if (kind === 'rocket' || kind === 'saclos' || kind === 'top') {
      put('box', p, 3.3, 0.5, 0.46, 1.2, alive ? fac : DEAD);    // launcher pod
      put('cyl', FORWARD + p, 3.5, 0.2, 1.3, 0.2, [0.1, 0.1, 0.11]);
    } else if (kind === 'bullet') {
      put('cyl', FORWARD + p, 4.85, 0.11, 2.4, 0.11, [0.12, 0.13, 0.14]);
      put('cyl', FORWARD + p, 6.15, 0.18, 0.36, 0.18, [0.1, 0.1, 0.11]);
    } else {
      put('cyl', FORWARD + p, 4.5, 0.09, 1.9, 0.09, [0.12, 0.12, 0.13]);
    }
    const fl = flashes && flashes.get(own.id);
    if (alive && fl && fl.arm === arm) {
      const tip = kind === 'bullet' ? 6.35 : 4.45;
      put('sphere', p, tip, 0.55, 0.55, 0.55, [1.0, 0.72, 0.22]);
      put('cone', FORWARD + p, tip + 0.8, 0.36, 1.1, 0.36, [1.0, 0.88, 0.45]);
    }
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
    // colour. the bore sizes it, so a 12.7 mm round reads thinner than a
    // 30 mm cannon shell
    const cal = (wd && wd.cal) || 12.7;
    const len = Math.min(16, speed * 0.014);
    const wid = Math.max(0.08, cal * 0.011);
    at(R, 'pyramid', fake, yaw, FORWARD + pitch, 0, 0, 0, wid, len, wid, col);
  } else {
    at(R, 'cyl', fake, yaw, FORWARD + pitch, 0, 0, -0.6, 0.32, 3.2, 0.32, [0.78, 0.78, 0.8]);
    at(R, 'cone', fake, yaw, FORWARD + pitch, 0, 0, 1.4, 0.32, 1.1, 0.32, col);
    at(R, 'sphere', fake, yaw, pitch, 0, 0, -2.4, 1.0, 1.0, 1.0, [1.0, 0.72, 0.25]);
  }
}
