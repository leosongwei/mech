// Glue: connection, camera, aiming prediction, render loop and HUD state.

import { Net } from './net.js';
import { MapData, predictImpact } from './map.js';
import { Input } from './input.js';
import { GameAudio } from './audio.js';
import { Renderer, makeCamera } from './renderer.js';
import { drawUnits } from './scene.js';
import { HUD } from './hud.js';
import { Particles } from './particles.js';
import { clamp, dirFrom, invert4, mat4, wrapPi } from './math.js';

export class Game {
  constructor() {
    this.glCanvas = document.getElementById('gl');
    this.hudCanvas = document.getElementById('hud');
    this.bootEl = document.getElementById('boot');
    this.errEl = document.getElementById('err');
    this.net = new Net((location.protocol === 'https:' ? 'wss://' : 'ws://') + location.host + '/ws');
    this.input = new Input(this.glCanvas);
    this.audio = new GameAudio();
    this.renderer = new Renderer(this.glCanvas);
    this.hud = new HUD(this.hudCanvas);
    this.particles = new Particles();
    this.flashes = new Map();
    this._trailPos = new Map();
    this.map = null;
    this.defs = { weapons: {}, units: {}, mech: { muzzles: {} } };
    this.messages = [];
    this.hitFlash = 0;
    this.shake = 0;
    this.sendAcc = 0;
    this.fps = 60;
    this.lastHpSum = -1;
    this.ownHpFrac = 1;
    this.predictions = [null, null];
    this.crosshairs = [null, null];
    this.helpOpen = false;
    this.started = false;
    this.winner = -1;
  }

  async boot() {
    this._status('CONNECTING...');
    try {
      const init = await this.net.connect();
      this.init = init;
      this.defs.weapons = init.weapons;
      this.defs.units = init.units;
      this.defs.mech = init.mech;
      this.buyable = init.buyable;
      this.playerFaction = init.faction;
      this._status('LOADING 16 KM MAP...');
      this.map = await MapData.load(init.map);
      this.renderer.setMap(this.map);
      this.hud.setMap(this.map);
      this._status('READY');
      this.net.onFx = (ev) => this._fx(ev);
      this.net.onMessage = (msg) => {
        if (msg.t === 'info') this._message(msg.msg, '#ffe08a');
        if (msg.t === 'joined') {
          this.playerFaction = msg.faction;
          this._message(`DEPLOYED AS ${msg.name}`, '#a9f0c0');
        }
      };
      this.input.onBuy = (idx, right) => {
        const wid = this.buyable[idx];
        if (wid !== undefined) this.net.send({ t: 'buy', arm: right ? 1 : 0, weapon: wid });
      };
      this.input.onToggleHelp = () => { this.helpOpen = !this.helpOpen; };
      this._setupLobby(init);
      document.getElementById('startBtn').addEventListener('click', () => this.start());
      this.ready = true;
    } catch (err) {
      this._status('CONNECTION FAILED - IS THE SERVER RUNNING?');
      this.errEl.textContent = String(err);
      console.error(err);
    }
  }

  _status(text) {
    let p = document.getElementById('bootStatus');
    if (!p) {
      p = document.createElement('p');
      p.id = 'bootStatus';
      this.bootEl.appendChild(p);
    }
    p.textContent = text;
  }

  // ------------------------------------------------------------- lobby
  _setupLobby(init) {
    const defs = this.defs.weapons;
    const list = init.buyable || [];
    const fill = (sel, defWid) => {
      sel.innerHTML = '';
      for (const wid of list) {
        const wd = defs[wid];
        if (!wd) continue;
        const opt = document.createElement('option');
        opt.value = String(wid);
        opt.textContent = `${wd.name}  ${(wd.range / 1000).toFixed(1)}km`;
        if (wid === defWid) opt.selected = true;
        sel.appendChild(opt);
      }
    };
    this.armL = document.getElementById('armL');
    this.armR = document.getElementById('armR');
    fill(this.armL, init.loadout ? init.loadout[0] : 1);
    fill(this.armR, init.loadout ? init.loadout[1] : 3);

    this.nameEl = document.getElementById('pname');
    this.nameEl.value = `PILOT-${init.pid}`;

    this.factionChoice = -1;
    document.querySelectorAll('.fac').forEach((btn) => {
      btn.addEventListener('click', () => {
        document.querySelectorAll('.fac').forEach((b) => b.classList.remove('on'));
        btn.classList.add('on');
        this.factionChoice = parseInt(btn.dataset.fac, 10);
      });
    });
    if (init.faction === 0 || init.faction === 1) {
      // highlight the auto-assigned side but keep AUTO selectable
      this.nameEl.addEventListener('keydown', (e) => e.stopPropagation());
    }

    const describe = () => {
      const a = defs[parseInt(this.armL.value, 10)] || {};
      const b = defs[parseInt(this.armR.value, 10)] || {};
      const fmt = (w) => `${w.name}: ${w.dmg} dmg, ${(w.range / 1000).toFixed(2)} km, ` +
        `${w.rpm} rpm${w.lock ? ', lock-on' : ''} - ${w.desc}`;
      document.getElementById('wdesc').innerHTML =
        `L &nbsp;${fmt(a)}<br>R &nbsp;${fmt(b)}`;
    };
    this.armL.addEventListener('change', describe);
    this.armR.addEventListener('change', describe);
    describe();
  }

