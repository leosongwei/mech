// The instance write path: three sets of per-mesh instance buffers (opaque,
// blended, overlay) and the only place an instance is composed.
//
// An instance is a 4x4 matrix and a colour - 20 floats - and `put` writes them
// straight into the buffer at `count * STRIDE_FLOATS`.  The old path built a
// `Float32Array` view of that slot for `compose()` on every instance, which was
// the client's biggest per-frame allocation: a battle emits thousands of
// instances a frame and the views added up to nothing at all.
//
// The three sets are separate because they are drawn in three passes with
// different GL state: opaque bodies before the particles, blended sprites after
// them, and the overlay last with the HUD's own projection.

import { composeInto } from '../core/math.js';
import { MESH, MESH_INFO, MESH_COUNT, OVERLAY_CAP, STRIDE_FLOATS } from './mesh-table.js';

function makeSet(gpu, caps) {
  return caps.map((cap, i) => ({
    mesh: i,
    cap,
    count: 0,
    data: new Float32Array(cap * STRIDE_FLOATS),
    vbo: gpu.buffer(),
  }));
}

export class Batches {
  constructor(gpu) {
    this.gpu = gpu;
    this.opaque = makeSet(gpu, MESH_INFO.map((m) => m.cap));
    this.blended = makeSet(gpu, MESH_INFO.map((m) => m.blendedCap));
    this.overlay = [0, 1].map((piece) => ({
      piece,
      count: 0,
      data: new Float32Array(OVERLAY_CAP * STRIDE_FLOATS),
      vbo: gpu.buffer(),
    }));
  }

  // start of frame: every count back to zero, the buffers stay
  reset() {
    for (const b of this.opaque) b.count = 0;
    for (const b of this.blended) b.count = 0;
    for (const b of this.overlay) b.count = 0;
  }

  _write(set, mesh, px, py, pz, yaw, pitch, sx, sy, sz, r, g, b, a) {
    const batch = set[mesh];
    if (batch.count >= batch.cap) return;
    const off = batch.count * STRIDE_FLOATS;
    const d = batch.data;
    composeInto(d, off, px, py, pz, yaw, pitch, sx, sy, sz);
    d[off + 16] = r; d[off + 17] = g; d[off + 18] = b;
    d[off + 19] = a;
    batch.count++;
  }

  // an opaque body: a hull, a part, a tracer, a canister, a smoke puff
  put(mesh, px, py, pz, yaw, pitch, sx, sy, sz, r, g, b, a = 1) {
    this._write(this.opaque, mesh, px, py, pz, yaw, pitch, sx, sy, sz, r, g, b, a);
  }

  // a blended sprite: dirt, sparks, fire, a missile's trail
  putA(mesh, px, py, pz, yaw, pitch, sx, sy, sz, r, g, b, a) {
    this._write(this.blended, mesh, px, py, pz, yaw, pitch, sx, sy, sz, r, g, b, a);
  }

  // one piece of the screen-space overlay, into the buffer of its own piece
  putOverlay(piece, px, py, pz, yaw, pitch, sx, sy, sz, r, g, b, a) {
    const batch = this.overlay[piece];
    if (!batch || batch.count >= OVERLAY_CAP) return;
    const off = batch.count * STRIDE_FLOATS;
    const d = batch.data;
    composeInto(d, off, px, py, pz, yaw, pitch, sx, sy, sz);
    d[off + 16] = r; d[off + 17] = g; d[off + 18] = b;
    d[off + 19] = a;
    batch.count++;
  }

  dispose() {
    for (const set of [this.opaque, this.blended, this.overlay]) {
      for (const b of set) this.gpu.release(b.vbo);
    }
  }
}

// The shape-named door, kept for the console (`g.renderer.box(...)`) and for the
// models, which read better as `at(b, MESH.BOX, ...)` than as an index.
export const SHAPES = {
  box: MESH.BOX,
  sphere: MESH.SPHERE,
  cone: MESH.CONE,
  cyl: MESH.CYL,
  rock: MESH.ROCK,
  pyramid: MESH.PYRAMID,
  smoke: MESH.SMOKE,
  facade: MESH.FACADE,
  wedge: MESH.WEDGE,
  nose: MESH.NOSE,
  hex: MESH.HEX,
};

export { MESH_COUNT };
