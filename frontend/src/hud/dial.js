// Warning circle, in the top right corner: a bird's eye of everything the threat
// receiver holds, drawn in the hull's own frame - straight up is where the mech
// is looking.  The face is a **fixed 3 km** for every machine, so a mark's
// distance from the middle is its real distance and two pilots read the same
// dial.  That number is the snapshot's guided tier (`TIER_ROUND`): a round
// further out than the wire carries cannot be drawn, so the face is the
// receiver's display ceiling rather than its sensitivity.
//
// Four marks, four answers, and which one you get is *what the receiver knows*,
// not a style:
//   * a red **dot** at the true bearing and true range - the doppler return
//     cleared the tracking floor (`seen`), so the round has a position;
//   * a blinking **spoke** out from the middle on the bearing only - the round
//     was heard but never placed, so there is no range to draw.  A bearing is
//     not a track, and the spoke has one look whether or not the receiver thinks
//     it is converging: that answer is the rim's job.
//   * a red **triangle** at the true position with a **dashed** beam running out
//     to it - somebody's laser sight is on this hull and he is inside 3 km;
//   * the same triangle **pinned to the rim** when he is further out than that:
//     the bearing is real, the distance is "3 km or more".
//
// The rim blinks on the alarm, and the alarm is computed once in
// `app/systems/rwr.js` (`radarAlarm`): a placed round (`seen`) is judged on the
// two horizontal rays it owns - where its track runs once this hull's motion is
// taken out, and where its own nose points - and a heard one on how steadily its
// bearing is tracking this hull.  The trill reads the same boolean, so the eye
// and the ear count the same beats.

import { FAR_COLOR, RADAR_PX, RING_ALARM_W, TAU, arrowRingAt, dialAt } from './layout.js';
import { font } from './canvas.js';
import { RWR_TONE_S } from '../audio/levels.js';

export function dial(ctx, s, w) {
  const R = RADAR_PX;
  // metres at the edge of the dial: a constant, not the machine's own discovery
  // range - that is a number the receiver *earns* (an echo), and it is drawn as
  // one by whether a contact has a position at all
  const ring = s.radarScale || 3000;
  const centre = dialAt(w);
  const cx = centre.x;
  const cy = centre.y;
  const own = s.own;
  const roundList = s.threats || [];
  const laserList = s.lasers || [];
  ctx.save();
  // The arrow ring first, the dial's twin on the left: same radius, same height,
  // and the 3D arrows are drawn inside it by the renderer (in these same CSS
  // pixels).  A thin dark wash keeps them readable over a bright sky without
  // painting a second hole in the picture.
  arrowsRing(ctx, arrowRingAt(w), R, s.radarAlarm === 1);
  // a black disc under the dial: the marks have to read against any sky or
  // ground, so the receiver owns that patch of the screen
  ctx.globalAlpha = 1;
  ctx.fillStyle = '#000';
  ctx.beginPath();
  ctx.arc(cx, cy, R + 3, 0, TAU);
  ctx.fill();
  // dial
  ctx.globalAlpha = 0.5;
  ctx.strokeStyle = roundList.some((t) => t.warn || t.aim)
    ? 'rgba(255,90,80,0.85)' : 'rgba(150,190,175,0.5)';
  ctx.lineWidth = 1.2;
  ctx.beginPath();
  ctx.arc(cx, cy, R, 0, TAU);
  ctx.stroke();
  ctx.globalAlpha = 0.22;
  ctx.beginPath();
  ctx.arc(cx, cy, R * 0.5, 0, TAU);
  ctx.stroke();
  // heading tick at the top: "up is forward"
  ctx.globalAlpha = 0.6;
  ctx.beginPath();
  ctx.moveTo(cx, cy - R - 5);
  ctx.lineTo(cx, cy - R + 2);
  ctx.stroke();
  font(ctx, 9, false);
  ctx.fillStyle = 'rgba(160,200,185,0.7)';
  // the scale under the heading tick: the face is a constant 3 km, so this label
  // is too ("3.0 km")
  ctx.fillText(`${(ring / 1000).toFixed(1)} km`, cx - 11, cy - R - 8);
  // laser designators: bearing 0 is straight ahead (screen up), positive to the
  // right.  The beam is a dashed line in along the line of sight and the source
  // is a solid triangle, so it reads at a glance which marks are lasers - and a
  // designator past the face (3 km) has its triangle pinned to the rim: the
  // bearing is the truth, the range is "out there".
  lasers(ctx, cx, cy, R, ring, own, laserList);
  // missiles.  A round the receiver has a *position* on gets a dot at its true
  // bearing and true range.  One it only *hears* gets a blinking line out from
  // the middle along that bearing and nothing else (`_threats` already dropped
  // the ones behind a ridge or beyond the wire's tier).
  //
  // Every heard round gets that line, converging or not: the line says "there is
  // something on this bearing", which is all a bearing is.  Whether it is coming
  // for this hull is the rim's answer, not the line's - so the line has one look
  // and the alarm has two (`steady` before `seen`, the `warn`/`aim` pair after).
  // That split is also why the alarm can be read off the rim alone with the
  // volume down.
  threats(ctx, cx, cy, R, ring, own, roundList);
  // the pilot
  ctx.globalAlpha = 0.95;
  ctx.fillStyle = '#7ce0a0';
  ctx.beginPath();
  ctx.arc(cx, cy, 2.6, 0, TAU);
  ctx.fill();
  // countermeasures: pulls left, or the seconds until the next one
  ctx.textAlign = 'center';
  font(ctx, 11, false);
  if (s.smoke !== null && s.smoke !== undefined) {
    ctx.fillStyle = s.smoke > 0 ? 'rgba(230,245,238,0.85)' : 'rgba(255,120,110,0.9)';
    const label = s.smoke > 0 ? `SMOKE ${s.smoke}  [Q]`
      : (s.smokeCd > 0 ? `SMOKE ${Math.ceil(s.smokeCd)}s` : 'SMOKE');
    ctx.fillText(label, cx, cy + R + 16);
  }
  ctx.restore();
}

