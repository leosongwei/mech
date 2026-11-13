// The round's rule: what stops a projectile.  A building is a column from the
// ground to the roof the server drew its walls with, a tree is nothing but its
// trunk, and the ground between is the continuous field of `field.js`.  Mirrors
// `Terrain.wall_at` / `_trunk_hit` / `ground_hit` so the predicted impact is the
// real one - the leaves and the smoke never stop a round.

import { TILE_BUILDING, TILE_TREE, TILE_FOREST } from './tiles.js';

// the obstacle column standing on the ground: a building up to the roof, a tree
// as nothing but its trunk
export function wallAt(map, x, z) {
  const i = Math.floor(x / map.tile), j = Math.floor(z / map.tile);
  if (i < 0 || j < 0 || i >= map.w || j >= map.h) return 1e6;
  const k = j * map.w + i;
  const t = map.tiles[k];
  if (t === TILE_BUILDING) return map.walls[k] || 8.0;
  if (t === TILE_TREE || t === TILE_FOREST) {
    for (const tr of map.trunksAt(i, j)) {
      const dx = x - tr.x, dz = z - tr.z;
      if (dx * dx + dz * dz <= tr.r * tr.r) return tr.h;
    }
  }
  return 0;
}

export function obstacleAt(map, x, z) {
  return map.heightAt(x, z) + wallAt(map, x, z);
}

// Where a segment first enters a tree trunk, or null.  A trunk is about a
// metre wide - far thinner than the 8 m the ground walk samples at - so it is
// solved as a cylinder instead of sampled, exactly as the server does
// (Terrain._trunk_hit); otherwise a predicted round flies through the tree
// the real one bursts in.
export function trunkHit(map, x0, y0, z0, x1, y1, z1) {
  const T = map.tile;
  const i0 = Math.floor(Math.min(x0, x1) / T), i1 = Math.floor(Math.max(x0, x1) / T);
  const j0 = Math.floor(Math.min(z0, z1) / T), j1 = Math.floor(Math.max(z0, z1) / T);
  const dx = x1 - x0, dy = y1 - y0, dz = z1 - z0;
  const a = dx * dx + dz * dz;
  if (a < 1e-9) return null;
  let best = null;
  for (let j = Math.max(j0, 0); j <= Math.min(j1, map.h - 1); j++) {
    for (let i = Math.max(i0, 0); i <= Math.min(i1, map.w - 1); i++) {
      for (const tr of map.trunksAt(i, j)) {
        const ex = x0 - tr.x, ez = z0 - tr.z;
        const b = 2 * (ex * dx + ez * dz);
        const c = ex * ex + ez * ez - tr.r * tr.r;
        const disc = b * b - 4 * a * c;
        if (disc < 0) continue;
        const sq = Math.sqrt(disc);
        // entry first, then the far root when the segment starts inside
        for (const t of [(-b - sq) / (2 * a), (-b + sq) / (2 * a)]) {
          if (t < 0 || t > 1) continue;
          if (y0 + dy * t <= map.heightAt(tr.x, tr.z) + tr.h && (best === null || t < best)) best = t;
          break;
        }
      }
    }
  }
  return best;
}

// march a segment and report the first ground contact
export function groundHit(map, x0, y0, z0, x1, y1, z1, step) {
  step = step || 12;
  const dx = x1 - x0, dy = y1 - y0, dz = z1 - z0;
  const dist = Math.hypot(dx, dy, dz);
  if (dist < 1e-4) return { hit: y0 <= obstacleAt(map, x0, z0), t: 0, x: x0, y: y0, z: z0 };
  const trunkT = trunkHit(map, x0, y0, z0, x1, y1, z1);
  const n = Math.max(1, Math.ceil(dist / step));
  let prev = 0;
  for (let k = 1; k <= n; k++) {
    const t = k / n;
    if (trunkT !== null && trunkT <= t) {
      return { hit: true, t: trunkT, x: x0 + dx * trunkT, y: y0 + dy * trunkT, z: z0 + dz * trunkT };
    }
    const x = x0 + dx * t, y = y0 + dy * t, z = z0 + dz * t;
    const ground = obstacleAt(map, x, z);
    if (y <= ground) {
      let a = prev, b = t;
      for (let it = 0; it < 4; it++) {
        const m = (a + b) * 0.5;
        if (y0 + dy * m > obstacleAt(map, x0 + dx * m, z0 + dz * m)) a = m; else b = m;
      }
      const tm = (a + b) * 0.5;
      return { hit: true, t: tm, x: x0 + dx * tm, y: y0 + dy * tm, z: z0 + dz * tm };
    }
    prev = t;
  }
  return { hit: false, t: 1, x: x1, y: y1, z: z1 };
}
