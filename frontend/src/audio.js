// Procedural WebAudio: no asset files, everything is synthesised.

// Threat warning tones, measured off the DCS AN/ALR-67 RWR audio set
// (openflightschool.de, book 806 chapter 895): the *same* pair of tones,
// 426 Hz and 561 Hz, square-ish (odd harmonics only, 3rd at -8 dB), alternated
// back to back with no gap.  What changes between a lock and a launch is only
// the alternation rate: 0.25 s per tone when a sight settles on you ("radar
// uplink"), 0.083 s once a missile is on its way ("missile launch").
const RWR_LO = 426;
const RWR_HI = 561;
const WARBLE_SLOW = 0.25;
const WARBLE_FAST = 0.083;
const UPLINK_VOL = 0.05;            // a lock is a warning, not an emergency
const MISSILE_VOL = 0.06;           // the trill sits a little above it
// Master gain for a full volume slider.  The per-cue mixes above were tuned
// 3x too quiet against the rest of the game, so the whole bus sits at 1.65 at
// the default 50%; the top of the slider is twice that again, and the settings
// menu scales the bus from 0 to MASTER_GAIN.
const MASTER_GAIN = 3.3;
const DEFAULT_VOLUME = 0.5;
const VOLUME_KEY = 'mech.volume';
// The threat receiver has a slider of its own: the RWR is the one voice a
// pilot turns down without turning the battle down with it.  Its own bus
// carries the warble voices only, scaled so the default (50%) is exactly the
// level the mixes above were tuned at.
const ALARM_VOLUME_KEY = 'mech.alarmvolume';
const DEFAULT_ALARM = 0.5;
const ALARM_GAIN = 1 / DEFAULT_ALARM;   // 50% on the slider = the old level
// Reverb send for sound events that arrive from far away: a bang 2 km out is
// mostly the valley answering back, while the same gun next to the cockpit is
// dry.  Fully wet past ~2.5 km, dry inside ~250 m.
const VERB_NEAR = 250;
const VERB_FAR = 2500;
const VERB_SEND = 0.7;
// ...and how much of the distance the wet signal is allowed to lose.  The
// direct sound pays the whole spreading loss, the diffuse field does not: a
// bang 2 km out really is mostly the hills answering back.  Lifting the send by
// `attenuation^(1 - VERB_FALLOFF)` is what turns "far away" into "echo" instead
// of "somebody turned the volume down", which one gain curve can never say.
const VERB_FALLOFF = 0.55;

// speed of sound in air at 20 C, m/s
export const SOUND_SPEED = 343;

// Distance is three physical effects and the ear uses all three.  Geometric
// spreading is the obvious one (-6 dB per doubling), but over a 16 km map a
// true 1/r is 84 dB of dynamic range - everything past ~2 km would be silence,
// which is why every game compresses it.  `SOUND_ROLLOFF` 0.6 keeps the *shape*
// of the near field (a gun at 100 m really is quieter than one at 20 m) and
// still leaves a hull 8 km out audible.  A muzzle is not a point source either,
// so nothing falls off inside `SOUND_REF_DIST`.
//
// `defs.SOUND_*` mirrors these three numbers: the server tests an event against
// the same curve before putting it on the wire.
export const SOUND_REF_DIST = 25;      // m, where the level is 1.0
export const SOUND_ROLLOFF = 0.6;      // 1.0 = inverse square, 0 = no falloff
export const SOUND_FLOOR = 0.02;       // arriving level below this is not heard

// How loud a cue is at SOUND_REF_DIST.  `defs.SOUND_LEVEL` mirrors this table,
// so a bang the server drops as inaudible is exactly one the ear could not have
// heard.
export const SOUND_LEVEL = { shot: 0.6, boom: 0.7, big: 0.8, blast: 1.0 };

// The 105 mm's report is four times the gains a gun cue spends (see `shot`):
// it is the loudest weapon on the field short of a hull going up, and the
// master bus is limited, not clamped, so a cue is scaled up rather than
// clamped flat.  Played against a quiet room the cue as it stands is already
// near the top of what an ear wants - a first pass at 1.5x this (2.04) was
// too loud - so this is the ceiling, not a starting point.
const HEAVY_GAIN = 1.36;