  start() {
    if (!this.ready) return;
    this.started = true;
    this.net.send({
      t: 'join',
      name: (this.nameEl && this.nameEl.value.trim()) || `PILOT-${this.init.pid}`,
      faction: this.factionChoice,
      loadout: [parseInt(this.armL.value, 10), parseInt(this.armR.value, 10)],
    });
    this.input.enabled = true;
    this.audio.ensure();
    this.audio.resume();
    this.bootEl.classList.add('hidden');
    this.glCanvas.requestPointerLock();
    this.last = performance.now();
    requestAnimationFrame(() => this.frame());
    this.net.send({ t: 'in', yaw: 0, pitch: 0, mx: 0, mz: 0, fl: false, fr: false });
  }

  _message(text, color) {
    this.messages.push({ text, color, life: 6 });
    if (this.messages.length > 40) this.messages.shift();
  }

  _fx(events) {
    const own = this.ownEntity();
    for (const ev of events) {
      if (ev.k === 'boom') {
        const wd = this.defs.weapons[ev.wid] || {};
        this.particles.impact(ev.x, ev.y, ev.z, ev.r || 1, !!ev.big || (wd.splash || 0) >= 5, wd.kind);
        const d = own ? Math.hypot(ev.x - own.x, ev.z - own.z) : 3000;
        const vol = clamp(1 - d / 1400, 0, 1);
        if (vol > 0.02) this.audio.boom(vol, ev.big);
        this.shake = Math.min(1.2, this.shake + clamp(1 - d / 300, 0, 1) * (ev.big ? 0.9 : 0.35));
      } else if (ev.k === 'shot') {
        if (ev.eid !== undefined) this.flashes.set(ev.eid, { t: 0.07, arm: ev.arm });
        const d = own ? Math.hypot(ev.x - own.x, ev.z - own.z) : 3000;
        const vol = clamp(1 - d / 700, 0, 1);
        if (vol > 0.05) {
          const wd = this.defs.weapons[ev.wid];
          this.audio.shot(ev.wid, wd ? wd.kind : 'bullet', vol * 0.6);
        }
      } else if (ev.k === 'kill') {
        if (ev.pid === this.init.pid) {
          this._message(`+$${ev.reward} KILL`, '#a9f0c0');
          this.audio.hit();
        }
      } else if (ev.k === 'capture') {
        const mine = ev.faction === this.playerFaction;
        this._message(mine ? 'TOWN SECURED' : 'TOWN LOST', mine ? '#7ce0a0' : '#ff9a9a');
      } else if (ev.k === 'respawn') {
        if (ev.pid === this.init.pid) this._message('RE-DEPLOYED', '#a9f0c0');
      } else if (ev.k === 'gameover') {
        this.winner = ev.winner;
      }
    }
  }

  ownEntity() {
    const m = this.net.meta;
    if (!m || m.mech < 0) return null;
    return this.net.entities.get(m.mech) || null;
  }

  frame() {
    const now = performance.now();
    const dt = Math.min(0.1, (now - this.last) / 1000);
    this.last = now;
    this.fps = this.fps * 0.92 + (1 / Math.max(dt, 1e-3)) * 0.08;

    this.input.update(dt);
    for (const m of this.messages) m.life -= dt;
    this.messages = this.messages.filter((m) => m.life > 0);
    this.hitFlash = Math.max(0, this.hitFlash - dt * 1.6);
    this.shake = Math.max(0, this.shake - dt * 2.2);

    this.net.interpolate(1 - Math.exp(-dt * 14));
    this._sendInput(dt);

    const own = this.ownEntity();
    if (own) {
      own.yaw = this.input.yaw;
      own.pitch = this.input.pitch;
    }

    this._checkDamage(own);
    this._noLockFeedback(own, dt);

    this.particles.update(dt);
    for (const [id, f] of this.flashes) {
      f.t -= dt;
      if (f.t <= 0) this.flashes.delete(id);
    }
    this._missileTrails();
    const cam = this._camera(own, dt);
    this.hud.resize();
    this.hud.vp = cam.viewProj;
    this._aim(own);
    this._updateLockTone(own);
    drawUnits(this.renderer, this.net.entities, this.net.projectiles, own ? own.id : -1, this.defs.weapons, this.flashes);
    this.particles.render(this.renderer);
    this.renderer.draw(cam);

    this.hud.draw(this._hudState(own), cam.viewProj);
    requestAnimationFrame(() => this.frame());
  }

