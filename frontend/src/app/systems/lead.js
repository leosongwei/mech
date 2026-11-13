// The pre-aim rings: where each arm's round has to be aimed to hit a hull on the
// move, drawn as a picker the pilot can put the cross inside of.
//
// Three steps, and the split between them is what makes the rings usable:
//
//   1. `update`  - per snapshot, in world space.  Which hulls are worth a ring,
//                  which arm gets which lead, one `leadPoint` per arm per
//                  target.  Runs only when the snapshot sequence moves.
//   2. `screen`  - per frame.  Projects those world points, sizes the box, asks
//                  `layout` where each ring is *drawn*.
//   3. `layout`  - per frame, screen space.  Pairs that would overlap are laid
//                  out as a pair on the line their own two solutions make, and
//                  the ring under the centre cross is picked: that pick is the
//                  sight (`aimSnap`).
//
// The split exists because a ring is a *picker* (drawn where the eye can use it)
// and a pre-aim solution is a *point* (where the round has to go).  `wx/wy/wz`
// is the point; `x/y` is where it is drawn.

import { LEAD_TARGETS } from '../../core/config.js';
import { clamp } from '../../core/math.js';
import { LEAD_PAIR_SEP } from '../../hud/index.js';
import { leadPoint } from '../../core/fcs.js';
import { sightBlocked } from '../../map/index.js';

export class Lead {
  constructor(app) {
    this.app = app;
    this.world = [];       // this snapshot's solutions, in world space
    this.seq = -1;         // the snapshot they were computed for
    this.leads = [];       // the same, projected for this frame
    this.pairDir = new Map();   // per target: the line its two rings lie on
  }

