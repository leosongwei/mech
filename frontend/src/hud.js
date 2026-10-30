// 2D HUD overlay: crosshairs, lock box, module health, weapons, minimap.

import { FACTION_COLORS } from './scene.js';
import { ZOOM_LEVELS } from './input.js';
import { drawIcon } from './icons.js';

const ARM_COLORS = ['#5fe6ff', '#ffb04a'];
const ARM_NAMES = ['L', 'R'];
const FAR_COLOR = '#ff4a4a';        // distance past the fire control's reach
const LEAD_COLOR = '#f2f6f4';       // the box round a hull with a pre-aim solution
const LEAD_RING = 7;                // px, radius of a pre-aim circle
const RADAR_PX = 78;                // px, outer radius of the threat circle
const RADAR_MARGIN = 22;            // px between the dial and the screen edges
// Everything that has to stay clear of the dial - the gear button and the
// message stack - is laid out in a column this far from the right edge.
const RADAR_COL = RADAR_PX * 2 + RADAR_MARGIN * 2 + 8;

export class HUD {
  constructor(canvas) {
    this.canvas = canvas;
    this.ctx = canvas.getContext('2d');
    this.dpr = 1;
    this.mapCanvas = null;
  }

  setMap(map) {
    const N = 320;
    const c = document.createElement('canvas');
    c.width = N;
    c.height = N;
    const g = c.getContext('2d');
    const img = g.createImageData(N, N);
    const sx = map.w / N, sz = map.h / N;
    const palette = {
      0: [64, 96, 48], 1: [70, 68, 64], 2: [134, 128, 118],
      3: [40, 74, 38], 4: [104, 104, 108], 5: [92, 94, 100], 6: [92, 94, 100],
      7: [26, 52, 26],
    };
    for (let j = 0; j < N; j++) {
      for (let i = 0; i < N; i++) {
        const x = Math.min(map.w - 1, (i * sx) | 0);
        const z = Math.min(map.h - 1, (j * sz) | 0);
        const t = map.tiles[z * map.w + x];
        const p = palette[t] || palette[0];
        const o = (j * N + i) * 4;
        img.data[o] = p[0];
        img.data[o + 1] = p[1];
        img.data[o + 2] = p[2];
        img.data[o + 3] = 255;
      }
    }
    g.putImageData(img, 0, 0);
    this.mapCanvas = c;
    this.mapSize = { w: map.w, h: map.h, tile: map.tile };
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
    this.s = s;

    if (s.own) {
      this._leads(s);
      this._centreCross(s, w, h);
      this._crosshairs(s, w, h);
      this._lock(s);
    }
    this._minimap(s);
    this._topBar(s, w);
    this._weapons(s, w, h);
    if (s.own) this._optics(s, w, h);
    if (s.own) this._radar(s, w, h);
    this._speed(s, h);
    this._modules(s, h);
    this._messages(s, w);
    this._status(s, w, h);
    this._work(s, w, h);
    if (s.hitFlash > 0.01) {
      const a = Math.min(0.5, s.hitFlash * 0.5);
      const g = ctx.createRadialGradient(w / 2, h / 2, Math.min(w, h) * 0.22,
        w / 2, h / 2, Math.max(w, h) * 0.62);
      g.addColorStop(0, 'rgba(150,0,0,0)');
      g.addColorStop(1, `rgba(170,10,10,${a})`);
      ctx.fillStyle = g;
      ctx.fillRect(0, 0, w, h);
    }
    if (s.dead) this._death(s, w, h);
    if (s.winner >= 0) this._gameover(s, w, h);
    if (!s.shopOpen && !s.settingsOpen) this._gear(s, w, h);
    if (s.shopOpen) this._shop(s, w, h);
    if (s.settingsOpen) this._settings(s, w, h);
    if (s.help) this._help(w, h);
  }

  _project(x, y, z) {
    const m = this.vp;
    if (!m) return null;
    const cx = m[0] * x + m[4] * y + m[8] * z + m[12];
    const cy = m[1] * x + m[5] * y + m[9] * z + m[13];
    const cw = m[3] * x + m[7] * y + m[11] * z + m[15];
    if (cw <= 0.01) return null;
    const nx = cx / cw, ny = cy / cw;
    if (nx < -1.3 || nx > 1.3 || ny < -1.3 || ny > 1.3) return null;
    return [(nx * 0.5 + 0.5) * this.w, (1 - (ny * 0.5 + 0.5)) * this.h];
  }

  // Pre-aim: the hull that was picked up gets a white box, and each arm gets a
  // circle on the point it has to be pointed at to hit it, joined to the box by
  // a line in the arm's own colour.  The circles differ - a rocket is in the air
  // far longer than a shell - so the pilot reads the ring, not the hull.
  _leads(s) {
    const leads = s.leads;
    if (!leads || !leads.length) return;
    const ctx = this.ctx;
    ctx.save();
    ctx.lineWidth = 1.2;
    for (const t of leads) {
      ctx.globalAlpha = 0.85;
      ctx.strokeStyle = LEAD_COLOR;
      ctx.strokeRect(t.x - t.hw, t.y - t.hh, t.hw * 2, t.hh * 2);
      for (const m of t.marks) {
        const col = ARM_COLORS[m.arm];
        // the line leaves the box at its edge, so it reads as "this hull, that
        // far ahead" instead of scribbling across the target
        const dx = m.x - t.x, dy = m.y - t.y;
        const len = Math.hypot(dx, dy);
        if (len < 3) continue;
        const ux = dx / len, uy = dy / len;
        const edge = Math.min(t.hw / Math.max(Math.abs(ux), 1e-6),
          t.hh / Math.max(Math.abs(uy), 1e-6));
        ctx.globalAlpha = 0.55;
        ctx.strokeStyle = col;
        ctx.beginPath();
        ctx.moveTo(t.x + ux * edge, t.y + uy * edge);
        ctx.lineTo(m.x - ux * LEAD_RING, m.y - uy * LEAD_RING);
        ctx.stroke();
        ctx.globalAlpha = 0.95;
        ctx.beginPath();
        ctx.arc(m.x, m.y, LEAD_RING, 0, 6.284);
        ctx.stroke();
        ctx.fillStyle = col;
        ctx.beginPath();
        ctx.arc(m.x, m.y, 1.2, 0, 6.284);
        ctx.fill();
      }
    }
    ctx.restore();
  }

  // The gun line proper: a white saltire fixed in the middle of the sight, the
  // same size as the pre-aim ring so the two read as one system.  The number
  // above it is the range the cross is pointing at - the sight ray's first
  // contact, out to the 4 km the settings share; past that there is nothing to
  // range against and it shows a red dash.
  _centreCross(s, w, h) {
    if (s.settingsOpen || s.shopOpen) return;
    const ctx = this.ctx;
    const x = w / 2, y = h / 2;
    const r = 9;
    const range = s.sightRange;
    const far = range === null || range === undefined;
    ctx.save();
    ctx.strokeStyle = '#ffffff';
    ctx.lineWidth = 1.7;
    ctx.beginPath();
    ctx.moveTo(x - r, y - r); ctx.lineTo(x + r, y + r);
    ctx.moveTo(x + r, y - r); ctx.lineTo(x - r, y + r);
    ctx.stroke();
    ctx.textAlign = 'center';
    ctx.font = '11px "Courier New", monospace';
    ctx.fillStyle = far ? FAR_COLOR : '#ffffff';
    ctx.fillText(far ? '-' : `${Math.round(range)}m`, x, y - r - 6);
    ctx.restore();
  }

