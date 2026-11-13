// The cue recipes: what a gunshot, a round landing, a hull going up and a refused
// trigger are each made of.
//
// A cue is a recipe over `synth.js` and nothing else - it opens no bus of its own
// and touches no node the graph owns beyond the ones it hands to `synth` - so the
// mix can be re-tuned here without reading a filter curve, and a new weapon is a
// branch in `shot` rather than a new voice in the toolbox.
//
// `farBoom` lives here, with the recipes, because it is the same *event* heard
// from kilometres away and its weight is a per-cue decision (`shot` weights a
// guided launch at 0.4 and a 105 mm at 1.6).  What belongs to the graph is the
// bed itself: the far bus, its compressor, and the rate limit one bang per
// `FAR_MIN_GAP` that keeps a burst from opening a voice each.

import {
  FAR_FULL, FAR_LEVEL, FAR_NEAR, FAR_ROLLOFF, HEAVY_GAIN, HEAVY_REPORT,
  SOUND_REF_DIST, attenuation, reverberation,
} from './levels.js';
import { blip, noiseBurst, thump } from './synth.js';

// A voice on the far bus, two layers: the body (a slow sine falling 95 -> 42 Hz,
// swelling rather than slamming, because a wavefront that crossed six kilometres
// has no transient left) and the dull edge an octave up that makes it read as a
// report instead of a hum.
//
// Both layers are played at `dist` 0 on purpose: the air filter of the bus is for
// a *near* sample heard through the atmosphere, and this is the
// already-through-the-atmosphere sample.  Filtering it again would take the edge
// off the very band it was built to keep.
//
// What it does not get is a reverb send of its own.  The room is for the near cue
// that arrived *through* the field; this voice is the field's answer already, and
// a convolver input on every distant event is the one thing that turned a battle
// into a wall (see `FAR_CEIL`).  It lands on the far bus instead, which is
// compressed as one bed.
export function farBoom(a, dist, src, weight, pan, behind, tall = 0.55) {
  if (!a.ctx || !a.graph.far) return;
  const fade = Math.max(0, Math.min(1, (dist - FAR_NEAR) / (FAR_FULL - FAR_NEAR)));
  const law = Math.pow(SOUND_REF_DIST / Math.max(SOUND_REF_DIST, dist), FAR_ROLLOFF);
  const g = FAR_LEVEL * src * weight * law * fade * fade;
  if (!(g > 0.0015)) return;
  if (!a.graph.farOpen()) return;
  const dest = a.graph.far;
  thump(a, 95, tall, Math.min(0.9, g), 0, pan, behind,
        { sweepTo: 42, attack: 0.04, dest });
  noiseBurst(a, tall * 0.8, 340, 0.8, Math.min(0.7, g * 0.8), 'lowpass',
             120, 0, null, pan, behind, { dest });
}

// `vol` is the source level (`SOUND_LEVEL`) already multiplied by the distance
// loss, and `dist` is how far the event was from the pilot when the wavefront
// reached them: between them they set the level, the air's lowpass and the reverb
// send.  `pan` / `behind` place it: left-right, and how much of it was at the
// pilot's back (see `graph.bus`).  `wid` rides along on every call and is where a
// per-weapon report would be looked up; `kind` and `heavy` decide the recipe.
export function shot(a, wid, kind, vol = 1, dist = 0, pan = 0, behind = 0, heavy = false) {
  a.ensure();
  if (!a.ctx) return;
  // the slot a voice holds is how long it sounds: a gatling's crack is over in a
  // tenth of a second and a nest of them is thirty a second, so charging them a
  // cannon's 0.8 s would fill the ceiling with clicks and start dropping the
  // explosions three kilometres out
  if (!a.voices.admit(vol, heavy ? 2.0 : kind === 'bullet' ? 0.2 : 0.8)) return;
  if (heavy) {
    // Measured off a recorded M1 (plan_imgs/m1fire.mkv, four rounds, analysed per
    // window - the table is `HEAVY_REPORT` in levels.js):
    //
    //   crack   peaks ~2.5 kHz, 20 ms in, -20 dB inside 100 ms
    //   boom    110-250 Hz, peaks 50-100 ms in, -20 dB only at ~1 s
    //   sub     25-60 Hz, peak ~100 ms in, under the floor
    //   tail    the recording still 10 dB over the silence at 2.5 s
    //
    // The old cue was two layers (a 900 Hz bandpass and a 68 Hz sine) that both
    // slammed at t=0 and were both over in 340 ms: no crack, and the boom in front
    // of the sound it belongs behind.  The gap between crack and bore is most of
    // what reads as heavy, so each layer starts where the recording puts it.  It
    // stops at 2 s rather than the recording's 2.5: at range the reverb send adds
    // the room on top, and a cockpit is not a range.  One round per five seconds,
    // so it never has to stack up the way a burst does - and it is meant to be the
    // loudest thing on the field short of a hull going up, hence the gains four
    // times a gun cue's (the master bus is limited, not clamped).
    const wet = reverberation(dist);
    for (const [dur, f0, f1, q, type, gain, delay, env] of HEAVY_REPORT) {
      noiseBurst(a, dur, f0, q, HEAVY_GAIN * vol * gain, type, f1, dist,
                 0.12 + 0.55 * wet, pan, behind, { delay, env });
    }
    // the weight under the floor: 120 Hz falling to 42, swelling in rather than
    // slamming, which is what the recording does at ~100 ms
    thump(a, 120, 0.9, HEAVY_GAIN * vol * 0.75, dist, pan, behind,
          { delay: 0.03, sweepTo: 42, attack: 0.02, wet: 0.25 + 0.55 * wet });
  } else if (kind === 'bullet') {
    noiseBurst(a, 0.11, 1400 + Math.random() * 500, 1.2, 0.28 * vol, 'bandpass', 400,
               dist, null, pan, behind);
    thump(a, 120, 0.09, 0.25 * vol, dist, pan, behind);
  } else if (kind === 'rocket' || kind === 'saclos' || kind === 'top') {
    noiseBurst(a, 0.75, 700, 0.7, 0.32 * vol, 'lowpass', 220, dist, null, pan, behind);
    thump(a, 80, 0.35, 0.2 * vol, dist, pan, behind);
  } else {
    noiseBurst(a, 0.08, 1800, 1.5, 0.2 * vol, 'bandpass', 600, dist, null, pan, behind);
  }
  // and, far enough out to have lost its crack on the way, the voice that survived
  // the trip.  A guided launch is a whoosh and carries nothing like as far as a
  // gun, hence the weight.
  const guided = kind === 'rocket' || kind === 'saclos' || kind === 'top';
  farBoom(a, dist, vol / attenuation(dist), heavy ? 1.6 : guided ? 0.4 : 1.0,
          pan, behind, heavy ? 0.9 : 0.5);
}

