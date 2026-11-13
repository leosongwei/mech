// Canisters on their way out, then the puffs they become.  The puffs are drawn
// opaque in the solid pass: a smoke screen is meant to hide everything behind
// it, and skipping the blend makes a dozen of them almost free.
//
// The colours and the end-shrink are the client's own (`core/config.js`), not
// the wire's: the server sends a puff's centre and its three radii, and this is
// the only place that decides what a screen looks like.

import { MESH } from '../render/mesh-table.js';
import { SMOKE_RGB, SMOKE_END_SHRINK } from '../core/config.js';

export function emitSmoke(b, list) {
  if (!list || !list.length) return;
  for (const p of list) {
    if (p.canister) {
      b.put(MESH.SPHERE, p.x, p.y, p.z, 0, 0, 1.5, 1.5, 1.5, 0.30, 0.30, 0.29, 1);
      continue;
    }
    // the last fraction of a second is spent shrinking away instead of
    // fading out, the only exit an opaque puff can afford
    const shrink = Math.min(1, p.t / SMOKE_END_SHRINK);
    b.put(MESH.SMOKE, p.x, p.y, p.z, 0, 0, p.rx * shrink, p.ry * shrink, p.rz * shrink,
      SMOKE_RGB[0], SMOKE_RGB[1], SMOKE_RGB[2], 1);
  }
}
