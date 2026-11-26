// The client's view of the world: the device, the scene, and the frame.
//
// A frame is a sequence of layers, and the layer order is the drawing order:
//
//   sky      one triangle, no depth, the far field
//   ground   the far LOD grid and the near tile patch, in two resolutions
//   roads    the asphalt, on the ground it was laid on
//   props    trees, houses, boulders - static, built with the map
//   bodies   everything that moves: hulls, parts, projectiles, canisters
//   particles the same primitives, blended, drawn after the solid world
//
// The overlay (the threat arrows) is a second scene drawn over the first in the
// HUD's own pixels, with the depth buffer cleared in between.
//
// What the frame's models do is one call: `draw(cam, emit)` hands `emit` an
// object with `put` / `putA`, and the models write primitives into this frame's
// instance layers. Everything after that - the uploads, the culling, the draws
// - belongs to three.

import {
  WebGLRenderer, Scene, PerspectiveCamera, Group, Color, LinearSRGBColorSpace,
} from 'three';

import { frameUniforms, bodyMaterial, SUN } from './materials.js';
import { PRIMS, PRIM } from './primitives.js';
import { Bodies } from './instances.js';
import { GroundTextures } from './textures.js';
import { Sky } from './sky.js';
import { Ground } from './ground.js';
import { Roads } from './roads.js';
import { Props } from './props.js';
import { Overlay } from './overlay.js';
import { syncCamera } from './camera.js';

// The layers, in drawing order. The numbers are three's `renderOrder`, so a
// layer's place in the frame is a property of the pass that owns it rather than
// a line of a draw method: `sky` is first and `particles` last, though the
// overlay is a scene of its own and only keeps its number for the record.
export const ORDER = {
  sky: 0,
  ground: 1,
  roads: 2,
  props: 3,
  bodies: 4,
  particles: 5,
  overlay: 6,
};

const MAX_DPR = 1.75;   // a 4K phone would otherwise draw four times the pixels
                        // of a 1080p screen for a picture nobody can tell apart

export class Renderer {
  constructor(canvas) {
    this.canvas = canvas;
    const device = new WebGLRenderer({
      canvas,
      antialias: true,
      alpha: false,
      stencil: false,
      powerPreference: 'high-performance',
    });
    // the programs write their own colour; three's colour management would be a
    // second opinion about what a fragment is worth
    device.outputColorSpace = LinearSRGBColorSpace;
    device.autoClear = true;
    device.info.autoReset = false;
    this.device = device;

    // The numbers every material of a frame reads: written once, shared by
    // reference, so no pass can leave a stale copy behind.
    this.U = frameUniforms();
    this.sun = [SUN.x, SUN.y, SUN.z];
    this.far = this.U.uFar.value;
    const fog = this.U.uFogColor.value;
    this._clearColor = new Color(fog.x, fog.y, fog.z);
    device.setClearColor(this._clearColor, 1);

    this.scene = new Scene();
    this.camera = new PerspectiveCamera();
    this.overlayScene = new Scene();
    this.overlayCamera = new PerspectiveCamera();

    this.groups = {
      sky: new Group(),
      ground: new Group(),
      roads: new Group(),
      props: new Group(),
      bodies: new Group(),
    };
    for (const g of Object.values(this.groups)) this.scene.add(g);

    this.sky = new Sky(this.U, ORDER.sky);
    this.groups.sky.add(this.sky.mesh);

    // The blended set exists in two materials: the quality step swaps which one
    // the layers carry. `quality` blends the impact effects; `performance` draws
    // the same instances opaque against the per-pixel noise mask, so a body's
    // transparency becomes the density of a speckle and no fragment blends.
    this._blended = {
      quality: PRIMS.map((_, prim) => bodyMaterial(this.U, prim, { transparent: true })),
      performance: PRIMS.map((_, prim) => bodyMaterial(this.U, prim, { dither: true })),
    };
    this._quality = 'quality';
    this.bodies = new Bodies(
      (prim) => bodyMaterial(this.U, prim),
      (prim) => this._blended.quality[prim],
      ORDER.bodies,
      ORDER.particles,
    );
    for (const mesh of this.bodies.meshes()) this.groups.bodies.add(mesh);

    this.overlay = new Overlay(ORDER.overlay);
    for (const mesh of this.overlay.meshes()) this.overlayScene.add(mesh);

    this.textures = new GroundTextures();
    this.ground = null;
    this.roads = null;
    this.props = null;

    this._skipEmit = false;
    this._size = { w: 0, h: 0, dpr: 0 };
    this.stats = { draws: 0, instances: 0 };
  }

