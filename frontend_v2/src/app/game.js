// The client's shell: it owns the modules, keeps the frame's state, and delegates
// every job it used to do itself.
//
// What is left here is the composition - who exists, what the frame calls, and the
// handful of doors the rest of the client reaches through (`ownEntity`,
// `sightEye`, `message`, the panel actions).  The aiming, the receiver, the rings,
// the camera, the panels and the boot page all live in `app/` beside this file, and
// the rendering, the world data, the wire and the audio are below it.

import { GameAudio } from '../audio/index.js';
import { emptyDraft } from '../shop.js';
import { HUD } from '../hud/index.js';
import { Input } from '../input/index.js';
import { Net } from '../net/index.js';
import { Particles } from '../fx/particles.js';
import { Renderer } from '../render/renderer.js';
import { KILL_LIFE_S, MONEY_POP_S, MSG_LIFE_S } from '../core/config.js';
import { boot, start } from './boot.js';
import { frame } from './frame.js';
import { hudClick, onUiClick } from './panels.js';
import { hudstate } from './hudstate.js';
import { buy, closeShop, confirmShop, shopClick, shopCfg, toggleShop } from './systems/shop.js';
import { savedAssist, savedRing, closeSettings, settingsClick, toggleAssist, toggleSettings } from './systems/settings.js';
import { Aim } from './systems/aim.js';
import { camera } from './systems/camera.js';
import { Lead } from './systems/lead.js';
import { Rwr } from './systems/rwr.js';

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
    this.flashes = new Map();       // muzzle flash per hull, `{t, arm}`
    this.map = null;
    this.defs = { weapons: {}, units: {}, mech: { muzzles: {} } };
    this.init = null;               // the server's init payload
    this.buyable = [];
    this.rearm = null;
    this.playerFaction = -1;
    this.messages = [];             // the notice stack, oldest first
    this.moneyPops = [];            // `+$N` payouts, drawn beside the money readout
    this.hitFlash = 0;
    this.shake = 0;
    this.sendAcc = 0;
    this.fps = 60;
    this.lastHpSum = -1;
    this.crosshairs = [null, null];
    this.leads = [];                // pre-aim boxes, projected for this frame
    this.helpOpen = false;
    this.started = false;
    this.ready = false;
    this.winner = -1;
    this.deathSpot = null;          // where the pilot's hull was last seen alive
    this.zoom = 1;                  // smoothed, drives the field of view
    this.zoomIdx = 0;               // optics step selected with the wheel
    this.zeroRange = 1000;          // sight setting in metres (CTRL+wheel)
    this.range = false;             // weapon range mode (server started with --range)
    this.lockFrac = 0.05;           // IR lock circle, as a fraction of the view width
    this.lockInfo = null;           // the tracked hull, projected for the HUD
    this.sightRange = null;         // the centre cross's own range reading
    this.wreckPuffs = new Map();    // per-hulk smoke timer
    this.seekerLog = [];            // every IR lock smoke or terrain broke, oldest first
    this.shopOpen = false;          // rearm panel (B) is up: the mouse is a cursor
    this.settingsOpen = false;      // options panel (gear): volume + debug knobs
    this.debug = false;             // server started with MECH_DEBUG=1
    this.shopDraft = emptyDraft();  // edits waiting for CONFIRM
    this.shopDrop = -1;             // which arm's weapon list is pulled down
    this.aimAssist = savedAssist(); // the cross snaps to a pre-aim ring, on by default
    this.assistRing = savedRing();  // x the plain circle, how wide that ring is
    this.aimSnap = null;            // the pre-aim ring the assist caught, if any
    this.denyTimer = 0;             // the empty-trigger message's own clock
    this.last = 0;                  // the frame loop's clock
    this.nameEl = null;
    this.factionChoice = -1;

    // the client's systems: one instance each, all of them reading the app
    this.rwr = new Rwr();
    this.aim = new Aim(this);
    this.lead = new Lead(this);
  }

  async boot() {
    return boot(this);
  }

  start() {
    return start(this);
  }

  frame() {
    return frame(this);
  }

  camera(own, dt) {
    return this._camera(own, dt);
  }

  // The camera is a method, not a stage-only call: `g.__cam` in the console
  // replaces `game._camera` to put the eye anywhere (the documented way to look
  // at the terrain), and that hook has to keep working.
  _camera(own, dt) {
    return camera(this, own, dt);
  }

  // The pilot's own hull, straight off the snapshot (the id is the meta's).
  ownEntity() {
    const m = this.net.meta;
    if (!m || m.mech < 0) return null;
    return this.net.entities.get(m.mech) || null;
  }

  // ------------------------------------------------------------ the notice stack
  message(text, color) {
    this.messages.push({ text, color, life: MSG_LIFE_S });
    if (this.messages.length > 40) this.messages.shift();
  }

  // A payout, said where the money is: a hull is worth its bounty and the number
  // that grew is the one in the corner, so the line goes beside it and fades
  // there.  Nothing about a kill is a `message`: the kill feed is the words and
  // this is the till.
  moneyGain(amount) {
    if (!(amount > 0)) return;
    this.moneyPops.push({ amount, life: MONEY_POP_S });
    if (this.moneyPops.length > 6) this.moneyPops.shift();
  }

  // A kill line, the way every shooter writes one: `killer killed victim with
  // weapon`, the two hulls in their own side's colour, the weapon bold.  The
  // server composes the words (it is the only side that knows who a nameless hull
  // was), the client only paints them; the money is `moneyGain`'s.
  killfeed(ev) {
    const segs = [
      { text: ev.killer, fac: ev.kfac, bold: true },
      { text: ' killed ', color: '#93a89c' },
      { text: ev.victim, fac: ev.vfac, bold: true },
    ];
    if (ev.w) segs.push({ text: ' with ', color: '#93a89c' },
                        { text: ev.w.toLowerCase(), color: '#e8f4ee', bold: true });
    this.messages.push({ segs, life: KILL_LIFE_S });
    if (this.messages.length > 40) this.messages.shift();
  }

  // ------------------------------------------------------------- aiming doors
  // The geometry lives in `app/systems/aim.js`; these are the names the rest of
  // the client built on, kept because a solve reads better as a verb about the
  // machine than as a call through a system object.
  sightEye(own, at = null) {
    return this.aim.sightEye(own, at);
  }

  sightAim() {
    return this.aim.sightAim();
  }

  muzzle(own, arm, at = null) {
    return this.aim.muzzle(own, arm, at);
  }

  firePose(own) {
    return this.aim.firePose(own);
  }

  ringSolve(own, a) {
    return this.aim.ringSolve(own, a);
  }

  ringPx() {
    return this.aim.ringPx();
  }

  // ------------------------------------------------------------- panel doors
  shopCfg() {
    return shopCfg(this);
  }

  toggleShop() {
    return toggleShop(this);
  }

  closeShop() {
    return closeShop(this);
  }

  confirmShop() {
    return confirmShop(this);
  }

  buy(draft) {
    return buy(this, draft);
  }

  shopClick(x, y) {
    return shopClick(this, x, y);
  }

  toggleSettings() {
    return toggleSettings(this);
  }

  closeSettings() {
    return closeSettings(this);
  }

  toggleAssist() {
    return toggleAssist(this);
  }

  settingsClick(x, y) {
    return settingsClick(this, x, y);
  }

  hudClick(x, y) {
    return hudClick(this, x, y);
  }

  uiClick(x, y) {
    return onUiClick(this, x, y);
  }

  // the HUD's state, for a console session that wants to see what it is drawing
  hudState(own = this.ownEntity()) {
    return hudstate(this, own);
  }
}
