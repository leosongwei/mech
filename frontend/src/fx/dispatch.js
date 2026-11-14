// The one reader of the server's event stream.
//
// `net` decodes an `fx` message - the JSON dictionary of the rare events and the
// binary `MECF` rows the shots and bursts ride (`net/wire.js`) - into plain event
// objects and hands the array here.  Nothing else in the client looks at a burst,
// a shot, a kill, a capture, a re-lock or a repainted tile: one handler per kind,
// and a table saying which, so the eleven-branch `if`/`else` chain the old frame
// carried is now data.
//
// Inside a branch the order of the effects is the order the pilot perceives:
// the particles and the kick are immediate (light and physics), the *bang* is
// not - an event goes into the sound field with the closure that would play it,
// and `audio.field.update` runs that closure when the wavefront reaches the
// pilot, wherever he has driven by then (`audio/field.js`).

import { SOUND_LEVEL } from '../audio/levels.js';
import { SHAKE_BOOM_BIG, SHAKE_BOOM_SMALL, SHAKE_GUN, SHAKE_HEAVY, SHAKE_LAUNCH,
         SHAKE_MAX } from '../core/config.js';
import { clamp } from '../core/math.js';
import { applyPaint } from '../map/index.js';

// The side this pilot joined: the server repeats it in every meta, and before the
// first meta arrives the init payload's own one is the answer.
function myFaction(app) {
  const m = app.net.meta;
  return m && m.faction !== undefined ? m.faction : app.net.init.faction;
}

// the pilot's own pid, the number every per-pilot event is addressed by
function myPid(app) {
  return app.net.init.pid;
}

// A round landing - or a hull going up, which is the same event with `blast` set
// and a much larger radius.
function boom(app, ev) {
  const wd = app.defs.weapons[ev.wid] || {};
  // a round shot down (`small`) is the round breaking up, not its warhead
  // going off: it keeps the little burst the server sized for it and
  // loses the big-hit treatment the weapon's own `er` would have earned
  const big = !ev.small && (!!ev.big || (wd.er || 0) >= 5);
  // the flash and the kick are light and physics: immediate, the bang
  // comes later
  app.particles.impact(ev.x, ev.y, ev.z, ev.r || 1, big, wd.kind);
  // a destroyed hull is not a big round hit: it gets a fireball of its
  // own on top, and it is felt from further out
  if (ev.blast) app.particles.explosion(ev.x, ev.y, ev.z, ev.r || 15);
  const own = app.ownEntity();
  const d = own ? Math.hypot(ev.x - own.x, ev.z - own.z) : 3000;
  app.shake = Math.min(1.2, app.shake + clamp(1 - d / (ev.blast ? 500 : 300), 0, 1)
    * (big ? SHAKE_BOOM_BIG : SHAKE_BOOM_SMALL) * (ev.blast ? 1.5 : 1));
  const level = ev.blast ? SOUND_LEVEL.blast
                         : (big ? SOUND_LEVEL.big : SOUND_LEVEL.boom);
  app.audio.field.push(ev.x, ev.y, ev.z, level, (vol, dist, pan, behind) => (ev.blast
    ? app.audio.explosion(vol, dist, pan, behind)
    : app.audio.boom(vol, big, dist, pan, behind)));
}

// a missile was shot down: a few sparks where it broke up, for everyone
// who can see it, and no cue of any kind beyond them - the battle's AI
// crews shoot missiles down all over the map and a chime per round was
// the busiest sound in it.  `pid` / `firer` still travel (the crews it
// concerns are the ones that can read them off `__game`).
function intercept(app, ev) {
  app.particles.impact(ev.x, ev.y, ev.z, 1);
}

// A round leaving a tube: a muzzle flash on the firing hull, and a kick if it is
// the pilot's own machine.
function shot(app, ev) {
  if (ev.eid !== undefined) app.flashes.set(ev.eid, { t: 0.07, arm: ev.arm });
  const wd = app.defs.weapons[ev.wid] || {};
  const kind = wd.kind || 'bullet';
  // the hull kicks with its own guns: a missile leaves the tube with a
  // shove, a 105 mm round shoves it almost as hard (and is fired five
  // seconds apart, so it never stacks into a buzz), a cannon just rocks
  // the machine
  const own = app.ownEntity();
  if (own && ev.eid === own.id) {
    const guided = kind === 'rocket' || kind === 'saclos' || kind === 'top';
    app.shake = Math.min(guided ? 1.2 : SHAKE_MAX, app.shake
      + (guided ? SHAKE_LAUNCH : wd.heavy ? SHAKE_HEAVY : SHAKE_GUN));
  }
  app.audio.field.push(ev.x, ev.y, ev.z, SOUND_LEVEL.shot,
    (vol, dist, pan, behind) =>
      app.audio.shot(ev.wid, kind, vol, dist, pan, behind, !!wd.heavy));
}

