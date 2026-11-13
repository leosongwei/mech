// The ground: the far LOD grid, the near tile patch, and the pass that draws
// them (plus the asphalt, which is `roads.js` and follows the patch).
//
// * far: one static radial grid, re-centred on the eye in whole tiles.  Heights
//   are fetched in the vertex shader, so the 16 km map never touches the CPU.
// * near: a patch of the tile grid that follows the eye.  Its vertices are tile
//   corners, so the facets are the tiles and an OpenTTD slope reads as a slope.
// * the far program cuts the patch's rectangle out of its own surface, and the
//   patch clears that cut, so the two resolutions overlap instead of meeting.

import { TERRAIN_VS, TILE_VS, TERRAIN_FS } from './shaders/terrain.js';
import { roads } from './roads.js';

// Optics: the radial terrain grid keeps the plain tile spacing for `a0` rings
// (no merged quads inside that radius) and doubles its ring spacing every `g`
// rings after that, so a magnified sight sees the ground at tile resolution far
// out.  `k` is the half grid size.  `a0` follows the magnification - a 20x
// sight splits a tile into pixels at 3 km, so nothing is merged out to there -
// and the cover shrinks with it, since the cone does: 17.6 km at 1x, 5 km at
// 20x, all of it deep in the fog at the far end.
export const TERRAIN_LOD = [
  { g: 26, k: 174, a0: 48 },     // 1x   tile detail to 768 m, grid to 17.6 km
  { g: 44, k: 193, a0: 62 },     // 4x   tile detail to 1 km,  grid to 8 km
  { g: 80, k: 247, a0: 109 },    // 10x  tile detail to 1.7 km, grid to 6 km
  { g: 64, k: 267, a0: 188 },    // 20x  tile detail to 3 km,  grid to 5 km
];

// the tile patch that carries the near ground: TILE_PATCH tiles either way,
// re-centred on the eye one whole tile at a time
export const TILE_PATCH = 48;

const TERRAIN_UNIFORMS = ['uViewProj', 'uHeight', 'uTiles', 'uMapSize', 'uTile', 'uG', 'uA0',
  'uCam', 'uSun', 'uFogColor', 'uFogDensity', 'uFar', 'uNear'];
const TILE_UNIFORMS = ['uViewProj', 'uHeight', 'uTiles', 'uMapSize', 'uTile', 'uOrigin',
  'uCam', 'uSun', 'uFogColor', 'uFogDensity', 'uFar', 'uNear'];

// A rectilinear grid of `n+1` x `n+1` cells as two triangles per cell, split the
// way `heightAt` splits a tile (a-c-b, b-c-d) so the patch's facets are the
// ground's facets.
function gridMesh(gpu, n) {
  const gl = gpu.gl;
  const cells = new Float32Array((n + 1) * (n + 1) * 2);
  let o = 0;
  for (let j = 0; j <= n; j++) {
    for (let i = 0; i <= n; i++) {
      cells[o++] = i;
      cells[o++] = j;
    }
  }
  const idx = new Uint32Array(n * n * 6);
  o = 0;
  for (let j = 0; j < n; j++) {
    for (let i = 0; i < n; i++) {
      const a = j * (n + 1) + i, b = a + 1, c = a + n + 1, d = c + 1;
      idx[o++] = a; idx[o++] = c; idx[o++] = b;
      idx[o++] = b; idx[o++] = c; idx[o++] = d;
    }
  }
  return {
    vbo: gpu.buffer(gl.ARRAY_BUFFER, cells, gl.STATIC_DRAW),
    ibo: gpu.buffer(gl.ELEMENT_ARRAY_BUFFER, idx, gl.STATIC_DRAW),
    count: idx.length,
  };
}

// The radial grid: `2K+1` vertices a side, centred on the eye, so `aCell` is a
// signed ring index and the vertex shader turns it into metres.
function radialMesh(gpu, K) {
  const gl = gpu.gl;
  const n = 2 * K + 1;
  const cells = new Float32Array(n * n * 2);
  let o = 0;
  for (let j = -K; j <= K; j++) {
    for (let i = -K; i <= K; i++) {
      cells[o++] = i;
      cells[o++] = j;
    }
  }
  const idx = new Uint32Array((n - 1) * (n - 1) * 6);
  o = 0;
  for (let j = 0; j < n - 1; j++) {
    for (let i = 0; i < n - 1; i++) {
      const a = j * n + i, b = a + 1, c = a + n, d = c + 1;
      idx[o++] = a; idx[o++] = c; idx[o++] = b;
      idx[o++] = b; idx[o++] = c; idx[o++] = d;
    }
  }
  return {
    vbo: gpu.buffer(gl.ARRAY_BUFFER, cells, gl.STATIC_DRAW),
    ibo: gpu.buffer(gl.ELEMENT_ARRAY_BUFFER, idx, gl.STATIC_DRAW),
    count: idx.length,
  };
}

export class Terrain {
  constructor(gpu) {
    const gl = gpu.gl;
    this.gpu = gpu;
    this.pTerrain = gpu.program('terrain', TERRAIN_VS, TERRAIN_FS, TERRAIN_UNIFORMS);
    this.pTile = gpu.program('tile', TILE_VS, TERRAIN_FS, TILE_UNIFORMS);
    this.aCell = gpu.attrib(this.pTerrain.prog, 'aCell');
    this.aCellTile = gpu.attrib(this.pTile.prog, 'aCell');
    // the sampler units are program state that never changes: heights on 0, the
    // tile map on 1
    gpu.useProgram(this.pTerrain);
    gl.uniform1i(this.pTerrain.u.uHeight, 0);
    gl.uniform1i(this.pTerrain.u.uTiles, 1);
    gpu.useProgram(this.pTile);
    gl.uniform1i(this.pTile.u.uHeight, 0);
    gl.uniform1i(this.pTile.u.uTiles, 1);
    gpu.useProgram(null);

    // The near patch: one patch of the tile grid, kept around the eye by a
    // uniform.  Its vertices are tile corners, so the facets are the tiles.
    this.patch = gridMesh(gpu, 2 * TILE_PATCH);
    this.grid = null;
    this.terrainG = 0;
    this.terrainA0 = 0;
  }

