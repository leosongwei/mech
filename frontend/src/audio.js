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

// speed of sound in air at 20 C, m/s
export const SOUND_SPEED = 343;

// Distance attenuation: exponential, halving every 500 m - half level at
// 500 m, a quarter at 1 km, 1.6% at 3 km - so gunfire across the map is still
// there, just distant.
export function attenuation(dist) {
  return Math.pow(0.5, Math.max(1, dist) / 500);
}

// How much of a sound event is the terrain answering back, 0..1.  Distance is
// the only knob: the wavefront of a far shot arrives smeared and raggedy.
export function reverberation(dist) {
  return Math.max(0, Math.min(1, (dist - VERB_NEAR) / (VERB_FAR - VERB_NEAR)));
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
  // end is what reads as "behind you".  Both are set once, when the cue fires:
  // a one-shot is located where it was heard, not tracked as the head turns.
  _bus(g, pan = 0, behind = 0) {
    let out = g;
    if (behind > 0.02) {
      const lp = this.ctx.createBiquadFilter();
      lp.type = 'lowpass';
      lp.frequency.value = 12000 - 10000 * Math.min(1, behind);   // 12k -> 2k
      lp.Q.value = 0.4;
      g.connect(lp);
      out = lp;
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
  // sound is the part that is placed - which is how a real one behaves.
  _verbSend(node, wet) {
    if (!(wet > 0) || !this.verb) return;
    const g = this.ctx.createGain();
    g.gain.value = VERB_SEND * wet;
    node.connect(g);
    g.connect(this.verb);
  }

  // `wet` overrides the distance-driven reverb send: a burst that is meant to be
  // heard as a rumble gets its room even from close up.
  _noiseBurst(dur, freq, q, gain, type, sweepTo, dist = 0, wet = null,
              pan = 0, behind = 0) {
    if (!this.ctx) return;
    const t = this.ctx.currentTime;
    const src = this.ctx.createBufferSource();
    src.buffer = this.noise;
    src.playbackRate.value = 0.8 + Math.random() * 0.5;
    const f = this.ctx.createBiquadFilter();
    f.type = type || 'bandpass';
    f.frequency.value = freq;
    f.Q.value = q;
    if (sweepTo) f.frequency.exponentialRampToValueAtTime(sweepTo, t + dur);
    const g = this.ctx.createGain();
    g.gain.setValueAtTime(gain, t);
    g.gain.exponentialRampToValueAtTime(0.0008, t + dur);
    src.connect(f); f.connect(g);
    this._bus(g, pan, behind);
    this._verbSend(g, wet === null ? reverberation(dist) : wet);
    src.start(t, Math.random() * 0.4);
    src.stop(t + dur + 0.05);
  }

  _thump(freq, dur, gain, dist = 0, pan = 0, behind = 0) {
    if (!this.ctx) return;
    const t = this.ctx.currentTime;
    const o = this.ctx.createOscillator();
    o.type = 'sine';
    o.frequency.setValueAtTime(freq, t);
    o.frequency.exponentialRampToValueAtTime(freq * 0.35, t + dur);
    const g = this.ctx.createGain();
    g.gain.setValueAtTime(gain, t);
    g.gain.exponentialRampToValueAtTime(0.001, t + dur);
    o.connect(g);
    this._bus(g, pan, behind);
    this._verbSend(g, reverberation(dist) * 0.6);
    o.start(t); o.stop(t + dur + 0.02);
  }

  // `dist` is how far the event was from the pilot when the wavefront reached
  // them: it sets the reverb send, so a far shot is heard with its own echo.
  // `pan` / `behind` place it: left-right, and how much of it was at the pilot's
  // back (see `_bus`).
  shot(wid, kind, vol = 1, dist = 0, pan = 0, behind = 0) {
    this.ensure();
    if (!this.ctx) return;
    if (kind === 'bullet') {
      this._noiseBurst(0.11, 1400 + Math.random() * 500, 1.2, 0.28 * vol, 'bandpass', 400,
                       dist, null, pan, behind);
      this._thump(120, 0.09, 0.25 * vol, dist, pan, behind);
    } else if (kind === 'rocket' || kind === 'saclos' || kind === 'top') {
      this._noiseBurst(0.75, 700, 0.7, 0.32 * vol, 'lowpass', 220, dist, null, pan, behind);
      this._thump(80, 0.35, 0.2 * vol, dist, pan, behind);
    } else {
      this._noiseBurst(0.08, 1800, 1.5, 0.2 * vol, 'bandpass', 600, dist, null, pan, behind);
    }
  }

  boom(vol = 1, big = false, dist = 0, pan = 0, behind = 0) {
    this.ensure();
    if (!this.ctx) return;
    this._noiseBurst(big ? 1.1 : 0.5, big ? 420 : 600, 0.8, Math.min(1, 0.5 * vol),
                     'lowpass', 90, dist, null, pan, behind);
    this._thump(big ? 55 : 85, big ? 0.9 : 0.4, Math.min(1, 0.5 * vol), dist, pan, behind);
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
  }

  hit() {
    this.ensure();
    if (!this.ctx) return;
    const t = this.ctx.currentTime;
    const o = this.ctx.createOscillator();
    o.type = 'square';
    o.frequency.setValueAtTime(1250, t);
    o.frequency.exponentialRampToValueAtTime(700, t + 0.07);
    const g = this.ctx.createGain();
    g.gain.setValueAtTime(0.12, t);
    g.gain.exponentialRampToValueAtTime(0.001, t + 0.08);
    o.connect(g); g.connect(this.master);
    o.start(t); o.stop(t + 0.09);
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

  // one-shot acknowledgement on the uplink voice (an IR seeker went dumb)
  laserPing() {
    this.ensure();
    if (!this.ctx) return;
    const v = this._warble('uplink', WARBLE_SLOW);
    this._warbleSet(v, true, UPLINK_VOL, 0.5);
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
