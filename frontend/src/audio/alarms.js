// The long-lived machine voices: the missile trill, the laser warning's beep
// train, and the IR lock tone.  These are not events - they sound for as long as a
// threat holds - so each is one oscillator chain opened once and then gated, and
// their rhythm is scheduled ahead of the audio clock rather than driven by the
// frame, so a stuttering frame cannot smear it.
//
// The state a voice keeps between calls lives on the facade (`a.warbles`,
// `a.beeps`, `a.beepWave`, `a.mode`, `a.beepTimer`, `a.lockOsc`, `a.lockGain`),
// because the recipes here are functions over it: a cue is a shape, and what
// survives between frames belongs to the object the app holds.

import { RWR_TONE_S } from './levels.js';
import { blip } from './synth.js';

// The two tones, measured off the DCS AN/ALR-67 RWR audio set (openflightschool.de,
// book 806 chapter 895): 426 Hz and 561 Hz, square-ish (odd harmonics only, 3rd at
// -8 dB), alternated back to back with no gap.  A missile on its way is a trill of
// them, one tone per `RWR_TONE_S`.
const RWR_LO = 426;
const RWR_HI = 561;

// The laser warning is its own cue, and a small one: six short beeps around 960 Hz
// - the pitch and the rhythm measured off the warning tone in *Mobile Suit Gundam:
// Hathaway*'s action cut - then a rest, cycled.  A ground battle is dense with
// things a pilot has to hear, and the old RWR lock warble held the whole ear for as
// long as it lasted; this says "somebody's sight is on you" and gives the ear back.
const BEEP_HZ = 960;
// ...and its timbre, measured off the source tone: the partials of a 960 Hz
// fundamental, in dB below the first.  It is a stacked, buzzy thing rather than a
// test tone, and two of them a few Hz apart are what give it the beat the
// recording has.
const BEEP_PARTIALS = [0, -13.4, -17.4, -14.3, -31.9, -17.2, -49.7, -33.7,
  -34.4, -51.2];
const BEEP_DETUNE = 7;              // Hz, the second oscillator
const BEEP_ON = 0.09;               // one "duh"
const BEEP_GAP = 0.06;              // the silence inside the train
const BEEP_REST = 0.7;              // the rest after the sixth
const BEEP_COUNT = 6;
const LASER_HOLD = 0.45;            // s, the sight can blink off for this long
const UPLINK_VOL = 0.02;            // a lock is a warning, not an emergency: a
                                    // third of the missile trill's level
const MISSILE_VOL = 0.06;           // the trill sits well above it
const ALARM_STALE_MS = 1000;        // nobody fed this alarm for a second
const LOCK_HZ = 1500;
const LOCK_VOL = 0.055;
const PIP_HZ = 950;                 // the acquiring pip
const PIP_PERIOD = 0.24;

// One long-lived voice per alarm: a square oscillator (odd harmonics, like the real
// RWR tone) through a soft lowpass so it buzzes without the raw square's fizz on
// top.
function warble(a, name, toneDur) {
  let v = a.warbles[name];
  if (!v) {
    const o = a.ctx.createOscillator();
    o.type = 'square';
    const lp = a.ctx.createBiquadFilter();
    lp.type = 'lowpass';
    lp.frequency.value = 1900;
    lp.Q.value = 0.6;
    const g = a.ctx.createGain();
    g.gain.value = 0;
    o.connect(lp); lp.connect(g); g.connect(a.graph.alarms);
    o.frequency.value = RWR_HI;
    o.start();
    v = a.warbles[name] = { o, lp, g, toneDur, next: 0, hi: false, on: false,
                            vol: 0, want: 0, hold: 0, req: false, stamp: 0 };
  }
  if (v.toneDur !== toneDur) {
    v.toneDur = toneDur;
    v.next = 0;
    v.hi = false;
  }
  return v;
}

