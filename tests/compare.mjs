// L2: the two hosts must be the same function.
//
//   node tests/compare.mjs /tmp/mech-fcs-cs.json /tmp/mech-fcs-js.json
//
// Both files are the solutions table each runner wrote (`--json`, and
// `tests/frontend/fcs/emit.mjs`).  Every field the solution is made of is compared
// with the tolerances in `tests/frontend/support/tol.mjs`, case by case; a case one
// host refused and the other did not is a failure on its own, because the *refusals*
// are the part that drifts first.
import { readFileSync } from 'node:fs';
import { TOL } from './frontend/support/tol.mjs';

const [aPath, bPath] = process.argv.slice(2);
if (!aPath || !bPath) {
  console.error('usage: node tests/compare.mjs <cs.json> <js.json>');
  process.exit(2);
}

const load = (p) => new Map(JSON.parse(readFileSync(p, 'utf8')).map((r) => [r.id, r]));
const cs = load(aPath);
const js = load(bPath);

let bad = 0, checked = 0;
const worst = { point: 0, angle: 0, tof: 0 };
const fail = (id, what) => { bad++; console.log(`FAIL ${id}: ${what}`); };

for (const [id, c] of cs) {
  const j = js.get(id);
  if (!j) { fail(id, 'missing from the front end\'s table'); continue; }
  if (c.valid !== j.valid) { fail(id, `valid differs (cs ${c.valid}, js ${j.valid})`); continue; }
  if (!c.valid) { checked++; continue; }
  const dp = Math.hypot(c.point[0] - j.point[0], c.point[1] - j.point[1],
                        c.point[2] - j.point[2]);
  worst.point = Math.max(worst.point, dp);
  const dist = Math.hypot(c.point[0], c.point[2]);
  const tol = TOL.point(dist);
  if (dp > tol) fail(id, `point off ${dp.toFixed(3)} m (tol ${tol.toFixed(2)})`);
  for (const k of ['yaw', 'pitch', 'elevation']) {
    const dk = Math.abs(c[k] - j[k]);
    worst.angle = Math.max(worst.angle, dk);
    if (dk > TOL.angle) fail(id, `${k} off ${dk.toExponential(2)} rad`);
  }
  const dt = Math.abs(c.tof - j.tof);
  worst.tof = Math.max(worst.tof, dt);
  if (dt > TOL.tof) fail(id, `tof off ${dt.toFixed(4)} s`);
  checked++;
}
for (const id of js.keys()) if (!cs.has(id)) fail(id, 'missing from the server\'s table');

// the worst difference seen, next to its tolerance: the number that says whether the
// two copies are the same law or merely close enough today
console.log(`\n${checked} cases compared, ${bad} differ`
  + `\n  worst point ${worst.point.toExponential(2)} m (tol 0.25 m)`
  + `\n  worst angle ${worst.angle.toExponential(2)} rad (tol 1e-3)`
  + `\n  worst tof   ${worst.tof.toExponential(2)} s (tol 0.02)`);
process.exit(bad === 0 ? 0 : 1);
