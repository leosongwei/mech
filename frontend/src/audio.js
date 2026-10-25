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

export class GameAudio {
  constructor() {
    this.ctx = null;
    this.noise = null;
    this.master = null;
    this.lockOsc = null;
    this.lockGain = null;
    this.warbles = {};
    this.beepTimer = 0;
    this.mode = 'none';
  }

  ensure() {
    if (this.ctx) return;
    const AC = window.AudioContext || window.webkitAudioContext;
    if (!AC) return;
    this.ctx = new AC();
    this.master = this.ctx.createGain();
    this.master.gain.value = 0.55;
    this.master.connect(this.ctx.destination);
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

  _noiseBurst(dur, freq, q, gain, type, sweepTo) {
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
    src.connect(f); f.connect(g); g.connect(this.master);
    src.start(t, Math.random() * 0.4);
    src.stop(t + dur + 0.05);
  }

  _thump(freq, dur, gain) {
    if (!this.ctx) return;
    const t = this.ctx.currentTime;
    const o = this.ctx.createOscillator();
    o.type = 'sine';
    o.frequency.setValueAtTime(freq, t);
    o.frequency.exponentialRampToValueAtTime(freq * 0.35, t + dur);
    const g = this.ctx.createGain();
    g.gain.setValueAtTime(gain, t);
    g.gain.exponentialRampToValueAtTime(0.001, t + dur);
    o.connect(g); g.connect(this.master);
    o.start(t); o.stop(t + dur + 0.02);
  }

  shot(wid, kind, vol = 1) {
    this.ensure();
    if (!this.ctx) return;
    if (kind === 'bullet') {
      this._noiseBurst(0.11, 1400 + Math.random() * 500, 1.2, 0.28 * vol, 'bandpass', 400);
      this._thump(120, 0.09, 0.25 * vol);
    } else if (kind === 'rocket' || kind === 'saclos' || kind === 'top') {
      this._noiseBurst(0.75, 700, 0.7, 0.32 * vol, 'lowpass', 220);
      this._thump(80, 0.35, 0.2 * vol);
    } else {
      this._noiseBurst(0.08, 1800, 1.5, 0.2 * vol, 'bandpass', 600);
    }
  }

  boom(vol = 1, big = false) {
    this.ensure();
    if (!this.ctx) return;
    this._noiseBurst(big ? 1.1 : 0.5, big ? 420 : 600, 0.8, Math.min(1, 0.5 * vol), 'lowpass', 90);
    this._thump(big ? 55 : 85, big ? 0.9 : 0.4, Math.min(1, 0.5 * vol));
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
      o.connect(lp); lp.connect(g); g.connect(this.master);
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
  // times slower - unmistakable next to the missile warble.
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
