// The net module's facade: what the app imports.
//
// `Net` is the socket (a `Client`), `View` the picture it fills, and the two fx
// doors are re-exported for a caller that already holds a frame.

export { Client as Net } from './client.js';
export { View } from './view.js';
export { decodeFx, isFxFrame } from './wire.js';
