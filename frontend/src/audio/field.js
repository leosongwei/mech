// The half of the audio system that lives in the air.
//
// A bang is not a point event: it leaves the source as a sphere growing at the
// speed of sound.  Every acoustic cue goes on this list with where, when and how
// loud it happened (`level`, the source level from `SOUND_LEVEL`), and each frame
// the sphere is compared with where the mech is *now* - so a hull that has driven on
// since the shell landed hears it once the wavefront actually catches up (running
// away delays it, closing on it hurries it), and the volume is the level at that
// arrival distance.  No Doppler: the pitch is whatever the synth produced.
//
// It is the client's other half of the server's `Netcode.hearable`: the server
// gates an event by whether *this* ear could have heard it, and this decides when.
// The app owns one of these (`GameAudio.field`) and does three things with it -
// `push` from the fx stage, `update` once a frame, `clear` when the pilot is gone.

import { SOUND_FLOOR, SOUND_SPEED, attenuation } from './levels.js';
import { EAR_NEAR, SOUND_TTL } from '../core/config.js';

// The fastest hull on the field, m/s: a mech tops out at 50 and the wavefront can
// never be hurried past this much either, which is the floor `tMin` is built on.
const CLOSING_MAX = 80;

export class SoundField {
  constructor(audio) {
    // the engine this field is part of (`GameAudio.field`); the events themselves
    // play through the closures `push` is handed
    this.a = audio;
    this.list = [];               // acoustic events in the air, oldest first
    this.own = null;              // the pilot's hull, as of the last `update`
  }

  // An event in the air.  `play(vol, dist, pan, behind)` is what runs when the
  // wavefront arrives; `vol` is already the level at that distance.
  push(x, y, z, level, play) {
    const t0 = performance.now() / 1000;
    // the wavefront cannot arrive before this: the mech closes on the source at its
    // own speed at most, so `SOUND_SPEED` plus the fastest hull is the quickest the
    // gap can shut.  A battle holds thousands of events in the air and this is what
    // lets the per-frame pass skip the ones that are still out there, where the
    // distance solve is the whole cost of the loop.
    const own = this.own;
    const d0 = own ? Math.hypot(x - own.x, y - own.y, z - own.z) : 0;
    this.list.push({ t0, x, y, z, level, play,
                     tMin: t0 + d0 / (SOUND_SPEED + CLOSING_MAX) });
  }

  // One frame: play everything whose sphere has reached the pilot's *current*
  // position.  `now` is seconds on the same clock `push` stamped (`performance`
  // based), and `viewYaw` is where the head is pointing at the moment of arrival.
  update(own, viewYaw, now) {
    // the ear the next `push` measures its wavefront floor against: the same hull a
    // sound landing this frame is measured from, so the two halves of the solve
    // never disagree about where the pilot is
    this.own = own;
    const list = this.list;
    if (!list.length) return;
    // no mech, no ear: the events in flight are for a pilot who is not there to
    // hear them, and holding fifty seconds of a battle for nobody is the one case
    // where the list is pure work
    if (!own) { list.length = 0; return; }
    let n = 0;
    for (let i = 0; i < list.length; i++) {
      const s = list[i];
      const age = now - s.t0;
      if (age > SOUND_TTL) continue;                   // nobody could still hear it
      if (now < s.tMin) { list[n++] = s; continue; }   // still on its way out
      const dist = Math.hypot(s.x - own.x, s.y - own.y, s.z - own.z);
      if (age * SOUND_SPEED >= dist) {
        const vol = s.level * attenuation(dist);
        if (vol > SOUND_FLOOR) {
          const [pan, behind] = this._bearing(s, own, dist, viewYaw);
          s.play(vol, dist, pan, behind);
        }
        continue;                                      // arrived: off the list
      }
      list[n++] = s;
    }
    list.length = n;
  }

  // Where a bang landed in the cockpit's ears: the lateral share of its bearing is
  // the pan (-1 left .. +1 right), and how much of it was behind the pilot is
  // something all stereo cannot say - that comes back as a dulling (`graph.bus`).
  // Taken at the moment the wavefront arrives, from where the mech is *then* and the
  // view yaw, because that is where the head is when the sound reaches it.
  _bearing(s, own, dist, viewYaw) {
    if (!own || !(dist > 0.5)) return [0, 0];
    const yaw = viewYaw;
    const dx = (s.x - own.x) / dist;
    const dz = (s.z - own.z) / dist;
    // dirFrom's forward is (cos yaw, sin yaw), so right is a quarter turn on
    const right = -dx * Math.sin(yaw) + dz * Math.cos(yaw);
    const fwd = dx * Math.cos(yaw) + dz * Math.sin(yaw);
    // a bang right on the canopy is neither left nor right
    const near = Math.min(1, dist / EAR_NEAR);
    return [Math.max(-1, Math.min(1, right)) * near, Math.max(0, -fwd)];
  }

  // Every event in the air, dropped: a restart, or a pilot who is gone for good.
  clear() {
    this.list.length = 0;
    this.own = null;
  }
}
