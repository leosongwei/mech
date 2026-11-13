// The renderer's facade: the context, the mesh geometry, the one submit path,
// and the handles every other module and the game hold.
//
// The frame itself is `passes.js` (a list of six passes), the GL resources and
// the state cache are `gpu.js`, and the ground, the asphalt, the props and the
// overlay each own a file.  What is left here is what more than one pass needs:
// the primitive meshes in one VBO, the instanced submit that draws out of any
// instance buffer with the mesh program, and the map's ownership (build the
// textures and the props, let the previous ones go).
//
// The names below are the client's contract: `window.__game.renderer` drives
// them from the console, and `AGENTS.md` documents them.

import { Gpu } from './gpu.js';
import { Batches } from './batch.js';
import { MESH, MESH_INFO, STRIDE_FLOATS } from './mesh-table.js';
import { buildMeshes } from './meshes.js';
import { MESH_VS, MESH_FS } from './shaders/mesh.js';
import { Textures } from './textures.js';
import { Terrain } from './terrain.js';
import { Roads } from './roads.js';
import { PropField } from './props.js';
import { Overlay } from './overlay.js';
import { drawFrame } from './passes.js';

const MESH_UNIFORMS = ['uViewProj', 'uSun', 'uCamPos', 'uFogColor', 'uFogDensity', 'uFar',
  'uUnlit', 'uFacade', 'uOutline'];
const INSTANCE_ATTRS = ['iM0', 'iM1', 'iM2', 'iM3', 'iColor'];

export class Renderer {
  constructor(canvas) {
    const gl = canvas.getContext('webgl2', {
      antialias: true, alpha: false, powerPreference: 'high-performance',
    });
    if (!gl) throw new Error('WebGL2 unavailable');
    this.gl = gl;
    this.canvas = canvas;
    this.gpu = new Gpu(gl);
    this.fogColor = [0.66, 0.71, 0.75];
    this.fogDensity = 1 / 3600;
    this.sun = (() => {
      const s = [0.55, 0.72, 0.42];
      const l = Math.hypot(...s);
      return s.map((x) => x / l);
    })();
    this.far = 32000;
    this.near = 1.0;
    this.stats = { draws: 0, instances: 0 };
    this.map = null;
    this._skipEmit = false;
    // the submit's multi-draw index arrays: grown once, reused every frame
    this._mdCap = 0;
    this._mdFirst = null;
    this._mdCount = null;
    this._mdInst = null;
    this._mdOff = null;

    // WEBGL_multi_draw: one call submits a whole command list.  Bind the
    // function to whichever object owns it (context or extension object).
    const multi = gl.getExtension('WEBGL_multi_draw');
    this.multiFn = null;
    if (typeof gl.multiDrawArraysInstancedWEBGL === 'function') {
      this.multiFn = gl.multiDrawArraysInstancedWEBGL.bind(gl);
    } else if (multi && typeof multi.multiDrawArraysInstancedWEBGL === 'function') {
      this.multiFn = multi.multiDrawArraysInstancedWEBGL.bind(multi);
    }

    // ---- primitive meshes in one VBO ---------------------------------
    const raw = buildMeshes();
    this.meshes = [];
    let total = 0;
    for (const m of raw) total += m.length;
    const verts = new Float32Array(total);
    let o = 0;
    for (const m of raw) {
      this.meshes.push({ first: o / 6, count: m.length / 6 });
      verts.set(m, o);
      o += m.length;
    }
    this.meshVBO = this.gpu.buffer(gl.ARRAY_BUFFER, verts, gl.STATIC_DRAW);
    this.meshFirst = this.meshes.map((m) => m.first);
    this.meshCount = this.meshes.map((m) => m.count);

    this.mesh = this.gpu.program('mesh', MESH_VS, MESH_FS, MESH_UNIFORMS);
    this.attribs = { aPos: this.gpu.attrib(this.mesh.prog, 'aPos'), aNormal: this.gpu.attrib(this.mesh.prog, 'aNormal') };
    for (const n of INSTANCE_ATTRS) this.attribs[n] = this.gpu.attrib(this.mesh.prog, n);

    // ---- the passes' own resources -----------------------------------
    this.batches = new Batches(this.gpu);
    // The three instance sets are allocated at their cap once and rewritten
    // every frame: `bufferSubData` needs the storage to exist, and a cap-sized
    // `DYNAMIC_DRAW` buffer is what the old renderer allocated up front.
    for (const set of [this.batches.opaque, this.batches.blended, this.batches.overlay]) {
      for (const b of set) this.gpu.retarget(b.vbo, b.data.byteLength, gl.DYNAMIC_DRAW);
    }
    this.textures = new Textures(this.gpu);
    this.terrain = new Terrain(this.gpu);
    this.roads = new Roads(this.gpu);
    this.props = new PropField(this.gpu);
    this.overlay = new Overlay(this.gpu, this.batches);
  }

