// The barrel the app imports: the map's data (`field.js`), its two collision
// rules (`solid.js` for a round, `sight.js` for an eye), the client's ballistic
// prediction, the tile vocabulary and the terrain paint.  The fire control is
// not here - it is `core/fcs.js`, and the app asks it directly.

export { MapData } from './field.js';
export * from './tiles.js';
export * from './predict.js';
export * from './solid.js';
export * from './sight.js';
export { applyPaint } from './paint.js';
