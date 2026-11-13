// Impact effects: dust, sparks, smoke and fire flashes.
//
// Everything is drawn as textured-free billboards built from the same instanced
// primitives as the world, in the blended particle pass after the opaque
// geometry.  The pool owns no drawing: `emit(b)` writes one blended instance per
// live particle into the frame's batch, and the pass that carries them decides
// the GL state.

import { MESH } from '../render/mesh-table.js';

const DUST = [0.56, 0.49, 0.38];
const DUST_LIGHT = [0.68, 0.62, 0.50];
const SMOKE = [0.26, 0.25, 0.24];
const SPARK = [1.0, 0.72, 0.28];
const FIRE = [1.0, 0.55, 0.18];

const MAX_PARTICLES = 900;
// the last moments of a particle's life, faded out by them
const FADE = 0.6;
// how many rounds may be drawing a trail at once before the oldest key is
// dropped - a round's id only lives for seconds, so the oldest is long gone
const TRAIL_KEYS = 256;

// One slot of the pool.  Every field is written by `_add` before the slot is
// shown, so a recycled slot never leaks the particle before it.
function makeSlot() {
  return {
    x: 0, y: 0, z: 0,
    vx: 0, vy: 0, vz: 0,
    life: 0,                    // s of life left; <= 0 is a free slot
    size: 0, grow: 0, drag: 0, grav: 0,
    col: DUST, alpha: 0,
    mesh: MESH.SPHERE,
    prev: -1, next: -1,         // the age list: this slot's neighbours in it
  };
}

export class Particles {
  constructor() {
    // A fixed pool of slots and an age list through them: `_add` writes a dead
    // slot if there is one and takes the *oldest* live one if there is not, so
    // exactly the particles the old `list` + `list.shift()` kept, and exactly the
    // one it dropped when the pool was full - a memmove of 900 pointers replaced
    // by a handful of index writes.  A slot's `life` is its liveness: a dead slot
    // is skipped by `update` and `emit` and reused by the next `_add` that needs
    // one, so the pool is never rebuilt and nothing here allocates a particle.
    this.slots = new Array(MAX_PARTICLES);
    for (let i = 0; i < MAX_PARTICLES; i++) this.slots[i] = makeSlot();
    this.freeList = new Int16Array(MAX_PARTICLES);   // indices of the dead slots
    this.free = 0;                                   // how many of them there are
    this.head = -1;                                  // oldest live slot
    this.tail = -1;                                  // newest live slot
    this.live = 0;
    // the point each round's trail last puffed at, keyed by the caller's name
    // for that round (its id): the spacing is per round, so the clock is too
    this.trails = new Map();
    this.clear();
  }

  // a fresh match: nothing in the air, nothing remembering a trail
  clear() {
    for (let i = 0; i < MAX_PARTICLES; i++) {
      const p = this.slots[i];
      p.life = 0;
      p.prev = -1;
      p.next = -1;
      this.freeList[i] = i;
    }
    this.free = MAX_PARTICLES;
    this.head = -1;
    this.tail = -1;
    this.live = 0;
    this.trails.clear();
  }

  // The one writer of a slot.  Position, velocity, then the body's own numbers:
  // life, size, grow, drag, gravity, colour, alpha, and the primitive it is drawn
  // as (a shard is the only thing that is not a dot).
  _add(px, py, pz, vx, vy, vz, life, size, grow, drag, grav, col, alpha, mesh = MESH.SPHERE) {
    let slot;
    if (this.free > 0) {
      // a slot nothing is using: no particle is lost
      slot = this.freeList[--this.free];
      this.live++;
    } else {
      // the pool is full, so the oldest goes, as `list.shift()` dropped it
      slot = this.head;
      this.head = this.slots[slot].next;
      if (this.head >= 0) this.slots[this.head].prev = -1;
    }
    const p = this.slots[slot];
    p.x = px; p.y = py; p.z = pz;
    p.vx = vx; p.vy = vy; p.vz = vz;
    p.life = life; p.size = size; p.grow = grow;
    p.drag = drag; p.grav = grav;
    p.col = col; p.alpha = alpha; p.mesh = mesh;
    // and it is the newest in the age list, whatever slot it landed in
    p.prev = this.tail;
    p.next = -1;
    if (this.tail >= 0) this.slots[this.tail].next = slot;
    this.tail = slot;
    if (this.head < 0) this.head = slot;
  }

