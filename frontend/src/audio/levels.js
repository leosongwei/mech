// The ear's numbers, and the three laws that turn a distance into a sound: how
// loud (attenuation), how dull (airCutoff) and how much of it is the valley
// answering back (reverberation, reverbLift).
//
// This module is arithmetic and nothing else - no WebAudio, no DOM, no imports -
// because it has readers that are not the audio engine: `hud/` blinks the threat
// dial's rim on `RWR_TONE_S`, `app/systems/` reads the level table and the alarm
// ring is counted against the same tone.  A view module importing the engine to
// get one constant is the edge this file removes, and every tuning note lives
// next to the number it justifies so that the number can be argued with.

// Threat warning tones, measured off the DCS AN/ALR-67 RWR audio set
// (openflightschool.de, book 806 chapter 895): a *same* pair of tones, 426 Hz
// and 561 Hz, square-ish (odd harmonics only, 3rd at -8 dB), alternated back to
// back with no gap.  A missile on its way is a trill of them, 0.083 s a tone
// ("missile launch").
//
// The pair and its timbre live in `alarms.js`, with the oscillator that plays
// them.  The period lives here, because it is the rate every other thing that
// counts that beat reads: the dial's rim blinks once per tone.
export const RWR_TONE_S = 0.083;

// Master gain for a full volume slider.  The per-cue mixes were tuned 3x too
// quiet against the rest of the game, so the whole bus sits at 1.65 at the
// default 50%; the top of the slider is twice that again, and the settings menu
// scales the bus from 0 to MASTER_GAIN.
export const MASTER_GAIN = 3.3;
export const DEFAULT_VOLUME = 0.5;

// The threat receiver has a slider of its own: the RWR is the one voice a pilot
// turns down without turning the battle down with it.  Its own bus carries the
// warble voices only, scaled so the default (50%) is exactly the level the mixes
// were tuned at.
export const DEFAULT_ALARM = 0.5;
export const ALARM_GAIN = 1 / DEFAULT_ALARM;   // 50% on the slider = the old level

// Reverb send for sound events that arrive from far away: a bang 2 km out is
// mostly the valley answering back, while the same gun next to the cockpit is
// dry.  Fully wet past ~2.5 km, dry inside ~250 m.
const VERB_NEAR = 250;
const VERB_FAR = 2500;
export const VERB_SEND = 0.7;
// ...and how much of the distance the wet signal is allowed to lose.  The direct
// sound pays the whole spreading loss, the diffuse field does not: a bang 2 km
// out really is mostly the hills answering back.  Lifting the send by
// `attenuation^(1 - VERB_FALLOFF)` is what turns "far away" into "echo" instead
// of "somebody turned the volume down", which one gain curve can never say.
const VERB_FALLOFF = 0.55;

// speed of sound in air at 20 C, m/s
export const SOUND_SPEED = 343;

// Distance is three physical effects and the ear uses all three.  Geometric
// spreading is the obvious one (-6 dB per doubling), but over a 16 km map a true
// 1/r is 84 dB of dynamic range - everything past ~2 km would be silence, which
// is why every game compresses it.  `SOUND_ROLLOFF` 0.6 keeps the *shape* of the
// near field (a gun at 100 m really is quieter than one at 20 m) and still
// leaves a hull 8 km out audible.  A muzzle is not a point source either, so
// nothing falls off inside `SOUND_REF_DIST`.
//
// `defs.SOUND_*` mirrors these three numbers: the server tests an event against
// the same curve before putting it on the wire.
export const SOUND_REF_DIST = 25;      // m, where the level is 1.0
export const SOUND_ROLLOFF = 0.6;      // 1.0 = inverse square, 0 = no falloff
export const SOUND_FLOOR = 0.02;       // arriving level below this is not heard

// How loud a cue is at SOUND_REF_DIST.  `defs.SOUND_LEVEL` mirrors this table, so
// a bang the server drops as inaudible is exactly one the ear could not have
// heard.
export const SOUND_LEVEL = { shot: 0.6, boom: 0.7, big: 0.8, blast: 1.0 };

