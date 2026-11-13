// Everything that reads a number out: the bar over the middle of the screen, the
// speed and hull bars under the minimap, the rearm bar and the status column.
//
// The five of them are one block in the old file's order - the top bar, then
// (after the sight and the dial) the speed, the hull modules, the messages, the
// status lines and the rearm bar - and they stay separate functions here because
// each is drawn at its own moment in the pipeline.

import { MODULES, SPEED, STATUS, TOPBAR, WORK } from './layout.js';
import { bar, font, rgbTrunc, rgbaTrunc, text } from './canvas.js';

// The bar over the middle: the wave, the clock, and one integrity bar per
// command base.  In range mode the wave is replaced by the fixture's name and
// the third line counts the targets still standing.
export function topBar(ctx, s, w) {
  const m = s.meta || {};
  ctx.save();
  text(ctx, s.range ? 'RANGE' : `WAVE ${m.wave || 0}`, w / 2, TOPBAR.wave_y,
    15, '#d8f5e2', 'center', true);
  const mm = Math.floor((m.time || 0) / 60);
  const ss = Math.floor((m.time || 0) % 60);
  text(ctx, `${mm}:${ss < 10 ? '0' : ''}${ss}`, w / 2, TOPBAR.time_y,
    12, 'rgba(200,230,210,0.7)');
  if (s.range) {
    let targets = 0;
    for (const e of s.contacts || []) if (e.etype === 3) targets++;
    text(ctx, `TARGETS ${targets}`, w / 2, TOPBAR.note_y, 12, 'rgba(200,230,210,0.55)');
  }

  // base integrity bars
  let y = TOPBAR.base_y;
  for (const e of s.entities.values()) {
    if (e.type !== 5) continue;
    const maxHp = ((s.units || {})[5] || {}).hp;
    const ratio = e.hp[0] / ((maxHp && maxHp[0]) || 4000);
    const bw = TOPBAR.base_w;
    const bx = w / 2 - bw / 2;
    bar(ctx, bx, y, bw, TOPBAR.base_h, ratio, rgbTrunc(e.faction), BASE_BAR);
    text(ctx, e.faction === s.playerFaction ? 'ALLIED BASE' : 'ENEMY BASE',
      w / 2, y - TOPBAR.base_label_dy, 10, 'rgba(220,240,230,0.75)');
    y += TOPBAR.base_step;
  }
  ctx.restore();
}

const BASE_BAR = { track: 'rgba(0,0,0,0.45)' };
const SPEED_BAR = { track: 'rgba(95,230,255,0.22)' };

// ground speed, sitting right above the hull bars: the mech now takes three
// seconds to wind up to 120 km/h, so the pilot needs to see the number
export function speed(ctx, s, h) {
  if (!s.own) return;
  const kmh = Math.max(0, Math.round(s.speed || 0));
  const x0 = SPEED.x;
  const y0 = h - SPEED.y_off;
  const bw = SPEED.w;
  ctx.save();
  font(ctx, 11, false);
  ctx.fillStyle = 'rgba(8,14,18,0.6)';
  ctx.fillRect(x0, y0, bw, SPEED.h);
  // bar along the bottom of the panel, full at the 180 km/h top speed
  bar(ctx, x0, y0 + SPEED.bar_dy, bw, SPEED.bar_h, kmh / 180,
    kmh > 150 ? '#5fe6ff' : 'rgba(95,230,255,0.8)', SPEED_BAR);
  text(ctx, 'SPEED', x0 + 6, y0 + SPEED.text_dy, 11, 'rgba(230,245,238,0.85)');
  text(ctx, `${kmh} km/h`, x0 + bw - 6, y0 + SPEED.text_dy, 11,
    kmh > 150 ? '#5fe6ff' : '#e8f4ee', 'right');
  ctx.restore();
}

// One bar per damaged module of the pilot's own machine.  The hull is one pool
// (chassis + torso share it), so slot 1 has nothing of its own to show; the arms
// are slot 2 and 3.
const MODULE_BARS = [[0, 'HULL'], [2, 'L.ARM'], [3, 'R.ARM']];
const MODULE_BAR = { track: 'rgba(8,14,18,0.6)' };

export function modules(ctx, s, h) {
  if (!s.own) return;
  const e = s.own;
  const maxes = s.mechHp || [];
  const x0 = MODULES.x;
  const y0 = h - MODULES.y_off;
  ctx.save();
  font(ctx, 11, false);
  for (let i = 0; i < MODULE_BARS.length; i++) {
    const slot = MODULE_BARS[i][0];
    const max = Math.max(1, maxes[slot] || 1);
    const r = e.hp[slot] / max;
    const y = y0 + i * MODULES.step;
    bar(ctx, x0, y, MODULES.w, MODULES.h, r,
      r > 0.6 ? '#5ad07a' : r > 0.3 ? '#e6c249' : '#e0503c', MODULE_BAR);
    text(ctx, MODULE_BARS[i][1], x0 + 6, y + 11, 11, '#e8f4ee');
    text(ctx, `${Math.round(e.hp[slot])}`, x0 + 128, y + 11, 11, 'rgba(230,245,238,0.85)');
  }
  ctx.restore();
}

