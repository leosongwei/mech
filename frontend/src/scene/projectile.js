// A round in flight: a tracer as one unlit pyramid stretched along its own
// path, everything guided as a body and a nose with the motor's flame behind.
//
// The pieces are laid out along the round's *flight* path (`pitch`), never its
// ground track: `at` only lays an offset out in the yaw plane, so a climbing
// rocket would keep its nose and its flame in the air where a level one has
// them and the pieces would come apart.

import { MESH } from '../render/mesh-table.js';
import { FORWARD, at } from './helpers.js';

export function projectile(b, p, ctx) {
  const defs = ctx.defs;
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
    at(b, MESH.PYRAMID, fake, yaw, FORWARD + pitch + Math.PI, 0, 0, 0, wid, len, wid, col);
  } else {
    // Every piece sits `d` metres along the round's own flight path, not along
    // its ground track: `at` only lays an offset out in the yaw plane, so a
    // rocket climbing away would keep its nose, body and flame in the air
    // where a level one would have them - the pieces come apart.
    const put = (mesh, d, sx, sy, sz, colour) => at(b, mesh, fake, yaw,
      FORWARD + pitch, 0, d * Math.sin(pitch), d * Math.cos(pitch), sx, sy, sz, colour);
    // The round is drawn at its own size when the table says it (`body`, the
    // (across, long) pair - a missile has to be the size of the mouth it leaves
    // from), and at the shared big body everything else flies as otherwise:
    // large enough to read at 2 km, which is what a tracer's shape is for.
    const size = (wd && wd.body) || [0.32, 3.2];
    const bd = size[0], bl = size[1];
    put(MESH.CYL, bl * -0.19, bd, bl, bd, [0.78, 0.78, 0.8]);
    put(MESH.CONE, bl * 0.44, bd, bl * 0.34, bd, col);
    put(MESH.SPHERE, bl * -0.75, bd * 3.1, bd * 3.1, bd * 3.1, [1.0, 0.72, 0.25]);
  }
}