  _crosshairs(s, w, h) {
    const ctx = this.ctx;
    for (let arm = 0; arm < 2; arm++) {
      const c = s.crosshairs[arm];
      if (!c) continue;
      const wid = s.own ? s.own.weapon[arm] : 0;
      const wd = (s.weapons && s.weapons[wid]) || {};
      if (wd.lock) {
        // IR seeker: the reticle *is* the lock circle - anything inside it
        // can be tracked, and a hull that leaves it drops the lock.  With a
        // missile in each arm the two rings are the same size and stagger to
        // their own side, each in its own hand's colour, so the pilot can
        // tell which tube is tracking.
        const both = s.own && s.weapons[s.own.weapon[0]] && s.weapons[s.own.weapon[0]].lock
          && s.weapons[s.own.weapon[1]] && s.weapons[s.own.weapon[1]].lock;
        this._seekerRing(s, c, arm, w, both ? (arm === 0 ? -0.4 : 0.4) : 0);
        continue;
      }
      const col = ARM_COLORS[arm];
      ctx.save();
      ctx.strokeStyle = col;
      ctx.fillStyle = col;
      ctx.lineWidth = 1.4;
      ctx.globalAlpha = c.disabled ? 0.25 : 0.95;
      const x = c.screen[0], y = c.screen[1];
      const r = 9;
      // brackets
      ctx.beginPath();
      ctx.moveTo(x - r - 6, y); ctx.lineTo(x - r, y);
      ctx.moveTo(x + r, y); ctx.lineTo(x + r + 6, y);
      ctx.moveTo(x, y - r - 6); ctx.lineTo(x, y - r);
      ctx.moveTo(x, y + r); ctx.lineTo(x, y + r + 6);
      ctx.stroke();
      ctx.beginPath();
      ctx.arc(x, y, 1.4, 0, 6.284);
      ctx.fill();
      // impact point
      if (c.hit) {
        ctx.globalAlpha = 0.55;
        ctx.beginPath();
        ctx.arc(x, y, 3.4, 0, 6.284);
        ctx.stroke();
      }
      ctx.globalAlpha = 0.85;
      ctx.font = '10px "Courier New", monospace';
      // both reticles sit on the screen centre when the sight is level: push
      // the right-arm label down so the two readings never overlap
      const other = s.crosshairs[1 - arm];
      const stacked = other && Math.abs(other.screen[0] - x) < 80
        && Math.abs(other.screen[1] - y) < 24;
      const ly = y + 4 + (stacked && arm === 1 ? 18 : 0);
      // past 4 km the sight ray finds nothing to range against: rangefinding
      // drops out and the reading is a bare red dash
      if (c.far) ctx.fillStyle = FAR_COLOR;
      const read = c.hit && !c.far ? `${Math.round(c.dist)}m` : '-';
      ctx.fillText(`${ARM_NAMES[arm]} ${read}`, x + r + 10, ly);
      ctx.restore();
    }
  }

  // 5% of the view width across: the seeker's field of view.  The ring keeps
  // its arm colour (cyan left, amber right) in every state - a lock is shown
  // by the thicker rim, the diamond and the full progress arc.  `shift` slides
  // the ring sideways, in radii, when both arms carry a seeker.
  _seekerRing(s, c, arm, w, shift) {
    const ctx = this.ctx;
    const r = 0.5 * (s.lockFrac || 0.05) * w;
    const x = c.screen[0] + (shift || 0) * r;
    const y = c.screen[1];
    const col = ARM_COLORS[arm];
    const L = s.lock;
    const p = L ? Math.max(0, Math.min(1, L.progress)) : 0;
    const locked = p >= 1;
    const held = L && p > 0;
    ctx.save();
    // idle rim
    ctx.globalAlpha = c.disabled ? 0.2 : 0.55;
    ctx.strokeStyle = col;
    ctx.lineWidth = 1.3;
    ctx.beginPath();
    ctx.arc(x, y, r, 0, 6.284);
    ctx.stroke();
    if (held) {
      // acquisition progress runs round the rim, in the arm's own colour
      ctx.globalAlpha = c.disabled ? 0.2 : 1;
      ctx.strokeStyle = col;
      ctx.lineWidth = locked ? 3.2 : 2.4;
      ctx.beginPath();
      ctx.arc(x, y, r, -Math.PI / 2, -Math.PI / 2 + p * 6.284);
      ctx.stroke();
    }
    ctx.globalAlpha = c.disabled ? 0.2 : 0.95;
    ctx.fillStyle = col;
    ctx.beginPath();
    ctx.arc(x, y, 1.5, 0, 6.284);
    ctx.fill();
    if (locked) {
      ctx.beginPath();
      ctx.moveTo(x, y - 6); ctx.lineTo(x + 6, y); ctx.lineTo(x, y + 6); ctx.lineTo(x - 6, y);
      ctx.closePath();
      ctx.fill();
    }
    ctx.globalAlpha = c.disabled ? 0.25 : 0.85;
    ctx.fillStyle = col;
    ctx.font = '10px "Courier New", monospace';
    const label = L && L.dist ? `${Math.round(L.dist)}m` : '-';
    ctx.fillText(`${ARM_NAMES[arm]} ${label}`, x + r + 8, y + (arm === 0 ? -4 : 16));
    ctx.restore();
  }

  // the tracked hull: a small marker on the target itself, in the colour of
  // the seeker that is tracking it, so the pilot can tell which vehicle was
  // picked from inside the circle
  _lock(s) {
    const L = s.lock;
    if (!L || !L.screen) return;
    const ctx = this.ctx;
    const [x, y] = L.screen;
    const p = Math.max(0, Math.min(1, L.progress));
    const locked = p >= 1;
    let arm = 0;
    if (s.own && s.weapons) {
      const w0 = s.weapons[s.own.weapon[0]] || {};
      const w1 = s.weapons[s.own.weapon[1]] || {};
      if (w1.lock && !w0.lock) arm = 1;
    }
    ctx.save();
    ctx.strokeStyle = ARM_COLORS[arm];
    ctx.lineWidth = locked ? 2 : 1.3;
    ctx.globalAlpha = 0.9;
    const m = locked ? 9 : 7;
    ctx.beginPath();
    ctx.moveTo(x, y - m); ctx.lineTo(x + m, y); ctx.lineTo(x, y + m); ctx.lineTo(x - m, y);
    ctx.closePath();
    ctx.stroke();
    ctx.restore();
  }

  _minimap(s) {
    if (!this.mapCanvas) return;
    const ctx = this.ctx;
    const size = 210;
    const x0 = 16, y0 = 16;
    ctx.save();
    ctx.globalAlpha = 0.94;
    ctx.drawImage(this.mapCanvas, x0, y0, size, size);
    ctx.strokeStyle = 'rgba(160,220,190,0.5)';
    ctx.lineWidth = 1;
    ctx.strokeRect(x0, y0, size, size);
    const S = this.mapSize;
    const kx = size / (S.w * S.tile);
    const kz = size / (S.h * S.tile);

    // towns
    for (const t of s.towns || []) {
      const tx = x0 + t.x * kx, tz = y0 + t.z * kz;
      const f = t.owner;
      ctx.fillStyle = f < 0 ? 'rgba(200,180,255,0.75)' : `rgba(${Math.round(FACTION_COLORS[f][0] * 255)},${Math.round(FACTION_COLORS[f][1] * 255)},${Math.round(FACTION_COLORS[f][2] * 255)},0.95)`;
      ctx.fillRect(tx - 2.5, tz - 2.5, 5, 5);
      if (t.progress > 0.02) {
        ctx.strokeStyle = 'rgba(255,255,255,0.8)';
        ctx.lineWidth = 1;
        ctx.beginPath();
        ctx.arc(tx, tz, 6, -1.57, -1.57 + 6.283 * Math.min(1, t.progress));
        ctx.stroke();
      }
    }
    // units
    for (const e of s.entities.values()) {
      if (e.type === 5) {
        ctx.fillStyle = '#fff';
        ctx.fillRect(x0 + e.x * kx - 4, y0 + e.z * kz - 4, 8, 8);
        continue;
      }
      if (e.type === 6) continue;
      const f = FACTION_COLORS[e.faction] || [0.8, 0.8, 0.8];
      const px = x0 + e.x * kx, pz = y0 + e.z * kz;
      const sz = e.type === 4 ? 3 : 1.6;
      ctx.fillStyle = `rgb(${f[0] * 255 | 0},${f[1] * 255 | 0},${f[2] * 255 | 0})`;
      ctx.fillRect(px - sz, pz - sz, sz * 2, sz * 2);
    }
    // own
    if (s.own) {
      const px = x0 + s.own.x * kx, pz = y0 + s.own.z * kz;
      const yaw = s.own.yaw;
      ctx.strokeStyle = '#ffffff';
      ctx.lineWidth = 1.5;
      ctx.beginPath();
      ctx.moveTo(px, pz);
      ctx.lineTo(px + Math.cos(yaw) * 14, pz + Math.sin(yaw) * 14);
      ctx.stroke();
      ctx.fillStyle = '#7ce0a0';
      ctx.beginPath();
      ctx.arc(px, pz, 3, 0, 6.284);
      ctx.fill();
    }
    ctx.restore();
  }

