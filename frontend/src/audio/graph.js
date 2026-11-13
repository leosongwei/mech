// The node graph: the one place a permanent node is created, and now the one place
// the whole thing is taken down again.
//
//     voice --+-> [bus] lowpass (air, behind) -> panner -> master
//             |                                            |
//             +-> verbSend -> send -> convolver -> master   |
//                                                            v
//                        master -> limiter -> top -> clip -> destination
//                        alarms -> master        (the trill and the beep train)
//                        far -> farLim -> master (the distant bed, compressed)
//
// `master` is the mix every cue lands on and the slider the settings menu moves.
// `alarms` rides inside it with a slider of its own, so a pilot can turn the RWR
// down without turning the battle down with it.  `verb` is one room for the whole
// field, fed through one fixed `send` input: a convolver whose input connections
// change hundreds of times a second recomputes its own mix on every change, which
// is both work and a click.  `far` is the bed of everything kilometres away, and
// `farLim` is what keeps it a bed.
//
// The 100 ms alarm clock lives here too, and not on the frame: the frame stops when
// the tab is in the background, and the trill has to keep its own time.

import {
  ALARM_GAIN, CLIP_GAIN, FAR_CEIL, FAR_MIN_GAP, MASTER_GAIN, VERB_SEND,
  airCutoff, clipCurve, reverbLift,
} from './levels.js';
import { pump, stopVoices } from './alarms.js';

const PUMP_MS = 100;

export class Graph {
  constructor(audio) {
    this.a = audio;                 // the facade: both volume sliders live there
    this.ctx = null;
    this.noise = null;
    this.master = null;
    this.alarms = null;
    this.verb = null;
    this.send = null;               // the room's one fixed input (see ensure)
    this.far = null;                // the distant bed's own bus (see FAR_CEIL)
    this.farLim = null;
    this.limiter = null;
    this.top = null;                // CLIP_GAIN into the shaper
    this.clip = null;               // the soft ceiling, the last node there is
    this.farT = -1e9;               // when a far voice was last opened
    this.pumpTimer = 0;
  }

  // Idempotent, and silent about a browser with no WebAudio at all: this runs from
  // the first user gesture, and a machine without an AudioContext still plays.
  ensure() {
    if (this.ctx) return this.ctx;
    const AC = window.AudioContext || window.webkitAudioContext;
    if (!AC) return null;
    const ctx = this.ctx = new AC();
    // A limiter between the mix and the speaker: a dead hull plays four layers at
    // once, and the sum is louder than the 0 dB the destination clamps at -
    // clipping that sounds like fizz rather than like weight.  The compressor
    // catches the peaks so the layers can be pushed without it, and the shaper
    // after it is the promise: a compressor has an attack, so a transient is
    // always partly through before it ducks, and a hard 0 dBFS clamp on that
    // sliver is a speaker buzz rather than a bang.
    //
    // The curve is the ceiling the mix is measured against.  A threshold that sits
    // below where a dense mix actually lives is the difference between a peak
    // catcher and a fuzz pedal: measured with three gatlings 200 m out (90 rounds
    // a second), -8 dB left the mix peaking at 0.90 with 5 % of its samples past
    // the 0.7 knee - continuous soft-clip distortion, heard as a faint buzz under
    // everything.  At -12 dB it peaks at 0.69 and touches the knee 0 % of the
    // time, while a 105 mm next to the pilot still reads 0.76 against that 0.69.
    // A knee keeps a single round's transient intact; the ratio is what holds a
    // burst of them down.
    this.limiter = ctx.createDynamicsCompressor();
    this.limiter.threshold.value = -12;
    this.limiter.knee.value = 6;
    this.limiter.ratio.value = 12;
    this.limiter.attack.value = 0.002;
    this.limiter.release.value = 0.25;
    this.top = ctx.createGain();
    this.top.gain.value = CLIP_GAIN;
    this.clip = ctx.createWaveShaper();
    this.clip.curve = clipCurve();
    this.clip.oversample = '4x';
    this.limiter.connect(this.top);
    this.top.connect(this.clip);
    this.clip.connect(ctx.destination);
    this.master = ctx.createGain();
    this.master.gain.value = MASTER_GAIN * this.a.volume;
    this.master.connect(this.limiter);
    this.alarms = ctx.createGain();
    this.alarms.gain.value = ALARM_GAIN * this.a.alarmVolume;
    this.alarms.connect(this.master);
    // one shared room for every event that arrives from a distance
    this.verb = ctx.createConvolver();
    this.verb.buffer = this._impulse(2.4, 2.8);
    this.verb.connect(this.master);
    // and one fixed input to it: every voice's (per-voice, distance-lifted) send
    // gain lands on this, so the room itself never sees the graph move
    this.send = ctx.createGain();
    this.send.gain.value = 1;
    this.send.connect(this.verb);
    // the far field is a bed, so it gets a bus of its own: see FAR_CEIL
    this.far = ctx.createGain();
    this.farLim = ctx.createDynamicsCompressor();
    this.farLim.threshold.value = FAR_CEIL;
    this.farLim.knee.value = 8;
    this.farLim.ratio.value = 8;
    this.farLim.attack.value = 0.01;
    this.farLim.release.value = 0.35;
    this.far.connect(this.farLim);
    this.farLim.connect(this.master);
    // The shared noise is long enough that nothing has to loop it: a buffer that
    // ends on a random sample and starts on another is a step in the waveform at
    // the seam, and a hull burning or a 105 mm's tail is loud enough for that to be
    // heard every 1.2 s.  Six seconds covers the longest voice on the field (a
    // hull's 4.5 s rumble, started at a random offset).
    const len = ctx.sampleRate * 6.0;
    const buf = ctx.createBuffer(1, len, ctx.sampleRate);
    const d = buf.getChannelData(0);
    for (let i = 0; i < len; i++) d[i] = Math.random() * 2 - 1;
    this.noise = buf;
    // the alarm warbles keep their own clock: the frame loop can stall (tab in the
    // background) without the tone freezing on one pitch or droning on
    this.pumpTimer = setInterval(() => pump(this.a), PUMP_MS);
    return ctx;
  }

