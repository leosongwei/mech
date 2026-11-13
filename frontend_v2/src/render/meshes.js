// Primitive meshes: everything in the game is assembled from these four
// shapes.  They all fit the unit cube so a single instance matrix scales,
// rotates and places them.

export function buildMeshes() {
  const meshes = [];
  const push = (list, p, n) => list.push(p[0], p[1], p[2], n[0], n[1], n[2]);
  const norm = (v) => {
    const l = Math.hypot(v[0], v[1], v[2]) || 1;
    return [v[0] / l, v[1] / l, v[2] / l];
  };

  // ---- cube ---------------------------------------------------------
  pushCube(meshes);

  // ---- sphere (radius 0.5) -----------------------------------------
  {
    const v = [];
    const SEG = 12, RING = 8;
    for (let r = 0; r < RING; r++) {
      const t0 = r / RING, t1 = (r + 1) / RING;
      const p0 = Math.PI * t0, p1 = Math.PI * t1;
      for (let s = 0; s < SEG; s++) {
        const a0 = (s / SEG) * Math.PI * 2, a1 = ((s + 1) / SEG) * Math.PI * 2;
        const pt = (phi, th) => {
          const n = [Math.sin(phi) * Math.cos(th), Math.cos(phi), Math.sin(phi) * Math.sin(th)];
          return { p: [n[0] * 0.5, n[1] * 0.5, n[2] * 0.5], n };
        };
        const A = pt(p0, a0), B = pt(p1, a0), C = pt(p1, a1), D = pt(p0, a1);
        push(v, A.p, A.n); push(v, B.p, B.n); push(v, C.p, C.n);
        push(v, A.p, A.n); push(v, C.p, C.n); push(v, D.p, D.n);
      }
    }
    meshes.push(v);
  }

  // ---- cone (base radius 0.5, height 1, apex up) ---------------------
  {
    const v = [];
    const SEG = 10;
    const apex = [0, 0.5, 0];
    for (let s = 0; s < SEG; s++) {
      const a0 = (s / SEG) * Math.PI * 2, a1 = ((s + 1) / SEG) * Math.PI * 2;
      const p0 = [Math.cos(a0) * 0.5, -0.5, Math.sin(a0) * 0.5];
      const p1 = [Math.cos(a1) * 0.5, -0.5, Math.sin(a1) * 0.5];
      const n0 = norm([Math.cos(a0), 0.7, Math.sin(a0)]);
      const n1 = norm([Math.cos(a1), 0.7, Math.sin(a1)]);
      const na = norm([(n0[0] + n1[0]) * 0.5, 0.7, (n0[2] + n1[2]) * 0.5]);
      push(v, p0, n0); push(v, p1, n1); push(v, apex, na);
      push(v, p0, [0, -1, 0]); push(v, p1, [0, -1, 0]); push(v, [0, -0.5, 0], [0, -1, 0]);
    }
    meshes.push(v);
  }

  // ---- cylinder (radius 0.5, height 1 along Y) ----------------------
  {
    const v = [];
    const SEG = 10;
    for (let s = 0; s < SEG; s++) {
      const a0 = (s / SEG) * Math.PI * 2, a1 = ((s + 1) / SEG) * Math.PI * 2;
      const p0 = [Math.cos(a0) * 0.5, -0.5, Math.sin(a0) * 0.5];
      const p1 = [Math.cos(a1) * 0.5, -0.5, Math.sin(a1) * 0.5];
      const q0 = [p0[0], 0.5, p0[2]];
      const q1 = [p1[0], 0.5, p1[2]];
      const n0 = norm([Math.cos(a0), 0, Math.sin(a0)]);
      const n1 = norm([Math.cos(a1), 0, Math.sin(a1)]);
      push(v, p0, n0); push(v, p1, n1); push(v, q1, n1);
      push(v, p0, n0); push(v, q1, n1); push(v, q0, n0);
      push(v, q0, [0, 1, 0]); push(v, q1, [0, 1, 0]); push(v, [0, 0.5, 0], [0, 1, 0]);
      push(v, p0, [0, -1, 0]); push(v, p1, [0, -1, 0]); push(v, [0, -0.5, 0], [0, -1, 0]);
    }
    meshes.push(v);
  }

  pushRock(meshes);
  pushPyramid(meshes);
  pushSmokeSphere(meshes);
  pushCube(meshes);              // the facade box: same hull, windows in the shader
  pushTaper(meshes, 0.42, 1.0);  // wedge: trapezoid from the side, cuboid head-on
  pushNose(meshes, 0.34);        // nose: two trapezoids and two side triangles
  pushHexTube(meshes);
  return meshes;
}

