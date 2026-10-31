// Glue: connection, camera, aiming prediction, render loop and HUD state.

import { Net } from './net.js';
import { MapData, leadPoint, predictImpact, sightBlocked, sightPoint, unitSpheres, zeroAt } from './map.js';
import { Input } from './input.js';
import { GameAudio, SOUND_FLOOR, SOUND_LEVEL, SOUND_SPEED, attenuation } from './audio.js';
import { Renderer, makeCamera } from './renderer.js';
import { drawUnits } from './scene.js';
import { HUD } from './hud.js';
import { Particles } from './particles.js';
import { clamp, dirFrom, invert4, mat4 } from './math.js';
import { emptyDraft, isEmpty, quickDraft, quote, toMessage } from './shop.js';

const BASE_FOV = 1.05;              // 1x vertical field of view, radians
const AIM_LINE = 60000;             // metres, "infinity" for an unzeroed reticle
const LEAD_TARGETS = 4;             // nearest few only, the rest is clutter
const RWR_RANGE = 1000;             // m, the threat receiver's own gate
const LASER_RANGE = 3400;           // longest sight an enemy could shoot at
const LASER_CONE = 0.05;            // rad, "his gun is pointed at me"
const SMOKE_RGB = [0.58, 0.52, 0.44];   // dirt brown, like the charge it is
const SMOKE_END_SHRINK = 0.35;      // s, last moments of a puff (it is opaque)
// A bang may stay in the air this long: the loudest thing a pilot can hear is a
// hull going up ~17 km out, and its wavefront takes 50 s to arrive - a shorter
// TTL would be the real cutoff and `SOUND_FLOOR` would never be reached.  The
// server gates events by the same two numbers (`defs.SOUND_TTL`).
const SOUND_TTL = 50;               // s, a bang still in the air after this is dropped
const EAR_NEAR = 1;                 // m, closer than this a bang has no side to be on
const SHAKE_BOOM_BIG = 0.225;       // near blast, big round (a quarter of the old kick)
const SHAKE_BOOM_SMALL = 0.0875;    // near blast, shell hit
const SHAKE_LAUNCH = 0.5;           // own missile leaving the tube
const SHAKE_GUN = 0.12;             // own cannon round, "slight" (a burst adds up)
const SHAKE_HEAVY = 2.1;            // own 105 mm round: five times a gun's kick -
                                    // it is fired once every 5 s, so it can be huge
