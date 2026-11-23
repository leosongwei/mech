// The primitives, and what each one is.
//
// Everything drawn in the world is one of eleven unit-sized shapes: a hull is a
// box, a wheel a sphere, a barrel a cylinder, a boulder a rock. A primitive is
// a three geometry plus the two facts the body program needs to know before it
// can shade one - whether it paints house walls, whether it is lit at all - and
// the capacity its instance buffer is allocated at.
//
// The geometry is authored here rather than taken from three's stock shapes
// because the polygons are hand-tuned: a twelve by eight sphere, six-sided
// rocket cells, a nose that comes to a line instead of a point. They all fit the
// unit cube, so one instance matrix scales, rotates and places them.

import { BufferGeometry, BufferAttribute } from 'three';

// A vertex sink: `p` is a position in the unit cube, `n` its normal. Flat faces
// pass the same normal for all their corners; the builders below work out what
// that normal is.
class Mesh {
  constructor() {
    this.pos = [];
    this.nrm = [];
  }

  vertex(p, n) {
    this.pos.push(p[0], p[1], p[2]);
    this.nrm.push(n[0], n[1], n[2]);
  }

  // One flat face, wound so its normal points away from the centre of the unit
  // block, and flat shaded from that winding.
  face(pts) {
    const [a, b, c] = pts;
    const d = pts.length > 3 ? pts[3] : c;
    const u = [b[0] - a[0], b[1] - a[1], b[2] - a[2]];
    const w = [d[0] - a[0], d[1] - a[1], d[2] - a[2]];
    let n = [u[1] * w[2] - u[2] * w[1], u[2] * w[0] - u[0] * w[2], u[0] * w[1] - u[1] * w[0]];
    const mid = pts.reduce((s, p) => [s[0] + p[0], s[1] + p[1], s[2] + p[2]], [0, 0, 0]);
    if (n[0] * mid[0] + n[1] * mid[1] + n[2] * mid[2] < 0) n = [-n[0], -n[1], -n[2]];
    n = norm(n);
    this.vertex(a, n);
    this.vertex(b, n);
    this.vertex(c, n);
    if (pts.length > 3) {
      this.vertex(a, n);
      this.vertex(c, n);
      this.vertex(d, n);
    }
  }

  build() {
    const g = new BufferGeometry();
    g.setAttribute('position', new BufferAttribute(new Float32Array(this.pos), 3));
    g.setAttribute('normal', new BufferAttribute(new Float32Array(this.nrm), 3));
    g.computeBoundingSphere();
    return g;
  }
}

function norm(v) {
  const l = Math.hypot(v[0], v[1], v[2]) || 1;
  return [v[0] / l, v[1] / l, v[2] / l];
}

// The unit cube, centred on the origin: hulls, crates, smoke canisters, and the
// facade mesh, which is the same cube with a wall pattern painted on by the
// fragment shader.
function box() {
  const m = new Mesh();
  const faces = [
    [[0, 0, 1], [1, 0, 0], [0, 1, 0]],
    [[0, 0, -1], [-1, 0, 0], [0, 1, 0]],
    [[1, 0, 0], [0, 0, -1], [0, 1, 0]],
    [[-1, 0, 0], [0, 0, 1], [0, 1, 0]],
    [[0, 1, 0], [1, 0, 0], [0, 0, -1]],
    [[0, -1, 0], [1, 0, 0], [0, 0, 1]],
  ];
  const corners = [[-1, -1], [1, -1], [1, 1], [-1, -1], [1, 1], [-1, 1]];
  for (const [n, u, w] of faces) {
    for (const [cu, cw] of corners) {
      m.vertex([
        n[0] * 0.5 + u[0] * cu * 0.5 + w[0] * cw * 0.5,
        n[1] * 0.5 + u[1] * cu * 0.5 + w[1] * cw * 0.5,
        n[2] * 0.5 + u[2] * cu * 0.5 + w[2] * cw * 0.5,
      ], n);
    }
  }
  return m.build();
}

