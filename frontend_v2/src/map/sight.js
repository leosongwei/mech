// The eye's rule: does anything cut the sight line a->b?  The mirror of
// `systems/sight.py`, and the one line of sight the game has: the field
// (ground, buildings, tree canopies - `sightAt`) and the deployed smoke puffs
// hide.  A trunk alone never does, and the leaves and the smoke stop the eye
// and nothing else.  The HUD asks it before it draws a pre-aim box, so a hull
// the seeker could not lock is a hull the pilot gets no marker on either.

import { TILE_BUILDING, inRoots } from './tiles.js';

// the obstacle column that cuts a *sight* line: a building up to its roof
// (mirrors Terrain.sight_wall).  The leaves of a tree are not a column - they
// are a volume of their own, up 10-18 m and a few metres across, so they are
// solved per tree in `canopiesAt`; a whole tile of them would hide a hull
// standing in the open beside the stand.
export function sightWall(map, x, z) {
  const i = Math.floor(x / map.tile), j = Math.floor(z / map.tile);
  if (i < 0 || j < 0 || i >= map.w || j >= map.h) return 1e6;
  const k = j * map.w + i;
  if (map.tiles[k] === TILE_BUILDING) return map.walls[k] || 8.0;
  return 0;
}

export function sightAt(map, x, z) {
  return map.heightAt(x, z) + sightWall(map, x, z);
}

// where a segment enters a canopy, in the segment's own t, or null (mirrors
// terrain._canopy_hit: solved, never sampled - a canopy is a few metres
// across, so an 8 m walk steps straight over its edge)
export function canopyHit(c, x0, y0, z0, dx, dy, dz, ta, tb) {
  if (c.ball) {
    const ax = (x0 - c.x) / c.rx, ay = (y0 - c.y) / c.ry, az = (z0 - c.z) / c.rz;
    const ex = dx / c.rx, ey = dy / c.ry, ez = dz / c.rz;
    const qa = ex * ex + ey * ey + ez * ez;
    if (qa < 1e-12) return null;
    const qb = 2 * (ax * ex + ay * ey + az * ez);
    const qc = ax * ax + ay * ay + az * az - 1;
    const iv = inRoots(qa, qb, qc);
    if (!iv) return null;
    const lo = Math.max(iv[0], ta);
    return lo <= tb && lo <= iv[1] ? lo : null;
  }
  const u = (c.rad / (c.y1 - c.y0)) ** 2;
  const ax = x0 - c.x, az = z0 - c.z;
  const hh = c.y1 - y0;
  const qa = dx * dx + dz * dz - u * dy * dy;
  const qb = 2 * (ax * dx + az * dz) + 2 * u * hh * dy;
  const qc = ax * ax + az * az - u * hh * hh;
  const iv = inRoots(qa, qb, qc);
  if (!iv) return null;
  let lo = Math.max(iv[0], ta), hi = Math.min(iv[1], tb);
  if (dy > 1e-9) {
    lo = Math.max(lo, (c.y0 - y0) / dy); hi = Math.min(hi, (c.y1 - y0) / dy);
  } else if (dy < -1e-9) {
    lo = Math.max(lo, (c.y1 - y0) / dy); hi = Math.min(hi, (c.y0 - y0) / dy);
  } else if (y0 < c.y0 || y0 > c.y1) return null;
  return lo > tb || lo > hi ? null : lo;
}

// walk the segment tile by tile (a 2D DDA, like the server's _spans) and solve
// the canopies of every tile it crosses
export function canopyBlocked(map, x0, y0, z0, dx, dy, dz) {
  const T = map.tile;
  let i = x0 >= 0 ? Math.floor(x0 / T) : -1;
  let j = z0 >= 0 ? Math.floor(z0 / T) : -1;
  const sx = dx > 0 ? 1 : -1, sz = dz > 0 ? 1 : -1;
  let tx = dx ? ((i + (dx > 0 ? 1 : 0)) * T - x0) / dx : 2;
  let tz = dz ? ((j + (dz > 0 ? 1 : 0)) * T - z0) / dz : 2;
  let t = 0;
  for (let guard = 0; guard < 8192; guard++) {
    const tn = Math.min(tx, tz, 1);
    if (i >= 0 && j >= 0 && i < map.w && j < map.h) {
      for (const c of map.canopiesAt(i, j)) {
        if (canopyHit(c, x0, y0, z0, dx, dy, dz, t, tn) !== null) return true;
      }
    }
    if (tn >= 1) return false;
    t = tn;
    if (tx <= tz) {
      i += sx;
      tx = dx ? ((i + (dx > 0 ? 1 : 0)) * T - x0) / dx : 2;
    } else {
      j += sz;
      tz = dz ? ((j + (dz > 0 ? 1 : 0)) * T - z0) / dz : 2;
    }
  }
  return false;
}

// the ellipsoid test of systems/smoke.py: scale the segment into the unit
// sphere of the puff and take the closest point
export function puffBlocks(p, ax, ay, az, bx, by, bz) {
  const rx = p.rx, ry = p.ry, rz = p.rz;
  if (!(rx > 0 && ry > 0 && rz > 0)) return false;
  const pax = (ax - p.x) / rx, pay = (ay - p.y) / ry, paz = (az - p.z) / rz;
  const dx = (bx - p.x) / rx - pax, dy = (by - p.y) / ry - pay, dz = (bz - p.z) / rz - paz;
  const L = dx * dx + dy * dy + dz * dz;
  let u = L < 1e-9 ? 0 : -(pax * dx + pay * dy + paz * dz) / L;
  u = u < 0 ? 0 : (u > 1 ? 1 : u);
  const qx = pax + dx * u, qy = pay + dy * u, qz = paz + dz * u;
  return qx * qx + qy * qy + qz * qz <= 1;
}

// `puffs` is the deployed smoke (`net.smoke` without the canisters still in
// the air); omit it and only the field is tested
export function sightBlocked(map, x0, y0, z0, x1, y1, z1, puffs, step) {
  step = step || 8;                 // half a tile, like the server's march
  const dx = x1 - x0, dy = y1 - y0, dz = z1 - z0;
  const dist = Math.hypot(dx, dy, dz);
  if (dist < 1e-4) return y0 <= sightAt(map, x0, z0);
  if (y0 <= sightAt(map, x0, z0) + 1.0) return true;   // the eye is in the ground
  const n = Math.max(1, Math.ceil(dist / step));
  for (let k = 1; k <= n; k++) {
    const t = k / n;
    if (y0 + dy * t <= sightAt(map, x0 + dx * t, z0 + dz * t)) return true;
  }
  if (canopyBlocked(map, x0, y0, z0, dx, dy, dz)) return true;
  if (puffs) {
    for (const p of puffs) if (puffBlocks(p, x0, y0, z0, x1, y1, z1)) return true;
  }
  return false;
}
