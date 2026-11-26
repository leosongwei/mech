// The ground: the far LOD grid, the near tile patch, and the pass that places
// them. (The asphalt is `roads.js` and follows the patch.)
//
// * far: one static radial grid, re-centred on the eye in whole tiles. Heights
//   are fetched in the vertex shader, so the 16 km map never touches the CPU.
// * near: a patch of the tile grid that follows the eye. Its vertices are tile
//   corners, so the facets are the tiles and an OpenTTD slope reads as a slope.
// * the far program cuts the patch's rectangle out of its own surface, and the
//   patch clears that cut, so the two resolutions overlap instead of meeting.

import { BufferGeometry, BufferAttribute, Mesh } from 'three';

import { groundMaterials } from './materials.js';

// Optics: the radial grid keeps the plain tile spacing for `a0` rings (no merged
// quads inside that radius) and doubles its ring spacing every `g` rings after
// that, so a magnified sight sees the ground at tile resolution far out. `k` is
// the half grid size.
export const TERRAIN_LOD = [
  { g: 26, k: 174, a0: 48 },     // 1x   tile detail to 768 m, grid to 17.6 km
  { g: 44, k: 193, a0: 62 },     // 4x   tile detail to 1 km,  grid to 8 km
  { g: 80, k: 247, a0: 109 },    // 10x  tile detail to 1.7 km, grid to 6 km
  { g: 64, k: 267, a0: 188 },    // 20x  tile detail to 3 km,  grid to 5 km
];

// the tile patch that carries the near ground: this many tiles either way,
// re-centred on the eye one whole tile at a time
export const TILE_PATCH = 48;

// A rectilinear grid of (n+1)^2 cells as two triangles per cell, split the way
// `heightAt` splits a tile (a-c-b, b-c-d) so the patch's facets are the
// ground's facets. The cell index rides in `position.xy` because that attribute
// is what three counts vertices by.
function gridGeometry(n, offset) {
  const cells = new Float32Array((n + 1) * (n + 1) * 3);
  let o = 0;
  for (let j = 0; j <= n; j++) {
    for (let i = 0; i <= n; i++) {
      cells[o++] = i - offset;
      cells[o++] = j - offset;
      cells[o++] = 0;
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
  const g = new BufferGeometry();
  g.setAttribute('position', new BufferAttribute(cells, 3));
  g.setIndex(new BufferAttribute(idx, 1));
  return g;
}

export class Ground {
  constructor(U, map, textures, order) {
    this.map = map;
    this.materials = groundMaterials(U, map, textures.height, textures.tiles);
    const u = this.materials.uniforms;

    // the near patch: one patch of the tile grid, kept around the eye by a
    // uniform. Its vertices are tile corners, so its facets are the tiles.
    this.patch = new Mesh(gridGeometry(2 * TILE_PATCH, 0), this.materials.patch);
    this.patch.frustumCulled = false;
    this.patch.matrixAutoUpdate = false;
    this.patch.renderOrder = order;

    this.grid = null;
    this.g = 0;
    this.a0 = 0;
    this.setZoomLevel(0);
    u.uNear.value.set(0, 0, -1, -1);
  }

  // rebuild the far grid for a new optics level (cheap, once per step)
  setZoomLevel(level) {
    const lod = TERRAIN_LOD[Math.max(0, Math.min(TERRAIN_LOD.length - 1, level))];
    if (this.grid && this.g === lod.g && this.a0 === lod.a0) return;
    this.g = lod.g;
    this.a0 = lod.a0;
    const n = 2 * lod.k;
    const geometry = gridGeometry(n, lod.k);
    if (this.grid) {
      this.grid.geometry.dispose();
      this.grid.geometry = geometry;
    } else {
      this.grid = new Mesh(geometry, this.materials.far);
      this.grid.frustumCulled = false;
      this.grid.matrixAutoUpdate = false;
    }
    const u = this.materials.uniforms;
    u.uG.value = lod.g;
    u.uA0.value = lod.a0;
  }

  // Follow the eye: the patch walks a tile at a time, and the far grid is told
  // which rectangle the patch has taken.
  update(cam) {
    const map = this.map;
    const tile = map.tile;
    const K = TILE_PATCH;
    // floor, not round: the far grid's flat core is placed on the tile lattice
    // from `floor(uCam / uTile)`, so this rectangle has to be cut out of the same
    // lattice or the two meshes disagree by a tile along its edge
    const ox = Math.min(Math.max(Math.floor(cam.eye[0] / tile) - K, 0), map.w - 1 - 2 * K);
    const oz = Math.min(Math.max(Math.floor(cam.eye[2] / tile) - K, 0), map.h - 1 - 2 * K);
    const u = this.materials.uniforms;
    u.uOrigin.value.set(ox, oz);
    // World bounds of the patch, handed to the far program so it can cut the
    // same rectangle out of its own mesh. The cut is one tile *inside* the
    // patch: the far mesh then keeps drawing under the patch's outer ring, and
    // the two surfaces overlap instead of meeting. A butt joint between two
    // grids of different resolution is a visible step at grazing angles whatever
    // the phase (no skirts, no stitching); an overlap is invisible.
    u.uNear.value.set((ox + 1) * tile, (oz + 1) * tile,
      (ox + 2 * K - 1) * tile, (oz + 2 * K - 1) * tile);
  }

  meshes() {
    return this.grid ? [this.grid, this.patch] : [this.patch];
  }

  dispose() {
    this.patch.geometry.dispose();
    if (this.grid) this.grid.geometry.dispose();
    this.materials.far.dispose();
    this.materials.patch.dispose();
  }
}