// The ring beside the dial and its rim: a wash under the arrows, and the alarm
// band.  With a round on its way the rim blinks instead of glowing - one beat
// per tone of the missile trill (`RWR_TONE_S`), red on one and washed out on the
// next, so the rim and the ear count the same beats.  Red is the pure 255, 0, 0 -
// the same red the arrows are - and the quiet state is the dial's own rim colour.
function arrowsRing(ctx, ra, R, alarm) {
  ctx.globalAlpha = 1;
  ctx.fillStyle = 'rgba(0,0,0,0.20)';
  ctx.beginPath();
  ctx.arc(ra.x, ra.y, R + 3, 0, TAU);
  ctx.fill();
  const beat = (performance.now() / 1000 / RWR_TONE_S) % 2 < 1;
  ctx.globalAlpha = 1;
  ctx.strokeStyle = !alarm ? 'rgba(150,190,175,0.5)'
    : (beat ? 'rgb(255,0,0)' : 'rgba(255,96,88,0.30)');
  // the alarm band is a thick one and it grows *inwards*: the stroke is centred
  // half a width inside the radius, so the ring's outer edge - and with it the
  // ring's size next to the dial's - does not move
  const lw = alarm ? RING_ALARM_W : 1.2;
  ctx.lineWidth = lw;
  ctx.beginPath();
  ctx.arc(ra.x, ra.y, R - lw * 0.5, 0, TAU);
  ctx.stroke();
}

function lasers(ctx, cx, cy, R, ring, own, list) {
  for (const l of list) {
    const a = Math.atan2(l.z - own.z, l.x - own.x) - own.yaw;
    const lr = Math.min(R, (l.dist / ring) * R);
    const sx = Math.sin(a);
    const sy = -Math.cos(a);
    ctx.globalAlpha = 0.75;
    ctx.strokeStyle = FAR_COLOR;
    ctx.lineWidth = 1.2;
    ctx.setLineDash([3, 3]);
    ctx.beginPath();
    ctx.moveTo(cx + sx * (lr - 16), cy + sy * (lr - 16));
    ctx.lineTo(cx + sx * (R * 0.18), cy + sy * (R * 0.18));
    ctx.stroke();
    ctx.setLineDash([]);
    // the arrow: tip inward at the source's range, base just outside it
    const px = sy;
    const py = -sx;                    // perpendicular, in dial space
    const tipX = cx + sx * (lr - 13);
    const tipY = cy + sy * (lr - 13);
    const baseX = cx + sx * (lr + 2);
    const baseY = cy + sy * (lr + 2);
    ctx.globalAlpha = 0.95;
    ctx.beginPath();
    ctx.moveTo(tipX, tipY);
    ctx.lineTo(baseX + px * 5.5, baseY + py * 5.5);
    ctx.lineTo(baseX - px * 5.5, baseY - py * 5.5);
    ctx.closePath();
    ctx.fillStyle = FAR_COLOR;
    ctx.fill();
    ctx.strokeStyle = 'rgba(0,0,0,0.85)';
    ctx.lineWidth = 1;
    ctx.stroke();
  }
}

function threats(ctx, cx, cy, R, ring, own, list) {
  const blink = (performance.now() / 1000 * 5) % 1 < 0.5;
  for (const t of list) {
    const a = Math.atan2(t.z - own.z, t.x - own.x) - own.yaw;
    const sa = Math.sin(a);
    const ca = -Math.cos(a);           // dial space: up is forward
    if (!t.seen) {
      if (!blink) continue;
      ctx.globalAlpha = 0.9;
      ctx.strokeStyle = FAR_COLOR;
      ctx.lineWidth = 1.6;
      ctx.beginPath();
      ctx.moveTo(cx, cy);
      ctx.lineTo(cx + sa * R, cy + ca * R);
      ctx.stroke();
      continue;
    }
    const r = Math.min(R, (t.dist / ring) * R);
    const x = cx + sa * r;
    const y = cy + ca * r;
    ctx.globalAlpha = 0.95;
    ctx.beginPath();
    ctx.arc(x, y, 3.0, 0, TAU);
    ctx.fillStyle = FAR_COLOR;
    ctx.fill();
    ctx.strokeStyle = 'rgba(0,0,0,0.85)';
    ctx.lineWidth = 1;
    ctx.stroke();
  }
}