// fill the frequency timeline up to ~0.3 s ahead while the voice is on
function warbleTick(a, v) {
  if (!v.on) { v.next = 0; return; }
  const now = a.ctx.currentTime;
  if (v.next < now) { v.next = now; v.hi = false; }
  while (v.next < now + 0.3) {
    v.hi = !v.hi;
    v.o.frequency.setValueAtTime(v.hi ? RWR_HI : RWR_LO, v.next);
    v.next += v.toneDur;
  }
}

function warbleUpdate(a, v) {
  const now = a.ctx.currentTime;
  const on = v.req || v.hold > now;
  if (on !== v.on || Math.abs(v.vol - v.want) > 0.006) {
    v.on = on;
    v.vol = v.want;
    v.g.gain.cancelScheduledValues(now);
    v.g.gain.setTargetAtTime(on ? v.vol : 0, now, on ? 0.012 : 0.07);
  }
  warbleTick(a, v);
}

// intent from the game loop: `on` now, but stay sounding through `hold` seconds of
// silence so a threat that blinks off for a frame cannot chatter
function warbleSet(a, v, on, vol, hold = 0) {
  const now = a.ctx.currentTime;
  if (on && hold) v.hold = Math.max(v.hold, now + hold);
  v.req = !!on;
  v.want = vol;
  v.stamp = performance.now();
  warbleUpdate(a, v);
}

// self-defence radar: a missile is inbound.  One steady trill for as long as the
// threat holds - no rate or volume changes, the circle already says how close it
// is.
export function missileWarning(a, active) {
  a.ensure();
  if (!a.ctx) return;
  const v = warble(a, 'missile', RWR_TONE_S);
  warbleSet(a, v, !!active, MISSILE_VOL, 0.3);
}

function beep(a) {
  let v = a.beeps.uplink;
  if (!v) {
    const g = a.ctx.createGain();
    g.gain.value = 0;
    // a soft top on the stack: the partials reach 11 kHz in the recording, which is
    // fizz next to a battle's low end
    const lp = a.ctx.createBiquadFilter();
    lp.type = 'lowpass';
    lp.frequency.value = 5200;
    lp.Q.value = 0.5;
    lp.connect(g); g.connect(a.graph.alarms);
    // the measured partials at BEEP_HZ, and a second oscillator a few Hz above it
    // for the beat that makes the pair sound stacked rather than tuned
    const w = beepWave(a);
    const oscs = [0, BEEP_DETUNE].map((det) => {
      const o = a.ctx.createOscillator();
      o.setPeriodicWave(w);
      o.frequency.value = BEEP_HZ + det;
      o.connect(lp);
      o.start();
      return o;
    });
    v = a.beeps.uplink = { oscs, lp, g, next: 0, i: 0, on: false, hold: 0 };
  }
  return v;
}

function beepWave(a) {
  if (a.beepWave) return a.beepWave;
  const real = new Float32Array(BEEP_PARTIALS.length + 1);
  const imag = new Float32Array(BEEP_PARTIALS.length + 1);
  BEEP_PARTIALS.forEach((db, i) => { imag[i + 1] = Math.pow(10, db / 20); });
  a.beepWave = a.ctx.createPeriodicWave(real, imag);
  return a.beepWave;
}

// fill the train up to ~0.3 s ahead: six gated beeps off the audio clock, so a
// stuttering frame cannot smear the rhythm
function beepTick(a, v) {
  const now = a.ctx.currentTime;
  if (v.next < now) { v.next = now; v.i = 0; }
  while (v.next < now + 0.3) {
    const start = v.next;
    const end = start + BEEP_ON;
    v.g.gain.setValueAtTime(0, start);
    v.g.gain.linearRampToValueAtTime(UPLINK_VOL, start + 0.006);
    v.g.gain.setValueAtTime(UPLINK_VOL, end - 0.01);
    v.g.gain.linearRampToValueAtTime(0, end);
    v.next = end + (v.i === BEEP_COUNT - 1 ? BEEP_REST : BEEP_GAP);
    v.i = (v.i + 1) % BEEP_COUNT;
  }
}

