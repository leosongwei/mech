// Props: the trees, the houses and the boulders the map's bitmaps imply.
//
// One plan per tile, used twice: first to count the instances, then to fill
// them. A tree tile grows a small stand of trunks and canopies, a forest tile a
// thick one - several trees share a single tile, the way OpenTTD plants them -
// and a building tile is a plain block with a roof.
//
// The instances are built once per map, one static layer per primitive per 640 m
// chunk, so a town's props are culled by one box test instead of a thousand.
// Nothing here is uploaded after the map is built.

import { Sphere, Vector3 } from 'three';

import {
  treeHash, treeTrunks, TILE_BUILDING, TILE_TREE, TILE_ROCK, TILE_FOREST,
} from '../map/tiles.js';
import { PRIM, PRIM_COUNT } from './primitives.js';
import { StaticLayer } from './instances.js';

const CHUNK_TILES = 40;              // 640 m prop chunks
const PROP_RANGE = 4600;             // m, the chunk cull radius
// a chunk's bounding sphere: the half diagonal of its square, plus the margin a
// tall tree or a roof pokes out by
const chunkRadius = (tile) => CHUNK_TILES * 0.5 * tile * Math.SQRT2 + 60;

const TRUNK = [0.26, 0.19, 0.13];
// house walls: concrete, sand, brick, slate
const HOUSES = [
  [0.62, 0.60, 0.55], [0.72, 0.63, 0.48], [0.58, 0.40, 0.34], [0.44, 0.46, 0.48],
];

