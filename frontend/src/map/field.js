// The heightfield and the bitmaps that travel with it: the ground under every
// point, what kind of tile stands on it, where the roads and the lanes run.
// This is the client's copy of the server's `Terrain`, and `heightAt` is the
// one function both hosts have to agree on (`tests/cases/height.json`).
//
// Importable in Node (`tests/frontend/height` builds a `MapData` directly):
// `atob` and `DecompressionStream` are touched inside `load` and nowhere else.

import {
  TILE_BUILDING, TILE_ROCK, TILE_SOLID, TILE_TREE, TILE_FOREST, canopyOf, treeTrunks,
} from './tiles.js';

function b64ToBytes(b64) {
  const bin = atob(b64);
  const out = new Uint8Array(bin.length);
  for (let i = 0; i < bin.length; i++) out[i] = bin.charCodeAt(i);
  return out;
}

async function inflate(bytes) {
  if (typeof DecompressionStream === 'undefined') throw new Error('browser lacks DecompressionStream');
  const ds = new DecompressionStream('deflate');
  const stream = new Blob([bytes]).stream().pipeThrough(ds);
  return new Uint8Array(await new Response(stream).arrayBuffer());
}

export class MapData {
  constructor(w, h, tile, heights, tiles, roads, walls) {
    this.w = w;
    this.h = h;
    this.tile = tile;
    this.heights = heights;      // Float32Array(w*h), row major [j*w+i]
    this.tiles = tiles;          // Uint8Array(w*h)
    this.roads = roads;          // Uint8Array(w*h), ROAD_* piece bits
    this.walls = walls;          // Uint8Array(w*h), wall/roof height in metres
    this.trunkCache = new Map(); // tile index -> trunk layout, filled on demand
    this.canopyCache = new Map();   // tile index -> canopies reaching into it
  }

  static async load(info, lanes) {
    const hBytes = await inflate(b64ToBytes(info.heights));
    const u16 = new Uint16Array(hBytes.buffer, hBytes.byteOffset, hBytes.byteLength >> 1);
    // heights travel as u16 `hunit` steps (`Netcode.H_UNIT`), so the plane is
    // half a metre wide per vertex and the ground between vertices is a slope
    const unit = info.hunit || 0.05;
    const heights = new Float32Array(u16.length);
    for (let i = 0; i < u16.length; i++) heights[i] = u16[i] * unit;
    const tiles = await inflate(b64ToBytes(info.tiles));
    const roads = info.roads ? await inflate(b64ToBytes(info.roads)) : new Uint8Array(tiles.length);
    const walls = info.walls ? await inflate(b64ToBytes(info.walls)) : new Uint8Array(tiles.length);
    const map = new MapData(info.w, info.h, info.tile, heights, tiles, roads, walls);
    map.hunit = unit;            // the step the plane was decoded at
    map.lanes = lanes || [];     // road centrelines, in world metres
    return map;
  }

  // The ground under a point: the one triangle of the tile the point falls in,
  // split along the north-east to south-west diagonal.  Mirrors
  // `Terrain.height_at` line for line, and it is the same two triangles
  // `renderer._buildTilePatch` draws - so a reticle, a predicted impact point
  // and the facet under a hull are one surface.
  heightAt(x, z) {
    const fx = Math.min(Math.max(x / this.tile, 0), this.w - 1.001);
    const fz = Math.min(Math.max(z / this.tile, 0), this.h - 1.001);
    const i = fx | 0, j = fz | 0;
    const tx = fx - i, tz = fz - j;
    const h = this.heights;
    const a = h[j * this.w + i], b = h[j * this.w + i + 1];
    const c = h[(j + 1) * this.w + i], d = h[(j + 1) * this.w + i + 1];
    if (tx + tz <= 1) return a + (b - a) * tx + (c - a) * tz;
    return d + (c - d) * (1 - tx) + (b - d) * (1 - tz);
  }

  tileAt(x, z) {
    const i = x / this.tile | 0, j = z / this.tile | 0;
    if (i < 0 || j < 0 || i >= this.w || j >= this.h) return TILE_ROCK;
    return this.tiles[j * this.w + i];
  }

  blocked(x, z) {
    const t = this.tileAt(x, z);
    return t === TILE_BUILDING || t === TILE_ROCK || t === TILE_SOLID;
  }

  // mirrors Terrain.inside on the server: past the map edge there is no
  // ground, heightAt just keeps handing back the border height
  inside(x, z) {
    return x >= 0 && z >= 0 && x <= (this.w - 1) * this.tile && z <= (this.h - 1) * this.tile;
  }

  // the trunk layout of a tile, laid out once per tile the eye ever asks about
  trunksAt(i, j) {
    const k = j * this.w + i;
    let tr = this.trunkCache.get(k);
    if (tr === undefined) {
      const t = this.tiles[k];
      tr = t === TILE_TREE || t === TILE_FOREST ? treeTrunks(i, j, t, this.tile) : [];
      this.trunkCache.set(k, tr);
    }
    return tr;
  }

  // Every canopy that reaches into tile (i, j), the mirror of
  // Terrain._canopies: one ball or one cone per tree, at the numbers the
  // renderer builds the meshes from (`_buildProps`), plus the trees of the 3x3
  // block around the tile - the leaves of the next tile over hang over this
  // one, and the eye sees them.
  canopiesAt(i, j) {
    const k = j * this.w + i;
    let c = this.canopyCache.get(k);
    if (c !== undefined) return c;
    c = [];
    let near = false;
    for (let jj = j - 1; jj <= j + 1 && !near; jj++) {
      if (jj < 0 || jj >= this.h) continue;
      for (let ii = i - 1; ii <= i + 1; ii++) {
        if (ii < 0 || ii >= this.w) continue;
        const t = this.tiles[jj * this.w + ii];
        if (t === TILE_TREE || t === TILE_FOREST) { near = true; break; }
      }
    }
    if (near) {
      const x0 = i * this.tile, z0 = j * this.tile;
      for (let jj = j - 1; jj <= j + 1; jj++) {
        if (jj < 0 || jj >= this.h) continue;
        for (let ii = i - 1; ii <= i + 1; ii++) {
          if (ii < 0 || ii >= this.w) continue;
          const t = this.tiles[jj * this.w + ii];
          if (t !== TILE_TREE && t !== TILE_FOREST) continue;
          for (const tr of this.trunksAt(ii, jj)) {
            const cy = canopyOf(this, tr);
            const dx = cy.x < x0 ? x0 - cy.x : (cy.x > x0 + this.tile ? cy.x - x0 - this.tile : 0);
            const dz = cy.z < z0 ? z0 - cy.z : (cy.z > z0 + this.tile ? cy.z - z0 - this.tile : 0);
            if (dx * dx + dz * dz <= cy.rad * cy.rad) c.push(cy);
          }
        }
      }
    }
    this.canopyCache.set(k, c);
    return c;
  }
}