// laser warning: somebody's sight is on the cockpit.  A train of six beeps, then a
// rest, then six again - one voice however many designators are on you, because
// three overlapping alarms say nothing the first one did not.  Rides the ALARM bus
// with the missile trill, so it has the same slider.
export function laserWarning(a, active) {
  a.ensure();
  if (!a.ctx) return;
  const v = beep(a);
  const now = a.ctx.currentTime;
  if (active) v.hold = Math.max(v.hold, now + LASER_HOLD);
  if (!active && v.hold <= now) {
    if (v.on) {
      v.on = false;
      v.g.gain.cancelScheduledValues(now);
      v.g.gain.setTargetAtTime(0, now, 0.02);
    }
    return;
  }
  if (!v.on) { v.on = true; v.next = 0; v.i = 0; }
  beepTick(a, v);
}

// The alarm clock, on a 100 ms interval of its own (`graph.js` owns it): the frame
// loop can stall - a tab in the background - without the tone freezing on one pitch
// or droning on.
export function pump(a) {
  if (!a.ctx) return;
  const now = performance.now();
  for (const name in a.warbles) {
    const v = a.warbles[name];
    // nobody has fed this alarm for a second: the game loop is not running, so do
    // not keep sounding
    if (v.req && now - v.stamp > ALARM_STALE_MS) v.req = false;
    warbleUpdate(a, v);
  }
}

// The IR seeker's tone: a steady 1500 Hz sine while a lock holds, and a pip every
// `PIP_PERIOD` while one is settling.  Both are the machine's own voice - centred,
// undamped by distance, no room - so they land on the master bus.
export function setLock(a, state, dt) {
  a.ensure();
  if (!a.ctx) return;
  if (state !== a.mode) {
    a.mode = state;
    a.beepTimer = 0;
    if (state !== 'locked' && a.lockOsc) {
      try { a.lockOsc.stop(); } catch (e) { }
      a.lockOsc = null;
      a.lockGain = null;
    }
    if (state === 'locked' && !a.lockOsc) {
      const o = a.ctx.createOscillator();
      o.type = 'sine';
      o.frequency.value = LOCK_HZ;
      const g = a.ctx.createGain();
      g.gain.value = LOCK_VOL;
      o.connect(g); g.connect(a.graph.master);
      // the tone is stopped when the lock goes, and this is what lets go of the two
      // nodes it was singing through
      o.onended = () => { o.disconnect(); g.disconnect(); };
      o.start();
      a.lockOsc = o;
      a.lockGain = g;
    }
  }
  if (state === 'acquiring') {
    a.beepTimer -= dt;
    if (a.beepTimer <= 0) {
      a.beepTimer = PIP_PERIOD;
      blip(a, { type: 'square', f0: PIP_HZ, gain: 0.05, decay: 0.09, dur: 0.1 });
    }
  }
}

// Every long-lived voice this file opened, stopped and disconnected: `graph.dispose`
// closes the context, and a node still scheduled on a closed context throws on the
// next touch.
export function stopVoices(a) {
  for (const name in a.warbles) {
    const v = a.warbles[name];
    try { v.o.stop(); } catch (e) { }
    v.o.disconnect();
    v.lp.disconnect();
    v.g.disconnect();
    delete a.warbles[name];
  }
  for (const name in a.beeps) {
    const v = a.beeps[name];
    for (const o of v.oscs) {
      try { o.stop(); } catch (e) { }
      o.disconnect();
    }
    v.lp.disconnect();
    v.g.disconnect();
    delete a.beeps[name];
  }
  if (a.lockOsc) {
    try { a.lockOsc.stop(); } catch (e) { }
    if (a.lockGain) a.lockGain.disconnect();
    a.lockOsc = null;
    a.lockGain = null;
  }
  a.mode = 'none';
  a.beepTimer = 0;
  // the wave belongs to the context that made it, and the next `ensure()` builds a
  // new one: keeping it would hand a closed context's wave to a fresh oscillator
  a.beepWave = null;
}
