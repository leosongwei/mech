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

  return meshes;
}

export const MESH_BOX = 0;
export const MESH_SPHERE = 1;
export const MESH_CONE = 2;
export const MESH_CYL = 3;
