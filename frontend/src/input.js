// Mouse + keyboard.  The mouse drives the *sight* - the free turret on the
// roof that answers it instantly; the hull and the arms slew after it at their
// own rates (systems/gunnery.py on the server).  ALT frees the view without
// moving the sight, so nothing follows it either.

import { clamp, wrapPi } from './math.js';

// optics steps selected with the wheel; sensitivity is divided by the
// magnification so a 20x sight does not whip the turret around
export const ZOOM_LEVELS = [1, 4, 10, 20];
// sight settings in metres: CTRL+wheel steps the zero, the plain wheel the
// optics.  Replaced by the server's list (`defs.ZERO_STEPS`) once a world
// arrives, so both ends step over the same ranges.
const ZERO_STEPS = [500, 1000, 1500, 2000, 2500, 3000, 3500, 4000];

export class Input {
  constructor(canvas) {
    this.canvas = canvas;
    this.keys = new Set();
    this.yaw = 0;
    this.pitch = 0;
    this.freeYaw = 0;
    this.freePitch = 0;
    this.alt = false;
    this.fireL = false;
    this.fireR = false;
    this.locked = false;
    this.sens = 0.0021;
    this.zoomIdx = 0;
    this.zeroSteps = ZERO_STEPS.slice();
    this.zeroIdx = 1;                 // 1000 m
    this.wheelAcc = 0;
    this.onBuy = null;
    this.onToggleHelp = null;
    this.onSettings = null;         // ESC while the settings panel is up
    this.onHudClick = null;         // click on a HUD button, pointer not locked
    this.onShop = null;             // B: open / close the rearm panel
    this.onShopConfirm = null;      // ENTER while it is open
    this.onShopCancel = null;       // ESC / B while it is open
    this.onUiClick = null;          // mouse click in the panel, client pixels
    this.uiOpen = false;            // set by the game: panel owns the mouse
    this.uiKind = null;             // 'shop' or 'settings': what that panel is
    this.mx = 0;
    this.my = 0;
    this.qPressed = false;          // one pull of the smoke trigger, sent once
    this.enabled = false;

    window.addEventListener('keydown', (e) => this._key(e, true));
    window.addEventListener('keyup', (e) => this._key(e, false));
    window.addEventListener('mousemove', (e) => this._mouse(e));
    window.addEventListener('wheel', (e) => this._wheel(e), { passive: false });
    canvas.addEventListener('mousedown', (e) => {
      if (!this.enabled) return;
      // the rearm panel is a normal mouse UI: no pointer lock, no trigger
      if (this.uiOpen) {
        this.mx = e.clientX; this.my = e.clientY;
        if (this.onUiClick) this.onUiClick(e.clientX, e.clientY);
        e.preventDefault();
        return;
      }
      if (!this.locked) {
        // the mouse is free: a click either takes a HUD button or grabs the
        // pointer back for the cockpit
        this.mx = e.clientX; this.my = e.clientY;
        if (this.onHudClick && this.onHudClick(e.clientX, e.clientY)) { e.preventDefault(); return; }
        canvas.requestPointerLock();
        return;
      }
      if (e.button === 0) this.fireL = true;
      if (e.button === 2) this.fireR = true;
    });
    window.addEventListener('mouseup', (e) => {
      if (e.button === 0) this.fireL = false;
      if (e.button === 2) this.fireR = false;
    });
    window.addEventListener('contextmenu', (e) => e.preventDefault());
    document.addEventListener('pointerlockchange', () => {
      this.locked = document.pointerLockElement === canvas;
      if (!this.locked) { this.fireL = false; this.fireR = false; }
    });
  }

