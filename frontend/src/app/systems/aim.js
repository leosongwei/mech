// The sight and the bore: where the pilot's cross points, where each arm's round
// would land, and where the sight has to be laid for the assist's ring.
//
// Two headings, and they are different things.  The *sight* is what the pilot
// looks along (or, with the assist holding a ring, the point that ring stands
// for); the *bore* is the hull's `yaw` and the arms' `pitch`, both from the
// server, both slewing after the sight.  Mid-flick the bore is behind the cross,
// and every marker here is drawn off the bore so it trails exactly as far as the
// machine does.
//
// One more split runs through the whole file: the *drawn* pose (`own.x/y/z`, a
// smoothing time constant behind the wire) is what the camera and the line of
// sight use, and the *fire* pose (`own.px/py/pz` walked on by velocity to the
// moment the server will actually fire) is what a solve starts from.  Aiming
// reads the second; the picture reads the first.

import { AIM_LINE, BASE_FOV, SNAP_HZ, TICK_HZ } from '../../core/config.js';
import { clamp, dirFrom } from '../../core/math.js';
import { LEAD_RING } from '../../hud/index.js';
import { leadPoint, zeroAt } from '../../core/fcs.js';
import { predictImpact, sightPoint, unitSpheres } from '../../map/index.js';

export class Aim {
  constructor(app) {
    this.app = app;
  }

  // The bore's end for one arm: the muzzle in the hull's own frame plus the reach
  // of whatever is bolted to it.  The server solves the same point from
  // `weapons.launch_geometry`.
  muzzle(own, arm, at = null) {
    const { app } = this;
    const mod = arm === 0 ? '2' : '3';
    const [ax, ay, az] = at || [own.x, own.y, own.z];
    const off = (app.defs.mech.muzzles && app.defs.mech.muzzles[mod]) || [0, 4.3, 0.9];
    // the bore ends at the mouth of whatever is bolted to the arm - a 4.1 m
    // cannon reaches a lot further than a 1.8 m nest - and it swings with the
    // arm, exactly like the server's `muzzle_point`
    const wd = (app.defs.weapons && app.defs.weapons[own.weapon ? own.weapon[arm] : 0]) || {};
    const reachTbl = app.defs.mech.reach || {};
    // where the bore ends, in the same order the server uses: the weapon's own
    // figure (`wd.reach`, the 105 mm's 6.3 m bore), then its rack's mouth of
    // tubes, then the kind's figure
    const reach = wd.reach !== undefined ? wd.reach
      : wd.rack ? wd.rack.mouth
        : reachTbl[wd.kind] === undefined ? off[2] : reachTbl[wd.kind];
    // the mouth is on the tube, and where the tube points is `defs.rack`'s
    // decision, not the arm's: bolted at its own quadrant (the top attack's
    // 42 deg), or carried on the arm with its elevation added to the aim -
    // flat, it is the arm itself.  The server solves the launch point from the
    // same spec.
    const spec = wd.rack || null;
    const elev = spec ? spec.elev || 0 : 0;
    const pitch = spec && spec.bolted ? elev : own.pitch + elev;
    const yaw = own.yaw;
    const c = Math.cos(yaw), s = Math.sin(yaw);
    const rx = -s, rz = c;
    const fwd = reach * Math.cos(pitch);
    return [
      ax + rx * off[0] + c * fwd,
      ay + off[1] + reach * Math.sin(pitch),
      az + rz * off[0] + s * fwd,
    ];
  }

  // How long after the newest snapshot the round actually leaves: half a snapshot
  // interval (the client holds the newest one, on average, that long), one more
  // one-way trip for the input to reach the server, and the tick the server takes
  // to read it.  The gun is laid from the hull's pose at *that* moment, so it is
  // the pose a fire-control solve has to start from.
  fireLag() {
    const { app } = this;
    const net = Number.isFinite(app.net.rttMs) ? 0.5 * app.net.rttMs / 1000 : 0;
    return 0.5 / SNAP_HZ + net + 0.5 / TICK_HZ;
  }

  // The hull's pose at that moment: the newest authoritative position
  // (`own.px/py/pz`, what the server says) walked on by its own velocity.
  firePose(own) {
    const t = this.fireLag();
    return [own.px + own.vx * t, own.py + own.vy * t, own.pz + own.vz * t];
  }

