// The socket and the clock: connect, dispatch every frame the server sends,
// measure the round trip, and hand the picture to the view.
//
// No bytes are decoded here - `wire` reads the envelope, `rows` the layouts,
// `view` owns the state - so this file is the object the app talks to: the JSON
// messages, the ack that goes back on the input, and the smoothed RTT / TPS / loss
// the HUD draws.

import { TICK_HZ } from '../core/config.js';
import { decodeFx, isFxFrame, isPingFrame, readClock, setBuffer } from './wire.js';
import { View } from './view.js';

export class Client {
  constructor(url) {
    this.url = url;
    this.ws = null;
    this.view = new View();
    this.meta = null;
    this.init = null;
    this.onFx = null;
    this.onMessage = null;
    this.echo = -1;             // the `st` the server last echoed: RTT is timed
                                // from the moment that value first went out
    this.lastAckSent = -1;
    this.ackSentAt = 0;
    this.rttMs = NaN;           // smoothed round trip, ms (see `_clock`)
    this.tps = 0;               // the server's own tick rate, last second
    this.loss = 0;              // % of this pilot's snapshots the server did not
                                // send, last second (a hold reads 100)
    this.lastSnap = -1;         // tick of the newest snapshot: a clock block
                                // arriving well after one means a hold
    this.held = false;          // the server is only sending clock blocks
    this.ready = false;
  }

  // The picture, by the names the rest of the client already reads.
  get entities() { return this.view.entities; }
  get projectiles() { return this.view.projectiles; }
  get smoke() { return this.view.smoke; }
  get smokeBy() { return this.view.smokeBy; }
  get contacts() { return this.view.contacts; }
  get seq() { return this.view.seq; }
  get tick() { return this.view.tick; }

  connect() {
    return new Promise((resolve, reject) => {
      const ws = new WebSocket(this.url);
      ws.binaryType = 'arraybuffer';
      this.ws = ws;
      ws.onopen = () => { };
      ws.onerror = (e) => reject(e);
      ws.onclose = () => { this.ready = false; };
      ws.onmessage = (ev) => {
        if (typeof ev.data === 'string') {
          const msg = JSON.parse(ev.data);
          if (msg.t === 'init') {
            this.init = msg;
            this.ready = true;
            resolve(msg);
          } else if (msg.t === 'meta') {
            this.meta = msg;
          } else if (msg.t === 'fx') {
            if (this.onFx) this.onFx(msg.events);
          } else if (this.onMessage) {
            this.onMessage(msg);
          }
        } else if (isFxFrame(ev.data)) {
          const evs = decodeFx(ev.data);
          if (this.onFx) this.onFx(evs);
        } else {
          this._snapshot(ev.data);
        }
      };
    });
  }

  /// The `st` to put in this frame's input: the newest tick seen, timestamped
  /// the first time it is reported (that is the moment the round trip starts).
  ack() {
    if (this.tick !== this.lastAckSent) {
      this.lastAckSent = this.tick;
      this.ackSentAt = performance.now();
    }
    return this.tick;
  }

  send(obj) {
    if (this.ws && this.ws.readyState === 1) this.ws.send(JSON.stringify(obj));
  }

  // smooth the render state towards the last snapshot
  interpolate(alpha) {
    this.view.interpolate(alpha);
  }

  _snapshot(buf) {
    const dv = setBuffer(buf);
    this._clock(dv);
    // A clock block (16 B) is what every non-snapshot tick carries, and the only
    // thing a jammed connection gets: the clock moves, the field does not.
    // Everything the client had - entities, projectiles, smoke, contacts - stays
    // exactly as it was, and the ack it sends back is how the server learns it can
    // resume.  A *run* of them with no snapshot in between is a hold - the server
    // is skipping snapshots on purpose - and that is what the HUD colours.  A
    // healthy link gets one every second tick, so four clocks in a row is already
    // past the cadence.
    if (isPingFrame(buf)) {
      if (this.lastSnap >= 0 && this.tick - this.lastSnap > 4) this.held = true;
      return;
    }
    this.held = false;
    this.lastSnap = this.tick;
    this.view.absorb(buf);
  }

  // The clock block every binary frame starts with: tick, the echo of our own
  // ack, the server's tick rate and this pilot's loss.  Reading it is also where
  // the round trip is measured.
  _clock(dv) {
    const c = readClock(dv);
    this.view.tick = c.tick;
    this.echo = c.echo;
    this.tps = c.tps;
    this.loss = c.loss;
    if (this.echo === this.lastAckSent && this.echo >= 0) {
      // `(the moment that ack first went out -> now)` is the round trip plus one
      // quantization: the server reads the ack as it arrives but can only echo
      // it on its *next* frame, i.e. a wait uniform over one tick, half of it on
      // average (our own side adds nothing - the stamp is taken as the input goes
      // out).  So half a tick comes back off.  The sample may still be slightly
      // negative on a link faster than that, which is fine: it is smoothed signed
      // and clamped where it is drawn.
      const sample = performance.now() - this.ackSentAt - 500 / TICK_HZ;
      if (Number.isNaN(this.rttMs)) this.rttMs = sample;
      else this.rttMs += (sample - this.rttMs) * 0.15;
    }
  }
}
