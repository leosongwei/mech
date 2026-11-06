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
// one tone of the missile trill, and the rate every other cue that counts that
// beat reads: the arrow ring's rim blinks on the same clock
export const RWR_TONE_S = 0.083;
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
// spreading is the obvious one: a point source in a free field loses 6 dB per
// doubling, `p` falling as 1/r, and from 1 m to the far corner of this map that
// is 84 dB.  That is the law here (`SOUND_ROLLOFF` 1.0, the free field), and the
// mix has about 50 dB of usable range, so the two are reconciled at
// `SOUND_FLOOR`: the arriving gain the ear is allowed to miss below, 0.002, i.e.
// a 30 mm report at 7.5 km and a hull going up at 12.5 km.  The wire is gated on
// that same number (`defs.SOUND_FLOOR`), so an event a pilot could not hear is
// the event the server leaves off his frame.  A muzzle is not a point source
// either, so nothing falls off inside `SOUND_REF_DIST`.
//
// `defs.SOUND_*` mirrors these three numbers: the server tests an event against
// the same curve before putting it on the wire.
export const SOUND_REF_DIST = 25;      // m, where the level is 1.0
export const SOUND_ROLLOFF = 1.0;      // 1.0 = inverse square, the free field
export const SOUND_FLOOR = 0.002;      // arriving level below this is not heard

// A cue quieter than this plays as the far voice and nothing else.  The near
// layers are a dozen nodes apiece (a 105 mm is five bursts and a thump), and a
// battle six kilometres off is hundreds of them a second - measured on a live
// client, 89 % of the voices it played were further than 4 km, i.e. the
// quietest ones and the ones that cost the most.  At 4 km a 30 mm report
// arrives at 0.6 * 25/4000 = 0.0038, a -48 dB cue the mix can barely carry,
// and the air has taken most of its crack off anyway; past this line the sound
// that survives is `_farBoom`, which is rate limited to one voice per 50 ms.
export const NEAR_FLOOR = 0.004;

// A ceiling on how many voices the mix may hold at once, whatever is firing and
// whoever is firing it.  The battlefield is the reason: a live client measured
// 350 sound calls a second with 89 % of them past 4 km, and every one of those
// opens a chain of six or more nodes.  A machine that cannot keep up does not
// merely sound thin - the audio thread falls behind its own clock, and the tab
// goes quiet.  So a voice holds a slot for as long as it plays: a burst of forty
// short events a second fits, forty sustained ones do not.  Past the ceiling a
// quiet cue is dropped and a loud one still gets in, because trading the 105 mm
// beside the pilot for a machine gun three kilometres out is the wrong bargain.
const MAX_VOICES = 48;
const STEAL_VOL = 0.05;   // -26 dB, this loud plays even when the mix is full

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
// The curve is the absorption model straight: `alpha(f)*d = 6 dB`, with `alpha`
// ~ f^1.26, puts the -6 dB point at 1.06 kHz at 1 km, 450 Hz at 3 km and 262 Hz
// at 6 km.  `_bus` spends a second corner on it past ~270 m, which is what turns
// 12 dB/octave into the 30-40 dB the model asks for; 1 km lands at 1.06 kHz,
// 3 km at 451.
//
// The near field is a *window* over that: the corner stays open (18 kHz, and
// `_bus` skips the node) inside ~160 m and closes to the model over the next
// 70 m, so 200 m is still the recording and 250 m is already the air.  The
// window used to close over 220 m and did not reach the model until ~450, which
// left 200-1000 m half open - measured 300 m out, 3 kHz was not filtered at all,
// and that band is exactly the crack: the one range an ear calls "crisp".
//
// Anything shallower and the far field keeps its crack: measured on the first
// pass (5.5 kHz at 3 km, one corner) a boom 3 km out still put 12 % of its
// energy above 2.4 kHz, and what an ear reads as a bang at that range is body -
// 200-500 Hz, the same band `boom` is built around.
export function airCutoff(dist) {
  const km = Math.max(0.03, dist / 1000);
  const f6 = 125 * Math.pow(15 / km, 0.794);
  const window = 18000 * Math.exp(-Math.max(0, dist - 160) / 55);
  return Math.max(260, Math.min(18000, Math.max(f6, window)));
}