// What one tile grows, as calls to `emit(prim, x, y, z, yaw, pitch, sx, sy, sz, colour)`.
// Both passes run this: the first counts, the second fills.
function plant(map, i, j, t, emit) {
  const tile = map.tile;
  const x = (i + 0.5) * tile;
  const z = (j + 0.5) * tile;
  const y = map.heightAt(x, z);
  const r1 = treeHash(i, j), r2 = treeHash(j * 1.7, i * 2.3);
  if (t === TILE_TREE || t === TILE_FOREST) {
    // the trunk spots come from `map/tiles.js`, which is also what the server
    // collides against: a round bursts on the trunk you can see
    for (const tr of treeTrunks(i, j, t, tile)) {
      const { q1, q2, q3 } = tr;
      const px = tr.x, pz = tr.z;
      const py = map.heightAt(px, pz);
      const broad = q3 > 0.45;
      const tall = 10.0 + q3 * 8.0;                     // 10..18 m
      const rad = tall * (broad ? 0.40 : 0.26) * (0.85 + q1 * 0.3);
      const lean = (q2 - 0.5) * 0.10;
      emit(PRIM.CYL, px, py + tall * 0.20, pz, q1 * 6.283, lean,
        0.7 + q3 * 0.5, tall * 0.40, 0.7 + q3 * 0.5, TRUNK);
      if (broad) {
        // broadleaf: a fat canopy ball sitting on the trunk
        emit(PRIM.SPHERE, px, py + tall * 0.60, pz, q1 * 6.283, lean,
          rad, tall * 0.46, rad,
          [0.12 + q2 * 0.08, 0.26 + q1 * 0.13, 0.10 + q3 * 0.06]);
      } else {
        // conifer: one narrow cone, skirt down to the trunk
        emit(PRIM.CONE, px, py + tall * 0.55, pz, q1 * 6.283, lean,
          rad, tall * 0.80, rad,
          [0.08 + q1 * 0.05, 0.19 + q2 * 0.14, 0.09 + q3 * 0.05]);
      }
    }
  } else if (t === TILE_BUILDING) {
    // A house: as tall as the wall column the server stored for this tile, so
    // the block you see is exactly the block a round stops on. The facade mesh
    // paints the storeys and windows, the slab on top is the roof. Every tile of
    // a block shares one column height, so a row of them reads as one building
    // with a level roof.
    const sy = map.walls[j * map.w + i] || 8.0;
    const sx = tile * (0.88 + r1 * 0.10);
    const sz = tile * (0.88 + r2 * 0.10);
    const wall = HOUSES[Math.floor(treeHash(i * 0.7, j * 1.3) * HOUSES.length) % HOUSES.length];
    // A town stands on its hill, so the facet under a block is a slope: the box
    // is drawn from the *lowest* corner of the tile up to the roofline, so it is
    // buried on the uphill side instead of hovering over the downhill one. The
    // roof stays where the wall column says it is.
    const hh = map.heights;
    const hlo = Math.min(hh[j * map.w + i], hh[j * map.w + i + 1],
      hh[(j + 1) * map.w + i], hh[(j + 1) * map.w + i + 1]);
    const foot = Math.min(hlo, y) - 0.4;
    const tall = y + sy - foot;
    emit(PRIM.FACADE, x, foot + tall * 0.5, z, 0, 0, sx, tall, sz, wall);
    // roof: a flat slab with a parapet, plus the odd plant box on top
    const dark = r1 > 0.5 ? 0.46 : 0.52;
    emit(PRIM.BOX, x, y + sy + 0.22, z, 0, 0, sx * 1.03, 0.45, sz * 1.03,
      [wall[0] * dark, wall[1] * dark, wall[2] * dark]);
    if (r2 > 0.72) {
      emit(PRIM.BOX, x + (r1 - 0.5) * sx * 0.5, y + sy + 0.6 + 1.1,
        z + (r2 - 0.5) * sz * 0.5, 0, 0, sx * 0.3, 2.2, sz * 0.3,
        [0.34, 0.35, 0.36]);
    }
    if (r2 < 0.22) {
      // a low annex against one side, well under the wall column, on the ground
      // of its own tile as well
      const ax = x + (r1 > 0.5 ? 1 : -1) * sx * 0.44;
      const az = z + (r2 - 0.5) * sz * 0.4;
      const afoot = Math.min(map.heightAt(ax, az), foot) - 0.3;
      const atall = y + sy * 0.72 - afoot;
      emit(PRIM.FACADE, ax, afoot + atall * 0.5, az, 0, 0,
        sx * 0.42, atall, sz * 0.42,
        [wall[0] * 0.94, wall[1] * 0.90, wall[2] * 0.86]);
    }
  } else if (t === TILE_ROCK) {
    // two or three angular chunks per tile, tipped over at random angles, half
    // sunk into the ground: low poly boulders
    const sz0 = 4.6 + r1 * 5.0;
    const rock = [0.30 + r1 * 0.08, 0.285 + r1 * 0.07, 0.265 + r2 * 0.06];
    for (let q = 0; q < 3; q++) {
      const h1 = treeHash(i + q * 3.1, j + q * 7.7);
      const h2 = treeHash(j * 1.3 + q, i * 2.9 - q);
      const a = r2 * 6.283 + q * 2.1;
      const off = sz0 * 0.32 * q;
      const bw = sz0 * (0.7 - q * 0.16) * (0.85 + h1 * 0.4);
      const g = 0.88 + h2 * 0.24;
      emit(PRIM.ROCK,
        x + Math.cos(a) * off, y + bw * 0.26, z + Math.sin(a) * off,
        a, (h1 - 0.5) * 0.5, bw, bw * (0.62 + h2 * 0.5), bw,
        [rock[0] * g, rock[1] * g, rock[2] * g]);
    }
  }
}

const wants = (t) => t === TILE_BUILDING || t === TILE_TREE || t === TILE_ROCK || t === TILE_FOREST;

export class Props {
  constructor(materialFor, order) {
    this.materialFor = materialFor;
    this.order = order;
    this.chunks = [];
    this._shown = 0;
    this.stats = { ms: 0, instances: 0, chunks: 0 };
  }

