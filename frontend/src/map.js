// Client copy of the terrain + the same ballistics the server runs, so the
// reticle impact marker agrees with the simulation.

import { dirFrom } from './math.js';

const TILE_GRASS = 0, TILE_ROAD = 1, TILE_BUILDING = 2, TILE_TREE = 3, TILE_ROCK = 4, TILE_BASE = 5, TILE_SOLID = 6;

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
  constructor(w, h, tile, heights, tiles) {
    this.w = w;
    this.h = h;
    this.tile = tile;
    this.heights = heights;      // Float32Array(w*h), row major [j*w+i]
    this.tiles = tiles;          // Uint8Array(w*h)
  }

  static async load(info) {
    const hBytes = await inflate(b64ToBytes(info.heights));
    const i16 = new Int16Array(hBytes.buffer, hBytes.byteOffset, hBytes.byteLength >> 1);
    const heights = new Float32Array(i16.length);
    for (let i = 0; i < i16.length; i++) heights[i] = i16[i];
    const tiles = await inflate(b64ToBytes(info.tiles));
    return new MapData(info.w, info.h, info.tile, heights, tiles);
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
    return t === TILE_BUILDING || t === TILE_TREE || t === TILE_ROCK || t === TILE_SOLID;
  }

  // march a segment and report the first ground contact
  groundHit(x0, y0, z0, x1, y1, z1, step) {
    step = step || 12;
    const dx = x1 - x0, dy = y1 - y0, dz = z1 - z0;
    const dist = Math.hypot(dx, dy, dz);
    if (dist < 1e-4) return { hit: y0 <= this.heightAt(x0, z0), t: 0, x: x0, y: y0, z: z0 };
    const n = Math.max(1, Math.ceil(dist / step));
    let prev = 0;
    for (let k = 1; k <= n; k++) {
      const t = k / n;
      const x = x0 + dx * t, y = y0 + dy * t, z = z0 + dz * t;
      let ground = this.heightAt(x, z);
      if (y <= ground) {
        let a = prev, b = t;
        for (let it = 0; it < 4; it++) {
          const m = (a + b) * 0.5;
          if (y0 + dy * m > this.heightAt(x0 + dx * m, z0 + dz * m)) a = m; else b = m;
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
  const maxT = Math.min(wd.life, wd.range / wd.vel);
  const near = [];
  if (units) {
    for (const u of units) {
      if (faction !== undefined && u.faction === faction) continue;
      const d = Math.hypot(u.x - ox, u.z - oz);
      if (d < wd.range + 60) near.push(u);
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
    const ground = map.heightAt(nx, nz);
    const groundT = ny <= ground ? Math.max(0, (y - ground) / Math.max(y - ny, 1e-6)) : 2;
    if (bestT <= 1 && bestT <= groundT) {
      out.hit = true; out.unit = hitU;
      out.x = x + sx * bestT; out.y = y + sy * bestT; out.z = z + sz * bestT;
      out.dist = Math.hypot(out.x - ox, out.z - oz);
      out.time = t + dt * bestT;
      return out;
    }
    if (ny <= ground) {
      const tt = Math.min(1, groundT);
      out.hit = true; out.unit = null;
      out.x = x + sx * tt; out.y = y + sy * tt; out.z = z + sz * tt;
      out.dist = Math.hypot(out.x - ox, out.z - oz);
      out.time = t + dt * tt;
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

export const TILES = { TILE_GRASS, TILE_ROAD, TILE_BUILDING, TILE_TREE, TILE_ROCK, TILE_BASE, TILE_SOLID };

// Fire control, mirroring backend/server/ballistics.py: what the muzzle ray
// runs into, and the elevation that drops the round on it.  Both sides have to
// agree or the reticle lies about where the shot lands.
const MARCH = 24;        // sight-ray resolution, metres
const MIN_DIST = 25;

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
  for (let t0 = 0; t0 < limit; t0 += MARCH) {
    const t1 = Math.min(limit, t0 + MARCH);
    const x = ox + dir[0] * t1, y = oy + dir[1] * t1, z = oz + dir[2] * t1;
    if (y > map.heightAt(x, z) && !map.blocked(x, z)) continue;
    let a = t0, b = t1;
    for (let i = 0; i < 6; i++) {
      const m = 0.5 * (a + b);
      const mx = ox + dir[0] * m, my = oy + dir[1] * m, mz = oz + dir[2] * m;
      if (my > map.heightAt(mx, mz) && !map.blocked(mx, mz)) a = m; else b = m;
    }
    const t = 0.5 * (a + b);
    return { x: ox + dir[0] * t, y: oy + dir[1] * t, z: oz + dir[2] * t };
  }
  return point;
}

// altitude of the round after `dist` metres of horizontal travel
function altitudeAt(wd, ox, oy, oz, yaw, pitch, dist) {
  const cp = Math.cos(pitch);
  let vx = cp * Math.cos(yaw) * wd.vel, vy = Math.sin(pitch) * wd.vel, vz = cp * Math.sin(yaw) * wd.vel;
  let x = ox, y = oy, z = oz;
  const dt = 1 / 30;
  const life = Math.min(wd.life, wd.range / Math.max(wd.vel, 1e-6));
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
      return prevY + (y - prevY) * f;
    }
    prevH = h; prevY = y;
  }
  return null;
}

export function solveElevation(wd, ox, oy, oz, yaw, pitch, target, maxElev) {
  const d = Math.hypot(target.x - ox, target.z - oz);
  if (d < MIN_DIST) return null;
  const h = target.y - oy;
  const g = wd.g, vel = wd.vel;
  if (g > 0 && vel > 0 && !wd.thrust) {
    if (d > vel * Math.min(wd.life, wd.range / vel)) return null;
    const disc = vel ** 4 - g * (g * d * d + 2 * h * vel * vel);
    if (disc < 0) return null;
    return Math.atan2(vel * vel - Math.sqrt(disc), g * d);
  }
  let t0 = pitch, t1 = pitch + 0.02;
  let y0 = altitudeAt(wd, ox, oy, oz, yaw, t0, d);
  if (y0 === null) return null;
  for (let i = 0; i < 6; i++) {
    const y1 = altitudeAt(wd, ox, oy, oz, yaw, t1, d);
    if (y1 === null) return null;
    const err = h - y1;
    if (Math.abs(err) < 0.1) return t1;
    const dy = y1 - y0;
    if (Math.abs(dy) < 1e-6) return null;
    const t2 = Math.max(-1.2, Math.min(maxElev, t1 + err * (t1 - t0) / dy));
    t0 = t1; y0 = y1; t1 = t2;
  }
  return Math.abs(t1) <= maxElev ? t1 : null;
}

// the elevation a zeroed shot of `wd` leaves at, or null for the plain aim
export function zeroPitch(map, wd, yaw, pitch, ox, oy, oz, units, ignoreId, margin, maxElev, faction) {
  const cap = wd.range * margin;
  if (cap <= MIN_DIST) return null;
  const p = sightPoint(map, ox, oy, oz, dirFrom(yaw, pitch), cap, units, ignoreId, faction);
  if (!p) return null;
  return solveElevation(wd, ox, oy, oz, yaw, pitch, p, maxElev);
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
