// The server painted terrain.  `debug_trees` grows a stand of trees on the
// server's map (`Terrain.plant`) and hands the same rectangle to every client,
// so the stand that hides the target is a stand the pilot can see.

// Copy the rectangle into our copy of the map and drop what was derived from
// the old bitmap.  Returns true when the renderer should be asked to repaint -
// the props and the tile texture both come from `map.tiles`.
export function applyPaint(map, msg) {
  if (!map || !msg.rows || !msg.cols) return false;
  let k = 0;
  for (let r = 0; r < msg.rows; r++) {
    const row = (msg.j0 + r) * map.w + msg.i0;
    for (let c = 0; c < msg.cols; c++, k++) map.tiles[row + c] = msg.data[k];
  }
  // the trunk and canopy caches are keyed by tile and hold what the old bitmap
  // said: a stand planted on ground that was already asked about would never be
  // found in them
  map.trunkCache.clear();
  map.canopyCache.clear();
  return true;
}
