// Where the guns are pointed: the centre cross, the arm brackets, the seeker
// rings and the marker on the hull that is being tracked.
//
// Three crosses live on this screen and they are three different questions.  The
// saltire in the middle is where the pilot *looks*; the brackets are where each
// arm's bore is actually laid, an instant behind it; and the white `+` is where
// the guns are when the aim assist has taken the sight off the view.  So the
// reticle is never one mark - the lag between them is the machine.

import { ARM_COLORS, ARM_NAMES, FAR_COLOR, TAU } from './layout.js';
import { circle, dot, font } from './canvas.js';

// Which arm owns the seeker, asked once and read by both things that need it:
// the lock marker's colour (`lock`) and whether the two seeker rings stagger to
// their own hands (`crosshairs`).  `arm` is 1 only when the right hand is the
// one carrying a seeker; `both` says the two hands are carrying the same kind of
// tube, which is the one case the rings would be drawn exactly on top of each
// other.
const OWNER = { arm: 0, both: false };

export function armOwner(s) {
  OWNER.arm = 0;
  OWNER.both = false;
  const own = s.own;
  const defs = s.weapons;
  if (!own || !defs) return OWNER;
  const w0 = defs[own.weapon[0]];
  const w1 = defs[own.weapon[1]];
  const l0 = !!(w0 && w0.lock);
  const l1 = !!(w1 && w1.lock);
  if (l1 && !l0) OWNER.arm = 1;
  OWNER.both = l0 && l1;
  return OWNER;
}

// The white cross the assist needs: the centre cross is where the pilot looks,
// this one is where the guns are actually laid.  The two come apart exactly when
// the assist has caught a ring - the sight snaps, the view does not - so it is
// drawn only while that is happening, in the centre cross's own weight and
// white, a size smaller, and square: an `x` on the middle of the screen and a
// `+` where the guns are, never two of the same mark.
export function aimCross(ctx, s) {
  if (!s.aim) return;
  const x = s.aim.x;
  const y = s.aim.y;
  const r = 5;
  ctx.save();
  ctx.strokeStyle = '#ffffff';
  ctx.lineWidth = 1.5;
  ctx.globalAlpha = 0.9;
  ctx.beginPath();
  ctx.moveTo(x, y - r);
  ctx.lineTo(x, y + r);
  ctx.moveTo(x - r, y);
  ctx.lineTo(x + r, y);
  ctx.stroke();
  ctx.restore();
}

// The gun line proper: a white saltire fixed in the middle of the sight, the
// same size as the pre-aim ring so the two read as one system.  The number above
// it is the range the cross is pointing at - the sight ray's first contact, out
// to the 4 km the settings share; past that there is nothing to range against
// and it shows a red dash.
export function centreCross(ctx, s, w, h) {
  if (s.settingsOpen || s.shopOpen) return;
  const x = w / 2;
  const y = h / 2;
  const r = 9;
  const range = s.sightRange;
  const far = range === null || range === undefined;
  ctx.save();
  ctx.strokeStyle = '#ffffff';
  ctx.lineWidth = 1.7;
  ctx.beginPath();
  ctx.moveTo(x - r, y - r);
  ctx.lineTo(x + r, y + r);
  ctx.moveTo(x + r, y - r);
  ctx.lineTo(x - r, y + r);
  ctx.stroke();
  ctx.textAlign = 'center';
  font(ctx, 11, false);
  ctx.fillStyle = far ? FAR_COLOR : '#ffffff';
  ctx.fillText(far ? '-' : `${Math.round(range)}m`, x, y - r - 6);
  ctx.restore();
}