  // `repaintTiles` calls this again: trunks and canopies are drawn from
  // `map.tiles`, so the whole field is rebuilt from the new bitmap and the
  // previous layers are released first.
  build(map) {
    const t0 = performance.now();
    const w = map.w, h = map.h, tile = map.tile;
    const nCx = Math.ceil(w / CHUNK_TILES), nCz = Math.ceil(h / CHUNK_TILES);

    // pass one: how many instances of each primitive each chunk carries
    const counts = new Array(nCx * nCz);
    for (let ci = 0; ci < counts.length; ci++) counts[ci] = new Array(PRIM_COUNT).fill(0);
    for (let cz = 0; cz < nCz; cz++) {
      for (let cx = 0; cx < nCx; cx++) {
        const ci = cz * nCx + cx;
        const i0 = cx * CHUNK_TILES, j0 = cz * CHUNK_TILES;
        const i1 = Math.min(w, i0 + CHUNK_TILES), j1 = Math.min(h, j0 + CHUNK_TILES);
        const tally = counts[ci];
        const count = (prim) => { tally[prim]++; };
        for (let j = j0; j < j1; j++) {
          for (let i = i0; i < i1; i++) {
            const t = map.tiles[j * w + i];
            if (wants(t)) plant(map, i, j, t, count);
          }
        }
      }
    }

    // pass two: allocate each chunk's layers at their exact size and fill them
    const layers = [];
    for (let cz = 0; cz < nCz; cz++) {
      for (let cx = 0; cx < nCx; cx++) {
        const ci = cz * nCx + cx;
        const tally = counts[ci];
        const made = [];
        for (let prim = 0; prim < PRIM_COUNT; prim++) {
          if (!tally[prim]) continue;
          const layer = new StaticLayer(prim, tally[prim], this.materialFor(prim), this.order);
          // the chunk's own bounds, in world metres: the mesh sits at the
          // origin and its instances are already placed, so the sphere is the
          // geometry's bound and three's frustum test is exact
          layer.geometry.boundingSphere = new Sphere(
            new Vector3((cx + 0.5) * CHUNK_TILES * tile, 0, (cz + 0.5) * CHUNK_TILES * tile),
            chunkRadius(tile));
          made[prim] = layer;
        }
        if (!made.length) continue;
        const fill = new Array(PRIM_COUNT).fill(0);
        const add = (prim, x, y, z, yaw, pitch, sx, sy, sz, col) => {
          made[prim].write(fill[prim]++, x, y, z, yaw, pitch, sx, sy, sz, col);
        };
        const i0 = cx * CHUNK_TILES, j0 = cz * CHUNK_TILES;
        const i1 = Math.min(w, i0 + CHUNK_TILES), j1 = Math.min(h, j0 + CHUNK_TILES);
        for (let j = j0; j < j1; j++) {
          for (let i = i0; i < i1; i++) {
            const t = map.tiles[j * w + i];
            if (wants(t)) plant(map, i, j, t, add);
          }
        }
        const centre = {
          x: (cx + 0.5) * CHUNK_TILES * tile,
          z: (cz + 0.5) * CHUNK_TILES * tile,
        };
        for (const layer of made) {
          if (!layer) continue;
          layer.flush();
          layers.push({ layer, centre });
        }
      }
    }

    this.dispose();
    this.chunks = layers;
    this.stats = {
      ms: Math.round(performance.now() - t0),
      instances: layers.reduce((n, c) => n + c.layer.filled, 0),
      chunks: layers.length,
    };
  }

  // Everything here is static: the only per-frame work is the distance test
  // against the eye, which is what keeps a town out of the frame until its
  // chunk is near enough to be a town.
  update(cam) {
    const R2 = PROP_RANGE * PROP_RANGE;
    const ex = cam.eye[0], ez = cam.eye[2];
    let shown = 0;
    for (const c of this.chunks) {
      const dx = c.centre.x - ex;
      const dz = c.centre.z - ez;
      const visible = dx * dx + dz * dz < R2;
      c.layer.mesh.visible = visible;
      if (visible) shown += c.layer.filled;
    }
    this._shown = shown;
  }

  // how many prop instances this frame's cull let through, for the readout
  visibleInstances() {
    return this._shown || 0;
  }

  meshes() {
    return this.chunks.map((c) => c.layer.mesh);
  }

  dispose() {
    for (const c of this.chunks) c.layer.dispose();
    this.chunks = [];
  }
}