// ...and the other half of the same physics: what the air takes off the top has
// to come back under it, or a far bang only ever gets quieter.  It is also ear,
// not just air: 100 Hz needs far more level than 1 kHz to be called equally
// loud, so a flat attenuation curve reads as "gone" long before the gain says
// it should.
//
// This used to be a 180 Hz lowshelf *per voice* in `_bus`, up to +18 dB past
// 6 km.  It is gone, and the far layer below is why: measured, the shelf bought
// a solo 5 km shot nothing at all (peak 0.117 with it, 0.117 without, because
// that shot is the far voice already), while in a battle it summed over ninety
// events a second to +4 dB of bed - and, being one biquad on every voice, it
// cost more DSP than everything else in the mix put together (measured: 1.30 of
// realtime with it, 0.84 without).  Whatever is done about the low end of a
// distant report has to happen on a bus that is compressed as one, not on every
// event separately.

// How much of a sound event is the terrain answering back, 0..1.  Distance is
// the only knob: the wavefront of a far shot arrives smeared and raggedy.
export function reverberation(dist) {
  return Math.max(0, Math.min(1, (dist - VERB_NEAR) / (VERB_FAR - VERB_NEAR)));
}

// The wet send's own distance law: 1 next to the cockpit, where the spreading
// loss is still small, and capped at 3 from ~500 m out.  The cap is what keeps
// a distant bang from arriving as nothing but its own reverb: the direct sound
// keeps falling on 1/r while the room stops gaining, so the further out the
// event is, the more of it is the field answering and the less of it is level.
// It sat at 6, which with `VERB_SEND` 0.7 made the room twice the dry signal
// from 1.3 km out - an explosion heard as a long bright wash of its own echo.
export function reverbLift(dist) {
  return Math.min(3, Math.pow(1 / attenuation(dist), 1 - VERB_FALLOFF));
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
// So `attenuation` stays what it is for the direct sound, and this layer rides
// on it: the same 1/r, scaled by `FAR_LEVEL`, which puts the far voice 6 dB
// under the near one at every distance past `FAR_FULL`.  The layers stay locked
// together because a bed that flattens out is what made a fight 12 km away read
// as louder than one at 2.6 km: on the free-field law a distant battle is
// simply quieter, and the far voice's job is the *spectrum* (all body, no
// crack), not the level.
export const FAR_LEVEL = 0.5;    // the layer's own level, under the same 1/r
const FAR_ROLLOFF = 1.0;         // inverse square, like the direct sound
const FAR_NEAR = 700;            // m, nothing inside this - the near cue is whole
const FAR_FULL = 2600;           // m, fully in past this (faded, squared)
// ...and the two things that keep it a *bed* instead of a wall.  A battle is
// ninety sound events a second, and every one of them is kilometres away: mixed
// per event the layer has no ceiling at all, so it summed to +9 dB over the mix
// it was added to (measured, a scripted 90 event/s fight) and buried the cues
// that matter.  So the far voices all land on one bus with a compressor on it -
// `FAR_CEIL` is where that bed settles however many events are in it - and a
// rate limit stops a burst of them from opening a voice each.  A far rumble is
// not a thing the ear counts: twenty a second is already a battle.  The ceiling
// follows the layer: one voice at 8 km peaks near -62 dBFS, ninety of them
// stack to about -43, so -34 is where the bed sits once a real fight is on.
const FAR_CEIL = -34;            // dBFS, where the far bed settles
// ...and the makeup its compressor adds back on its own: measured, a
// -50 dBFS sine through -34 / 8:1 comes out at +16.2 dB, so a far voice would
// arrive at 6.5x the level `FAR_LEVEL` gives it (see `farTrim`).
const FAR_MAKEUP = 6.45;
const FAR_MIN_GAP = 0.05;        // s, at most one far voice per this

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
const CLIP_GAIN = 0.86;          // unity through the limiter, then the shaper
const CLIP_KNEE = 0.7;           // the linear part of the curve

// The soft-clip curve: linear up to CLIP_KNEE, then a tanh bend that reaches 1
// without a corner.  A hard 0 dBFS clamp is broadband fizz on every transient;
// this is a rounding a decibel and a half wide, which no ear hears as anything
// but the peak it already was.
function _clipCurve() {
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
    this.send = null;               // the room's one fixed input (see ensure)
    this.far = null;                // the distant bed's own bus (see FAR_CEIL)
    this.farLim = null;
    this.farTrim = null;            // the makeup `FAR_CEIL`'s compressor adds
    this.limiter = null;
    this.top = null;                // CLIP_GAIN into the shaper
    this.clip = null;               // the soft ceiling, the last node there is
    this._farT = -1e9;              // when a far voice was last opened
    this._voiceEnd = [];            // when each live voice starts being free
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

  // Room for one more event?  Slots are held by the voices that are still
  // sounding, so the ceiling is on the simultaneous count rather than on the
  // rate - which is the shape a battle has: dozens of short cracks a second is
  // fine, a dozen two second rumbles at once is not.
  _admit(vol, dur) {
    const now = this.ctx.currentTime;
    const end = this._voiceEnd;
    let n = 0;
    for (let i = 0; i < end.length; i++) if (end[i] > now) end[n++] = end[i];
    end.length = n;
    if (n >= MAX_VOICES && vol < STEAL_VOL) return false;
    end.push(now + dur);
    return true;
  }

  ensure() {
    if (this.ctx) return;
    const AC = window.AudioContext || window.webkitAudioContext;
    if (!AC) return;
    this.ctx = new AC();
    // A limiter between the mix and the speaker: a dead hull plays four layers
    // at once, and the sum is louder than the 0 dB the destination clamps at -
    // clipping that sounds like fizz rather than like weight.  The compressor
    // catches the peaks so the layers can be pushed without it, and the shaper
    // after it is the promise: a compressor has an attack, so a transient is
    // always partly through before it ducks, and a hard 0 dBFS clamp on that
    // sliver is a speaker buzz rather than a bang.
    //
    // The curve is 12:1 over a 6 dB knee, and the threshold is what decides
    // whether the shaper ever works.  Measured with three gatlings 200 m out
    // (90 rounds a second, all of them inside `NEAR_FLOOR`): -8 dB left the mix
    // peaking at 0.90 with 5 % of its samples past the 0.7 knee - that is
    // continuous soft-clip distortion, heard as a faint buzz under everything
    // and present since the mix was built.  At -12 dB it peaks at 0.69 and
    // touches the knee 0 % of the time, while a 105 mm next to the pilot still
    // reads 0.76 against that 0.69 - the loudest cue on the field stays the
    // loudest, and it stops being a fuzz pedal.  A knee keeps a single round's
    // transient intact; the ratio is what holds a burst of them down.
    this.limiter = this.ctx.createDynamicsCompressor();
    this.limiter.threshold.value = -12;
    this.limiter.knee.value = 6;
    this.limiter.ratio.value = 12;
    this.limiter.attack.value = 0.002;
    this.limiter.release.value = 0.25;
    this.top = this.ctx.createGain();
    this.top.gain.value = CLIP_GAIN;
    this.clip = this.ctx.createWaveShaper();
    this.clip.curve = _clipCurve();
    this.clip.oversample = '4x';
    this.limiter.connect(this.top);
    this.top.connect(this.clip);
    this.clip.connect(this.ctx.destination);
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
    // and one fixed input to it: a convolver whose input connections change a
    // few hundred times a second recomputes its own mix on every change, which
    // is both work and a click.  Every voice's (per-voice, distance-lifted) send
    // gain lands on this, so the room itself never sees the graph move.
    this.send = this.ctx.createGain();
    this.send.gain.value = 1;
    this.send.connect(this.verb);
    // the far field is a bed, so it gets a bus of its own: see FAR_CEIL
    this.far = this.ctx.createGain();
    this.farLim = this.ctx.createDynamicsCompressor();
    this.farLim.threshold.value = FAR_CEIL;
    this.farLim.knee.value = 8;
    this.farLim.ratio.value = 8;
    this.farLim.attack.value = 0.01;
    this.farLim.release.value = 0.35;
    // ...and then give the layer back its own level.  A DynamicsCompressor adds
    // a makeup gain derived from its threshold and ratio, and at -34 / 8:1 that
    // gain is +16.2 dB on everything quieter than the knee (measured with a
    // -50 dBFS sine, which is where a far voice lives).  The bed was therefore
    // sixteen decibels above where `FAR_LEVEL` puts it - as loud as the near cue
    // it is meant to sit under, and, being a 95 Hz sine, the only thing left of
    // a boom 3 km out.  `farTrim` spends the makeup back, so a lone far voice
    // arrives at the level the design asks for and the compressor is only ever
    // the ceiling above the knee.
    this.farTrim = this.ctx.createGain();
    this.farTrim.gain.value = 1 / FAR_MAKEUP;
    this.far.connect(this.farLim);
    this.farLim.connect(this.farTrim);
    this.farTrim.connect(this.master);
    // The shared noise is long enough that nothing has to loop it: a buffer
    // that ends on a random sample and starts on another is a step in the
    // waveform at the seam, and a hull burning or a 105 mm's tail is loud
    // enough for that to be heard every 1.2 s.  Six seconds covers the longest
    // voice on the field (a hull's 4.5 s rumble, started at a random offset).
    const len = this.ctx.sampleRate * 6.0;
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
  // matters.  What the air just took off the top is not made up here: a makeup
  // curve applied per voice sums into a bass wall (see `_bus`'s neighbour above
  // and `FAR_CEIL`), so the body of a distant report comes from the far voice,
  // which is a bed and is compressed as one.
  // `dest` is the bus the voice lands on - the master for everything the pilot
  // is meant to hear as an event, the far bus for the distant bed.
  _bus(g, pan = 0, behind = 0, dist = 0, dest = null) {
    const made = [g];
    let out = g;
    const cut = Math.min(airCutoff(dist),
                         behind > 0.02 ? 12000 - 10000 * Math.min(1, behind) : 1e9);
    // A filter that would pass the whole band is a node for nothing: 16 kHz is
    // where the air has taken its first decibel, so everything inside 180 m
    // (and the whole near field of a gatling) skips it.  A gatling nest is
    // ninety voices a second and each one used to carry two of these.
    //
    // One corner is 12 dB/octave, and past ~270 m the model asks for 30-40 dB
    // down at a few kHz, so the corner is spent twice there: two at 451 Hz put
    // 3 kHz 55 dB down, which is what takes a boom's crack out of it entirely.
    // The second stage exists only where it is heard - a mid-field voice pays
    // one biquad, and the near field pays none.
    const stages = cut < 4000 ? 2 : cut < 16000 ? 1 : 0;
    for (let i = 0; i < stages; i++) {
      const lp = this.ctx.createBiquadFilter();
      lp.type = 'lowpass';
      lp.frequency.value = cut;   // 18k near -> 2.9 kHz at 300 m -> 451 Hz at 3 km
      lp.Q.value = 0.5;
      out.connect(lp);
      out = lp;
      made.push(lp);
    }
    // what goes into the room is the air-filtered signal but still unplaced:
    // a distant echo has crossed exactly as much atmosphere as the direct
    // sound, so it arrives just as dull, while the lateral position is the
    // dry sound's job (the room is wide, the event is placed)
    const prePan = out;
    if (Math.abs(pan) > 0.01 && this.ctx.createStereoPanner) {
      const p = this.ctx.createStereoPanner();
      p.pan.value = Math.max(-1, Math.min(1, pan));
      out.connect(p);
      out = p;
      made.push(p);
    }
    out.connect(dest || this.master);
    return { made, prePan };
  }

  // Send a voice into the shared room by `wet` (0..1).  Knobbed off it is a
  // single extra gain node per voice, which is nothing next to the convolver.
  // The send leaves ahead of `_bus`, so the room stays wide while the direct
  // sound is the part that is placed - which is how a real one behaves.  `dist`
  // lifts it back over the distance the dry sound just lost (`reverbLift`).
  _verbSend(node, wet, dist = 0) {
    if (!(wet > 0) || !this.verb) return null;
    const g = this.ctx.createGain();
    g.gain.value = VERB_SEND * wet * reverbLift(dist);
    node.connect(g);
    g.connect(this.send);
    return g;
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
    // the shared noise buffer is six seconds long, which is longer than any
    // voice on the field, so the seam is never played
    src.loop = dur > 5.5;
    const f = this.ctx.createBiquadFilter();
    f.type = type || 'bandpass';
    f.frequency.value = freq;
    f.Q.value = q;
    if (sweepTo) {
      // `hold` keeps the filter open for the first few tens of milliseconds: an
      // exponential sweep from 2.6 kHz to 110 Hz is already down at 500 Hz by
      // the time a quarter of it has played, which is how a burst that starts
      // bright still measures a low centroid.  A held opening is the transient.
      const hold = opts && opts.hold ? opts.hold : 0;
      if (hold > 0) f.frequency.setValueAtTime(freq, t + hold);
      f.frequency.exponentialRampToValueAtTime(sweepTo, t + dur);
    }
    // an optional second corner.  `hp` is what turns a low-passed burst into a
    // band: a real impact is flat from 100 to 500 Hz and then gone, so the low
    // end needs a floor as much as the top needs a ceiling (-24 dB at 63 Hz in
    // the recording, which one filter cannot give).
    let tail = f;
    let h = null;
    if (opts && opts.hp) {
      h = this.ctx.createBiquadFilter();
      h.type = 'highpass';
      h.frequency.value = opts.hp;
      h.Q.value = 0.7;
      f.connect(h);
      tail = h;
    }
    const g = this.ctx.createGain();
    if (opts && opts.env) {
      // a measured decay instead of one exponential ramp: [[t, gain], ...].
      // The breakpoints are *shape*, so the layer's own `gain` still has to be
      // folded in here - dropping it (as this did at first) leaves every layer
      // of the report at unity and the mix exactly the wrong way round: the
      // bright 0.7x crack arriving 12 dB over the 3.4x boom.
      opts.env.forEach(([at, v], i) => {
        const floor = Math.min(0.0001, gain * 0.01);
        const to = Math.max(v * gain, floor);
        if (i === 0) g.gain.setValueAtTime(to, t + at);
        else g.gain.exponentialRampToValueAtTime(to, t + at);
      });
    } else {
      // a distant cue arrives quieter than the floor this used to ramp to, and
      // an exponential ramp to a fixed 0.0008 would then grow it as it decays
      const peak = Math.max(gain, 0.0001);
      g.gain.setValueAtTime(peak, t);
      g.gain.exponentialRampToValueAtTime(Math.min(0.0008, peak * 0.3), t + dur * 0.9);
    }
    // and the last few milliseconds go to silence, linearly: an exponential ramp
    // can only ever approach zero, and the step from where it stops to the node
    // being cut is a click - a battle makes hundreds a second and that is the
    // low buzz under everything
    g.gain.linearRampToValueAtTime(0, t + dur);
    src.connect(f); tail.connect(g);
    // a voice on the far bus is already the sample that crossed the valley:
    // sending it into the room again put a reverb tail on every single event of
    // a battle, which is both the loudest thing in the bed and the most
    // expensive one (a convolver input per voice, ninety times a second)
    const dest = opts && opts.dest ? opts.dest : null;
    const busOut = this._bus(g, pan, behind, dist, dest);
    const chain = busOut.made;
    if (!dest) {
      const vg = this._verbSend(busOut.prePan, wet === null ? reverberation(dist) : wet, dist);
      if (vg) chain.push(vg);
    }
    // a voice is a temporary chain: the nodes are worth nothing once the source
    // has played, and a battle opens hundreds of them a second, so the chain is
    // torn down when the source ends instead of waiting for a collector
    src.onended = () => {
      src.disconnect();
      f.disconnect();
      if (h) h.disconnect();
      for (const n of chain) n.disconnect();
    };
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
    // same floor as `_noiseBurst`: a far voice is quieter than the ramp's end,
    // and ramping up to it would turn a decay into a swell
    const peak = Math.max(gain, 0.0001);
    if (attack > 0) {
      // scheduled from zero, or the node's default gain of 1 is already open
      // when the oscillator starts
      g.gain.setValueAtTime(0.0001, t);
      g.gain.exponentialRampToValueAtTime(peak, t + attack);
    } else {
      g.gain.setValueAtTime(peak, t);
    }
    g.gain.exponentialRampToValueAtTime(Math.min(0.001, peak * 0.3), t + attack + dur * 0.9);
    g.gain.linearRampToValueAtTime(0, t + attack + dur);
    o.connect(g);
    const dest = opts && opts.dest ? opts.dest : null;
    const busOut = this._bus(g, pan, behind, dist, dest);
    const chain = busOut.made;
    if (!dest) {
      const vg = this._verbSend(busOut.prePan, opts && opts.wet !== undefined ? opts.wet
        : reverberation(dist) * 0.6, dist);
      if (vg) chain.push(vg);
    }
    o.onended = () => {
      o.disconnect();
      for (const n of chain) n.disconnect();
    };
    o.start(t); o.stop(t + attack + dur + 0.02);
  }

  // The far-field voice: the same event heard from kilometres away, as a rumble
  // with no crack left in it.  It is mixed on its own law (see `FAR_LEVEL`) and
  // faded in past `FAR_NEAR`, so the near cue is whole where the pilot can see
  // what fired.
  //
  // Both layers are played at `dist` 0 on purpose: the air filter of `_bus` is
  // for a *near* sample heard through the atmosphere, and
  // this is the already-through-the-atmosphere sample.  Filtering it again
  // would take the edge off the very band it was built to keep.
  //
  // What it does not get is a reverb send of its own.  The room is for the near
  // cue that arrived *through* the field; this voice is the field's answer
  // already, and a convolver input on every distant event is the one thing that
  // turned a battle into a wall (see `FAR_CEIL`).  It lands on the far bus
  // instead, which is compressed as one bed.
  _farBoom(dist, src, weight, pan, behind, tall = 0.55) {
    if (!this.ctx || !this.far) return;
    const fade = Math.max(0, Math.min(1, (dist - FAR_NEAR) / (FAR_FULL - FAR_NEAR)));
    const law = Math.pow(SOUND_REF_DIST / Math.max(SOUND_REF_DIST, dist),
                         FAR_ROLLOFF);
    const g = FAR_LEVEL * src * weight * law * fade * fade;
    // -70 dBFS: the far corner of the map is a rumble you can just about place,
    // and a voice under this one is arithmetic the ear never gets
    if (!(g > 0.0003)) return;
    // one voice at a time: at ninety events a second the bed is the same rumble
    // whether or not each bang opens its own
    const now = this.ctx.currentTime;
    if (now - (this._farT === undefined ? -1e9 : this._farT) < FAR_MIN_GAP) return;
    this._farT = now;
    const dest = this.far;
    // The body is noise, not a tone.  It used to be a 95 -> 42 Hz sine and
    // nothing else above it: a pure tone carries no air, so an explosion 3 km
    // out arrived as a clean electronic sweep - the one thing in the mix with a
    // pitch, and the reason a distant bang read as a bell rather than as a
    // boom.  A rumble that crossed six kilometres is broadband at the bottom,
    // soft in its attack (there is no transient left) and slow to die.
    this._noiseBurst(tall * 1.6, 260, 0.5, Math.min(0.9, g), 'lowpass', 60, 0, null,
                     pan, behind, { dest, env: [[0, 0.3], [0.06, 1], [0.35, 0.6],
                                                [0.9, 0.24], [tall * 1.4, 0.01]] });
    // and the sub under it at a tenth of that: felt on a system that reproduces
    // 30-60 Hz, out of the way on one that does not, and never the layer the
    // event is *made* of (a sine is one bin of the spectrum and this one is
    // audited: at a third it held 40-60 % of a distant event's energy, i.e. the
    // event was a tone again)
    this._thump(58, tall, Math.min(0.9, g * 0.1), 0, pan, behind,
                { sweepTo: 34, attack: 0.06, dest });
  }

  // `vol` is the source level (`SOUND_LEVEL`) already multiplied by the
  // distance loss, and `dist` is how far the event was from the pilot when the
  // wavefront reached them: between them they set the level, the air's lowpass
  // and the reverb send.  `pan` / `behind` place it: left-right, and how much
  // of it was at the pilot's back (see `_bus`).
  shot(wid, kind, vol = 1, dist = 0, pan = 0, behind = 0, heavy = false) {
    this.ensure();
    if (!this.ctx) return;
    const guided = kind === 'rocket' || kind === 'saclos' || kind === 'top';
    const farW = heavy ? 1.6 : guided ? 0.4 : 1.0;
    if (vol < NEAR_FLOOR) {
      this._farBoom(dist, vol / attenuation(dist), farW, pan, behind,
                    heavy ? 0.9 : 0.5);
      return;
    }
    // the slot a voice holds is how long it sounds: a gatling's crack is over in
    // a tenth of a second and a nest of them is thirty a second, so charging
    // them a cannon's 0.8 s would fill the ceiling with clicks and start
    // dropping the explosions three kilometres out
    if (!this._admit(vol, heavy ? 2.0 : kind === 'bullet' ? 0.2 : 0.8)) return;
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
      // One voice for every automatic weapon - the 30 mm autocannon, the 12.7 mm
      // gatling and the rifle: a 1.4 kHz band sweeping into the bore's thump.
      // A 3 kHz crack was tried and measured brighter (a gatling's transient is
      // really up there), but in the cockpit it is the wrong cue: these fire
      // twenty rounds a second each and they sit *under* the 105 mm, where a
      // bright transient turns the whole field into a hiss.
      this._noiseBurst(0.11, 1400 + Math.random() * 500, 1.2, 0.28 * vol, 'bandpass',
                       400, dist, null, pan, behind);
      this._thump(120, 0.09, 0.25 * vol, dist, pan, behind);
    } else if (kind === 'rocket' || kind === 'saclos' || kind === 'top') {
      // Three layers, because a launch is three sounds at once and any one of
      // them alone reads wrong: the hiss of propellant leaving the tube (a band
      // swept down from 1.9 kHz), the body of the gas cloud, and the shove the
      // launcher gives the hull.  The body is the one that was missing - a cue
      // with only the hiss and the thump measured a 1.1 kHz centroid and read
      // thin, and a Su-34's S-8 salvo puts most of what a launch *adds* at
      // 315-500 Hz (46 % of the energy during the salvo), so that band is what
      // gives the rocket its weight.
      this._noiseBurst(0.6, 1500, 1.2, 0.08 * vol, 'bandpass', 650, dist, null, pan, behind);
      this._noiseBurst(0.5, 520, 0.95, 0.95 * vol, 'bandpass', 330, dist, null, pan, behind);
      this._thump(95, 0.3, 0.05 * vol, dist, pan, behind);
    } else {
      this._noiseBurst(0.08, 1800, 1.5, 0.2 * vol, 'bandpass', 600, dist, null, pan, behind);
    }
    // and, far enough out to have lost its crack on the way, the voice that
    // survived the trip.  A guided launch is a whoosh and carries nothing like
    // as far as a gun, hence the weight.
    this._farBoom(dist, vol / attenuation(dist), farW, pan, behind,
                  heavy ? 0.9 : 0.5);
  }

  boom(vol = 1, big = false, dist = 0, pan = 0, behind = 0) {
    this.ensure();
    if (!this.ctx) return;
    if (vol < NEAR_FLOOR) {
      this._farBoom(dist, vol / attenuation(dist), big ? 1.3 : 1.0, pan, behind,
                    big ? 1.2 : 0.6);
      return;
    }
    if (!this._admit(vol, big ? 1.2 : 0.6)) return;
    // Measured off a real 40 mm airburst recording, three impacts at the target
    // (tools/blast_report.py carries the model; this is the microphone):
    //
    //   spectral centroid   758 / 765 / 924 Hz      (mean ~816)
    //   energy above 2 kHz   11.0 / 10.6 / 14.7 %   (mean ~12)
    //   200-500 Hz            52 / 60 / 47 %        <- where almost all of it is
    //   below 100 Hz           1.3 / 2.6 / 2.3 %     <- almost none
    //   -6 dB span           53 / 50 / 39 ms
    //   -20 dB span         481 / 320 / 211 ms
    //
    // Two things were wrong with the cue.  It had a 100 Hz sine thump and a
    // lowpass at 220 Hz, so its energy sat an octave and a half below a real
    // impact's - which is the "muffled" - and it held for 420 ms with no
    // transient, where a real one is half gone in 50 ms and only its tail
    // reaches a third of a second.  A Friedlander source says the same thing
    // the recording does once the charge is small: the body is a few hundred
    // hertz, not tens.
    //
    // So: a highpassed crack for the 2 kHz tenth, a wide bandpass at ~380 Hz
    // for the body (that band is the whole of what an impact sounds like), and
    // an envelope that falls 6 dB in 45 ms and then crawls - no sine under it
    // until the charge is a warhead.
    //
    // The third-octave shape the recording has, in dB below its own peak:
    // 200-315 Hz is the top (0 to -1.7), 100-160 sits at -5, 400-630 at -3 to
    // -6, then a trough at 800-1000 (-10.5), a second shoulder at 2-4 kHz
    // (-6.3) and nothing left by 8 kHz (-15).  Two wide bands three octaves
    // apart reproduce that: the gap between them *is* the trough.
    this._noiseBurst(big ? 0.09 : 0.06, big ? 2600 : 3000, 1.3,
                     Math.min(1, (big ? 0.55 : 0.45) * vol), 'bandpass', big ? 1600 : 1900,
                     dist, null, pan, behind);
    // The body is a lowpass, not a narrow band: the recording is flat from 100
    // to 500 Hz and then falls 12 dB per octave, which is exactly a lowpass at
    // ~450 - and a bandpass at 190 has a skirt far too shallow to make the
    // 800-1000 Hz trough (-11 dB there, measured) come out.
    this._noiseBurst(big ? 1.0 : 0.72, big ? 420 : 450, 0.7,
                     Math.min(1, (big ? 0.62 : 0.55) * vol), 'lowpass', big ? 170 : 200,
                     dist, null, pan, behind, { hp: big ? 70 : 85, env: big
                       ? [[0, 1], [0.12, 0.5], [0.40, 0.1], [0.86, 0.01]]
                       : [[0, 1], [0.075, 0.5], [0.26, 0.1], [0.55, 0.01]] });
    if (big) this._thump(58, 0.3, Math.min(1, 0.2 * vol), dist, pan, behind);
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
    if (vol < NEAR_FLOOR) {
      this._farBoom(dist, vol / attenuation(dist), 1.2, pan, behind, 1.8);
      return;
    }
    if (!this._admit(vol, 4.6)) return;
    const v = Math.min(1, vol);
    const wet = reverberation(dist);
    // the pressure front: a low-mid shove rather than a gunshot's bright crack
    this._noiseBurst(0.3, 650, 0.5, Math.min(1, 0.55 * v), 'lowpass', 90, dist,
                     0.2 + 0.5 * wet, pan, behind);
    // the fire: a broad wall of noise, low-passed hard and swept down
    this._noiseBurst(2.8, 800, 0.5, Math.min(1, 0.9 * v), 'lowpass', 60, dist,
                     0.35 + 0.65 * wet, pan, behind);
    // the kick under the floor: audible bass before the sine dives below it.
    // A sine is felt, so this layer belongs to a blast the pilot is *near*: from
    // 250 m out it recedes into the rumble the far voice brings, or a hull dying
    // 3 km away is a two-second tone and nothing else (measured: 58 Hz held 61 %
    // of that event's energy)
    this._thump(58, 2.1, Math.min(1, 0.95 * v) * (1 - 0.85 * wet), dist, pan, behind);
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
    const v = this._warble('missile', RWR_TONE_S);
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
