// The rearm panel.
//
// A mouse panel: every box pushes its rectangle into the frame's hit list, and
// the app maps a click back to the id it finds there (last one wins, so a small
// button sitting inside a big one still gets the click).  The panel is paused
// furniture - the machine will not move while it is up - so it is drawn in one
// pass, from the shell down: header, the two arm columns, the middle column, the
// bill, the three commands, and last the pulled-down weapon list, whose entries
// have to land on top of everything else in the hit list.

import { ARM_COLORS, PANEL_SHOP, SHOP } from '../layout.js';
import { btn, cursor, font, panel } from '../canvas.js';

// The three things the panel falls back on when the state has not caught up
// (the frame a panel opens, a server that shipped no quote).  Module level, so a
// panel frame that is missing one of them is not three objects of garbage.
const NO_ARM = { wid: 0, hp: 0, hpmax: 1, mag: 0, reserve: 0 };
const NO_WEAPON = { name: 'EMPTY', mag: 0, reserve: 0, ammo_cost: 0 };
const NO_BUYABLE = { name: '?', mag: 0, reserve: 0, ammo_cost: 0, cost: 0 };
const NO_DRAFT = { arms: [null, null], rearm: [false, false], repair: false, smoke: false };
const NO_QUOTE = { lines: [], total: 0, ok: false };
const NO_QUICK = { total: 0, dur: 0, ok: false };

// money in whole units, rounded the way the server rounds its own prices
const cash = (v) => Math.floor(v + 0.5);

// the two arm columns' left edges, reused
const COL_X = [0, 0];