  // The boxes a pilot lines the sights up with to hit a hull on the move: the
  // point each arm's round has to be aimed at.  The sight is zeroed to a fixed
  // range, so the round needs `range / vel` seconds to reach anything it is laid
  // on and the lead is the hull's own motion over that time - put the centre
  // cross on the box at that range and the shot lands.  A gun and a rocket pod
  // need very different leads, so every arm gets its own box.  Recomputed per
  // snapshot and kept in world space (`world`), so the box stays glued to the
  // hull while the pilot slews the sight.
  update(own) {
    const { app } = this;
    if (app.net.seq === this.seq) return;
    this.seq = app.net.seq;
    this.world = [];
    if (!own || !app.map) return;
    const zero = app.init.zero || { range: 4000, max_elev: 1.05 };
    // the smoke that is actually up: a canister still in the air is a charge,
    // not a screen (the server only ever tests deployed puffs)
    const puffs = app.net.smoke.filter((s) => !s.canister);
    // two poses, and the difference is the point of the whole solve.  The *eye*
    // is what the pilot sees and the line of sight is taken from, so it is the
    // drawn one; the *muzzle* is where the round will actually leave from, so it
    // is the fire-tick pose.  A ring solved from the drawn pose lands a metre
    // behind a driving pilot and a metre short of a moving hull - at 600 m, both
    // of them a miss.
    const lag = app.aim.fireLag();
    const eye = app.aim.sightEye(own);
    const pose = app.aim.firePose(own);
    const aimEye = app.aim.sightEye(own, pose);

    const arms = [];
    for (let arm = 0; arm < 2; arm++) {
      const wd = app.defs.weapons[own.weapon[arm]];
      if (own.hp[arm === 0 ? 2 : 3] <= 0) continue;
      // only rounds that fly ballistically need the lead: the SACLOS and the
      // top-attack missile steer themselves onto whatever the sight marks
      if (!wd || (wd.kind !== 'bullet' && wd.kind !== 'rocket')) continue;
      arms.push({ arm, wd, m: app.aim.muzzle(own, arm, pose) });
    }
    // A missile the radar has *seen* gets the same treatment as a hull: a box on
    // the ball it is, and one ring per arm at the point the round has to be aimed
    // at.  It is the one target that cannot be hit by aiming where it *is* - a
    // round crosses 20 m in a tick and the fuse arm is 4 m - so the lead matters
    // more here than anywhere.  Same line of sight rule as the hulls below: a
    // round behind a ridge or a screen of smoke is not boxed.
    for (const t of app.rwr.threats || []) {
      if (!t.seen || !(t.hit_r > 0)) continue;
      if (sightBlocked(app.map, eye[0], eye[1], eye[2], t.x, t.y, t.z, puffs)) continue;
      const m = { id: t.id, ball: true, x: t.x, y: t.y, z: t.z,
                  rx: t.hit_r, ry: t.hit_r, marks: [] };
      for (const a of arms) {
        const p = leadPoint(a.wd, app.zeroRange, zero.max_elev,
          a.m[0], a.m[1], a.m[2], aimEye[0], aimEye[1], aimEye[2], t.x, t.y, t.z,
          t.vx || 0, t.vz || 0, t.vy || 0);
        if (p) m.marks.push({ arm: a.arm, x: p.x, y: p.y, z: p.z, tof: p.tof });
      }
      this.world.push(m);
    }
    if (!arms.length) return;

    // Enemy hulls on screen, nearest first: the four closest get a box, the rest
    // is clutter.  Every one of them gets a ring, moving or not: with a fixed
    // zero the ring no longer means "lead the target" but "put the centre cross
    // here", and a standing hull 2 km out needs just as much of a lift as a
    // driving one needs lead.
    const cand = [];
    for (const e of app.net.entities.values()) {
      if (e.type !== 3 && e.type !== 4) continue;           // APC / MECH only
      if (e.faction === own.faction) continue;
      const ud = app.defs.units[e.type] || {};
      const d = Math.hypot(e.x - own.x, e.z - own.z);
      if (d < 25 || d > zero.range) continue;              // out of the fire control
      if (!app.hud._project(e.x, e.y + (ud.height || 3) * 0.5, e.z)) continue;
      cand.push({ e, ud, d });
    }
    if (!cand.length) return;
    cand.sort((a, b) => a.d - b.d);

    for (const c of cand.slice(0, LEAD_TARGETS)) {
      const e = c.e;
      const half = (c.ud.height || 3) * 0.5;               // hull centre above the ground
      // no line of sight, no box: a hull behind a ridge, a building, a tree stand
      // or a screen of smoke is one the seeker cannot lock either, and a marker
      // floating on it would be a wallhack.  Same rule the server's AI and IR
      // seeker obey.
      if (sightBlocked(app.map, eye[0], eye[1], eye[2], e.x, e.y + half, e.z, puffs)) continue;
      const t = {
        id: e.id, src: e,
        x: e.x, y: e.y + half, z: e.z,
        rx: c.ud.radius || 2, ry: half,
        marks: [],
      };
      for (const a of arms) {
        // the lead is solved against the hull's real velocity, which is what the
        // wire carries (`vel`); `mv*` is the same number smoothed.  The pull is
        // `ax` / `az`, the acceleration `net` measures off two velocity samples:
        // a hull still accelerating into the shot, or braking out of one, is
        // further on (or shorter) than its speed alone says by the time the round
        // arrives.  A measurement older than half a second says nothing about
        // now - a hull that was tiered out or held off the wire is ringed at its
        // velocity alone.
        const fresh = e.avt !== undefined && app.net.tick - e.avt <= 15;
        const p = leadPoint(a.wd, app.zeroRange, zero.max_elev,
          a.m[0], a.m[1], a.m[2], aimEye[0], aimEye[1], aimEye[2],
          e.px + e.vx * lag, e.py + half, e.pz + e.vz * lag,
          e.mvx === undefined ? e.vx : e.mvx, e.mvz === undefined ? e.vz : e.mvz,
          e.mvy === undefined ? e.vy : e.mvy, fresh ? e.ax : 0, fresh ? e.az : 0);
        if (p) t.marks.push({ arm: a.arm, x: p.x, y: p.y, z: p.z, tof: p.tof });
      }
      // the box stands on its own: a hull can be in view while the point to aim
      // at is off the edge of the screen
      this.world.push(t);
    }
  }

