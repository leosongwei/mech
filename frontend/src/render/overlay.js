// The screen-space overlay: the threat arrows in the ring beside the warning
// dial.
//
// A cylinder shaft and a cone head - the same instanced primitives every unit in
// the world is made of - drawn in the HUD's own CSS pixels. The axis is the
// round's own direction in the camera's frame - +x right, +y down, +z into the
// glass - so an arrow for a round straight ahead points away from the pilot and
// shows its butt, one for a round behind him comes off the glass at him, and a
// round above or below is read the same way. Nothing here is a screen-plane
// rotation.
//
// Every arrow of a frame springs from the one point `setOrigin` was given - the
// centre of the ring - so three rounds at three bearings read as three needles of
// one compass rather than as a list. That point is also the projection origin,
// which is what keeps an arrow with any depth to it from smearing sideways as it
// turns away, and it is why the whole overlay is one matrix and one draw per
// piece, however many arrows there are.
//
// Three things below are load bearing, and each was a measurement:
//
// * the perspective divides about the *ring's centre*, which is also every
//   arrow's tail: an arrow parked off to one side smears diagonally the moment it
//   has any depth to it;
// * the depth buffer is cleared before the pass, because the pieces overlap each
//   other (a cone's base pokes through its own sides when it is seen end-on) and
//   they float over terrain that must not cut them - so they are sorted against
//   themselves and against nothing else;
// * the far plane is 2, which turns the shader's log depth into a plain log2 of
//   w - monotone over the [1, 1.1] the pieces live in, where the world's 32 km
//   scale would put every one of them at the same depth value.

import { PRIM, OVERLAY_CAP } from './primitives.js';
import { Layer } from './instances.js';
import { overlayMaterial } from './materials.js';

const FOCAL = 900;                   // px, how far away an overlay arrow is told
                                     // to be: an arrow pointing away from the
                                     // pilot tapers, and one coming at him grows
const PUSH = 60;                     // px of depth every piece is pushed behind
                                     // the glass, so its w stays above 1 and the
                                     // log depth it writes stays ordered

// The two pieces, in draw order: the shaft, then the head. `piece` is the
// shader's name for it (1 shaft, 2 head, which the head's base cap is painted
// by).
const SHAFT = 0;
const HEAD = 1;
const PIECES = [
  { layer: SHAFT, prim: PRIM.CYL, piece: 1 },
  { layer: HEAD, prim: PRIM.CONE, piece: 2 },
];

export class Overlay {
  constructor(order) {
    this.layers = PIECES.map((p) => {
      const material = overlayMaterial();
      material.uniforms.uPiece.value = p.piece;
      return new Layer(p.prim, OVERLAY_CAP, material, order + p.layer);
    });
    // the light those pieces are drawn under, folded into their frame once a
    // frame by the rwr system; this default is the world's sun until the first
    // frame arrives
    this.sun = [0.55, -0.72, -0.42];
    this.origin = [0, 0];
    // the projection, rewritten in place every frame instead of allocated
    this.matrix = new Float32Array(16);
  }

  meshes() {
    return this.layers.map((l) => l.mesh);
  }

  setSun(s) {
    this.sun = s;
  }

  setOrigin(x, y) {
    this.origin[0] = x;
    this.origin[1] = y;
  }

  screenArrow(dx, dy, dz, len, head, wid, r, g, b, a) {
    if (this.layers[SHAFT].count + 2 > OVERLAY_CAP) return;
    const l = Math.hypot(dx, dy, dz) || 1;
    const ax = dx / l, ay = dy / l, az = dz / l;
    // the arrow's own side axis: square to it, spun round to +X when the arrow
    // lies along the screen's down axis (the cone and the cylinder are round, so
    // which perpendicular is picked only decides which way they roll)
    let rx = 0, ry = 1, rz = 0;
    if (Math.abs(ay) > 0.95) { rx = 1; ry = 0; rz = 0; }
    let sx = ry * az - rz * ay, sy = rz * ax - rx * az, sz = rx * ay - ry * ax;
    const sl = Math.hypot(sx, sy, sz) || 1;
    sx /= sl; sy /= sl; sz /= sl;
    // and the third column, which closes a right-handed frame so the normals
    // come out the right way round
    const tx = ay * sz - az * sy, ty = az * sx - ax * sz, tz = ax * sy - ay * sx;
    const shaft = Math.max(1, len - head);
    const o = this.origin;
    const pieces = [
      { layer: SHAFT, k: shaft * 0.5, l: shaft, w: wid },
      { layer: HEAD, k: shaft + head * 0.5, l: head, w: wid * 2.1 },
    ];
    for (const p of pieces) {
      const layer = this.layers[p.layer];
      const off = layer.slot();
      if (off < 0) continue;
      const d = layer.data;
      // A piece's local +Y is its axis (the cylinder and the cone are both built
      // along it), so the arrow's direction goes in that column, with the two
      // perpendiculars either side of it - both scaled by the piece's width,
      // since local X and local Z are the mesh's two radial axes and a round
      // piece is as wide across one as across the other. This is an oriented,
      // non-uniform scale, which a yaw/pitch/scale compose cannot express.
      d[off] = sx * p.w; d[off + 1] = sy * p.w; d[off + 2] = sz * p.w; d[off + 3] = 0;
      d[off + 4] = ax * p.l; d[off + 5] = ay * p.l; d[off + 6] = az * p.l; d[off + 7] = 0;
      d[off + 8] = tx * p.w; d[off + 9] = ty * p.w; d[off + 10] = tz * p.w; d[off + 11] = 0;
      d[off + 12] = o[0] + ax * p.k;
      d[off + 13] = o[1] + ay * p.k;
      d[off + 14] = az * p.k + PUSH;
      d[off + 15] = 1;
      d[off + 16] = r; d[off + 17] = g; d[off + 18] = b;
      d[off + 19] = a === undefined ? 1 : a;
    }
  }

  // CSS pixels to clip space with the divide about (cx, cy): an arrow's own
  // pixels, z = depth into the glass, w = 1 + z/D. A vertex on the arrow's
  // centre line comes out at exactly the pixel it was asked for, and the rest of
  // the piece is scaled about that point - so turning an arrow towards the pilot
  // telescopes it instead of sliding it sideways.
  _projection(cx, cy, w, h) {
    const D = FOCAL;
    const m = this.matrix;
    const ax = 2 / w, ay = 2 / h;
    m[0] = ax; m[4] = 0; m[8] = (ax * cx - 1) / D; m[12] = -1;
    m[5] = -ay; m[9] = (1 - ay * cy) / D; m[13] = 1;
    m[11] = 1 / D; m[15] = 1;
    return m;
  }

  // Fill the pass's uniforms: the projection, the light, and the eye the rim
  // term is measured from (the ring's own, `FOCAL` pixels in front of it).
  prepare(width, height) {
    const o = this.origin;
    const m = this._projection(o[0], o[1], width || 1, height || 1);
    for (const layer of this.layers) {
      const u = layer.mesh.material.uniforms;
      u.uViewProj.value.fromArray(m);
      u.uSun.value.fromArray(this.sun);
      u.uCamPos.value.set(o[0], o[1], -FOCAL);
      layer.flush();
    }
  }

  reset() {
    for (const layer of this.layers) layer.reset();
  }

  dispose() {
    for (const layer of this.layers) layer.dispose();
  }
}