  _topBar(s, w) {
    const ctx = this.ctx;
    const m = s.meta || {};
    ctx.save();
    ctx.font = 'bold 15px "Courier New", monospace';
    ctx.fillStyle = '#d8f5e2';
    ctx.textAlign = 'center';
    ctx.fillText(s.range ? 'RANGE' : `WAVE ${m.wave || 0}`, w / 2, 26);
    ctx.font = '12px "Courier New", monospace';
    const mm = Math.floor((m.time || 0) / 60);
    const ss = Math.floor((m.time || 0) % 60);
    ctx.fillStyle = 'rgba(200,230,210,0.7)';
    ctx.fillText(`${mm}:${ss < 10 ? '0' : ''}${ss}`, w / 2, 42);
    if (s.range) {
      let targets = 0;
      for (const e of s.entities.values()) if (e.type === 3) targets++;
      ctx.fillStyle = 'rgba(200,230,210,0.55)';
      ctx.fillText(`TARGETS ${targets}`, w / 2, 58);
    }

    // base integrity bars
    let y = 58;
    for (const e of s.entities.values()) {
      if (e.type !== 5) continue;
      const f = FACTION_COLORS[e.faction];
      const maxHp = ((s.units || {})[5] || {}).hp;
      const ratio = Math.max(0, Math.min(1, e.hp[0] / ((maxHp && maxHp[0]) || 4000)));
      const bw = 150;
      const bx = w / 2 - bw / 2;
      ctx.fillStyle = 'rgba(0,0,0,0.45)';
      ctx.fillRect(bx, y, bw, 5);
      ctx.fillStyle = `rgb(${f[0] * 255 | 0},${f[1] * 255 | 0},${f[2] * 255 | 0})`;
      ctx.fillRect(bx, y, bw * ratio, 5);
      ctx.fillStyle = 'rgba(220,240,230,0.75)';
      ctx.font = '10px "Courier New", monospace';
      ctx.fillText(e.faction === s.playerFaction ? 'ALLIED BASE' : 'ENEMY BASE', w / 2, y - 3);
      y += 18;
    }
    ctx.restore();
  }

  _weapons(s, w, h) {
    const ctx = this.ctx;
    const m = s.meta || {};
    const defs = s.weapons || {};
    ctx.save();
    ctx.font = '12px "Courier New", monospace';
    const y0 = h - 96;
    const panelW = 250;
    for (let arm = 0; arm < 2; arm++) {
      const x0 = w / 2 + (arm === 0 ? -panelW - 10 : 10);
      const e = s.own;
      if (!e) continue;
      const wid = e.weapon ? e.weapon[arm] : 0;
      const wd = defs[wid] || {};
      const mod = arm === 0 ? 2 : 3;
      const dead = e.hp[mod] <= 0;
      ctx.fillStyle = 'rgba(10,16,20,0.62)';
      ctx.fillRect(x0, y0, panelW, 56);
      ctx.strokeStyle = ARM_COLORS[arm];
      ctx.globalAlpha = dead ? 0.3 : 0.9;
      ctx.lineWidth = 1;
      ctx.strokeRect(x0 + 0.5, y0 + 0.5, panelW - 1, 55);
      ctx.fillStyle = ARM_COLORS[arm];
      ctx.font = 'bold 11px "Courier New", monospace';
      ctx.fillText(`${ARM_NAMES[arm]}-ARM  ${dead ? 'DESTROYED' : (wd.name || 'EMPTY')}`, x0 + 8, y0 + 16);
      if (!dead) {
        ctx.fillStyle = '#cfe6d6';
        ctx.font = '12px "Courier New", monospace';
        const mag = e.mag[arm], ammo = e.ammo[arm];
        // a weapon with no magazine (the rocket pod) shows a single pool
        ctx.fillText(ammo > 0 ? `${mag} / ${ammo}` : `${mag}`, x0 + 8, y0 + 36);
        const reloading = (e.flags >> (2 + arm)) & 1;
        if (reloading) {
          ctx.fillStyle = '#ffcc66';
          ctx.fillText('RELOADING', x0 + 90, y0 + 36);
        }
        ctx.fillStyle = 'rgba(200,230,210,0.55)';
        ctx.font = '10px "Courier New", monospace';
        ctx.fillText(`${(wd.range / 1000).toFixed(1)}km  ${wd.desc || ''}`, x0 + 8, y0 + 50);
      }
      ctx.globalAlpha = 1;
    }
    ctx.restore();
  }

  // wheel driven sight magnification: 1x / 4x / 10x / 20x
  _optics(s, w, h) {
    const ctx = this.ctx;
    const levels = ZOOM_LEVELS;
    const idx = Math.max(0, Math.min(levels.length - 1, s.zoomIdx | 0));
    const slot = 44;
    const total = slot * levels.length;
    const x0 = w / 2 - total / 2;
    const y = h - 140;
    ctx.save();
    ctx.textAlign = 'center';
    ctx.font = '10px "Courier New", monospace';
    ctx.fillStyle = 'rgba(150,190,175,0.55)';
    ctx.fillText('OPTICS', w / 2, y - 14);
    ctx.font = '12px "Courier New", monospace';
    for (let i = 0; i < levels.length; i++) {
      const cx = x0 + slot * i + slot / 2;
      const on = i === idx;
      if (on) {
        ctx.fillStyle = 'rgba(95,230,255,0.14)';
        ctx.fillRect(x0 + slot * i, y - 11, slot, 15);
        ctx.strokeStyle = 'rgba(95,230,255,0.8)';
        ctx.lineWidth = 1;
        ctx.strokeRect(x0 + slot * i + 0.5, y - 10.5, slot - 1, 14);
      }
      ctx.fillStyle = on ? '#9ceaff' : `rgba(170,200,190,${0.34 + 0.16 * (i === idx - 1 || i === idx + 1 ? 1 : 0)})`;
      ctx.fillText(`${levels[i]}x`, cx, y);
    }
    // live magnification while the sight is travelling between steps, out to
    // the side so it never lands on the zero row
    if (Math.abs(s.zoom - levels[idx]) > 0.02) {
      ctx.fillStyle = 'rgba(156,234,255,0.7)';
      ctx.font = '10px "Courier New", monospace';
      ctx.textAlign = 'left';
      ctx.fillText(`${s.zoom.toFixed(1)}x`, w / 2 + total / 2 + 10, y - 5);
      ctx.textAlign = 'center';
    }
    this._zeroBar(s, w, y + 34, slot);
    ctx.restore();
  }