  // Put the pre-aim solutions on the screen as it stands this frame.
  screen(own) {
    const { app } = this;
    this.leads = [];
    app.leads = this.leads;
    app.aimSnap = null;
    if (!own || !this.world.length) return;
    const tanHalf = Math.tan(app.aim.fov() * 0.5);
    const eyeY = own.y + ((app.defs.mech && app.defs.mech.eye) || 5.05);
    const ring = app.aim.ringPx();
    for (const t of this.world) {
      const box = app.hud._project(t.x, t.y, t.z);
      if (!box) continue;
      const dist = Math.hypot(t.x - own.x, t.y - eyeY, t.z - own.z);
      const pxm = app.hud.h / (2 * Math.max(dist, 1) * tanHalf);   // pixels per metre
      // a missile's box is sized by its own ball (`hit_r`, carried in `rx`) and
      // has a floor of its own: a 0.08 m round is well under a pixel at a
      // kilometre, and a mark too small to see is not a pre-aim box
      const hw = clamp(t.rx * pxm, t.ball ? 8 : 6, 70);
      const out = {
        id: t.id, src: t.src || null,
        x: box[0], y: box[1],
        hw,
        hh: t.ball ? hw : clamp(t.ry * pxm, 5, 70),
        dist,
        marks: [],
      };
      for (const m of t.marks) {
        const scr = this.markerScreen(m);
        // the mark keeps both ends: where the ring is drawn (a picker, with the
        // assist on) and the point it stands for - the one the sight is snapped
        // to, so a ring moved aside is still the honest answer it always was
        if (scr) out.marks.push({ arm: m.arm, x: scr[0], y: scr[1], tof: m.tof,
          ring, wx: m.x, wy: m.y, wz: m.z });
      }
      this.leads.push(out);
    }
    this.layout(own);
  }

  // Where a pre-aim ring is drawn.  Its own point when that lands in the frame,
  // and clamped inside the frame - one ring radius of margin - when it does not: a
  // lead is metres to hundreds of metres away from the hull (a rocket pod at 2 km
  // flies for 4-5 s) and a zoomed sight sees a narrow slice of the world, so the
  // answer to "where do I point this arm" is regularly off the screen.  The ring is
  // still drawn there, in the direction it lies, because a ring dropped for being
  // out of frame takes the pair with it: one ring left, no white line, nothing
  // saying which arm the survivor is.  What the ring *stands for* stays the true
  // point (`wx`/`wy`/`wz`), so the picker and the snap are unaffected.
  //
  // A point behind the camera has no place on the screen at all, and answers
  // `null` - the box it belongs to is off the back of the sight by then.
  markerScreen(m) {
    const { app } = this;
    const scr = app.hud._project(m.x, m.y, m.z);
    if (scr) return scr;
    const vp = app.hud.vp;
    if (!vp) return null;
    const cx = vp[0] * m.x + vp[4] * m.y + vp[8] * m.z + vp[12];
    const cy = vp[1] * m.x + vp[5] * m.y + vp[9] * m.z + vp[13];
    const cw = vp[3] * m.x + vp[7] * m.y + vp[11] * m.z + vp[15];
    if (cw <= 0.01) return null;
    const w = app.hud.w, h = app.hud.h;
    const pad = app.aim.ringPx() + 8;
    const x = (cx / cw * 0.5 + 0.5) * w;
    const y = (1 - (cy / cw * 0.5 + 0.5)) * h;
    return [clamp(x, pad, Math.max(pad, w - pad)),
      clamp(y, pad, Math.max(pad, h - pad))];
  }

