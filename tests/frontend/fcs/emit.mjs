// The front end's solutions table, in the shape `tests/compare.mjs` reads.
//
//   node tests/frontend/fcs/emit.mjs /tmp/mech-fcs-js.json
//
// The assertions live in `run.test.mjs` (`node --test`); this is the data half, kept
// out of the test run so `node --test` stays a pass/fail thing.
import { writeFileSync } from 'node:fs';

import { load, run } from './cases.mjs';

const out = process.argv[2];
if (!out) {
  console.error('usage: node tests/frontend/fcs/emit.mjs <out.json>');
  process.exit(2);
}

const rows = load().map((c) => {
  const s = run(c);
  if (!s.valid) return { id: c.id, valid: false };
  return { id: c.id, valid: true, point: s.point, yaw: s.yaw, pitch: s.pitch,
           elevation: s.elevation, tof: s.tof };
});
writeFileSync(out, JSON.stringify(rows, null, 1) + '\n');
console.log(`solutions written to ${out}`);