// A sphere of radius 0.5. The smoke variant is rounder: the pancake puffs are
// metres across and sit right in front of the camera, where a twelve by eight
// sphere reads as a dodecagon and the silhouette gives the wall away.
function sphere(SEG, RING) {
  const m = new Mesh();
  const at = (phi, th) => {
    const n = [Math.sin(phi) * Math.cos(th), Math.cos(phi), Math.sin(phi) * Math.sin(th)];
    return { p: [n[0] * 0.5, n[1] * 0.5, n[2] * 0.5], n };
  };
  for (let r = 0; r < RING; r++) {
    const p0 = Math.PI * (r / RING), p1 = Math.PI * ((r + 1) / RING);
    for (let s = 0; s < SEG; s++) {
      const a0 = (s / SEG) * Math.PI * 2, a1 = ((s + 1) / SEG) * Math.PI * 2;
      const A = at(p0, a0), B = at(p1, a0), C = at(p1, a1), D = at(p0, a1);
      m.vertex(A.p, A.n); m.vertex(B.p, B.n); m.vertex(C.p, C.n);
      m.vertex(A.p, A.n); m.vertex(C.p, C.n); m.vertex(D.p, D.n);
    }
  }
  return m.build();
}

// A cone: base radius 0.5 at y = -0.5, apex at y = +0.5.
function cone(SEG) {
  const m = new Mesh();
  const apex = [0, 0.5, 0];
  for (let s = 0; s < SEG; s++) {
    const a0 = (s / SEG) * Math.PI * 2, a1 = ((s + 1) / SEG) * Math.PI * 2;
    const p0 = [Math.cos(a0) * 0.5, -0.5, Math.sin(a0) * 0.5];
    const p1 = [Math.cos(a1) * 0.5, -0.5, Math.sin(a1) * 0.5];
    const n0 = norm([Math.cos(a0), 0.7, Math.sin(a0)]);
    const n1 = norm([Math.cos(a1), 0.7, Math.sin(a1)]);
    const na = norm([(n0[0] + n1[0]) * 0.5, 0.7, (n0[2] + n1[2]) * 0.5]);
    m.vertex(p0, n0); m.vertex(p1, n1); m.vertex(apex, na);
    m.vertex(p0, [0, -1, 0]); m.vertex(p1, [0, -1, 0]); m.vertex([0, -0.5, 0], [0, -1, 0]);
  }
  return m.build();
}

// A cylinder: radius 0.5, height 1 along Y.
function cylinder(SEG) {
  const m = new Mesh();
  for (let s = 0; s < SEG; s++) {
    const a0 = (s / SEG) * Math.PI * 2, a1 = ((s + 1) / SEG) * Math.PI * 2;
    const p0 = [Math.cos(a0) * 0.5, -0.5, Math.sin(a0) * 0.5];
    const p1 = [Math.cos(a1) * 0.5, -0.5, Math.sin(a1) * 0.5];
    const q0 = [p0[0], 0.5, p0[2]];
    const q1 = [p1[0], 0.5, p1[2]];
    const n0 = norm([Math.cos(a0), 0, Math.sin(a0)]);
    const n1 = norm([Math.cos(a1), 0, Math.sin(a1)]);
    m.vertex(p0, n0); m.vertex(p1, n1); m.vertex(q1, n1);
    m.vertex(p0, n0); m.vertex(q1, n1); m.vertex(q0, n0);
    m.vertex(q0, [0, 1, 0]); m.vertex(q1, [0, 1, 0]); m.vertex([0, 0.5, 0], [0, 1, 0]);
    m.vertex(p0, [0, -1, 0]); m.vertex(p1, [0, -1, 0]); m.vertex([0, -0.5, 0], [0, -1, 0]);
  }
  return m.build();
}

// A four-sided pyramid: base radius 0.5 at y = -0.5, apex at y = +0.5. Six
// triangles, which is all a tracer needs - the streak is stretched along its
// flight path and drawn unlit, so the facets only show in the silhouette.
function pyramid() {
  const m = new Mesh();
  const apex = [0, 0.5, 0];
  const corner = (i) => {
    const a = (i / 4) * Math.PI * 2;
    return [Math.cos(a) * 0.5, -0.5, Math.sin(a) * 0.5];
  };
  for (let i = 0; i < 4; i++) {
    const p0 = corner(i), p1 = corner(i + 1);
    const u = [p1[0] - p0[0], p1[1] - p0[1], p1[2] - p0[2]];
    const w = [apex[0] - p0[0], apex[1] - p0[1], apex[2] - p0[2]];
    const n = norm([u[1] * w[2] - u[2] * w[1], u[2] * w[0] - u[0] * w[2], u[0] * w[1] - u[1] * w[0]]);
    m.vertex(p0, n); m.vertex(p1, n); m.vertex(apex, n);
  }
  const a = corner(0), b = corner(1), c = corner(2), d = corner(3);
  m.vertex(a, [0, -1, 0]); m.vertex(b, [0, -1, 0]); m.vertex(c, [0, -1, 0]);
  m.vertex(a, [0, -1, 0]); m.vertex(c, [0, -1, 0]); m.vertex(d, [0, -1, 0]);
  return m.build();
}

