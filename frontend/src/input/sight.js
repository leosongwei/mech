// The sight: where the pilot is looking.
//
// The mouse drives the *sight* - the free turret on the roof that answers it
// instantly; the hull and the arms slew after it at their own rates
// (Systems/Gunnery.cs on the server).  ALT frees the view without moving the
// sight, so nothing follows it either, and the free angles spring back onto the
// sight line when ALT is let go.

import { clamp, wrapPi } from '../core/math.js';

// radians per pixel of mouse movement; the optics divide it, so a 20x sight does
// not whip the turret around
const SENS = 0.0021;
// how fast the free view springs back onto the sight line, per second
const SPRING = 4.5;
// how far the head may crane, and how far the gun may look; the arm's own
// elevation limits are the mount's (`Defs.MECH_PITCH_MIN/MAX`), not these
const FREE_PITCH = 0.9;
const PITCH_MIN = -0.6;
const PITCH_MAX = 0.9;

export class Sight {
  constructor() {
    this.yaw = 0;
    this.pitch = 0;
    this.freeYaw = 0;
    this.freePitch = 0;
    this.alt = false;               // the view is free of the sight
  }

  // The view: the sight plus the free look, so the camera and the cross read one
  // heading with ALT on and off.  A getter on purpose - the view yaw is derived,
  // never stored, and assigning to it must not quietly close the free look.
  get viewYaw() {
    return wrapPi(this.yaw + this.freeYaw);
  }

  // a mouse delta in raw pixels, with the sight's magnification
  look(dx, dy, zoom) {
    const s = SENS / zoom;
    if (this.alt) {
      this.freeYaw = wrapPi(this.freeYaw + dx * s);
      this.freePitch = clamp(this.freePitch - dy * s, -FREE_PITCH, FREE_PITCH);
    } else {
      this.yaw = wrapPi(this.yaw + dx * s);
      this.pitch = clamp(this.pitch - dy * s, PITCH_MIN, PITCH_MAX);
    }
  }

  update(dt) {
    if (this.alt) return;
    const k = Math.min(1, dt * SPRING);
    this.freeYaw += -this.freeYaw * k;
    this.freePitch += -this.freePitch * k;
  }

  // WASD in the frame the machine is driving in: +fz is forward, +fx is right.
  // The keys are handed in because they belong to the input facet, not to the
  // sight.
  //
  // The frame is the view - so the stick follows the cross - *except* with ALT
  // held: then the head is free to crane round and the machine keeps the heading
  // it was walking on, so a pilot looking 90 degrees off still holds W to go the
  // way the hull was going.  Which is the sight's own yaw, because while ALT is
  // down the mouse does not move the sight at all.
  moveVector(keys) {
    let fx = 0, fz = 0;
    if (keys.has('KeyW')) fz += 1;
    if (keys.has('KeyS')) fz -= 1;
    if (keys.has('KeyA')) fx -= 1;
    if (keys.has('KeyD')) fx += 1;
    if (fx === 0 && fz === 0) return [0, 0];
    const y = this.alt ? this.yaw : this.viewYaw;
    const fwd = [Math.cos(y), Math.sin(y)];
    const right = [-Math.sin(y), Math.cos(y)];
    const mx = fwd[0] * fz + right[0] * fx;
    const mz = fwd[1] * fz + right[1] * fx;
    const l = Math.hypot(mx, mz) || 1;
    return [mx / l, mz / l];
  }
}
