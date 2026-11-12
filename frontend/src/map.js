// Client copy of the terrain + the same ballistics the server runs, so the
// reticle impact marker agrees with the simulation.

// The fire control lives in `fcs.js` (a pure function of numbers, shared with the
// server's `Core/FireControl.cs`); the doors are re-exported here because the HUD
// and the tests have always asked this module for them.
export { flightTo, solveElevation, zeroAt, leadPoint, solve } from './fcs.js';

const TILE_GRASS = 0, TILE_ROAD = 1, TILE_BUILDING = 2, TILE_TREE = 3, TILE_ROCK = 4, TILE_BASE = 5, TILE_SOLID = 6,
  TILE_FOREST = 7;

// road pieces, mirrors terrain.py (an OpenTTD road tile is a set of halves;
// the two diagonal bits are the corner-to-corner piece we added on top)
export const ROAD_N = 1;
export const ROAD_E = 2;
export const ROAD_S = 4;
export const ROAD_W = 8;
export const ROAD_D_WS = 16;    // west edge middle -> south edge middle
export const ROAD_D_NE = 32;    // north edge middle -> east edge middle
export const ROAD_STRAIGHT = ROAD_N | ROAD_E | ROAD_S | ROAD_W;
export const ROAD_DIAG = ROAD_D_WS | ROAD_D_NE;

const TRUNK_R = 0.5;            // trunk radius, must match terrain.py

// The hash the renderer plants its trees with - the server collides against
// exactly the trunks it produces (`terrain.py`, `_tree_hash`).
export function treeHash(a, b) {
  const s = Math.sin(a * 12.9898 + b * 78.233) * 43758.5453;
  return s - Math.floor(s);
}

// Where a tree tile's trunks stand: `{x, z, r, h}` each, 1-2 trees on a tree
// tile and 3-5 on a forest tile, plus the three hash draws (`q1`-`q3`) the
// renderer gives each tree its height, canopy and colour from.  The canopy
// above the trunks is not a trunk: `canopyOf` builds it, and it hides what is
// behind it without stopping anything that flies.
export function treeTrunks(i, j, t, tile) {
  const r1 = treeHash(i, j);
  const n = t === 7 ? 3 + Math.floor(r1 * 3) : 1 + Math.floor(r1 * 2);
  const cx = (i + 0.5) * tile, cz = (j + 0.5) * tile;
  const out = [];
  for (let q = 0; q < n; q++) {
    const q1 = treeHash(i + q * 5.3, j + q * 1.9);
    const q2 = treeHash(j + q * 3.7, i + q * 7.1);
    const q3 = treeHash(i * 1.3 + q, j * 2.7 - q);
    out.push({
      x: cx + (q1 - 0.5) * tile * 0.66,      // spread over the tile, never
      z: cz + (q2 - 0.5) * tile * 0.66,      // touching its border
      r: TRUNK_R * (0.7 + q3 * 0.5),
      h: (10.0 + q3 * 8.0) * 0.40,           // the trunk column, canopy excluded
      q1, q2, q3,
    });
  }
  return out;
}

// The volume one tree hides behind, from the same draws the renderer builds
// the mesh from: a broadleaf (q3 > 0.45) is a ball on the trunk, a conifer a
// cone standing on it.  Mirrors terrain._canopy - what hides a hull is what
// the eye can see, not a slab over the whole tile.
export function canopyOf(map, tr) {
  const ground = map.heightAt(tr.x, tr.z);
  const tall = 10.0 + tr.q3 * 8.0;
  if (tr.q3 > 0.45) {
    const rad = tall * 0.40 * (0.85 + tr.q1 * 0.3);
    return { ball: true, x: tr.x, z: tr.z, y: ground + tall * 0.60,
      rx: rad, ry: tall * 0.46, rz: rad, rad };
  }
  const rad = tall * 0.26 * (0.85 + tr.q1 * 0.3);
  return { ball: false, x: tr.x, z: tr.z, y0: ground + tall * 0.15,
    y1: ground + tall * 0.95, rad };
}