  // The size the canvas is drawn at: CSS pixels times the device ratio, capped,
  // because a 4K phone would otherwise render four times the pixels of a 1080p
  // screen for a picture nobody can tell apart.
  resize() {
    const dpr = Math.min(window.devicePixelRatio || 1, 1.75);
    const w = Math.floor(this.canvas.clientWidth * dpr);
    const h = Math.floor(this.canvas.clientHeight * dpr);
    if (this.canvas.width !== w || this.canvas.height !== h) {
      this.canvas.width = w;
      this.canvas.height = h;
    }
  }

  // ------------------------------------------------------------------ map
  // A map arrives once per match (`repaintTiles` is the other door, and only for
  // the tiles).  Everything the map owns is built here and the previous map's
  // copy is released first, so a second `setMap()` is not a leak.
  setMap(map) {
    this.map = map;
    this.textures.build(map);
    this.roads.build(map);
    this.props.build(map);
    this.setZoomLevel(0);
  }

  // rebuild the far terrain grid for a new optics level (cheap, once per step)
  setZoomLevel(level) {
    this.terrain.setZoomLevel(level);
  }

  // The debug tree planter changed the ground under us: the tile texture and the
  // props (trunks, canopies) are drawn from `map.tiles`, so both are rebuilt
  // from the new bitmap.  Heights, roads and walls are untouched.
  repaintTiles(map) {
    this.map = map;
    this.textures.uploadTiles(map);
    this.props.build(map);
  }

  get roadCount() { return this.roads.count; }

  get propStats() { return this.props.stats; }

  // ---------------------------------------------------------------- frame
  // resize + clear + the frame's own uniforms, then the pass list runs.  The
  // world's uniforms are set once here, before the first pass that shoots a
  // batch through the mesh program.
  beginFrame(cam) {
    const gl = this.gl;
    this.resize();
    this.stats.draws = 0;
    this.stats.instances = 0;
    gl.viewport(0, 0, this.canvas.width, this.canvas.height);
    gl.clearColor(this.fogColor[0], this.fogColor[1], this.fogColor[2], 1);
    gl.clearDepth(1.0);
    this.gpu.setState({ depth: true, func: gl.LESS, depthWrite: true, blend: false, cull: false });
    gl.clear(gl.COLOR_BUFFER_BIT | gl.DEPTH_BUFFER_BIT);
    // The frame's own emission starts empty every tick.  The overlay is not part
    // of it: the threat pass fills those two buffers before `draw()` is called
    // and the overlay pass empties them as it draws, which is the order the old
    // `_drawArrows` had (`batches.reset()` would clear them here).
    for (const set of [this.batches.opaque, this.batches.blended]) {
      for (const b of set) b.count = 0;
    }
    this.setupMeshUniforms(cam);
  }

  draw(cam, emit) {
    drawFrame(this, cam, emit);
  }