// A block that narrows towards its -Y end: kx/kz are the bottom face as a
// fraction of the unit top face.  With kz = 1 it is a trapezoid prism - a
// pivot pedestal that tapers under the side view only and stays a plain
// cuboid head-on.
function pushTaper(meshes, kx, kz) {
  const v = [];
  const push = (p, n) => v.push(p[0], p[1], p[2], n[0], n[1], n[2]);
  const top = [[-0.5, -0.5], [0.5, -0.5], [0.5, 0.5], [-0.5, 0.5]].map(([x, z]) => [x, 0.5, z]);
  const bot = [[-0.5, -0.5], [0.5, -0.5], [0.5, 0.5], [-0.5, 0.5]]
    .map(([x, z]) => [x * kx, -0.5, z * kz]);
  face(v, top);
  face(v, bot);
  for (let i = 0; i < 4; i++) {
    const j = (i + 1) % 4;
    face(v, [top[i], top[j], bot[j], bot[i]]);
  }
  meshes.push(v);
}

// The armoured nose: a unit block that comes to a short transverse line at
// mid-height, so its rear face is the hull's whole front cross-section.  Roof
// and belly both slope in - two trapezoids - and the cheeks close the sides as
// two triangles; the four seams read as an X head-on.  Local +X is the point.
function pushNose(meshes, k) {
  const v = [];
  const rear = [[-0.5, -0.5, -0.5], [-0.5, -0.5, 0.5], [-0.5, 0.5, 0.5], [-0.5, 0.5, -0.5]];
  const [r0, r1, r2, r3] = rear;
  const t0 = [0.5, 0, -0.5 * k];
  const t1 = [0.5, 0, 0.5 * k];
  face(v, rear);
  face(v, [r3, r2, t1, t0]);      // roof, sloping down to the point
  face(v, [r0, r1, t1, t0]);      // belly, sloping up to it
  face(v, [r1, r2, t1]);          // right cheek
  face(v, [r0, r3, t0]);          // left cheek
  meshes.push(v);
}

// One flat face, wound so its normal points away from the centre of the unit
// block, and flat shaded from that winding.
function face(v, pts) {
  const [a, b, c] = pts;
  const d = pts.length > 3 ? pts[3] : c;
  const u = [b[0] - a[0], b[1] - a[1], b[2] - a[2]];
  const w = [d[0] - a[0], d[1] - a[1], d[2] - a[2]];
  let n = [u[1] * w[2] - u[2] * w[1], u[2] * w[0] - u[0] * w[2], u[0] * w[1] - u[1] * w[0]];
  const mid = pts.reduce((s, p) => [s[0] + p[0], s[1] + p[1], s[2] + p[2]], [0, 0, 0]);
  if (n[0] * mid[0] + n[1] * mid[1] + n[2] * mid[2] < 0) n = [-n[0], -n[1], -n[2]];
  const l = Math.hypot(n[0], n[1], n[2]) || 1;
  n = [n[0] / l, n[1] / l, n[2] / l];
  const put = (p) => v.push(p[0], p[1], p[2], n[0], n[1], n[2]);
  put(a); put(b); put(c);
  if (pts.length > 3) { put(a); put(c); put(d); }
}