  // The origin every aiming readout is measured from: the eye sits in the
  // commander's sight head, which is bolted to the *hull* `defs.mech.sight_z`
  // behind the hull's own rotation axis (`mathutil.sight_eye` on the server, line
  // for line).  The head's own rotation about its pivot does not move it - the
  // head turns, the eye is the pivot - but the hull swinging underneath does:
  // that arc is the geometry the decoupled sight exists for, and it is why the
  // machine can be felt moving under a view that has already turned.
  sightEye(own, at = null) {
    const mech = this.app.defs.mech || {};
    const oz = mech.sight_z === undefined ? -0.62 : mech.sight_z;
    const [ax, ay, az] = at || [own.x, own.y, own.z];
    const c = Math.cos(own.yaw), s = Math.sin(own.yaw);
    return [
      ax + c * oz,
      ay + (mech.eye || 5.05),
      az + s * oz,
    ];
  }

  // Where the crew is *actually* looking - the value the wire carries as `yaw` /
  // `pitch`, which is what the server turns the machine and lays the fire control
  // by.  The pilot's view never moves: with the assist holding a ring the sight
  // is that ring's own aim point and the view is still his mouse, so the centre
  // cross and the white one come apart by the ring the guns are on.
  //
  // The picked ring is solved *again* here, off the pose the round will leave
  // from, rather than read off the last snapshot's answer: the hull, the muzzle
  // and the target all move in the 60-100 ms between the two, and at 600 m that
  // is metres of lead.  The ring on the glass keeps the snapshot's answer - it is
  // a picker, not the aim.
  sightAim() {
    const { app } = this;
    const a = app.aimSnap;
    if (!a) return { yaw: app.input.yaw, pitch: app.input.pitch };
    const own = app.ownEntity();
    const p = own ? this.ringSolve(own, a) : null;
    if (!p) return { yaw: app.input.yaw, pitch: app.input.pitch };
    a.yaw = p.yaw;
    a.pitch = p.pitch;
    return { yaw: p.yaw, pitch: p.pitch };
  }

  // One ring's aim point for the instant the round leaves: `leadPoint` from the
  // muzzle at the fire tick, against the target at the fire tick plus the round's
  // flight time.  A hull is read off its authority (`px/py/pz` plus its velocity);
  // a missile off the record the receiver keeps.
  ringSolve(own, a) {
    const { app } = this;
    const wd = app.defs.weapons[a.wid];
    if (!wd) return null;
    const lag = this.fireLag();
    const e = a.e;
    let tx, ty, tz, vx, vy, vz, ax = 0, az = 0;
    if (e) {
      if (!Number.isFinite(e.px)) return null;
      const half = ((app.defs.units[e.type] || {}).height || 3) * 0.5;
      // the pull `net` measured off two velocity samples, while it is fresh
      if (e.avt !== undefined && app.net.tick - e.avt <= 15) { ax = e.ax || 0; az = e.az || 0; }
      tx = e.px + e.vx * lag; ty = e.py + half; tz = e.pz + e.vz * lag;
      vx = e.mvx === undefined ? e.vx : e.mvx;
      vz = e.mvz === undefined ? e.vz : e.mvz;
      // a hull on a slope really is climbing: `vel.y` is on the wire and the
      // server's fire control leads with it, so the ring does too (a hull on the
      // flat reads 0 and nothing changes)
      vy = e.mvy === undefined ? e.vy : e.mvy;
    } else if (a.fly) {
      const f = a.fly;
      tx = f.x + (f.vx || 0) * lag; ty = f.y; tz = f.z + (f.vz || 0) * lag;
      vx = f.vx || 0; vz = f.vz || 0; vy = f.vy || 0;
    } else return null;
    const pose = this.firePose(own);
    const eye = this.sightEye(own, pose);
    const m = this.muzzle(own, a.arm, pose);
    const zero = app.init.zero || { range: 4000, max_elev: 1.05 };
    const p = leadPoint(wd, app.zeroRange, zero.max_elev,
      m[0], m[1], m[2], eye[0], eye[1], eye[2], tx, ty, tz, vx, vz, vy, ax, az);
    if (!p) return null;
    const dh = Math.hypot(p.x - eye[0], p.z - eye[2]);
    const mech = app.defs.mech || {};
    // the mount's own stops (`defs.mech.pitch_min/max`, the arms' envelope): a
    // solution the gun cannot be laid at is not one to snap the sight onto
    const lo = mech.pitch_min === undefined ? -0.45 : mech.pitch_min;
    const hi = mech.pitch_max === undefined ? 0.75 : mech.pitch_max;
    return {
      yaw: Math.atan2(p.z - eye[2], p.x - eye[0]),
      pitch: clamp(Math.atan2(p.y - eye[1], Math.max(dh, 1e-3)), lo, hi),
    };
  }