  // rebuild the far grid for a new optics level (cheap, once per step)
  setZoomLevel(level) {
    const lod = TERRAIN_LOD[Math.max(0, Math.min(TERRAIN_LOD.length - 1, level))];
    if (this.grid && this.terrainG === lod.g && this.terrainA0 === lod.a0) return;
    this.terrainG = lod.g;
    this.terrainA0 = lod.a0;
    this.gpu.release(this.grid && this.grid.vbo);
    this.gpu.release(this.grid && this.grid.ibo);
    this.grid = radialMesh(this.gpu, lod.k);
  }

  dispose() {
    if (this.grid) {
      this.gpu.release(this.grid.vbo);
      this.gpu.release(this.grid.ibo);
      this.grid = null;
    }
    if (this.patch) {
      this.gpu.release(this.patch.vbo);
      this.gpu.release(this.patch.ibo);
      this.patch = null;
    }
  }

  draw(R, cam) {
    const gl = this.gpu.gl;
    const map = R.map;
    const tile = map.tile;
    const gpu = this.gpu;
    // the tile patch is centred on the eye, snapped to whole tiles so every
    // vertex stays exactly on a tile corner
    const K = TILE_PATCH;
    // floor, not round: the far grid's flat core is placed on the tile lattice
    // from `floor(uCam / uTile)`, so this rectangle has to be cut out of the same
    // lattice or the two meshes disagree by a tile along its edge
    const ox = Math.min(Math.max(Math.floor(cam.eye[0] / tile) - K, 0), map.w - 1 - 2 * K);
    const oz = Math.min(Math.max(Math.floor(cam.eye[2] / tile) - K, 0), map.h - 1 - 2 * K);
    // World bounds of the patch, handed to the far program so it can cut the same
    // rectangle out of its own mesh.  The cut is one tile *inside* the patch: the
    // far mesh then keeps drawing under the patch's outer ring, and the two
    // surfaces overlap instead of meeting.  A butt joint between two grids of
    // different resolution is a visible step at grazing angles whatever the phase
    // (no skirts, no stitching); an overlap is invisible and costs one ring.
    const patch = [(ox + 1) * tile, (oz + 1) * tile, (ox + 2 * K - 1) * tile, (oz + 2 * K - 1) * tile];

    const common = (u) => {
      gl.uniformMatrix4fv(u.uViewProj, false, cam.viewProj);
      gl.uniform2f(u.uMapSize, map.w, map.h);
      gl.uniform1f(u.uTile, tile);
      gl.uniform3fv(u.uCam, cam.eye);
      gl.uniform3fv(u.uSun, R.sun);
      gl.uniform3fv(u.uFogColor, R.fogColor);
      gl.uniform1f(u.uFogDensity, R.fogDensity);
      gl.uniform1f(u.uFar, R.far);
      gl.uniform4f(u.uNear, patch[0], patch[1], patch[2], patch[3]);
      gpu.bindTexture(0, R.textures.height);
      gpu.bindTexture(1, R.textures.tiles);
    };

    // far ground first, leaving the patch area to the tile grid
    gpu.useProgram(this.pTerrain);
    common(this.pTerrain.u);
    gl.uniform1f(this.pTerrain.u.uG, this.terrainG);
    gl.uniform1f(this.pTerrain.u.uA0, this.terrainA0);
    gl.bindBuffer(gl.ARRAY_BUFFER, this.grid.vbo);
    gl.enableVertexAttribArray(this.aCell);
    gl.vertexAttribPointer(this.aCell, 2, gl.FLOAT, false, 0, 0);
    gl.bindBuffer(gl.ELEMENT_ARRAY_BUFFER, this.grid.ibo);
    gl.drawElements(gl.TRIANGLES, this.grid.count, gl.UNSIGNED_INT, 0);
    gl.disableVertexAttribArray(this.aCell);
    R.stats.draws++;

    // near ground: the tile grid, flat shaded facets and all.  Its own bounds
    // are cleared in uNear, or it would discard every fragment it owns.
    gpu.useProgram(this.pTile);
    common(this.pTile.u);
    gl.uniform4f(this.pTile.u.uNear, 0, 0, -1, -1);
    gl.uniform2f(this.pTile.u.uOrigin, ox, oz);
    gl.bindBuffer(gl.ARRAY_BUFFER, this.patch.vbo);
    gl.enableVertexAttribArray(this.aCellTile);
    gl.vertexAttribPointer(this.aCellTile, 2, gl.FLOAT, false, 0, 0);
    gl.bindBuffer(gl.ELEMENT_ARRAY_BUFFER, this.patch.ibo);
    gl.drawElements(gl.TRIANGLES, this.patch.count, gl.UNSIGNED_INT, 0);
    gl.disableVertexAttribArray(this.aCellTile);
    R.stats.draws++;

    // then the roads laid on top of it, in the ground's own state - the asphalt
    // is part of the terrain pass, the way it is part of the terrain
    roads.draw(R, cam);
  }
}

export const terrain = {
  name: 'terrain',
  draw(R, cam) { R.terrain.draw(R, cam); },
};