// A 105 mm report is five sounds in a row, and every number in this table came
// off a real one: `plan_imgs/m1fire.mkv`, four rounds at 1:30-1:55, analysed
// per window (the comment above `shot` carries the measured figures).  A report
// is not one louder gunshot - the ear hears the crack, then the bore, and *the
// gap between them* is most of what reads as heavy.
//
// Each row is [dur, f0, f1, q, type, gain, delay, [[t, gain] ...]]: a filtered
// noise burst whose filter sweeps f0 -> f1 over `dur`, `gain` its peak level,
// `delay` when it starts relative to the crack, and the breakpoints its
// envelope follows from there.
//
// Two things this table had wrong, both caught by measuring the result instead
// of trusting the design:
//
//   * it was twice too long.  The recording is still audible at 2.5 s, but a
//     cockpit is not a range: at distance `_verbSend` stretches the tail for us
//     anyway, and the 3.0/4.0 s layers here left the shot hanging over the next
//     two seconds of the fight.  Everything is now inside 2 s.
//   * its two "low" layers were the bright ones.  A bandpass keeps a wide top,
//     so the 900 -> 380 Hz rumble was the loudest thing left after half a
//     second, and the 240 -> 45 Hz tail still passes a couple of kHz.  Both are
//     lowpasses now, and the tail sits *below* the boom instead of beside it.
const HEAVY_REPORT = [
  // the crack off the muzzle brake: a broad snarl at 2.8 kHz, 20 dB down in
  // 100 ms (a highpass here instead left 8-16 kHz ringing for the whole shot)
  [0.45, 2800, 900, 0.6, 'bandpass', 0.34, 0,
   [[0, 1], [0.02, 1], [0.09, 0.4], [0.25, 0.08], [0.45, 0.02]]],
  // the brake's own cough, filling the octaves under the crack
  [0.45, 1200, 420, 0.6, 'bandpass', 0.34, 0,
   [[0, 0.9], [0.04, 1], [0.15, 0.45], [0.35, 0.12], [0.45, 0.02]]],
  // the pressure front: the bore's boom, 110-250 Hz.  It swells in over
  // 120 ms rather than starting at full, which is what keeps the first
  // 50 ms crack-first instead of thud-first
  [2.0, 400, 110, 0.5, 'lowpass', 3.0, 0.03,
   [[0, 0.25], [0.06, 0.9], [0.12, 1], [0.3, 0.78], [0.7, 0.5], [1.1, 0.28],
    [1.6, 0.1], [2.0, 0.02]]],
  // the rumble under it, rolling out slowly
  [1.8, 600, 180, 0.5, 'lowpass', 1.5, 0.02,
   [[0, 0.2], [0.08, 0.9], [0.16, 1], [0.4, 0.7], [0.8, 0.4], [1.2, 0.2],
    [1.8, 0.02]]],
  // and the ground answering back, an octave under everything else
  [2.0, 160, 38, 0.4, 'lowpass', 2.7, 0.05,
   [[0, 0.1], [0.1, 0.8], [0.2, 1], [0.6, 0.5], [1.1, 0.22], [2.0, 0.02]]],
];

export function attenuation(dist) {
  return Math.pow(SOUND_REF_DIST / Math.max(SOUND_REF_DIST, dist), SOUND_ROLLOFF);
}

// Air and ground eat the top end far faster than the bottom (~0.4 dB/km at
// 125 Hz against ~30 dB/km at 4 kHz, ISO 9613-1 at 20 C), so 2 km out the highs
// are simply gone.  That, not the level, is what the ear reads as "far away": a
// distant cannon is a dull thud with no crack left in it.
//
// The curve is two segments, and the floor is why.  A single exponential from
// 18 kHz put a cue 6 km out below 640 Hz, and a gun cue is *made* of a
// 1400-1900 Hz noise burst (the 105 mm's crack sits at 2.8 kHz): the air filter
// was not dulling the far shot, it was deleting it, and no gain curve can bring
// back a band that is no longer there.  So the near field loses its top at half
// the old rate (5.5 kHz at 1 km instead of 10 kHz), the far field keeps falling,
// and everything past ~6.5 km is held at 1.4 kHz - still clearly a muffled
// thud, still something a thump layer underneath can carry.
export function airCutoff(dist) {
  const near = 18000 * Math.exp(-Math.min(dist, 1200) / 2400);
  const far = near * Math.exp(-Math.max(0, dist - 1200) / 2600);
  return Math.min(18000, Math.max(1400, far));
}