  // a round hit the ground: dirt fountain + a little flash
  impact(x, y, z, radius, big, kind) {
    const n = big ? 12 : 5;
    for (let i = 0; i < n; i++) {
      const a = Math.random() * Math.PI * 2;
      const sp = (0.4 + Math.random() * 1.6) * (big ? 3.5 : 1.6);
      this._add(
        x, y + 0.3, z,
        Math.cos(a) * sp, 1.5 + Math.random() * (big ? 7 : 3), Math.sin(a) * sp,
        0.55 + Math.random() * (big ? 1.1 : 0.6),
        (big ? 1.1 : 0.30) + Math.random() * (big ? 1.8 : 0.55),
        (big ? 6 : 3.4) * (0.6 + Math.random() * 0.8),
        2.2, 5.5,
        Math.random() < 0.5 ? DUST : DUST_LIGHT,
        big ? 0.35 : 0.30,
      );
    }
    // sparks / fire
    const sparks = big ? 10 : 3;
    for (let i = 0; i < sparks; i++) {
      const a = Math.random() * Math.PI * 2;
      const sp = (big ? 8 : 4) * Math.random() + 2;
      this._add(
        x, y + 0.3, z,
        Math.cos(a) * sp, 2 + Math.random() * (big ? 12 : 5), Math.sin(a) * sp,
        0.18 + Math.random() * 0.3,
        0.25 + Math.random() * 0.3,
        -0.1,
        0.6, 14,
        Math.random() < 0.5 ? SPARK : FIRE,
        0.95,
      );
    }
    // brief muzzle-sized flash on the ground
    this._add(
      x, y + 0.4, z,
      0, 0.4, 0,
      big ? 0.22 : 0.12,
      (big ? 3.2 : 1.1) * Math.max(0.6, Math.min(2.5, radius)),
      big ? 9 : 4,
      0, 0,
      [1.0, 0.85, 0.45],
      0.7,
    );
    // smoke lingers on big hits
    if (big) {
      for (let i = 0; i < 6; i++) {
        const a = Math.random() * Math.PI * 2;
        this._add(
          x + Math.cos(a) * Math.random() * 3, y + 1.0, z + Math.sin(a) * Math.random() * 3,
          Math.cos(a) * 0.8, 2.5 + Math.random() * 2, Math.sin(a) * 0.8,
          1.4 + Math.random() * 1.3,
          2.2 + Math.random() * 2.0,
          4.5,
          1.2, -0.4,
          SMOKE,
          0.32,
        );
      }
    }
  }

  // A hull going up (defs.DEATH_BLAST): a fireball that is still there a
  // second later, and a column of smoke that is still there five.  A round
  // impact puffs and is gone before the eye finds it, which is not what a
  // vehicle exploding looks like.
  explosion(x, y, z, radius) {
    // the fireball grows with the blast but with its area, not its radius: a
    // 30 m shock is a wider fire, not a wall of orange across the screen
    const er = Math.max(6, Math.min(40, radius || 15));
    const R = 15 * Math.sqrt(er / 15);
    // the flash: at the hull, swelling rather than snapping open
    this._add(
      x, y + 1.4, z,
      0, 2.0, 0,
      0.5, R * 0.45, R * 0.9,
      0.6, -0.5,
      [1.0, 0.9, 0.55],
      0.9,
    );
    // fire: a ring of it rolling out of the hull, still burning a second later
    for (let i = 0; i < 16; i++) {
      const a = Math.random() * Math.PI * 2;
      const sp = R * (0.12 + Math.random() * 0.28);
      this._add(
        x + Math.cos(a) * R * 0.15, y + 1.0 + Math.random() * 2.5, z + Math.sin(a) * R * 0.15,
        Math.cos(a) * sp, 2.5 + Math.random() * 3.5, Math.sin(a) * sp,
        0.7 + Math.random() * 0.9,
        R * (0.13 + Math.random() * 0.14),
        R * 0.24,
        2.6, -1.2,
        Math.random() < 0.3 ? SPARK : FIRE,
        0.8,
      );
    }
    // the column: dark, slow, and still standing when the fire is out
    for (let i = 0; i < 12; i++) {
      const a = Math.random() * Math.PI * 2;
      this._add(
        x + Math.cos(a) * R * 0.1, y + 1.6 + Math.random() * 2.0, z + Math.sin(a) * R * 0.1,
        Math.cos(a) * 1.4, 3.0 + Math.random() * 3.5, Math.sin(a) * 1.4,
        2.6 + Math.random() * 2.2,
        R * (0.2 + Math.random() * 0.14),
        R * 0.1,
        1.0, -0.7,
        SMOKE,
        0.4,
      );
    }
    // plate and track thrown clear, falling back through the fireball
    for (let i = 0; i < 18; i++) {
      const a = Math.random() * Math.PI * 2;
      const sp = 8 + Math.random() * 16;
      this._add(
        x, y + 1.2, z,
        Math.cos(a) * sp, 6 + Math.random() * 14, Math.sin(a) * sp,
        0.45 + Math.random() * 0.5,
        0.2 + Math.random() * 0.3,
        -0.05,
        0.5, 16,
        Math.random() < 0.5 ? SPARK : FIRE,
        0.95,
      );
    }
  }