// The 105 mm's report is four times the gains a gun cue spends (see
// `cues.shot`): it is the loudest weapon on the field short of a hull going up,
// and the master bus is limited, not clamped, so a cue is scaled up rather than
// clamped flat.  Played against a quiet room the cue as it stands is already near
// the top of what an ear wants - a first pass at 1.5x this (2.04) was too loud -
// so this is the ceiling, not a starting point.
export const HEAVY_GAIN = 1.36;

// A 105 mm report is five sounds in a row, and every number in this table came
// off a real one: `plan_imgs/m1fire.mkv`, four rounds at 1:30-1:55, analysed per
// window (the comment above `cues.shot` carries the measured figures).  A report
// is not one louder gunshot - the ear hears the crack, then the bore, and *the
// gap between them* is most of what reads as heavy.
//
// Each row is [dur, f0, f1, q, type, gain, delay, [[t, gain] ...]]: a filtered
// noise burst whose filter sweeps f0 -> f1 over `dur`, `gain` its peak level,
// `delay` when it starts relative to the crack, and the breakpoints its envelope
// follows from there.
//
// Two things this table had wrong, both caught by measuring the result instead
// of trusting the design:
//
//   * it was twice too long.  The recording is still audible at 2.5 s, but a
//     cockpit is not a range: at distance the reverb send stretches the tail for
//     us anyway, and the 3.0/4.0 s layers here left the shot hanging over the
//     next two seconds of the fight.  Everything is now inside 2 s.
//   * its two "low" layers were the bright ones.  A bandpass keeps a wide top, so
//     the 900 -> 380 Hz rumble was the loudest thing left after half a second,
//     and the 240 -> 45 Hz tail still passes a couple of kHz.  Both are lowpasses
//     now, and the tail sits *below* the boom instead of beside it.
export const HEAVY_REPORT = [
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

// Air and ground eat the top end far faster than the bottom (~0.4 dB/km at 125 Hz
// against ~30 dB/km at 4 kHz, ISO 9613-1 at 20 C), so 2 km out the highs are
// simply gone.  That, not the level, is what the ear reads as "far away": a
// distant cannon is a dull thud with no crack left in it.
//
// The curve is two segments, and the floor is why.  A single exponential from
// 18 kHz put a cue 6 km out below 640 Hz, and a gun cue is *made* of a 1400-1900
// Hz noise burst (the 105 mm's crack sits at 2.8 kHz): the air filter was not
// dulling the far shot, it was deleting it, and no gain curve can bring back a
// band that is no longer there.  So the near field loses its top at half the old
// rate (5.5 kHz at 1 km instead of 10 kHz), the far field keeps falling, and
// everything past ~6.5 km is held at 1.4 kHz - still clearly a muffled thud,
// still something a thump layer underneath can carry.
export function airCutoff(dist) {
  const near = 18000 * Math.exp(-Math.min(dist, 1200) / 2400);
  const far = near * Math.exp(-Math.max(0, dist - 1200) / 2600);
  return Math.min(18000, Math.max(1400, far));
}

// ...and the other half of the same physics: what the air takes off the top has
// to come back under it, or a far bang only ever gets quieter.  It is also ear,
// not just air: 100 Hz needs far more level than 1 kHz to be called equally
// loud, so a flat attenuation curve reads as "gone" long before the gain says it
// should.
//
// This used to be a 180 Hz lowshelf *per voice* in the bus, up to +18 dB past
// 6 km.  It is gone, and the far layer below is why: measured, the shelf bought a
// solo 5 km shot nothing at all (peak 0.117 with it, 0.117 without, because that
// shot is the far voice already), while in a battle it summed over ninety events
// a second to +4 dB of bed - and, being one biquad on every voice, it cost more
// DSP than everything else in the mix put together (measured: 1.30 of realtime
// with it, 0.84 without).  Whatever is done about the low end of a distant
// report has to happen on a bus that is compressed as one, not on every event
// separately.

// How much of a sound event is the terrain answering back, 0..1.  Distance is the
// only knob: the wavefront of a far shot arrives smeared and raggedy.
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
// rifles are crack / bang / distant thud as three samples, DCS gives each `sdef`
// an outer radius with its own curve), and the reason is that a level curve
// cannot re-arrange a spectrum: at -30 dB a gunshot is still a gunshot, only
// inaudible.
//
// So `attenuation` stays what it is for the direct sound, and this layer is mixed
// on its own law - 25^(1/4) instead of 25^(3/5) - which costs 11 dB across the
// whole map instead of 34.  A battle 10 km away therefore sits a few dB under one
// 3 km away rather than 20 dB under, which is the compression every sim makes on
// purpose (a valley really is a quieter place than a parade ground, but
// "inaudible" is a bug, not realism).
export const FAR_LEVEL = 0.13;   // the layer's own level, before the law
export const FAR_ROLLOFF = 0.25; // (25/d)^0.25: 11 dB over the whole map
export const FAR_NEAR = 700;     // m, nothing inside this - the near cue is whole
export const FAR_FULL = 2600;    // m, fully in past this (faded, squared)
// ...and the two things that keep it a *bed* instead of a wall.  A battle is
// ninety sound events a second, and every one of them is kilometres away: mixed
// per event the layer has no ceiling at all, so it summed to +9 dB over the mix
// it was added to (measured, a scripted 90 event/s fight) and buried the cues
// that matter.  So the far voices all land on one bus with a compressor on it -
// `FAR_CEIL` is where that bed settles however many events are in it - and a
// rate limit stops a burst of them from opening a voice each.  A far rumble is
// not a thing the ear counts: twenty a second is already a battle.
export const FAR_CEIL = -20;     // dBFS, where the far bed settles
export const FAR_MIN_GAP = 0.05; // s, at most one far voice per this

// A ceiling on how many voices the mix may hold at once, whatever is firing and
// whoever is firing it.  A live client measured 350 sound calls a second, and
// every one of them opens a chain of six or more nodes: a machine that cannot
// keep up does not merely sound thin, the audio thread falls behind its own clock
// and the tab goes quiet.  So a voice holds a slot for as long as it plays - a
// burst of short cracks fits, a dozen two second rumbles at once does not - and
// past the ceiling a quiet cue is dropped while a loud one still gets in, because
// trading the 105 mm beside the pilot for a machine gun three kilometres out is
// the wrong bargain.  `voices.js` is the policy.
export const MAX_VOICES = 48;
export const STEAL_VOL = 0.05;   // -26 dB, this loud plays even when the mix is full

// The master bus ends in a soft clipper, and this is the headroom it gets.  A
// DynamicsCompressor is not a limiter: at 4 ms attack it hands a transient its
// first few milliseconds at full gain, so a 105 mm round next to the pilot
// measured 1.17 at the destination - over 0 dBFS, i.e. hard-clipped by the
// browser, which is the fizzy crackle that sounds like a broken speaker rather
// than a big gun.  Keeping the sum under CLIP_GAIN and rounding the last
// `CLIP_KNEE` of the scale makes that impossible whatever the mix does.
//
// The figure is measured, not chosen: the compressor does its own makeup gain
// (this pair of settings lifts everything +1.3 dB on its own), so the trim is
// whatever puts the chain back at unity - a cue next to the cockpit has to be
// exactly as loud as it was, the whole mix was tuned by ear against it.  A
// constant cue reads 0.094 at the destination before and after.
export const CLIP_GAIN = 0.86;   // unity through the limiter, then the shaper
export const CLIP_KNEE = 0.7;    // the linear part of the curve

// The soft-clip curve: linear up to CLIP_KNEE, then a tanh bend that reaches 1
// without a corner.  A hard 0 dBFS clamp is broadband fizz on every transient;
// this is a rounding a decibel and a half wide, which no ear hears as anything
// but the peak it already was.  `graph.js` hands the result to the shaper.
export function clipCurve() {
  const n = 4096;
  const curve = new Float32Array(n);
  for (let i = 0; i < n; i++) {
    const x = (i / (n - 1)) * 2 - 1;
    const a = Math.abs(x);
    const y = a <= CLIP_KNEE
      ? a : CLIP_KNEE + (1 - CLIP_KNEE) * Math.tanh((a - CLIP_KNEE) / (1 - CLIP_KNEE));
    curve[i] = Math.sign(x) * Math.min(1, y);
  }
  return curve;
}
