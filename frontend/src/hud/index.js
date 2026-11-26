// The 2D HUD, assembled: one object the app talks to, and the thirteen blocks it
// draws in the order the frame needs them.
//
// This file is the facade and nothing else.  It owns the canvas, the device
// pixel ratio, the two screen measurements the app reads (`w` / `h`), the view
// projection the frame is drawn against, and the hit list the mouse panels fill;
// every pixel comes from one of the modules beside it, and the order they are
// called in *is* the document - the arms are laid on the same screen as the
// cross they are drawn under, the messages are right aligned to the arrow ring
// the dial draws, the panels come last so they cover everything.
//
// The app imports this and three names out of `layout.js` (the two pre-aim sizes
// and the arrow ring's centre); nothing else here is its business.

import { HitList } from './canvas.js';
import { project } from '../render/camera.js';

import { aimCross, centreCross, crosshairs, lock } from './reticle.js';
import { leads } from './leads.js';
import { dial } from './dial.js';
import { bakeMap, minimap } from './minimap.js';
import { messages } from './messages.js';
import { modules, speed, status, topBar, work } from './status.js';
import { death, gameover, hitFlash, optics, weapons } from './overlays.js';
import { gear } from './gear.js';
import { shop } from './panels/shop.js';
import { settings } from './panels/settings.js';

// the pointer while it is locked: no side, no position, and one array for the
// frames that carry no mouse at all
const NO_MOUSE = [-1, -1];

export class HUD {
  constructor(canvas) {
    this.canvas = canvas;
    this.ctx = canvas.getContext('2d');
    this.dpr = 1;
    this.w = 0;
    this.h = 0;
    this.vp = null;
    // the minimap's bake: a canvas of the tiles and the metres it covers
    this.baked = null;
    // the frame's clickable boxes, and the panel furniture the app reads back
    this.hits = new HitList();
    this.ui = {
      hits: this.hits,
      mouse: NO_MOUSE,
      gear: { x: 0, y: 0, w: 0, h: 0 },
      vol: { x: 0, w: 0, lo: 0, hi: 1 },
      alarm: { x: 0, w: 0, lo: 0, hi: 1 },
      ring: { x: 0, w: 0, lo: 2, hi: 8 },
    };
    // `_project`'s answer, rewritten in place by each call
    this.out = { x: 0, y: 0, z: 0 };
  }

  // The frame's hit rects.  The app resets them when a panel opens (`hud.hot = []`)
  // so a box from the last frame cannot take a click on the frame the pointer
  // came back; reading it gives the list itself.
  get hot() {
    return this.hits;
  }

  set hot(v) {
    this.hits.reset();
  }

  get gearRect() {
    return this.ui.gear;
  }

  get volRect() {
    return this.ui.vol;
  }

  get alarmRect() {
    return this.ui.alarm;
  }

  get ringRect() {
    return this.ui.ring;
  }

  // The map arrives once, from the init payload; the bake is the expensive half
  // of the minimap and it happens here.
  setMap(map) {
    this.baked = bakeMap(map);
  }

  resize() {
    const dpr = Math.min(window.devicePixelRatio || 1, 1.75);
    this.dpr = dpr;
    const w = Math.floor(this.canvas.clientWidth * dpr);
    const h = Math.floor(this.canvas.clientHeight * dpr);
    if (this.canvas.width !== w || this.canvas.height !== h) {
      this.canvas.width = w;
      this.canvas.height = h;
    }
    this.w = this.canvas.clientWidth;
    this.h = this.canvas.clientHeight;
    return { w: this.w, h: this.h };
  }

  draw(s, vp) {
    const { w, h } = this.resize();
    const ctx = this.ctx;
    ctx.setTransform(this.dpr, 0, 0, this.dpr, 0, 0);
    ctx.clearRect(0, 0, w, h);
    this.vp = vp;
    const ui = this.ui;
    ui.mouse = s.mouse || NO_MOUSE;

    // what the pilot is being told to shoot, and with what
    if (s.own) {
      leads(ctx, s);
      aimCross(ctx, s);
      centreCross(ctx, s, w, h);
      crosshairs(ctx, s, w, h);
      lock(ctx, s);
    }
    // the field, the bar, the arms, the sight, the dial
    minimap(ctx, s, this.baked);
    topBar(ctx, s, w);
    weapons(ctx, s, w, h);
    if (s.own) optics(ctx, s, w, h);
    if (s.own) dial(ctx, s, w);
    // the machine
    speed(ctx, s, h);
    modules(ctx, s, h);
    messages(ctx, s, w);
    status(ctx, s, w, h);
    work(ctx, s, w, h);
    hitFlash(ctx, s, w, h);
    // and the states that own the whole screen
    if (s.dead) death(ctx, s, w, h);
    if (s.winner >= 0) gameover(ctx, s, w, h);
    if (!s.shopOpen && !s.settingsOpen) gear(ctx, s, w, h, ui);
    if (s.shopOpen) shop(ctx, s, w, h, ui);
    if (s.settingsOpen) settings(ctx, s, w, h, ui);
  }

  // The id of the box under the pointer, or null: the small box pushed last wins
  // over the frame it sits in.
  hitUi(x, y) {
    return this.hits.pick(x, y);
  }

  // A world point in CSS pixels, or null when it is behind the eye or well off
  // the screen.  A compatibility alias for the app's older callers - the one
  // projection in the client is `render/camera.project`, and this wraps it with
  // the visibility window the HUD has always applied.  It answers in a shared
  // object, so read it before the next call.
  // `[sx, sy]` in CSS pixels, or null when the point is behind the eye or off the
  // frame by a margin.  An array, not a shared object: a frame solves several of
  // these and keeps two of them alive at once (a ring's mark and its box), so a
  // rewritten-in-place answer would alias.  The 1.3 NDC margin is the old HUD's
  // and it is what a pre-aim ring that has left the frame is clamped against.
  _project(x, y, z) {
    const m = this.vp;
    if (!m) return null;
    const out = this.out;
    if (!project(out, m, this.w, this.h, x, y, z)) return null;
    const nx = (out.x / this.w) * 2 - 1;
    const ny = 1 - (out.y / this.h) * 2;
    if (nx < -1.3 || nx > 1.3 || ny < -1.3 || ny > 1.3) return null;
    return [out.x, out.y];
  }
}

export { LEAD_RING, LEAD_PAIR_SEP, arrowRingAt } from './layout.js';
