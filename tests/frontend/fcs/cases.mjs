// The cases, solved by the client's copy of the law, plus the one measurement both
// hosts make for themselves: does the elevation this solution asked for actually put
// the round on the target?
//
// Each host answers that with its *own* integrator (here `flightTo`, there
// `Ballistics.flight_at`), because a typo in one copy of the solver must not be
// hidden by the other copy agreeing with it.
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

import { solve } from '../../../frontend_v2/src/core/fcs.js';
import { TOL } from '../support/tol.mjs';

const here = dirname(fileURLToPath(import.meta.url));

/** The shared case table (`tests/cases/fcs.json`), as data. */
export function load() {
  const path = join(here, '..', '..', 'cases', 'fcs.json');
  return JSON.parse(readFileSync(path, 'utf8')).cases;
}

/** The `shot`/`track` pair of one case, with `accAge` applied the way a host must:
 * a stale pull is no pull. */
export function inputs(c) {
  const track = { ...c.track };
  if (track.accAge > 0.5) track.acc = [0, 0];
  return { shot: c.shot, track };
}

/** One case solved by `frontend/src/fcs.js`. */
export function run(c) {
  const { shot, track } = inputs(c);
  return solve(shot, track);
}

/** Where the target really is `t` seconds after the shot leaves: velocity all the
 * way, plus the measured pull for the part of the flight it is worth trusting. */
function target_at(c, t) {
  const { track } = inputs(c);
  const [tx, ty, tz] = track.pos;
  const [vx, vy, vz] = track.vel;
  const [ax, az] = track.acc;
  const ta = Math.min(t, 1.0);            // LEAD_ACCEL_S: only the first second
  return [tx + vx * t + 0.5 * ax * t * ta,
          ty + track.aim + vy * t,
          tz + vz * t + 0.5 * az * t * ta];
}

/** L1: fly the round this solution asked for and measure its closest approach to
 * where the target really is.  The leg is solved segment against segment, because a
 * 30 mm round covers 36 m in one tick and sampling tick boundaries would report a
 * clean miss on a clean hit. */
export function closest(c, sol) {
  const { shot } = inputs(c);
  const wd = shot.wd;
  const [mx, my, mz] = shot.muzzle;
  const cp = Math.cos(sol.elevation);
  let vx = cp * Math.cos(sol.yaw) * wd.vel;
  let vy = Math.sin(sol.elevation) * wd.vel;
  let vz = cp * Math.sin(sol.yaw) * wd.vel;
  let px = mx, py = my, pz = mz;
  let best = Infinity;
  const dt = 1 / 30;
  for (let t = 0; t < wd.life; t += dt) {
    if (wd.thrust) {
      const s = Math.hypot(vx, vy, vz);
      const k = Math.min(wd.maxvel, s + wd.thrust * dt) / Math.max(s, 1e-6);
      vx *= k; vy *= k; vz *= k;
    }
    vy -= wd.g * dt;
    const nx = px + vx * dt, ny = py + vy * dt, nz = pz + vz * dt;
    const a = target_at(c, t), b = target_at(c, t + dt);
    // the tick's relative motion: the round's leg minus the target's
    const r0x = px - a[0], r0y = py - a[1], r0z = pz - a[2];
    const wx = nx - px - (b[0] - a[0]);
    const wy = ny - py - (b[1] - a[1]);
    const wz = nz - pz - (b[2] - a[2]);
    const ww = wx * wx + wy * wy + wz * wz;
    const u = ww < 1e-9 ? 0
      : Math.max(0, Math.min(1, -(r0x * wx + r0y * wy + r0z * wz) / ww));
    const cx = r0x + wx * u, cy = r0y + wy * u, cz = r0z + wz * u;
    const d = Math.sqrt(cx * cx + cy * cy + cz * cz);
    if (d < best) best = d;
    if (ny < -200) break;
    px = nx; py = ny; pz = nz;
  }
  return best;
}

export { TOL };
