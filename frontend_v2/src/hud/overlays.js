// The full-screen blocks: the two arm panels, the sight's tab rows, the damage
// vignette, the death card and the match verdict.
//
// They have nothing in common but their place in the frame - each is a handful
// of centred lines over its own rectangle - so they live together and share only
// the primitives in `canvas.js`.  The old `_help` panel is gone: `help` is a
// constant `false` in the HUD state, so the panel and its eleven lines of
// keybinding copy were unreachable (the keys are in `docs/product/screens.md`
// and the HUD has no way to open them).

import { ARM_COLORS, ARM_NAMES, ARMS, DEATH, GAMEOVER, HIT_FLASH, OPTICS } from './layout.js';
import { font, tabRow, text } from './canvas.js';
import { ZOOM_LEVELS } from '../core/config.js';

// The damage vignette: a red wash round the edges of the screen, deepest at the
// corners.  It is drawn as a radial gradient with a hole in the middle so the
// centre of the sight stays clean - the pilot has to be able to shoot back.
export function hitFlash(ctx, s, w, h) {
  if (!(s.hitFlash > 0.01)) return;
  const a = Math.min(HIT_FLASH.max_alpha, s.hitFlash * 0.5);
  const g = ctx.createRadialGradient(w / 2, h / 2, Math.min(w, h) * HIT_FLASH.r0,
    w / 2, h / 2, Math.max(w, h) * HIT_FLASH.r1);
  g.addColorStop(0, 'rgba(150,0,0,0)');
  g.addColorStop(1, `rgba(170,10,10,${a})`);
  ctx.fillStyle = g;
  ctx.fillRect(0, 0, w, h);
}

// The two arm panels, low in the middle: what each arm is carrying, what is in
// it, and - in the small line - what it is for.  A destroyed arm says so and
// drops its numbers, because its ammunition left with it.
export function weapons(ctx, s, w, h) {
  const defs = s.weapons || {};
  ctx.save();
  font(ctx, 12, false);
  const y0 = h - ARMS.y_off;
  const panelW = ARMS.w;
  for (let arm = 0; arm < 2; arm++) {
    const x0 = w / 2 + (arm === 0 ? -panelW - ARMS.gap : ARMS.gap);
    const e = s.own;
    if (!e) continue;
    const wid = e.weapon ? e.weapon[arm] : 0;
    const wd = defs[wid] || {};
    const mod = arm === 0 ? 2 : 3;
    const dead = e.hp[mod] <= 0;
    ctx.fillStyle = 'rgba(10,16,20,0.62)';
    ctx.fillRect(x0, y0, panelW, ARMS.h);
    ctx.strokeStyle = ARM_COLORS[arm];
    ctx.globalAlpha = dead ? 0.3 : 0.9;
    ctx.lineWidth = 1;
    ctx.strokeRect(x0 + 0.5, y0 + 0.5, panelW - 1, ARMS.h - 1);
    ctx.fillStyle = ARM_COLORS[arm];
    font(ctx, 11, true);
    ctx.fillText(`${ARM_NAMES[arm]}-ARM  ${dead ? 'DESTROYED' : (wd.name || 'EMPTY')}`,
      x0 + 8, y0 + 16);
    if (!dead) {
      ctx.fillStyle = '#cfe6d6';
      font(ctx, 12, false);
      const mag = e.mag[arm];
      const ammo = e.ammo[arm];
      // a weapon with no magazine (the rocket pod) shows a single pool
      ctx.fillText(ammo > 0 ? `${mag} / ${ammo}` : `${mag}`, x0 + 8, y0 + 36);
      const reloading = (e.flags >> (2 + arm)) & 1;
      if (reloading) {
        ctx.fillStyle = '#ffcc66';
        ctx.fillText('RELOADING', x0 + 90, y0 + 36);
      }
      ctx.fillStyle = 'rgba(200,230,210,0.55)';
      font(ctx, 10, false);
      ctx.fillText(`${((wd.lock_range || wd.range) / 1000).toFixed(1)}km  ${wd.desc || ''}`,
        x0 + 8, y0 + 50);
    }
    ctx.globalAlpha = 1;
  }
  ctx.restore();
}

