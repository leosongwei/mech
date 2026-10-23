// 2D HUD overlay: crosshairs, lock box, module health, weapons, minimap.

import { FACTION_COLORS } from './scene.js';
import { ZOOM_LEVELS } from './input.js';

const ARM_COLORS = ['#5fe6ff', '#ffb04a'];
const ARM_NAMES = ['L', 'R'];
const FAR_COLOR = '#ff4a4a';        // distance past the fire control's reach

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
      0: [56, 82, 44], 1: [112, 104, 88], 2: [120, 116, 112],
      3: [30, 58, 30], 4: [96, 96, 100], 5: [78, 80, 88], 6: [78, 80, 88],
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
      this._crosshairs(s, w, h);
      this._lock(s);
    }
    this._minimap(s);
    this._topBar(s, w);
    this._weapons(s, w, h);
    if (s.own) this._optics(s, w, h);
    this._modules(s, h);
    this._messages(s, w);
    this._status(s, w, h);
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
      const ly = y + 4 + (stacked && arm === 1 ? 14 : 0);
      // red = the sight point is past what the fire control can zero for; a
      // trailing + means the round expires in the air before it gets there,
      // so the number is as far as it flies, not an impact
      if (c.far) ctx.fillStyle = FAR_COLOR;
      const read = c.hit ? `${Math.round(c.dist)}m`
        : c.far ? `${Math.round(c.dist)}m+` : '-';
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
      const ratio = Math.max(0, Math.min(1, e.hp[0] / 6500));
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
    const y = h - 112;
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
    // live magnification while the sight is travelling between steps
    if (Math.abs(s.zoom - levels[idx]) > 0.02) {
      ctx.fillStyle = 'rgba(156,234,255,0.7)';
      ctx.font = '10px "Courier New", monospace';
      ctx.fillText(`${s.zoom.toFixed(1)}x`, w / 2, y + 20);
    }
    ctx.restore();
  }

  _modules(s, h) {
    if (!s.own) return;
    const ctx = this.ctx;
    const e = s.own;
    const labels = ['CHASSIS', 'TORSO', 'L.ARM', 'R.ARM'];
    const maxes = [700, 900, 450, 450];
    const x0 = 16, y0 = h - 138;
    ctx.save();
    ctx.font = '11px "Courier New", monospace';
    for (let i = 0; i < 4; i++) {
      const r = Math.max(0, Math.min(1, e.hp[i] / maxes[i]));
      const y = y0 + i * 20;
      ctx.fillStyle = 'rgba(8,14,18,0.6)';
      ctx.fillRect(x0, y, 168, 15);
      const col = r > 0.6 ? '#5ad07a' : r > 0.3 ? '#e6c249' : '#e0503c';
      ctx.fillStyle = col;
      ctx.fillRect(x0, y, 168 * r, 15);
      ctx.fillStyle = '#e8f4ee';
      ctx.fillText(labels[i], x0 + 6, y + 11);
      ctx.fillStyle = 'rgba(230,245,238,0.85)';
      ctx.fillText(`${Math.round(e.hp[i])}`, x0 + 128, y + 11);
    }
    ctx.restore();
  }

  _messages(s, w) {
    const ctx = this.ctx;
    ctx.save();
    ctx.textAlign = 'right';
    ctx.font = '12px "Courier New", monospace';
    let y = 40;
    for (let i = s.messages.length - 1; i >= 0 && i > s.messages.length - 8; i--) {
      const m = s.messages[i];
      const a = Math.max(0, Math.min(1, m.life));
      ctx.globalAlpha = a * 0.9;
      ctx.fillStyle = m.color || '#cfe6d6';
      ctx.fillText(m.text, w - 16, y);
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
      ctx.font = '11px "Courier New", monospace';
      ctx.fillStyle = '#ffe08a';
      ctx.fillText('AT BASE - REARM:', 16, y + 8);
      y += 22;
      const list = s.buyable || [];
      const defs = s.weapons || {};
      for (let i = 0; i < list.length; i++) {
        const wd = defs[list[i]];
        if (!wd) continue;
        const can = (m.money || 0) >= wd.cost;
        ctx.fillStyle = can ? 'rgba(210,240,220,0.85)' : 'rgba(160,170,165,0.5)';
        ctx.fillText(`[${i + 1}] ${wd.name}  $${wd.cost}`, 16, y);
        y += 14;
      }
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
    ctx.fillText('RESPAWN IS AT THE MOST ADVANCED OWNED TOWN OR THE BASE', w / 2, h / 2 + 44);
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
      'ALT + MOUSE  FREE LOOK',
      'LMB / RMB    LEFT / RIGHT ARM',
      '1-5          BUY LEFT ARM WEAPON (at base)',
      'SHIFT+1-5    BUY RIGHT ARM WEAPON (at base)',
      'ESC          RELEASE MOUSE',
    ];
    ctx.save();
    ctx.fillStyle = 'rgba(6,12,16,0.8)';
    ctx.fillRect(w / 2 - 230, h / 2 - 132, 460, 254);
    ctx.strokeStyle = 'rgba(120,220,160,0.5)';
    ctx.strokeRect(w / 2 - 230, h / 2 - 132, 460, 254);
    ctx.fillStyle = '#cfe6d6';
    ctx.font = '13px "Courier New", monospace';
    ctx.textAlign = 'left';
    lines.forEach((l, i) => ctx.fillText(l, w / 2 - 205, h / 2 - 100 + i * 24));
    ctx.restore();
  }
}
