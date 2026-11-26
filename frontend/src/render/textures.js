// The two textures the ground is drawn from: the heightfield (R32F, metres) and
// the tile map (R8UI, `TILE_*`). Both are read by integer texel - the vertex
// shader fetches heights, the fragment shader fetches one tile id - so they are
// nearest-filtered, clamped to the edge, and never flipped.
//
// They are rebuilt from the bitmap rather than painted, which is why `setMap()`
// called a second time has to let go of the previous pair.

import {
  DataTexture, RedFormat, RedIntegerFormat, FloatType, UnsignedByteType,
  NearestFilter, ClampToEdgeWrapping,
} from 'three';

function dataTexture(data, map, format, type) {
  const t = new DataTexture(data, map.w, map.h, format, type);
  t.magFilter = NearestFilter;
  t.minFilter = NearestFilter;
  t.wrapS = ClampToEdgeWrapping;
  t.wrapT = ClampToEdgeWrapping;
  t.generateMipmaps = false;
  // texel (i, j) is `data[j * w + i]`, the same indexing the server uses
  t.flipY = false;
  t.unpackAlignment = 1;
  t.needsUpdate = true;
  return t;
}

export class GroundTextures {
  constructor() {
    this.height = null;
    this.tiles = null;
  }

  // a fresh map: drop what we had and upload the new pair
  build(map) {
    this.dispose();
    this.height = dataTexture(map.heights, map, RedFormat, FloatType);
    this.tiles = dataTexture(map.tiles, map, RedIntegerFormat, UnsignedByteType);
  }

  // `repaintTiles`: the debug tree planter rewrote `map.tiles` in place, so the
  // tile bitmap is re-uploaded and the heights are untouched.
  uploadTiles(map) {
    if (!this.tiles) return;
    this.tiles.image = { data: map.tiles, width: map.w, height: map.h };
    this.tiles.needsUpdate = true;
  }

  dispose() {
    if (this.height) this.height.dispose();
    if (this.tiles) this.tiles.dispose();
    this.height = null;
    this.tiles = null;
  }
}