// ------------------------------------------------ the sight block's tab rows
// Three rows of cells, one above the other under the middle of the screen, and
// the one the machine is in is lit.  They differ in size and colour and in
// nothing else, so each is one `tabRow` and one module-level description.
//
// The assist row is on top because the switch (`V`) changes the width of the
// pre-aim rings, which is what a pilot reads when he is deciding whether the
// cross will be caught by one.  The two rows under it own the sight's own
// colours - cyan for the optics, amber for the zero - so the assist row is the
// HUD's plain white.
const ASSIST_ON = '#ffffff';
const ASSIST_OFF = 'rgba(170,200,190,0.45)';

const ASSIST_ROW = {
  title: 'AIM ASSIST', title_size: 10, title_color: 'rgba(150,190,175,0.55)', title_dy: -13,
  cell_size: 11, hi_fill: 'rgba(255,255,255,0.14)', hi_stroke: 'rgba(255,255,255,0.85)',
  hi_dy: -10, hi_h: 13,
  cell_text: (i) => (i === 1 ? 'ON' : 'OFF'),
  cell_color: (i, idx) => (i === idx ? ASSIST_ON : ASSIST_OFF),
};

const OPTICS_ON = '#9ceaff';
const OPTICS_NEAR = `rgba(170,200,190,${0.34 + 0.16})`;
const OPTICS_DIM = 'rgba(170,200,190,0.34)';

const OPTICS_ROW = {
  title: 'OPTICS', title_size: 10, title_color: 'rgba(150,190,175,0.55)', title_dy: -14,
  cell_size: 12, hi_fill: 'rgba(95,230,255,0.14)', hi_stroke: 'rgba(95,230,255,0.8)',
  hi_dy: -11, hi_h: 15,
  cell_text: (i) => `${ZOOM_LEVELS[i]}x`,
  cell_color: (i, idx) => (i === idx ? OPTICS_ON
    : (i === idx - 1 || i === idx + 1 ? OPTICS_NEAR : OPTICS_DIM)),
};

const ZERO_ON = '#ffce8a';
const ZERO_NEAR = `rgba(170,200,190,${0.30 + 0.14})`;
const ZERO_DIM = 'rgba(170,200,190,0.3)';

const ZERO_ROW = {
  title: 'ZERO km', title_size: 10, title_color: 'rgba(150,190,175,0.55)', title_dy: -13,
  cell_size: 11, hi_fill: 'rgba(255,176,74,0.14)', hi_stroke: 'rgba(255,176,74,0.8)',
  hi_dy: -10, hi_h: 13,
  // the steps are the state's (`input.zeroSteps`), so the row carries the list it
  // is being drawn for and formats a cell as the kilometres it stands for
  steps: null,
  cell_text(i) { return `${this.steps[i] / 1000}`; },
  cell_color(i, idx) {
    return i === idx ? ZERO_ON
      : (i === idx - 1 || i === idx + 1 ? ZERO_NEAR : ZERO_DIM);
  },
};

