// The primitives the whole HUD draws with.
//
// Everything here existed five or three or thirteen times inside `hud.js`: the
// same track-and-fill pair under a rearm bar, a speed readout, a health bar and
// a base's integrity; the same centred slot row for the optics, the zero and
// the assist; the same mouse cross drawn twice in two panels; the same
// `this.hot.push({...})` for every clickable box and the same backwards scan in
// `hitUi`.  One copy each, and the font strings - which were rebuilt, and so
// re-parsed by the rasteriser, every time a size changed - are cached by size.

import { TAU } from './layout.js';
import { FACTION_COLORS } from '../scene/palette.js';

// ------------------------------------------------------------------- paint
// `FACTION_COLORS` is static, so the CSS string a mark, a town or a name is
// painted with is built once and handed round.  The old minimap built one per
// contact per frame - three hundred strings for four possible answers - and the
// kill feed built one per segment per frame.
//
// Two channel rules survive side by side because they are two different
// strings: the names and the towns round their channels, the marks truncate
// them (`0.92 * 255` is 235 one way and 234 the other).
const PAINT = new Map();

function paint(key, fac, alpha, round) {
  let s = PAINT.get(key);
  if (s === undefined) {
    const c = FACTION_COLORS[fac] || [0.8, 0.8, 0.8];
    const ch = round
      ? `${Math.round(c[0] * 255)},${Math.round(c[1] * 255)},${Math.round(c[2] * 255)}`
      : `${c[0] * 255 | 0},${c[1] * 255 | 0},${c[2] * 255 | 0}`;
    s = alpha === 1 ? `rgb(${ch})` : `rgba(${ch},${alpha})`;
    PAINT.set(key, s);
  }
  return s;
}

export function rgbRound(fac) {
  return paint(fac * 100000 + 1, fac, 1, true);
}

export function rgbaRound(fac, alpha) {
  return paint(fac * 100000 + 1000 + (alpha * 1000 | 0), fac, alpha, true);
}

export function rgbTrunc(fac) {
  return paint(fac * 100000 + 2, fac, 1, false);
}

export function rgbaTrunc(fac, alpha) {
  return paint(fac * 100000 + 50000 + (alpha * 1000 | 0), fac, alpha, false);
}

// font size -> the string canvas wants.  A canvas font is re-parsed whenever it
// is assigned, so the same four sizes a frame are four strings, not forty.
const PLAIN = new Map();
const BOLD = new Map();

export function font(ctx, size, bold) {
  const cache = bold ? BOLD : PLAIN;
  let s = cache.get(size);
  if (s === undefined) {
    s = `${bold ? 'bold ' : ''}${size}px "Courier New", monospace`;
    cache.set(size, s);
  }
  ctx.font = s;
  return s;
}

// One line: the font, the colour and the alignment, then the text.  `align` is
// optional because a block that has already set `textAlign` for a run of lines
// means to keep it.
export function text(ctx, str, x, y, size, color, align, bold) {
  font(ctx, size, bold);
  ctx.fillStyle = color;
  if (align !== undefined) ctx.textAlign = align;
  ctx.fillText(str, x, y);
}

const NO_OPTS = {};
const NO_LINES = [];

// A track with a fill in it, and optionally a frame round it: the rearm bar, the
// speed bar, a hull module's health and a base's integrity are one shape.  The
// fraction is clamped here so no caller has to remember to.
export function bar(ctx, x, y, w, h, frac, fill, opts) {
  const o = opts || NO_OPTS;
  const f = Math.max(0, Math.min(1, frac));
  ctx.fillStyle = o.track || 'rgba(8,14,18,0.6)';
  ctx.fillRect(x, y, w, h);
  ctx.fillStyle = fill;
  ctx.fillRect(x, y, w * f, h);
  if (o.frame) {
    ctx.strokeStyle = o.frame;
    ctx.lineWidth = o.frame_w || 1.2;
    ctx.strokeRect(x + 0.5, y + 0.5, w - 1, h - 1);
  }
}

// The shell both mouse panels are drawn on: a scrim over the field, a near
// opaque body, a green border.  `shell` is `layout.PANEL_SHOP` /
// `PANEL_SETTINGS` - a module-level description, never built per frame.
export function panel(ctx, sw, sh, x, y, pw, ph, shell) {
  ctx.fillStyle = shell.scrim;
  ctx.fillRect(0, 0, sw, sh);
  ctx.fillStyle = shell.body;
  ctx.fillRect(x, y, pw, ph);
  ctx.strokeStyle = 'rgba(140,225,180,0.7)';
  ctx.lineWidth = 1.6;
  ctx.strokeRect(x + 0.5, y + 0.5, pw - 1, ph - 1);
}

