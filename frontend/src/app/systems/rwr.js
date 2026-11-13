// The radar warning receiver: what the pilot's machine can hear, what it can
// place, and which of those is coming for this hull.
//
// This is one system with one output (`alarm`) and it feeds three things: the
// warning dial's rim and marks, the threat arrows in the ring beside it, and the
// two trills.  The dial's *face* is a constant (`RWR_RANGE`), so the mark's
// distance from the middle is metres; the receiver's own discovery range is the
// hull's (`radarRing`, `d^2 <= K * v_c`), so a mark either has a position or is
// heard as a bearing alone.
//
// The state it keeps is one Map: the bearing history of every round it can hear,
// which is the only thing that answers "is it coming" for a round it cannot
// place.  It lives here rather than on the app because it is per-round and
// valueless to anyone else.

import {
  ALARM_GAIN, ARROW_HEAD, ARROW_LEN, ARROW_RGB, ARROW_WID, BEAR_MAX, BEAR_WIN,
  LASER_CONE, LASER_RANGE, RWR_ARROWS, RWR_RANGE, STEADY_T, WARN_CONE, WARN_MARGIN,
} from '../../core/config.js';
import { clamp, dirFrom } from '../../core/math.js';
import { arrowRingAt } from '../../hud/index.js';
import { sightBlocked } from '../../map/index.js';

// The closest approach of a round's horizontal ray to this hull - along the
// *forward* part of the ray, so a round that has already gone by is measured by
// the gap to itself and not by the line it left behind.  `threats` asks it of
// two vectors and gets two answers: the *relative* velocity is where the round's
// track runs once this hull's own motion is taken out (`warn`), the round's own
// is where its nose is pointing (`aim`).  They are the same test on a hull
// standing still.
export function rayMiss(dx, dz, vx, vz) {
  const v = Math.hypot(vx, vz);
  if (v < 1e-3) return Math.hypot(dx, dz);          // hovering / straight down
  const s = -(dx * vx + dz * vz) / v;               // metres along the ray
  if (s <= 0) return Math.hypot(dx, dz);            // behind it: the gap as it is
  return Math.hypot(dx + vx / v * s, dz + vz / v * s);
}

export class Rwr {
  constructor() {
    this.threats = [];     // placed rounds, nearest first (the dial and the boxes)
    this.lasers = [];      // enemy sights on this hull
    this.alarm = 0;        // the one boolean the rim, the arrows and the tone read
    this.ring = 0;         // the hull's own discovery range, metres
    this._bear = new Map();   // round id -> bearing history (no position yet)
    this._heard = new Set();  // the ids heard this frame, for pruning
  }