  setupMeshUniforms(cam) {
    const gl = this.gl;
    const u = this.mesh.u;
    this.gpu.useProgram(this.mesh);
    gl.uniformMatrix4fv(u.uViewProj, false, cam.viewProj);
    gl.uniform3fv(u.uSun, this.sun);
    gl.uniform3fv(u.uCamPos, cam.eye);
    gl.uniform3fv(u.uFogColor, this.fogColor);
    gl.uniform1f(u.uFogDensity, this.fogDensity);
    gl.uniform1f(u.uFar, this.far);
    gl.uniform1f(u.uOutline, 0);
  }

  // the triangle range one primitive occupies in `meshVBO`
  meshRange(mesh) {
    return { first: this.meshFirst[mesh], count: this.meshCount[mesh] };
  }

  _bindMesh(vbo, byteOffset) {
    const gl = this.gl;
    const a = this.attribs;
    gl.bindBuffer(gl.ARRAY_BUFFER, this.meshVBO);
    gl.enableVertexAttribArray(a.aPos);
    gl.vertexAttribPointer(a.aPos, 3, gl.FLOAT, false, 24, 0);
    gl.enableVertexAttribArray(a.aNormal);
    gl.vertexAttribPointer(a.aNormal, 3, gl.FLOAT, false, 24, 12);
    gl.bindBuffer(gl.ARRAY_BUFFER, vbo);
    const stride = STRIDE_FLOATS * 4;
    for (let k = 0; k < 5; k++) {
      const loc = a[INSTANCE_ATTRS[k]];
      gl.enableVertexAttribArray(loc);
      gl.vertexAttribPointer(loc, 4, gl.FLOAT, false, stride, (byteOffset || 0) + k * 16);
      gl.vertexAttribDivisor(loc, 1);
    }
  }

  // One primitive, one instance buffer, a list of `{first, count}` runs in it.
  // WebGL2 has no indirect draw, so the list goes to WEBGL_multi_draw (one call
  // per mesh) and falls back to a per-command instanced draw without it.  The
  // four index arrays are the submit's scratch, grown once and reused: they used
  // to be allocated per submit, i.e. dozens of times a frame.
  submit(mesh, vbo, cmds) {
    if (!cmds.length) return;
    const gl = this.gl;
    const info = MESH_INFO[mesh];
    const u = this.mesh.u;
    // the fragments below are this program's uniforms: the terrain and the road
    // passes in between may have bound another one
    this.gpu.useProgram(this.mesh);
    gl.uniform1f(u.uUnlit, info.unlit ? 1 : 0);
    gl.uniform1f(u.uFacade, info.facade ? 1 : 0);
    const first = this.meshFirst[mesh];
    const count = this.meshCount[mesh];

    if (this.multiFn && cmds.length > 1) {
      if (this._mdCap < cmds.length) {
        this._mdCap = cmds.length;
        this._mdFirst = new Int32Array(cmds.length);
        this._mdCount = new Int32Array(cmds.length);
        this._mdInst = new Int32Array(cmds.length);
        this._mdOff = new Int32Array(cmds.length);
      }
      const firsts = this._mdFirst, counts = this._mdCount;
      const insts = this._mdInst, offs = this._mdOff;
      let total = 0;
      for (let k = 0; k < cmds.length; k++) {
        firsts[k] = first;
        counts[k] = count;
        insts[k] = cmds[k].count;
        offs[k] = cmds[k].first;
        total += cmds[k].count;
      }
      this._bindMesh(vbo, 0);
      try {
        this.multiFn(gl.TRIANGLES, firsts, counts, insts, offs);
        this.stats.draws++;
        this.stats.instances += total;
        return;
      } catch (err) {
        console.warn('multi draw failed, falling back', err);
        this.multiFn = null;
      }
    }
    const stride = STRIDE_FLOATS * 4;
    for (const c of cmds) {
      this._bindMesh(vbo, 0);
      // no baseInstance in WebGL2: shift the attribute pointers instead
      for (let k = 0; k < 5; k++) {
        const loc = this.attribs[INSTANCE_ATTRS[k]];
        gl.vertexAttribPointer(loc, 4, gl.FLOAT, false, stride, c.first * stride + k * 16);
      }
      gl.drawArraysInstanced(gl.TRIANGLES, first, count, c.count);
      this.stats.draws++;
      this.stats.instances += c.count;
    }
  }

