// The client's ballistic prediction: where a round fired along the cross
// really lands, and what the centre cross is ranging against.  Both solve
// against the round's rule (`solid.js`) so the reticle agrees with the
// simulation - a building is a column, a trunk is a cylinder, the ground is the
// field of `field.js`.

import { obstacleAt, trunkHit } from './solid.js';

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
      const g = map.inside(px, pz) ? obstacleAt(map, px, pz) : -Infinity;
      if (py > g) { prevU = u; continue; }
      // bisect onto the obstacle the server tests against, so both sides land
      // within a few centimetres of the same point on the facade
      let a = prevU, b = u;
      for (let it = 0; it < 3; it++) {
        const m = (a + b) * 0.5;
        const mx2 = x + sx * m, my2 = y + sy * m, mz2 = z + sz * m;
        const gm = map.inside(mx2, mz2) ? obstacleAt(map, mx2, mz2) : -Infinity;
        if (my2 > gm) a = m; else b = m;
      }
      groundT = b; gx = x + sx * b; gy = y + sy * b; gz = z + sz * b;
      break;
    }
    // a tree trunk is a metre wide - the 8 m samples above walk right past it,
    // so it is solved as a cylinder (mirrors Terrain._trunk_hit)
    const tT = trunkHit(map, x, y, z, nx, ny, nz);
    if (tT !== null && tT < groundT) {
      groundT = tT;
      gx = x + sx * tT; gy = y + sy * tT; gz = z + sz * tT;
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
    const th = trunkHit(map, ox + dir[0] * t0, oy + dir[1] * t0, oz + dir[2] * t0, x, y, z);
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
export const UNIT_SPHERES = {
  1: [[0.9, 0.65]], 2: [[0.9, 0.65]], 3: [[1.1, 1.7], [2.4, 1.2]],
  5: [[5.0, 8.5]], 6: [[0.9, 0.65]], 9: [[4.0, 5.0], [8.8, 3.6]],
};
export const DEFAULT_SPHERES = [[1.0, 1.5]];

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