  _sendInput(dt) {
    this.sendAcc += dt;
    if (this.sendAcc < 1 / 30) return;
    this.sendAcc = 0;
    const [mx, mz] = this.input.moveVector();
    this.net.send({
      t: 'in',
      yaw: this.input.yaw,
      pitch: this.input.pitch,
      mx, mz,
      fl: this.input.fireL,
      fr: this.input.fireR,
    });
  }

  _missileTrails() {
    // one puff every few metres of flight, per round
    for (const p of this.net.projectiles) {
      if (p.kind === 0) continue;
      this.particles.lastTrail = this._trailPos.get(p.id) || null;
      this.particles.trail(p.x, p.y, p.z, 7);
      this._trailPos.set(p.id, this.particles.lastTrail || [p.x, p.y, p.z]);
    }
    if (this._trailPos.size > 200) this._trailPos.clear();
  }

  _checkDamage(own) {
    if (!own) { this.lastHpSum = -1; return; }
    const sum = own.hp[0] + own.hp[1] + own.hp[2] + own.hp[3];
    if (this.lastHpSum >= 0 && sum < this.lastHpSum) {
      this.hitFlash = Math.min(1, this.hitFlash + 0.55);
      this.audio.hit();
    }
    this.lastHpSum = sum;
  }

  // an IR missile cannot leave the tube without a solid lock: tell the pilot
  // instead of silently swallowing the trigger
  _noLockFeedback(own, dt) {
    if (!own) return;
    this.denyTimer = (this.denyTimer || 0) - dt;
    const fire = [this.input.fireL, this.input.fireR];
    for (let arm = 0; arm < 2; arm++) {
      if (!fire[arm]) continue;
      const wd = this.defs.weapons[own.weapon[arm]];
      if (!wd || !wd.lock) continue;
      const m = this.net.meta || {};
      if (m.lock_progress >= 1 && m.lock_target >= 0) continue;
      if (this.denyTimer > 0) continue;
      this.denyTimer = 0.7;
      this.audio.denied();
      const acquiring = m.lock_target >= 0;
      this._message(acquiring ? 'LOCKING - HOLD SIGHTS ON TARGET' : 'NO LOCK - IR SEEKER HAS NO TARGET',
        '#ff9a9a');
    }
  }

  // ------------------------------------------------------------- aiming
  _muzzle(own, arm) {
    const mod = arm === 0 ? '2' : '3';
    const off = (this.defs.mech.muzzles && this.defs.mech.muzzles[mod]) || [0, 4.3, 0.9];
    const yaw = this.input.yaw;
    const c = Math.cos(yaw), s = Math.sin(yaw);
    const rx = -s, rz = c;
    return [
      own.x + rx * off[0] + c * off[2],
      own.y + off[1],
      own.z + rz * off[0] + s * off[2],
    ];
  }

  _aim(own) {
    this.crosshairs = [null, null];
    if (!own) return;
    const yaw = this.input.yaw;
    const pitch = this.input.pitch;
    const dir = dirFrom(yaw, pitch);
    const units = [];
    for (const e of this.net.entities.values()) {
      if (e.id === own.id) continue;
      const r = e.type === 5 ? 8 : e.type === 4 ? 2.6 : e.type === 3 ? 2.4 : 0.8;
      units.push({ x: e.x, y: e.y + (e.type === 4 ? 3 : 1), z: e.z, r });
    }
    for (let arm = 0; arm < 2; arm++) {
      const wid = own.weapon[arm];
      const wd = this.defs.weapons[wid];
      const mod = arm === 0 ? 2 : 3;
      const disabled = own.hp[mod] <= 0;
      const m = this._muzzle(own, arm);
      let point = null;
      let hit = false;
      let dist = 0;
      if (wd && (wd.kind === 'bullet' || wd.kind === 'rocket')) {
        point = predictImpact(this.map, wd, m[0], m[1], m[2], dir, units, {});
        hit = point.hit;
        dist = point.dist;
      } else if (wd) {
        const far = Math.min(wd.range, 2400);
        point = { x: m[0] + dir[0] * far, y: m[1] + dir[1] * far, z: m[2] + dir[2] * far, hit: false, dist: far };
        dist = far;
      }
      if (!point) continue;
      const scr = this.hud._project(point.x, point.y, point.z);
      this.crosshairs[arm] = {
        screen: scr || [this.hud.w / 2, this.hud.h / 2],
        hit, dist, disabled,
      };
    }
  }