export function boom(a, vol = 1, big = false, dist = 0, pan = 0, behind = 0) {
  a.ensure();
  if (!a.ctx) return;
  if (!a.voices.admit(vol, big ? 1.2 : 0.6)) return;
  noiseBurst(a, big ? 1.1 : 0.5, big ? 420 : 600, 0.8, Math.min(1, 0.5 * vol),
             'lowpass', 90, dist, null, pan, behind);
  thump(a, big ? 55 : 85, big ? 0.9 : 0.4, Math.min(1, 0.5 * vol), dist, pan, behind);
  farBoom(a, dist, vol / attenuation(dist), big ? 1.3 : 1.0, pan, behind,
          big ? 1.2 : 0.6);
}

// A hull going up (defs.DEATH_BLAST).  Not a louder `boom()` - a dead mech is a
// different event, so it gets its own voice: a pressure front, a wall of fire that
// falls away over nearly three seconds, a bass thump under the floor, and a rumble
// that is still rolling when the fireball has gone out.  Four layers timed to
// overlap, which is what makes it read as heavy.
export function explosion(a, vol = 1, dist = 0, pan = 0, behind = 0) {
  a.ensure();
  if (!a.ctx) return;
  if (!a.voices.admit(vol, 4.6)) return;
  const v = Math.min(1, vol);
  const wet = reverberation(dist);
  // the pressure front: a low-mid shove rather than a gunshot's bright crack
  noiseBurst(a, 0.3, 650, 0.5, Math.min(1, 0.55 * v), 'lowpass', 90, dist,
             0.2 + 0.5 * wet, pan, behind);
  // the fire: a broad wall of noise, low-passed hard and swept down
  noiseBurst(a, 2.8, 800, 0.5, Math.min(1, 0.9 * v), 'lowpass', 60, dist,
             0.35 + 0.65 * wet, pan, behind);
  // the kick under the floor: audible bass before the sine dives below it
  thump(a, 58, 2.1, Math.min(1, 0.95 * v), dist, pan, behind);
  // the tail: the valley answering back long after the fire is out
  noiseBurst(a, 4.5, 240, 0.35, Math.min(1, 0.5 * v), 'lowpass', 40, dist,
             0.55 + 0.45 * wet, pan, behind);
  // and the far-field version of the same event: a hull going up is the loudest
  // thing on the field, so it carries further than anything else and rumbles for
  // longer when it gets there
  farBoom(a, dist, vol / attenuation(dist), 1.2, pan, behind, 1.8);
}

// The trigger refused: a pilot pulled it on something that cannot fire.  One short
// buzz, dead centre - it belongs to the machine in his hands, so it has no place
// in the world and no distance.
export function denied(a) {
  a.ensure();
  if (!a.ctx) return;
  blip(a, { type: 'square', f0: 320, f1: 180, sweep: 0.16, gain: 0.06,
            decay: 0.18, dur: 0.19 });
}