// Wheel driven sight magnification: 1x / 4x / 10x / 20x, and under it the range
// the gun is zeroed to.
export function optics(ctx, s, w, h) {
  const levels = ZOOM_LEVELS;
  const idx = Math.max(0, Math.min(levels.length - 1, s.zoomIdx | 0));
  const slot = OPTICS.slot;
  const total = slot * levels.length;
  const x0 = w / 2 - total / 2;
  const y = h - OPTICS.y_off;
  ctx.save();
  ctx.textAlign = 'center';
  tabRow(ctx, w / 2, y - OPTICS.assist_dy, slot, 2, s.assistOn ? 1 : 0, ASSIST_ROW);
  tabRow(ctx, w / 2, y, slot, levels.length, idx, OPTICS_ROW);
  // live magnification while the sight is travelling between steps, out to the
  // side so it never lands on the zero row
  if (Math.abs(s.zoom - levels[idx]) > 0.02) {
    text(ctx, `${s.zoom.toFixed(1)}x`, w / 2 + total / 2 + OPTICS.live_dx, y - OPTICS.live_dy,
      10, 'rgba(156,234,255,0.7)', 'left');
    ctx.textAlign = 'center';
  }
  zeroBar(ctx, s, w, y + OPTICS.zero_dy, slot);
  ctx.restore();
}

// The sight setting, one row under the optics: the range the gun is zeroed to,
// stepped with CTRL+wheel.  A round drops 4 m over 1.5 km and 70 m over 4 km, so
// this is the number that decides where it lands; it also sets how much lead a
// moving target gets.
function zeroBar(ctx, s, w, y, slot) {
  const steps = s.zeroSteps || [];
  if (!steps.length) return;
  const idx = Math.max(0, Math.min(steps.length - 1, s.zeroIdx | 0));
  ZERO_ROW.steps = steps;
  tabRow(ctx, w / 2, y, slot, steps.length, idx, ZERO_ROW);
}

// The death card.  It covers the field in its own red so the wreck behind it
// reads as background, and it says the two things a dead pilot needs: how long
// he waits and what he comes back with.
export function death(ctx, s, w, h) {
  const m = s.meta || {};
  ctx.save();
  ctx.fillStyle = 'rgba(40,0,0,0.32)';
  ctx.fillRect(0, 0, w, h);
  ctx.textAlign = 'center';
  text(ctx, 'MECH DESTROYED', w / 2, h / 2 - DEATH.title_dy, 34, '#ff9a9a', 'center', true);
  text(ctx, `RE-DEPLOY IN ${(m.respawn || 0).toFixed(1)}s`, w / 2, h / 2 + DEATH.redeploy_dy,
    16, '#e8d6d6');
  text(ctx, s.range ? 'RESPAWN ON THE RANGE FIRING LINE'
    : 'RESPAWN ONE TOWN BACK FROM WHERE YOU FELL, OR AT THE BASE',
  w / 2, h / 2 + DEATH.note_dy, 12, 'rgba(220,200,200,0.75)');
  text(ctx, 'LOADOUT LOST - STANDARD ARMS ON RE-DEPLOY', w / 2, h / 2 + DEATH.note2_dy,
    12, 'rgba(220,200,200,0.75)');
  ctx.restore();
}

// The verdict, 30 % down the screen and clear of the cross and the guns: the
// match is decided but the fight goes on for its countdown.
export function gameover(ctx, s, w, h) {
  const won = s.winner === s.playerFaction;
  ctx.save();
  ctx.fillStyle = won ? 'rgba(0,40,20,0.45)' : 'rgba(40,0,10,0.45)';
  ctx.fillRect(0, 0, w, h);
  ctx.textAlign = 'center';
  const y = h * GAMEOVER.y_frac;
  text(ctx, won ? 'VICTORY' : 'DEFEAT', w / 2, y, 46, won ? '#9dffb0' : '#ff8a8a', 'center', true);
  // the line is about the base that *ended the match*, which from the losing side
  // is your own: a pilot who was just beaten does not want to be told his enemy's
  // base went up
  text(ctx, won ? 'ENEMY COMMAND BASE DESTROYED' : 'OUR COMMAND BASE DESTROYED',
    w / 2, y + GAMEOVER.verdict_dy, 16, '#e8f4ee');
  const t = (s.meta && s.meta.end_timer) || 0;
  text(ctx, `NEW MATCH IN ${t.toFixed(0)}s`, w / 2, y + GAMEOVER.timer_dy,
    13, '#e8f4ee');
  ctx.restore();
}