  // The sight setting, one row under the optics: the range the gun is zeroed
  // to, stepped with CTRL+wheel.  A round drops 4 m over 1.5 km and 70 m over
  // 4 km, so this is the number that decides where it lands; it also sets how
  // much lead a moving target gets.
  _zeroBar(s, w, y, slot) {
    const steps = s.zeroSteps || [];
    if (!steps.length) return;
    const ctx = this.ctx;
    const idx = Math.max(0, Math.min(steps.length - 1, s.zeroIdx | 0));
    const total = slot * steps.length;
    const x0 = w / 2 - total / 2;
    ctx.save();
    ctx.textAlign = 'center';
    ctx.font = '10px "Courier New", monospace';
    ctx.fillStyle = 'rgba(150,190,175,0.55)';
    ctx.fillText('ZERO km', w / 2, y - 13);
    ctx.font = '11px "Courier New", monospace';
    for (let i = 0; i < steps.length; i++) {
      const cx = x0 + slot * i + slot / 2;
      const on = i === idx;
      if (on) {
        ctx.fillStyle = 'rgba(255,176,74,0.14)';
        ctx.fillRect(x0 + slot * i, y - 10, slot, 13);
        ctx.strokeStyle = 'rgba(255,176,74,0.8)';
        ctx.lineWidth = 1;
        ctx.strokeRect(x0 + slot * i + 0.5, y - 9.5, slot - 1, 12);
      }
      ctx.fillStyle = on ? '#ffce8a'
        : `rgba(170,200,190,${0.30 + 0.14 * (i === idx - 1 || i === idx + 1 ? 1 : 0)})`;
      ctx.fillText(`${steps[i] / 1000}`, cx, y);
    }
    ctx.restore();
  }