  // smoke trail behind a missile (spaced out, one puff every few metres).
  // `key` is the caller's name for the round - its id - and it is an argument
  // because the spacing is per round: a map of last points belongs to whoever
  // knows which round it is watching, not to a scratch field here.
  trail(key, x, y, z, spacing) {
    const gap = spacing || 6;
    const last = this.trails.get(key);
    if (last) {
      const d = Math.hypot(x - last[0], y - last[1], z - last[2]);
      if (d < gap) return;
    } else if (this.trails.size >= TRAIL_KEYS) {
      this.trails.delete(this.trails.keys().next().value);
    }
    this.trails.set(key, [x, y, z]);
    this._add(
      x, y, z,
      (Math.random() - 0.5) * 0.4, 0.3 + Math.random() * 0.3, (Math.random() - 0.5) * 0.4,
      0.7 + Math.random() * 0.4,
      0.55,
      2.0,
      1.8, -0.15,
      [0.76, 0.74, 0.72],
      0.22,
    );
  }

  // a lazy puff off a burning wreck
  smoke(x, y, z, size, alpha) {
    this._add(
      x, y, z,
      (Math.random() - 0.5) * 0.35, 1.1 + Math.random() * 0.7, (Math.random() - 0.5) * 0.35,
      2.0 + Math.random() * 0.9,
      size * (0.45 + Math.random() * 0.25),
      size * (0.75 + Math.random() * 0.35),
      1.1, -0.5,
      SMOKE,
      alpha === undefined ? 0.3 : alpha,
    );
  }

  // the pool ages in place: a survivor keeps its slot, a dead one leaves the age
  // list and goes back on the free stack for the next `_add`
  update(dt) {
    let live = 0;
    for (let i = 0; i < MAX_PARTICLES; i++) {
      const p = this.slots[i];
      if (p.life <= 0) continue;
      p.life -= dt;
      if (p.life <= 0) {
        // out of the age list
        if (p.prev >= 0) this.slots[p.prev].next = p.next; else this.head = p.next;
        if (p.next >= 0) this.slots[p.next].prev = p.prev; else this.tail = p.prev;
        p.prev = -1; p.next = -1;
        this.freeList[this.free++] = i;
        continue;
      }
      const k = Math.exp(-p.drag * dt);
      p.vx *= k;
      p.vz *= k;
      p.vy = p.vy * k - p.grav * dt;
      p.x += p.vx * dt;
      p.y += p.vy * dt;
      p.z += p.vz * dt;
      p.size = Math.max(0.05, p.size + p.grow * dt);
      live++;
    }
    this.live = live;
  }

  // every live particle as one blended instance, into the frame's batch
  emit(b) {
    for (let i = 0; i < MAX_PARTICLES; i++) {
      const p = this.slots[i];
      if (p.life <= 0) continue;
      const a = Math.max(0, Math.min(1, p.life / FADE)) * p.alpha;
      if (a <= 0.01) continue;
      const c = p.col;
      if (p.mesh === MESH.CONE) {
        // a shard is the same shape the cone draw was: 1.6x taller than wide
        b.putA(MESH.CONE, p.x, p.y, p.z, 0, 0, p.size, p.size * 1.6, p.size, c[0], c[1], c[2], a);
      } else {
        b.putA(MESH.SPHERE, p.x, p.y, p.z, 0, 0, p.size, p.size, p.size, c[0], c[1], c[2], a);
      }
    }
  }
}