// A boulder: an icosahedron with its vertices pushed in and out at random and
// flat shaded per face, so every rock shows a handful of straight facets
// instead of reading as a ball.
function rock() {
  const m = new Mesh();
  const t = (1 + Math.sqrt(5)) / 2;
  const corners = [
    [-1, t, 0], [1, t, 0], [-1, -t, 0], [1, -t, 0],
    [0, -1, t], [0, 1, t], [0, -1, -t], [0, 1, -t],
    [t, 0, -1], [t, 0, 1], [-t, 0, -1], [-t, 0, 1],
  ];
  const jitter = [0.92, 1.08, 0.84, 1.14, 0.96, 1.02, 0.88, 1.06, 1.0, 0.9, 1.12, 0.95];
  const verts = corners.map((p, i) => {
    const l = Math.hypot(p[0], p[1], p[2]) || 1;
    const s = (0.5 * jitter[i]) / l;
    // squashed on Y: a boulder sits wide and low
    return [p[0] * s, p[1] * s * 0.82, p[2] * s];
  });
  const faces = [
    [0, 11, 5], [0, 5, 1], [0, 1, 7], [0, 7, 10], [0, 10, 11],
    [1, 5, 9], [5, 11, 4], [11, 10, 2], [10, 7, 6], [7, 1, 8],
    [3, 9, 4], [3, 4, 2], [3, 2, 6], [3, 6, 8], [3, 8, 9],
    [4, 9, 5], [2, 4, 11], [6, 2, 10], [8, 6, 7], [9, 8, 1],
  ];
  for (const face of faces) {
    let [a, b, c] = face;
    const A = verts[a], B = verts[b], C = verts[c];
    const u = [B[0] - A[0], B[1] - A[1], B[2] - A[2]];
    const w = [C[0] - A[0], C[1] - A[1], C[2] - A[2]];
    let n = [u[1] * w[2] - u[2] * w[1], u[2] * w[0] - u[0] * w[2], u[0] * w[1] - u[1] * w[0]];
    // keep the winding anticlockwise seen from outside
    if (n[0] * (A[0] + B[0] + C[0]) + n[1] * (A[1] + B[1] + C[1]) + n[2] * (A[2] + B[2] + C[2]) < 0) {
      [b, c] = [c, b];
      n = [-n[0], -n[1], -n[2]];
    }
    m.vertex(A, norm(n)); m.vertex(B, norm(n)); m.vertex(C, norm(n));
  }
  return m.build();
}

// A block that narrows towards its -Y end: kx/kz are the bottom face as a
// fraction of the unit top face. With kz = 1 it is a trapezoid prism - a pivot
// pedestal that tapers under the side view only and stays a plain cuboid
// head-on.
function taper(kx, kz) {
  const m = new Mesh();
  const top = [[-0.5, -0.5], [0.5, -0.5], [0.5, 0.5], [-0.5, 0.5]].map(([x, z]) => [x, 0.5, z]);
  const bot = [[-0.5, -0.5], [0.5, -0.5], [0.5, 0.5], [-0.5, 0.5]]
    .map(([x, z]) => [x * kx, -0.5, z * kz]);
  m.face(top);
  m.face(bot);
  for (let i = 0; i < 4; i++) {
    const j = (i + 1) % 4;
    m.face([top[i], top[j], bot[j], bot[i]]);
  }
  return m.build();
}