  // Warning circle, in the top right corner: a bird's eye of everything the
  // threat receiver has a track on, drawn in the hull's own frame - straight up
  // is where the mech is looking.
  // The green dot in the middle is the pilot, the red diamonds are the rounds
  // the self-defence radar has *seen*, the short red spokes are rounds it can
  // hear closing but has no range on (they blink, a bearing is not a track),
  // and the red arrows are laser designators (somebody's sight is on us), each
  // pointing at the hull from where the designator sits, with a dashed beam
  // running in along its line of sight.
  // The rim turns red for an *incoming* round only (`warn`): the dial plots
  // everything it hears, including a missile on its way to somebody else.
  _radar(s, w, h) {
    const ctx = this.ctx;
    const R = RADAR_PX;
    // metres at the edge of the dial: the radar's own discovery range, so the
    // scale under the marks is the machine's and not a constant (1000 m for a
    // mech, 500 m for an APC)
    const ring = s.radarRing > 0 ? s.radarRing : 1000;
    const cx = w - R - RADAR_MARGIN;
    const cy = R + RADAR_MARGIN + 10;   // up in the corner, the gear sits beside it
    const own = s.own;
    const threats = s.threats || [];
    const lasers = s.lasers || [];
    ctx.save();
    // a black disc under the dial: the marks have to read against any sky or
    // ground, so the receiver owns that patch of the screen
    ctx.globalAlpha = 1;
    ctx.fillStyle = '#000';
    ctx.beginPath();
    ctx.arc(cx, cy, R + 3, 0, 6.284);
    ctx.fill();
    // dial
    ctx.globalAlpha = 0.5;
    ctx.strokeStyle = threats.some((t) => t.warn)
      ? 'rgba(255,90,80,0.85)' : 'rgba(150,190,175,0.5)';
    ctx.lineWidth = 1.2;
    ctx.beginPath();
    ctx.arc(cx, cy, R, 0, 6.284);
    ctx.stroke();
    ctx.globalAlpha = 0.22;
    ctx.beginPath();
    ctx.arc(cx, cy, R * 0.5, 0, 6.284);
    ctx.stroke();
    // heading tick at the top: "up is forward"
    ctx.globalAlpha = 0.6;
    ctx.beginPath();
    ctx.moveTo(cx, cy - R - 5); ctx.lineTo(cx, cy - R + 2);
    ctx.stroke();
    ctx.font = '9px "Courier New", monospace';
    ctx.fillStyle = 'rgba(160,200,185,0.7)';
    // the scale under the heading tick, in the short form the corner has room
    // for - 1000 m reads as "1.0 km"
    ctx.fillText(ring >= 1000 ? `${(ring / 1000).toFixed(1)} km` : `${Math.round(ring)} m`,
      cx - 11, cy - R - 8);
    // laser designators: bearing 0 is straight ahead (screen up), positive to
    // the right.  The sight line is a thin dashed spoke inwards - a beam, not
    // a track - and the source itself gets a solid arrow pointing at the hull,
    // so it reads at a glance which of the marks is the laser.
    for (const l of lasers) {
      const a = Math.atan2(l.z - own.z, l.x - own.x) - own.yaw;
      const lr = Math.min(R, (l.dist / ring) * R);
      const sx = Math.sin(a), sy = -Math.cos(a);
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
      const px = sy, py = -sx;                    // perpendicular, in dial space
      const tipX = cx + sx * (lr - 13), tipY = cy + sy * (lr - 13);
      const baseX = cx + sx * (lr + 2), baseY = cy + sy * (lr + 2);
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
    // missiles.  A round the radar has seen gets a diamond at its true bearing
    // and true range; one it can only hear closing gets a spoke from the edge
    // of the dial to the pilot, because the bearing is all the receiver has -
    // it blinks so it never reads as a track.
    const blink = (performance.now() / 1000 * 5) % 1 < 0.5;
    for (const t of threats) {
      const a = Math.atan2(t.z - own.z, t.x - own.x) - own.yaw;
      const sa = Math.sin(a), ca = -Math.cos(a);   // dial space: up is forward
      if (!t.seen) {
        if (!blink) continue;
        ctx.globalAlpha = 0.9;
        ctx.strokeStyle = FAR_COLOR;
        ctx.lineWidth = 1.6;
        ctx.beginPath();
        ctx.moveTo(cx + sa * R, cy + ca * R);
        ctx.lineTo(cx, cy);
        ctx.stroke();
        continue;
      }
      const r = Math.min(R, (t.dist / ring) * R);
      const x = cx + sa * r;
      const y = cy + ca * r;
      ctx.globalAlpha = 0.95;
      ctx.fillStyle = FAR_COLOR;
      ctx.save();
      ctx.translate(x, y);
      ctx.rotate(a - Math.PI / 4);         // a square standing on its corner
      ctx.fillRect(-2.8, -2.8, 5.6, 5.6);
      ctx.restore();
    }
    // the pilot
    ctx.globalAlpha = 0.95;
    ctx.fillStyle = '#7ce0a0';
    ctx.beginPath();
    ctx.arc(cx, cy, 2.6, 0, 6.284);
    ctx.fill();
    // countermeasures: pulls left, or the seconds until the next one
    ctx.textAlign = 'center';
    ctx.font = '11px "Courier New", monospace';
    if (s.smoke !== null && s.smoke !== undefined) {
      ctx.fillStyle = s.smoke > 0 ? 'rgba(230,245,238,0.85)' : 'rgba(255,120,110,0.9)';
      const label = s.smoke > 0 ? `SMOKE ${s.smoke}  [Q]`
        : (s.smokeCd > 0 ? `SMOKE ${Math.ceil(s.smokeCd)}s` : 'SMOKE');
      ctx.fillText(label, cx, cy + R + 16);
    }
    ctx.restore();
  }

  // ground speed, sitting right above the hull bars: the mech now takes three
  // seconds to wind up to 120 km/h, so the pilot needs to see the number
  _speed(s, h) {
    if (!s.own) return;
    const ctx = this.ctx;
    const kmh = Math.max(0, Math.round(s.speed || 0));
    const x0 = 16, y0 = h - 164, w = 168;
    ctx.save();
    ctx.font = '11px "Courier New", monospace';
    ctx.fillStyle = 'rgba(8,14,18,0.6)';
    ctx.fillRect(x0, y0, w, 20);
    // bar along the bottom of the panel, full at the 180 km/h top speed
    const r = Math.min(1, kmh / 180);
    ctx.fillStyle = 'rgba(95,230,255,0.22)';
    ctx.fillRect(x0, y0 + 16, w, 3);
    ctx.fillStyle = kmh > 150 ? '#5fe6ff' : 'rgba(95,230,255,0.8)';
    ctx.fillRect(x0, y0 + 16, w * r, 3);
    ctx.fillStyle = 'rgba(230,245,238,0.85)';
    ctx.fillText('SPEED', x0 + 6, y0 + 12);
    ctx.textAlign = 'right';
    ctx.fillStyle = kmh > 150 ? '#5fe6ff' : '#e8f4ee';
    ctx.fillText(`${kmh} km/h`, x0 + w - 6, y0 + 12);
    ctx.restore();
  }

  _modules(s, h) {
    if (!s.own) return;
    const ctx = this.ctx;
    const e = s.own;
    // the hull is one pool (chassis + torso share it), so slot 1 has nothing
    // of its own to show; the arms are slot 2 and 3
    const bars = [[0, 'HULL'], [2, 'L.ARM'], [3, 'R.ARM']];
    const maxes = s.mechHp || [];
    const x0 = 16, y0 = h - 118;
    ctx.save();
    ctx.font = '11px "Courier New", monospace';
    for (let i = 0; i < bars.length; i++) {
      const slot = bars[i][0];
      const max = Math.max(1, maxes[slot] || 1);
      const r = Math.max(0, Math.min(1, e.hp[slot] / max));
      const y = y0 + i * 20;
      ctx.fillStyle = 'rgba(8,14,18,0.6)';
      ctx.fillRect(x0, y, 168, 15);
      const col = r > 0.6 ? '#5ad07a' : r > 0.3 ? '#e6c249' : '#e0503c';
      ctx.fillStyle = col;
      ctx.fillRect(x0, y, 168 * r, 15);
      ctx.fillStyle = '#e8f4ee';
      ctx.fillText(bars[i][1], x0 + 6, y + 11);
      ctx.fillStyle = 'rgba(230,245,238,0.85)';
      ctx.fillText(`${Math.round(e.hp[slot])}`, x0 + 128, y + 11);
    }
    ctx.restore();
  }

  // The rearm bar, low in the middle of the screen: while a job runs the hull
  // will not move and the triggers are dead, so the pilot is told why, for how
  // long, and how much of it is done.  `meta.work` carries the seconds left,
  // the length of the whole job and the label the server made of the lines.
  _work(s, w, h) {
    const wk = (s.meta || {}).work;
    if (!wk || !(wk.total > 0)) return;
    const ctx = this.ctx;
    const bw = Math.min(420, w * 0.44), bh = 10;
    const x = (w - bw) / 2;
    const y = h * 0.78;
    const r = Math.max(0, Math.min(1, 1 - (wk.t || 0) / wk.total));
    ctx.save();
    ctx.fillStyle = 'rgba(8,14,18,0.72)';
    ctx.fillRect(x - 3, y - 3, bw + 6, bh + 6);
    ctx.fillStyle = 'rgba(95,230,255,0.2)';
    ctx.fillRect(x, y, bw, bh);
    ctx.fillStyle = '#5fe6ff';
    ctx.fillRect(x, y, bw * r, bh);
    ctx.strokeStyle = 'rgba(95,230,255,0.8)';
    ctx.lineWidth = 1.2;
    ctx.strokeRect(x + 0.5, y + 0.5, bw - 1, bh - 1);
    ctx.textAlign = 'center';
    ctx.font = 'bold 12px "Courier New", monospace';
    ctx.fillStyle = '#e8f4ee';
    ctx.fillText(`${wk.label || 'REARM'}  ${(wk.t || 0).toFixed(1)}s`, w / 2, y - 9);
    ctx.font = '11px "Courier New", monospace';
    ctx.fillStyle = 'rgba(255,207,90,0.9)';
    ctx.fillText('HOLD POSITION · TRIGGERS SAFE', w / 2, y + bh + 16);
    ctx.restore();
  }

  _messages(s, w) {
    const ctx = this.ctx;
    ctx.save();
    ctx.textAlign = 'right';
    ctx.font = '12px "Courier New", monospace';
    // clear of the gear button, and of the warning circle beyond it
    let y = 62;
    for (let i = s.messages.length - 1; i >= 0 && i > s.messages.length - 8; i--) {
      const m = s.messages[i];
      const a = Math.max(0, Math.min(1, m.life));
      ctx.globalAlpha = a * 0.9;
      ctx.fillStyle = m.color || '#cfe6d6';
      ctx.fillText(m.text, w - RADAR_COL, y);
      y += 17;
    }
    ctx.restore();
  }

  _status(s, w, h) {
    const ctx = this.ctx;
    const m = s.meta || {};
    ctx.save();
    ctx.textAlign = 'left';
    ctx.font = 'bold 20px "Courier New", monospace';
    ctx.fillStyle = '#a9f0c0';
    ctx.fillText(`$${m.money || 0}`, 16, 252);
    ctx.font = '12px "Courier New", monospace';
    ctx.fillStyle = 'rgba(210,235,220,0.8)';
    ctx.fillText(`KILLS ${m.kills || 0}   DEATHS ${m.deaths || 0}   SCORE ${m.score || 0}`, 16, 270);

    // team scoreboard
    let y = 292;
    for (const p of m.players || []) {
      const f = FACTION_COLORS[p.faction];
      ctx.fillStyle = `rgba(${f[0] * 255 | 0},${f[1] * 255 | 0},${f[2] * 255 | 0},0.9)`;
      ctx.fillText(`${p.name}  ${p.kills}/${p.deaths}`, 16, y);
      y += 15;
    }

    if (s.atBase) {
      ctx.font = '12px "Courier New", monospace';
      ctx.fillStyle = '#ffe08a';
      ctx.fillText('[B] REARM / REPAIR', 16, y + 10);
      y += 22;
    }

    ctx.textAlign = 'right';
    ctx.fillStyle = 'rgba(180,210,195,0.65)';
    ctx.font = '11px "Courier New", monospace';
    ctx.fillText(`FPS ${s.stats.fps | 0}  DRAWS ${s.stats.draws}  INST ${s.stats.instances}`, w - 16, h - 16);
    ctx.fillText(s.helpOpen ? 'H - HIDE CONTROLS' : 'H - CONTROLS', w - 16, h - 32);
    ctx.restore();
  }

  _death(s, w, h) {
    const ctx = this.ctx;
    const m = s.meta || {};
    ctx.save();
    ctx.fillStyle = 'rgba(40,0,0,0.32)';
    ctx.fillRect(0, 0, w, h);
    ctx.textAlign = 'center';
    ctx.fillStyle = '#ff9a9a';
    ctx.font = 'bold 34px "Courier New", monospace';
    ctx.fillText('MECH DESTROYED', w / 2, h / 2 - 20);
    ctx.fillStyle = '#e8d6d6';
    ctx.font = '16px "Courier New", monospace';
    ctx.fillText(`RE-DEPLOY IN ${(m.respawn || 0).toFixed(1)}s`, w / 2, h / 2 + 16);
    ctx.font = '12px "Courier New", monospace';
    ctx.fillStyle = 'rgba(220,200,200,0.75)';
    ctx.fillText(s.range ? 'RESPAWN ON THE RANGE FIRING LINE'
      : 'RESPAWN ONE TOWN BACK FROM WHERE YOU FELL, OR AT THE BASE', w / 2, h / 2 + 44);
    ctx.restore();
  }

  _gameover(s, w, h) {
    const ctx = this.ctx;
    const won = s.winner === s.playerFaction;
    ctx.save();
    ctx.fillStyle = won ? 'rgba(0,40,20,0.45)' : 'rgba(40,0,10,0.45)';
    ctx.fillRect(0, 0, w, h);
    ctx.textAlign = 'center';
    ctx.font = 'bold 46px "Courier New", monospace';
    ctx.fillStyle = won ? '#9dffb0' : '#ff8a8a';
    ctx.fillText(won ? 'VICTORY' : 'DEFEAT', w / 2, h / 2 - 10);
    ctx.font = '16px "Courier New", monospace';
    ctx.fillStyle = '#e8f4ee';
    ctx.fillText('ENEMY COMMAND BASE DESTROYED', w / 2, h / 2 + 24);
    const t = (s.meta && s.meta.end_timer) || 0;
    ctx.font = '13px "Courier New", monospace';
    ctx.fillText(`NEW MATCH IN ${t.toFixed(0)}s`, w / 2, h / 2 + 52);
    ctx.restore();
  }

  _help(w, h) {
    const ctx = this.ctx;
    const lines = [
      'W A S D      MOVE (relative to view)',
      'MOUSE        TORSO YAW / ARM ELEVATION',
      'WHEEL        SCOPE 1x / 4x / 10x / 20x',
      'CTRL + WHEEL   SIGHT ZERO 0.5 / 1 / 1.5 ... 4 km',
      'ALT + MOUSE  FREE LOOK',
      'LMB / RMB    LEFT / RIGHT ARM',
      'Q            HOT SMOKE (breaks incoming missiles)',
      'B            REARM / REPAIR PANEL (at base or own town)',
      'ENTER / ESC  CONFIRM / CANCEL THE PANEL',
      'ESC          RELEASE MOUSE',
    ];
    const bh = lines.length * 24 + 34;
    const by = h / 2 - bh / 2;
    ctx.save();
    ctx.fillStyle = 'rgba(6,12,16,0.8)';
    ctx.fillRect(w / 2 - 235, by, 470, bh);
    ctx.strokeStyle = 'rgba(120,220,160,0.5)';
    ctx.strokeRect(w / 2 - 235, by, 470, bh);
    ctx.fillStyle = '#cfe6d6';
    ctx.font = '13px "Courier New", monospace';
    ctx.textAlign = 'left';
    lines.forEach((l, i) => ctx.fillText(l, w / 2 - 210, by + 30 + i * 24));
    ctx.restore();
  }

  // ------------------------------------------------------------ rearm shop
  // A mouse panel: every box pushes its rectangle into `this.hot`, and the game
  // maps a click back to the id it finds there (last one wins, so a small
  // button sitting inside a big one still gets the click).
  _shop(s, w, h) {
    const ctx = this.ctx;
    const pw = Math.min(980, w * 0.9);
    const ph = Math.min(700, h * 0.92);
    const px = (w - pw) / 2;
    const py = (h - ph) / 2;
    const meta = s.meta || {};
    const weapons = s.weapons || {};
    const draft = s.shopDraft || { arms: [null, null], rearm: [false, false], repair: false, smoke: false };
    const quote = s.shopQuote || { lines: [], total: 0, ok: false };
    const cfg = s.rearm || {};
    const money = meta.money || 0;
    const mouse = s.mouse || [-1, -1];
    const refund = cfg.sell_refund === undefined ? 0.5 : cfg.sell_refund;
    const perHp = cfg.repair_cost === undefined ? 1 : cfg.repair_cost;
    const smokeCost = cfg.smoke_cost === undefined ? 250 : cfg.smoke_cost;
    const smokeFull = cfg.smoke_charges === undefined ? 10 : cfg.smoke_charges;
    const round = (v) => Math.floor(v + 0.5);
    const fs = Math.max(11, Math.min(16, ph * 0.026));

    this.hot = [];
    ctx.save();
    ctx.textBaseline = 'middle';
    ctx.fillStyle = 'rgba(4,8,10,0.66)';
    ctx.fillRect(0, 0, w, h);
    ctx.fillStyle = 'rgba(10,18,20,0.95)';
    ctx.fillRect(px, py, pw, ph);
    ctx.strokeStyle = 'rgba(140,225,180,0.7)';
    ctx.lineWidth = 1.6;
    ctx.strokeRect(px + 0.5, py + 0.5, pw - 1, ph - 1);

    const btn = (id, x, y, bw, bh, lines, opts) => this._btn(mouse, fs, id, x, y, bw, bh, lines, opts);

    // header: money on the left, close cross top right
    ctx.textAlign = 'left';
    ctx.font = `bold ${fs * 1.1}px "Courier New", monospace`;
    ctx.fillStyle = '#a9f0c0';
    ctx.fillText(`$${money}`, px + pw * 0.05, py + ph * 0.05);
    ctx.font = `${fs * 0.85}px "Courier New", monospace`;
    ctx.fillStyle = 'rgba(180,210,195,0.7)';
    ctx.fillText('REARM / REPAIR', px + pw * 0.05 + 84, py + ph * 0.05);
    btn('close', px + pw - ph * 0.09 - 14, py + 14, ph * 0.09, ph * 0.09, ['X'], {});

    // arms: a hardware box with a cycle arrow either side, a REARM under it
    const colW = pw * 0.26;
    const colX = [px + pw * 0.055, px + pw * 0.685];
    const boxY = py + ph * 0.20;
    const boxH = ph * 0.17;
    for (let arm = 0; arm < 2; arm++) {
      const a = (meta.arms || [])[arm] || { wid: 0, hp: 0, hpmax: 1, mag: 0, reserve: 0 };
      const picked = draft.arms[arm];
      const shownWid = picked === null ? a.wid : picked;
      const wd = weapons[shownWid] || { name: 'EMPTY', mag: 0, reserve: 0, ammo_cost: 0 };
      const rounds = picked === null ? a.mag + a.reserve : wd.mag + wd.reserve;
      const hpPct = Math.round(100 * a.hp / Math.max(1, a.hpmax));
      const x0 = colX[arm];
      const tag = arm === 0 ? 'L-ARM' : 'R-ARM';
      ctx.textAlign = 'left';
      ctx.font = `bold ${fs * 0.95}px "Courier New", monospace`;
      ctx.fillStyle = ARM_COLORS[arm];
      ctx.fillText(tag, x0 + 4, boxY - fs * 1.1);
      const open = s.shopDrop === arm;
      btn(arm === 0 ? 'drop0' : 'drop1', x0, boxY, colW, boxH,
        [wd.name, `${rounds} RDS · HP ${hpPct}%`, 'CLICK TO PICK A WEAPON'],
        { active: picked !== null || open });
      // the chevron that says "this opens"
      ctx.save();
      ctx.textAlign = 'center';
      ctx.fillStyle = open ? '#ffcf5a' : 'rgba(196,224,208,0.8)';
      ctx.font = `bold ${fs * 1.1}px "Courier New", monospace`;
      ctx.fillText(open ? '\u25B4' : '\u25BE', x0 + colW - fs * 1.1, boxY + boxH - fs * 0.9);
      ctx.restore();

      const full = wd.mag + wd.reserve;
      const missing = full - rounds;
      const canRearm = missing > 0 && picked === null;
      const cost = round(missing * (wd.ammo_cost || 0));
      btn(arm === 0 ? 'lrearm' : 'rrearm', x0 + (colW - pw * 0.16) / 2, boxY + boxH + ph * 0.04,
        pw * 0.16, ph * 0.085,
        ['REARM', canRearm ? `${missing} RDS  $${cost}` : 'FULL'],
        { disabled: !canRearm, active: draft.rearm[arm] });
    }

    // middle column: smoke on top, repair below
    const smokeNow = meta.smoke === null || meta.smoke === undefined ? smokeFull : meta.smoke;
    const smokeSpent = smokeNow < smokeFull || (meta.smoke_cd || 0) > 0;
    btn('smoke', px + pw * 0.5 - pw * 0.105, py + ph * 0.05, pw * 0.21, ph * 0.105,
      ['REARM SMOKE', `$${smokeCost} · ${smokeNow}/${smokeFull}`],
      { active: draft.smoke && smokeSpent, disabled: !smokeSpent });
    const mechHp = s.mechHp || [0, 0, 0, 0];
    const hpNow = meta.hp || [];
    let need = 0;
    for (let i = 0; i < 4; i++) need += Math.max(0, (mechHp[i] || 0) - (hpNow[i] || 0));
    need = Math.round(need);
    const repairCost = round(need * perHp);
    btn('repair', px + pw * 0.5 - pw * 0.105, py + ph * 0.20, pw * 0.21, ph * 0.30,
      ['REPAIR', need > 0 ? `${need} HP` : 'NO DAMAGE', need > 0 ? `$${repairCost}` : ''],
      { disabled: need <= 0 || meta.mech === undefined || meta.mech < 0,
        active: draft.repair });

    // CHANGES: one line per line of the draft, then the bill
    const cx = px + pw * 0.055;
    let cy = py + ph * 0.60;
    const lh = fs * 1.55;
    ctx.textAlign = 'left';
    ctx.font = `bold ${fs}px "Courier New", monospace`;
    ctx.fillStyle = '#cfe6d6';
    ctx.fillText('CHANGES', cx, cy);
    cy += lh;
    const lines = quote.lines || [];
    ctx.font = `${fs * 0.92}px "Courier New", monospace`;
    if (!lines.length) {
      ctx.fillStyle = 'rgba(180,200,190,0.5)';
      ctx.fillText('* pick a weapon, rounds, smoke or a repair above', cx + 8, cy);
      cy += lh;
    }
    for (const l of lines.slice(0, 6)) {
      const sign = l.delta > 0 ? `- $${l.delta}` : `+ $${-l.delta}`;
      const colour = l.delta > 0 ? '#ffd8a0' : '#a9f0c0';
      ctx.fillStyle = 'rgba(220,236,226,0.9)';
      const room = Math.floor((pw * 0.52) / (fs * 0.92 * 0.62));
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
    ctx.font = `bold ${fs * 1.05}px "Courier New", monospace`;
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
    btn('confirm', bx, by, bw, bh,
      ['CONFIRM', '(ENTER)'], { disabled: !enabled, active: enabled });
    // the everyday stop, in one click: every module welded, every rack filled.
    // Its price and its time are on the button because it is pressed without
    // opening anything in the draft.
    const quick = s.shopQuick || { total: 0, dur: 0, ok: false };
    btn('quickall', bx + bw + gap, by, bw, bh,
      ['REPAIR & REARM', `$${quick.total} · ${quick.dur.toFixed(1)}s`],
      { disabled: !quick.ok, active: quick.ok });
    btn('cancel', bx + (bw + gap) * 2, by, bw, bh, ['CANCEL', '(ESC)'], {});

    // a pulled-down weapon list: it floats over the panel and its entries are
    // pushed last, so hitUi hands them the click before anything underneath
    if (s.shopDrop === 0 || s.shopDrop === 1) {
      const arm = s.shopDrop;
      const x0 = colX[arm];
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
        const wd = weapons[wid] || { name: '?', mag: 0, reserve: 0, ammo_cost: 0, cost: 0 };
        const load = round((wd.mag + wd.reserve) * (wd.ammo_cost || 0));
        const total = (wd.cost || 0) + load;
        const mark = wid === fitted ? '\u2022 ' : '  ';
        btn(`pick:${arm}:${wid}`, x0, ly + i * ih, colW, ih,
          [`${mark}${wd.name}`, `HW $${wd.cost} + RDS $${load} = $${total}`],
          { active: draft.arms[arm] === wid });
      }
    }

    // the cursor, since the pointer lock is released while the panel is up
    if (mouse[0] >= 0) {
      ctx.strokeStyle = '#e8fff2';
      ctx.lineWidth = 1.4;
      ctx.beginPath();
      ctx.moveTo(mouse[0] - 7, mouse[1]);
      ctx.lineTo(mouse[0] + 7, mouse[1]);
      ctx.moveTo(mouse[0], mouse[1] - 7);
      ctx.lineTo(mouse[0], mouse[1] + 7);
      ctx.stroke();
    }
    ctx.restore();
  }

  // The gear, in the top right - pushed in from the corner so the warning
  // circle can have it: the one HUD button, and the way into the settings
  // panel.  It needs a free pointer (ESC), so it sits dim until the mouse is
  // released and brightens under the cursor.
  _gear(s, w, h) {
    const ctx = this.ctx;
    const mouse = s.mouse || [-1, -1];
    const S = 32;
    const x = w - S - RADAR_COL;
    const y = 10;
    const hv = mouse[0] >= x && mouse[0] <= x + S && mouse[1] >= y && mouse[1] <= y + S;
    this.gearRect = { x, y, w: S, h: S };
    const cx = x + S / 2;
    const cy = y + S / 2;
    ctx.save();
    ctx.fillStyle = hv ? 'rgba(66,120,92,0.75)' : 'rgba(10,18,24,0.62)';
    ctx.fillRect(x, y, S, S);
    ctx.strokeStyle = hv ? 'rgba(180,245,205,0.95)' : 'rgba(120,220,160,0.45)';
    ctx.lineWidth = 1.2;
    ctx.strokeRect(x + 0.5, y + 0.5, S - 1, S - 1);
    const rgba = hv ? '#e8fff2' : 'rgba(190,226,204,0.8)';
    // the Material Symbols gear, or a plain toothed wheel for the first frames
    // before the subset font lands (and if it ever fails to load)
    if (!drawIcon(ctx, 'settings', cx, cy, S * 0.62, rgba)) {
      ctx.fillStyle = rgba;
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
      ctx.fillStyle = hv ? 'rgba(20,40,30,0.95)' : 'rgba(10,18,24,0.9)';
      ctx.beginPath();
      ctx.arc(cx, cy, 3.4, 0, Math.PI * 2);
      ctx.fill();
    }
    ctx.restore();
  }

  // ------------------------------------------------------------- settings
  // The gear opens the options panel: the master volume and, when the server was
  // started with MECH_DEBUG=1, the debug knobs.  Same mouse machinery as the
  // rearm panel; the game maps the clicks back through hitUi/volRect.
  _settings(s, w, h) {
    const ctx = this.ctx;
    const mouse = s.mouse || [-1, -1];
    const dbg = !!s.debug;
    const vol = s.volume === undefined ? 1 : s.volume;
    const alarm = s.alarmVolume === undefined ? 1 : s.alarmVolume;
    const god = !!(s.meta && s.meta.god);
    const pw = Math.min(560, w * 0.84);
    const fs = Math.max(11, Math.min(15, pw * 0.03));
    const pad = fs * 1.5;
    const rowH = fs * 2.4;
    const closeH = fs * 2.2;
    // the panel is as tall as its content, so the debug block costs nothing
    // when the server shipped without it
    let contentH = fs * 2.6 + fs * 1.5 + rowH * 2 + fs * 1.4 * 2 + closeH;
    if (dbg) contentH += fs * 1.5 + rowH + fs * 1.4;
    const ph = Math.min(h * 0.92, contentH + pad * 2);
    const px = (w - pw) / 2;
    const py = (h - ph) / 2;
    const cx = px + pad;
    const cw = pw - pad * 2;
    const btn = (id, x, y, bw, bh, lines, opts) => this._btn(mouse, fs, id, x, y, bw, bh, lines, opts);

    this.hot = [];
    this.volRect = null;
    this.alarmRect = null;
    ctx.save();
    ctx.textBaseline = 'middle';
    ctx.fillStyle = 'rgba(4,8,10,0.55)';
    ctx.fillRect(0, 0, w, h);
    ctx.fillStyle = 'rgba(10,18,20,0.96)';
    ctx.fillRect(px, py, pw, ph);
    ctx.strokeStyle = 'rgba(140,225,180,0.7)';
    ctx.lineWidth = 1.6;
    ctx.strokeRect(px + 0.5, py + 0.5, pw - 1, ph - 1);

    let y = py + pad;
    ctx.textAlign = 'left';
    ctx.font = `bold ${fs * 1.35}px "Courier New", monospace`;
    ctx.fillStyle = '#a9f0c0';
    ctx.fillText('SETTINGS', cx, y + fs * 0.95);
    y += fs * 2.6;

    // close cross in the corner (the CLOSE button below does the same thing)
    const xw = rowH * 0.84;
    btn('close', px + pw - pad - xw, py + pad - fs * 0.3, xw, xw, [], {});
    const xc = px + pw - pad - xw / 2;
    const yc = py + pad - fs * 0.3 + xw / 2;
    if (!drawIcon(ctx, 'close', xc, yc, xw * 0.55, '#e8fff2')) {
      ctx.strokeStyle = '#e8fff2';
      ctx.lineWidth = 1.6;
      const d = xw * 0.22;
      ctx.beginPath();
      ctx.moveTo(xc - d, yc - d); ctx.lineTo(xc + d, yc + d);
      ctx.moveTo(xc + d, yc - d); ctx.lineTo(xc - d, yc + d);
      ctx.stroke();
    }

    // ---- sound: master and the threat receiver, one slider each.  Every row
    // is a step-down, an absolute bar and a step-up; the bar is `this.<id>Rect`
    // so the game can read a click back as a position on it.
    ctx.font = `bold ${fs * 0.85}px "Courier New", monospace`;
    ctx.fillStyle = 'rgba(180,210,195,0.75)';
    ctx.fillText('SOUND', cx, y + fs * 0.45);
    y += fs * 1.5;
    const slider = (id, label, value, fill) => {
      const step = fs * 3;
      btn(`${id}-`, cx + fs * 1.9, y, step, rowH, ['-'], { disabled: value <= 0 });
      const barX = cx + fs * 1.9 + step + fs * 0.5;
      const barW = cw - fs * 1.9 - step * 2 - fs;
      btn(id, barX, y, barW, rowH, [], {});
      ctx.fillStyle = fill;
      ctx.fillRect(barX + 2, y + 2, Math.max(0, (barW - 4) * value), rowH - 4);
      ctx.textAlign = 'center';
      ctx.font = `bold ${fs * 0.95}px "Courier New", monospace`;
      ctx.fillStyle = '#e8fff2';
      ctx.fillText(`${label} ${Math.round(value * 100)}%`, barX + barW / 2, y + rowH / 2);
      btn(`${id}+`, barX + barW + fs * 0.5, y, step, rowH, ['+'], { disabled: value >= 1 });
      this[`${id}Rect`] = { x: barX, w: barW };
      y += rowH + fs * 1.4;
    };
    drawIcon(ctx, 'volume_up', cx + fs * 0.9, y + rowH / 2, fs * 1.5, 'rgba(190,226,204,0.8)');
    slider('vol', 'MASTER', vol, 'rgba(124,224,160,0.45)');
    // the alarm row has no glyph of its own (the font subset is exactly the
    // icons in icons.json), so it gets a warning triangle in the same slot
    const ix = cx + fs * 0.9, iy = y + rowH / 2, ir = fs * 0.85;
    ctx.strokeStyle = 'rgba(255,176,74,0.85)';
    ctx.lineWidth = 1.4;
    ctx.beginPath();
    ctx.moveTo(ix, iy - ir);
    ctx.lineTo(ix + ir, iy + ir * 0.8);
    ctx.lineTo(ix - ir, iy + ir * 0.8);
    ctx.closePath();
    ctx.stroke();
    slider('alarm', 'ALARM', alarm, 'rgba(255,176,74,0.45)');

    // ---- debug: only when the server says the messages are accepted
    if (dbg) {
      ctx.textAlign = 'left';
      ctx.font = `bold ${fs * 0.85}px "Courier New", monospace`;
      ctx.fillStyle = 'rgba(180,210,195,0.75)';
      ctx.fillText('DEBUG', cx, y + fs * 0.45);
      y += fs * 1.5;
      const gap = fs * 0.6;
      const bw = (cw - gap * 2) / 3;
      btn('respawn', cx, y, bw, rowH, ['RESPAWN', 'skip the timer'], {});
      btn('restart', cx + bw + gap, y, bw, rowH, ['RESTART', 'fresh round'], {});
      btn('god', cx + (bw + gap) * 2, y, bw, rowH,
        [god ? 'GOD ON' : 'GOD OFF', god ? 'no damage' : 'take damage'], { active: god });
      y += rowH + fs * 1.4;
    }

    btn('close', px + (pw - cw * 0.4) / 2, y, cw * 0.4, closeH, ['CLOSE', '(ESC)'], {});

    // the cursor, since the pointer lock is released while the panel is up
    if (mouse[0] >= 0) {
      ctx.textAlign = 'left';
      ctx.strokeStyle = '#e8fff2';
      ctx.lineWidth = 1.4;
      ctx.beginPath();
      ctx.moveTo(mouse[0] - 7, mouse[1]);
      ctx.lineTo(mouse[0] + 7, mouse[1]);
      ctx.moveTo(mouse[0], mouse[1] - 7);
      ctx.lineTo(mouse[0], mouse[1] + 7);
      ctx.stroke();
    }
    ctx.restore();
  }

  _hover(mouse, x, y, bw, bh) {
    return mouse[0] >= x && mouse[0] <= x + bw && mouse[1] >= y && mouse[1] <= y + bh;
  }

  // one clickable box: pushes its rectangle into `this.hot`, draws the frame and
  // up to three lines of text.  A dead button still swallows the click, so it
  // cannot fall through to whatever is underneath it.
  _btn(mouse, fs, id, x, y, bw, bh, lines, opts) {
    const ctx = this.ctx;
    opts = opts || {};
    const hv = this._hover(mouse, x, y, bw, bh);
    this.hot.push({ id: opts.disabled ? 'disabled' : id, x, y, w: bw, h: bh });
    ctx.fillStyle = opts.disabled ? 'rgba(18,24,26,0.5)'
      : hv ? 'rgba(66,120,92,0.5)' : 'rgba(14,26,28,0.7)';
    ctx.fillRect(x, y, bw, bh);
    ctx.strokeStyle = opts.active ? '#ffcf5a'
      : opts.disabled ? 'rgba(120,140,130,0.3)' : 'rgba(150,230,190,0.7)';
    ctx.lineWidth = opts.active ? 2.2 : 1.2;
    ctx.strokeRect(x + 0.5, y + 0.5, bw - 1, bh - 1);
    ctx.textAlign = 'center';
    const ls = lines || [];
    const lh = fs * 1.4;
    let ty = y + bh / 2 - (ls.length - 1) * lh / 2;
    for (let i = 0; i < ls.length; i++) {
      const big = i === 0;
      let size = big ? fs * 1.05 : fs * 0.78;
      const est = ls[i].length * size * 0.62;        // Courier New is monospaced
      if (est > bw - 12) size = Math.max(7.5, size * (bw - 12) / est);
      ctx.font = `${big ? 'bold ' : ''}${size}px "Courier New", monospace`;
      ctx.fillStyle = opts.disabled ? 'rgba(150,166,158,0.5)'
        : big ? '#e8fff2' : 'rgba(196,224,208,0.85)';
      ctx.fillText(ls[i], x + bw / 2, ty);
      ty += lh;
    }
  }

  // last box wins: small arrows are pushed after the frame they sit in
  hitUi(x, y) {
    const hot = this.hot || [];
    for (let i = hot.length - 1; i >= 0; i--) {
      const b = hot[i];
      if (x >= b.x && x <= b.x + b.w && y >= b.y && y <= b.y + b.h) return b.id;
    }
    return null;
  }
}