  // The assist's two jobs, both in screen space because both are about what the
  // pilot can put the cross on.
  //
  // * **Layout**: a target's two rings, when they would overlap, are laid out as
  //   a pair on the line their own two solutions make - each ring on its own
  //   arm's side of the middle of it, and never closer in than `LEAD_PAIR_SEP`
  //   radii, so the pair *overlaps* instead of standing tangent (`hud._ringCut`
  //   cuts the lens between them on a white line).  Two radii out to the side is
  //   a long way to swing a cross to shoot one arm's lead, and the overlap still
  //   reads as two circles: the picker is the bisector of the two centres, which
  //   is the line they are drawn cut on.  The pair rides the true points instead
  //   of a fixed screen direction: it is the line between the two solutions that
  //   decides where they sit, so a fast target - an incoming round's lead swings
  //   metres between two frames - cannot make the pair jump from one arrangement
  //   to the other.  The rings are pickers from then on; the points themselves
  //   are `wx` / `wy` / `wz`.
  // * **Selection**: whichever ring the centre cross is inside of is the one
  //   caught - and the nearest hull wins when two of them overlap - and its own
  //   aim point becomes the sight (`sightAim`), so the guns are laid on the
  //   target while the view stays where the pilot put it.
  //
  // A cross that is not the sight catches nothing: ALT holds the view still and
  // pans it freely (`input.freeYaw`), and a panel takes the pointer altogether, so
  // in both the ring under the middle of the screen is not what the guns are being
  // pointed with.
  layout(own) {
    const { app } = this;
    if (!app.aimAssist || !this.leads.length) return;
    if (app.input.alt || app.input.uiOpen) return;
    const ring = app.aim.ringPx();
    const sep = ring * LEAD_PAIR_SEP;       // the closest the two centres get
    const prev = this.pairDir;
    const next = new Map();
    for (const t of this.leads) {
      const [a, b] = t.marks;
      if (!b) continue;
      const dx = b.x - a.x, dy = b.y - a.y;
      const d = Math.hypot(dx, dy);
      // a pair that already clears itself is drawn where its solutions are
      if (d >= ring * 2) continue;
      // the line the pair lies on: the two solutions themselves.  Below a
      // fraction of a pixel their difference is noise, and the last frame's
      // direction is the honest answer - it is the one the pilot is reading
      let ux = 1, uy = 0;
      if (d > ring * 0.05) { ux = dx / d; uy = dy / d; }
      else if (prev && prev.has(t.id)) [ux, uy] = prev.get(t.id);
      next.set(t.id, [ux, uy]);
      // the pair is cut on its white line only once it is drawn overlapping
      t.cut = true;
      const half = Math.max(d, sep) / 2;
      const mx = (a.x + b.x) / 2, my = (a.y + b.y) / 2;
      a.x = mx - ux * half; a.y = my - uy * half;
      b.x = mx + ux * half; b.y = my + uy * half;
    }
    this.pairDir = next;
    const cx = app.hud.w / 2, cy = app.hud.h / 2;
    let best = null;
    for (const t of this.leads) {
      const [a, b] = t.marks;
      if (!a) continue;
      let hit = null;
      if (b) {
        // a laid-out pair is one stadium: the two circles and the band round the
        // point they touch, which is where a pilot aiming at the hull's own box
        // puts the cross - so the nearer circle centre of the pair is picked
        const vx = b.x - a.x, vy = b.y - a.y;
        const l2 = vx * vx + vy * vy || 1;
        const u = clamp(((cx - a.x) * vx + (cy - a.y) * vy) / l2, 0, 1);
        const dx = a.x + vx * u - cx, dy = a.y + vy * u - cy;
        if (Math.hypot(dx, dy) <= ring) {
          const da = Math.hypot(a.x - cx, a.y - cy);
          const db = Math.hypot(b.x - cx, b.y - cy);
          hit = da <= db ? a : b;
        }
      } else if (Math.hypot(a.x - cx, a.y - cy) <= ring) {
        hit = a;
      }
      // a target that was ringed before wins over the one behind it
      if (hit && (!best || t.dist < best.t.dist)) best = { t, m: hit };
    }
    if (!best) return;
    best.m.active = true;
    // what is held is the *pick*, not its solution: the aim is solved again every
    // input frame (`ringSolve`), off the pose the round will leave from.
    // `x/y/z` stays the ring's own world point - that is what the HUD draws.
    app.aimSnap = {
      x: best.m.wx, y: best.m.wy, z: best.m.wz,
      arm: best.m.arm, id: best.t.id,
      wid: own.weapon[best.m.arm],
      e: best.t.src || null,          // a hull, or a missile's own record
      fly: best.t.src ? null : best.t,
    };
  }
}
