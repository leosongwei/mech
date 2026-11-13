// The audio engine's door: one object the app holds, and the only one it talks to.
//
// It is a facade over four pieces, and each of them owns one thing:
//
//   graph.js    the permanent nodes - master, limiter, the soft clipper, the alarm
//               bus, the room, the far bed - plus `ensure` / `resume` / `dispose`
//   voices.js   the voice budget: how many may sound at once, and which one loses
//   synth.js    the voice toolbox: the three shapes a cue is built out of, and the
//               only place a temporary chain is opened or torn down
//   cues.js     the recipes (a gunshot, a round landing, a hull going up, a refused
//               trigger), and the far-field version of each
//   alarms.js   the long-lived machine voices: the missile trill, the laser train,
//               the lock tone
//   field.js    the sound events in the air, and when their wavefront arrives
//
// Everything happens inside `ensure()`: an `AudioContext` cannot be built before the
// page has had a user gesture, and this module is imported by the HUD as well, which
// runs while the lobby is still up.
//
// The app drives three things directly - `audio.field.push(...)` from the fx stage,
// `audio.field.update(own, viewYaw, now)` once a frame, and the sliders - and
// nothing else touches a node.

import { load, save } from '../core/storage.js';
import { DEFAULT_ALARM, DEFAULT_VOLUME, MASTER_GAIN, ALARM_GAIN } from './levels.js';
import { Graph } from './graph.js';
import { Voices } from './voices.js';
import { SoundField } from './field.js';
import * as cues from './cues.js';
import * as alarms from './alarms.js';

const VOLUME_KEY = 'mech.volume';
const ALARM_VOLUME_KEY = 'mech.alarmvolume';

// 0..1 out of storage, or the default when nothing valid is there.  A private-mode
// window has no storage at all and `load` answers the fallback.
function storedVolume() {
  const v = parseFloat(load(VOLUME_KEY, ''));
  return isFinite(v) && v >= 0 && v <= 1 ? v : DEFAULT_VOLUME;
}

function storedAlarm() {
  const v = parseFloat(load(ALARM_VOLUME_KEY, ''));
  return isFinite(v) && v >= 0 && v <= 1 ? v : DEFAULT_ALARM;
}

export class GameAudio {
  constructor() {
    this.volume = storedVolume();         // 0..1, the settings menu's master slider
    this.alarmVolume = storedAlarm();     // 0..1, the threat receiver's own one
    this.graph = new Graph(this);
    this.voices = new Voices(this);
    this.field = new SoundField(this);

    // the alarm voices' state between frames (`alarms.js` owns the recipes)
    this.warbles = {};
    this.beeps = {};
    this.beepWave = null;
    this.mode = 'none';                   // the lock tone: 'none' | 'acquiring' | 'locked'
    this.beepTimer = 0;
    this.lockOsc = null;
    this.lockGain = null;
  }

  // 0..1, applied to every voice at once; remembered across reloads
  setVolume(v) {
    this.volume = Math.max(0, Math.min(1, v));
    save(VOLUME_KEY, String(this.volume));
    if (this.graph.master) this.graph.master.gain.value = MASTER_GAIN * this.volume;
  }

  // 0..1, the threat receiver alone - it sits on its own bus inside master
  setAlarmVolume(v) {
    this.alarmVolume = Math.max(0, Math.min(1, v));
    save(ALARM_VOLUME_KEY, String(this.alarmVolume));
    if (this.graph.alarms) this.graph.alarms.gain.value = ALARM_GAIN * this.alarmVolume;
  }

  // The context, built on the first call and never rebuilt: `null` on a browser with
  // no WebAudio, which every cue tests for before it opens a node.
  ensure() {
    return this.graph.ensure();
  }

  resume() {
    this.graph.resume();
  }

  // Every permanent node, every long-lived voice, the alarm clock and the context.
  dispose() {
    this.graph.dispose();
  }

  shot(wid, kind, vol, dist, pan, behind, heavy) {
    cues.shot(this, wid, kind, vol, dist, pan, behind, heavy);
  }

  boom(vol, big, dist, pan, behind) {
    cues.boom(this, vol, big, dist, pan, behind);
  }

  explosion(vol, dist, pan, behind) {
    cues.explosion(this, vol, dist, pan, behind);
  }

  denied() {
    cues.denied(this);
  }

  missileWarning(on) {
    alarms.missileWarning(this, on);
  }

  laserWarning(on) {
    alarms.laserWarning(this, on);
  }

  setLock(state, dt) {
    alarms.setLock(this, state, dt);
  }

  get ctx() {
    return this.graph.ctx;
  }
}

export { SoundField } from './field.js';
