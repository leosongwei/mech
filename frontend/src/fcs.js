// The fire control: where the sight must be laid, and how high the barrel must be,
// for a gun zeroed to `Z` to put its round on a moving target.
//
// This is the law `Core/FireControl.cs` solves on the server, line for line.  It is
// a pure function of numbers - a `shot` (muzzle, eye, weapon, setting) and a
// `track` (where the target is, what it is doing, how high to hit it) - so it runs
// in the browser, in Node and in the test suite with no world, no map and no state.
// `tests/cases/fcs.json` is the table that keeps the two implementations honest
// (`run_tests.sh`).
//
// The shape of the law: the *sight* line is where the crew looks (that is what the
// wire carries and what the hull swings after), the *elevation* is where the barrel
// is laid (the sight line, held off by `Z` metres of zeroing), and the solution
// walks two loops - the sight elevation to the target's height at its range, and
// the intercept point by the flight time that implies.

import { dirFrom } from './math.js';

const MIN_DIST = 25;                // m, closer than this the aim point is the machine itself
const LEAD_ACCEL_S = 1.0;           // s, how much of the flight the target's pull counts for
const LEAD_ACCEL_MAX = 12;          // m/s^2, above the mech's 9.3 (measurement noise)
const INNER = 4;                    // passes on the sight elevation
const CONVERGE = 0.15;              // m, the elevation loop's stopping distance

// height and flight time of the round once it has covered `dist` metres of
// horizontal travel: the same step, gravity and thrust curve the server runs
export function flightTo(wd, ox, oy, oz, yaw, pitch, dist) {
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
      // the *time* is interpolated the same way the height is: reporting the end of
      // the tick costs up to a whole tick of flight time, which against a target
      // climbing at 40 m/s is over a metre of lead
      return { y: prevY + (y - prevY) * f, t: t + f * dt };
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
//  * **Parallax** - the eye sits in the sight head, 1.4 m inboard of the arm
//    and 1.5 m above it, so a sight line and a bore that share a heading do not
//    share a point at range.  The bearing is solved from the muzzle, so the
//    round flies to the hull, and the ring is then drawn along that bearing
//    *from the eye* - which is what the pilot actually lays the cross on.
//
// The lead itself is first order in the target's velocity, plus a second-order
// term when one is measured (`ax` / `az`, off the wire by `net.js`): a hull
// that is still pulling away is not where its current speed alone says it will
// be, and at a 1.5-2 s time of flight a 9 m/s^2 machine is 10-20 m further on
// than the straight lead.  The acceleration is a straight-line extrapolation of
// what is often a bend, though, so it is trusted for the first
// `LEAD_ACCEL_S` seconds of the flight and its magnitude is capped at what a
// machine can actually do - integrating a turn out to a long flight would draw
// a circle the target never drives.
export function leadPoint(wd, zeroRange, maxElev, mx, my, mz, ex, ey, ez,
                          tx, ty, tz, vx, vz, vy = 0, ax = 0, az = 0) {
  // the bearing the bore has to take: from the muzzle to where the target will
  // be.  Static targets keep the pilot's heading, movers need the lead folded
  // in, so the bearing is solved twice.  `vy` is the target's climb rate: a
  // hull drives on the flat (0, and nothing changes for one), a missile in the
  // air climbs or dives and *that* component is half of the lead.
  const amag = Math.hypot(ax, az);
  if (amag > LEAD_ACCEL_MAX) { ax *= LEAD_ACCEL_MAX / amag; az *= LEAD_ACCEL_MAX / amag; }
  let bx = tx, by = ty, bz = tz;
  let yaw = Math.atan2(bz - mz, bx - mx);
  let shot = null;
  let p = 0;
  // four passes, not two: a hull leads by a few metres and converges at once,
  // but a missile closing at 600 m/s leads by hundreds and each pass moves the
  // aim point that far again (~0.9 of the previous pass - it settles in three)
  for (let pass = 0; pass < 4; pass++) {
    const dm = Math.hypot(bx - mx, bz - mz);
    // inside `MIN_DIST` the aim point is the machine itself: there is no elevation
    // to solve for, and the mount simply points at it
    if (dm < MIN_DIST) return null;
    yaw = Math.atan2(bz - mz, bx - mx);
    p = Math.atan2(by - my, dm);               // start on the target
    shot = null;
    for (let i = 0; i < 4; i++) {
      // no elevation on this mount puts the round on the point: there is no shot,
      // rather than a plausible-looking angle that misses
      const theta = zeroAt(wd, yaw, p, mx, my, mz, zeroRange, maxElev);
      if (theta === null) return null;
      const s = flightTo(wd, 0, 0, 0, 0, theta, dm);
      if (s === null) return null;
      shot = s;
      const err = by - (my + s.y);
      if (Math.abs(err) < 0.15) break;
      p += Math.atan2(err, dm);
    }
    if (shot === null) return null;
    // where the target has moved to by the time that round lands: velocity all
    // the way, plus the measured pull over `ta` seconds of the flight.  The
    // term is `0.5 a T ta` - the full square while the flight is short enough
    // for the extrapolation to be worth trusting (`ta` = T), and linear in T
    // past `LEAD_ACCEL_S` so a long shot does not draw a circle
    const ta = Math.min(shot.t, LEAD_ACCEL_S);
    bx = tx + vx * shot.t + 0.5 * ax * shot.t * ta;
    by = ty + vy * shot.t;
    bz = tz + vz * shot.t + 0.5 * az * shot.t * ta;
  }
  // the ring sits on the sight line the pilot lays the cross along: the bore's
  // bearing, drawn from the eye, so aiming at it puts the round on the target
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


// The one entry point both hosts share: a `shot` and a `track` in, a solution out.
// Field names are the ones `tests/cases/fcs.json` uses, so a case file reads as a
// call to this function.
export function solve(shot, track) {
  const [mx, my, mz] = shot.muzzle;
  const [ex, ey, ez] = shot.eye;
  const [tx, ty, tz] = track.pos;
  const [vx, vy, vz] = track.vel;
  const [axv, azv] = track.acc || [0, 0];
  const maxElev = shot.maxElev === undefined ? 1.05 : shot.maxElev;
  const p = leadPoint(shot.wd, shot.zero, maxElev, mx, my, mz, ex, ey, ez,
                      tx, ty + track.aim, tz, vx, vz, vy, axv, azv);
  if (!p) return { valid: false };
  // the sight line the crew lays: the bore's bearing, drawn from the eye
  const dh = Math.hypot(p.x - ex, p.z - ez);
  const yaw = Math.atan2(p.z - ez, p.x - ex);
  const pitch = Math.atan2(p.y - ey, Math.max(dh, 1e-3));
  const elev = zeroAt(shot.wd, yaw, pitch, mx, my, mz, shot.zero, maxElev);
  return {
    valid: true,
    point: [p.x, p.y, p.z],
    yaw,
    pitch,
    elevation: elev === null ? pitch : elev,
    tof: p.tof,
  };
}