  // `warn` and `aim` are one test on two vectors, and between them they cover
  // both ways a round comes for this hull: `warn` corrects for this hull's own
  // motion, so it answers a round aimed at where the hull *will* be (a led beam
  // rider, the terminal PN of a homing round); `aim` ignores that motion, so it
  // answers a round whose nose is on the hull *now* - pure pursuit, which is
  // what a top attack flies the whole way in from `seen`, and which curves onto
  // a crossing hull while its straight-line track still misses by hundreds of
  // metres.  Measured on the range: crossing at 50 m/s against a 1500 m top
  // attack, `warn` first holds at about 240 m and `aim` at 890 m - the round hit
  // either way, and the 650 m of silence in between was the alarm being wrong
  // about a round that was pointed straight at the hull.
  //
  // Everything geometric here is *horizontal*: a top-attack round climbs to
  // 950 m, glides, then dives, so its vertical profile says "not for me" right
  // up until it is overhead.  Its bearing and its ground track do not.
  //
  // All judgements are recomputed every frame, with no latch and no hysteresis:
  // a round that slows down or turns falls back out of `seen` and has to earn it
  // again, and that fallback is information the pilot can learn to read
  // (`docs/product/rwr.md` §5).
  update(app, own) {
    this.threats = [];
    this.lasers = [];
    this.alarm = 0;
    this.ring = 0;
    if (!own) { this._bear.clear(); return; }
    const radar = app.defs.radar || { v_ref: 600, rcs: 0.02 };
    const strength = (app.defs.units[own.type] || {}).radar_strength || 0;
    // `ring` is the discovery range against the brightest cross-section in the
    // game closing at v_ref (`defs.radar_range`) - 1368 m for a mech, 500 m for
    // an APC.  It is *not* the dial's scale: the face is a constant `RWR_RANGE`
    // and this number is the hull's own floor
    this.ring = Math.pow(Math.max(0, strength * radar.rcs), 0.25);
    const vRef = radar.v_ref || 600;
    const eye = [own.x, own.y + 5, own.z];
    // the round is measured against *this* hull's velocity, not the world's:
    // running at a missile lights the receiver earlier, running from it later.
    // All three axes - a hull following a slope has a real vy.
    const ovx = own.vx || 0, ovy = own.vy || 0, ovz = own.vz || 0;
    // the smoke the sight rule cares about: deployed puffs, not canisters
    const puffs = app.net.smoke ? app.net.smoke.filter((s) => !s.canister) : null;
    const now = performance.now() / 1000;
    const heard = this._heard;
    heard.clear();

    for (const p of app.net.projectiles) {
      if (p.faction === own.faction) continue;
      if (p.kind === 0) continue;                      // shells do not count
      const dx = p.x - eye[0], dy = p.y - eye[1], dz = p.z - eye[2];
      const d = Math.hypot(dx, dy, dz);
      if (d < 1 || d > RWR_RANGE) continue;            // outside the wire's tier
      const wd = app.defs.weapons[p.wid] || {};
      const v = Math.hypot(p.vx, p.vy, p.vz);
      if (v < 1) continue;
      // the relative vector, and the closing speed along it.  A round opening
      // out or crossing gives `v_c <= 0`, which fails every gate below by
      // itself - the same way the server's `radar_k` has no speed threshold
      const wx = p.vx - ovx, wy = p.vy - ovy, wz = p.vz - ovz;
      const vc = Math.min(vRef, -((dx * wx + dy * wy + dz * wz) / d));
      const k = Math.sqrt(Math.max(0, strength * (wd.rcs || 0))) / vRef;
      // heard: the alarm's own floor, 12 dB under the tracker's
      if (d * d > ALARM_GAIN * k * vc) continue;
      // one line of sight, the same door the AI's lookout, the IR lock and the
      // pre-aim box go through: terrain, a canopy or a screen of smoke hides a
      // round from the receiver exactly as it hides a hull
      if (sightBlocked(app.map, eye[0], eye[1], eye[2], p.x, p.y, p.z, puffs)) continue;

      // steady: the horizontal bearing has not been drifting.  Recorded for
      // every round the receiver can hear, whether or not it can place it, so a
      // fallback out of `seen` has a history to fall back on
      const az = Math.atan2(dz, dx);
      heard.add(p.id);
      const h = this._bearTrack(p.id, az, now);
      const rate = this._bearRate(h);
      const steady = Math.abs(rate) < ((wd.er || 0) + WARN_MARGIN) / k;

      // seen: the echo clears the tracker's floor - bearing, slant range and
      // closing speed, i.e. a position and a velocity vector
      const seen = d * d <= k * vc;

      // warn / aim: two rays out of the round, one tolerance.  `warn` runs the
      // ray along the *relative* velocity - the track as this hull's own motion
      // makes it, which is what a round aimed at a lead point produces; `aim`
      // runs it along the round's own, which is where the round is pointed right
      // now.  The tolerance is `er + WARN_MARGIN` up close and opens out with
      // range as `d_h * sin(WARN_CONE)` - a guided round 2 km out has a lateral
      // error it is still correcting
      const dh = Math.hypot(dx, dz);
      const tol = (wd.er || 0) + Math.max(WARN_MARGIN, dh * Math.sin(WARN_CONE));
      const warn = rayMiss(dx, dz, wx, wz) <= tol;
      const aim = rayMiss(dx, dz, p.vx, p.vz) <= tol;
      // the frame's verdict for this round: placed rounds are judged on both
      // geometries, a round only *heard* on its bearing drift - the one thing a
      // doppler echo can say about a round it cannot yet place
      const alarm = seen ? (warn || aim) : steady;
      this.threats.push({
        id: p.id, x: p.x, y: p.y, z: p.z, dist: d, kind: p.kind,
        seen, steady, warn, aim, alarm, wid: p.wid,
        hit_r: wd.hit_r || 0, vx: p.vx, vy: p.vy, vz: p.vz,
      });
    }
    this.threats.sort((a, b) => a.dist - b.dist);
    // the one boolean the dial's rim, the arrows and the tone read.  Recomputed
    // every frame, so a round that falls out of `seen` hands judgement straight
    // back to `steady`
    this.alarm = this.threats.some((t) => t.alarm) ? 1 : 0;
    // a round that left the snapshot, or was hidden, forgets its bearing
    if (this._bear.size) {
      for (const id of this._bear.keys()) {
        if (!heard.has(id)) this._bear.delete(id);
      }
    }

    this._lasers(app, own, eye, puffs);
  }

