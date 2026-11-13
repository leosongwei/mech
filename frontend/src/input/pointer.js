// The pointer: the two buttons, pointer lock, the wheel, and the free-pointer
// HUD click path.
//
// It listens and writes into the `input` facet it is given, so this file is one
// concern and not a second owner of the state: the sight takes the mouse motion
// (`input.sight`), a panel takes the mouse entirely while it is up, and a click
// with the pointer free is either a HUD button's or it grabs the lock back for
// the cockpit.

import { ZOOM_LEVELS } from '../core/config.js';
import { BUTTONS } from './bindings.js';

// sight settings in metres: CTRL+wheel steps the zero, the plain wheel the
// optics.  Replaced by the server's list (`defs.ZERO_STEPS`) once a world
// arrives, so both ends step over the same ranges.
export const ZERO_STEPS = [500, 1000, 1500, 2000, 2500, 3000, 3500, 4000];

// one notch of a wheel, or one swipe of a trackpad, is one step
const NOTCH = 40;

export class Pointer {
  constructor(canvas, input) {
    this.canvas = canvas;
    this.input = input;

    window.addEventListener('mousemove', (e) => this._move(e));
    window.addEventListener('wheel', (e) => this._wheel(e), { passive: false });
    canvas.addEventListener('mousedown', (e) => this._down(e));
    window.addEventListener('mouseup', (e) => this._up(e));
    window.addEventListener('contextmenu', (e) => e.preventDefault());
    document.addEventListener('pointerlockchange', () => {
      input.locked = document.pointerLockElement === canvas;
      if (!input.locked) { input.fireL = false; input.fireR = false; }
    });
  }

  _down(e) {
    const input = this.input;
    if (!input.enabled) return;
    // the rearm panel is a normal mouse UI: no pointer lock, no trigger
    if (input.uiOpen) {
      input.mx = e.clientX; input.my = e.clientY;
      if (input.onUiClick) input.onUiClick(e.clientX, e.clientY);
      e.preventDefault();
      return;
    }
    if (!input.locked) {
      // the mouse is free: a click either takes a HUD button or grabs the
      // pointer back for the cockpit
      input.mx = e.clientX; input.my = e.clientY;
      if (input.onHudClick && input.onHudClick(e.clientX, e.clientY)) { e.preventDefault(); return; }
      this.canvas.requestPointerLock();
      return;
    }
    const slot = BUTTONS[e.button];
    if (slot) input[slot] = true;
  }

  _up(e) {
    const slot = BUTTONS[e.button];
    if (slot) this.input[slot] = false;
  }

  _move(e) {
    const input = this.input;
    if (input.uiOpen) { input.mx = e.clientX; input.my = e.clientY; return; }
    if (!input.locked) {
      // free pointer: only the HUD buttons want it, and they highlight on hover
      input.mx = e.clientX; input.my = e.clientY;
      return;
    }
    input.sight.look(e.movementX || 0, e.movementY || 0, input.zoom);
  }

  _wheel(e) {
    const input = this.input;
    if (!input.enabled) return;
    e.preventDefault();
    // one notch (or a trackpad swipe) per step
    input.wheelAcc += e.deltaY;
    if (Math.abs(input.wheelAcc) < NOTCH) return;
    const dir = input.wheelAcc > 0 ? -1 : 1;
    input.wheelAcc = 0;
    // CTRL steers the sight setting, everything else the optics
    if (e.ctrlKey) {
      input.zeroIdx = Math.max(0, Math.min(input.zeroSteps.length - 1, input.zeroIdx + dir));
      return;
    }
    input.zoomIdx = Math.max(0, Math.min(ZOOM_LEVELS.length - 1, input.zoomIdx + dir));
  }
}
