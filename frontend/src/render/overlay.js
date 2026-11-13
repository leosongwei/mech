// The screen-space overlay: the threat arrows in the ring beside the warning
// dial.
//
// A cylinder shaft and a cone head - the same instanced primitives every unit in
// the world is made of - drawn in the HUD's own CSS pixels.  The axis is the
// round's own direction in the camera's frame - +x right, +y down, +z into the
// glass - so an arrow for a round straight ahead points away from the pilot and
// shows its butt, one for a round behind him comes off the glass at him, and a
// round above or below is read the same way.  Nothing here is a screen-plane
// rotation.
//
// Every arrow of a frame springs from the one point `overlayOrigin` was given -
// the centre of the ring - so three rounds at three bearings read as three
// needles of one compass rather than as a list.  That point is also the
// overlay's projection origin, which is what keeps an arrow with any depth to it
// from smearing sideways as it turns away, and it is why the whole overlay is
// one matrix and one draw per piece, however many arrows there are.
//
// This pass has its own program (see `shaders/overlay.js`) instead of borrowing
// the world's: the mesh program used to be re-configured here for one draw and
// left that way (`uFar = 2`, `uFogDensity = 0`), and the matrix it was given was
// a fresh `Float32Array` every frame.  Three things in the pass below are load
// bearing, and each was a measurement:
//
// * the perspective divides about the *ring's centre* (`uViewProj` is built from
//   the origin), which is also every arrow's tail: an arrow parked off to one
//   side smears diagonally the moment it has any depth to it;
// * the depth buffer is cleared and tested for the pass, because the pieces
//   overlap each other (a cone's base pokes through its own sides when it is
//   seen end-on) and they float over terrain that must not cut them - so they
//   are sorted against themselves and against nothing else;
// * `uFar` is 2, which turns the shader's log depth into a plain log2 of w -
//   monotone over the [1, 1.1] the pieces live in, where the world's 32 km scale
//   would put every one of them at the same depth value.

import { OVERLAY_VS, OVERLAY_FS } from './shaders/overlay.js';
import { MESH, MESH_INFO, OVERLAY_CAP, STRIDE_FLOATS } from './mesh-table.js';

const OVL_FOCAL = 900;               // px, how far away an overlay arrow is told
                                     // to be: an arrow pointing away from the
                                     // pilot tapers, and one coming at him grows
const OVL_PUSH = 60;                 // px of depth every overlay piece is pushed
                                     // behind the glass, so its w stays above 1
                                     // and the log depth it writes stays ordered
const OVL_FAR = 2;                   // the scale `uFar` above

// The two pieces, in draw order: the shaft, then the head.  `piece` is
// `MESH_INFO[i].overlay` (shaft 1, head 2, which the shader paints the white base
// cap with) and `slot` is the batch of that piece.
const PIECES = [MESH.CYL, MESH.CONE].map((mesh) => ({
  mesh,
  piece: MESH_INFO[mesh].overlay,
  slot: MESH_INFO[mesh].overlay - 1,
}));

export class Overlay {
  constructor(gpu, batches) {
    this.gpu = gpu;
    this.batches = batches;
    const gl = gpu.gl;
    this.program = gpu.program('overlay', OVERLAY_VS, OVERLAY_FS,
      ['uViewProj', 'uSun', 'uCamPos', 'uFar', 'uPiece']);
    this.attribs = {
      aPos: gpu.attrib(this.program.prog, 'aPos'),
      aNormal: gpu.attrib(this.program.prog, 'aNormal'),
      iM0: gpu.attrib(this.program.prog, 'iM0'),
      iM1: gpu.attrib(this.program.prog, 'iM1'),
      iM2: gpu.attrib(this.program.prog, 'iM2'),
      iM3: gpu.attrib(this.program.prog, 'iM3'),
      iColor: gpu.attrib(this.program.prog, 'iColor'),
    };
    // the light those pieces are drawn under, folded into their frame once a
    // frame by the game (`overlaySun`); this default is the world's sun until
    // the first frame arrives
    this.sun = [0.55, -0.72, -0.42];
    // the point every arrow this frame is projected about (`overlayOrigin`):
    // one divide for the whole column, which is what lets it be one draw
    this.origin = [0, 0];
    // the projection, rewritten in place every frame instead of allocated
    this.matrix = new Float32Array(16);
    gpu.useProgram(this.program);
    gl.uniform1f(this.program.u.uFar, OVL_FAR);
    gpu.useProgram(null);
  }

  setSun(s) { this.sun = s; }

  setOrigin(x, y) {
    this.origin[0] = x;
    this.origin[1] = y;
  }