// ...and the other half of the same physics: what the air takes off the top has
// to come back under it, or a far bang only ever gets quieter.  The shelf is the
// makeup - it puts the weight back into the octaves the distance lowpass left
// alive, which is exactly what a real one sounds like 5 km off (all rumble, no
// crack).  It is also ear, not just air: 100 Hz needs far more level than 1 kHz
// to be called equally loud, so a flat attenuation curve reads as "gone" long
// before the gain says it should.  Nothing inside BASS_NEAR: a muzzle next to
// the cockpit is bright.
export const BASS_NEAR = 120;          // m, no makeup inside this
const BASS_SLOPE = 9;                  // dB per decade of distance
const BASS_MAX = 18;                   // dB, the ceiling at ~16 km
export const BASS_SHELF_HZ = 180;

export function bassLift(dist) {
  return Math.min(BASS_MAX,
                  BASS_SLOPE * Math.log10(Math.max(1, dist) / BASS_NEAR));
}

// How much of a sound event is the terrain answering back, 0..1.  Distance is
// the only knob: the wavefront of a far shot arrives smeared and raggedy.
export function reverberation(dist) {
  return Math.max(0, Math.min(1, (dist - VERB_NEAR) / (VERB_FAR - VERB_NEAR)));
}

// The wet send's own distance law: 1 next to the cockpit, ~4.7 at 8 km.
export function reverbLift(dist) {
  return Math.min(6, Math.pow(1 / attenuation(dist), 1 - VERB_FALLOFF));
}

// The far-field layer.  A gun 6 km off is not a quiet gun - it is a different
// sound: all body, no crack, the crack having been eaten by the air long before
// it got here.  Every game that does distance well gets this from a *separate
// voice* for the far field rather than by turning the near one down (Arma's
// rifles are crack / bang / distant thud as three samples, DCS gives each
// `sdef` an outer radius with its own curve), and the reason is that a level
// curve cannot re-arrange a spectrum: at -30 dB a gunshot is still a gunshot,
// only inaudible.
//
// So `attenuation` stays what it is for the direct sound, and this layer is
// mixed on its own law - 25^(1/4) instead of 25^(3/5) - which costs 11 dB
// across the whole map instead of 34.  A battle 10 km away therefore sits a
// few dB under one 3 km away rather than 20 dB under, which is the compression
// every sim makes on purpose (a valley really is a quieter place than a
// parade ground, but "inaudible" is a bug, not realism).
export const FAR_LEVEL = 0.13;   // the layer's own level, before the law
const FAR_ROLLOFF = 0.25;        // (25/d)^0.25: 11 dB over the whole map
const FAR_NEAR = 700;            // m, nothing inside this - the near cue is whole
const FAR_FULL = 2600;           // m, fully in past this (faded, squared)

function storedVolume() {
  try {
    const v = parseFloat(localStorage.getItem(VOLUME_KEY));
    if (isFinite(v) && v >= 0 && v <= 1) return v;
  } catch (e) { /* private mode: no storage, keep the default */ }
  return DEFAULT_VOLUME;
}

function storedAlarm() {
  try {
    const v = parseFloat(localStorage.getItem(ALARM_VOLUME_KEY));
    if (isFinite(v) && v >= 0 && v <= 1) return v;
  } catch (e) { }
  return DEFAULT_ALARM;
}

export class GameAudio {
  constructor() {
    this.ctx = null;
    this.noise = null;
    this.master = null;
    this.alarms = null;
    this.verb = null;
    this.lockOsc = null;
    this.lockGain = null;
    this.warbles = {};
    this.beepTimer = 0;
    this.mode = 'none';
    this.volume = storedVolume();   // 0..1, the settings menu's master slider
    this.alarmVolume = storedAlarm();   // 0..1, the threat receiver's own one
  }

  // 0..1, applied to every voice at once; remembered across reloads
  setVolume(v) {
    this.volume = Math.max(0, Math.min(1, v));
    try { localStorage.setItem(VOLUME_KEY, String(this.volume)); } catch (e) { }
    if (this.master) this.master.gain.value = MASTER_GAIN * this.volume;
  }

  // 0..1, the threat receiver alone - it sits on its own bus inside master
  setAlarmVolume(v) {
    this.alarmVolume = Math.max(0, Math.min(1, v));
    try { localStorage.setItem(ALARM_VOLUME_KEY, String(this.alarmVolume)); } catch (e) { }
    if (this.alarms) this.alarms.gain.value = ALARM_GAIN * this.alarmVolume;
  }