  // The opaque instance buffers: every primitive with something in it, one
  // upload and one submit each, `uUnlit` / `uFacade` off the mesh table.
  flushOpaque() {
    this._flush(this.batches.opaque);
  }

  flushBlended() {
    this._flush(this.batches.blended);
  }

  _flush(set) {
    for (let m = 0; m < set.length; m++) {
      const batch = set[m];
      if (!batch.count) continue;
      this.gpu.sub(batch.vbo, batch.data.subarray(0, batch.count * STRIDE_FLOATS));
      this.submit(m, batch.vbo, [{ first: 0, count: batch.count }]);
    }
  }

  // ------------------------------------------------------------- overlay
  // The screen-space overlay's three doors, kept on the facade because the game
  // calls them on the renderer; the pass and its program live in `overlay.js`.
  overlaySun(s) { this.overlay.setSun(s); }

  overlayOrigin(x, y) { this.overlay.setOrigin(x, y); }

  screenArrow(dx, dy, dz, len, head, wid, r, g, b, a) {
    this.overlay.screenArrow(dx, dy, dz, len, head, wid, r, g, b, a);
  }

  // ----------------------------------------------------------- compat
  // The old per-frame emission doors.  `beginDynamic` is the scenery switch
  // (`g.renderer.beginDynamic()` is how the AGENTS.md screenshot hides the
  // dynamic bodies): it drops the next frame's `emit` and empties the batches.
  beginDynamic() {
    this._skipEmit = true;
    for (const set of [this.batches.opaque, this.batches.blended]) {
      for (const b of set) b.count = 0;
    }
  }

  // and this one only empties the blended set, for an emitter that draws its own
  // particles before the frame
  beginParticles() {
    for (const b of this.batches.blended) b.count = 0;
  }

  // `emit` asks whether this frame draws the world's instances.
  takeEmit() {
    if (this._skipEmit) {
      this._skipEmit = false;
      return false;
    }
    return true;
  }

  // The shape-named doors (`g.renderer.box(...)`) and the two blended ones the
  // particle emitters use.  An instance is a mesh index and thirteen numbers.
  put(mesh, ...a) { this.batches.put(mesh, ...a); }
  push(mesh, ...a) { this.batches.put(mesh, ...a); }
  pushA(mesh, ...a) { this.batches.putA(mesh, ...a); }

  box(...a) { this.batches.put(MESH.BOX, ...a); }
  sphere(...a) { this.batches.put(MESH.SPHERE, ...a); }
  cone(...a) { this.batches.put(MESH.CONE, ...a); }
  cyl(...a) { this.batches.put(MESH.CYL, ...a); }
  rock(...a) { this.batches.put(MESH.ROCK, ...a); }
  pyramid(...a) { this.batches.put(MESH.PYRAMID, ...a); }
  smoke(...a) { this.batches.put(MESH.SMOKE, ...a); }
  facade(...a) { this.batches.put(MESH.FACADE, ...a); }
  wedge(...a) { this.batches.put(MESH.WEDGE, ...a); }
  nose(...a) { this.batches.put(MESH.NOSE, ...a); }
  hex(...a) { this.batches.put(MESH.HEX, ...a); }

  sphereA(...a) { this.batches.putA(MESH.SPHERE, ...a); }
  coneA(...a) { this.batches.putA(MESH.CONE, ...a); }

  // ------------------------------------------------------------- lifetime
  dispose() {
    this.batches.dispose();
    this.terrain.dispose();
    this.roads.dispose();
    this.props.dispose();
    this.textures.dispose();
    this.gpu.dispose();
    this.map = null;
  }
}
