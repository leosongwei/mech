// Mouse + keyboard.  Mouse X drives the torso, mouse Y drives arm elevation,
// ALT frees the view without moving the torso or the arms.

import { clamp, wrapPi } from './math.js';

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
    this.onBuy = null;
    this.onToggleHelp = null;
    this.enabled = false;

    window.addEventListener('keydown', (e) => this._key(e, true));
    window.addEventListener('keyup', (e) => this._key(e, false));
    window.addEventListener('mousemove', (e) => this._mouse(e));
    canvas.addEventListener('mousedown', (e) => {
      if (!this.enabled) return;
      if (!this.locked) { canvas.requestPointerLock(); return; }
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
    if (down && this.onBuy && /^Digit[1-5]$/.test(code)) {
      this.onBuy(parseInt(code.slice(5), 10) - 1, e.shiftKey);
      e.preventDefault();
      return;
    }
    if (down && code === 'KeyH' && this.onToggleHelp) this.onToggleHelp();
    if (code === 'AltLeft' || code === 'AltRight') {
      this.alt = down;
      e.preventDefault();
    }
    if (down) this.keys.add(code); else this.keys.delete(code);
    if (['KeyW', 'KeyA', 'KeyS', 'KeyD', 'Space', 'Tab'].includes(code)) e.preventDefault();
  }

  _mouse(e) {
    if (!this.locked) return;
    const dx = e.movementX || 0;
    const dy = e.movementY || 0;
    if (this.alt) {
      this.freeYaw = wrapPi(this.freeYaw + dx * this.sens);
      this.freePitch = clamp(this.freePitch + dy * this.sens, -0.9, 0.9);
    } else {
      this.yaw = wrapPi(this.yaw + dx * this.sens);
      this.pitch = clamp(this.pitch - dy * this.sens, -0.6, 0.9);
    }
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