  // ------------------------------------------------------------ the doors
  // `g.renderer.quality` is the effects switch (`app.quality` mirrors it).
  get quality() {
    return this._quality;
  }

  set quality(q) {
    const next = q === 'performance' ? 'performance' : 'quality';
    if (next === this._quality) return;
    this._quality = next;
    this.bodies.setBlendedMaterial((prim) => this._blended[next][prim]);
  }

  get propStats() {
    return this.props ? this.props.stats : { ms: 0, instances: 0, chunks: 0 };
  }

  get roadCount() {
    return this.roads ? this.roads.count : 0;
  }

  // ------------------------------------------------------------------ map
  // A map arrives once per match (`repaintTiles` is the other door, and only for
  // the tiles). Everything the map owns is built here and the previous map's
  // copy is released first, so a second `setMap()` is not a leak.
  setMap(map) {
    this._releaseMap();
    this.textures.build(map);
    this.ground = new Ground(this.U, map, this.textures, ORDER.ground);
    this.roads = new Roads(this.U, map, ORDER.roads);
    this.props = new Props((prim) => this.bodies.opaque[prim].material, ORDER.props);
    this.props.build(map);

    for (const mesh of this.ground.meshes()) this.groups.ground.add(mesh);
    this.groups.roads.add(this.roads.mesh);
    for (const mesh of this.props.meshes()) this.groups.props.add(mesh);
    this.setZoomLevel(0);
  }

  // the previous map's ground, asphalt and props go back to the driver
  _releaseMap() {
    for (const group of [this.groups.ground, this.groups.roads, this.groups.props]) {
      group.clear();
    }
    if (this.ground) this.ground.dispose();
    if (this.roads) this.roads.dispose();
    if (this.props) this.props.dispose();
    this.ground = null;
    this.roads = null;
    this.props = null;
  }

  // rebuild the far terrain grid for a new optics level (cheap, once per step)
  setZoomLevel(level) {
    if (this.ground) this.ground.setZoomLevel(level);
  }

  // The debug tree planter changed the ground under us: the tile texture and the
  // props (trunks, canopies) are drawn from `map.tiles`, so both are rebuilt
  // from the new bitmap. Heights, roads and walls are untouched.
  repaintTiles(map) {
    this.textures.uploadTiles(map);
    this.groups.props.clear();
    this.props.dispose();
    this.props.build(map);
    for (const mesh of this.props.meshes()) this.groups.props.add(mesh);
  }

  // ---------------------------------------------------------------- frame
  // resize + the frame's own uniforms + an empty instance frame, then the
  // layers. `emit` is how the world's bodies get in: the models write into this
  // frame's layers, and everything after that is three's.
  draw(cam, emit) {
    this._beginFrame(cam);
    if (emit && this._takeEmit()) emit(this.bodies);
    this.bodies.flush();
    // the culls: props by distance, the ground by the eye (its patch centre and
    // the rectangle the far mesh cuts out)
    if (this.props) this.props.update(cam);
    if (this.ground) this.ground.update(cam);

    this.device.render(this.scene, this.camera);

    // the overlay floats over the picture with the depth buffer cleared, so its
    // pieces are sorted against themselves and against nothing else
    this.overlay.prepare(this.canvas.clientWidth, this.canvas.clientHeight);
    this.device.autoClear = false;
    this.device.clearDepth();
    this.device.render(this.overlayScene, this.overlayCamera);
    this.device.autoClear = true;
    this.overlay.reset();

    this.stats.draws = this.device.info.render.calls;
    this.stats.instances = this.bodies.instances
      + (this.props ? this.props.visibleInstances() : 0);
  }

