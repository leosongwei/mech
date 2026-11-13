// The client's front door: the boot page, the lobby, and the moment the pilot
// lands in the cockpit.
//
// `boot()` opens the socket, copies the definitions the server sends into the
// app, loads the map into the renderer and the HUD, and wires the input facet's
// nine callbacks to the systems that answer them.  The lobby is plain DOM (it is
// on screen before any of this runs), `start()` is the one shot that sends the
// join, hides the page, locks the pointer and starts the frame loop.

import { NAME_KEY } from '../core/config.js';
import { load, save } from '../core/storage.js';
import { onFx } from '../fx/dispatch.js';
import { MapData } from '../map/index.js';
import { hudClick, lockPointer, onUiClick } from './panels.js';
import { closeShop, confirmShop, toggleShop } from './systems/shop.js';
import { toggleAssist, toggleSettings } from './systems/settings.js';

// The lobby's callsign is the one setting that is not the audio bus's business
// (`audio/index.js` keeps the two volumes), so it is stored under the same
// `mech.` prefix: private mode has no storage and the lobby simply comes up on
// `PILOT-<pid>`.
function savedName() {
  return (load(NAME_KEY, '') || '').trim().slice(0, 16);
}

function rememberName(name) {
  save(NAME_KEY, name);
}

export async function boot(app) {
  status(app, 'CONNECTING...');
  try {
    const init = await app.net.connect();
    app.init = init;
    app.defs.weapons = init.weapons;
    app.defs.units = init.units;
    app.defs.mech = init.mech;
    // the radar equation's constants: the client solves the same
    // `d^2 <= K * v_closing` the server's AI does
    app.defs.radar = init.radar;
    app.buyable = init.buyable;
    app.rearm = init.rearm || null;
    app.playerFaction = init.faction;
    // the death cam's centre starts on this side's base, and the first live
    // frame takes it over with the hull's own position
    app.deathSpot = { x: init.bases[init.faction][0], z: init.bases[init.faction][1] };
    app.range = init.mode === 'range';
    if (init.lock && init.lock.frac) app.lockFrac = init.lock.frac;
    // sight settings come from the server so the steps are one list
    if (init.zero && init.zero.steps) {
      app.input.zeroSteps = init.zero.steps.slice();
      const d = app.input.zeroSteps.indexOf(init.zero.default);
      app.input.zeroIdx = d < 0 ? 0 : d;
      app.zeroRange = app.input.zeroRange;
    }
    status(app, app.range ? 'LOADING RANGE...' : 'LOADING 16 KM MAP...');
    app.map = await MapData.load(init.map, init.lanes);
    app.renderer.setMap(app.map);
    app.hud.setMap(app.map);
    status(app, 'READY');
    app.net.onFx = (evs) => onFx(app, evs);
    app.net.onMessage = (msg) => {
      if (msg.t === 'info') app.message(msg.msg, '#ffe08a');
      if (msg.t === 'joined') {
        app.playerFaction = msg.faction;
        app.message(`DEPLOYED AS ${msg.name}`, '#a9f0c0');
      }
    };
    app.input.onPick = (idx, right) => {
      // the digits only move the draft: CONFIRM is the one thing that spends
      // money, and a number pressed with the panel shut picks nothing
      if (!app.shopOpen) return;
      const wid = app.buyable[idx];
      if (wid !== undefined) app.shopDraft.arms[right ? 1 : 0] = wid;
    };
    app.input.onShop = () => toggleShop(app);
    app.input.onShopConfirm = () => confirmShop(app);
    app.input.onShopCancel = () => closeShop(app);
    app.input.onUiClick = (x, y) => onUiClick(app, x, y);
    app.input.onToggleHelp = () => { app.helpOpen = !app.helpOpen; };
    app.input.onToggleAssist = () => toggleAssist(app);
    app.input.onSettings = () => toggleSettings(app);
    app.input.onHudClick = (x, y) => hudClick(app, x, y);
    if (app.range) {
      // the lobby is the same, only the subtitle says what this server is
      const sub = app.bootEl.querySelector('p');
      if (sub) sub.textContent = 'WEAPON RANGE · 4 KM MAP · APC TARGETS · REARM ANYWHERE';
    }
    setupLobby(app, init);
    app.debug = !!init.debug;
    document.getElementById('startBtn').addEventListener('click', () => start(app));
    app.ready = true;
  } catch (err) {
    status(app, 'CONNECTION FAILED - IS THE SERVER RUNNING?');
    app.errEl.textContent = String(err);
    console.error(err);
  }
}

export function status(app, text) {
  let p = document.getElementById('bootStatus');
  if (!p) {
    p = document.createElement('p');
    p.id = 'bootStatus';
    app.bootEl.appendChild(p);
  }
  p.textContent = text;
}

export function setupLobby(app, init) {
  // no loadout here: every mech leaves the lobby on the default pair and
  // weapons are a rearm-panel purchase (`B`) like everything else
  app.nameEl = document.getElementById('pname');
  app.nameEl.value = savedName() || `PILOT-${init.pid}`;

  app.factionChoice = -1;
  document.querySelectorAll('.fac').forEach((btn) => {
    btn.addEventListener('click', () => {
      document.querySelectorAll('.fac').forEach((b) => b.classList.remove('on'));
      btn.classList.add('on');
      app.factionChoice = parseInt(btn.dataset.fac, 10);
    });
  });
  if (init.faction === 0 || init.faction === 1) {
    // highlight the auto-assigned side but keep AUTO selectable
    app.nameEl.addEventListener('keydown', (e) => e.stopPropagation());
  }
}

export function start(app) {
  // once only: the DEPLOY button keeps DOM focus after the lobby fades out,
  // and a later ENTER would fire its native click - a free respawn
  if (!app.ready || app.started) return;
  app.started = true;
  const btn = document.getElementById('startBtn');
  if (btn && btn.blur) btn.blur();
  const name = (app.nameEl && app.nameEl.value.trim()) || `PILOT-${app.init.pid}`;
  rememberName(name);
  app.net.send({ t: 'join', name, faction: app.factionChoice });
  app.input.enabled = true;
  app.audio.ensure();
  app.audio.resume();
  // a click that the browser does not count as user activation (or a start
  // from the keyboard) leaves the context suspended, and every alarm stays
  // silent: unlock on the first real gesture instead
  const unlock = () => app.audio.resume();
  window.addEventListener('pointerdown', unlock, { once: true });
  window.addEventListener('keydown', unlock, { once: true });
  app.bootEl.classList.add('hidden');
  lockPointer(app);
  app.last = performance.now();
  requestAnimationFrame(() => app.frame());
  app.net.send({ t: 'in', yaw: 0, pitch: 0, mx: 0, mz: 0, fl: false, fr: false });
}