export function shop(ctx, s, w, h, ui) {
  const pw = Math.min(SHOP.w_max, w * SHOP.w_frac);
  const ph = Math.min(SHOP.h_max, h * SHOP.h_frac);
  const px = (w - pw) / 2;
  const py = (h - ph) / 2;
  const meta = s.meta || {};
  const weapons = s.weapons || {};
  const draft = s.shopDraft || NO_DRAFT;
  const quote = s.shopQuote || NO_QUOTE;
  const cfg = s.rearm || {};
  const money = meta.money || 0;
  const mouse = ui.mouse;
  const perHp = cfg.repair_cost === undefined ? 1 : cfg.repair_cost;
  const smokeCost = cfg.smoke_cost === undefined ? 10 : cfg.smoke_cost;
  const smokeFull = cfg.smoke_charges === undefined ? 10 : cfg.smoke_charges;
  const fs = Math.max(11, Math.min(16, ph * 0.026));

  ui.hits.reset();
  ctx.save();
  ctx.textBaseline = 'middle';
  panel(ctx, w, h, px, py, pw, ph, PANEL_SHOP);

  const btnAt = (id, x, y, bw, bh, lines, opts) =>
    btn(ctx, ui, fs, id, x, y, bw, bh, lines, opts);

  // header: money on the left, close cross top right
  ctx.textAlign = 'left';
  font(ctx, fs * 1.1, true);
  ctx.fillStyle = '#a9f0c0';
  ctx.fillText(`$${money}`, px + pw * 0.05, py + ph * 0.05);
  font(ctx, fs * 0.85, false);
  ctx.fillStyle = 'rgba(180,210,195,0.7)';
  ctx.fillText('REARM / REPAIR', px + pw * 0.05 + 84, py + ph * 0.05);
  btnAt('close', px + pw - ph * 0.09 - 14, py + 14, ph * 0.09, ph * 0.09, ['X'], {});

  // arms: a hardware box with a cycle arrow either side, a REARM under it
  const colW = pw * 0.26;
  COL_X[0] = px + pw * 0.055;
  COL_X[1] = px + pw * 0.685;
  const boxY = py + ph * 0.20;
  const boxH = ph * 0.17;
  for (let arm = 0; arm < 2; arm++) {
    const a = (meta.arms || [])[arm] || NO_ARM;
    const picked = draft.arms[arm];
    const shownWid = picked === null ? a.wid : picked;
    const wd = weapons[shownWid] || NO_WEAPON;
    const rounds = picked === null ? a.mag + a.reserve : wd.mag + wd.reserve;
    const hpPct = Math.round(100 * a.hp / Math.max(1, a.hpmax));
    const x0 = COL_X[arm];
    const tag = arm === 0 ? 'L-ARM' : 'R-ARM';
    ctx.textAlign = 'left';
    font(ctx, fs * 0.95, true);
    ctx.fillStyle = ARM_COLORS[arm];
    ctx.fillText(tag, x0 + 4, boxY - fs * 1.1);
    const open = s.shopDrop === arm;
    btnAt(arm === 0 ? 'drop0' : 'drop1', x0, boxY, colW, boxH,
      [wd.name, `${rounds} RDS · HP ${hpPct}%`, 'CLICK TO PICK A WEAPON'],
      { active: picked !== null || open });
    // the chevron that says "this opens"
    ctx.save();
    ctx.textAlign = 'center';
    ctx.fillStyle = open ? '#ffcf5a' : 'rgba(196,224,208,0.8)';
    font(ctx, fs * 1.1, true);
    ctx.fillText(open ? '\u25B4' : '\u25BE', x0 + colW - fs * 1.1, boxY + boxH - fs * 0.9);
    ctx.restore();

    const full = wd.mag + wd.reserve;
    const missing = full - rounds;
    const canRearm = missing > 0 && picked === null;
    const cost = cash(missing * (wd.ammo_cost || 0));
    btnAt(arm === 0 ? 'lrearm' : 'rrearm', x0 + (colW - pw * 0.16) / 2, boxY + boxH + ph * 0.04,
      pw * 0.16, ph * 0.085,
      ['REARM', canRearm ? `${missing} RDS  $${cost}` : 'FULL'],
      { disabled: !canRearm, active: draft.rearm[arm] });
  }

  // middle column: smoke on top, repair below
  const smokeNow = meta.smoke === null || meta.smoke === undefined ? smokeFull : meta.smoke;
  const smokeMissing = Math.max(0, smokeFull - smokeNow);
  btnAt('smoke', px + pw * 0.5 - pw * 0.105, py + ph * 0.05, pw * 0.21, ph * 0.105,
    ['REARM SMOKE', smokeMissing > 0 ? `${smokeMissing} × $${smokeCost}` : 'FULL',
      `${smokeNow}/${smokeFull}`],
    { active: draft.smoke && smokeMissing > 0, disabled: smokeMissing <= 0 });
  const mechHp = s.mechHp || [0, 0, 0, 0];
  const hpNow = meta.hp || [];
  let need = 0;
  for (let i = 0; i < 4; i++) need += Math.max(0, (mechHp[i] || 0) - (hpNow[i] || 0));
  need = Math.round(need);
  const repairCost = cash(need * perHp);
  btnAt('repair', px + pw * 0.5 - pw * 0.105, py + ph * 0.20, pw * 0.21, ph * 0.30,
    ['REPAIR', need > 0 ? `${need} HP` : 'NO DAMAGE', need > 0 ? `$${repairCost}` : ''],
    { disabled: need <= 0 || meta.mech === undefined || meta.mech < 0,
      active: draft.repair });

  // CHANGES: one line per line of the draft, then the bill
  const cx = px + pw * 0.055;
  let cy = py + ph * 0.60;
  const lh = fs * 1.55;
  ctx.textAlign = 'left';
  font(ctx, fs, true);
  ctx.fillStyle = '#cfe6d6';
  ctx.fillText('CHANGES', cx, cy);
  cy += lh;
  const lines = quote.lines || [];
  font(ctx, fs * 0.92, false);
  if (!lines.length) {
    ctx.fillStyle = 'rgba(180,200,190,0.5)';
    ctx.fillText('* pick a weapon, rounds, smoke or a repair above', cx + 8, cy);
    cy += lh;
  }
  const room = Math.floor((pw * 0.52) / (fs * 0.92 * 0.62));
  for (let i = 0; i < lines.length && i < 6; i++) {
    const l = lines[i];
    const sign = l.delta > 0 ? `- $${l.delta}` : `+ $${-l.delta}`;
    const colour = l.delta > 0 ? '#ffd8a0' : '#a9f0c0';
    ctx.fillStyle = 'rgba(220,236,226,0.9)';
    const t = `* ${l.text}`;
    ctx.fillText(t.length > room ? t.slice(0, room - 1) + '…' : t, cx + 8, cy);
    // the seconds this line books at the wrench, in its own column
    if (l.dur > 0) {
      ctx.textAlign = 'right';
      ctx.fillStyle = 'rgba(150,220,255,0.8)';
      ctx.fillText(`${l.dur.toFixed(1)}s`, cx + pw * 0.82, cy);
      ctx.textAlign = 'left';
      ctx.fillStyle = 'rgba(220,236,226,0.9)';
    }
    ctx.textAlign = 'right';
    ctx.fillStyle = colour;
    ctx.fillText(sign, cx + pw * 0.7, cy);
    ctx.textAlign = 'left';
    cy += lh;
  }
  const total = quote.total || 0;
  const short = total > money;
  const dur = quote.dur || 0;
  font(ctx, fs * 1.05, true);
  ctx.fillStyle = short ? '#ff8a8a' : total > 0 ? '#ffd8a0' : '#a9f0c0';
  const totalText = total > 0 ? `TOTAL: $-${total}` : `TOTAL: +$${-total}`;
  ctx.fillText(lines.length ? `${totalText}   ${dur.toFixed(1)}s` : '', cx, cy + lh * 0.15);
  if (short && lines.length) {
    ctx.textAlign = 'right';
    ctx.fillStyle = '#ff8a8a';
    ctx.fillText('NOT ENOUGH MONEY', cx + pw * 0.93, cy + lh * 0.15);
    ctx.textAlign = 'left';
  }

  // confirm / one-click repair+rearm / cancel
  const bw = pw * 0.2;
  const gap = pw * 0.02;
  const bh = ph * 0.11;
  const by = py + ph * 0.81;
  const bx = px + pw * 0.5 - (bw * 3 + gap * 2) / 2;
  const enabled = lines.length > 0 && !short;
  btnAt('confirm', bx, by, bw, bh, ['CONFIRM', '(ENTER)'], { disabled: !enabled, active: enabled });
  // the everyday stop, in one click: every module welded, every rack filled.  Its
  // price and its time are on the button because it is pressed without opening
  // anything in the draft.
  const quick = s.shopQuick || NO_QUICK;
  btnAt('quickall', bx + bw + gap, by, bw, bh,
    ['REPAIR & REARM', `$${quick.total} · ${quick.dur.toFixed(1)}s`],
    { disabled: !quick.ok, active: quick.ok });
  btnAt('cancel', bx + (bw + gap) * 2, by, bw, bh, ['CANCEL', '(ESC)'], {});

  // a pulled-down weapon list: it floats over the panel and its entries are
  // pushed last, so hitUi hands them the click before anything underneath
  if (s.shopDrop === 0 || s.shopDrop === 1) {
    const arm = s.shopDrop;
    const x0 = COL_X[arm];
    const list = s.buyable || [];
    const ih = fs * 2.5;
    const ly = boxY + boxH + 2;
    ctx.fillStyle = 'rgba(7,13,15,0.98)';
    ctx.fillRect(x0, ly, colW, ih * list.length);
    ctx.strokeStyle = 'rgba(150,230,190,0.85)';
    ctx.lineWidth = 1.4;
    ctx.strokeRect(x0 + 0.5, ly + 0.5, colW - 1, ih * list.length - 1);
    const fitted = ((meta.arms || [])[arm] || {}).wid;
    for (let i = 0; i < list.length; i++) {
      const wid = list[i];
      const wd = weapons[wid] || NO_BUYABLE;
      const load = cash((wd.mag + wd.reserve) * (wd.ammo_cost || 0));
      const sum = (wd.cost || 0) + load;
      const mark = wid === fitted ? '\u2022 ' : '  ';
      btnAt(`pick:${arm}:${wid}`, x0, ly + i * ih, colW, ih,
        [`${mark}${wd.name}`, `HW $${wd.cost} + RDS $${load} = $${sum}`],
        { active: draft.arms[arm] === wid });
    }
  }

  // the cursor, since the pointer lock is released while the panel is up
  if (mouse[0] >= 0) cursor(ctx, mouse[0], mouse[1]);
  ctx.restore();
}