  // The frame's aiming pass: the centre cross's range and each arm's impact
  // marker.  Writes `app.crosshairs` and `app.sightRange`.
  run(own) {
    const { app } = this;
    app.crosshairs = [null, null];
    app.sightRange = null;
    if (!own) return;
    // two headings, and they are different things.  `dir` is the sight: the
    // cross, the range readout, the seeker ring, the beam a SACLOS missile
    // rides - the view, or the ring the aim assist has caught (`sightAim`).  The
    // arms are laid along the bore (`own.yaw` / `own.pitch`), which trails the
    // sight while the machine catches up - that is what each arm's marker is
    // predicted from below
    const sight = this.sightAim();
    const dir = dirFrom(sight.yaw, sight.pitch);
    const byaw = own.yaw;
    const bpitch = own.pitch;
    const bdir = dirFrom(byaw, bpitch);
    const units = [];
    for (const e of app.net.entities.values()) {
      // wrecks and corpses are inert: rounds fly straight through them, so the
      // sight ray and the impact prediction must ignore them too
      if (e.id === own.id || e.type === 7 || e.type === 8) continue;
      // per-sphere, exactly like the server's hit tests
      for (const s of unitSpheres(e.type, e.x, e.y, e.z, e.yaw, app.init.mech.hitspheres)) {
        units.push({ id: e.id, faction: e.faction, x: s.x, y: s.y, z: s.z, r: s.r });
      }
    }
    // the centre cross is the sight line: how far down it the first thing
    // (ground, wall, trunk, hull) sits - the mirror of the server's sight point,
    // out to the one range every setting shares
    const z0 = app.init.zero || { range: 4000, max_elev: 1.05 };
    const eye = this.sightEye(own);
    const sp = sightPoint(app.map, eye[0], eye[1], eye[2], dir, z0.range,
      units, own.id, own.faction);
    if (sp) {
      const d = Math.hypot(sp.x - eye[0], sp.y - eye[1], sp.z - eye[2]);
      if (d < z0.range) app.sightRange = d;
    }
    const fire = this.firePose(own);
    for (let arm = 0; arm < 2; arm++) {
      const wid = own.weapon[arm];
      const wd = app.defs.weapons[wid];
      const mod = arm === 0 ? 2 : 3;
      const disabled = own.hp[mod] <= 0;
      const m = this.muzzle(own, arm, fire);
      let point = null;
      let hit = false;
      let dist = 0;
      let far = false;
      if (wd && (wd.kind === 'bullet' || wd.kind === 'rocket')) {
        // fire control: the barrel is held off for the drop to the range the
        // sight is set to, exactly as the server fires it.  The elevation
        // depends on that setting and the hull's attitude only, so the marker
        // sits still across a crest line instead of dancing on the ridge.  bias
        // is absorbed by the solution, hence the copy with bias 0
        const z = app.init.zero || { range: 4000, max_elev: 1.05 };
        const theta = zeroAt(wd, byaw, bpitch, m[0], m[1], m[2], app.zeroRange, z.max_elev);
        const shot = theta === null ? wd : { ...wd, bias: 0 };
        point = predictImpact(app.map, shot, m[0], m[1], m[2],
          theta === null ? bdir : dirFrom(byaw, theta), units, {}, own.faction);
        hit = point.hit;
        dist = point.dist;
        // a setting the round cannot reach (or a shot that runs off the map)
        // leaves the marker on the bore line and the reading red
        far = !point.hit;
        if (theta === null) {
          point = { x: m[0] + bdir[0] * AIM_LINE, y: m[1] + bdir[1] * AIM_LINE,
            z: m[2] + bdir[2] * AIM_LINE };
        }
      } else if (wd) {
        // a guided round follows the *sight*, not the tube it left: the marker
        // sits on the cross even while the arms are still coming round
        const near = Math.min(wd.range, 2400);
        point = { x: m[0] + dir[0] * near, y: m[1] + dir[1] * near, z: m[2] + dir[2] * near, hit: false, dist: near };
        dist = near;
      }
      if (!point) continue;
      const scr = app.hud._project(point.x, point.y, point.z);
      // no marker for a barrel pointing behind the pilot: the old fallback put it
      // on the centre of the screen, which with a lagging bore would read as "on
      // target" for the moment a flick takes the arms off the view
      if (!scr) continue;
      app.crosshairs[arm] = { screen: scr, hit, dist, disabled, far };
    }
  }

  // The drawn radius of a pre-aim circle, px: the plain one, or the assist's
  // multiple of it (`assistRing`, the setting's slider).  Lives here because the
  // sight snap and the ring layout both need the same number.
  ringPx() {
    const { app } = this;
    return LEAD_RING * (app.aimAssist ? app.assistRing : 1);
  }

  // The optics' own field of view, smoothed: what the pre-aim boxes are sized
  // against.
  fov() {
    return clamp(BASE_FOV / this.app.zoom, 0.02, BASE_FOV);
  }
}
