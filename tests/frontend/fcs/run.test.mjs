// The client's fire control, against the shared contract (`tests/cases/fcs.json`).
//
//   node --test tests/frontend/fcs/run.test.mjs
//   node --test --test-name-pattern ac30 tests/frontend/fcs/run.test.mjs
//
// L1 and L3 of the three assertions the plan describes (see `docs/plans/fcs_unify.md`
// §6.4): every case must solve to a shot that lands on the target, and every edge
// case must come back invalid.  L2 - the two hosts agreeing - is `tests/compare.mjs`,
// because it needs both of them to have run.
import test from 'node:test';
import assert from 'node:assert';

import { closest, inputs, load, run, TOL } from './cases.mjs';

const cases = load();

for (const c of cases) {
  test(c.id, () => {
    const { track } = inputs(c);
    const sol = run(c);

    if (c.tag) {
      // the edge cases: the solver must refuse, not produce a plausible number
      if (c.tag === 'stale_acc') {
        // a stale pull is no pull: the answer must equal the fresh-zero one
        const pair = cases.find((k) => k.id === c.pair);
        assert.ok(pair, `${c.id}: no pair case ${c.pair}`);
        const a = run(pair);
        assert.ok(sol.valid && a.valid, `${c.id}: expected both valid`);
        assert.ok(agrees(sol, a, c), `${c.id}: a stale pull changed the answer`);
        return;
      }
      assert.equal(sol.valid, false, `${c.id} (${c.tag}): expected an invalid solution`);
      return;
    }

    assert.ok(sol.valid, `${c.id}: no solution`);
    // the sight line is where the crew looks, so it must point at the point
    const [px, py, pz] = sol.point;
    const [ex, ey, ez] = c.shot.eye;
    const dh = Math.hypot(px - ex, pz - ez);
    assert.ok(Math.abs(Math.atan2(pz - ez, px - ex) - sol.yaw) < TOL.angle,
      `${c.id}: yaw is not the sight line through the point`);
    assert.ok(Math.abs(Math.atan2(py - ey, Math.max(dh, 1e-3)) - sol.pitch) < TOL.angle,
      `${c.id}: pitch is not the sight line through the point`);
    // L1: the elevation it asked for puts the round on the target
    const miss = closest(c, sol);
    assert.ok(miss <= TOL.miss(track.radius),
      `${c.id}: the round passes ${miss.toFixed(2)} m off (allowed `
      + `${TOL.miss(track.radius).toFixed(2)} m)`);
  });
}

function agrees(a, b, c) {
  const d = Math.hypot(a.point[0] - b.point[0], a.point[1] - b.point[1],
                       a.point[2] - b.point[2]);
  return d <= TOL.point(c.track.pos[0]) && Math.abs(a.tof - b.tof) <= TOL.tof;
}