// The armoured nose: a unit block that comes to a short transverse line at
// mid-height, so its rear face is the hull's whole front cross-section. Roof and
// belly both slope in - two trapezoids - and the cheeks close the sides as two
// triangles; the four seams read as an X head-on. Local +X is the point.
function nose(k) {
  const m = new Mesh();
  const rear = [[-0.5, -0.5, -0.5], [-0.5, -0.5, 0.5], [-0.5, 0.5, 0.5], [-0.5, 0.5, -0.5]];
  const [r0, r1, r2, r3] = rear;
  const t0 = [0.5, 0, -0.5 * k];
  const t1 = [0.5, 0, 0.5 * k];
  m.face(rear);
  m.face([r3, r2, t1, t0]);      // roof, sloping down to the point
  m.face([r0, r1, t1, t0]);      // belly, sloping up to it
  m.face([r1, r2, t1]);          // right cheek
  m.face([r0, r3, t0]);          // left cheek
  return m.build();
}

// A six-sided tube along Y, open at +Y and plugged dark at the far end: forty
// rocket cells cost six walls each instead of a ten-sided cylinder, and the
// hexagons read as a launcher face instead of a grid of dots.
function hexTube() {
  const m = new Mesh();
  const ring = [0, 1, 2, 3, 4, 5].map((i) => {
    const a = (i / 6) * Math.PI * 2 + Math.PI / 6;
    return [Math.cos(a) * 0.5, Math.sin(a) * 0.5];
  });
  for (let i = 0; i < 6; i++) {
    const [x0, z0] = ring[i];
    const [x1, z1] = ring[(i + 1) % 6];
    const n = norm([(x0 + x1) / 2, 0, (z0 + z1) / 2]);
    const a = [x0, 0.5, z0], c = [x1, -0.5, z1];
    let b = [x1, 0.5, z1], d = [x0, -0.5, z0];
    if ((b[0] - a[0]) * (d[2] - a[2]) - (b[2] - a[2]) * (d[0] - a[0]) < 0) {
      [b, d] = [d, b];
    }
    m.vertex(a, n); m.vertex(b, n); m.vertex(c, n);
    m.vertex(a, n); m.vertex(c, n); m.vertex(d, n);
  }
  // the dark bottom of the cell, facing back up the tube
  for (let i = 1; i < 5; i++) {
    const a = [ring[0][0], -0.5, ring[0][1]];
    let b = [ring[i][0], -0.5, ring[i][1]];
    let c = [ring[i + 1][0], -0.5, ring[i + 1][1]];
    if ((b[0] - a[0]) * (c[2] - a[2]) - (b[2] - a[2]) * (c[0] - a[0]) < 0) [b, c] = [c, b];
    m.vertex(a, [0, 1, 0]); m.vertex(b, [0, 1, 0]); m.vertex(c, [0, 1, 0]);
  }
  return m.build();
}

// The vocabulary. `cap` is how many instances of that primitive one frame's
// buffer holds - a guard against a runaway emitter, sized to what a battle
// actually reaches (boxes are hulls, parts and a tracer's debris; the round
// smoke sphere is drawn rarely but is the close-up puff).
function primitive(geometry, cap, flags) {
  return { geometry, cap, facade: false, unlit: false, ...flags };
}

export const PRIM = {
  BOX: 0,
  SPHERE: 1,
  CONE: 2,
  CYL: 3,
  ROCK: 4,
  PYRAMID: 5,
  SMOKE: 6,
  FACADE: 7,
  WEDGE: 8,
  NOSE: 9,
  HEX: 10,
};

export const PRIMS = [
  primitive(box(), 40000),                                        // hulls, parts, rubble
  primitive(sphere(12, 8), 24000),                                // wheels, joints, dots
  primitive(cone(10), 24000),                                     // muzzles, canopies
  primitive(cylinder(10), 24000),                                 // barrels, tubes
  primitive(rock(), 8000),                                        // boulders
  primitive(pyramid(), 32000, { unlit: true }),                   // tracers
  primitive(sphere(24, 16), 64),                                  // the close-up puff
  primitive(box(), 64, { facade: true }),                         // a house wall
  primitive(taper(0.42, 1.0), 2000),                              // a mech's leg pedestal
  primitive(nose(0.34), 600),                                     // a cockpit
  primitive(hexTube(), 6000),                                     // one cell of a launcher rack
];

// The blended set - impact sprites, sparks, a missile's trail - is one flat cap
// for every primitive.
export const BLENDED_CAP = 12000;

// How many primitives there are, for the tables indexed by one.
export const PRIM_COUNT = PRIMS.length;

// The screen-space overlay draws at most this many pieces (shaft + head per
// arrow), out of its own two buffers.
export const OVERLAY_CAP = 16;