// A six-sided tube along Y, open at +Y and plugged dark at the far end: forty
// rocket cells cost six walls each instead of a ten-sided cylinder, and the
// hexagons read as a launcher face instead of a grid of dots.
function pushHexTube(meshes) {
  const v = [];
  const push = (p, n) => v.push(p[0], p[1], p[2], n[0], n[1], n[2]);
  const ring = [0, 1, 2, 3, 4, 5].map((i) => {
    const a = (i / 6) * Math.PI * 2 + Math.PI / 6;
    return [Math.cos(a) * 0.5, Math.sin(a) * 0.5];
  });
  for (let i = 0; i < 6; i++) {
    const [x0, z0] = ring[i];
    const [x1, z1] = ring[(i + 1) % 6];
    let n = [(x0 + x1) / 2, 0, (z0 + z1) / 2];
    const l = Math.hypot(n[0], n[2]) || 1;
    n = [n[0] / l, 0, n[2] / l];
    // wound anticlockwise looking at the wall from outside the tube
    const a = [x0, 0.5, z0], c = [x1, -0.5, z1];
    let b = [x1, 0.5, z1], d = [x0, -0.5, z0];
    if ((b[0] - a[0]) * (d[2] - a[2]) - (b[2] - a[2]) * (d[0] - a[0]) < 0) {
      [b, d] = [d, b];
    }
    push(a, n); push(b, n); push(c, n);
    push(a, n); push(c, n); push(d, n);
  }
  // the dark bottom of the cell, facing back up the tube
  const n = [0, 1, 0];
  for (let i = 1; i < 5; i++) {
    const a = [ring[0][0], -0.5, ring[0][1]];
    let b = [ring[i][0], -0.5, ring[i][1]];
    let c = [ring[i + 1][0], -0.5, ring[i + 1][1]];
    if ((b[0] - a[0]) * (c[2] - a[2]) - (b[2] - a[2]) * (c[0] - a[0]) < 0) [b, c] = [c, b];
    push(a, n); push(b, n); push(c, n);
  }
  meshes.push(v);
}

// The unit cube, centred on the origin.  It is the workhorse - hulls, crates,
// smoke canisters - and doubles as the facade mesh: a house is a cube with a
// wall pattern painted on by the fragment shader.
function pushCube(meshes) {
  const v = [];
  const push = (p, n) => v.push(p[0], p[1], p[2], n[0], n[1], n[2]);
  const faces = [
    [[0, 0, 1], [1, 0, 0], [0, 1, 0]],
    [[0, 0, -1], [-1, 0, 0], [0, 1, 0]],
    [[1, 0, 0], [0, 0, -1], [0, 1, 0]],
    [[-1, 0, 0], [0, 0, 1], [0, 1, 0]],
    [[0, 1, 0], [1, 0, 0], [0, 0, -1]],
    [[0, -1, 0], [1, 0, 0], [0, 0, 1]],
  ];
  for (const [n, u, w] of faces) {
    const corners = [[-1, -1], [1, -1], [1, 1], [-1, -1], [1, 1], [-1, 1]];
    for (const [cu, cw] of corners) {
      push([
        n[0] * 0.5 + u[0] * cu * 0.5 + w[0] * cw * 0.5,
        n[1] * 0.5 + u[1] * cu * 0.5 + w[1] * cw * 0.5,
        n[2] * 0.5 + u[2] * cu * 0.5 + w[2] * cw * 0.5,
      ], n);
    }
  }
  meshes.push(v);
}

// A boulder: an icosahedron with its vertices pushed in and out at random and
// flat shaded per face, so every rock shows a handful of straight facets
// instead of reading as a ball.
function pushRock(meshes) {
  const v = [];
  const push = (list, p, n) => list.push(p[0], p[1], p[2], n[0], n[1], n[2]);
  const norm = (p) => {
    const l = Math.hypot(p[0], p[1], p[2]) || 1;
    return [p[0] / l, p[1] / l, p[2] / l];
  };
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
    // keep the winding anticlockwise seen from outside, so back faces cull
    if (n[0] * (A[0] + B[0] + C[0]) + n[1] * (A[1] + B[1] + C[1]) + n[2] * (A[2] + B[2] + C[2]) < 0) {
      [b, c] = [c, b];
      n = [-n[0], -n[1], -n[2]];
    }
    n = norm(n);
    push(v, A, n); push(v, B, n); push(v, C, n);
  }
  meshes.push(v);
}

