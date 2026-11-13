// The ground's contract, client side: the same `tests/cases/height.json` the
// server answers (`tests/backend/Height/HeightTests.cs`), read through
// `MapData.heightAt`.  The cross, the impact marker and the ballistic
// prediction all solve through this function, so it has to be the same surface
// the server bursts a round on - and the same two triangles the renderer draws.
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';
import { test } from 'node:test';
import assert from 'node:assert';

import { MapData } from '../../../frontend/src/map.js';

const here = dirname(fileURLToPath(import.meta.url));
const table = JSON.parse(readFileSync(join(here, '..', '..', 'cases', 'height.json'), 'utf8'));

const heights = new Float32Array(table.heights.length * table.heights[0].length);
{
  let n = 0;
  for (const row of table.heights) for (const v of row) heights[n++] = v;
}

const ground = new MapData(
  table.w, table.h, table.tile, heights,
  new Uint8Array(table.w * table.h), new Uint8Array(table.w * table.h),
  new Uint8Array(table.w * table.h));

test('every point answers the height the table wrote', () => {
  for (const c of table.cases) {
    const got = ground.heightAt(c.x, c.z);
    assert.ok(Math.abs(got - c.h) <= table.tolerance,
      `${c.id}: ${got} vs ${c.h} (${c.why})`);
  }
});

test('the facet is a plane and it is not the bilinear blend', () => {
  // tile (0,0): a = 0 (i,j), b = 10 (i+1,j), c = 20 (i,j+1), d = 40
  // a plane is affine, so the middle of two points on it is the mean
  const left = ground.heightAt(2, 4), right = ground.heightAt(6, 4);
  assert.ok(Math.abs(ground.heightAt(4, 4) - (left + right) / 2) <= 1e-4,
    'triangle {a,b,c} is not affine in (x, z)');
  const bilinear = (0 * 0.25 + 10 * 0.25) * 0.5 + (20 * 0.25 + 40 * 0.25) * 0.5;
  assert.ok(Math.abs(ground.heightAt(4, 4) - bilinear) > 1e-3,
    'triangle {a,b,c} answers the bilinear blend: the facet rule is not in place');
});
