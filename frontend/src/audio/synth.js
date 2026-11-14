// The voice toolbox: the three shapes every cue on the field is built out of,
// and the only place a temporary node chain is created or torn down.
//
// A voice is a temporary chain: the nodes are worth nothing once the source has
// played, and a battle opens hundreds of them a second, so each one is
// disconnected in its own `onended` instead of waiting for a collector.  Measured
// on a 4 s gatling-and-boom load, the graph held 3492 nodes after the load and
// never let go, against 1 once the teardown runs.
//
// Every function here takes the `GameAudio` facade as `a` and reads `a.ctx`,
// `a.graph` and `a.voices` - the toolbox owns no state, so a cue is a recipe over
// it and the graph is the only thing that has to be brought up and down.

import { reverberation } from './levels.js';

// A measured decay instead of one exponential ramp: [[t, gain], ...].  The
// breakpoints are *shape*, so the layer's own `gain` still has to be folded in
// here - dropping it (as this did at first) leaves every layer of the report at
// unity and the mix exactly the wrong way round: the bright 0.7x crack arriving
// 12 dB over the 3.4x boom.
function envelope(g, gain, env, t) {
  env.forEach(([at, v], i) => {
    const floor = Math.min(0.0001, gain * 0.01);
    const to = Math.max(v * gain, floor);
    if (i === 0) g.gain.setValueAtTime(to, t + at);
    else g.gain.exponentialRampToValueAtTime(to, t + at);
  });
}

// A filtered noise burst: the body of every weapon cue there is.  `wet` overrides
// the distance-driven reverb send - a burst that is meant to be heard as a rumble
// gets its room even from close up - and `opts.delay` starts the whole voice
// later, because the layers of a report are not simultaneous (see `cues.shot`) and
// one buffer source cannot be offset from another's clock.
export function noiseBurst(a, dur, freq, q, gain, type, sweepTo, dist = 0, wet = null,
                           pan = 0, behind = 0, opts = null) {
  const ctx = a.ctx;
  if (!ctx) return;
  const t = ctx.currentTime + (opts && opts.delay ? opts.delay : 0);
  const src = ctx.createBufferSource();
  src.buffer = a.graph.noise;
  src.playbackRate.value = 0.8 + Math.random() * 0.5;
  // the shared noise buffer is six seconds long, which is longer than any voice on
  // the field, so the seam is never played
  src.loop = dur > 5.5;
  const f = ctx.createBiquadFilter();
  f.type = type || 'bandpass';
  f.frequency.value = freq;
  f.Q.value = q;
  if (sweepTo) f.frequency.exponentialRampToValueAtTime(sweepTo, t + dur);
  const g = ctx.createGain();
  if (opts && opts.env) {
    envelope(g, gain, opts.env, t);
  } else {
    // a cue that arrives quiet is quieter than the floor this used to ramp to, and
    // an exponential ramp to a fixed 0.0008 would then grow it as it decays
    const peak = Math.max(gain, 0.0001);
    g.gain.setValueAtTime(peak, t);
    g.gain.exponentialRampToValueAtTime(Math.min(0.0008, peak * 0.3), t + dur * 0.9);
  }
  // and the last few milliseconds go to silence, linearly: an exponential ramp can
  // only ever approach zero, and the step from where it stops to the node being
  // cut is a click - a battle makes hundreds a second and that is the low buzz
  // under everything
  g.gain.linearRampToValueAtTime(0, t + dur);
  src.connect(f); f.connect(g);
  // a voice on the far bus is already the sample that crossed the valley: sending
  // it into the room again put a reverb tail on every single event of a battle,
  // which is both the loudest thing in the bed and the most expensive one (a
  // convolver input per voice, ninety times a second)
  const dest = opts && opts.dest ? opts.dest : null;
  const chain = a.graph.bus(g, pan, behind, dist, dest);
  if (!dest) {
    const vg = a.graph.verbSend(g, wet === null ? reverberation(dist) : wet, dist);
    if (vg) chain.push(vg);
  }
  src.onended = () => {
    src.disconnect();
    f.disconnect();
    for (const n of chain) n.disconnect();
  };
  src.start(t, Math.random() * 0.4);
  src.stop(t + dur + 0.05);
}

// A sine falling from `freq` to `freq * 0.35` (or to `opts.sweepTo`) for the
// weight under a bang.  `opts` is the heavy report's shape: `delay` for a thump
// that arrives behind the crack, `attack` for one that swells rather than slams,
// `wet` for a boom that is all room.
export function thump(a, freq, dur, gain, dist = 0, pan = 0, behind = 0, opts = null) {
  const ctx = a.ctx;
  if (!ctx) return;
  const delay = opts && opts.delay ? opts.delay : 0;
  const attack = opts && opts.attack ? opts.attack : 0;
  const t = ctx.currentTime + delay;
  const o = ctx.createOscillator();
  o.type = 'sine';
  o.frequency.setValueAtTime(freq, t);
  o.frequency.exponentialRampToValueAtTime(
    opts && opts.sweepTo ? opts.sweepTo : freq * 0.35, t + dur);
  const g = ctx.createGain();
  // same floor as `noiseBurst`: a quiet voice is quieter than the ramp's end, and
  // ramping up to it would turn a decay into a swell
  const peak = Math.max(gain, 0.0001);
  if (attack > 0) {
    // scheduled from zero, or the node's default gain of 1 is already open when
    // the oscillator starts
    g.gain.setValueAtTime(0.0001, t);
    g.gain.exponentialRampToValueAtTime(peak, t + attack);
  } else {
    g.gain.setValueAtTime(peak, t);
  }
  g.gain.exponentialRampToValueAtTime(Math.min(0.001, peak * 0.3), t + attack + dur * 0.9);
  g.gain.linearRampToValueAtTime(0, t + attack + dur);
  o.connect(g);
  const dest = opts && opts.dest ? opts.dest : null;
  const chain = a.graph.bus(g, pan, behind, dist, dest);
  if (!dest) {
    const vg = a.graph.verbSend(g, opts && opts.wet !== undefined ? opts.wet
      : reverberation(dist) * 0.6, dist);
    if (vg) chain.push(vg);
  }
  o.onended = () => {
    o.disconnect();
    for (const n of chain) n.disconnect();
  };
  o.start(t); o.stop(t + attack + dur + 0.02);
}

// A one-shot oscillator: `type` for the tone, the pitch falling `f0 -> f1` over
// `sweep` (0 for a flat pip), the gain falling from `gain` to `floor` over
// `decay`, and the node stopped `dur` after it starts.  Two callers, both machine
// voices that land on the master bus with no bus and no room: the empty-trigger
// buzz and the lock tone's acquiring pip.
export function blip(a, { type = 'square', f0, f1 = 0, sweep = 0, gain,
                          floor = 0.001, decay, dur, dest = null }) {
  const ctx = a.ctx;
  if (!ctx) return;
  const t = ctx.currentTime;
  const o = ctx.createOscillator();
  o.type = type;
  o.frequency.setValueAtTime(f0, t);
  if (sweep > 0) o.frequency.exponentialRampToValueAtTime(f1, t + sweep);
  const g = ctx.createGain();
  g.gain.setValueAtTime(gain, t);
  g.gain.exponentialRampToValueAtTime(floor, t + decay);
  o.connect(g); g.connect(dest || a.graph.master);
  // the two pips used to leave their nodes behind: an empty-trigger buzz every
  // 0.7 s and a pip every 0.24 s is thirty chains a minute hanging off the master
  o.onended = () => { o.disconnect(); g.disconnect(); };
  o.start(t); o.stop(t + dur);
}
