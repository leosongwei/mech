// The wheeled hull and the fixtures that share its silhouette: an APC, the
// burnt hulk it becomes and the command base.
//
// An APC is the one chassis whose turret is its own: `e.yaw` is the hull it
// drives on and `e.turret` is where the gun points, so its barrel and its
// muzzle flash go out along the *elevated* turret bore while the wheels stay
// under the hull's ground track.

import { MESH } from '../../render/mesh-table.js';
import { FORWARD, at } from '../helpers.js';
import { METAL, METAL_D, METAL_L, GLASS, HULK, SOOT, facColor, dim } from '../palette.js';

export function apc(b, e, ctx) {
  const fac = facColor(e);
  const yaw = e.yaw;
  const tYaw = e.turret;
  at(b, MESH.BOX, e, yaw, 0, 0, 1.35, 0, 3.4, 1.3, 3.4, METAL);
  at(b, MESH.BOX, e, yaw, 0, 0, 0.45, 0, 3.1, 0.5, 3.2, METAL_D);
  at(b, MESH.BOX, e, yaw, 0, 0, 1.7, -0.6, 2.6, 0.7, 2.6, fac);
  at(b, MESH.BOX, e, yaw, 0, 0, 1.55, 2.3, 1.4, 0.5, 2.2, GLASS);
  for (const s of [-1, 1]) {
    for (const f of [-1.15, 0, 1.15]) {
      at(b, MESH.SPHERE, e, yaw, 0, s * 1.75, 0.62, f, 1.25, 1.25, 1.25, [0.08, 0.08, 0.09]);
    }
  }
  // the turret's barrel and its flash go out along the *elevated* bore, not
  // along the hull's ground track (`at` lays an offset out in the yaw plane)
  const cp = Math.cos(e.pitch), sp = Math.sin(e.pitch);
  at(b, MESH.BOX, e, tYaw, e.pitch, 0, 2.85, 0, 2.1, 1.0, 2.3, METAL_L);
  at(b, MESH.CYL, e, tYaw, FORWARD + e.pitch, 0, 2.9 + 1.6 * sp, 1.6 * cp,
    0.26, 3.6, 0.26, [0.12, 0.12, 0.13]);
  const af = ctx.flashes && ctx.flashes.get(e.id);
  if (af) {
    at(b, MESH.SPHERE, e, tYaw, e.pitch, 0, 2.9 + 3.5 * sp, 3.5 * cp,
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
    at(b, MESH.BOX, e, yaw, 0.06, 0, 1.05, 0, 3.35, 0.9, 3.35, HULK);
    at(b, MESH.BOX, e, yaw, 0.06, 0, 0.45, 0, 3.1, 0.4, 3.1, SOOT);
    at(b, MESH.BOX, e, yaw, 0.06, 0, 1.7, -0.55, 2.4, 0.5, 2.4, fac);
  } else {                                // mech, collapsed on its plenums
    for (const side of [-1, 1]) {
      at(b, MESH.BOX, e, e.leg, 0, side * 1.32, 0.30, 0, 4.70, 0.46, 1.15, SOOT);
      at(b, MESH.BOX, e, e.leg, 0.35, side * 1.20, 0.78, 0.1, 1.0, 0.9, 0.75, HULK);
    }
    at(b, MESH.BOX, e, yaw, 0.12, 0, 1.20, -0.65, 4.05, 1.15, 1.60, HULK);
    at(b, MESH.BOX, e, yaw, 0.12, 0, 1.90, -0.85, 2.85, 0.50, 1.35, fac);
    at(b, MESH.NOSE, e, yaw, 0, 0.10, 1.55, 1.75, 2.10, 1.00, 1.25, HULK);
  }
}

export function base(b, e) {
  const fac = facColor(e);
  at(b, MESH.BOX, e, 0, 0, 0, 9, 0, 46, 18, 34, [0.34, 0.35, 0.38]);
  at(b, MESH.BOX, e, 0, 0, 0, 19, 0, 28, 4, 20, fac);
  at(b, MESH.CYL, e, 0, 0, 0, 26, 0, 6, 12, 6, [0.45, 0.46, 0.5]);
  at(b, MESH.CONE, e, 0, 0, 0, 35, 0, 10, 10, 10, fac);
  for (const [sx, sz] of [[-1, -1], [1, -1], [-1, 1], [1, 1]]) {
    at(b, MESH.CYL, e, 0, 0, sx * 20, 13, sz * 15, 7, 26, 7, [0.40, 0.41, 0.44]);
    at(b, MESH.SPHERE, e, 0, 0, sx * 20, 27, sz * 15, 8, 5, 8, fac);
  }
  at(b, MESH.CYL, e, 0, 0, 0, 34, 0, 0.9, 16, 0.9, [0.6, 0.6, 0.62]);
  at(b, MESH.BOX, e, 0, 0, 0, 41, 3, 0.4, 3, 7, fac);
  at(b, MESH.SPHERE, e, 0, 0, 0, 45, 0, 2.5, 2.5, 2.5, [1.0, 0.35, 0.3]);
}
