// The tile vocabulary: what a tile of the heightfield is, which halves of it
// carry road, and where the trees standing on it are.  The renderer plants its
// props from the same numbers, so the trunk the eye collides against is the
// trunk the screen draws, and the canopy that hides a hull is the mesh hung
// over it.

export const TILE_GRASS = 0, TILE_ROAD = 1, TILE_BUILDING = 2, TILE_TREE = 3, TILE_ROCK = 4,
  TILE_BASE = 5, TILE_SOLID = 6, TILE_FOREST = 7;

export const TILES = {
  TILE_GRASS, TILE_ROAD, TILE_BUILDING, TILE_TREE, TILE_ROCK, TILE_BASE, TILE_SOLID, TILE_FOREST,
};

// road pieces, mirrors terrain.py (an OpenTTD road tile is a set of halves;
// the two diagonal bits are the corner-to-corner piece we added on top)
export const ROAD_N = 1;
export const ROAD_E = 2;
export const ROAD_S = 4;
export const ROAD_W = 8;
export const ROAD_D_WS = 16;    // west edge middle -> south edge middle
export const ROAD_D_NE = 32;    // north edge middle -> east edge middle
export const ROAD_STRAIGHT = ROAD_N | ROAD_E | ROAD_S | ROAD_W;
export const ROAD_DIAG = ROAD_D_WS | ROAD_D_NE;

const TRUNK_R = 0.5;            // trunk radius, must match terrain.py

// The hash the renderer plants its trees with - the server collides against
// exactly the trunks it produces (`terrain.py`, `_tree_hash`).
export function treeHash(a, b) {
  const s = Math.sin(a * 12.9898 + b * 78.233) * 43758.5453;
  return s - Math.floor(s);
}

// Where a tree tile's trunks stand: `{x, z, r, h}` each, 1-2 trees on a tree
// tile and 3-5 on a forest tile, plus the three hash draws (`q1`-`q3`) the
// renderer gives each tree its height, canopy and colour from.  The canopy
// above the trunks is not a trunk: `canopyOf` builds it, and it hides what is
// behind it without stopping anything that flies.
export function treeTrunks(i, j, t, tile) {
  const r1 = treeHash(i, j);
  const n = t === 7 ? 3 + Math.floor(r1 * 3) : 1 + Math.floor(r1 * 2);
  const cx = (i + 0.5) * tile, cz = (j + 0.5) * tile;
  const out = [];
  for (let q = 0; q < n; q++) {
    const q1 = treeHash(i + q * 5.3, j + q * 1.9);
    const q2 = treeHash(j + q * 3.7, i + q * 7.1);
    const q3 = treeHash(i * 1.3 + q, j * 2.7 - q);
    out.push({
      x: cx + (q1 - 0.5) * tile * 0.66,      // spread over the tile, never
      z: cz + (q2 - 0.5) * tile * 0.66,      // touching its border
      r: TRUNK_R * (0.7 + q3 * 0.5),
      h: (10.0 + q3 * 8.0) * 0.40,           // the trunk column, canopy excluded
      q1, q2, q3,
    });
  }
  return out;
}

// The volume one tree hides behind, from the same draws the renderer builds
// the mesh from: a broadleaf (q3 > 0.45) is a ball on the trunk, a conifer a
// cone standing on it.  Mirrors terrain._canopy - what hides a hull is what
// the eye can see, not a slab over the whole tile.
export function canopyOf(map, tr) {
  const ground = map.heightAt(tr.x, tr.z);
  const tall = 10.0 + tr.q3 * 8.0;
  if (tr.q3 > 0.45) {
    const rad = tall * 0.40 * (0.85 + tr.q1 * 0.3);
    return { ball: true, x: tr.x, z: tr.z, y: ground + tall * 0.60,
      rx: rad, ry: tall * 0.46, rz: rad, rad };
  }
  const rad = tall * 0.26 * (0.85 + tr.q1 * 0.3);
  return { ball: false, x: tr.x, z: tr.z, y0: ground + tall * 0.15,
    y1: ground + tall * 0.95, rad };
}

// the interval [lo, hi] where a*t^2 + b*t + c <= 0, or null
export function inRoots(a, b, c) {
  if (Math.abs(a) < 1e-12) {
    if (Math.abs(b) < 1e-12) return c <= 0 ? [0, 1] : null;
    const t = -c / b;
    return b > 0 ? [-1e9, t] : [t, 1e9];
  }
  const disc = b * b - 4 * a * c;
  if (disc < 0) return null;
  const sq = Math.sqrt(disc);
  const r0 = (-b - sq) / (2 * a), r1 = (-b + sq) / (2 * a);
  return r0 <= r1 ? [r0, r1] : [r1, r0];
}
