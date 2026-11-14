// Builds the per-frame instance list from abstract primitives: a mech is boxes
// + cylinders + a sphere cockpit, an infantryman is a sphere head on a cone
// body with a cylinder rifle.  One file per kind of thing; this one is the
// emitter the world pass calls.
//
// It used to be `drawUnits`, which opened the dynamic pass itself
// (`R.beginDynamic()`) before it walked the entity table - the batch now
// arrives empty, so the frame owns that call and the models own only their
// pieces.  The dispatch table is that `switch (e.type)` and nothing else: the
// type numbers are the server's (`Core/Defs.cs`), and each one names the model
// that owns it.
//
// `ctx` = { defs, flashes, ownId, now }: the weapon table, the muzzle flashes
// keyed by entity id, the pilot's own hull (drawn without its sight head - the
// eye is inside it) and the frame's clock in ms.

import { apc, base, wreck } from './units/vehicle.js';
import { mech } from './units/mech.js';
import { infantry, civilian, corpse, bunker } from './units/infantry.js';
import { projectile } from './projectile.js';

// Types 1 and 2 are one model: the same man carrying a rocket instead of a
// rifle, so they are two entries of `infantry` and the flag is all that differs.
const EMIT = {
  1: (b, e) => infantry(b, e, false),
  2: (b, e) => infantry(b, e, true),
  3: apc,
  4: mech,
  5: base,
  6: civilian,
  7: wreck,
  8: corpse,
  9: bunker,
};

export function emitUnits(b, entities, ctx) {
  for (const e of entities.values()) {
    const f = EMIT[e.type];
    if (f) f(b, e, ctx);
  }
}

export function emitProjectiles(b, projectiles, ctx) {
  for (const p of projectiles) projectile(b, p, ctx);
}

export { emitSmoke } from './smoke.js';
export { FACTION_COLORS } from './palette.js';