// The two arm reticles.  A gun's is a bracket pair with its own impact dot and a
// `L/R <range>m` reading; a launcher's is the seeker ring, because that reticle
// *is* the lock circle - anything inside it can be tracked, and a hull that
// leaves it drops the lock.
export function crosshairs(ctx, s, w, h) {
  const owner = armOwner(s);
  for (let arm = 0; arm < 2; arm++) {
    const c = s.crosshairs[arm];
    if (!c) continue;
    const wid = s.own ? s.own.weapon[arm] : 0;
    const wd = (s.weapons && s.weapons[wid]) || {};
    if (wd.lock) {
      // with a missile in each arm the two rings are the same size and stagger
      // to their own side, each in its own hand's colour, so the pilot can tell
      // which tube is tracking
      seekerRing(ctx, s, c, arm, w, owner.both ? (arm === 0 ? -0.4 : 0.4) : 0);
      continue;
    }
    const col = ARM_COLORS[arm];
    ctx.save();
    ctx.strokeStyle = col;
    ctx.fillStyle = col;
    ctx.lineWidth = 1.4;
    ctx.globalAlpha = c.disabled ? 0.25 : 0.95;
    const x = c.screen[0];
    const y = c.screen[1];
    const r = 9;
    // brackets
    ctx.beginPath();
    ctx.moveTo(x - r - 6, y);
    ctx.lineTo(x - r, y);
    ctx.moveTo(x + r, y);
    ctx.lineTo(x + r + 6, y);
    ctx.moveTo(x, y - r - 6);
    ctx.lineTo(x, y - r);
    ctx.moveTo(x, y + r);
    ctx.lineTo(x, y + r + 6);
    ctx.stroke();
    dot(ctx, x, y, 1.4);
    // impact point
    if (c.hit) {
      ctx.globalAlpha = 0.55;
      circle(ctx, x, y, 3.4);
      ctx.stroke();
    }
    ctx.globalAlpha = 0.85;
    font(ctx, 10, false);
    // both reticles sit on the screen centre when the sight is level: push the
    // right-arm label down so the two readings never overlap
    const other = s.crosshairs[1 - arm];
    const stacked = other && Math.abs(other.screen[0] - x) < 80
      && Math.abs(other.screen[1] - y) < 24;
    const ly = y + 4 + (stacked && arm === 1 ? 18 : 0);
    // past 4 km the sight ray finds nothing to range against: rangefinding
    // drops out and the reading is a bare red dash
    if (c.far) ctx.fillStyle = FAR_COLOR;
    const read = c.hit && !c.far ? `${Math.round(c.dist)}m` : '-';
    ctx.fillText(`${ARM_NAMES[arm]} ${read}`, x + r + 10, ly);
    ctx.restore();
  }
}

// 5 % of the view width across: the seeker's field of view.  The ring keeps its
// arm colour (cyan left, amber right) in every state - a lock is shown by the
// thicker rim, the diamond and the full progress arc.  `shift` slides the ring
// sideways, in radii, when both arms carry a seeker.
export function seekerRing(ctx, s, c, arm, w, shift) {
  const r = 0.5 * (s.lockFrac || 0.05) * w;
  const x = c.screen[0] + (shift || 0) * r;
  const y = c.screen[1];
  const col = ARM_COLORS[arm];
  const L = s.lock;
  const p = L ? Math.max(0, Math.min(1, L.progress)) : 0;
  const locked = p >= 1;
  const held = L && p > 0;
  ctx.save();
  // idle rim
  ctx.globalAlpha = c.disabled ? 0.2 : 0.55;
  ctx.strokeStyle = col;
  ctx.lineWidth = 1.3;
  circle(ctx, x, y, r);
  ctx.stroke();
  if (held) {
    // acquisition progress runs round the rim, in the arm's own colour
    ctx.globalAlpha = c.disabled ? 0.2 : 1;
    ctx.strokeStyle = col;
    ctx.lineWidth = locked ? 3.2 : 2.4;
    ctx.beginPath();
    ctx.arc(x, y, r, -Math.PI / 2, -Math.PI / 2 + p * TAU);
    ctx.stroke();
  }
  ctx.globalAlpha = c.disabled ? 0.2 : 0.95;
  ctx.fillStyle = col;
  dot(ctx, x, y, 1.5);
  if (locked) {
    ctx.beginPath();
    ctx.moveTo(x, y - 6);
    ctx.lineTo(x + 6, y);
    ctx.lineTo(x, y + 6);
    ctx.lineTo(x - 6, y);
    ctx.closePath();
    ctx.fill();
  }
  ctx.globalAlpha = c.disabled ? 0.25 : 0.85;
  ctx.fillStyle = col;
  font(ctx, 10, false);
  const label = L && L.dist ? `${Math.round(L.dist)}m` : '-';
  ctx.fillText(`${ARM_NAMES[arm]} ${label}`, x + r + 8, y + (arm === 0 ? -4 : 16));
  ctx.restore();
}

// The tracked hull: a small marker on the target itself, in the colour of the
// seeker that is tracking it, so the pilot can tell which vehicle was picked
// from inside the circle.
export function lock(ctx, s) {
  const L = s.lock;
  if (!L || !L.screen) return;
  const x = L.screen[0];
  const y = L.screen[1];
  const p = Math.max(0, Math.min(1, L.progress));
  const locked = p >= 1;
  const arm = armOwner(s).arm;
  ctx.save();
  ctx.strokeStyle = ARM_COLORS[arm];
  ctx.lineWidth = locked ? 2 : 1.3;
  ctx.globalAlpha = 0.9;
  const m = locked ? 9 : 7;
  ctx.beginPath();
  ctx.moveTo(x, y - m);
  ctx.lineTo(x + m, y);
  ctx.lineTo(x, y + m);
  ctx.lineTo(x - m, y);
  ctx.closePath();
  ctx.stroke();
  ctx.restore();
}
