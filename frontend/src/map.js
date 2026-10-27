// Client copy of the terrain + the same ballistics the server runs, so the
// reticle impact marker agrees with the simulation.

import { dirFrom } from './math.js';

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
// above the trunks is not in here: it hides what is behind it without stopping
// anything that flies.
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
  }

  static async load(info, lanes) {
    const hBytes = await inflate(b64ToBytes(info.heights));
    const i16 = new Int16Array(hBytes.buffer, hBytes.byteOffset, hBytes.byteLength >> 1);
    const heights = new Float32Array(i16.length);
    for (let i = 0; i < i16.length; i++) heights[i] = i16[i];
    const tiles = await inflate(b64ToBytes(info.tiles));
    const roads = info.roads ? await inflate(b64ToBytes(info.roads)) : new Uint8Array(tiles.length);
    const walls = info.walls ? await inflate(b64ToBytes(info.walls)) : new Uint8Array(tiles.length);
    const map = new MapData(info.w, info.h, info.tile, heights, tiles, roads, walls);
    map.lanes = lanes || [];     // road centrelines, in world metres
    return map;
  }

  heightAt(x, z) {
    const fx = Math.min(Math.max(x / this.tile, 0), this.w - 1.001);
    const fz = Math.min(Math.max(z / this.tile, 0), this.h - 1.001);
    const i = fx | 0, j = fz | 0;
    const tx = fx - i, tz = fz - j;
    const h = this.heights;
    const a = h[j * this.w + i], b = h[j * this.w + i + 1];
    const c = h[(j + 1) * this.w + i], d = h[(j + 1) * this.w + i + 1];
    return (a * (1 - tx) + b * tx) * (1 - tz) + (c * (1 - tx) + d * tx) * tz;
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

// Fire control, mirroring backend/server/ballistics.py: the sight is set to a
// fixed range and the barrel holds off for the drop to that range.  Both sides
// have to agree or the reticle lies about where the shot lands.
const MARCH = 24;        // sight-ray resolution, metres
const MIN_DIST = 25;

// What the centre cross is pointing at: the first thing the sight ray runs
// into - ground, a building face, a tree trunk or a hull - out to `maxDist`.
// This is the number under the cross, and it is the mirror of the server's
// `ballistics.sight_point`, so the reading is the range the gun really has.
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
    // a trunk is a metre wide and MARCH is 24 m, so it is solved over the whole
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

// height and flight time of the round once it has covered `dist` metres of
// horizontal travel: the same step, gravity and thrust curve the server runs
function flightTo(wd, ox, oy, oz, yaw, pitch, dist) {
  const cp = Math.cos(pitch);
  let vx = cp * Math.cos(yaw) * wd.vel, vy = Math.sin(pitch) * wd.vel, vz = cp * Math.sin(yaw) * wd.vel;
  let x = ox, y = oy, z = oz;
  const dt = 1 / 30;
  const life = wd.life;
  let prevH = 0, prevY = y;
  for (let t = 0; t < life; t += dt) {
    if (wd.thrust) {
      const speed = Math.hypot(vx, vy, vz);
      const k = Math.min(wd.maxvel, speed + wd.thrust * dt) / Math.max(speed, 1e-6);
      vx *= k; vy *= k; vz *= k;
    }
    vy -= wd.g * dt;
    x += vx * dt; y += vy * dt; z += vz * dt;
    const h = Math.hypot(x - ox, z - oz);
    if (h >= dist) {
      const span = h - prevH;
      const f = span > 1e-6 ? (dist - prevH) / span : 1;
      return { y: prevY + (y - prevY) * f, t: t + dt };
    }
    prevH = h; prevY = y;
  }
  return null;
}

// altitude of the round after `dist` metres of horizontal travel
function altitudeAt(wd, ox, oy, oz, yaw, pitch, dist) {
  const hit = flightTo(wd, ox, oy, oz, yaw, pitch, dist);
  return hit ? hit.y : null;
}

export function solveElevation(wd, ox, oy, oz, yaw, pitch, target, maxElev) {
  const d = Math.hypot(target.x - ox, target.z - oz);
  if (d < MIN_DIST) return null;
  const h = target.y - oy;
  const g = wd.g, vel = wd.vel;
  if (g > 0 && vel > 0 && !wd.thrust) {
    if (d > vel * wd.life) return null;
    const disc = vel ** 4 - g * (g * d * d + 2 * h * vel * vel);
    if (disc < 0) return null;
    return Math.atan2(vel * vel - Math.sqrt(disc), g * d);
  }
  // altitudeAt gives an absolute height: walk it against the target's absolute
  // height, not the drop below the muzzle used by the closed form above
  let t0 = pitch, t1 = pitch + 0.02;
  let y0 = altitudeAt(wd, ox, oy, oz, yaw, t0, d);
  if (y0 === null) return null;
  for (let i = 0; i < 6; i++) {
    const y1 = altitudeAt(wd, ox, oy, oz, yaw, t1, d);
    if (y1 === null) return null;
    const err = target.y - y1;
    if (Math.abs(err) < 0.1) return t1;
    const dy = y1 - y0;
    if (Math.abs(dy) < 1e-6) return null;
    const t2 = Math.max(-1.2, Math.min(maxElev, t1 + err * (t1 - t0) / dy));
    t0 = t1; y0 = y1; t1 = t2;
  }
  return Math.abs(t1) <= maxElev ? t1 : null;
}

// The pre-aim ring: the point the centre cross has to be laid on for the round
// to arrive on a hull that will be at (tx, ty, tz) in a moment.
//
// Two offsets separate the cross from the bore and both are handled here:
//
//  * **Range** - a fixed zero only meets the sight line at the range it is set
//    to, so nearer the round is high and further it is low.  The ring sits
//    where the sight line has to point, which is solved as a fixed point: `p`
//    is a sight elevation, the round it would launch is integrated out to the
//    target's range, and `p` is walked until that round meets the hull centre.
//  * **Parallax** - the eye sits on the canopy, 1.4 m inboard of the arm and
//    1.5 m above it, so a sight line and a bore that share a heading do not
//    share a point at range.  The bearing is solved from the muzzle, so the
//    round flies to the hull, and the ring is then drawn along that bearing
//    *from the eye* - which is what the pilot actually lays the cross on.
export function leadPoint(wd, zeroRange, maxElev, mx, my, mz, ex, ey, ez,
                          tx, ty, tz, vx, vz) {
  // the bearing the bore has to take: from the muzzle to where the hull will
  // be.  Static targets keep the pilot's heading, movers need the lead folded
  // in, so the bearing is solved twice.
  let bx = tx, bz = tz;
  let yaw = Math.atan2(bz - mz, bx - mx);
  let shot = null;
  let p = 0;
  for (let pass = 0; pass < 2; pass++) {
    const dm = Math.hypot(bx - mx, bz - mz);
    yaw = Math.atan2(bz - mz, bx - mx);
    p = Math.atan2(ty - my, dm);               // start on the hull
    shot = null;
    for (let i = 0; i < 4; i++) {
      const theta = zeroAt(wd, yaw, p, mx, my, mz, zeroRange, maxElev);
      const s = flightTo(wd, 0, 0, 0, 0, theta === null ? p : theta, dm);
      if (s === null) return null;
      shot = s;
      const err = ty - (my + s.y);
      if (Math.abs(err) < 0.15) break;
      p += Math.atan2(err, dm);
    }
    if (shot === null) return null;
    // where the hull has moved to by the time that round lands
    bx = tx + vx * shot.t;
    bz = tz + vz * shot.t;
  }
  // the ring sits on the sight line the pilot lays the cross along: the bore's
  // bearing, drawn from the eye, so aiming at it puts the round on the hull
  const de = Math.hypot(bx - ex, bz - ez);
  return {
    x: ex + Math.cos(yaw) * de,
    y: ey + Math.tan(p) * de,
    z: ez + Math.sin(yaw) * de,
    tof: shot.t,
  };
}

// The launch elevation that puts the round on the sight line `distance`
// metres out - the pilot's sight setting.  Unlike a solve against the first
// thing the ray hits, this depends on the setting and the hull's attitude
// only, so a hull crossing a ridge no longer makes the reticle jump.
export function zeroAt(wd, yaw, pitch, ox, oy, oz, distance, maxElev) {
  const d = dirFrom(yaw, pitch);
  const target = {
    x: ox + d[0] * distance,
    y: oy + d[1] * distance,
    z: oz + d[2] * distance,
  };
  return solveElevation(wd, ox, oy, oz, yaw, pitch, target, maxElev);
}

// Hit spheres of a unit, mirroring mathutil.unit_spheres: the sight ray and
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
