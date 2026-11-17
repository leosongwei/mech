// The point the machine believes a hull it is tracking is at.
//
// The pilot tracks five hulls at most - the four laser channels his designator
// holds and the candidate his cross has named - and every marker the HUD puts on
// them sits on this point.  While his own sight has the hull the point *is* the
// hull, refreshed every frame; the moment that line is cut - smoke, a crest, a
// stand of trees, a house - it walks at the velocity and the pull last measured
// on the hull, which is the same walk `Systems/Beams.cs` flies a channel on and
// the same shape a top-attack round's `tgt_mem` has.
//
// The mark is the whole point of it: a diamond read straight off the snapshot's
// own row would be the hull itself, through the screen hiding it, and would tell
// the pilot ten metres of truth the weapon he is holding does not get.  What he
// gets here is what the weapon would have.
//
// Both weapons share this one mechanism (`systems/feedback.js` draws the
// candidate and the channels from it): a seeker holds a candidate, a designator
// holds up to four channels, and the belief of a hull is the same thing either
// way.
//
// The pull is trusted for `BELIEF_ACCEL_S` - the second the server's
// `Beams._walk` trusts it too - and the walk stops after `BELIEF_WALK_S`, the
// channel's own memory: past that the point stands still, which is honest.  A
// hull the sight finds again re-acquires on the frame it is clear, so the marker
// snaps back onto the machine instead of easing onto it.

import { BELIEF_ACCEL_S, BELIEF_WALK_S } from '../../core/config.js';
import { sightBlocked } from '../../map/index.js';

export class Beliefs {
  constructor() {
    this.map = new Map();     // hull id -> { x, y, z, vx, vy, vz, ax, az, blind, seen }
  }

  /// The belief for one hull, or null when this machine is not tracking it.
  get(id) {
    return this.map.get(id) || null;
  }

  /// Refresh the hulls the server says are tracked (the candidate and every
  /// channel) and walk the ones the sight has lost.  Runs once a frame, after the
  /// wire has been read and before the HUD is built.
  update(app, own, dt) {
    const tracked = new Set();
    const meta = app.net.meta;
    if (meta) {
      if (meta.lock_target >= 0) tracked.add(meta.lock_target);
      if (meta.locks) for (const l of meta.locks) tracked.add(l.id);
    }
    // a hull the server let go of - dead, out of range, out of the cone - takes
    // its belief with it
    for (const id of this.map.keys()) {
      if (!tracked.has(id)) this.map.delete(id);
    }
    if (!own || !tracked.size) return;

    const eye = app.sightEye(own);
    // the smoke the sight rule cares about: deployed puffs, not canisters
    const puffs = app.net.smoke ? app.net.smoke.filter((s) => !s.canister) : null;
    const units = app.defs.units || {};
    for (const id of tracked) {
      const e = app.net.entities.get(id);
      if (!e) { this.map.delete(id); continue; }
      // the hull's own centre: the point every other part of the game aims at
      const cx = e.x;
      const cy = e.y + ((units[e.type] || {}).height || 3) * 0.5;
      const cz = e.z;
      // one door for "can the pilot see it": the same line of sight the pre-aim
      // box and the seeker go through (terrain, buildings, canopies, smoke)
      const seen = !sightBlocked(app.map, eye[0], eye[1], eye[2], cx, cy, cz, puffs);
      let b = this.map.get(id);
      if (!b) {
        b = { x: cx, y: cy, z: cz, vx: 0, vy: 0, vz: 0, ax: 0, az: 0, blind: 0, seen };
        this.map.set(id, b);
      }
      if (seen) {
        b.x = cx; b.y = cy; b.z = cz;
        b.vx = e.vx || 0; b.vy = e.vy || 0; b.vz = e.vz || 0;
        // the pull `net` measured off two velocity samples, while it is fresh
        const fresh = e.avt !== undefined && app.net.tick - e.avt <= 15;
        b.ax = fresh ? (e.ax || 0) : 0;
        b.az = fresh ? (e.az || 0) : 0;
        b.blind = 0;
      } else {
        b.blind += dt;
        if (b.blind <= BELIEF_WALK_S) {
          b.x += b.vx * dt;
          b.y += b.vy * dt;
          b.z += b.vz * dt;
          if (b.blind <= BELIEF_ACCEL_S) {
            b.vx += b.ax * dt;
            b.vz += b.az * dt;
          }
        }
      }
      b.seen = seen;
    }
  }
}
