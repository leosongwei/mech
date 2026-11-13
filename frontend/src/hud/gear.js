// The gear, in the very corner of the screen, and the two glyphs the HUD's
// buttons draw.
//
// The warning circle is round, so the square the corner leaves free is the only
// spot that costs nothing.  The gear is the one HUD button, and the way into the
// settings panel: it needs a free pointer (ESC), so it sits dim until the mouse
// is released and brightens under the cursor.
//
// Both icons are Material Symbols glyphs drawn from the vendored subset font,
// and the subset takes a few milliseconds to land - so each one has a primitive
// of its own for the first frames (and for a font that never arrives).  That
// pairing is the one thing the gear and the settings panel's close cross have in
// common, so it lives here and the panel imports it.

import { GEAR_EDGE } from './layout.js';
import { hover } from './canvas.js';
import { drawIcon } from '../icons.js';

const GEAR_SIZE = 32;

// The glyph if the subset is there, and the answer to whether the primitive is
// needed.  One door, because a fallback that forgets to check `drawIcon`'s
// return value draws two icons on top of each other.
function glyph(ctx, name, x, y, size, color) {
  return drawIcon(ctx, name, x, y, size, color);
}

// The gear: a toothed wheel when the font is not in yet - eight teeth round a
// hub, the hub punched out of it in the button's own dark.
function wheelIcon(ctx, cx, cy, size, lit) {
  const color = lit ? '#e8fff2' : 'rgba(190,226,204,0.8)';
  if (glyph(ctx, 'settings', cx, cy, size, color)) return;
  ctx.fillStyle = color;
  for (let i = 0; i < 8; i++) {
    ctx.save();
    ctx.translate(cx, cy);
    ctx.rotate((i * Math.PI) / 4);
    ctx.fillRect(-2, -11, 4, 4);
    ctx.restore();
  }
  ctx.beginPath();
  ctx.arc(cx, cy, 7.5, 0, Math.PI * 2);
  ctx.fill();
  ctx.fillStyle = lit ? 'rgba(20,40,30,0.95)' : 'rgba(10,18,24,0.9)';
  ctx.beginPath();
  ctx.arc(cx, cy, 3.4, 0, Math.PI * 2);
  ctx.fill();
}

// The close cross: an `X`, sized off the box it closes (`w` is that box, the
// glyph a little over half of it).
export function closeIcon(ctx, cx, cy, w) {
  if (glyph(ctx, 'close', cx, cy, w * 0.55, '#e8fff2')) return;
  ctx.strokeStyle = '#e8fff2';
  ctx.lineWidth = 1.6;
  const d = w * 0.22;
  ctx.beginPath();
  ctx.moveTo(cx - d, cy - d);
  ctx.lineTo(cx + d, cy + d);
  ctx.moveTo(cx + d, cy - d);
  ctx.lineTo(cx - d, cy + d);
  ctx.stroke();
}

// The button itself, and the rectangle the app hit-tests (`hud.gearRect`, which
// is `ui.gear` - one object for the life of the page rather than one per frame).
export function gear(ctx, s, w, h, ui) {
  const mouse = ui.mouse;
  const S = GEAR_SIZE;
  const x = w - S - GEAR_EDGE;
  const y = GEAR_EDGE;
  const rect = ui.gear;
  rect.x = x;
  rect.y = y;
  rect.w = S;
  rect.h = S;
  const hv = hover(mouse[0], mouse[1], x, y, S, S);
  const cx = x + S / 2;
  const cy = y + S / 2;
  ctx.save();
  ctx.fillStyle = hv ? 'rgba(66,120,92,0.75)' : 'rgba(10,18,24,0.62)';
  ctx.fillRect(x, y, S, S);
  ctx.strokeStyle = hv ? 'rgba(180,245,205,0.95)' : 'rgba(120,220,160,0.45)';
  ctx.lineWidth = 1.2;
  ctx.strokeRect(x + 0.5, y + 0.5, S - 1, S - 1);
  wheelIcon(ctx, cx, cy, S * 0.62, hv);
  ctx.restore();
}