export const MESH_BOX = 0;
export const MESH_SPHERE = 1;
export const MESH_CONE = 2;
export const MESH_CYL = 3;
export const MESH_ROCK = 4;
export const MESH_PYRAMID = 5;
export const MESH_SMOKE = 6;
export const MESH_FACADE = 7;        // cube with window rows on its walls
export const MESH_WEDGE = 8;         // block tapering along X: trapezoid from the side
export const MESH_NOSE = 9;          // spearhead: two trapezoids, two side triangles
export const MESH_HEX = 10;          // six-sided tube, plugged dark at the far end

// A four-sided pyramid: base radius 0.5 at y = -0.5, apex at y = +0.5, flat
// shaded per face.  Six triangles, which is all a tracer needs - the streak is
// stretched along its flight path and drawn unlit, so the facets only show in
// the silhouette.
export function pushPyramid(meshes) {
  const v = [];
  const push = (p, n) => v.push(p[0], p[1], p[2], n[0], n[1], n[2]);
  const apex = [0, 0.5, 0];
  const corner = (i) => {
    const a = (i / 4) * Math.PI * 2;
    return [Math.cos(a) * 0.5, -0.5, Math.sin(a) * 0.5];
  };
  for (let i = 0; i < 4; i++) {
    const p0 = corner(i), p1 = corner(i + 1);
    const u = [p1[0] - p0[0], p1[1] - p0[1], p1[2] - p0[2]];
    const w = [apex[0] - p0[0], apex[1] - p0[1], apex[2] - p0[2]];
    let n = [u[1] * w[2] - u[2] * w[1], u[2] * w[0] - u[0] * w[2], u[0] * w[1] - u[1] * w[0]];
    const l = Math.hypot(...n) || 1;
    n = [n[0] / l, n[1] / l, n[2] / l];
    push(p0, n); push(p1, n); push(apex, n);
  }
  const a = corner(0), b = corner(1), c = corner(2), d = corner(3);
  push(a, [0, -1, 0]); push(b, [0, -1, 0]); push(c, [0, -1, 0]);
  push(a, [0, -1, 0]); push(c, [0, -1, 0]); push(d, [0, -1, 0]);
  meshes.push(v);
}

// A rounder sphere for smoke: the pancake puffs are metres across and sit
// right in front of the camera, where the 12x8 unit sphere reads as a
// dodecagon and the silhouette gives the wall away.
export function pushSmokeSphere(meshes) {
  const v = [];
  const SEG = 24, RING = 16;
  const push = (p, n) => v.push(p[0], p[1], p[2], n[0], n[1], n[2]);
  const at = (phi, th) => {
    const n = [Math.sin(phi) * Math.cos(th), Math.cos(phi), Math.sin(phi) * Math.sin(th)];
    return { p: [n[0] * 0.5, n[1] * 0.5, n[2] * 0.5], n };
  };
  for (let r = 0; r < RING; r++) {
    const p0 = Math.PI * (r / RING), p1 = Math.PI * ((r + 1) / RING);
    for (let s = 0; s < SEG; s++) {
      const a0 = (s / SEG) * Math.PI * 2, a1 = ((s + 1) / SEG) * Math.PI * 2;
      const A = at(p0, a0), B = at(p1, a0), C = at(p1, a1), D = at(p0, a1);
      push(A.p, A.n); push(B.p, B.n); push(C.p, C.n);
      push(A.p, A.n); push(C.p, C.n); push(D.p, D.n);
    }
  }
  meshes.push(v);
}
