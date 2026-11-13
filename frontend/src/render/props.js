// Props: the trees, the houses and the boulders the map's bitmaps imply.
//
// One plan per tile, used twice: first to count the instances, then to fill
// them.  A tree tile grows a small stand of trunks and canopies, a forest tile a
// thick one - several trees share a single tile, the way OpenTTD plants them -
// and a building tile is a plain block with a roof.
//
// The instances are built once per map into one buffer per primitive, cut into
// 640 m chunks so the pass can cull a town's props in one test instead of a
// thousand.  Everything is static: the pass uploads nothing per frame.

import { treeHash, treeTrunks, TILE_BUILDING, TILE_TREE, TILE_ROCK, TILE_FOREST } from '../map/tiles.js';
import { composeInto } from '../core/math.js';
import { MESH, MESH_COUNT, STRIDE_FLOATS } from './mesh-table.js';

const CHUNK_TILES = 40;              // 640 m prop chunks
const PROP_RANGE = 4600;             // m, the chunk cull radius

export class PropField {
  constructor(gpu) {
    this.gpu = gpu;
    this.batches = null;
    this.visible = Array.from({ length: MESH_COUNT }, () => []);
    this.stats = { ms: 0, instances: 0, chunks: 0 };
  }

  // `repaintTiles` calls this again: trunks and canopies are drawn from
  // `map.tiles`, so the whole field is rebuilt from the new bitmap and the
  // previous buffers are released first.
  build(map) {
    const gl = this.gpu.gl;
    const w = map.w, h = map.h, tile = map.tile;
    const nCx = Math.ceil(w / CHUNK_TILES), nCz = Math.ceil(h / CHUNK_TILES);
    const t0 = performance.now();

    // One plan per tile, used twice: first to count the instances, then to
    // fill them.
    const TRUNK = [0.26, 0.19, 0.13];
    // house walls: concrete, sand, brick, slate
    const HOUSES = [
      [0.62, 0.60, 0.55], [0.72, 0.63, 0.48], [0.58, 0.40, 0.34], [0.44, 0.46, 0.48],
    ];
    const plant = (i, j, t, emit) => {
      const x = (i + 0.5) * tile;
      const z = (j + 0.5) * tile;
      const y = map.heightAt(x, z);
      const r1 = treeHash(i, j), r2 = treeHash(j * 1.7, i * 2.3);
      if (t === TILE_TREE || t === TILE_FOREST) {
        // the trunk spots come from `map/tiles.js`, which is also what the
        // server collides against: a round bursts on the trunk you can see
        for (const tr of treeTrunks(i, j, t, tile)) {
          const { q1, q2, q3 } = tr;
          const px = tr.x, pz = tr.z;
          const py = map.heightAt(px, pz);
          const broad = q3 > 0.45;
          const tall = 10.0 + q3 * 8.0;                     // 10..18 m
          const rad = tall * (broad ? 0.40 : 0.26) * (0.85 + q1 * 0.3);
          const lean = (q2 - 0.5) * 0.10;
          emit(MESH.CYL, px, py + tall * 0.20, pz, q1 * 6.283, lean,
            0.7 + q3 * 0.5, tall * 0.40, 0.7 + q3 * 0.5, TRUNK);
          if (broad) {
            // broadleaf: a fat canopy ball sitting on the trunk
            emit(MESH.SPHERE, px, py + tall * 0.60, pz, q1 * 6.283, lean,
              rad, tall * 0.46, rad,
              [0.12 + q2 * 0.08, 0.26 + q1 * 0.13, 0.10 + q3 * 0.06]);
          } else {
            // conifer: one narrow cone, skirt down to the trunk
            emit(MESH.CONE, px, py + tall * 0.55, pz, q1 * 6.283, lean,
              rad, tall * 0.80, rad,
              [0.08 + q1 * 0.05, 0.19 + q2 * 0.14, 0.09 + q3 * 0.05]);
          }
        }
      } else if (t === TILE_BUILDING) {
        // A house: as tall as the wall column the server stored for this tile,
        // so the block you see is exactly the block a round stops on.  The
        // facade mesh paints the storeys and windows, the slab on top is the
        // roof.  Every tile of a block shares one column height, so a row of
        // them reads as one building with a level roof.
        const sy = map.walls[j * w + i] || 8.0;
        const sx = tile * (0.88 + r1 * 0.10);
        const sz = tile * (0.88 + r2 * 0.10);
        const wall = HOUSES[Math.floor(treeHash(i * 0.7, j * 1.3) * HOUSES.length) % HOUSES.length];
        // A town stands on its hill, so the facet under a block is a slope: the
        // box is drawn from the *lowest* corner of the tile up to the roofline, so
        // it is buried on the uphill side instead of hovering over the downhill
        // one.  The roof stays where the wall column says it is.
        const hh = map.heights;
        const hlo = Math.min(hh[j * w + i], hh[j * w + i + 1],
          hh[(j + 1) * w + i], hh[(j + 1) * w + i + 1]);
        const foot = Math.min(hlo, y) - 0.4;
        const tall = y + sy - foot;
        emit(MESH.FACADE, x, foot + tall * 0.5, z, 0, 0, sx, tall, sz, wall);
        // roof: a flat slab with a parapet, plus the odd plant box on top
        const dark = r1 > 0.5 ? 0.46 : 0.52;
        emit(MESH.BOX, x, y + sy + 0.22, z, 0, 0, sx * 1.03, 0.45, sz * 1.03,
          [wall[0] * dark, wall[1] * dark, wall[2] * dark]);
        if (r2 > 0.72) {
          emit(MESH.BOX, x + (r1 - 0.5) * sx * 0.5, y + sy + 0.6 + 1.1,
            z + (r2 - 0.5) * sz * 0.5, 0, 0, sx * 0.3, 2.2, sz * 0.3,
            [0.34, 0.35, 0.36]);
        }
        if (r2 < 0.22) {
          // a low annex against one side, well under the wall column, on the
          // ground of its own tile as well
          const ax = x + (r1 > 0.5 ? 1 : -1) * sx * 0.44;
          const az = z + (r2 - 0.5) * sz * 0.4;
          const afoot = Math.min(map.heightAt(ax, az), foot) - 0.3;
          const atall = y + sy * 0.72 - afoot;
          emit(MESH.FACADE, ax, afoot + atall * 0.5, az, 0, 0,
            sx * 0.42, atall, sz * 0.42,
            [wall[0] * 0.94, wall[1] * 0.90, wall[2] * 0.86]);
        }
      } else if (t === TILE_ROCK) {
        // two or three angular chunks per tile, tipped over at random angles,
        // half sunk into the ground: low poly boulders
        const sz0 = 4.6 + r1 * 5.0;
        const rock = [0.30 + r1 * 0.08, 0.285 + r1 * 0.07, 0.265 + r2 * 0.06];
        for (let q = 0; q < 3; q++) {
          const h1 = treeHash(i + q * 3.1, j + q * 7.7);
          const h2 = treeHash(j * 1.3 + q, i * 2.9 - q);
          const a = r2 * 6.283 + q * 2.1;
          const off = sz0 * 0.32 * q;
          const bw = sz0 * (0.7 - q * 0.16) * (0.85 + h1 * 0.4);
          const g = 0.88 + h2 * 0.24;
          emit(MESH.ROCK,
            x + Math.cos(a) * off, y + bw * 0.26, z + Math.sin(a) * off,
            a, (h1 - 0.5) * 0.5, bw, bw * (0.62 + h2 * 0.5), bw,
            [rock[0] * g, rock[1] * g, rock[2] * g]);
        }
      }
    };

    const want = (t) => t === TILE_BUILDING || t === TILE_TREE || t === TILE_ROCK || t === TILE_FOREST;
    const counts = new Array(MESH_COUNT).fill(0);
    const tally = (mesh) => { counts[mesh]++; };
    for (let j = 0; j < h; j++) {
      for (let i = 0; i < w; i++) {
        const t = map.tiles[j * w + i];
        if (want(t)) plant(i, j, t, tally);
      }
    }
    const datas = counts.map((c) => new Float32Array(Math.max(1, c) * STRIDE_FLOATS));
    const fills = new Array(MESH_COUNT).fill(0);
    const chunkFirst = Array.from({ length: MESH_COUNT }, () => new Int32Array(nCx * nCz).fill(-1));
    const chunkCount = Array.from({ length: MESH_COUNT }, () => new Int32Array(nCx * nCz));
    let ci = 0;
    const add = (mesh, x, y, z, yaw, pitch, sx, sy, sz, col) => {
      const arr = datas[mesh];
      const idx = fills[mesh];
      if ((idx + 1) * STRIDE_FLOATS > arr.length) return;
      if (chunkFirst[mesh][ci] < 0) chunkFirst[mesh][ci] = idx;
      const base = idx * STRIDE_FLOATS;
      composeInto(arr, base, x, y, z, yaw, pitch || 0, sx, sy, sz);
      arr[base + 16] = col[0]; arr[base + 17] = col[1];
      arr[base + 18] = col[2]; arr[base + 19] = 1;
      fills[mesh] = idx + 1;
      chunkCount[mesh][ci]++;
    };

    for (let cz = 0; cz < nCz; cz++) {
      for (let cx = 0; cx < nCx; cx++) {
        ci = cz * nCx + cx;
        const i0 = cx * CHUNK_TILES, j0 = cz * CHUNK_TILES;
        const i1 = Math.min(w, i0 + CHUNK_TILES), j1 = Math.min(h, j0 + CHUNK_TILES);
        for (let j = j0; j < j1; j++) {
          for (let i = i0; i < i1; i++) {
            const t = map.tiles[j * w + i];
            if (want(t)) plant(i, j, t, add);
          }
        }
      }
    }

    this.dispose();
    this.batches = Array.from({ length: MESH_COUNT }, (_, m) => {
      const count = fills[m];
      if (count === 0) return null;
      const vbo = this.gpu.buffer(gl.ARRAY_BUFFER, datas[m].subarray(0, count * STRIDE_FLOATS), gl.STATIC_DRAW);
      const chunks = [];
      for (let cz = 0; cz < nCz; cz++) {
        for (let cx = 0; cx < nCx; cx++) {
          const ci2 = cz * nCx + cx;
          if (!chunkCount[m][ci2]) continue;
          chunks.push({
            cx: (cx + 0.5) * CHUNK_TILES * tile,
            cz: (cz + 0.5) * CHUNK_TILES * tile,
            first: chunkFirst[m][ci2],
            count: chunkCount[m][ci2],
          });
        }
      }
      return { vbo, count, chunks };
    });
    this.stats = {
      ms: Math.round(performance.now() - t0),
      instances: fills.reduce((a, b) => a + b, 0),
      chunks: this.batches.reduce((a, b) => a + (b ? b.chunks.length : 0), 0),
    };
  }

  // Everything static, cut into 640 m chunks: the only per-frame work is the
  // box test against the eye and the submit.
  draw(R, cam) {
    if (!this.batches) return;
    const R2 = PROP_RANGE * PROP_RANGE;
    const ex = cam.eye[0], ez = cam.eye[2];
    for (let m = 0; m < this.batches.length; m++) {
      const batch = this.batches[m];
      if (!batch) continue;
      const cmds = this.visible[m];
      let n = 0;
      for (const c of batch.chunks) {
        const dx = c.cx - ex;
        const dz = c.cz - ez;
        if (dx * dx + dz * dz < R2) cmds[n++] = c;
      }
      cmds.length = n;
      R.submit(m, batch.vbo, cmds);
    }
  }

  dispose() {
    for (const b of this.batches || []) if (b) this.gpu.release(b.vbo);
    this.batches = null;
  }
}

export const props = {
  name: 'props',
  draw(R, cam) { R.props.draw(R, cam); },
};