// A kill line, the way every shooter writes one: the words are composed on the
// server (it is the only side that knows who a nameless hull was) and painted in
// the message stack, in the two sides' own colours
function killfeed(app, ev) {
  app.killfeed(ev);
}

// the till, and only the till: the words are the kill feed's line, and a
// kill it stays silent about (an infantryman) is money all the same.  No
// tone - a payout is read, not heard.
function kill(app, ev) {
  if (ev.pid === myPid(app)) app.moneyGain(ev.reward);
}

// an IR missile lost its lock: its line of sight is full of smoke or a
// ridge.  The pilot is told nothing - a blind seeker is not something
// the hull can observe (a blinded round flies the same profile a guided
// one does, `Seeker.cs`), and the battle's AI crews lose locks all over
// the map.  Kept on the game object so a console script can read the
// run back; the range's tests read the server log (`mech.smoke`).
function seeker(app, ev) {
  const rec = { t: Math.round(performance.now()), dist: ev.dist, mech: ev.tid,
                why: ev.why, x: ev.x, y: ev.y, z: ev.z };
  app.seekerLog.push(rec);
  if (app.seekerLog.length > 64) app.seekerLog.shift();
}

// A town changed hands.  The event carries the side that now holds it, so the
// same capture reads as `TOWN SECURED` on one side of the lane and `TOWN LOST` on
// the other.
function capture(app, ev) {
  const mine = ev.faction === myFaction(app);
  app.message(mine ? 'TOWN SECURED' : 'TOWN LOST', mine ? '#7ce0a0' : '#ff9a9a');
}

function respawn(app, ev) {
  if (ev.pid === myPid(app)) app.message('RE-DEPLOYED', '#a9f0c0');
}

// the wrench is out of the machine: rounds and armour are back
function rearm(app, ev) {
  if (ev.pid === myPid(app)) app.message(`${ev.label || 'REARM'} COMPLETE`, '#a9f0c0');
}

function gameover(app, ev) {
  app.winner = ev.winner;
}

// the debug tree planter painted tiles on the server: copy the same
// rectangle into our copy of the map and redraw what comes from it, so
// the stand that hides the target is a stand we can see
function tiles(app, ev) {
  if (applyPaint(app.map, ev) && app.renderer) app.renderer.repaintTiles(app.map);
}

const HANDLERS = {
  boom, intercept, shot, killfeed, kill, seeker, capture, respawn, rearm, gameover, tiles,
};

// The frame's fx stage: `net.onFx` points here, and an event kind the client does
// not know is skipped in silence (an older client, a newer server).
export function onFx(app, events) {
  for (const ev of events) {
    const h = HANDLERS[ev.k];
    if (h) h(app, ev);
  }
}

// One puff every few metres of flight, per round, laid down where the nozzle is -
// the model hangs its flame 2.4 m behind the round's centre along the flight
// path, so the plume has to start there too.  The spacing is the round's own:
// `particles.trail` keeps the last point per key and answers nothing for a round
// that has not covered the gap yet.
export function missileTrails(app) {
  const P = app.particles;
  for (const p of app.net.projectiles) {
    if (p.kind === 0) continue;
    const sp = Math.hypot(p.vx, p.vy, p.vz) || 1;
    const k = 2.4 / sp;
    P.trail(p.id, p.x - p.vx * k, p.y - p.vy * k, p.z - p.vz * k, 7);
  }
}

// burning hulls smoke until the server frees them.  Each wreck keeps its own
// interval, so a field of them does not puff in step.
export function wreckSmoke(app, dt) {
  for (const e of app.net.entities.values()) {
    if (e.type !== 7) continue;
    const kind = (e.flags >> 4) & 3;
    if (kind === 0) continue;
    let t = (app.wreckPuffs.get(e.id) || Math.random() * 0.2) - dt;
    if (t <= 0) {
      t = 0.12 + Math.random() * 0.08;
      const h = kind === 2 ? 2.2 : 1.8;
      app.particles.smoke(e.x + (Math.random() - 0.5) * 0.8, e.y + h,
        e.z + (Math.random() - 0.5) * 0.8, 0.9 + Math.random() * 0.5, 0.28);
    }
    app.wreckPuffs.set(e.id, t);
  }
  // a wreck that has been freed leaves its timer behind; a battle recycles those
  // ids, and the map is only swept once it has grown past the field's own size
  if (app.wreckPuffs.size > 64) {
    for (const id of app.wreckPuffs.keys()) {
      if (!app.net.entities.has(id)) app.wreckPuffs.delete(id);
    }
  }
}
