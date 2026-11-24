// The wheeled hull and the fixtures that share its silhouette: an APC, the
// burnt hulk it becomes and the command base.
//
// An APC is the one chassis whose turret is its own: `e.yaw` is the hull it
// drives on and `e.turret` is where the gun points, so its barrel and its
// muzzle flash go out along the *elevated* turret bore while the wheels stay
// under the hull's ground track.

import { PRIM } from '../../render/primitives.js';
import { FORWARD, at, postAt } from '../helpers.js';
import { METAL, METAL_D, METAL_L, GLASS, HULK, SOOT, facColor, dim } from '../palette.js';

// A wheel: a cylinder laid on its side, axle along the hull's right axis.
// Ry(yaw + pi/2) * Rz(FORWARD) puts the cylinder's Y axis on the right vector;
// `at` turns the offset with that same yaw, so the hull-frame (forward, right)
// pair goes in as (-forward, right). The hub is a wider, lighter cylinder so
// its faces stand a centimetre proud of the tyre on each side.
function wheel(b, e, yaw, f, r) {
  at(b, PRIM.CYL, e, yaw + Math.PI / 2, FORWARD, -f, 0.6, r, 1.2, 0.5, 1.2, [0.08, 0.08, 0.09]);
  at(b, PRIM.CYL, e, yaw + Math.PI / 2, FORWARD, -f, 0.6, r, 0.55, 0.52, 0.55, METAL_D);
}

// The 6x6 gun carrier, in the JAGUAR's proportions: one box hull with a low
// boat-bow frustum on its front and the vision slit in the plate above it,
// one front axle and a close rear pair, a boxy turret with a long gun and a
// sight block.
export function apc(b, e, ctx) {
  const fac = facColor(e);
  const yaw = e.yaw;
  const tYaw = e.turret;
  // The hull sits 0.5 m forward of the turret pivot, which puts the turret
  // aft on the roof while its traverse axis stays on the entity origin.
  at(b, PRIM.BOX, e, yaw, 0, 0, 1.3, -0.4, 3.8, 1.5, 2.9, METAL);
  // The bow is added armour, so it sits low: its rear face spans the hull's
  // front from the belly up to 1.6 m, leaving a vertical band of hull plate
  // above it, and 1.2 m forward it keeps a nose plate at 45% height and 52%
  // width - a frustum from the front, a trapezoid from the side.
  at(b, PRIM.PROW, e, yaw, 0, 0, 1.075, 2.1, 1.2, 1.05, 2.9, METAL);
  // The vision slit is cut into that band of hull plate above the bow,
  // 8 cm proud of the front face.
  at(b, PRIM.BOX, e, yaw, 0, 0, 1.82, 1.53, 0.1, 0.24, 2.3, GLASS);
  at(b, PRIM.BOX, e, yaw, 0, 0, 1.78, -0.5, 3.2, 0.6, 2.5, fac);
  for (const s of [-1, 1]) {
    at(b, PRIM.CYL, e, yaw, 0, s * 0.55, 2.10, -1.1, 0.65, 0.07, 0.65, METAL_D);
    // one front axle under the bow, a close pair at the rear
    for (const f of [1.35, -0.25, -1.35]) wheel(b, e, yaw, f, s * 1.55);
  }
  // The turret box stays level; only the bore elevates. Barrel and flash go
  // out along the *elevated* bore from a pivot 0.5 m forward of the turret
  // centre (`at` lays an offset out in the yaw plane).
  const cp = Math.cos(e.pitch), sp = Math.sin(e.pitch);
  at(b, PRIM.BOX, e, tYaw, 0, 0, 2.45, 0.3, 1.8, 0.75, 2.0, METAL_L);
  at(b, PRIM.BOX, e, tYaw, 0, 0.55, 2.98, 0.05, 0.5, 0.35, 0.5, METAL_D);
  at(b, PRIM.BOX, e, tYaw, 0, 0.55, 2.98, 0.31, 0.06, 0.2, 0.3, GLASS);
  at(b, PRIM.CYL, e, tYaw, FORWARD + e.pitch, 0, 2.45 + 1.4 * sp, 0.8 + 1.4 * cp,
    0.2, 2.8, 0.2, [0.12, 0.12, 0.13]);
  // The crew's aperture, on the turret roof and gimballed on its own bearing: the
  // post turns with the turret, the head with `gaze` (`Systems/AI/Watch.cs`).  A
  // carrier whose whisker is off you has not seen you, whatever its gun is doing.
  const gaze = e.gaze === undefined ? tYaw : e.gaze;
  at(b, PRIM.CYL, e, tYaw, 0, -0.55, 2.92, 0.72, 0.18, 0.28, 0.18, METAL_D);
  postAt(b, PRIM.BOX, e, tYaw, gaze, -0.55, 3.14, 0.72, 0, 0.38, 0.22, 0.26, METAL_L);
  postAt(b, PRIM.BOX, e, tYaw, gaze, -0.55, 3.14, 0.72, 0.21, 0.05, 0.14, 0.18, GLASS);
  const af = ctx.flashes && ctx.flashes.get(e.id);
  if (af) {
    at(b, PRIM.SPHERE, e, tYaw, e.pitch, 0, 2.45 + 3.0 * sp, 0.8 + 3.0 * cp,
      0.8, 0.8, 0.8, [1.0, 0.85, 0.4]);
  }
}