  ensure() {
    if (this.ctx) return;
    const AC = window.AudioContext || window.webkitAudioContext;
    if (!AC) return;
    this.ctx = new AC();
    // A limiter between the mix and the speaker: a dead hull plays four layers
    // at once, and the sum is louder than the 0 dB the destination clamps at -
    // clipping that sounds like fizz rather than like weight.  The compressor
    // catches the peaks so the layers can be pushed without it.
    this.limiter = this.ctx.createDynamicsCompressor();
    this.limiter.threshold.value = -8;
    this.limiter.knee.value = 12;
    this.limiter.ratio.value = 12;
    this.limiter.attack.value = 0.004;
    this.limiter.release.value = 0.25;
    this.limiter.connect(this.ctx.destination);
    this.master = this.ctx.createGain();
    this.master.gain.value = MASTER_GAIN * this.volume;
    this.master.connect(this.limiter);
    this.alarms = this.ctx.createGain();
    this.alarms.gain.value = ALARM_GAIN * this.alarmVolume;
    this.alarms.connect(this.master);
    // one shared room for every event that arrives from a distance
    this.verb = this.ctx.createConvolver();
    this.verb.buffer = this._impulse(2.4, 2.8);
    this.verb.connect(this.master);
    const len = this.ctx.sampleRate * 1.2;
    const buf = this.ctx.createBuffer(1, len, this.ctx.sampleRate);
    const d = buf.getChannelData(0);
    for (let i = 0; i < len; i++) d[i] = Math.random() * 2 - 1;
    this.noise = buf;
    // the alarm warbles keep their own clock: the frame loop can stall (tab in
    // the background) without the tone freezing on one pitch or droning on
    this.pump = setInterval(() => this._pump(), 100);
  }

  _pump() {
    if (!this.ctx) return;
    const now = performance.now();
    for (const name in this.warbles) {
      const v = this.warbles[name];
      // nobody has fed this alarm for a second: the game loop is not running,
      // so do not keep sounding
      if (v.req && now - v.stamp > 1000) v.req = false;
      this._warbleUpdate(v);
    }
  }

  resume() {
    if (this.ctx && this.ctx.state === 'suspended') this.ctx.resume();
  }

  // Impulse response for the shared convolver: decaying stereo noise with no
  // discrete early reflections, which is enough to read as "open ground with
  // hills around it" without the metallic ring of a real room.
  _impulse(seconds, decay) {
    const rate = this.ctx.sampleRate;
    const len = Math.max(1, Math.floor(rate * seconds));
    const buf = this.ctx.createBuffer(2, len, rate);
    for (let c = 0; c < 2; c++) {
      const d = buf.getChannelData(c);
      for (let i = 0; i < len; i++) d[i] = (Math.random() * 2 - 1) * Math.pow(1 - i / len, decay);
    }
    return buf;
  }

  // Where a voice enters the mix.  `pan` (-1 hard left .. +1 hard right) is the
  // event's lateral bearing as the wavefront reaches the pilot; `behind` (0..1)
  // is how far behind the cockpit it was, and it is spent as a lowpass - two
  // ears cannot place a bang along the nose-to-tail axis, so rolling off its top
  // end is what reads as "behind you".  All three are set once, when the cue
  // fires: a one-shot is located where it was heard, not tracked as the head
  // turns.  `dist` is the other lowpass - the air itself (see `airCutoff`) - and
  // the two are one filter, because the steeper of the two is the only one that
  // matters.  The bass shelf after it is the makeup for what the air just took
  // off the top (`bassLift`): without it a distant shot is not dull, it is gone.
  _bus(g, pan = 0, behind = 0, dist = 0) {
    let out = g;
    const cut = Math.min(airCutoff(dist),
                         behind > 0.02 ? 12000 - 10000 * Math.min(1, behind) : 1e9);
    if (cut < 17500) {
      const lp = this.ctx.createBiquadFilter();
      lp.type = 'lowpass';
      lp.frequency.value = cut;   // 12k -> 2k behind, 18k -> 1.4 kHz far
      lp.Q.value = 0.4;
      out.connect(lp);
      out = lp;
    }
    const lift = bassLift(dist);
    if (lift > 0.5) {
      const sh = this.ctx.createBiquadFilter();
      sh.type = 'lowshelf';
      sh.frequency.value = BASS_SHELF_HZ;
      sh.gain.value = lift;       // 0 inside 120 m, +18 dB past ~6 km
      out.connect(sh);
      out = sh;
    }
    if (Math.abs(pan) > 0.01 && this.ctx.createStereoPanner) {
      const p = this.ctx.createStereoPanner();
      p.pan.value = Math.max(-1, Math.min(1, pan));
      out.connect(p);
      out = p;
    }
    out.connect(this.master);
  }

