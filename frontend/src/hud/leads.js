// Pre-aim: the hull that was picked up gets a white box, and each arm gets a
// circle on the point it has to be pointed at to hit it, joined to the box by a
// line in the arm's own colour.  The circles differ - a rocket is in the air far
// longer than a shell - so the pilot reads the ring, not the hull.
//
// With the aim assist on the circles are `LEAD_RING * ringScale` wide and are
// the thing the cross is caught by, so they double as pickers: a target's two
// rings are laid out as a pair on the line of their own two solutions
// (`game._leadLayout`), the one the cross is inside of is drawn at full weight,
// and where the two overlap each one keeps only the arc outside its partner -
// the pair is divided by a white line, `ringCut`.

import { ARM_COLORS, LEAD_COLOR, LEAD_RING, TAU } from './layout.js';

// The two cuts of one pair, reused: a target's rings are cut once per frame each
// and the frame is 16 ms long, so two objects serve every pair on the screen.
const CUT_A = { a0: 0, a1: 0, x0: 0, y0: 0, x1: 0, y1: 0 };
const CUT_B = { a0: 0, a1: 0, x0: 0, y0: 0, x1: 0, y1: 0 };
// and the pair of cuts itself, in `marks` order
const CUTS = [null, null];

export function leads(ctx, s) {
  const list = s.leads;
  if (!list || !list.length) return;
  ctx.save();
  ctx.lineWidth = 1.2;
  for (const t of list) {
    ctx.globalAlpha = 0.85;
    ctx.strokeStyle = LEAD_COLOR;
    ctx.strokeRect(t.x - t.hw, t.y - t.hh, t.hw * 2, t.hh * 2);
    // an overlapping pair is one shape cut in two: the arcs stop at the white
    // line, which is the same line the near-centre picker decides on.  One
    // cut per ring, in `marks` order.
    const pair = t.cut && t.marks.length === 2 ? t.marks : null;
    let cuts = null;
    if (pair) {
      CUTS[0] = ringCut(pair[0], pair[1], CUT_A);
      CUTS[1] = ringCut(pair[1], pair[0], CUT_B);
      cuts = CUTS;
    }
    for (let i = 0; i < t.marks.length; i++) {
      const m = t.marks[i];
      const col = ARM_COLORS[m.arm];
      const r = m.ring || LEAD_RING;
      const dx = m.x - t.x;
      const dy = m.y - t.y;
      const len = Math.hypot(dx, dy);
      // the colour is set before the leader is *decided on*, so a ring with no
      // leader (its centre is inside the box, i.e. a close-range target) is still
      // the arm's own - it used to inherit the box's white
      ctx.strokeStyle = col;
      // the leader runs from the box's *edge* to the ring's own centre: it starts
      // in the middle of the box and the part inside it is hidden, and it ends on
      // the ring's dot rather than on its rim - so a ring the box already sits in
      // is a short stub off the box, and never a chord shot through the middle
      // and out the far side.  A ring on the box's own centre has nothing to join
      // and simply has no leader; the ring itself is drawn either way, because a
      // missing ring reads as a missing arm
      if (len > 3) {
        const ux = dx / len;
        const uy = dy / len;
        const edge = Math.min(t.hw / Math.max(Math.abs(ux), 1e-6),
          t.hh / Math.max(Math.abs(uy), 1e-6));
        if (len > edge + 1) {
          ctx.globalAlpha = 0.55;
          ctx.lineWidth = 1.2;
          ctx.beginPath();
          ctx.moveTo(t.x + ux * edge, t.y + uy * edge);
          ctx.lineTo(m.x, m.y);
          ctx.stroke();
        }
      }
      ctx.globalAlpha = m.active ? 1 : 0.95;
      ctx.lineWidth = m.active ? 2 : 1.2;
      // the arc on this mark's own side of the line: it runs from one crossing
      // round the far side to the other, so the two arcs never double-stroke the
      // lens between them
      const own = cuts && cuts[i];
      ctx.beginPath();
      if (own) ctx.arc(m.x, m.y, r, own.a0, own.a1);
      else ctx.arc(m.x, m.y, r, 0, TAU);
      ctx.stroke();
      ctx.fillStyle = col;
      ctx.beginPath();
      ctx.arc(m.x, m.y, 1.2, 0, TAU);
      ctx.fill();
    }
    const line = cuts && cuts[0];
    if (line) {
      // the dividing line, drawn once for the pair and in white: it is the
      // boundary of what the cross catches, not part of either arm's colour.  It
      // is drawn *over* the box - the box eats the leaders, the pair's boundary
      // is the one line that has to reach the eye, and a target close enough for
      // its box to cover the lens is exactly where the two rings are hardest to
      // tell apart
      ctx.globalAlpha = 0.85;
      ctx.strokeStyle = '#ffffff';
      ctx.lineWidth = 1.2;
      ctx.beginPath();
      ctx.moveTo(line.x0, line.y0);
      ctx.lineTo(line.x1, line.y1);
      ctx.stroke();
    }
  }
  ctx.restore();
}

// Where two pre-aim rings of one target cross: the arc of `m` that lies outside
// `o` (as `a0` .. `a1`, the long way round the far side), and the two crossings
// themselves, which are the chord the white dividing line is drawn on.  Both
// rings are the same radius, so that chord is the perpendicular bisector of the
// two centres - exactly the set of points they are equally far from, i.e. the
// line the nearest-centre picker decides on.
//
// Pure geometry: it reads two marks and writes six numbers into `out` (a cached
// object at the call site, a fresh one in a test), and answers `null` when the
// two do not overlap - a pair that clears itself is two whole circles and has
// nothing to cut.
export function ringCut(m, o, out) {
  const r = m.ring || LEAD_RING;
  const dx = o.x - m.x;
  const dy = o.y - m.y;
  const d = Math.hypot(dx, dy);
  if (d >= r * 2 || d < 0.5) return null;
  const phi = Math.atan2(dy, dx);
  const al = Math.acos(Math.min(1, d / (2 * r)));
  const dst = out || { a0: 0, a1: 0, x0: 0, y0: 0, x1: 0, y1: 0 };
  dst.a0 = phi + al;
  dst.a1 = phi - al + Math.PI * 2;
  dst.x0 = m.x + r * Math.cos(phi + al);
  dst.y0 = m.y + r * Math.sin(phi + al);
  dst.x1 = m.x + r * Math.cos(phi - al);
  dst.y1 = m.y + r * Math.sin(phi - al);
  return dst;
}
