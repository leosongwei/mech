// The two textures the ground is drawn from: the heightfield (R32F, metres) and
// the tile map (R8UI, `TILE_*`).  Both are looked up by integer texel - the
// vertex shader fetches heights, the fragment shader fetches one tile id - so
// they are nearest-filtered and clamped to the edge.
//
// They are rebuilt from the bitmap rather than painted, which is why `setMap()`
// called a second time has to let go of the previous pair: the old renderer
// created a new texture every time and left the old one to the driver.

export class Textures {
  constructor(gpu) {
    this.gpu = gpu;
    this.height = null;
    this.tiles = null;
  }

  // a fresh map (or the same map loaded again): drop what we had and upload
  build(map) {
    const gl = this.gpu.gl;
    this.dispose();
    this.height = this.gpu.texture({
      w: map.w, h: map.h, internal: gl.R32F, format: gl.RED, type: gl.FLOAT, data: map.heights,
    });
    this.tiles = this.gpu.texture({
      w: map.w, h: map.h, internal: gl.R8UI, format: gl.RED_INTEGER, type: gl.UNSIGNED_BYTE,
      data: map.tiles,
    });
  }

  // `repaintTiles`: the debug tree planter rewrote `map.tiles`, so the tile
  // bitmap is re-uploaded in place.  Heights, roads and walls are untouched.
  uploadTiles(map) {
    const gl = this.gpu.gl;
    this.gpu.texImage(this.tiles, {
      w: map.w, h: map.h, internal: gl.R8UI, format: gl.RED_INTEGER, type: gl.UNSIGNED_BYTE,
      data: map.tiles,
    });
  }

  dispose() {
    this.gpu.release(this.height);
    this.gpu.release(this.tiles);
    this.height = null;
    this.tiles = null;
  }
}
