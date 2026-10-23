// Glue: connection, camera, aiming prediction, render loop and HUD state.

import { Net } from './net.js';
import { MapData, predictImpact, unitSpheres, zeroPitch } from './map.js';
import { Input } from './input.js';
import { GameAudio } from './audio.js';
import { Renderer, makeCamera } from './renderer.js';
import { drawUnits, drawFirstPersonArms } from './scene.js';
import { HUD } from './hud.js';
import { Particles } from './particles.js';
import { clamp, dirFrom, invert4, mat4 } from './math.js';

const BASE_FOV = 1.05;              // 1x vertical field of view, radians
const AIM_LINE = 60000;             // metres, "infinity" for an unzeroed reticle

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
    this.zoom = 1;              // smoothed, drives the field of view
    this.zoomIdx = 0;           // optics step selected with the wheel
    this.range = false;         // weapon range mode (server started with --range)
    this.lockFrac = 0.05;       // IR lock circle, as a fraction of the view width
    this.wreckPuffs = new Map();    // per-hulk smoke timer
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
      this.range = init.mode === 'range';
      if (init.lock && init.lock.frac) this.lockFrac = init.lock.frac;
      this._status(this.range ? 'LOADING RANGE...' : 'LOADING 16 KM MAP...');
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
      if (this.range) {
        // the lobby is the same, only the subtitle says what this server is
        const sub = this.bootEl.querySelector('p');
        if (sub) sub.textContent = 'WEAPON RANGE · 4 KM MAP · APC TARGETS · REARM ANYWHERE';
      }
      this._setupLobby(init);
      this._setupDebug(init);
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

  // --------------------------------------------------------- debug panel
  // shipped from the server only when MECH_DEBUG=1: quick knobs for testing
  _setupDebug(init) {
    const el = document.getElementById('debug');
    if (!el || !init.debug) return;
    this.debugEl = el;
    this.debugGod = el.querySelector('[data-dbg="god"]');
    el.addEventListener('click', (e) => {
      const kind = e.target && e.target.dataset ? e.target.dataset.dbg : null;
      if (kind === 'respawn') {
        this.net.send({ t: 'respawn' });
      } else if (kind === 'restart') {
        this.net.send({ t: 'debug_restart' });
      } else if (kind === 'god') {
        const on = !(this.net.meta && this.net.meta.god);
        this.net.send({ t: 'debug_god', on });
      }
    });
    el.classList.remove('hidden');
  }

  _syncDebug() {
    if (!this.debugGod) return;
    const on = !!(this.net.meta && this.net.meta.god);
    this.debugGod.textContent = on ? 'GOD ON' : 'GOD OFF';
    this.debugGod.classList.toggle('on', on);
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
    if (this.input.zoomIdx !== this.zoomIdx) {
      this.zoomIdx = this.input.zoomIdx;
      this.renderer.setZoomLevel(this.zoomIdx);
    }
    this.zoom += (this.input.zoom - this.zoom) * (1 - Math.exp(-dt * 22));
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
    this._syncDebug();

    this.particles.update(dt);
    for (const [id, f] of this.flashes) {
      f.t -= dt;
      if (f.t <= 0) this.flashes.delete(id);
    }
    this._missileTrails();
    this._wreckSmoke(dt);
    const cam = this._camera(own, dt);
    this.hud.resize();
    this.hud.vp = cam.viewProj;
    this._aim(own);
    this._updateLockTone(own);
    drawUnits(this.renderer, this.net.entities, this.net.projectiles, own ? own.id : -1, this.defs.weapons, this.flashes);
    if (own) drawFirstPersonArms(this.renderer, own, this.defs.weapons, this.flashes, cam.eye);
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
      // the IR lock circle is a fixed slice of the screen: the server needs
      // the magnification and the viewport shape to match it
      z: this.zoom,
      a: this.glCanvas.clientWidth / Math.max(1, this.glCanvas.clientHeight),
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

  // burning hulls smoke until the server frees them
  _wreckSmoke(dt) {
    for (const e of this.net.entities.values()) {
      if (e.type !== 7) continue;
      const kind = (e.flags >> 4) & 3;
      if (kind === 0) continue;
      let t = (this.wreckPuffs.get(e.id) || Math.random() * 0.2) - dt;
      if (t <= 0) {
        t = 0.12 + Math.random() * 0.08;
        const h = kind === 2 ? 2.2 : 1.8;
        this.particles.smoke(e.x + (Math.random() - 0.5) * 0.8, e.y + h,
          e.z + (Math.random() - 0.5) * 0.8, 0.9 + Math.random() * 0.5, 0.28);
      }
      this.wreckPuffs.set(e.id, t);
    }
    if (this.wreckPuffs.size > 64) {
      for (const id of this.wreckPuffs.keys()) {
        if (!this.net.entities.has(id)) this.wreckPuffs.delete(id);
      }
    }
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
      // wrecks and corpses are inert: rounds fly straight through them, so the
      // sight ray and the impact prediction must ignore them too
      if (e.id === own.id || e.type === 7 || e.type === 8) continue;
      // per-sphere, exactly like the server's hit tests
      for (const s of unitSpheres(e.type, e.x, e.y, e.z, e.yaw, this.init.mech.hitspheres)) {
        units.push({ id: e.id, faction: e.faction, x: s.x, y: s.y, z: s.z, r: s.r });
      }
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
      let far = false;
      if (wd && (wd.kind === 'bullet' || wd.kind === 'rocket')) {
        // fire control: the server zeroes the shot against whatever the muzzle
        // points at, so predict with the elevation it is going to use.  bias is
        // already inside the solution, hence the copy with bias 0
        const z = this.init.zero || { range: 4000, max_elev: 1.05 };
        const fc = zeroPitch(this.map, wd, yaw, pitch, m[0], m[1], m[2], units, own.id,
          z.range, z.max_elev, own.faction);
        const theta = fc.theta;
        const shot = theta === null ? wd : { ...wd, bias: 0 };
        point = predictImpact(this.map, shot, m[0], m[1], m[2],
          theta === null ? dir : dirFrom(yaw, theta), units, {}, own.faction);
        hit = point.hit;
        dist = point.dist;
        // red with a trailing + whenever the count is not an impact: either
        // the sight point is out of reach, or the round expires in the air
        // (aimed past the map edge / further than the fuse can carry it)
        far = fc.far || !point.hit;
        if (theta === null) {
          // nothing to zero on: the shot simply runs down the aim line, so the
          // reticle belongs on that line at infinity -- hold the crosshair on
          // the target and fire, no drop marker to aim off
          point = { x: m[0] + dir[0] * AIM_LINE, y: m[1] + dir[1] * AIM_LINE,
            z: m[2] + dir[2] * AIM_LINE };
        }
      } else if (wd) {
        const near = Math.min(wd.range, 2400);
        point = { x: m[0] + dir[0] * near, y: m[1] + dir[1] * near, z: m[2] + dir[2] * near, hit: false, dist: near };
        dist = near;
      }
      if (!point) continue;
      const scr = this.hud._project(point.x, point.y, point.z);
      this.crosshairs[arm] = {
        screen: scr || [this.hud.w / 2, this.hud.h / 2],
        hit, dist, disabled, far,
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
    // first person: the eye sits in the cockpit and looks exactly where the
    // arms point, so the reticle is the centre of the screen at every zoom
    let yaw = this.input.viewYaw;
    let pitch = clamp(this.input.pitch + this.input.freePitch * 0.8, -1.35, 1.35);
    if (this.shake > 0.01) {
      // angular kick, scaled by the magnification so recoil does not blind
      // the pilot through a 20x sight
      const s = this.shake * 0.02 / this.zoom;
      yaw += (Math.random() - 0.5) * s;
      pitch += (Math.random() - 0.5) * s;
    }
    const dir = dirFrom(yaw, pitch);
    const eyeH = (this.defs.mech && this.defs.mech.eye) || 5.05;
    const eye = [own.x, own.y + eyeH, own.z];
    const aspect = this.glCanvas.clientWidth / Math.max(1, this.glCanvas.clientHeight);
    const fov = clamp(BASE_FOV / this.zoom, 0.02, BASE_FOV);
    makeCamera(cam, eye, [eye[0] + dir[0], eye[1] + dir[1], eye[2] + dir[2]], fov, aspect, 0.4, this.renderer.far);
    invert4(cam.invViewProj, cam.viewProj);
    return cam;
  }

  _hudState(own) {
    const m = this.net.meta || {};
    const faction = m.faction !== undefined ? m.faction : this.playerFaction;
    let atBase = false;
    if (this.range) atBase = true;      // range mode: rearm anywhere
    if (own && !atBase) {
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
      lockFrac: this.lockFrac,
      messages: this.messages,
      winner: m.winner !== undefined ? m.winner : this.winner,
      dead: !own,
      atBase,
      range: this.range,
      zoom: this.zoom,
      zoomIdx: this.zoomIdx,
      hitFlash: this.hitFlash,
      help: false,
      helpOpen: this.helpOpen,
      stats: { fps: this.fps, draws: this.renderer.stats.draws, instances: this.renderer.stats.instances },
    };
  }
}