  // Laser warning: who has his sight *on me*.  Only a vehicle carries a laser
  // designator - a rifleman looking at the mech is not a threat the receiver is
  // meant to sing about.  For an AI hull the snapshot carries its current target,
  // so it has to name my mech - that is the truth, and it keeps the circle quiet
  // when a hundred hulls are just looking the other way.  A human opponent
  // exposes no target, so there the fallback is a tight cone on his sight line.
  _lasers(app, own, eye, puffs) {
    for (const e of app.net.entities.values()) {
      if (e.faction === own.faction) continue;
      if (e.type < 3 || e.type > 4) continue;           // apc / mech only
      const dx = eye[0] - e.x, dy = eye[1] - (e.y + 2.5), dz = eye[2] - e.z;
      const d = Math.hypot(dx, dy, dz);
      if (d > LASER_RANGE || d < 20) continue;
      let aimed = e.target === own.id;
      if (!aimed && e.owner !== 255) {                  // human pilot: cone test
        const yaw = e.type === 3 ? e.turret : e.yaw;
        const cp = Math.cos(e.pitch);
        const dot = (dx * cp * Math.cos(yaw) + dy * Math.sin(e.pitch) + dz * cp * Math.sin(yaw)) / d;
        aimed = dot > Math.cos(LASER_CONE);
      }
      if (!aimed) continue;
      // a laser is light: a ridge, a canopy or a screen of smoke between us is
      // the same wall it is for a round, so his sight line has to pass the one
      // sight rule before the receiver says a word about it
      if (sightBlocked(app.map, eye[0], eye[1], eye[2], e.x, e.y + 2.5, e.z, puffs)) continue;
      this.lasers.push({ x: e.x, z: e.z, dist: d, id: e.id });
    }
  }

  // The bearing history of one round: the last `BEAR_WIN` seconds of samples, the
  // angle unwrapped across the +/-PI seam so a drift through due west reads as a
  // drift and not as a 6.28 rad jump.  Every round the receiver can *hear* is
  // tracked, including the ones it can already place - a round that slows down
  // and falls out of `seen` has to find its history still there.
  _bearTrack(id, az, now) {
    let h = this._bear.get(id);
    if (!h) { h = { t: [], th: [] }; this._bear.set(id, h); }
    const n = h.th.length;
    const prev = n ? h.th[n - 1] : az;
    h.th.push(prev + Math.atan2(Math.sin(az - prev), Math.cos(az - prev)));
    h.t.push(now);
    // trimmed by *time*, not by a frame count: the frame rate decides how many
    // samples a window holds, and a `STEADY_T` verdict needs the window, not the
    // count
    while (h.t.length > 1 && now - h.t[0] > BEAR_WIN) { h.t.shift(); h.th.shift(); }
    while (h.t.length > BEAR_MAX) { h.t.shift(); h.th.shift(); }
    return h;
  }

  // The drift rate over the newest `STEADY_T` window.  `Infinity` means "still
  // measuring": a history shorter than the window is not a verdict yet, and
  // `steady` stays false until it is.  The window is read from the tail so a
  // round that has just stopped turning clears at once - a long average would
  // keep the old turn in the number and delay the recovery.
  _bearRate(h) {
    const n = h.th.length;
    if (n < 2) return Infinity;
    const tEnd = h.t[n - 1];
    let i = n - 2;
    while (i > 0 && tEnd - h.t[i] < STEADY_T) i--;
    const dt = tEnd - h.t[i];
    if (dt < STEADY_T || dt <= 0) return Infinity;   // still measuring
    return (h.th[n - 1] - h.th[i]) / dt;
  }