// The one tab row: a small caption over a centred run of `count` cells, one of
// them lit and framed.  The optics, the zero and the assist rows are the same
// shape laid out at three sizes, so `opts` carries the differences and is a
// module-level constant at the call site:
//
//   title / title_size / title_color / title_dy   the caption (or none)
//   cell_size, hi_fill, hi_stroke, hi_dy, hi_h    the cells and the lit frame
//   cell_text(i), cell_color(i, idx)              what a cell says and wears
export function tabRow(ctx, cx, y, slot, count, idx, opts) {
  const total = slot * count;
  const x0 = cx - total / 2;
  if (opts.title) {
    font(ctx, opts.title_size, false);
    ctx.fillStyle = opts.title_color;
    ctx.fillText(opts.title, cx, y + opts.title_dy);
  }
  font(ctx, opts.cell_size, false);
  for (let i = 0; i < count; i++) {
    const x = x0 + slot * i;
    if (i === idx) {
      ctx.fillStyle = opts.hi_fill;
      ctx.fillRect(x, y + opts.hi_dy, slot, opts.hi_h);
      ctx.strokeStyle = opts.hi_stroke;
      ctx.lineWidth = 1;
      ctx.strokeRect(x + 0.5, y + opts.hi_dy + 0.5, slot - 1, opts.hi_h - 1);
    }
    ctx.fillStyle = opts.cell_color(i, idx);
    ctx.fillText(opts.cell_text(i), x + slot / 2, y);
  }
}

// The mouse while a panel has the pointer: a small cross, drawn where the click
// will land.  The pointer lock is released while a panel is up, so the system
// cursor is hidden and the HUD owns this one.
export function cursor(ctx, x, y) {
  ctx.strokeStyle = '#e8fff2';
  ctx.lineWidth = 1.4;
  ctx.beginPath();
  ctx.moveTo(x - 7, y);
  ctx.lineTo(x + 7, y);
  ctx.moveTo(x, y - 7);
  ctx.lineTo(x, y + 7);
  ctx.stroke();
}

// Is the pointer inside a box?  The gear uses it, and so does every button.
export function hover(mx, my, x, y, w, h) {
  return mx >= x && mx <= x + w && my >= y && my <= y + h;
}

// The frame's clickable boxes in draw order.  A panel pushes a button as it lays
// it out and `pick` walks them backwards, so a small box sitting inside a big
// one is the one that gets the click.  Four parallel arrays reused every frame:
// the old shape was one fresh `{id, x, y, w, h}` per button per frame, in a list
// that was thrown away at the top of the next one.
export class HitList {
  constructor() {
    this.n = 0;
    this.ids = [];
    this.xs = [];
    this.ys = [];
    this.ws = [];
    this.hs = [];
  }

  reset() {
    this.n = 0;
  }

  push(id, x, y, w, h) {
    const i = this.n++;
    this.ids[i] = id;
    this.xs[i] = x;
    this.ys[i] = y;
    this.ws[i] = w;
    this.hs[i] = h;
  }

  pick(x, y) {
    for (let i = this.n - 1; i >= 0; i--) {
      if (x >= this.xs[i] && x <= this.xs[i] + this.ws[i]
        && y >= this.ys[i] && y <= this.ys[i] + this.hs[i]) return this.ids[i];
    }
    return null;
  }
}

// One clickable box: pushes its rectangle into the hit list, draws the frame and
// up to three lines of text.  A dead button still swallows the click, so it
// cannot fall through to whatever is underneath it.
export function btn(ctx, ui, fs, id, x, y, bw, bh, lines, opts) {
  const o = opts || NO_OPTS;
  const mouse = ui.mouse;
  const hv = hover(mouse[0], mouse[1], x, y, bw, bh);
  ui.hits.push(o.disabled ? 'disabled' : id, x, y, bw, bh);
  ctx.fillStyle = o.disabled ? 'rgba(18,24,26,0.5)'
    : hv ? 'rgba(66,120,92,0.5)' : 'rgba(14,26,28,0.7)';
  ctx.fillRect(x, y, bw, bh);
  ctx.strokeStyle = o.active ? '#ffcf5a'
    : o.disabled ? 'rgba(120,140,130,0.3)' : 'rgba(150,230,190,0.7)';
  ctx.lineWidth = o.active ? 2.2 : 1.2;
  ctx.strokeRect(x + 0.5, y + 0.5, bw - 1, bh - 1);
  ctx.textAlign = 'center';
  const ls = lines || NO_LINES;
  const lh = fs * 1.4;
  let ty = y + bh / 2 - (ls.length - 1) * lh / 2;
  for (let i = 0; i < ls.length; i++) {
    const big = i === 0;
    let size = big ? fs * 1.05 : fs * 0.78;
    const est = ls[i].length * size * 0.62;        // Courier New is monospaced
    if (est > bw - 12) size = Math.max(7.5, size * (bw - 12) / est);
    font(ctx, size, big);
    ctx.fillStyle = o.disabled ? 'rgba(150,166,158,0.5)'
      : big ? '#e8fff2' : 'rgba(196,224,208,0.85)';
    ctx.fillText(ls[i], x + bw / 2, ty);
    ty += lh;
  }
}

// A ring, which is 90 % of what the HUD's circles are.
export function circle(ctx, x, y, r) {
  ctx.beginPath();
  ctx.arc(x, y, r, 0, TAU);
}

// A dot: a filled circle of radius `r`, in the current fill colour.
export function dot(ctx, x, y, r) {
  circle(ctx, x, y, r);
  ctx.fill();
}