  // A context built before the page has had a user gesture comes up suspended, and
  // a suspended context is a silent battle: the lobby calls this on the first click
  // that reaches the page.
  resume() {
    if (this.ctx && this.ctx.state === 'suspended') this.ctx.resume();
  }

  // Impulse response for the shared convolver: decaying stereo noise with no
  // discrete early reflections, which is enough to read as "open ground with hills
  // around it" without the metallic ring of a real room.
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
  // event's lateral bearing as the wavefront reaches the pilot; `behind` (0..1) is
  // how far behind the cockpit it was, and it is spent as a lowpass - two ears
  // cannot place a bang along the nose-to-tail axis, so rolling off its top end is
  // what reads as "behind you".  All three are set once, when the cue fires: a
  // one-shot is located where it was heard, not tracked as the head turns.  `dist`
  // is the other lowpass - the air itself (see `airCutoff`) - and the two are one
  // filter, because the steeper of the two is the only one that matters.  What the
  // air just took off the top is not made up here: a makeup curve applied per voice
  // sums into a bass wall (see the note in `levels.js` and `FAR_CEIL`), so the body
  // of a distant report comes from the far voice, which is a bed and is compressed
  // as one.
  //
  // `dest` is the bus the voice lands on - the master for everything the pilot is
  // meant to hear as an event, the far bus for the distant bed.  The nodes it opens
  // are handed back, so the voice that owns them can disconnect them when it ends.
  bus(g, pan = 0, behind = 0, dist = 0, dest = null) {
    const ctx = this.ctx;
    const made = [g];
    let out = g;
    const cut = Math.min(airCutoff(dist),
                         behind > 0.02 ? 12000 - 10000 * Math.min(1, behind) : 1e9);
    if (cut < 17500) {
      const lp = ctx.createBiquadFilter();
      lp.type = 'lowpass';
      lp.frequency.value = cut;   // 12k -> 2k behind, 18k -> 1.4 kHz far
      lp.Q.value = 0.4;
      out.connect(lp);
      out = lp;
      made.push(lp);
    }
    if (Math.abs(pan) > 0.01 && ctx.createStereoPanner) {
      const p = ctx.createStereoPanner();
      p.pan.value = Math.max(-1, Math.min(1, pan));
      out.connect(p);
      out = p;
      made.push(p);
    }
    out.connect(dest || this.master);
    return made;
  }

  // Send a voice into the shared room by `wet` (0..1).  Knobbed off it is a single
  // extra gain node per voice, which is nothing next to the convolver.  The send
  // leaves ahead of the bus, so the room stays wide while the direct sound is the
  // part that is placed - which is how a real one behaves.  `dist` lifts it back
  // over the distance the dry sound just lost (`reverbLift`).
  verbSend(node, wet, dist = 0) {
    if (!(wet > 0) || !this.verb) return null;
    const g = this.ctx.createGain();
    g.gain.value = VERB_SEND * wet * reverbLift(dist);
    node.connect(g);
    g.connect(this.send);
    return g;
  }

  // The bed takes at most one voice per `FAR_MIN_GAP`: a battle is ninety events a
  // second and every one of them is kilometres away, so each bang opening its own
  // rumble is the wall `FAR_CEIL` exists to prevent.  A far rumble is not a thing
  // the ear counts: twenty a second is already a battle.  `true` once per gap; the
  // clock is the audio context's, so a background tab does not open a burst of them
  // when it wakes.
  farOpen() {
    const now = this.ctx.currentTime;
    if (now - this.farT < FAR_MIN_GAP) return false;
    this.farT = now;
    return true;
  }

  // Everything this file brought up, in reverse: the alarm clock, the long-lived
  // voices, the context.  The old engine had no teardown at all - one page, one
  // context, and a restart left a running graph and a `setInterval` behind for as
  // long as the tab lived.
  dispose() {
    if (this.pumpTimer) {
      clearInterval(this.pumpTimer);
      this.pumpTimer = 0;
    }
    if (!this.ctx) return;
    stopVoices(this.a);
    const ctx = this.ctx;
    this.ctx = null;
    this.noise = null;
    this.master = null;
    this.alarms = null;
    this.verb = null;
    this.send = null;
    this.far = null;
    this.farLim = null;
    this.limiter = null;
    this.top = null;
    this.clip = null;
    this.farT = -1e9;
    if (this.a.voices) this.a.voices.clear();
    try { ctx.close(); } catch (e) { /* already closed */ }
  }
}
