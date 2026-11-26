// Target boxes and pre-aim rings: the target layer says which vehicles and
// guided rounds are known; the pre-aim layer says where each ballistic arm has
// to point.  They share one record so a target box remains useful without a
// ballistic weapon, while friendly targets never acquire rings or aim snaps.
//
// Three steps keep the two layers separate:
//
//   1. `update`  - per snapshot, in world space.  Build target records from the
//                  wire, then add one `leadPoint` per arm per enemy target.
//   2. `screen`  - per frame.  Project those world points and size the target
//                  boxes and rings.
//   3. `layout`  - per frame, screen space.  Lay out overlapping enemy rings and
//                  pick the ring under the centre cross (`aimSnap`).
//
// A ring is a *picker* (drawn where the eye can use it) and a pre-aim solution is
// a *point* (where the round has to go).  `wx/wy/wz` is the point; `x/y` is where
// it is drawn.

import { LEAD_TARGETS } from '../../core/config.js';
import { clamp } from '../../core/math.js';
import { LEAD_PAIR_SEP } from '../../hud/layout.js';
import { leadPoint } from '../../core/fcs.js';
import { sightBlocked } from '../../map/index.js';

export class Lead {
  constructor(app) {
    this.app = app;
    this.world = [];       // this snapshot's target records, in world space
    this.seq = -1;         // the snapshot they were computed for
    this.leads = [];       // the same, projected for this frame
    this.pairDir = new Map();   // per target: the line its two rings lie on
  }

  // Target records are built before looking at the arms.  Vehicles keep the
  // existing 25 m - 4 km and nearest-four rule; guided rounds keep the snapshot
  // tier.  Direct sight wins over a datalink bit; friendly rows carry the shared
  // position bit and use the same solid/dashed source rule.
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
    const eye = app.aim.sightEye(own);

    const cand = [];
    for (const e of app.net.entities.values()) {
      if (e.type !== 3 && e.type !== 4) continue;           // APC / MECH only
      const ud = app.defs.units[e.type] || {};
      const d = Math.hypot(e.x - own.x, e.z - own.z);
      if (d < 25 || d > zero.range) continue;              // out of the fire control
      const half = (ud.height || 3) * 0.5;
      if (!app.hud._project(e.x, e.y + half, e.z)) continue;
      cand.push({ e, ud, d, half });
    }
    cand.sort((a, b) => a.d - b.d);

    const targets = [];
    for (const c of cand.slice(0, LEAD_TARGETS)) {
      const e = c.e;
      const friendly = e.faction === own.faction;
      const blocked = sightBlocked(app.map,
        eye[0], eye[1], eye[2], e.x, e.y + c.half, e.z, puffs);
      const source = !blocked ? 'direct' : e.link ? 'datalink' : null;
      if (!source) continue;
      targets.push({
        id: e.id, kind: 'vehicle', faction: e.faction, source, src: e,
        x: e.x, y: e.y + c.half, z: e.z,
        rx: c.ud.radius || 2, ry: c.half, marks: [],
      });
    }

    // Guided rounds come from the snapshot, not the RWR's placed-threat list:
    // the receiver keeps its own hearing and alarm rules, while this layer draws
    // every known friendly round and every directly seen or linked enemy round.
    for (const p of app.net.projectiles) {
      if (p.kind !== 2 && p.kind !== 3) continue;
      if (!app.hud._project(p.x, p.y, p.z)) continue;
      const friendly = p.faction === own.faction;
      const blocked = sightBlocked(app.map,
        eye[0], eye[1], eye[2], p.x, p.y, p.z, puffs);
      const source = !blocked ? 'direct' : p.link ? 'datalink' : null;
      if (!source) continue;
      const wd = app.defs.weapons[p.wid] || {};
      targets.push({
        id: p.id, kind: 'missile', faction: p.faction, source,
        x: p.x, y: p.y, z: p.z, vx: p.vx, vy: p.vy, vz: p.vz,
        rx: wd.hit_r || 0, ry: wd.hit_r || 0, marks: [],
      });
    }
    this.world = targets;
    if (!targets.length) return;

    // Two poses, and the difference is the point of the whole solve.  The *eye*
    // is what the pilot sees and the line of sight is taken from, so it is the
    // drawn one; the *muzzle* is where the round will actually leave from, so it
    // is the fire-tick pose.  A ring solved from the drawn pose lands a metre
    // behind a driving pilot and a metre short of a moving hull.
    const arms = [];
    for (let arm = 0; arm < 2; arm++) {
      const wd = app.defs.weapons[own.weapon[arm]];
      if (own.hp[arm === 0 ? 2 : 3] <= 0) continue;
      // only rounds that fly ballistically need the lead: the SACLOS and the
      // top-attack missile steer themselves onto whatever the sight marks
      if (!wd || (wd.kind !== 'bullet' && wd.kind !== 'rocket')) continue;
      arms.push({ arm, wd });
    }
    if (!arms.length) return;
    const lag = app.aim.fireLag();
    const pose = app.aim.firePose(own);
    const aimEye = app.aim.sightEye(own, pose);
    for (const a of arms) a.m = app.aim.muzzle(own, a.arm, pose);

    for (const t of targets) {
      if (t.faction === own.faction) continue;
      for (const a of arms) {
        let p;
        if (t.kind === 'missile') {
          p = leadPoint(a.wd, app.zeroRange, zero.max_elev,
            a.m[0], a.m[1], a.m[2], aimEye[0], aimEye[1], aimEye[2],
            t.x, t.y, t.z, t.vx || 0, t.vz || 0, t.vy || 0);
        } else {
          const e = t.src;
          const half = t.ry;
          // The pull is trusted only while `net`'s two-sample measurement is
          // fresh; a held or tiered row falls back to velocity alone.
          const fresh = e.avt !== undefined && app.net.tick - e.avt <= 15;
          p = leadPoint(a.wd, app.zeroRange, zero.max_elev,
            a.m[0], a.m[1], a.m[2], aimEye[0], aimEye[1], aimEye[2],
            e.px + e.vx * lag, e.py + half, e.pz + e.vz * lag,
            e.mvx === undefined ? e.vx : e.mvx, e.mvz === undefined ? e.vz : e.mvz,
            e.mvy === undefined ? e.vy : e.mvy, fresh ? e.ax : 0, fresh ? e.az : 0);
        }
        if (p) t.marks.push({ arm: a.arm, x: p.x, y: p.y, z: p.z, tof: p.tof });
      }
    }
  }

  // Put the target boxes and pre-aim solutions on the screen as it stands this
  // frame.  The target metadata survives projection unchanged; only enemy
  // records carry marks into layout.
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
      // A missile's target box is sized by its own ball (`hit_r`, carried in
      // `rx`) and has a floor of its own: a 0.08 m round is well under a pixel
      // at a kilometre, and a mark too small to see is not useful.
      const hw = clamp(t.rx * pxm, t.kind === 'missile' ? 8 : 6, 70);
      const out = {
        id: t.id, kind: t.kind, faction: t.faction, source: t.source,
        src: t.src || null,
        x: box[0], y: box[1],
        hw,
        hh: t.kind === 'missile' ? hw : clamp(t.ry * pxm, 5, 70),
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
      if (!t.marks.length) continue;
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
      if (!t.marks.length) continue;
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
