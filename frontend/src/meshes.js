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
  {
    const v = [];
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
        push(v, [
          n[0] * 0.5 + u[0] * cu * 0.5 + w[0] * cw * 0.5,
          n[1] * 0.5 + u[1] * cu * 0.5 + w[1] * cw * 0.5,
          n[2] * 0.5 + u[2] * cu * 0.5 + w[2] * cw * 0.5,
        ], n);
      }
    }
    meshes.push(v);
  }

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
  pushQuad(meshes);
  return meshes;
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
export const MESH_QUAD = 5;

// A flat billboard: one unit square in the XY plane facing +Z, two triangles.
// Tracers are drawn with it instead of a 12-sided cylinder, which cost ~10x
// the vertices for a streak nobody ever sees from the side.
export function pushQuad(meshes) {
  const v = [];
  const n = [0, 0, 1];
  const c = [[-0.5, -0.5], [0.5, -0.5], [0.5, 0.5], [-0.5, -0.5], [0.5, 0.5], [-0.5, 0.5]];
  for (const [x, y] of c) v.push(x, y, 0, n[0], n[1], n[2]);
  meshes.push(v);
}