  // Send a voice into the shared room by `wet` (0..1).  Knobbed off it is a
  // single extra gain node per voice, which is nothing next to the convolver.
  // The send leaves ahead of `_bus`, so the room stays wide while the direct
  // sound is the part that is placed - which is how a real one behaves.  `dist`
  // lifts it back over the distance the dry sound just lost (`reverbLift`).
  _verbSend(node, wet, dist = 0) {
    if (!(wet > 0) || !this.verb) return;
    const g = this.ctx.createGain();
    g.gain.value = VERB_SEND * wet * reverbLift(dist);
    node.connect(g);
    g.connect(this.verb);
  }

  // `wet` overrides the distance-driven reverb send: a burst that is meant to be
  // heard as a rumble gets its room even from close up.  `delay` starts the
  // whole voice later - the layers of a report are not simultaneous (see
  // `shot`), and one buffer source cannot be offset from another's clock.
  _noiseBurst(dur, freq, q, gain, type, sweepTo, dist = 0, wet = null,
              pan = 0, behind = 0, opts = null) {
    if (!this.ctx) return;
    const t = this.ctx.currentTime + (opts && opts.delay ? opts.delay : 0);
    const src = this.ctx.createBufferSource();
    src.buffer = this.noise;
    src.playbackRate.value = 0.8 + Math.random() * 0.5;
    // the shared noise buffer is 1.2 s long: anything wound out past it (the
    // rumble tail of a report, a hull's burn) has to loop or it stops dead
    src.loop = dur > 1.0;
    const f = this.ctx.createBiquadFilter();
    f.type = type || 'bandpass';
    f.frequency.value = freq;
    f.Q.value = q;
    if (sweepTo) f.frequency.exponentialRampToValueAtTime(sweepTo, t + dur);
    const g = this.ctx.createGain();
    if (opts && opts.env) {
      // a measured decay instead of one exponential ramp: [[t, gain], ...].
      // The breakpoints are *shape*, so the layer's own `gain` still has to be
      // folded in here - dropping it (as this did at first) leaves every layer
      // of the report at unity and the mix exactly the wrong way round: the
      // bright 0.7x crack arriving 12 dB over the 3.4x boom.
      opts.env.forEach(([at, v], i) => {
        const to = Math.max(v * gain, 0.0001);
        if (i === 0) g.gain.setValueAtTime(to, t + at);
        else g.gain.exponentialRampToValueAtTime(to, t + at);
      });
    } else {
      g.gain.setValueAtTime(gain, t);
      g.gain.exponentialRampToValueAtTime(0.0008, t + dur);
    }
    src.connect(f); f.connect(g);
    this._bus(g, pan, behind, dist);
    this._verbSend(g, wet === null ? reverberation(dist) : wet, dist);
    src.start(t, Math.random() * 0.4);
    src.stop(t + dur + 0.05);
  }

  // A sine falling from `freq` to `freq * 0.35` (or to `opts.sweepTo`) for the
  // weight under a bang.  `opts` is the heavy report's shape: `delay` for a
  // thump that arrives behind the crack, `attack` for one that swells rather
  // than slams, `wet` for a boom that is all room.
  _thump(freq, dur, gain, dist = 0, pan = 0, behind = 0, opts = null) {
    if (!this.ctx) return;
    const delay = opts && opts.delay ? opts.delay : 0;
    const attack = opts && opts.attack ? opts.attack : 0;
    const t = this.ctx.currentTime + delay;
    const o = this.ctx.createOscillator();
    o.type = 'sine';
    o.frequency.setValueAtTime(freq, t);
    o.frequency.exponentialRampToValueAtTime(
      opts && opts.sweepTo ? opts.sweepTo : freq * 0.35, t + dur);
    const g = this.ctx.createGain();
    if (attack > 0) {
      // scheduled from zero, or the node's default gain of 1 is already open
      // when the oscillator starts
      g.gain.setValueAtTime(0.0001, t);
      g.gain.exponentialRampToValueAtTime(gain, t + attack);
    } else {
      g.gain.setValueAtTime(gain, t);
    }
    g.gain.exponentialRampToValueAtTime(0.001, t + attack + dur);
    o.connect(g);
    this._bus(g, pan, behind, dist);
    this._verbSend(g, opts && opts.wet !== undefined ? opts.wet
      : reverberation(dist) * 0.6, dist);
    o.start(t); o.stop(t + attack + dur + 0.02);
  }

