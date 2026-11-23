// The instance path: how a body gets into a draw call.
//
// A body is one primitive, one 4x4 matrix and one colour. `Layer` owns one
// primitive's instances for one kind of pass: a buffer of twenty floats per
// instance (the matrix, then the colour), the interleaved GPU attribute pair
// that reads them, and the mesh that draws them.
//
// The buffer is written in place while the frame's models run, and the upload
// is the *used* range only, so a frame that emits forty boxes uploads forty
// boxes' worth. Nothing is allocated per instance and nothing is sorted: the
// layer's order is the emitters' order, which is what the blended pass relies on
// to keep one puff in front of the next.

import {
  InstancedBufferGeometry, InstancedInterleavedBuffer, InterleavedBufferAttribute,
  Mesh as ThreeMesh, DynamicDrawUsage, StaticDrawUsage,
} from 'three';

import { PRIMS, BLENDED_CAP } from './primitives.js';
import { composeInto } from '../core/math.js';

// mat4 (16) + vec4 colour (4)
export const STRIDE_FLOATS = 20;

// A primitive's vertex buffer, borrowed by one instance layer. Every layer of a
// primitive shares the same position/normal attributes and owns only its
// instance attributes, so adding a pass costs one buffer, not one copy of the
// geometry.
function instanceGeometry(base) {
  const g = new InstancedBufferGeometry();
  g.index = base.index;
  for (const name of Object.keys(base.attributes)) {
    g.setAttribute(name, base.attributes[name]);
  }
  // the instances are placed in world space by the vertex shader, so the
  // geometry's own bounds say nothing about what it covers
  g.boundingSphere = null;
  g.instanceCount = 0;
  return g;
}

export class Layer {
  constructor(prim, cap, material, order = 0) {
    this.prim = prim;
    this.cap = cap;
    this.count = 0;
    this.material = material;
    this.data = new Float32Array(cap * STRIDE_FLOATS);
    this.buffer = new InstancedInterleavedBuffer(this.data, STRIDE_FLOATS, 1);
    this.buffer.setUsage(DynamicDrawUsage);
    this.geometry = instanceGeometry(PRIMS[prim].geometry);
    this.geometry.setAttribute('iMatrix', new InterleavedBufferAttribute(this.buffer, 16, 0));
    this.geometry.setAttribute('iColor', new InterleavedBufferAttribute(this.buffer, 4, 16));
    this.mesh = new ThreeMesh(this.geometry, material);
    this.mesh.frustumCulled = false;
    this.mesh.matrixAutoUpdate = false;
    this.mesh.renderOrder = order;
    this.mesh.visible = false;
  }

  // The float offset of the next free instance, or -1 when the layer is full.
  // A cap is a guard against a runaway emitter, so a full layer drops the
  // instance instead of growing a buffer mid-frame.
  slot() {
    if (this.count >= this.cap) return -1;
    return this.count++ * STRIDE_FLOATS;
  }

  reset() {
    this.count = 0;
  }

  // Publish this frame's instances: the draw count, the upload range, whether
  // the mesh is worth submitting at all.
  flush() {
    const n = this.count;
    this.geometry.instanceCount = n;
    this.mesh.visible = n > 0;
    if (!n) return;
    this.buffer.clearUpdateRanges();
    this.buffer.addUpdateRange(0, n * STRIDE_FLOATS);
    this.buffer.needsUpdate = true;
  }

  get static() { return false; }

  // A layer's vertex buffers belong to the primitive and are shared by every
  // layer of it, so they are dropped from this geometry before the dispose that
  // would otherwise free buffers the other layers still draw from. What is this
  // layer's own is the two instance attributes.
  dispose() {
    const g = this.geometry;
    for (const name of Object.keys(g.attributes)) {
      if (name !== 'iMatrix' && name !== 'iColor') g.deleteAttribute(name);
    }
    g.index = null;
    g.dispose();
  }
}

// A layer whose contents are written once, at build time (`props.js`): the
// buffer is allocated at its exact size and uploaded once.
export class StaticLayer extends Layer {
  constructor(prim, cap, material, order = 0) {
    super(prim, cap, material, order);
    this.buffer.setUsage(StaticDrawUsage);
    this.filled = 0;
  }

  get static() { return true; }

  // Build-time write, not a frame slot: the caller knows the count up front.
  write(index, x, y, z, yaw, pitch, sx, sy, sz, c) {
    const off = index * STRIDE_FLOATS;
    composeInto(this.data, off, x, y, z, yaw, pitch, sx, sy, sz);
    this.data[off + 16] = c[0];
    this.data[off + 17] = c[1];
    this.data[off + 18] = c[2];
    this.data[off + 19] = 1;
    this.filled = Math.max(this.filled, index + 1);
  }

  flush() {
    const n = this.filled;
    this.geometry.instanceCount = n;
    this.mesh.visible = n > 0;
    if (!n) return;
    this.buffer.needsUpdate = true;
  }
}

// The world's two instance sets. They are separate because they are drawn with
// different state and at different points of the frame: the opaque bodies with
// the ground behind them, the blended ones after everything solid.
export class Bodies {
  constructor(opaqueMaterial, blendedMaterial, opaqueOrder, blendedOrder) {
    // the material is asked for by primitive index: a material is compiled per
    // primitive, because a primitive's shape decides what its program does
    this.opaque = PRIMS.map((p, i) => new Layer(i, p.cap, opaqueMaterial(i), opaqueOrder));
    this.blended = PRIMS.map((p, i) => new Layer(i, BLENDED_CAP, blendedMaterial(i), blendedOrder));
  }

  meshes() {
    return [...this.opaque, ...this.blended].map((l) => l.mesh);
  }

  // An opaque body: a hull, a part, a tracer, a canister, a smoke puff.
  put(prim, px, py, pz, yaw, pitch, sx, sy, sz, r, g, b, a = 1) {
    const layer = this.opaque[prim];
    const off = layer.slot();
    if (off < 0) return;
    composeInto(layer.data, off, px, py, pz, yaw, pitch, sx, sy, sz);
    layer.data[off + 16] = r;
    layer.data[off + 17] = g;
    layer.data[off + 18] = b;
    layer.data[off + 19] = a;
  }

  // A blended sprite: dirt, sparks, fire, a missile's trail.
  putA(prim, px, py, pz, yaw, pitch, sx, sy, sz, r, g, b, a) {
    const layer = this.blended[prim];
    const off = layer.slot();
    if (off < 0) return;
    composeInto(layer.data, off, px, py, pz, yaw, pitch, sx, sy, sz);
    layer.data[off + 16] = r;
    layer.data[off + 17] = g;
    layer.data[off + 18] = b;
    layer.data[off + 19] = a;
  }

  reset() {
    for (const l of this.opaque) l.reset();
    for (const l of this.blended) l.reset();
  }

  flush() {
    for (const l of this.opaque) l.flush();
    for (const l of this.blended) l.flush();
  }

  // The blended set's material is the quality switch's business
  // (`performance` draws the same instances opaque against the noise mask).
  setBlendedMaterial(materialOf) {
    for (const l of this.blended) l.mesh.material = materialOf(l.prim);
  }

  get instances() {
    let n = 0;
    for (const l of this.opaque) n += l.count;
    for (const l of this.blended) n += l.count;
    return n;
  }

  dispose() {
    for (const l of this.opaque) l.dispose();
    for (const l of this.blended) l.dispose();
  }
}