  _updateLockTone(own) {
    const m = this.net.meta;
    if (!own || !m || m.lock_target < 0) {
      this.audio.setLock('none', 0.016);
      this.lockInfo = null;
      return;
    }
    const locked = m.lock_progress >= 1;
    this.audio.setLock(locked ? 'locked' : 'acquiring', 0.016);
    const t = this.net.entities.get(m.lock_target);
    if (!t) { this.lockInfo = null; return; }
    this.lockInfo = {
      screen: this.hud._project(t.x, t.y + (t.type === 4 ? 3.5 : t.type === 5 ? 6 : 1), t.z),
      progress: m.lock_progress,
      dist: Math.hypot(t.x - own.x, t.z - own.z),
      target: t,
    };
  }

  _camera(own, dt) {
    const cam = { viewProj: mat4(), invViewProj: mat4() };
    if (!own) {
      // death cam: orbit the base
      const bx = this.init.bases[this.net.meta && this.net.meta.faction !== undefined
        ? this.net.meta.faction : this.playerFaction];
      const t = this.net.meta ? this.net.meta.time || 0 : 0;
      const ang = t * 0.15;
      const eye = [bx[0] + Math.cos(ang) * 160, this.map.heightAt(bx[0], bx[1]) + 90, bx[1] + Math.sin(ang) * 160];
      makeCamera(cam, eye, [bx[0], this.map.heightAt(bx[0], bx[1]) + 10, bx[1]], 1.0, this.glCanvas.clientWidth / this.glCanvas.clientHeight, 1, this.renderer.far);
      invert4(cam.invViewProj, cam.viewProj);
      return cam;
    }
    const camPitch = clamp(this.input.pitch * 0.62 - 0.12 + this.input.freePitch * 0.8, -1.1, 0.9);
    const yaw = this.input.viewYaw;
    const dir = dirFrom(yaw, camPitch);
    const target = [own.x, own.y + 6.2, own.z];
    // pull the camera in when a building / tree / the ground sits between the
    // mech and the default third person position
    let dist = 22;
    for (let d = 6; d <= 22; d += 2) {
      const px = target[0] - dir[0] * d;
      const pz = target[2] - dir[2] * d;
      const py = target[1] - dir[1] * d + 3.5;
      if (this._structureBlocked(px, py, pz)) {
        dist = Math.max(5, d - 2);
        break;
      }
      if (py < this.map.heightAt(px, pz) + 1.5) {
        dist = Math.max(5, d - 2);
        break;
      }
    }
    let eye = [target[0] - dir[0] * dist, target[1] - dir[1] * dist + 3.5, target[2] - dir[2] * dist];
    const ground = this.map.heightAt(eye[0], eye[2]) + 2.2;
    if (eye[1] < ground) eye[1] = ground;
    if (this.shake > 0.01) {
      const s = this.shake * 0.9;
      eye = [eye[0] + (Math.random() - 0.5) * s, eye[1] + (Math.random() - 0.5) * s, eye[2] + (Math.random() - 0.5) * s];
    }
    const aspect = this.glCanvas.clientWidth / Math.max(1, this.glCanvas.clientHeight);
    makeCamera(cam, eye, target, 1.05, aspect, 1, this.renderer.far);
    invert4(cam.invViewProj, cam.viewProj);
    return cam;
  }

  // command bases are drawn from primitives, not from tiles: treat them as
  // solid so the chase camera never ends up inside a bunker
  _structureBlocked(px, py, pz) {
    for (const e of this.net.entities.values()) {
      if (e.type !== 5) continue;
      const dx = e.x - px, dz = e.z - pz;
      if (dx * dx + dz * dz < 26 * 26 && py < e.y + 30) return true;
    }
    return false;
  }

  _hudState(own) {
    const m = this.net.meta || {};
    const faction = m.faction !== undefined ? m.faction : this.playerFaction;
    let atBase = false;
    if (own) {
      const b = this.init.bases[faction];
      if (Math.hypot(own.x - b[0], own.z - b[1]) < 220) atBase = true;
      for (const t of m.towns || []) {
        if (t.owner === faction && Math.hypot(own.x - t.x, own.z - t.z) < 220) atBase = true;
      }
    }
    return {
      meta: m,
      own,
      entities: this.net.entities,
      weapons: this.defs.weapons,
      buyable: this.buyable,
      towns: m.towns || [],
      playerFaction: faction,
      crosshairs: this.crosshairs,
      lock: this.lockInfo,
      messages: this.messages,
      winner: m.winner !== undefined ? m.winner : this.winner,
      dead: !own,
      atBase,
      hitFlash: this.hitFlash,
      help: false,
      helpOpen: this.helpOpen,
      stats: { fps: this.fps, draws: this.renderer.stats.draws, instances: this.renderer.stats.instances },
    };
  }
}