  // The far-field voice: the same event heard from kilometres away, as a rumble
  // with no crack left in it.  It is mixed on its own law (see `FAR_LEVEL`) and
  // faded in past `FAR_NEAR`, so the near cue is whole where the pilot can see
  // what fired.
  //
  // Both layers are played at `dist` 0 on purpose: the air filter and the bass
  // shelf of `_bus` are for a *near* sample heard through the atmosphere, and
  // this is the already-through-the-atmosphere sample.  Filtering it again
  // would take the edge off the very band it was built to keep.  The reverb
  // send is where the distance goes - `wet` is passed explicitly, and
  // `reverbLift` still lifts it, because a far bang is mostly the valley.
  _farBoom(dist, src, weight, pan, behind, tall = 0.55) {
    if (!this.ctx) return;
    const fade = Math.max(0, Math.min(1, (dist - FAR_NEAR) / (FAR_FULL - FAR_NEAR)));
    const law = Math.pow(SOUND_REF_DIST / Math.max(SOUND_REF_DIST, dist),
                         FAR_ROLLOFF);
    const g = FAR_LEVEL * src * weight * law * fade * fade;
    if (!(g > 0.0015)) return;
    const wet = 0.6 + 0.4 * reverberation(dist);
    // the body: a slow sine falling 95 -> 42 Hz, swelling rather than slamming,
    // because a wavefront that crossed six kilometres has no transient left
    this._thump(95, tall, Math.min(0.9, g), 0, pan, behind,
                { sweepTo: 42, attack: 0.04, wet });
    // the dull edge an octave up, so it reads as a report instead of a hum
    this._noiseBurst(tall * 0.8, 340, 0.8, Math.min(0.7, g * 0.8), 'lowpass',
                     120, 0, wet, pan, behind);
  }

  // `vol` is the source level (`SOUND_LEVEL`) already multiplied by the
  // distance loss, and `dist` is how far the event was from the pilot when the
  // wavefront reached them: between them they set the level, the air's lowpass
  // and the reverb send.  `pan` / `behind` place it: left-right, and how much
  // of it was at the pilot's back (see `_bus`).
  shot(wid, kind, vol = 1, dist = 0, pan = 0, behind = 0, heavy = false) {
    this.ensure();
    if (!this.ctx) return;
    if (heavy) {
      // Measured off a recorded M1 (plan_imgs/m1fire.mkv, four rounds, analysed
      // per window - the table is `HEAVY_REPORT` above):
      //
      //   crack   peaks ~2.5 kHz, 20 ms in, -20 dB inside 100 ms
      //   boom    110-250 Hz, peaks 50-100 ms in, -20 dB only at ~1 s
      //   sub     25-60 Hz, peak ~100 ms in, under the floor
      //   tail    the recording still 10 dB over the silence at 2.5 s
      //
      // The old cue was two layers (a 900 Hz bandpass and a 68 Hz sine) that
      // both slammed at t=0 and were both over in 340 ms: no crack, and the
      // boom in front of the sound it belongs behind.  The gap between crack
      // and bore is most of what reads as heavy, so each layer starts where the
      // recording puts it.  It stops at 2 s rather than the recording's 2.5:
      // at range `_verbSend` adds the room on top, and a cockpit is not a
      // range.  One round per five seconds, so it never has to stack up the way
      // a burst does - and it is meant to be the loudest thing on the field
      // short of a hull going up, hence the gains four times a gun cue's (the
      // master bus is limited, not clamped).
      const wet = reverberation(dist);
      for (const [dur, f0, f1, q, type, gain, delay, env] of HEAVY_REPORT) {
        this._noiseBurst(dur, f0, q, HEAVY_GAIN * vol * gain, type, f1, dist,
                         0.12 + 0.55 * wet, pan, behind, { delay, env });
      }
      // the weight under the floor: 120 Hz falling to 42, swelling in rather
      // than slamming, which is what the recording does at ~100 ms
      this._thump(120, 0.9, HEAVY_GAIN * vol * 0.75, dist, pan, behind,
                  { delay: 0.03, sweepTo: 42, attack: 0.02,
                    wet: 0.25 + 0.55 * wet });
    } else if (kind === 'bullet') {
      this._noiseBurst(0.11, 1400 + Math.random() * 500, 1.2, 0.28 * vol, 'bandpass', 400,
                       dist, null, pan, behind);
      this._thump(120, 0.09, 0.25 * vol, dist, pan, behind);
    } else if (kind === 'rocket' || kind === 'saclos' || kind === 'top') {
      this._noiseBurst(0.75, 700, 0.7, 0.32 * vol, 'lowpass', 220, dist, null, pan, behind);
      this._thump(80, 0.35, 0.2 * vol, dist, pan, behind);
    } else {
      this._noiseBurst(0.08, 1800, 1.5, 0.2 * vol, 'bandpass', 600, dist, null, pan, behind);
    }
    // and, far enough out to have lost its crack on the way, the voice that
    // survived the trip.  A guided launch is a whoosh and carries nothing like
    // as far as a gun, hence the weight.
    const guided = kind === 'rocket' || kind === 'saclos' || kind === 'top';
    this._farBoom(dist, vol / attenuation(dist), heavy ? 1.6 : guided ? 0.4 : 1.0,
                  pan, behind, heavy ? 0.9 : 0.5);
  }