  _beginFrame(cam) {
    this._resize();
    syncCamera(this.camera, cam);
    const U = this.U;
    U.uViewProj.value.fromArray(cam.viewProj);
    U.uInvVP.value.fromArray(cam.invViewProj);
    U.uCamPos.value.set(cam.eye[0], cam.eye[1], cam.eye[2]);
    U.uCam.value.copy(U.uCamPos.value);
    U.uSun.value.set(this.sun[0], this.sun[1], this.sun[2]);
    this.device.info.reset();
    // the frame's emission starts empty every tick. The overlay is not part of
    // it: the threat pass fills those buffers before `draw()` is called.
    this.bodies.reset();
  }

  // CSS pixels times the device ratio, capped; the canvas' CSS size is the
  // page's business and is never written here.
  _resize() {
    const dpr = Math.min(window.devicePixelRatio || 1, MAX_DPR);
    const w = this.canvas.clientWidth || 1;
    const h = this.canvas.clientHeight || 1;
    const size = this._size;
    if (size.w === w && size.h === h && size.dpr === dpr) return;
    size.w = w;
    size.h = h;
    size.dpr = dpr;
    this.device.setPixelRatio(dpr);
    this.device.setSize(w, h, false);
    this.device.setClearColor(this._clearColor, 1);
  }

  // ------------------------------------------------------------- overlay
  // The screen-space overlay's three doors, kept on the facade because the game
  // calls them on the renderer; the pieces and their program live in
  // `overlay.js`.
  overlaySun(s) { this.overlay.setSun(s); }

  overlayOrigin(x, y) { this.overlay.setOrigin(x, y); }

  screenArrow(dx, dy, dz, len, head, wid, r, g, b, a) {
    this.overlay.screenArrow(dx, dy, dz, len, head, wid, r, g, b, a);
  }

  // -------------------------------------------------------------- compat
  // The scenery switch (`g.renderer.beginDynamic()` is how a screenshot hides
  // the moving bodies): it drops the next frame's `emit` and empties the layers.
  beginDynamic() {
    this._skipEmit = true;
    this.bodies.reset();
  }

  // and this one only empties the blended set, for an emitter that draws its own
  // particles before the frame
  beginParticles() {
    for (const layer of this.bodies.blended) layer.reset();
  }

  // `emit` asks whether this frame draws the world's bodies.
  _takeEmit() {
    if (this._skipEmit) {
      this._skipEmit = false;
      return false;
    }
    return true;
  }

  // The shape-named doors (`g.renderer.box(...)`) and the two blended ones the
  // particle emitters use. An instance is a primitive and thirteen numbers.
  put(prim, ...a) { this.bodies.put(prim, ...a); }
  push(prim, ...a) { this.bodies.put(prim, ...a); }
  putA(prim, ...a) { this.bodies.putA(prim, ...a); }

  box(...a) { this.bodies.put(PRIM.BOX, ...a); }
  sphere(...a) { this.bodies.put(PRIM.SPHERE, ...a); }
  cone(...a) { this.bodies.put(PRIM.CONE, ...a); }
  cyl(...a) { this.bodies.put(PRIM.CYL, ...a); }
  rock(...a) { this.bodies.put(PRIM.ROCK, ...a); }
  pyramid(...a) { this.bodies.put(PRIM.PYRAMID, ...a); }
  smoke(...a) { this.bodies.put(PRIM.SMOKE, ...a); }
  facade(...a) { this.bodies.put(PRIM.FACADE, ...a); }
  wedge(...a) { this.bodies.put(PRIM.WEDGE, ...a); }
  nose(...a) { this.bodies.put(PRIM.NOSE, ...a); }
  hex(...a) { this.bodies.put(PRIM.HEX, ...a); }

  sphereA(...a) { this.bodies.putA(PRIM.SPHERE, ...a); }
  coneA(...a) { this.bodies.putA(PRIM.CONE, ...a); }

  // ------------------------------------------------------------- lifetime
  dispose() {
    this.bodies.dispose();
    this.overlay.dispose();
    if (this.ground) this.ground.dispose();
    if (this.roads) this.roads.dispose();
    if (this.props) this.props.dispose();
    this.textures.dispose();
    this.device.dispose();
  }
}