  _key(e, down) {
    const code = e.code;
    if (this.uiOpen) {
      // the panel takes the keyboard while it is up
      if (this.uiKind === 'settings') {
        // the settings panel is modal: nothing else reaches the game from here
        if (down && code === 'Escape' && this.onSettings) {
          this.onSettings();
          e.preventDefault();
        }
        return;
      }
      // ENTER buys, ESC drops the rearm panel
      if (down && code === 'Enter' && this.onShopConfirm) { this.onShopConfirm(); e.preventDefault(); return; }
      if (down && (code === 'Escape' || code === 'KeyB') && this.onShopCancel) {
        this.onShopCancel(); e.preventDefault(); return;
      }
      if (down && /^Digit[1-6]$/.test(code) && this.onBuy) {
        // number keys pick the weapon for an arm while the panel is up
        this.onBuy(parseInt(code.slice(5), 10) - 1, e.shiftKey);
        e.preventDefault();
        return;
      }
    }
    if (down && code === 'KeyB' && this.onShop) { this.onShop(); e.preventDefault(); return; }
    if (down && this.onBuy && /^Digit[1-6]$/.test(code)) {
      this.onBuy(parseInt(code.slice(5), 10) - 1, e.shiftKey);
      e.preventDefault();
      return;
    }
    if (down && code === 'KeyH' && this.onToggleHelp) this.onToggleHelp();
    // one-shot keys: the browser repeats keydown while a key is held, so the
    // pull is latched and the sender clears it
    if (down && code === 'KeyQ' && !e.repeat) this.qPressed = true;
    if (code === 'AltLeft' || code === 'AltRight') {
      this.alt = down;
      e.preventDefault();
    }
    if (down) this.keys.add(code); else this.keys.delete(code);
    if (['KeyW', 'KeyA', 'KeyS', 'KeyD', 'Space', 'Tab'].includes(code)) e.preventDefault();
  }

  _mouse(e) {
    if (this.uiOpen) { this.mx = e.clientX; this.my = e.clientY; return; }
    if (!this.locked) {
      // free pointer: only the HUD buttons want it, and they highlight on hover
      this.mx = e.clientX; this.my = e.clientY;
      return;
    }
    const dx = (e.movementX || 0) / this.zoom;
    const dy = (e.movementY || 0) / this.zoom;
    if (this.alt) {
      this.freeYaw = wrapPi(this.freeYaw + dx * this.sens);
      this.freePitch = clamp(this.freePitch - dy * this.sens, -0.9, 0.9);
    } else {
      this.yaw = wrapPi(this.yaw + dx * this.sens);
      this.pitch = clamp(this.pitch - dy * this.sens, -0.6, 0.9);
    }
  }

  _wheel(e) {
    if (!this.enabled) return;
    e.preventDefault();
    // one notch (or a trackpad swipe) per step
    this.wheelAcc += e.deltaY;
    if (Math.abs(this.wheelAcc) < 40) return;
    const dir = this.wheelAcc > 0 ? -1 : 1;
    this.wheelAcc = 0;
    // CTRL steers the sight setting, everything else the optics
    if (e.ctrlKey) {
      this.zeroIdx = Math.max(0, Math.min(this.zeroSteps.length - 1, this.zeroIdx + dir));
      return;
    }
    this.zoomIdx = Math.max(0, Math.min(ZOOM_LEVELS.length - 1, this.zoomIdx + dir));
  }

  // the range the sight is zeroed to, metres
  get zeroRange() {
    return this.zeroSteps[this.zeroIdx];
  }

  get zoom() {
    return ZOOM_LEVELS[this.zoomIdx];
  }

  update(dt) {
    if (!this.alt) {
      this.freeYaw += -this.freeYaw * Math.min(1, dt * 4.5);
      this.freePitch += -this.freePitch * Math.min(1, dt * 4.5);
    }
  }

  get viewYaw() {
    return wrapPi(this.yaw + this.freeYaw);
  }

  moveVector() {
    let fx = 0, fz = 0;
    if (this.keys.has('KeyW')) fz += 1;
    if (this.keys.has('KeyS')) fz -= 1;
    if (this.keys.has('KeyA')) fx -= 1;
    if (this.keys.has('KeyD')) fx += 1;
    if (fx === 0 && fz === 0) return [0, 0];
    const y = this.viewYaw;
    const fwd = [Math.cos(y), Math.sin(y)];
    const right = [-Math.sin(y), Math.cos(y)];
    let mx = fwd[0] * fz + right[0] * fx;
    let mz = fwd[1] * fz + right[1] * fx;
    const l = Math.hypot(mx, mz) || 1;
    return [mx / l, mz / l];
  }
}