  boom(vol = 1, big = false, dist = 0, pan = 0, behind = 0) {
    this.ensure();
    if (!this.ctx) return;
    this._noiseBurst(big ? 1.1 : 0.5, big ? 420 : 600, 0.8, Math.min(1, 0.5 * vol),
                     'lowpass', 90, dist, null, pan, behind);
    this._thump(big ? 55 : 85, big ? 0.9 : 0.4, Math.min(1, 0.5 * vol), dist, pan, behind);
    this._farBoom(dist, vol / attenuation(dist), big ? 1.3 : 1.0, pan, behind,
                  big ? 1.2 : 0.6);
  }

  // A hull going up (defs.DEATH_BLAST).  Not a louder `boom()` - a dead mech is
  // a different event, so it gets its own voice: a pressure front, a wall of
  // fire that falls away over nearly three seconds, a bass thump under the
  // floor, and a rumble that is still rolling when the fireball has gone out.
  // Four layers timed to overlap, which is what makes it read as heavy.
  explosion(vol = 1, dist = 0, pan = 0, behind = 0) {
    this.ensure();
    if (!this.ctx) return;
    const v = Math.min(1, vol);
    const wet = reverberation(dist);
    // the pressure front: a low-mid shove rather than a gunshot's bright crack
    this._noiseBurst(0.3, 650, 0.5, Math.min(1, 0.55 * v), 'lowpass', 90, dist,
                     0.2 + 0.5 * wet, pan, behind);
    // the fire: a broad wall of noise, low-passed hard and swept down
    this._noiseBurst(2.8, 800, 0.5, Math.min(1, 0.9 * v), 'lowpass', 60, dist,
                     0.35 + 0.65 * wet, pan, behind);
    // the kick under the floor: audible bass before the sine dives below it
    this._thump(58, 2.1, Math.min(1, 0.95 * v), dist, pan, behind);
    // the tail: the valley answering back long after the fire is out
    this._noiseBurst(4.5, 240, 0.35, Math.min(1, 0.5 * v), 'lowpass', 40, dist,
                     0.55 + 0.45 * wet, pan, behind);
    // and the far-field version of the same event: a hull going up is the
    // loudest thing on the field, so it carries further than anything else and
    // rumbles for longer when it gets there
    this._farBoom(dist, vol / attenuation(dist), 1.2, pan, behind, 1.8);
  }

  denied() {
    this.ensure();
    if (!this.ctx) return;
    const t = this.ctx.currentTime;
    const o = this.ctx.createOscillator();
    o.type = 'square';
    o.frequency.setValueAtTime(320, t);
    o.frequency.exponentialRampToValueAtTime(180, t + 0.16);
    const g = this.ctx.createGain();
    g.gain.setValueAtTime(0.06, t);
    g.gain.exponentialRampToValueAtTime(0.001, t + 0.18);
    o.connect(g); g.connect(this.master);
    o.start(t); o.stop(t + 0.19);
  }

