// The settings panel, behind the gear.
//
// The master volume, the threat receiver's, the aiming rule and - when the
// server was started with MECH_DEBUG=1 - the debug knobs.  Same mouse machinery
// as the rearm panel: every box pushes a rect into the frame's hit list, and the
// three slider tracks are also left on the HUD (`volRect` / `alarmRect` /
// `ringRect`) because a click on a track is a *position*, not a button.
//
// The panel is as tall as its content, so the debug block and the assist row
// cost nothing when they are not there.

import { PANEL_SETTINGS, SETTINGS, TAU } from '../layout.js';
import { btn, cursor, font, panel } from '../canvas.js';
import { closeIcon } from '../gear.js';
import { drawIcon } from '../../icons.js';

const NO_ENDS = { lo: 0, hi: 1 };
const RING_ENDS = { lo: 2, hi: 8 };
const NO_LINES = [];

export function settings(ctx, s, w, h, ui) {
  const mouse = ui.mouse;
  const dbg = !!s.debug;
  const vol = s.volume === undefined ? 1 : s.volume;
  const alarm = s.alarmVolume === undefined ? 1 : s.alarmVolume;
  const assist = !!s.assistOn;
  const god = !!(s.meta && s.meta.god);
  const pw = Math.min(SETTINGS.w_max, w * SETTINGS.w_frac);
  const fs = Math.max(11, Math.min(15, pw * 0.03));
  const pad = fs * 1.5;
  const rowH = fs * 2.4;
  const closeH = fs * 2.2;
  // the panel is as tall as its content, so the debug block costs nothing when
  // the server shipped without it
  let contentH = fs * 2.6 + fs * 1.5 + rowH * 2 + fs * 1.4 * 2 + closeH;
  contentH += fs * 1.5 + rowH + fs * 1.4;
  if (assist) contentH += rowH + fs * 1.4;
  if (dbg) contentH += fs * 1.5 + rowH + fs * 1.4;
  const ph = Math.min(h * 0.92, contentH + pad * 2);
  const px = (w - pw) / 2;
  const py = (h - ph) / 2;
  const cx = px + pad;
  const cw = pw - pad * 2;
  const btnAt = (id, x, y, bw, bh, lines, opts) =>
    btn(ctx, ui, fs, id, x, y, bw, bh, lines, opts);

  ui.hits.reset();
  ctx.save();
  ctx.textBaseline = 'middle';
  panel(ctx, w, h, px, py, pw, ph, PANEL_SETTINGS);

  let y = py + pad;
  ctx.textAlign = 'left';
  font(ctx, fs * 1.35, true);
  ctx.fillStyle = '#a9f0c0';
  ctx.fillText('SETTINGS', cx, y + fs * 0.95);
  y += fs * 2.6;

  // close cross in the corner (the CLOSE button below does the same thing)
  const xw = rowH * 0.84;
  btnAt('close', px + pw - pad - xw, py + pad - fs * 0.3, xw, xw, NO_LINES, {});
  closeIcon(ctx, px + pw - pad - xw / 2, py + pad - fs * 0.3 + xw / 2, xw);

  // ---- sound: master and the threat receiver, one slider each.  Every row is a
  // step-down, an absolute bar and a step-up.  The bar's rectangle is left on the
  // HUD (`hud.volRect` and friends, out of `ui`) so the app can read a click back
  // as a position on it, and `lo` / `hi` are the two ends it is drawn over
  font(ctx, fs * 0.85, true);
  ctx.fillStyle = 'rgba(180,210,195,0.75)';
  ctx.fillText('SOUND', cx, y + fs * 0.45);
  y += fs * 1.5;
  const slider = (id, text, value, fill, ends) => {
    const step = fs * 3;
    const e = ends || NO_ENDS;
    btnAt(`${id}-`, cx + fs * 1.9, y, step, rowH, ['-'], { disabled: value <= 0 });
    const barX = cx + fs * 1.9 + step + fs * 0.5;
    const barW = cw - fs * 1.9 - step * 2 - fs;
    btnAt(id, barX, y, barW, rowH, NO_LINES, {});
    ctx.fillStyle = fill;
    ctx.fillRect(barX + 2, y + 2, Math.max(0, (barW - 4) * value), rowH - 4);
    ctx.textAlign = 'center';
    font(ctx, fs * 0.95, true);
    ctx.fillStyle = '#e8fff2';
    ctx.fillText(text, barX + barW / 2, y + rowH / 2);
    btnAt(`${id}+`, barX + barW + fs * 0.5, y, step, rowH, ['+'], { disabled: value >= 1 });
    const rect = ui[id];
    rect.x = barX;
    rect.w = barW;
    rect.lo = e.lo;
    rect.hi = e.hi;
    y += rowH + fs * 1.4;
  };
  drawIcon(ctx, 'volume_up', cx + fs * 0.9, y + rowH / 2, fs * 1.5, 'rgba(190,226,204,0.8)');
  slider('vol', `MASTER ${Math.round(vol * 100)}%`, vol, 'rgba(124,224,160,0.45)');
  // the alarm row has no glyph of its own (the font subset is exactly the icons in
  // icons.json), so it gets a warning triangle in the same slot
  const ix = cx + fs * 0.9;
  const iy = y + rowH / 2;
  const ir = fs * 0.85;
  ctx.strokeStyle = 'rgba(255,176,74,0.85)';
  ctx.lineWidth = 1.4;
  ctx.beginPath();
  ctx.moveTo(ix, iy - ir);
  ctx.lineTo(ix + ir, iy + ir * 0.8);
  ctx.lineTo(ix - ir, iy + ir * 0.8);
  ctx.closePath();
  ctx.stroke();
  slider('alarm', `ALARM ${Math.round(alarm * 100)}%`, alarm, 'rgba(255,176,74,0.45)');

  // ---- aiming: the rule the pre-aim rings obey, one switch
  ctx.textAlign = 'left';
  font(ctx, fs * 0.85, true);
  ctx.fillStyle = 'rgba(180,210,195,0.75)';
  ctx.fillText('AIM', cx, y + fs * 0.45);
  y += fs * 1.5;
  btnAt('assist', cx, y, cw, rowH,
    [assist ? 'AIM ASSIST ON' : 'AIM ASSIST OFF',
      assist ? 'the cross snaps to a pre-aim ring' : 'the cross is free'],
    { active: assist });
  y += rowH + fs * 1.4;
  // how wide the ring the cross is caught by is, as a multiple of the plain one:
  // the wider it is the harder the assist grabs, and the coarser it aims
  if (assist) {
    const sx = cx + fs * 0.9;
    const sy = y + rowH / 2;
    const sr = fs * 0.8;
    ctx.strokeStyle = 'rgba(124,224,160,0.85)';
    ctx.lineWidth = 1.4;
    ctx.beginPath();
    ctx.arc(sx, sy, sr, 0, TAU);
    ctx.stroke();
    slider('ring', `RING ${s.ringScale.toFixed(1)}x`, (s.ringScale - 2) / 6,
      'rgba(124,224,160,0.45)', RING_ENDS);
  }

  // ---- debug: only when the server says the messages are accepted
  if (dbg) {
    ctx.textAlign = 'left';
    font(ctx, fs * 0.85, true);
    ctx.fillStyle = 'rgba(180,210,195,0.75)';
    ctx.fillText('DEBUG', cx, y + fs * 0.45);
    y += fs * 1.5;
    const gap = fs * 0.6;
    const bw = (cw - gap * 2) / 3;
    btnAt('respawn', cx, y, bw, rowH, ['RESPAWN', 'skip the timer'], {});
    btnAt('restart', cx + bw + gap, y, bw, rowH, ['RESTART', 'fresh round'], {});
    btnAt('god', cx + (bw + gap) * 2, y, bw, rowH,
      [god ? 'GOD ON' : 'GOD OFF', god ? 'no damage' : 'take damage'], { active: god });
    y += rowH + fs * 1.4;
  }

  btnAt('close', px + (pw - cw * 0.4) / 2, y, cw * 0.4, closeH, ['CLOSE', '(ESC)'], {});

  // the cursor, since the pointer lock is released while the panel is up
  if (mouse[0] >= 0) cursor(ctx, mouse[0], mouse[1]);
  ctx.restore();
}
