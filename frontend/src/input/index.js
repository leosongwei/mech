// The input facet: the keyboard, the pointer and the sight in one object.
//
// The three concerns live next door - `bindings.js` is the key map as data,
// `pointer.js` the mouse and the pointer lock, `sight.js` the two headings - and
// this class is what the app reads and writes: the fields an input message is
// built from (`yaw`, `pitch`, `moveVector()`, `zeroRange`, `zoom`, the two
// trigger bits, the latched smoke pull) and the nine callbacks the panels and
// the cross hang off.

import { ZOOM_LEVELS } from '../core/config.js';
import { KEYS, lookup, pickArm } from './bindings.js';
import { Pointer, ZERO_STEPS } from './pointer.js';
import { Sight } from './sight.js';

export class Input {
  constructor(canvas) {
    this.canvas = canvas;
    this.keys = new Set();
    this.sight = new Sight();
    this.fireL = false;
    this.fireR = false;
    this.locked = false;              // the pointer is on the cockpit, not the HUD
    this.zoomIdx = 0;
    this.zeroSteps = ZERO_STEPS.slice();
    this.zeroIdx = 1;                 // 1000 m
    this.wheelAcc = 0;
    this.mx = 0;
    this.my = 0;
    this.qPressed = false;            // one pull of the smoke trigger, sent once
    this.enabled = false;
    this.uiOpen = false;              // set by the app: panel owns the mouse
    this.uiKind = null;               // 'shop' or 'settings': what that panel is

    this.onPick = null;               // Digit1-6 while the rearm panel is up
    this.onShop = null;               // B: open / close the rearm panel
    this.onShopConfirm = null;        // ENTER while it is open
    this.onShopCancel = null;         // ESC / B while it is open
    this.onUiClick = null;            // mouse click in the panel, client pixels
    this.onToggleHelp = null;
    this.onToggleAssist = null;       // V: the cross's snap to a pre-aim ring
    this.onSettings = null;           // ESC while the settings panel is up
    this.onHudClick = null;           // click on a HUD button, pointer not locked

    this.pointer = new Pointer(canvas, this);
    window.addEventListener('keydown', (e) => this._keyDown(e));
    window.addEventListener('keyup', (e) => this._keyUp(e));
  }

  // The sight's two headings are the sight's - writing `g.input.yaw` from the
  // console has to reach the same number the mouse does, so the facet carries
  // them as accessors rather than as a second copy.
  get yaw() { return this.sight.yaw; }
  set yaw(v) { this.sight.yaw = v; }

  get pitch() { return this.sight.pitch; }
  set pitch(v) { this.sight.pitch = v; }

  get freeYaw() { return this.sight.freeYaw; }
  set freeYaw(v) { this.sight.freeYaw = v; }

  get freePitch() { return this.sight.freePitch; }
  set freePitch(v) { this.sight.freePitch = v; }

  get alt() { return this.sight.alt; }
  set alt(v) { this.sight.alt = v; }

  // the heading the camera, the cross and the range readout read
  get viewYaw() { return this.sight.viewYaw; }

  // the range the sight is zeroed to, metres
  get zeroRange() { return this.zeroSteps[this.zeroIdx]; }

  get zoom() { return ZOOM_LEVELS[this.zoomIdx]; }

  update(dt) {
    this.sight.update(dt);
  }

  moveVector() {
    return this.sight.moveVector(this.keys);
  }

  _keyDown(e) {
    const code = e.code;
    // The panel owns the keyboard while it is up: the settings panel is modal
    // and swallows everything, the rearm panel takes the keys it names and hands
    // the rest back to the game.
    const entry = lookup(code, this.uiOpen ? this.uiKind : null);
    if (entry && entry.prevent) e.preventDefault();
    if (entry && entry.swallow) return;
    if (entry && entry.action) {
      if (entry.once && e.repeat) return;
      if (this._dispatch(entry.action, e)) return;
    }
    this.keys.add(code);
  }

  _keyUp(e) {
    const code = e.code;
    const entry = KEYS[code];
    if (entry && entry.prevent) e.preventDefault();
    if (entry && entry.action === 'freeLook') this.sight.alt = false;
    this.keys.delete(code);
  }

  // Run one action; answers true when it swallowed the key.  A binding acts only
  // while the app has put a callback behind it, so a panel that is up without one
  // leaves the key held like any other.
  _dispatch(action, e) {
    switch (action) {
      case 'settings':
        // the settings panel is modal: its ESC is the one key it answers, and
        // it takes every key either way
        if (this.onSettings) this.onSettings();
        return true;
      case 'shopConfirm':
        if (!this.onShopConfirm) return false;
        this.onShopConfirm();
        return true;
      case 'shopCancel':
        if (!this.onShopCancel) return false;
        this.onShopCancel();
        return true;
      case 'shop':
        if (!this.onShop) return false;
        this.onShop();
        return true;
      case 'pick': {
        const arm = pickArm(e.code);
        if (arm < 0 || !this.onPick) return false;
        this.onPick(arm, e.shiftKey);
        return true;
      }
      case 'help':
        if (this.onToggleHelp) this.onToggleHelp();
        return false;
      case 'assist':
        if (this.onToggleAssist) this.onToggleAssist();
        return false;
      case 'smoke':
        this.qPressed = true;
        return false;
      case 'freeLook':
        this.sight.alt = true;
        return false;
      default:
        return false;
    }
  }
}