const SHAKE_MAX = 2.4;              // ceiling on the accumulator, set so a 105 mm
                                    // round is felt in full instead of being cut off

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
    this.leads = [];            // pre-aim boxes, projected for this frame
    this.leadWorld = [];        // the same solutions in world space
    this.leadSeq = -1;          // snapshot the boxes were computed for
    this.helpOpen = false;
    this.started = false;
    this.winner = -1;
    this.zoom = 1;              // smoothed, drives the field of view
    this.zoomIdx = 0;           // optics step selected with the wheel
    this.zeroRange = 1000;      // sight setting in metres (CTRL+wheel)
    this.range = false;         // weapon range mode (server started with --range)
    this.lockFrac = 0.05;       // IR lock circle, as a fraction of the view width
    this.wreckPuffs = new Map();    // per-hulk smoke timer
    this.seekerLog = [];            // every IR lock smoke or terrain broke, oldest first
    this.shopOpen = false;          // rearm panel (B) is up: the mouse is a cursor
    this.sounds = [];               // acoustic events in the air, oldest first
    this.settingsOpen = false;      // options panel (gear): volume + debug knobs
    this.debug = false;             // server started with MECH_DEBUG=1
    this.shopDraft = emptyDraft();  // edits waiting for CONFIRM
    this.shopDrop = -1;             // which arm's weapon list is pulled down
  }

  async boot() {
    this._status('CONNECTING...');
    try {
      const init = await this.net.connect();
      this.init = init;
      this.defs.weapons = init.weapons;
      this.defs.units = init.units;
      this.defs.mech = init.mech;
      // the radar equation's constants: the client solves the same
      // `d^2 <= K * v_closing` the server's AI does
      this.defs.radar = init.radar;
      this.buyable = init.buyable;
      this.rearm = init.rearm || null;
      this.playerFaction = init.faction;
      this.range = init.mode === 'range';
      if (init.lock && init.lock.frac) this.lockFrac = init.lock.frac;
      // sight settings come from the server so the steps are one list
      if (init.zero && init.zero.steps) {
        this.input.zeroSteps = init.zero.steps.slice();
        const d = this.input.zeroSteps.indexOf(init.zero.default);
        this.input.zeroIdx = d < 0 ? 0 : d;
        this.zeroRange = this.input.zeroRange;
      }
      this._status(this.range ? 'LOADING RANGE...' : 'LOADING 16 KM MAP...');
      this.map = await MapData.load(init.map, init.lanes);
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
      this.input.onPick = (idx, right) => {
        // the digits only move the draft: CONFIRM is the one thing that spends
        // money, and a number pressed with the panel shut picks nothing
        if (!this.shopOpen) return;
        const wid = this.buyable[idx];
        if (wid !== undefined) this.shopDraft.arms[right ? 1 : 0] = wid;
      };
      this.input.onShop = () => this._shopToggle();
      this.input.onShopConfirm = () => this._shopConfirm();
      this.input.onShopCancel = () => this._shopClose();
      this.input.onUiClick = (x, y) =>
        (this.settingsOpen ? this._settingsClick(x, y) : this._shopClick(x, y));
      this.input.onToggleHelp = () => { this.helpOpen = !this.helpOpen; };
      this.input.onSettings = () => this._settingsToggle();
      this.input.onHudClick = (x, y) => this._hudClick(x, y);
      if (this.range) {
        // the lobby is the same, only the subtitle says what this server is
        const sub = this.bootEl.querySelector('p');
        if (sub) sub.textContent = 'WEAPON RANGE · 4 KM MAP · APC TARGETS · REARM ANYWHERE';
      }
      this._setupLobby(init);
      this.debug = !!init.debug;
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
    // no loadout here: every mech leaves the lobby on the default pair and
    // weapons are a rearm-panel purchase (`B`) like everything else
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
  }

  start() {
    // once only: the DEPLOY button keeps DOM focus after the lobby fades out,
    // and a later ENTER would fire its native click - a free respawn
    if (!this.ready || this.started) return;
    this.started = true;
    const btn = document.getElementById('startBtn');
    if (btn && btn.blur) btn.blur();
    this.net.send({
      t: 'join',
      name: (this.nameEl && this.nameEl.value.trim()) || `PILOT-${this.init.pid}`,
      faction: this.factionChoice,
    });
    this.input.enabled = true;
    this.audio.ensure();
    this.audio.resume();
    // a click that the browser does not count as user activation (or a start
    // from the keyboard) leaves the context suspended, and every alarm stays
    // silent: unlock on the first real gesture instead
    const unlock = () => this.audio.resume();
    window.addEventListener('pointerdown', unlock, { once: true });
    window.addEventListener('keydown', unlock, { once: true });
    this.bootEl.classList.add('hidden');
    this._lockPointer();
    this.last = performance.now();
    requestAnimationFrame(() => this.frame());
    this.net.send({ t: 'in', yaw: 0, pitch: 0, mx: 0, mz: 0, fl: false, fr: false });
  }

  // pointer lock is refused in automated tabs and while the panel is up; a
  // rejected promise there is expected, not an error to log
  _lockPointer() {
    try {
      const p = this.glCanvas.requestPointerLock();
      if (p && p.catch) p.catch(() => {});
    } catch (e) { /* not enabled, nothing to do */ }
  }

  _message(text, color) {
    this.messages.push({ text, color, life: 6 });
    if (this.messages.length > 40) this.messages.shift();
  }

  // -------------------------------------------------------------- acoustics
  // A bang is not a point event: it leaves the source as a sphere growing at
  // the speed of sound.  Every acoustic cue goes on this list with where, when
  // and how loud it happened (`level`, the source level from `SOUND_LEVEL`), and
  // each frame the sphere is compared with where the mech is *now* - so a hull
  // that has driven on since the shell landed hears it once the wavefront
  // actually catches up (running away delays it, closing on it hurries it), and
  // the volume is the level at that arrival distance.  No Doppler: the pitch is
  // whatever the synth produced.
  _sound(x, y, z, level, play) {
    this.sounds.push({ t0: performance.now() / 1000, x, y, z, level, play });
  }

  // Where a bang landed in the cockpit's ears: the lateral share of its bearing
  // is the pan (-1 left .. +1 right), and how much of it was behind the pilot is
  // all stereo cannot say - that comes back as a dulling (`audio._bus`).  Taken
  // at the moment the wavefront arrives, from where the mech is *then* and the
  // view yaw, because that is where the head is when the sound reaches it.
  _bearing(s, own, dist) {
    if (!own || !(dist > 0.5)) return [0, 0];
    const yaw = this.input.viewYaw;
    const dx = (s.x - own.x) / dist;
    const dz = (s.z - own.z) / dist;
    // dirFrom's forward is (cos yaw, sin yaw), so right is a quarter turn on
    const right = -dx * Math.sin(yaw) + dz * Math.cos(yaw);
    const fwd = dx * Math.cos(yaw) + dz * Math.sin(yaw);
    // a bang right on the canopy is neither left nor right
    const near = Math.min(1, dist / EAR_NEAR);
    return [clamp(right, -1, 1) * near, Math.max(0, -fwd)];
  }

  _pumpSounds() {
    const list = this.sounds;
    if (!list.length) return;
    const own = this.ownEntity();
    const now = performance.now() / 1000;
    let n = 0;
    for (let i = 0; i < list.length; i++) {
      const s = list[i];
      const age = now - s.t0;
      if (age > SOUND_TTL) continue;              // nobody could still hear it
      // no mech, no ear: the event just ages out (a dead pilot hears nothing)
      const dist = own ? Math.hypot(s.x - own.x, s.y - own.y, s.z - own.z) : Infinity;
      if (age * SOUND_SPEED >= dist) {
        const vol = s.level * attenuation(dist);
        if (vol > SOUND_FLOOR) {
          const [pan, behind] = this._bearing(s, own, dist);
          s.play(vol, dist, pan, behind);
        }
        continue;                                 // arrived: off the list
      }
      list[n++] = s;
    }
    list.length = n;
  }

  _fx(events) {
    const own = this.ownEntity();
    for (const ev of events) {
      if (ev.k === 'boom') {
        const wd = this.defs.weapons[ev.wid] || {};
        // a round shot down (`small`) is the round breaking up, not its warhead
        // going off: it keeps the little burst the server sized for it and
        // loses the big-hit treatment the weapon's own `er` would have earned
        const big = !ev.small && (!!ev.big || (wd.er || 0) >= 5);
        // the flash and the kick are light and physics: immediate, the bang
        // comes later
        this.particles.impact(ev.x, ev.y, ev.z, ev.r || 1, big, wd.kind);
        // a destroyed hull is not a big round hit: it gets a fireball of its
        // own on top, and it is felt from further out
        if (ev.blast) this.particles.explosion(ev.x, ev.y, ev.z, ev.r || 15);
        const d = own ? Math.hypot(ev.x - own.x, ev.z - own.z) : 3000;
        this.shake = Math.min(1.2, this.shake + clamp(1 - d / (ev.blast ? 500 : 300), 0, 1)
          * (big ? SHAKE_BOOM_BIG : SHAKE_BOOM_SMALL) * (ev.blast ? 1.5 : 1));
        const level = ev.blast ? SOUND_LEVEL.blast
                               : (big ? SOUND_LEVEL.big : SOUND_LEVEL.boom);
        this._sound(ev.x, ev.y, ev.z, level, (vol, dist, pan, behind) => (ev.blast
          ? this.audio.explosion(vol, dist, pan, behind)
          : this.audio.boom(vol, big, dist, pan, behind)));
      } else if (ev.k === 'intercept') {
        // a missile was shot down: a few sparks where it broke up for everyone
        // who can see it, but the tone belongs to the two crews it concerns -
        // the one that fired the burst (`pid`) and the one whose round it was
        // (`firer`).  The battle's AI crews shoot missiles down all over the
        // map; a chime per round for everybody was the busiest sound in it.
        this.particles.impact(ev.x, ev.y, ev.z, 1);
        if (ev.pid === this.init.pid || ev.firer === this.init.pid) this.audio.hit();
      } else if (ev.k === 'shot') {
        if (ev.eid !== undefined) this.flashes.set(ev.eid, { t: 0.07, arm: ev.arm });
        const wd = this.defs.weapons[ev.wid] || {};
        const kind = wd.kind || 'bullet';
        // the hull kicks with its own guns: a missile leaves the tube with a
        // shove, a 105 mm round shoves it almost as hard (and is fired five
        // seconds apart, so it never stacks into a buzz), a cannon just rocks
        // the machine
        if (own && ev.eid === own.id) {
          const guided = kind === 'rocket' || kind === 'saclos' || kind === 'top';
          this.shake = Math.min(guided ? 1.2 : SHAKE_MAX, this.shake
            + (guided ? SHAKE_LAUNCH : wd.heavy ? SHAKE_HEAVY : SHAKE_GUN));
        }
        this._sound(ev.x, ev.y, ev.z, SOUND_LEVEL.shot,
          (vol, dist, pan, behind) =>
            this.audio.shot(ev.wid, kind, vol, dist, pan, behind, !!wd.heavy));
      } else if (ev.k === 'kill') {
        if (ev.pid === this.init.pid) {
          this._message(`+$${ev.reward} KILL`, '#a9f0c0');
          this.audio.hit();
        }
      } else if (ev.k === 'seeker') {
        // an IR missile lost its lock: its line of sight is full of smoke or a
        // ridge.  The pilot is told nothing - a blind seeker is not something
        // the hull can observe (a blinded round flies the same profile a guided
        // one does, `seeker.py`), and the battle's AI crews lose locks all over
        // the map.  Kept on the game object so a console script can read the
        // run back; the range's tests read the server log (`mech.smoke`).
        const rec = { t: Math.round(performance.now()), dist: ev.dist, mech: ev.tid,
                      why: ev.why, x: ev.x, y: ev.y, z: ev.z };
        this.seekerLog.push(rec);
        if (this.seekerLog.length > 64) this.seekerLog.shift();
      } else if (ev.k === 'capture') {
        const mine = ev.faction === this.playerFaction;
        this._message(mine ? 'TOWN SECURED' : 'TOWN LOST', mine ? '#7ce0a0' : '#ff9a9a');
      } else if (ev.k === 'respawn') {
        if (ev.pid === this.init.pid) this._message('RE-DEPLOYED', '#a9f0c0');
      } else if (ev.k === 'rearm') {
        // the wrench is out of the machine: rounds and armour are back
        if (ev.pid === this.init.pid) this._message(`${ev.label || 'REARM'} COMPLETE`, '#a9f0c0');
      } else if (ev.k === 'gameover') {
        this.winner = ev.winner;
      } else if (ev.k === 'tiles') {
        // the debug tree planter painted tiles on the server: copy the same
        // rectangle into our copy of the map and redraw what comes from it, so
        // the stand that hides the target is a stand we can see
        const m = this.map;
        if (m && ev.rows && ev.cols) {
          let k = 0;
          for (let r = 0; r < ev.rows; r++) {
            const row = (ev.j0 + r) * m.w + ev.i0;
            for (let c = 0; c < ev.cols; c++, k++) m.tiles[row + c] = ev.data[k];
          }
          // the trunk and canopy caches are keyed by tile and hold what the
          // old bitmap said: a stand planted on ground that was already asked
          // about would never be found in them
          m.trunkCache.clear();
          m.canopyCache.clear();
          if (this.renderer) this.renderer.repaintTiles(m);
        }
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
    this.zeroRange = this.input.zeroRange;
    for (const m of this.messages) m.life -= dt;
    this.messages = this.messages.filter((m) => m.life > 0);
    this.hitFlash = Math.max(0, this.hitFlash - dt * 1.6);
    this.shake = Math.max(0, this.shake - dt * 2.2);

    this.net.interpolate(1 - Math.exp(-dt * 14));
    this._pumpSounds();
    this._sendInput(dt);

    // the own machine's pose is the *server's* now: the hull swings after the
    // sight and the arms climb after it at their own rates, so overriding yaw /
    // pitch with the view here would draw (and predict for) a machine that
    // snaps to wherever the pilot looks - exactly what it no longer does
    const own = this.ownEntity();

    if (this.shopOpen && !own) this._shopClose();
    this._checkDamage(own);
    this._noLockFeedback(own, dt);

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
    // the radar first: the pre-aim boxes are drawn on what it has seen
    this._threats(own);
    this._leadMarkers(own);
    this._leadScreen(own);
    this._alarms(own);
    this._updateLockTone(own);
    drawUnits(this.renderer, this.net.entities, this.net.projectiles,
      this.defs.weapons, this.flashes, own ? own.id : -1);
    this.particles.render(this.renderer);
    this._drawSmoke();
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
      fl: this.shopOpen ? false : this.input.fireL,
      fr: this.shopOpen ? false : this.input.fireR,
      // the IR lock circle is a fixed slice of the screen: the server needs
      // the magnification and the viewport shape to match it
      z: this.zoom,
      a: this.glCanvas.clientWidth / Math.max(1, this.glCanvas.clientHeight),
      // sight setting the gun is zeroed to, so the server fires the barrel
      // elevation the reticle promises
      zr: this.input.zeroRange,
      // hot smoke: one pull per press, cleared as soon as it is on the wire
      q: this.shopOpen ? false : this.input.qPressed,
    });
    this.input.qPressed = false;
  }

  _missileTrails() {
    // one puff every few metres of flight, per round, laid down where the
    // nozzle is - the model hangs its flame 2.4 m behind the round's centre
    // along the flight path, so the plume has to start there too
    for (const p of this.net.projectiles) {
      if (p.kind === 0) continue;
      const sp = Math.hypot(p.vx, p.vy, p.vz) || 1;
      const k = 2.4 / sp;
      this.particles.lastTrail = this._trailPos.get(p.id) || null;
      this.particles.trail(p.x - p.vx * k, p.y - p.vy * k, p.z - p.vz * k, 7);
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

  // Canisters on their way out, then the puffs they become.  The puffs are
  // drawn opaque in the solid pass: a smoke screen is meant to hide everything
  // behind it, and skipping the blend makes a dozen of them almost free.
  _drawSmoke() {
    const R = this.renderer;
    const list = this.net.smoke;
    if (!list || !list.length) return;
    for (const p of list) {
      if (p.canister) {
        R.sphere(p.x, p.y, p.z, 0, 0, 1.5, 1.5, 1.5, 0.30, 0.30, 0.29, 1);
        continue;
      }
      // the last fraction of a second is spent shrinking away instead of
      // fading out, the only exit an opaque puff can afford
      const shrink = Math.min(1, p.t / SMOKE_END_SHRINK);
      R.smoke(p.x, p.y, p.z, 0, 0, p.rx * shrink, p.ry * shrink, p.rz * shrink,
              SMOKE_RGB[0], SMOKE_RGB[1], SMOKE_RGB[2], 1);
    }
  }

  // Self-defence radar + laser warning: everything is already in the snapshot,
  // so the pilot's own HUD works out the threats.  The receiver answers the same
  // question the server's AI asks (`defs.radar_k`): a doppler set only sees what
  // is moving *towards* it, so a round gives an echo
  //
  //     strength * rcs / d^4 * (v_closing / v_ref)^2 >= 1   <=>   d^2 <= K * v_c
  //
  // with `K = sqrt(strength * rcs) / v_ref`.  A round opening out or crossing
  // gives a non-positive closing speed and fails the test by itself - there is
  // no speed threshold any more.  What it is *not*: a prediction of being hit,
  // which is why an unseen round still gets a bearing on the dial.
  _threats(own) {
    this.threats = [];
    this.lasers = [];
    this.radarAlarm = 0;
    this.radarRing = 0;
    if (!own) return;
    const radar = this.defs.radar || { v_ref: 600, rcs: 0.02 };
    const strength = (this.defs.units[own.type] || {}).radar_strength || 0;
    // the dial's outer ring: the discovery range against the brightest
    // cross-section in the game closing at v_ref (`defs.radar_range`), i.e.
    // 1000 m for a mech and 500 m for an APC - read off the data, so the label
    // in the corner is never a number of its own
    this.radarRing = Math.pow(Math.max(0, strength * radar.rcs), 0.25);
    // the receiver's own gate, a kilometre down the pipe and no further - or
    // less if the machine's radar is shorter-ranged than that (an APC), so a
    // mark never has to be clamped onto the rim of a dial it is outside of
    const gate = this.radarRing > 0 ? Math.min(RWR_RANGE, this.radarRing) : 0;
    const eye = [own.x, own.y + 5, own.z];
    // the round is measured against *this* hull's velocity, not the world's:
    // running at a missile lights the receiver earlier, running from it later
    const ovx = own.vx || 0, ovz = own.vz || 0;
    // the smoke the sight rule cares about: deployed puffs, not canisters
    const puffs = this.net.smoke ? this.net.smoke.filter((s) => !s.canister) : null;
    for (const p of this.net.projectiles) {
      if (p.faction === own.faction) continue;
      if (p.kind === 0) continue;                      // shells do not count
      const dx = p.x - eye[0], dy = p.y - eye[1], dz = p.z - eye[2];
      const d = Math.hypot(dx, dy, dz);
      if (d < 1 || d > gate) continue;                 // outside the receiver
      const wd = this.defs.weapons[p.wid] || {};
      const v = Math.hypot(p.vx, p.vy, p.vz);
      if (v < 1) continue;
      // the relative vector: the round's velocity against the hull's.  Positive
      // `vc` while it still has to cover the gap between us at that closing
      // rate, and the collision course below is solved on the same vector.
      const wx = p.vx - ovx, wy = p.vy, wz = p.vz - ovz;
      const vc = -((dx * wx + dy * wy + dz * wz) / d);
      if (vc <= 0) continue;                           // opening out or crossing
      const k = Math.sqrt(Math.max(0, strength * (wd.rcs || 0))) / radar.v_ref;
      // seen: the echo clears the receiver's floor.  Not seen: the bearing is
      // still known and it is still closing - the dial shows it as such.
      const seen = d * d <= k * vc;
      // A tracked round is not automatically an *incoming* one: the dial plots
      // every echo the receiver has, the alarm sings only for the rounds whose
      // course can actually reach this hull (`er` out, `de` 20 of armour
      // against it).  Guided rounds (SACLOS, top attack) fly a profile, not a
      // line - a top-attack climbs 150 m over its target and only drops onto
      // the roof at the end - so the course that matters for them is the
      // horizontal one, which is the ground they fly over.
      const guided = p.kind === 2 || p.kind === 3;
      let warn;
      if (guided) {
        const wh = Math.hypot(wx, wz);
        if (wh < 1e-3) {
          warn = Math.hypot(dx, dz) <= (wd.er || 0) + 20;   // hovering at me
        } else {
          const along = (dx * wx + dz * wz) / wh;
          warn = Math.hypot(dx - wx / wh * along, dz - wz / wh * along)
            <= (wd.er || 0) + 20;
        }
      } else {
        const w = Math.hypot(wx, wy, wz);
        const along = (dx * wx + dy * wy + dz * wz) / w;
        warn = Math.hypot(dx - wx / w * along, dy - wy / w * along,
          dz - wz / w * along) <= (wd.er || 0) + 20;
      }
      // one line of sight, the same door the AI's lookout, the IR lock and the
      // pre-aim box go through: terrain, a canopy or a screen of smoke hides a
      // round from the receiver exactly as it hides a hull
      if (sightBlocked(this.map, eye[0], eye[1], eye[2], p.x, p.y, p.z, puffs)) continue;
      this.threats.push({ x: p.x, y: p.y, z: p.z, dist: d, kind: p.kind,
                          seen, warn, wid: p.wid, hit_r: wd.hit_r || 0,
                          vx: p.vx, vy: p.vy, vz: p.vz });
    }
    this.threats.sort((a, b) => a.dist - b.dist);

    // Laser warning: who has his sight *on me*.  Only a vehicle carries a
    // laser designator - a rifleman looking at the mech is not a threat the
    // receiver is meant to sing about.  For an AI hull the snapshot carries
    // its current target, so it has to name my mech - that is the truth, and
    // it keeps the circle quiet when a hundred hulls are just looking the
    // other way.  A human opponent exposes no target, so there the fallback is
    // a tight cone on his sight line.
    for (const e of this.net.entities.values()) {
      if (e.faction === own.faction) continue;
      if (e.type < 3 || e.type > 4) continue;           // apc / mech only
      const dx = eye[0] - e.x, dy = eye[1] - (e.y + 2.5), dz = eye[2] - e.z;
      const d = Math.hypot(dx, dy, dz);
      if (d > LASER_RANGE || d < 20) continue;
      let aimed = e.target === own.id;
      if (!aimed && e.owner !== 255) {                  // human pilot: cone test
        const yaw = e.type === 3 ? e.turret : e.yaw;
        const cp = Math.cos(e.pitch);
        const dot = (dx * cp * Math.cos(yaw) + dy * Math.sin(e.pitch) + dz * cp * Math.sin(yaw)) / d;
        aimed = dot > Math.cos(LASER_CONE);
      }
      if (!aimed) continue;
      this.lasers.push({ x: e.x, z: e.z, dist: d, id: e.id });
    }
  }

  // two alarms with their own voices, both taken from the AN/ALR-67 RWR set:
  // a missile inbound trills fast (0.083 s per tone), a sight settling on the
  // cockpit warbles four times slower.  Both just loop for as long as the
  // threat holds, so they are driven every frame with a hold in the audio side
  // that keeps them from dropping out on a one-frame gap.  The dial plots every
  // round the receiver has a track on; only the ones on a course that reaches
  // this hull (`warn`) are worth a tone.
  _alarms(own) {
    const t = (own && this.threats) || [];
    this.audio.missileWarning(t.some((x) => x.warn));
    const L = (own && this.lasers) || [];
    this.audio.laserWarning(L.length > 0);
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
  // The barrel the pilot is looking down: where it *is*, not where the cross
  // is.  A mech's arms ride on the hull and elevate on their own pivots, so the
  // bore is the hull's yaw (`own.yaw`) and the arms' pitch (`own.pitch`) - both
  // from the server, both slewing after the sight.  Mid-flick the bore is
  // behind the cross, and the impact marker drawn off it has to be too.
  _muzzle(own, arm) {
    const mod = arm === 0 ? '2' : '3';
    const off = (this.defs.mech.muzzles && this.defs.mech.muzzles[mod]) || [0, 4.3, 0.9];
    // the bore ends at the mouth of whatever is bolted to the arm - a 4.1 m
    // cannon reaches a lot further than a 1.8 m nest - and it swings with the
    // arm, exactly like the server's `muzzle_point`
    const wd = (this.defs.weapons && this.defs.weapons[own.weapon ? own.weapon[arm] : 0]) || {};
    const reachTbl = this.defs.mech.reach || {};
    // where the bore ends, in the same order the server uses: the weapon's own
    // figure (`wd.reach`, the 105 mm's 6.3 m bore), then its rack's mouth of
    // tubes, then the kind's figure
    const reach = wd.reach !== undefined ? wd.reach
      : wd.rack ? wd.rack.mouth
        : reachTbl[wd.kind] === undefined ? off[2] : reachTbl[wd.kind];
    // a launcher bolted at its own quadrant stands there whatever the arm is
    // doing - the same angle the server solves the launch point from
    const pitch = wd.rack && wd.rack.elev ? wd.rack.elev : own.pitch;
    const yaw = own.yaw;
    const c = Math.cos(yaw), s = Math.sin(yaw);
    const rx = -s, rz = c;
    const fwd = reach * Math.cos(pitch);
    return [
      own.x + rx * off[0] + c * fwd,
      own.y + off[1] + reach * Math.sin(pitch),
      own.z + rz * off[0] + s * fwd,
    ];
  }

  // The origin every aiming readout is measured from: the eye sits in the
  // commander's sight head, which is bolted to the *hull* `defs.mech.sight_z`
  // behind the hull's own rotation axis (`mathutil.sight_eye` on the server,
  // line for line).  The head's own rotation about its pivot does not move it -
  // the head turns, the eye is the pivot - but the hull swinging underneath
  // does: that arc is the geometry the decoupled sight exists for, and it is
  // why the machine can be felt moving under a view that has already turned.
  _sightEye(own) {
    const mech = this.defs.mech || {};
    const oz = mech.sight_z === undefined ? -0.62 : mech.sight_z;
    const c = Math.cos(own.yaw), s = Math.sin(own.yaw);
    return [
      own.x + c * oz,
      own.y + (mech.eye || 5.05),
      own.z + s * oz,
    ];
  }

  _aim(own) {
    this.crosshairs = [null, null];
    this.sightRange = null;
    if (!own) return;
    // two headings, and they are different things.  `dir` is the sight: the
    // cross, the range readout, the seeker ring, the beam a SACLOS missile
    // rides.  The arms are laid along the bore (`own.yaw` / `own.pitch`), which
    // trails the sight while the machine catches up - that is what each arm's
    // marker is predicted from below
    const dir = dirFrom(this.input.yaw, this.input.pitch);
    const byaw = own.yaw;
    const bpitch = own.pitch;
    const bdir = dirFrom(byaw, bpitch);
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
    // the centre cross is the sight line: how far down it the first thing
    // (ground, wall, trunk, hull) sits - the mirror of the server's sight
    // point, out to the one range every setting shares
    const z0 = this.init.zero || { range: 4000, max_elev: 1.05 };
    const eye = this._sightEye(own);
    const sp = sightPoint(this.map, eye[0], eye[1], eye[2], dir, z0.range,
      units, own.id, own.faction);
    if (sp) {
      const d = Math.hypot(sp.x - eye[0], sp.y - eye[1], sp.z - eye[2]);
      if (d < z0.range) this.sightRange = d;
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
        // fire control: the barrel is held off for the drop to the range the
        // sight is set to, exactly as the server fires it.  The elevation
        // depends on that setting and the hull's attitude only, so the marker
        // sits still across a crest line instead of dancing on the ridge.  bias
        // is absorbed by the solution, hence the copy with bias 0
        const z = this.init.zero || { range: 4000, max_elev: 1.05 };
        const theta = zeroAt(wd, byaw, bpitch, m[0], m[1], m[2], this.zeroRange, z.max_elev);
        const shot = theta === null ? wd : { ...wd, bias: 0 };
        point = predictImpact(this.map, shot, m[0], m[1], m[2],
          theta === null ? bdir : dirFrom(byaw, theta), units, {}, own.faction);
        hit = point.hit;
        dist = point.dist;
        // a setting the round cannot reach (or a shot that runs off the map)
        // leaves the marker on the bore line and the reading red
        far = !point.hit;
        if (theta === null) {
          point = { x: m[0] + bdir[0] * AIM_LINE, y: m[1] + bdir[1] * AIM_LINE,
            z: m[2] + bdir[2] * AIM_LINE };
        }
      } else if (wd) {
        // a guided round follows the *sight*, not the tube it left: the marker
        // sits on the cross even while the arms are still coming round
        const near = Math.min(wd.range, 2400);
        point = { x: m[0] + dir[0] * near, y: m[1] + dir[1] * near, z: m[2] + dir[2] * near, hit: false, dist: near };
        dist = near;
      }
      if (!point) continue;
      const scr = this.hud._project(point.x, point.y, point.z);
      // no marker for a barrel pointing behind the pilot: the old fallback put
      // it on the centre of the screen, which with a lagging bore would read as
      // "on target" for the moment a flick takes the arms off the view
      if (!scr) continue;
      this.crosshairs[arm] = { screen: scr, hit, dist, disabled, far };
    }
  }

  // ------------------------------------------------------------- pre-aim
  // The boxes a pilot lines the sights up with to hit a hull on the move: the
  // point each arm's round has to be aimed at.  The sight is zeroed to a fixed
  // range, so the round needs `range / vel` seconds to reach anything it is
  // laid on and the lead is the hull's own motion over that time - put the
  // centre cross on the box at that range and the shot lands.  A gun and a
  // rocket pod need very different leads, so every arm gets its own box.
  // Recomputed per snapshot and kept in world space (`leadWorld`), so the box
  // stays glued to the hull while the pilot slews the sight.
  _leadMarkers(own) {
    if (this.net.seq === this.leadSeq) return;
    this.leadSeq = this.net.seq;
    this.leadWorld = [];
    if (!own || !this.map) return;
    const fov = clamp(BASE_FOV / this.zoom, 0.02, BASE_FOV);
    const tanHalf = Math.tan(fov * 0.5);
    const zero = this.init.zero || { range: 4000, max_elev: 1.05 };
    // the smoke that is actually up: a canister still in the air is a charge,
    // not a screen (the server only ever tests deployed puffs)
    const puffs = this.net.smoke.filter((s) => !s.canister);
    // the eye both the bearing and the line of sight are taken from: the
    // camera, because that is where the pilot's cross actually sits
    const eye = this._sightEye(own);

    const arms = [];
    for (let arm = 0; arm < 2; arm++) {
      const wd = this.defs.weapons[own.weapon[arm]];
      if (own.hp[arm === 0 ? 2 : 3] <= 0) continue;
      // only rounds that fly ballistically need the lead: the SACLOS and the
      // top-attack missile steer themselves onto whatever the sight marks
      if (!wd || (wd.kind !== 'bullet' && wd.kind !== 'rocket')) continue;
      arms.push({ arm, wd, m: this._muzzle(own, arm) });
    }
    // A missile the radar has *seen* (`_threats`) gets the same treatment as a
    // hull: a box on the ball it is, and one ring per arm at the point the
    // round has to be aimed at.  It is the one target that cannot be hit by
    // aiming where it *is* - a round crosses 20 m in a tick and the fuse arm is
    // 4 m - so the lead matters more here than anywhere.  Same line of sight
    // rule as the hulls below: a round behind a ridge or a screen of smoke is
    // not boxed.
    for (const t of this.threats || []) {
      if (!t.seen || !(t.hit_r > 0)) continue;
      if (sightBlocked(this.map, eye[0], eye[1], eye[2], t.x, t.y, t.z, puffs)) continue;
      const m = { ball: true, x: t.x, y: t.y, z: t.z,
                  rx: t.hit_r, ry: t.hit_r, marks: [] };
      for (const a of arms) {
        const p = leadPoint(a.wd, this.zeroRange, zero.max_elev,
          a.m[0], a.m[1], a.m[2], eye[0], eye[1], eye[2], t.x, t.y, t.z,
          t.vx || 0, t.vz || 0, t.vy || 0);
        if (p) m.marks.push({ arm: a.arm, x: p.x, y: p.y, z: p.z, tof: p.tof });
      }
      this.leadWorld.push(m);
    }
    if (!arms.length) return;

    // Enemy hulls on screen, nearest first: the four closest get a box, the
    // rest is clutter.  Every one of them gets a ring, moving or not: with a
    // fixed zero the ring no longer means "lead the target" but "put the
    // centre cross here", and a standing hull 2 km out needs just as much of a
    // lift as a driving one needs lead.
    const cand = [];
    for (const e of this.net.entities.values()) {
      if (e.type !== 3 && e.type !== 4) continue;           // APC / MECH only
      if (e.faction === own.faction) continue;
      const ud = this.defs.units[e.type] || {};
      const d = Math.hypot(e.x - own.x, e.z - own.z);
      if (d < 25 || d > zero.range) continue;              // out of the fire control
      if (!this.hud._project(e.x, e.y + (ud.height || 3) * 0.5, e.z)) continue;
      cand.push({ e, ud, d });
    }
    if (!cand.length) return;
    cand.sort((a, b) => a.d - b.d);

    for (const c of cand.slice(0, LEAD_TARGETS)) {
      const e = c.e;
      const half = (c.ud.height || 3) * 0.5;               // hull centre above the ground
      // no line of sight, no box: a hull behind a ridge, a building, a tree
      // stand or a screen of smoke is one the seeker cannot lock either, and a
      // marker floating on it would be a wallhack.  Same rule the server's AI
      // and IR seeker obey (backend/server/systems/sight.py).
      if (sightBlocked(this.map, eye[0], eye[1], eye[2], e.x, e.y + half, e.z, puffs)) continue;
      const t = {
        id: e.id,
        x: e.x, y: e.y + half, z: e.z,
        rx: c.ud.radius || 2, ry: half,
        marks: [],
      };
      for (const a of arms) {
        // the lead has to be solved against the *measured* speed: a mech that
        // fights its legs moves slower than the velocity it was given
        const p = leadPoint(a.wd, this.zeroRange, zero.max_elev,
          a.m[0], a.m[1], a.m[2], eye[0], eye[1], eye[2], t.x, t.y, t.z,
          e.mvx === undefined ? e.vx : e.mvx, e.mvz === undefined ? e.vz : e.mvz);
        if (p) t.marks.push({ arm: a.arm, x: p.x, y: p.y, z: p.z, tof: p.tof });
      }
      // the box stands on its own: a hull can be in view while the point to
      // aim at is off the edge of the screen
      this.leadWorld.push(t);
    }
  }

  // put the pre-aim solutions on the screen as it stands this frame
  _leadScreen(own) {
    this.leads = [];
    if (!own || !this.leadWorld.length) return;
    const fov = clamp(BASE_FOV / this.zoom, 0.02, BASE_FOV);
    const tanHalf = Math.tan(fov * 0.5);
    const eyeY = own.y + ((this.defs.mech && this.defs.mech.eye) || 5.05);
    for (const t of this.leadWorld) {
      const box = this.hud._project(t.x, t.y, t.z);
      if (!box) continue;
      const dist = Math.hypot(t.x - own.x, t.y - eyeY, t.z - own.z);
      const pxm = this.hud.h / (2 * Math.max(dist, 1) * tanHalf);   // pixels per metre
      // a missile's box is sized by its own ball (`hit_r`, carried in `rx`) and
      // has a floor of its own: a 0.08 m round is well under a pixel at a
      // kilometre, and a mark too small to see is not a pre-aim box
      const hw = clamp(t.rx * pxm, t.ball ? 8 : 6, 70);
      const out = {
        id: t.id,
        x: box[0], y: box[1],
        hw,
        hh: t.ball ? hw : clamp(t.ry * pxm, 5, 70),
        marks: [],
      };
      for (const m of t.marks) {
        const scr = this.hud._project(m.x, m.y, m.z);
        if (scr) out.marks.push({ arm: m.arm, x: scr[0], y: scr[1], tof: m.tof });
      }
      this.leads.push(out);
    }
  }

  // ------------------------------------------------------------ rearm shop
  // B opens a draft loadout at a base or an owned town; nothing is charged
  // until CONFIRM, and then the server prices the whole draft again and either
  // takes it all or refuses it (see shop.py).  While the panel is up the mouse
  // is a cursor, so the trigger is off and the pointer lock is released.
  _shopCfg() {
    return { weapons: this.defs.weapons, buyable: this.buyable,
             rearm: this.rearm, mech: this.defs.mech };
  }

  _shopToggle() {
    if (!this.started) return;
    if (this.shopOpen) { this._shopClose(); return; }
    if (this.settingsOpen) this._settingsClose();
    if (!this.ownEntity()) return;
    if (!this._hudState(this.ownEntity()).atBase) {
      this._message('REARM ONLY AT BASE OR AN OWNED TOWN', '#ff9a9a');
      return;
    }
    if (this.net.meta && this.net.meta.work) {
      this._message('ALREADY REARMING', '#ff9a9a');
      return;
    }
    this.shopOpen = true;
    this.shopDraft = emptyDraft();
    this.shopDrop = -1;
    this.input.uiOpen = true;
    this.input.uiKind = 'shop';
    this.input.keys.clear();
    this.input.fireL = this.input.fireR = false;
    this.input.qPressed = false;
    if (document.pointerLockElement) document.exitPointerLock();
  }

  _shopClose() {
    if (!this.shopOpen) return;
    this.shopOpen = false;
    this.shopDrop = -1;
    this.input.uiOpen = false;
    this.input.uiKind = null;
    this.hud.hot = [];
    if (this.ownEntity() && this.started) this._lockPointer();
  }

  // ------------------------------------------------------------- settings
  // The gear at the top right - pushed in from the corner to make room for the
  // warning circle - opens the options panel: the master volume and the threat
  // receiver's own slider, plus the debug knobs when the server runs with
  // MECH_DEBUG=1.  It needs a free pointer, so it is clickable right after ESC;
  // like the rearm panel it owns the mouse while it is up.

  // clicks on the HUD while the pointer is free; true when the click was taken
  _hudClick(x, y) {
    const r = this.hud.gearRect;
    if (!r || x < r.x || x > r.x + r.w || y < r.y || y > r.y + r.h) return false;
    this._settingsToggle();
    return true;
  }

  _settingsToggle() {
    if (!this.started) return;
    if (this.settingsOpen) { this._settingsClose(); return; }
    if (this.shopOpen) this._shopClose();
    this.settingsOpen = true;
    this.input.uiOpen = true;
    this.input.uiKind = 'settings';
    this.input.keys.clear();
    this.input.fireL = this.input.fireR = false;
    this.input.qPressed = false;
    if (document.pointerLockElement) document.exitPointerLock();
  }

  _settingsClose() {
    if (!this.settingsOpen) return;
    this.settingsOpen = false;
    this.input.uiOpen = false;
    this.input.uiKind = null;
    this.hud.hot = [];
    if (this.ownEntity() && this.started) this._lockPointer();
  }

  _settingsClick(x, y) {
    if (!this.settingsOpen) return;
    const id = this.hud.hitUi(x, y);
    if (!id) return;
    if (id === 'vol' || id === 'vol-' || id === 'vol+') {
      // the bar is an absolute control, the two buttons a 5% step
      const r = this.hud.volRect;
      const step = (id === 'vol-' ? -0.05 : id === 'vol+' ? 0.05 : 0);
      const v = step ? this.audio.volume + step : (r && r.w ? (x - r.x) / r.w : 0);
      this.audio.setVolume(v);
      return;
    }
    // the threat receiver's own slider, same control, separate bus
    if (id === 'alarm' || id === 'alarm-' || id === 'alarm+') {
      const r = this.hud.alarmRect;
      const step = (id === 'alarm-' ? -0.05 : id === 'alarm+' ? 0.05 : 0);
      const v = step ? this.audio.alarmVolume + step : (r && r.w ? (x - r.x) / r.w : 0);
      this.audio.setAlarmVolume(v);
      return;
    }
    if (id === 'respawn') {
      this.net.send({ t: 'respawn' });
    } else if (id === 'restart') {
      this.net.send({ t: 'debug_restart' });
    } else if (id === 'god') {
      const on = !(this.net.meta && this.net.meta.god);
      this.net.send({ t: 'debug_god', on });
    } else if (id === 'close') {
      this._settingsClose();
    }
  }

  _shopConfirm() {
    this._shopBuy(this.shopDraft);
  }

  // Send one priced draft.  The server prices it again, charges it and books
  // the work it takes (the mech is held still until the bars are full again).
  _shopBuy(draft) {
    if (!this.shopOpen) return;
    if (isEmpty(draft)) { this._message('NOTHING TO BUY', '#ff9a9a'); return; }
    const q = quote(this._shopCfg(), this.net.meta || {}, draft);
    if (!q.ok) {
      this._message(q.lines.length ? 'NOT ENOUGH MONEY' : 'NOTHING TO BUY', '#ff9a9a');
      return;
    }
    this.net.send(toMessage(draft));
    this._shopClose();
  }

  _shopClick(x, y) {
    if (!this.shopOpen) return;
    const id = this.hud.hitUi(x, y);
    const d = this.shopDraft;
    if (!id) { this.shopDrop = -1; return; }
    if (id === 'disabled') return;              // greyed out: nothing to buy
    if (id.startsWith('pick:')) {
      // "pick:<arm>:<weapon>" - the entry a pulled-down list handed back
      const [, arm, wid] = id.split(':');
      d.arms[+arm] = +wid;
      this.shopDrop = -1;
      return;
    }
    if (id === 'drop0' || id === 'drop1') {
      const arm = id === 'drop0' ? 0 : 1;
      this.shopDrop = this.shopDrop === arm ? -1 : arm;
      return;
    }
    this.shopDrop = -1;
    if (id === 'smoke') d.smoke = !d.smoke;
    else if (id === 'repair') d.repair = !d.repair;
    else if (id === 'lrearm') d.rearm[0] = !d.rearm[0];
    else if (id === 'rrearm') d.rearm[1] = !d.rearm[1];
    else if (id === 'confirm') this._shopConfirm();
    else if (id === 'quickall') this._shopBuy(quickDraft());
    else if (id === 'cancel' || id === 'close') this._shopClose();
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
    // cockpit camera: the eye is the commander's sight head, on its optical
    // axis (`defs.mech.eye`), and it looks exactly where the arms point, so
    // the reticle is the centre of the screen at every zoom.  The machine
    // itself is drawn in full (no separate view model), which is what makes
    // the barrels, the hull and the muzzle flashes all sit where the rounds
    // really come out - the only piece left out of the pilot's own machine is
    // the sight head the eye sits in front of (scene.mech).
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
    const eye = this._sightEye(own);
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
      units: this.defs.units,
      buyable: this.buyable,
      rearm: this.rearm,
      towns: m.towns || [],
      playerFaction: faction,
      crosshairs: this.crosshairs,
      leads: this.leads,
      lock: this.lockInfo,
      lockFrac: this.lockFrac,
      // the hull's measured ground speed: the snapshot velocity is what the
      // pilot *asked* for, the displacement between snapshots is what the
      // mech actually did (and it ramps up over three seconds)
      speed: own ? Math.hypot(own.mvx || 0, own.mvz || 0) * 3.6 : 0,
      threats: this.threats || [],
      // metres at the edge of the threat dial, off `defs.radar` and the hull's
      // own `radar_strength` - the corner label reads it back
      radarRing: this.radarRing || 0,
      lasers: this.lasers || [],
      smoke: m.smoke === undefined ? null : m.smoke,
      smokeCd: m.smoke_cd || 0,
      messages: this.messages,
      winner: m.winner !== undefined ? m.winner : this.winner,
      dead: !own,
      atBase,
      mechHp: (this.defs.mech && this.defs.mech.hp) || [100, 0, 50, 50],
      shopOpen: this.shopOpen,
      settingsOpen: this.settingsOpen,
      volume: this.audio.volume,
      alarmVolume: this.audio.alarmVolume,
      debug: this.debug,
      shopDraft: this.shopDraft,
      shopDrop: this.shopDrop,
      shopQuote: this.shopOpen ? quote(this._shopCfg(), m, this.shopDraft) : null,
      // the one-click REPAIR & REARM button prices its own draft, so it can
      // show what it would cost and how long it would take
      shopQuick: this.shopOpen ? quote(this._shopCfg(), m, quickDraft()) : null,
      mouse: [this.input.mx, this.input.my],
      range: this.range,
      zoom: this.zoom,
      zoomIdx: this.zoomIdx,
      zeroRange: this.zeroRange,
      zeroIdx: this.input.zeroIdx,
      zeroSteps: this.input.zeroSteps,
      sightRange: this.sightRange,
      hitFlash: this.hitFlash,
      help: false,
      helpOpen: this.helpOpen,
      stats: { fps: this.fps, draws: this.renderer.stats.draws, instances: this.renderer.stats.instances },
    };
  }
}