  // one long-lived voice per alarm: a square oscillator (odd harmonics, like
  // the real RWR tone) through a soft lowpass so it buzzes without the raw
  // square's fizz on top.  The tone steps are scheduled a little ahead of the
  // audio clock, so a stuttering frame cannot smear the rhythm.
  _warble(name, toneDur) {
    let v = this.warbles[name];
    if (!v) {
      const o = this.ctx.createOscillator();
      o.type = 'square';
      const lp = this.ctx.createBiquadFilter();
      lp.type = 'lowpass';
      lp.frequency.value = 1900;
      lp.Q.value = 0.6;
      const g = this.ctx.createGain();
      g.gain.value = 0;
      o.connect(lp); lp.connect(g); g.connect(this.alarms);
      o.frequency.value = RWR_HI;
      o.start();
      v = this.warbles[name] = { o, g, toneDur, next: 0, hi: false, on: false,
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
  _warbleTick(v) {
    if (!v.on) { v.next = 0; return; }
    const now = this.ctx.currentTime;
    if (v.next < now) { v.next = now; v.hi = false; }
    while (v.next < now + 0.3) {
      v.hi = !v.hi;
      v.o.frequency.setValueAtTime(v.hi ? RWR_HI : RWR_LO, v.next);
      v.next += v.toneDur;
    }
  }

  // intent from the game loop: `on` now, but stay sounding through `hold`
  // seconds of silence so a threat that blinks off for a frame cannot chatter
  _warbleSet(v, on, vol, hold = 0) {
    const now = this.ctx.currentTime;
    if (on && hold) v.hold = Math.max(v.hold, now + hold);
    v.req = !!on;
    v.want = vol;
    v.stamp = performance.now();
    this._warbleUpdate(v);
  }

  _warbleUpdate(v) {
    const now = this.ctx.currentTime;
    const on = v.req || v.hold > now;
    if (on !== v.on || Math.abs(v.vol - v.want) > 0.006) {
      v.on = on;
      v.vol = v.want;
      v.g.gain.cancelScheduledValues(now);
      v.g.gain.setTargetAtTime(on ? v.vol : 0, now, on ? 0.012 : 0.07);
    }
    this._warbleTick(v);
  }

  // self-defence radar: a missile is inbound.  One steady trill for as long as
  // the threat holds - no rate or volume changes, the circle already says how
  // close it is.
  missileWarning(active) {
    this.ensure();
    if (!this.ctx) return;
    const v = this._warble('missile', WARBLE_FAST);
    this._warbleSet(v, !!active, MISSILE_VOL, 0.3);
  }

  // laser warning: somebody's sight is on the cockpit.  Same tone pair, four
  // times slower - unmistakable next to the missile warble.  Both alarms ride
  // the ALARM bus, so they have a slider of their own in the settings menu.
  laserWarning(active) {
    this.ensure();
    if (!this.ctx) return;
    const v = this._warble('uplink', WARBLE_SLOW);
    this._warbleSet(v, !!active, UPLINK_VOL, 0.45);
  }

  setLock(state, dt) {
    this.ensure();
    if (!this.ctx) return;
    if (state !== this.mode) {
      this.mode = state;
      this.beepTimer = 0;
      if (state !== 'locked' && this.lockOsc) {
        try { this.lockOsc.stop(); } catch (e) { }
        this.lockOsc = null;
        this.lockGain = null;
      }
      if (state === 'locked' && !this.lockOsc) {
        const o = this.ctx.createOscillator();
        o.type = 'sine';
        o.frequency.value = 1500;
        const g = this.ctx.createGain();
        g.gain.value = 0.055;
        o.connect(g); g.connect(this.master);
        o.start();
        this.lockOsc = o;
        this.lockGain = g;
      }
    }
    if (state === 'acquiring') {
      this.beepTimer -= dt;
      if (this.beepTimer <= 0) {
        this.beepTimer = 0.24;
        const t = this.ctx.currentTime;
        const o = this.ctx.createOscillator();
        o.type = 'square';
        o.frequency.value = 950;
        const g = this.ctx.createGain();
        g.gain.setValueAtTime(0.05, t);
        g.gain.exponentialRampToValueAtTime(0.001, t + 0.09);
        o.connect(g); g.connect(this.master);
        o.start(t); o.stop(t + 0.1);
      }
    }
  }
}
