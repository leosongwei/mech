// Impact effects: dust, sparks, smoke and fire flashes.
//
// Everything is drawn as textured-free billboards built from the same
// instanced primitives, in a blended pass after the opaque geometry.

const DUST = [0.56, 0.49, 0.38];
const DUST_LIGHT = [0.68, 0.62, 0.50];
const SMOKE = [0.26, 0.25, 0.24];
const SPARK = [1.0, 0.72, 0.28];
const FIRE = [1.0, 0.55, 0.18];

const MAX_PARTICLES = 900;

export class Particles {
  constructor() {
    this.list = [];
  }

  _add(p) {
    if (this.list.length >= MAX_PARTICLES) this.list.shift();
    this.list.push(p);
  }

  // a round hit the ground: dirt fountain + a little flash
  impact(x, y, z, radius, big, kind) {
    const n = big ? 12 : 5;
    for (let i = 0; i < n; i++) {
      const a = Math.random() * Math.PI * 2;
      const sp = (0.4 + Math.random() * 1.6) * (big ? 3.5 : 1.6);
      this._add({
        x, y: y + 0.3, z,
        vx: Math.cos(a) * sp,
        vy: 1.5 + Math.random() * (big ? 7 : 3),
        vz: Math.sin(a) * sp,
        life: 0.55 + Math.random() * (big ? 1.1 : 0.6),
        max: 1,
        size: (big ? 1.1 : 0.30) + Math.random() * (big ? 1.8 : 0.55),
        grow: (big ? 6 : 3.4) * (0.6 + Math.random() * 0.8),
        drag: 2.2,
        col: Math.random() < 0.5 ? DUST : DUST_LIGHT,
        alpha: big ? 0.35 : 0.30,
        grav: 5.5,
        mesh: 'sphere',
      });
    }
    // sparks / fire
    const sparks = big ? 10 : 3;
    for (let i = 0; i < sparks; i++) {
      const a = Math.random() * Math.PI * 2;
      const sp = (big ? 8 : 4) * Math.random() + 2;
      this._add({
        x, y: y + 0.3, z,
        vx: Math.cos(a) * sp,
        vy: 2 + Math.random() * (big ? 12 : 5),
        vz: Math.sin(a) * sp,
        life: 0.18 + Math.random() * 0.3,
        max: 1,
        size: 0.25 + Math.random() * 0.3,
        grow: -0.1,
        drag: 0.6,
        col: Math.random() < 0.5 ? SPARK : FIRE,
        alpha: 0.95,
        grav: 14,
        mesh: 'sphere',
      });
    }
    // brief muzzle-sized flash on the ground
    this._add({
      x, y: y + 0.4, z,
      vx: 0, vy: 0.4, vz: 0,
      life: big ? 0.22 : 0.12,
      max: 1,
      size: (big ? 3.2 : 1.1) * Math.max(0.6, Math.min(2.5, radius)),
      grow: big ? 9 : 4,
      drag: 0,
      col: [1.0, 0.85, 0.45],
      alpha: 0.7,
      grav: 0,
      mesh: 'sphere',
    });
    // smoke lingers on big hits
    if (big) {
      for (let i = 0; i < 6; i++) {
        const a = Math.random() * Math.PI * 2;
        this._add({
          x: x + Math.cos(a) * Math.random() * 3,
          y: y + 1.0,
          z: z + Math.sin(a) * Math.random() * 3,
          vx: Math.cos(a) * 0.8,
          vy: 2.5 + Math.random() * 2,
          vz: Math.sin(a) * 0.8,
          life: 1.4 + Math.random() * 1.3,
          max: 1,
          size: 2.2 + Math.random() * 2.0,
          grow: 4.5,
          drag: 1.2,
          col: SMOKE,
          alpha: 0.32,
          grav: -0.4,
          mesh: 'sphere',
        });
      }
    }
  }

  // smoke trail behind a missile (spaced out, one puff every few metres)
  trail(x, y, z, spacing) {
    spacing = spacing || 6;
    const last = this.lastTrail;
    if (last) {
      const d = Math.hypot(x - last[0], y - last[1], z - last[2]);
      if (d < spacing) return;
    }
    this.lastTrail = [x, y, z];
    this._add({
      x, y, z,
      vx: (Math.random() - 0.5) * 0.4,
      vy: 0.3 + Math.random() * 0.3,
      vz: (Math.random() - 0.5) * 0.4,
      life: 0.7 + Math.random() * 0.4,
      max: 1,
      size: 0.55,
      grow: 2.0,
      drag: 1.8,
      col: [0.76, 0.74, 0.72],
      alpha: 0.22,
      grav: -0.15,
      mesh: 'sphere',
    });
  }

  update(dt) {
    const out = [];
    for (const p of this.list) {
      p.life -= dt;
      if (p.life <= 0) continue;
      const k = Math.exp(-p.drag * dt);
      p.vx *= k;
      p.vz *= k;
      p.vy = p.vy * k - p.grav * dt;
      p.x += p.vx * dt;
      p.y += p.vy * dt;
      p.z += p.vz * dt;
      p.size = Math.max(0.05, p.size + p.grow * dt);
      out.push(p);
    }
    this.list = out;
  }

  render(R) {
    R.beginParticles();
    for (const p of this.list) {
      const a = Math.max(0, Math.min(1, p.life / 0.6)) * p.alpha;
      if (a <= 0.01) continue;
      if (p.mesh === 'sphere') {
        R.sphereA(p.x, p.y, p.z, 0, 0, p.size, p.size, p.size, p.col[0], p.col[1], p.col[2], a);
      } else {
        R.coneA(p.x, p.y, p.z, 0, 0, p.size, p.size * 1.6, p.size, p.col[0], p.col[1], p.col[2], a);
      }
    }
  }
}