  screenArrow(dx, dy, dz, len, head, wid, r, g, b, a) {
    const batches = this.batches;
    if (batches.overlay[0].count + 2 > OVERLAY_CAP) return;
    const l = Math.hypot(dx, dy, dz) || 1;
    const ax = dx / l, ay = dy / l, az = dz / l;
    // the arrow's own side axis: square to it, spun round to +X when the arrow
    // lies along the screen's down axis (the cone and the cylinder are round,
    // so which perpendicular is picked only decides which way they roll)
    let rx = 0, ry = 1, rz = 0;
    if (Math.abs(ay) > 0.95) { rx = 1; ry = 0; rz = 0; }
    let sx = ry * az - rz * ay, sy = rz * ax - rx * az, sz = rx * ay - ry * ax;
    const sl = Math.hypot(sx, sy, sz) || 1;
    sx /= sl; sy /= sl; sz /= sl;
    // and the third column, which closes a right-handed frame so the normals
    // come out the right way round
    const tx = ay * sz - az * sy, ty = az * sx - ax * sz, tz = ax * sy - ay * sx;
    const shaft = Math.max(1, len - head);
    // The arrow sits `OVL_PUSH` pixels behind the glass so every one of its
    // vertices lands at w > 1, which keeps the log depth the shader writes
    // positive and ordered; and the shaft is pushed before the head when it is
    // the deeper of the two, because they overlap and a tie would go to whoever
    // draws first.
    const pieces = [
      { piece: 0, k: shaft * 0.5, l: shaft, w: wid },
      { piece: 1, k: shaft + head * 0.5, l: head, w: wid * 2.1 },
    ];
    if (az > 0) pieces.reverse();
    const o = this.origin;
    for (const p of pieces) {
      const batch = batches.overlay[PIECES[p.piece].slot];
      const off = batch.count * STRIDE_FLOATS;
      const d = batch.data;
      // A piece's local +Y is its axis (the cylinder and the cone are both built
      // along it), so the arrow's direction goes in that column, with the two
      // perpendiculars either side of it - both scaled by the piece's width,
      // since local X and local Z are the mesh's two radial axes and a round
      // piece is as wide across one as across the other.  The frame is written
      // straight into the batch: it is an oriented, non-uniform scale, which
      // `composeInto` (a yaw/pitch/scale) cannot express.
      d[off] = sx * p.w; d[off + 1] = sy * p.w; d[off + 2] = sz * p.w; d[off + 3] = 0;
      d[off + 4] = ax * p.l; d[off + 5] = ay * p.l; d[off + 6] = az * p.l; d[off + 7] = 0;
      d[off + 8] = tx * p.w; d[off + 9] = ty * p.w; d[off + 10] = tz * p.w; d[off + 11] = 0;
      d[off + 12] = o[0] + ax * p.k;
      d[off + 13] = o[1] + ay * p.k;
      d[off + 14] = az * p.k + OVL_PUSH;
      d[off + 15] = 1;
      d[off + 16] = r; d[off + 17] = g; d[off + 18] = b;
      d[off + 19] = a === undefined ? 1 : a;
      batch.count++;
    }
  }

  // CSS pixels to clip space with the divide about (cx, cy): an arrow's own
  // pixels, z = depth into the glass, w = 1 + z/D.  A vertex on the arrow's
  // centre line comes out at exactly the pixel it was asked for, and the rest of
  // the piece is scaled about that point - so turning an arrow towards the pilot
  // telescopes it instead of sliding it sideways.
  _matrix(cx, cy, w, h) {
    const D = OVL_FOCAL;
    const m = this.matrix;
    const ax = 2 / w, ay = 2 / h;
    m[0] = ax; m[4] = 0; m[8] = (ax * cx - 1) / D; m[12] = -1;
    m[5] = -ay; m[9] = (1 - ay * cy) / D; m[13] = 1;
    m[11] = 1 / D; m[15] = 1;
    m[2] = 0; m[3] = 0; m[6] = 0; m[7] = 0; m[10] = 0; m[14] = 0;
    return m;
  }

  draw(R) {
    const batches = this.batches;
    if (!batches.overlay[0].count && !batches.overlay[1].count) return;
    const gl = R.gpu.gl;
    const gpu = R.gpu;
    const o = this.origin;
    const D = OVL_FOCAL;
    const u = this.program.u;
    const a = this.attribs;

    gpu.useProgram(this.program);
    gl.uniform3fv(u.uSun, this.sun);
    gl.uniformMatrix4fv(u.uViewProj, false, this._matrix(o[0], o[1],
      R.canvas.clientWidth || 1, R.canvas.clientHeight || 1));
    // the eye for the rim term is the ring's own, `D` pixels in front of it
    gl.uniform3f(u.uCamPos, o[0], o[1], -D);

    // the pieces are sorted against themselves and against nothing else
    gpu.withState({ depth: false, depthWrite: true }, () => {
      gl.clear(gl.DEPTH_BUFFER_BIT);
    });

    gpu.withState({ depth: true, func: gl.LESS, blend: true }, () => {
      gl.bindBuffer(gl.ARRAY_BUFFER, R.meshVBO);
      gl.enableVertexAttribArray(a.aPos);
      gl.vertexAttribPointer(a.aPos, 3, gl.FLOAT, false, 24, 0);
      gl.enableVertexAttribArray(a.aNormal);
      gl.vertexAttribPointer(a.aNormal, 3, gl.FLOAT, false, 24, 12);
      const stride = STRIDE_FLOATS * 4;
      const names = ['iM0', 'iM1', 'iM2', 'iM3', 'iColor'];
      for (const piece of PIECES) {
        const batch = batches.overlay[piece.slot];
        if (!batch.count) continue;
        const { first, count } = R.meshRange(piece.mesh);
        gpu.sub(batch.vbo, batch.data.subarray(0, batch.count * STRIDE_FLOATS));
        gl.bindBuffer(gl.ARRAY_BUFFER, batch.vbo);
        for (let k = 0; k < 5; k++) {
          const loc = a[names[k]];
          gl.enableVertexAttribArray(loc);
          gl.vertexAttribPointer(loc, 4, gl.FLOAT, false, stride, k * 16);
          gl.vertexAttribDivisor(loc, 1);
        }
        // the shader paints the cone's base cap white, so it needs to know which
        // piece it is looking at
        gl.uniform1f(u.uPiece, piece.piece);
        gl.drawArraysInstanced(gl.TRIANGLES, first, count, batch.count);
        R.stats.draws++;
        R.stats.instances += batch.count;
        batch.count = 0;
      }
      gl.disableVertexAttribArray(a.aPos);
      gl.disableVertexAttribArray(a.aNormal);
      for (const n of names) gl.disableVertexAttribArray(a[n]);
    });
  }
}

export const overlay = {
  name: 'overlay',
  draw(R) { R.overlay.draw(R); },
};