// The rearm bar, low in the middle of the screen: while a job runs the hull will
// not move and the triggers are dead, so the pilot is told why, for how long, and
// how much of it is done.  `meta.work` carries the seconds left, the length of
// the whole job and the label the server made of the lines.
const WORK_BAR = { track: 'rgba(95,230,255,0.2)', frame: 'rgba(95,230,255,0.8)' };

export function work(ctx, s, w, h) {
  const wk = (s.meta || {}).work;
  if (!wk || !(wk.total > 0)) return;
  const bw = Math.min(WORK.w_max, w * WORK.w_frac);
  const bh = WORK.h;
  const x = (w - bw) / 2;
  const y = h * WORK.y_frac;
  const t = wk.t || 0;
  ctx.save();
  ctx.fillStyle = 'rgba(8,14,18,0.72)';
  ctx.fillRect(x - WORK.pad, y - WORK.pad, bw + WORK.pad * 2, bh + WORK.pad * 2);
  bar(ctx, x, y, bw, bh, 1 - t / wk.total, '#5fe6ff', WORK_BAR);
  text(ctx, `${wk.label || 'REARM'}  ${t.toFixed(1)}s`, w / 2, y - WORK.label_dy,
    12, '#e8f4ee', 'center', true);
  text(ctx, 'HOLD POSITION · TRIGGERS SAFE', w / 2, y + bh + WORK.note_dy,
    11, 'rgba(255,207,90,0.9)');
  ctx.restore();
}

// The status column, down the left edge: the till, the scoreboard and the rearm
// hint, and along the bottom right the three lines of machine readout.
export function status(ctx, s, w, h) {
  const m = s.meta || {};
  ctx.save();
  ctx.textAlign = 'left';
  const money = `$${m.money || 0}`;
  text(ctx, money, STATUS.x, STATUS.money_y, 20, '#a9f0c0', 'left', true);
  // a payout says itself beside the number it changed (`game._moneyGain`): the
  // money's own line, to the right of it, oldest first and each one fading where
  // it stands.  The stack on the right is the words - this is the till.
  const pops = s.moneyPops;
  if (pops && pops.length) {
    let px = STATUS.x + ctx.measureText(money).width + 12;
    font(ctx, 15, true);
    ctx.fillStyle = '#a9f0c0';
    for (const p of pops) {
      const gain = `+$${p.amount}`;
      ctx.globalAlpha = Math.max(0, Math.min(1, p.life / 0.8));
      ctx.fillText(gain, px, STATUS.money_y);
      px += ctx.measureText(gain).width + 10;
    }
    ctx.globalAlpha = 1;
  }
  text(ctx, `KILLS ${m.kills || 0}   DEATHS ${m.deaths || 0}   SCORE ${m.score || 0}`,
    STATUS.x, STATUS.kills_y, 12, 'rgba(210,235,220,0.8)');

  // team scoreboard
  let y = STATUS.board_y;
  for (const p of m.players || []) {
    ctx.fillStyle = rgbaTrunc(p.faction, 0.9);
    ctx.fillText(`${p.name}  ${p.kills}/${p.deaths}`, STATUS.x, y);
    y += STATUS.board_step;
  }

  if (s.atBase) {
    text(ctx, '[B] REARM / REPAIR', STATUS.x, y + STATUS.rearm_dy, 12, '#ffe08a');
    y += STATUS.rearm_step;
  }

  ctx.textAlign = 'right';
  ctx.fillStyle = 'rgba(180,210,195,0.65)';
  font(ctx, 11, false);
  // the network line: RTT measured on the echoed ack, the server's own tick rate,
  // and the share of this pilot's snapshots the server chose not to send last
  // second (a held connection reads 100)
  const net = s.net || {};
  // the field is a fixed five characters - three digits padded with spaces plus
  // the unit, or a bare dash until the first echo - so the rest of the line does
  // not shuffle as the number grows
  const ping = net.rtt >= 0 ? Math.max(0, net.rtt).toFixed(0).padStart(3) + 'ms' : '    -';
  const loss = net.loss !== undefined ? net.loss.toFixed(2) : '-';
  const low = h - STATUS.net_y;
  ctx.fillStyle = net.held ? 'rgba(255,170,120,0.85)' : 'rgba(150,200,175,0.75)';
  ctx.fillText(`PING ${ping}  TPS ${(net.tps || 0).toFixed(1)}  LOSS ${loss}%`,
    w - 16, low);
  ctx.fillStyle = 'rgba(150,200,175,0.75)';
  ctx.fillText(`FPS ${s.stats.fps | 0}  DRAWS ${s.stats.draws}  INST ${s.stats.instances}`,
    w - 16, low - STATUS.net_step);
  ctx.fillText(s.helpOpen ? 'H - HIDE CONTROLS' : 'H - CONTROLS',
    w - 16, low - STATUS.net_step * 2);
  ctx.restore();
}
