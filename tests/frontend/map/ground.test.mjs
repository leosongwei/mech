// Where the centre cross meets the ground, client side: the point a loitering round
// circles when its shot carries no lock (`map/solid.js::groundPoint`, sent as
// `in.dg`).  A flat field of known height answers the arithmetic exactly, and the
// three ways a cross names nothing - in the sky, off the field, past the reach -
// answer null, because the server's own fallback takes over there.
import { test } from 'node:test';
import assert from 'node:assert';

import { MapData } from '../../../frontend/src/map/index.js';
import { groundPoint } from '../../../frontend/src/map/solid.js';

const TILE = 16;
const W = 100;                    // 1600 m of field, which is inside every walk below
const H = 100;
const GROUND = 100;               // the flat plane every case starts from

function flat(h = GROUND) {
  const heights = new Float32Array(W * H).fill(h);
  const zeros = new Uint8Array(W * H);
  return new MapData(W, H, TILE, heights, zeros, zeros, zeros);
}

const reach = 3000;

test('a cross below the horizon lands where the geometry says', () => {
  const map = flat();
  const eye = [800, GROUND + 5.05, 800];
  // looking due east, 0.1 rad down: the eye is 5.05 m up, so the ground is
  // 5.05 / tan(0.1) = 50.3 m out
  const p = groundPoint(map, eye, 0, -0.1, reach);
  assert.ok(p, 'the walk found nothing on a flat plane');
  const want = 5.05 / Math.tan(0.1);
  assert.ok(Math.abs(p[0] - (800 + want)) <= 0.2, `x ${p[0]} vs ${800 + want}`);
  assert.ok(Math.abs(p[1] - 800) <= 0.2, `z ${p[1]} vs 800`);
});

test('a cross in the sky names no point', () => {
  const map = flat();
  assert.equal(groundPoint(map, [800, GROUND + 5.05, 800], 0, 0.5, reach), null);
  // level is still sky: the eye never descends to the plane it sits above
  assert.equal(groundPoint(map, [800, GROUND + 5.05, 800], 0, 0, reach), null);
});

test('a cross that leaves the field names no point', () => {
  const map = flat();
  // 20 m from the west edge, looking west: the ray is out of the field inside 50 m
  assert.equal(groundPoint(map, [20, GROUND + 5.05, 800], Math.PI, -0.1, reach), null);
});

test('the walk stops at the reach, and the flat answer past it is the fallback', () => {
  const map = flat();
  const eye = [50, GROUND + 5.05, 800];
  // 1 km out: inside a 1.5 km walk, past a 500 m one
  const pitch = -Math.atan(5.05 / 1000);
  assert.ok(groundPoint(map, eye, 0, pitch, 1500), 'a 1 km point is inside a 1.5 km walk');
  assert.equal(groundPoint(map, eye, 0, pitch, 500), null);
});

test('a ridge is the point, not the plain behind it', () => {
  const map = flat();
  // a wall of ground 30 m high, one tile wide, 200 m east of the eye; the ray is
  // level enough to still be above the plain when it reaches the rise
  const crest = Math.floor((50 + 200) / TILE) * TILE;
  const i = crest / TILE;
  for (let j = 0; j < H; j++) map.heights[j * W + i] = GROUND + 30;
  const p = groundPoint(map, [50, GROUND + 5.05, 800], 0, -0.01, reach);
  assert.ok(p, 'the walk missed the ridge');
  // the facet rises over the tile before the crest, and the plain behind it is
  // 500 m out: the answer has to be on that facet
  assert.ok(p[0] >= crest - TILE && p[0] <= crest,
    `the point is on the rise: ${p[0]} against ${crest - TILE}..${crest}`);
});
