// The materials, and the frame-wide uniforms they all read.
//
// Every program here is raw GLSL 3.00: the game's look is a set of specific
// equations - a log depth write, a fog law, a flat-faceted ground, a dithered
// impact pass - and three's stock materials would have to be rebuilt to say
// them. What three does for these is the part that is plumbing: the context,
// the program cache, the attribute bindings, the state changes, the draw calls.
//
// The frame's own numbers (projection, eye, sun, fog, far plane) are *one*
// uniform object per value, shared by reference between every material of the
// frame, so a frame updates each value in one place and no pass can leave a
// stale copy behind.

import {
  RawShaderMaterial, GLSL3, Matrix4, Vector2, Vector3, Vector4, DoubleSide,
} from 'three';

import { WORLD_VS, WORLD_FS } from './shaders/world.js';
import { SKY_VS, SKY_FS } from './shaders/sky.js';
import { FAR_VS, PATCH_VS, GROUND_FS } from './shaders/ground.js';
import { ROAD_VS, ROAD_FS } from './shaders/road.js';
import { OVERLAY_VS, OVERLAY_FS } from './shaders/overlay.js';
import { PRIMS } from './primitives.js';

// The sun is a fixed direction. Everything lit in the game is lit by it, and the
// sky's glow is drawn on the same direction, so a hull's lit side and the sun in
// the sky are the same fact.
export const SUN = new Vector3(0.55, 0.72, 0.42).normalize();

// The world's numbers. `uViewProj` is written once a frame from the frame's
// camera; the rest are the constants of the picture.
export function frameUniforms() {
  return {
    uViewProj: { value: new Matrix4() },
    uInvVP: { value: new Matrix4() },
    uSun: { value: SUN },
    uCamPos: { value: new Vector3() },
    // the ground and the asphalt measure their fog by this one, which is the
    // same eye under a name the vertex shaders can reach
    uCam: { value: new Vector3() },
    uFogColor: { value: new Vector3(0.66, 0.71, 0.75) },
    uFogDensity: { value: 1 / 3600 },
    uFar: { value: 32000 },
  };
}

// One instanced body program, compiled for the primitive it draws. `variant`
// adds the two per-body behaviours and the impact pass's mask; the tables live
// in `primitives.js` and in the caller, so a primitive's material is decided by
// what the primitive is.
export function bodyMaterial(U, prim, { dither = false, transparent = false } = {}) {
  const info = PRIMS[prim];
  const defines = {};
  if (info.facade) defines.FACADE = 1;
  if (info.unlit) defines.UNLIT = 1;
  if (dither) defines.DITHER = 1;
  return new RawShaderMaterial({
    glslVersion: GLSL3,
    defines,
    uniforms: U,
    vertexShader: WORLD_VS,
    fragmentShader: WORLD_FS,
    // nothing in the world is culled by winding: a hull is drawn from the
    // inside as well as the outside, and a part that pokes through another is
    // not a hole
    side: DoubleSide,
    transparent,
    depthTest: true,
    // the solid bodies own the depth buffer; the two impact variants - blended
    // and dithered - leave it alone, so a particle's place among particles is
    // the order it was emitted in rather than its z
    depthWrite: !transparent && !dither,
    // the blended pass keeps the emitters' order, which is what makes one puff
    // sit in front of the next; a per-object sort would have nothing to sort by
  });
}

export function skyMaterial(U) {
  return new RawShaderMaterial({
    glslVersion: GLSL3,
    uniforms: U,
    vertexShader: SKY_VS,
    fragmentShader: SKY_FS,
    depthTest: false,
    depthWrite: false,
    side: DoubleSide,
  });
}

// The two ground programs share one fragment shader and their own uniforms: the
// height and tile textures, and the lens-independent facts of the map.
function groundUniforms(U, map, height, tiles) {
  return {
    ...U,
    uHeight: { value: height },
    uTiles: { value: tiles },
    uMapSize: { value: new Vector2(map.w, map.h) },
    uTile: { value: map.tile },
    uNear: { value: new Vector4(0, 0, -1, -1) },
    uOrigin: { value: new Vector2(0, 0) },
    uG: { value: 26 },
    uA0: { value: 48 },
  };
}

// The far mesh cuts the patch's rectangle out of its own surface; the patch is
// compiled without the cut (`PATCH`), because its own bounds *are* the cut. The
// two programs share one uniform object, so the rectangle is written in one
// place and only the far mesh reads it.
export function groundMaterials(U, map, height, tiles) {
  const shared = groundUniforms(U, map, height, tiles);
  const make = (vertexShader, defines) => new RawShaderMaterial({
    glslVersion: GLSL3,
    defines,
    uniforms: shared,
    vertexShader,
    fragmentShader: GROUND_FS,
    side: DoubleSide,
  });
  return {
    far: make(FAR_VS, {}),
    patch: make(PATCH_VS, { PATCH: 1 }),
    uniforms: shared,
  };
}

export function roadMaterial(U) {
  return new RawShaderMaterial({
    glslVersion: GLSL3,
    uniforms: U,
    vertexShader: ROAD_VS,
    fragmentShader: ROAD_FS,
    side: DoubleSide,
  });
}

// The overlay draws in the HUD's own pixels and is the one place three's camera
// is not the frame's: `uViewProj` is the HUD's projection, built about the ring
// every arrow springs from, and `uFar` is 2 so the shader's log depth becomes a
// plain log2 of w over the [1, 1.1] the pieces live in.
export function overlayMaterial() {
  return new RawShaderMaterial({
    glslVersion: GLSL3,
    uniforms: {
      uViewProj: { value: new Matrix4() },
      uSun: { value: new Vector3(0.55, -0.72, -0.42) },
      uCamPos: { value: new Vector3() },
      uFar: { value: 2 },
      uPiece: { value: 1 },
    },
    vertexShader: OVERLAY_VS,
    fragmentShader: OVERLAY_FS,
    side: DoubleSide,
    transparent: true,
    depthTest: true,
    depthWrite: true,
  });
}
