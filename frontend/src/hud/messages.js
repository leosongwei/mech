// The message stack, right aligned under the warning dial.
//
// A message is either plain (`text` + `color`) or a list of segments - the kill
// feed paints the killer and the victim in their own side's colour and the
// weapon bold, so the line cannot be a single `fillText`.  Right aligned, so the
// width is measured before anything is drawn: the segments are drawn left to
// right from the ring's left edge.
//
// A segment used to be measured twice - once to lay the line out and once to
// advance through it - and its colour was turned into an `rgb()` string on every
// one of those frames.  Both are cached now: the width per segment is kept for
// the draw pass and the colour is `canvas.rgbRound`, which is one string per
// side for the life of the page.

import {
  ARROW_R, KILL_H, KILL_PAD, KILL_TOP, MESSAGE_BOLD, MESSAGE_COLOR, MESSAGE_FONT,
  MESSAGES, arrowRingAt,
} from './layout.js';
import { rgbRound } from './canvas.js';
import { FACTION_COLORS } from '../scene/palette.js';

// a segment is coloured by name (`color`) or by side (`fac`, a `FACTION_COLORS`
// index - which is how the kill feed says "red killed blue" without the server
// having to know what the client paints with)
function segmentColor(fac, color) {
  if (fac !== undefined && FACTION_COLORS[fac]) return rgbRound(fac);
  return color || MESSAGE_COLOR;
}

// the widths of the line being laid out, one per segment: the measure pass fills
// it and the draw pass walks it
const WIDTHS = [0, 0, 0, 0, 0, 0, 0, 0];

export function messages(ctx, s, w) {
  ctx.save();
  ctx.textAlign = 'left';
  // clear of the arrow ring, and of the warning circle beyond it
  const right = arrowRingAt(w).x - ARROW_R - MESSAGES.gap;
  const list = s.messages;
  const n = list.length;
  let y = MESSAGES.y;
  for (let i = n - 1; i >= 0 && i > n - MESSAGES.cap; i--) {
    const m = list[i];
    const a = Math.max(0, Math.min(1, m.life));
    ctx.globalAlpha = a * 0.9;
    const segs = m.segs;
    if (segs) {
      let width = 0;
      for (let k = 0; k < segs.length; k++) {
        const g = segs[k];
        ctx.font = g.bold ? MESSAGE_BOLD : MESSAGE_FONT;
        const sw = ctx.measureText(g.text).width;
        WIDTHS[k] = sw;
        width += sw;
      }
      let x = right - width;
      // a kill line gets a dark plate behind it - it is the one message that is
      // read while the sky moves under it, and three colours of 12px monospace
      // are not readable straight off a bright cloud.  The plate is laid under
      // the text only, so the plain messages of the same list keep their old
      // unobtrusive look.
      ctx.fillStyle = 'rgba(8,14,10,0.62)';
      ctx.fillRect(x - KILL_PAD, y - KILL_TOP, width + KILL_PAD * 2, KILL_H);
      for (let k = 0; k < segs.length; k++) {
        const g = segs[k];
        ctx.font = g.bold ? MESSAGE_BOLD : MESSAGE_FONT;
        ctx.fillStyle = segmentColor(g.fac, g.color);
        ctx.fillText(g.text, x, y);
        x += WIDTHS[k];
      }
    } else {
      ctx.font = MESSAGE_FONT;
      const width = ctx.measureText(m.text).width;
      ctx.fillStyle = segmentColor(m.fac, m.color);
      ctx.fillText(m.text, right - width, y);
    }
    y += MESSAGES.step;
  }
  ctx.restore();
}