// A hulk: the same silhouette as the living machine, burnt down and dark.
// The server encodes what it used to be in flags bits 4-5.
export function wreck(b, e) {
  const kind = (e.flags >> 4) & 3;
  const yaw = e.yaw;
  const fac = dim(facColor(e), 0.32);
  if (kind === 1) {                       // APC
    at(b, PRIM.BOX, e, yaw, 0.06, 0, 1.05, -0.4, 3.7, 1.2, 2.85, HULK);
    at(b, PRIM.PROW, e, yaw, 0.06, 0, 0.9, 2.0, 1.15, 0.9, 2.85, HULK);
    at(b, PRIM.BOX, e, yaw, 0.06, 0, 1.62, -0.5, 2.9, 0.3, 2.3, fac);
  } else {                                // mech, collapsed on its plenums
    for (const side of [-1, 1]) {
      at(b, PRIM.BOX, e, e.leg, 0, side * 1.32, 0.30, 0, 4.70, 0.46, 1.15, SOOT);
      at(b, PRIM.BOX, e, e.leg, 0.35, side * 1.20, 0.78, 0.1, 1.0, 0.9, 0.75, HULK);
    }
    at(b, PRIM.BOX, e, yaw, 0.12, 0, 1.20, -0.65, 4.05, 1.15, 1.60, HULK);
    at(b, PRIM.BOX, e, yaw, 0.12, 0, 1.90, -0.85, 2.85, 0.50, 1.35, fac);
    at(b, PRIM.NOSE, e, yaw, 0, 0.10, 1.55, 1.75, 2.10, 1.00, 1.25, HULK);
  }
}

export function base(b, e) {
  const fac = facColor(e);
  at(b, PRIM.BOX, e, 0, 0, 0, 9, 0, 46, 18, 34, [0.34, 0.35, 0.38]);
  at(b, PRIM.BOX, e, 0, 0, 0, 19, 0, 28, 4, 20, fac);
  at(b, PRIM.CYL, e, 0, 0, 0, 26, 0, 6, 12, 6, [0.45, 0.46, 0.5]);
  at(b, PRIM.CONE, e, 0, 0, 0, 35, 0, 10, 10, 10, fac);
  for (const [sx, sz] of [[-1, -1], [1, -1], [-1, 1], [1, 1]]) {
    at(b, PRIM.CYL, e, 0, 0, sx * 20, 13, sz * 15, 7, 26, 7, [0.40, 0.41, 0.44]);
    at(b, PRIM.SPHERE, e, 0, 0, sx * 20, 27, sz * 15, 8, 5, 8, fac);
  }
  at(b, PRIM.CYL, e, 0, 0, 0, 34, 0, 0.9, 16, 0.9, [0.6, 0.6, 0.62]);
  at(b, PRIM.BOX, e, 0, 0, 0, 41, 3, 0.4, 3, 7, fac);
  at(b, PRIM.SPHERE, e, 0, 0, 0, 45, 0, 2.5, 2.5, 2.5, [1.0, 0.35, 0.3]);
}