  // The threat arrows: the nearest three rounds on a course that reaches this
  // hull get a 3D arrow in the strip beside the warning dial, each pointing at
  // where its round actually is on screen - the pilot turns his sight that way
  // and it is there to shoot at.  Screen frame, unlike the dial's own hull frame,
  // because the arrow's whole job is "look there": the sight and the hull
  // disagree exactly when a pilot is craning round for an incoming round.  The
  // strip is the one the gear vacated when it moved into the corner.
  //
  // Runs after `aim`: it reads the eye the sight is mounted on.
  arrows(app) {
    const th = this.threats;
    const own = app.ownEntity();
    if (!own || !th || !th.length) return;
    // the camera's own three axes, taken from the same yaw and pitch the frame is
    // built with (shake aside - an arrow is not kicked by the gun): an arrow is
    // the round's direction as the pilot *sees* it, so a round on the crosshair
    // points away from him and shows its butt
    const f = dirFrom(app.input.viewYaw,
      clamp(app.input.pitch + app.input.freePitch * 0.8, -1.35, 1.35));
    const rl = Math.hypot(f[0], f[2]) || 1;
    const r = [-f[2] / rl, 0, f[0] / rl];              // screen right
    const u = [r[1] * f[2] - r[2] * f[1], r[2] * f[0] - r[0] * f[2],
      r[0] * f[1] - r[1] * f[0]];                      // screen up
    // the same sun the machine is lit by, folded into this frame
    const s = app.renderer.sun;
    app.renderer.overlaySun([
      s[0] * r[0] + s[1] * r[1] + s[2] * r[2],
      -(s[0] * u[0] + s[1] * u[1] + s[2] * u[2]),
      s[0] * f[0] + s[1] * f[1] + s[2] * f[2],
    ]);
    const eye = app.sightEye(own);
    // every arrow springs from the ring's centre, which is also the overlay's
    // projection origin: three rounds at three bearings are three needles of one
    // compass, not three rows of a list
    const at = arrowRingAt(app.renderer.canvas.clientWidth);
    app.renderer.overlayOrigin(at.x, at.y);
    let n = 0;
    for (const t of th) {
      if (!t.alarm) continue;
      if (n === RWR_ARROWS) break;
      const vx = t.x - eye[0], vy = t.y - eye[1], vz = t.z - eye[2];
      const l = Math.hypot(vx, vy, vz) || 1;
      app.renderer.screenArrow(
        (vx * r[0] + vy * r[1] + vz * r[2]) / l,
        -(vx * u[0] + vy * u[1] + vz * u[2]) / l,
        (vx * f[0] + vy * f[1] + vz * f[2]) / l,
        ARROW_LEN, ARROW_HEAD, ARROW_WID,
        ARROW_RGB[0], ARROW_RGB[1], ARROW_RGB[2], 0.96);
      n++;
    }
  }

  // two alarms with their own voices, both taken from the AN/ALR-67 RWR set: a
  // missile inbound trills fast (0.083 s per tone), a sight settling on the
  // cockpit warbles four times slower.  Both just loop for as long as the threat
  // holds, so they are driven every frame with a hold in the audio side that
  // keeps them from dropping out on a one-frame gap.
  //
  // The tone and the dial's rim read the *same* boolean, `alarm`: they blink and
  // trill on the same beat (`RWR_TONE_S`), and two channels answering one
  // question is the point - a pilot may have the alarm volume down, or be looking
  // somewhere else.  A round the receiver has placed is judged on its geometry;
  // one it only hears is judged on its bearing drift.  The gate upstream of both
  // is "can I receive it at all", which is the sight line and the wire's tier.
  alarms(app, own) {
    app.audio.missileWarning(!!own && this.alarm === 1);
    app.audio.laserWarning(!!own && this.lasers.length > 0);
  }
}