// the interval [lo, hi] where a*t^2 + b*t + c <= 0, or null
function inRoots(a, b, c) {
  if (Math.abs(a) < 1e-12) {
    if (Math.abs(b) < 1e-12) return c <= 0 ? [0, 1] : null;
    const t = -c / b;
    return b > 0 ? [-1e9, t] : [t, 1e9];
  }
  const disc = b * b - 4 * a * c;
  if (disc < 0) return null;
  const sq = Math.sqrt(disc);
  const r0 = (-b - sq) / (2 * a), r1 = (-b + sq) / (2 * a);
  return r0 <= r1 ? [r0, r1] : [r1, r0];
}

// where a segment enters a canopy, in the segment's own t, or null (mirrors
// terrain._canopy_hit: solved, never sampled - a canopy is a few metres
// across, so an 8 m walk steps straight over its edge)
function canopyHit(c, x0, y0, z0, dx, dy, dz, ta, tb) {
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
function canopyBlocked(map, x0, y0, z0, dx, dy, dz) {
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

function b64ToBytes(b64) {
  const bin = atob(b64);
  const out = new Uint8Array(bin.length);
  for (let i = 0; i < bin.length; i++) out[i] = bin.charCodeAt(i);
  return out;
}

async function inflate(bytes) {
  if (typeof DecompressionStream === 'undefined') throw new Error('browser lacks DecompressionStream');
  const ds = new DecompressionStream('deflate');
  const stream = new Blob([bytes]).stream().pipeThrough(ds);
  return new Uint8Array(await new Response(stream).arrayBuffer());
}

export class MapData {
  constructor(w, h, tile, heights, tiles, roads, walls) {
    this.w = w;
    this.h = h;
    this.tile = tile;
    this.heights = heights;      // Float32Array(w*h), row major [j*w+i]
    this.tiles = tiles;          // Uint8Array(w*h)
    this.roads = roads;          // Uint8Array(w*h), ROAD_* piece bits
    this.walls = walls;          // Uint8Array(w*h), wall/roof height in metres
    this.trunkCache = new Map(); // tile index -> trunk layout, filled on demand
    this.canopyCache = new Map();   // tile index -> canopies reaching into it
  }

  static async load(info, lanes) {
    const hBytes = await inflate(b64ToBytes(info.heights));
    const u16 = new Uint16Array(hBytes.buffer, hBytes.byteOffset, hBytes.byteLength >> 1);
    // heights travel as u16 `hunit` steps (`Netcode.H_UNIT`), so the plane is
    // half a metre wide per vertex and the ground between vertices is a slope
    const unit = info.hunit || 0.05;
    const heights = new Float32Array(u16.length);
    for (let i = 0; i < u16.length; i++) heights[i] = u16[i] * unit;
    const tiles = await inflate(b64ToBytes(info.tiles));
    const roads = info.roads ? await inflate(b64ToBytes(info.roads)) : new Uint8Array(tiles.length);
    const walls = info.walls ? await inflate(b64ToBytes(info.walls)) : new Uint8Array(tiles.length);
    const map = new MapData(info.w, info.h, info.tile, heights, tiles, roads, walls);
    map.lanes = lanes || [];     // road centrelines, in world metres
    return map;
  }

  // The ground under a point: the one triangle of the tile the point falls in,
  // split along the north-east to south-west diagonal.  Mirrors
  // `Terrain.height_at` line for line, and it is the same two triangles
  // `renderer._buildTilePatch` draws - so a reticle, a predicted impact point
  // and the facet under a hull are one surface.
  heightAt(x, z) {
    const fx = Math.min(Math.max(x / this.tile, 0), this.w - 1.001);
    const fz = Math.min(Math.max(z / this.tile, 0), this.h - 1.001);
    const i = fx | 0, j = fz | 0;
    const tx = fx - i, tz = fz - j;
    const h = this.heights;
    const a = h[j * this.w + i], b = h[j * this.w + i + 1];
    const c = h[(j + 1) * this.w + i], d = h[(j + 1) * this.w + i + 1];
    if (tx + tz <= 1) return a + (b - a) * tx + (c - a) * tz;
    return d + (c - d) * (1 - tx) + (b - d) * (1 - tz);
  }

  tileAt(x, z) {
    const i = x / this.tile | 0, j = z / this.tile | 0;
    if (i < 0 || j < 0 || i >= this.w || j >= this.h) return TILE_ROCK;
    return this.tiles[j * this.w + i];
  }

  blocked(x, z) {
    const t = this.tileAt(x, z);
    return t === TILE_BUILDING || t === TILE_ROCK || t === TILE_SOLID;
  }

  // mirrors Terrain.inside on the server: past the map edge there is no
  // ground, heightAt just keeps handing back the border height
  inside(x, z) {
    return x >= 0 && z >= 0 && x <= (this.w - 1) * this.tile && z <= (this.h - 1) * this.tile;
  }

  // the obstacle column standing on the ground: a building up to the roof the
  // server drew its walls with, a tree as nothing but its trunk - mirrors
  // Terrain.wall_at so the predicted impact is the real one
  wallAt(x, z) {
    const i = Math.floor(x / this.tile), j = Math.floor(z / this.tile);
    if (i < 0 || j < 0 || i >= this.w || j >= this.h) return 1e6;
    const k = j * this.w + i;
    const t = this.tiles[k];
    if (t === TILE_BUILDING) return this.walls[k] || 8.0;
    if (t === TILE_TREE || t === TILE_FOREST) {
      for (const tr of this.trunksAt(i, j)) {
        const dx = x - tr.x, dz = z - tr.z;
        if (dx * dx + dz * dz <= tr.r * tr.r) return tr.h;
      }
    }
    return 0;
  }

  // the trunk layout of a tile, laid out once per tile the eye ever asks about
  trunksAt(i, j) {
    const k = j * this.w + i;
    let tr = this.trunkCache.get(k);
    if (tr === undefined) {
      const t = this.tiles[k];
      tr = t === TILE_TREE || t === TILE_FOREST ? treeTrunks(i, j, t, this.tile) : [];
      this.trunkCache.set(k, tr);
    }
    return tr;
  }

  // Where a segment first enters a tree trunk, or null.  A trunk is about a
  // metre wide - far thinner than the 8 m the ground walk samples at - so it is
  // solved as a cylinder instead of sampled, exactly as the server does
  // (Terrain._trunk_hit); otherwise a predicted round flies through the tree
  // the real one bursts in.
  trunkHit(x0, y0, z0, x1, y1, z1) {
    const T = this.tile;
    const i0 = Math.floor(Math.min(x0, x1) / T), i1 = Math.floor(Math.max(x0, x1) / T);
    const j0 = Math.floor(Math.min(z0, z1) / T), j1 = Math.floor(Math.max(z0, z1) / T);
    const dx = x1 - x0, dy = y1 - y0, dz = z1 - z0;
    const a = dx * dx + dz * dz;
    if (a < 1e-9) return null;
    let best = null;
    for (let j = Math.max(j0, 0); j <= Math.min(j1, this.h - 1); j++) {
      for (let i = Math.max(i0, 0); i <= Math.min(i1, this.w - 1); i++) {
        for (const tr of this.trunksAt(i, j)) {
          const ex = x0 - tr.x, ez = z0 - tr.z;
          const b = 2 * (ex * dx + ez * dz);
          const c = ex * ex + ez * ez - tr.r * tr.r;
          const disc = b * b - 4 * a * c;
          if (disc < 0) continue;
          const sq = Math.sqrt(disc);
          // entry first, then the far root when the segment starts inside
          for (const t of [(-b - sq) / (2 * a), (-b + sq) / (2 * a)]) {
            if (t < 0 || t > 1) continue;
            if (y0 + dy * t <= this.heightAt(tr.x, tr.z) + tr.h && (best === null || t < best)) best = t;
            break;
          }
        }
      }
    }
    return best;
  }

  obstacleAt(x, z) {
    return this.heightAt(x, z) + this.wallAt(x, z);
  }

  // the obstacle column that cuts a *sight* line: a building up to its roof
  // (mirrors Terrain.sight_wall).  The leaves of a tree are not a column - they
  // are a volume of their own, up 10-18 m and a few metres across, so they are
  // solved per tree in `canopiesAt`; a whole tile of them would hide a hull
  // standing in the open beside the stand.
  sightWall(x, z) {
    const i = Math.floor(x / this.tile), j = Math.floor(z / this.tile);
    if (i < 0 || j < 0 || i >= this.w || j >= this.h) return 1e6;
    const k = j * this.w + i;
    if (this.tiles[k] === TILE_BUILDING) return this.walls[k] || 8.0;
    return 0;
  }

  // Every canopy that reaches into tile (i, j), the mirror of
  // Terrain._canopies: one ball or one cone per tree, at the numbers the
  // renderer builds the meshes from (`_buildProps`), plus the trees of the 3x3
  // block around the tile - the leaves of the next tile over hang over this
  // one, and the eye sees them.
  canopiesAt(i, j) {
    const k = j * this.w + i;
    let c = this.canopyCache.get(k);
    if (c !== undefined) return c;
    c = [];
    let near = false;
    for (let jj = j - 1; jj <= j + 1 && !near; jj++) {
      if (jj < 0 || jj >= this.h) continue;
      for (let ii = i - 1; ii <= i + 1; ii++) {
        if (ii < 0 || ii >= this.w) continue;
        const t = this.tiles[jj * this.w + ii];
        if (t === TILE_TREE || t === TILE_FOREST) { near = true; break; }
      }
    }
    if (near) {
      const x0 = i * this.tile, z0 = j * this.tile;
      for (let jj = j - 1; jj <= j + 1; jj++) {
        if (jj < 0 || jj >= this.h) continue;
        for (let ii = i - 1; ii <= i + 1; ii++) {
          if (ii < 0 || ii >= this.w) continue;
          const t = this.tiles[jj * this.w + ii];
          if (t !== TILE_TREE && t !== TILE_FOREST) continue;
          for (const tr of this.trunksAt(ii, jj)) {
            const cy = canopyOf(this, tr);
            const dx = cy.x < x0 ? x0 - cy.x : (cy.x > x0 + this.tile ? cy.x - x0 - this.tile : 0);
            const dz = cy.z < z0 ? z0 - cy.z : (cy.z > z0 + this.tile ? cy.z - z0 - this.tile : 0);
            if (dx * dx + dz * dz <= cy.rad * cy.rad) c.push(cy);
          }
        }
      }
    }
    this.canopyCache.set(k, c);
    return c;
  }

  sightAt(x, z) {
    return this.heightAt(x, z) + this.sightWall(x, z);
  }

  // march a segment and report the first ground contact
  groundHit(x0, y0, z0, x1, y1, z1, step) {
    step = step || 12;
    const dx = x1 - x0, dy = y1 - y0, dz = z1 - z0;
    const dist = Math.hypot(dx, dy, dz);
    if (dist < 1e-4) return { hit: y0 <= this.obstacleAt(x0, z0), t: 0, x: x0, y: y0, z: z0 };
    const trunkT = this.trunkHit(x0, y0, z0, x1, y1, z1);
    const n = Math.max(1, Math.ceil(dist / step));
    let prev = 0;
    for (let k = 1; k <= n; k++) {
      const t = k / n;
      if (trunkT !== null && trunkT <= t) {
        return { hit: true, t: trunkT, x: x0 + dx * trunkT, y: y0 + dy * trunkT, z: z0 + dz * trunkT };
      }
      const x = x0 + dx * t, y = y0 + dy * t, z = z0 + dz * t;
      let ground = this.obstacleAt(x, z);
      if (y <= ground) {
        let a = prev, b = t;
        for (let it = 0; it < 4; it++) {
          const m = (a + b) * 0.5;
          if (y0 + dy * m > this.obstacleAt(x0 + dx * m, z0 + dz * m)) a = m; else b = m;
        }
        const tm = (a + b) * 0.5;
        return { hit: true, t: tm, x: x0 + dx * tm, y: y0 + dy * tm, z: z0 + dz * tm };
      }
      prev = t;
    }
    return { hit: false, t: 1, x: x1, y: y1, z: z1 };
  }
}

// ---------- line of sight -----------------------------------------------
// Does anything cut the sight line a->b?  The mirror of
// backend/server/systems/sight.py, and the one rule the game has: the field
// (ground, buildings, tree canopies - `sightAt`) and the deployed smoke puffs
// hide; a trunk alone never does, and neither the leaves nor the smoke stop a
// round.  The HUD asks it before it draws a pre-aim box, so a hull the seeker
// could not lock is a hull the pilot gets no marker on either.

// the ellipsoid test of systems/smoke.py: scale the segment into the unit
// sphere of the puff and take the closest point
function puffBlocks(p, ax, ay, az, bx, by, bz) {
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
  if (dist < 1e-4) return y0 <= map.sightAt(x0, z0);
  if (y0 <= map.sightAt(x0, z0) + 1.0) return true;   // the eye is in the ground
  const n = Math.max(1, Math.ceil(dist / step));
  for (let k = 1; k <= n; k++) {
    const t = k / n;
    if (y0 + dy * t <= map.sightAt(x0 + dx * t, z0 + dz * t)) return true;
  }
  if (canopyBlocked(map, x0, y0, z0, dx, dy, dz)) return true;
  if (puffs) {
    for (const p of puffs) if (puffBlocks(p, x0, y0, z0, x1, y1, z1)) return true;
  }
  return false;
}

// Ballistic prediction for direct fire weapons (bullets + unguided rockets).
export function predictImpact(map, wd, ox, oy, oz, dir, units, out, faction) {
  out = out || {};
  // the launcher's mechanical elevation: the rocket pod is sighted up so its
  // slow round meets the cannon's line at 1 km, same bias the server applies
  const bias = wd.bias || 0;
  let dx = dir[0], dy0 = dir[1], dz = dir[2];
  if (bias) {
    const pitch = Math.asin(Math.max(-1, Math.min(1, dir[1]))) + bias;
    const yaw = Math.atan2(dir[2], dir[0]);
    const cp = Math.cos(pitch);
    dx = cp * Math.cos(yaw); dy0 = Math.sin(pitch); dz = cp * Math.sin(yaw);
  }
  let vx = dx * wd.vel, vy = dy0 * wd.vel, vz = dz * wd.vel;
  let x = ox, y = oy, z = oz;
  // the server integrates at 30 Hz: using the same step here keeps the
  // predicted impact point identical to where the round really lands
  const dt = 1 / 30;
  const maxT = wd.life;
  // the round now lives long enough to fly 10 km, so the candidate cut has to
  // follow the fuse too: a round that still has speed left can hit anything it
  // can reach, not just what is inside its nominal `range`
  const reach = wd.life * Math.max(wd.vel, wd.maxvel || 0);
  const near = [];
  if (units) {
    for (const u of units) {
      if (faction !== undefined && u.faction === faction) continue;
      const d = Math.hypot(u.x - ox, u.z - oz);
      if (d < reach + 60) near.push(u);
    }
  }
  for (let t = 0; t < maxT; t += dt) {
    if (wd.kind === 'rocket') {
      const s = Math.hypot(vx, vy, vz);
      const ns = Math.min(wd.maxvel, s + wd.thrust * dt);
      const k = ns / Math.max(s, 1e-6);
      vx *= k; vy *= k; vz *= k;
      vy -= wd.g * dt;
    } else {
      vy -= wd.g * dt;
    }
    const nx = x + vx * dt, ny = y + vy * dt, nz = z + vz * dt;

    // units
    let bestT = 2, hitU = null;
    const sx = nx - x, sy = ny - y, sz = nz - z;
    const l2 = sx * sx + sy * sy + sz * sz;
    for (const u of near) {
      const rx = u.x - x, ry = u.y - y, rz = u.z - z;
      let tt = (rx * sx + ry * sy + rz * sz) / l2;
      tt = tt < 0 ? 0 : tt > 1 ? 1 : tt;
      const cx = x + sx * tt - u.x, cy = y + sy * tt - u.y, cz = z + sz * tt - u.z;
      const d2 = cx * cx + cy * cy + cz * cz;
      if (d2 < (u.r + 0.3) * (u.r + 0.3) && tt < bestT) {
        bestT = tt;
        hitU = u;
      }
    }
    // The ground is sampled *along* the step, not only at its end: the 30 mm
    // round covers 36 m in one 30 Hz tick - more than two tiles - so testing
    // just (nx, ny, nz) would step straight through a building the server stops
    // it on, and the reticle would claim a hit far behind the facade.  Past the
    // map edge the server finds no ground (`terrain.ground_hit` gives up
    // outside the field), so the prediction must not stop there either.
    let groundT = 2, gx = 0, gy = 0, gz = 0;
    const nSamp = Math.max(1, Math.ceil(Math.sqrt(l2) / 8));
    let prevU = 0;
    for (let k = 1; k <= nSamp; k++) {
      const u = k / nSamp;
      const px = x + sx * u, py = y + sy * u, pz = z + sz * u;
      const g = map.inside(px, pz) ? map.obstacleAt(px, pz) : -Infinity;
      if (py > g) { prevU = u; continue; }
      // bisect onto the obstacle the server tests against, so both sides land
      // within a few centimetres of the same point on the facade
      let a = prevU, b = u;
      for (let it = 0; it < 3; it++) {
        const m = (a + b) * 0.5;
        const mx2 = x + sx * m, my2 = y + sy * m, mz2 = z + sz * m;
        const gm = map.inside(mx2, mz2) ? map.obstacleAt(mx2, mz2) : -Infinity;
        if (my2 > gm) a = m; else b = m;
      }
      groundT = b; gx = x + sx * b; gy = y + sy * b; gz = z + sz * b;
      break;
    }
    // a tree trunk is a metre wide - the 8 m samples above walk right past it,
    // so it is solved as a cylinder (mirrors Terrain._trunk_hit)
    const trunkT = map.trunkHit(x, y, z, nx, ny, nz);
    if (trunkT !== null && trunkT < groundT) {
      groundT = trunkT;
      gx = x + sx * trunkT; gy = y + sy * trunkT; gz = z + sz * trunkT;
    }
    if (bestT <= 1 && bestT <= groundT) {
      out.hit = true; out.unit = hitU;
      out.x = x + sx * bestT; out.y = y + sy * bestT; out.z = z + sz * bestT;
      out.dist = Math.hypot(out.x - ox, out.z - oz);
      out.time = t + dt * bestT;
      return out;
    }
    if (groundT <= 1) {
      out.hit = true; out.unit = null;
      out.x = gx; out.y = gy; out.z = gz;
      out.dist = Math.hypot(out.x - ox, out.z - oz);
      out.time = t + dt * groundT;
      return out;
    }
    x = nx; y = ny; z = nz;
  }
  out.hit = false; out.unit = null;
  out.x = x; out.y = y; out.z = z;
  out.dist = Math.hypot(x - ox, z - oz);
  out.time = maxT;
  return out;
}

export const TILES = {
  TILE_GRASS, TILE_ROAD, TILE_BUILDING, TILE_TREE, TILE_ROCK, TILE_BASE, TILE_SOLID, TILE_FOREST,
};

// The sight ray's resolution, metres: the range readout walks the crosshair in
// steps this big and then bisects - the figure the server's own line of sight
// walks at (`Terrain.los`, 8 m), so the two agree about which side of a ridge a
// point is on.
const MARCH = 8;

// What the centre cross is pointing at: the first thing the sight ray runs
// into - ground, a building face, a tree trunk or a hull - out to `maxDist`.
// This is the number under the cross, and it is the client's own reading: the
// gun is zeroed to the pilot's dial (`fcs.js`), so the round lands at the dial's
// range along this line whatever the ray found.
export function sightPoint(map, ox, oy, oz, dir, maxDist, units, ignoreId, faction) {
  let best = maxDist;
  let point = null;
  for (const u of units) {
    if (u.id === ignoreId) continue;
    if (faction !== undefined && u.faction === faction) continue;   // own rounds pass through
    const vx = u.x - ox, vy = u.y - oy, vz = u.z - oz;
    const b = vx * dir[0] + vy * dir[1] + vz * dir[2];
    if (b < 0 || b - u.r > best) continue;
    const disc = b * b - (vx * vx + vy * vy + vz * vz - u.r * u.r);
    if (disc < 0) continue;
    const t = b - Math.sqrt(disc);
    if (t < 0 || t >= best) continue;
    best = t;
    point = { x: ox + dir[0] * t, y: oy + dir[1] * t, z: oz + dir[2] * t };
  }
  const limit = best;
  const at = (t) => ({ x: ox + dir[0] * t, y: oy + dir[1] * t, z: oz + dir[2] * t });
  for (let t0 = 0; t0 < limit; t0 += MARCH) {
    const t1 = Math.min(limit, t0 + MARCH);
    const x = ox + dir[0] * t1, y = oy + dir[1] * t1, z = oz + dir[2] * t1;
    // off the map there is nothing to range against
    if (!map.inside(x, z)) return point;
    // a trunk is a metre wide and MARCH is 8 m, so it is solved over the whole
    // step instead of sampled
    const th = map.trunkHit(ox + dir[0] * t0, oy + dir[1] * t0, oz + dir[2] * t0, x, y, z);
    const trunkT = th === null ? null : t0 + th * (t1 - t0);
    if (y > map.heightAt(x, z) && !map.blocked(x, z)) {
      if (trunkT !== null) return at(trunkT);
      continue;
    }
    let a = t0, b = t1;
    for (let i = 0; i < 6; i++) {
      const m = 0.5 * (a + b);
      const mx = ox + dir[0] * m, my = oy + dir[1] * m, mz = oz + dir[2] * m;
      if (my > map.heightAt(mx, mz) && !map.blocked(mx, mz)) a = m; else b = m;
    }
    const t = 0.5 * (a + b);
    return at(trunkT !== null && trunkT < t ? trunkT : t);
  }
  return point;
}

// Hit spheres of a unit, mirroring mathutil.sphere_geom: the sight ray and
// the impact prediction have to see the same bodies the server's rounds do,
// otherwise the zeroing solution lands somewhere the reticle does not point
const UNIT_SPHERES = {
  1: [[0.9, 0.65]], 2: [[0.9, 0.65]], 3: [[1.1, 1.7], [2.4, 1.2]],
  5: [[5.0, 8.5]], 6: [[0.9, 0.65]], 9: [[4.0, 5.0], [8.8, 3.6]],
};
const DEFAULT_SPHERES = [[1.0, 1.5]];

export function unitSpheres(etype, x, y, z, yaw, mechSpheres) {
  if (etype === 4 && mechSpheres) {
    // (module, right offset, height, forward offset, radius)
    const c = Math.cos(yaw), s = Math.sin(yaw);
    return mechSpheres.map((m) => ({
      x: x - s * m[1] + c * m[3], y: y + m[2], z: z + c * m[1] + s * m[3], r: m[4],
    }));
  }
  const set = UNIT_SPHERES[etype] || DEFAULT_SPHERES;
  return set.map((m) => ({ x, y: y + m[0], z, r: m[1] }));
}
