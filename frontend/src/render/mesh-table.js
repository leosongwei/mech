// The mesh table: what each primitive is, and what every pass has to know about
// it before it can be drawn.
//
// The index order is the order `meshes.js` builds its arrays in and the order the
// renderer's instance buffers are laid out in, so it is load bearing - but it is
// named here once and read everywhere.  The per-mesh *behaviour* lives here too:
// which primitive paints window rows, which one is unlit, which ones the
// screen-space overlay may use.  It used to be three `if`s inside the renderer
// (`uFacade = meshIndex === MESH_FACADE`, `unlit = m === MESH_PYRAMID`,
// `uOutline = m === MESH_CONE ? 2 : 1`) and a second, parallel `DYN_CAP` array -
// so adding a mesh meant remembering four places and failing silently if you did
// not.

export const MESH = {
  BOX: 0,
  SPHERE: 1,
  CONE: 2,
  CYL: 3,
  ROCK: 4,
  PYRAMID: 5,
  SMOKE: 6,
  FACADE: 7,
  WEDGE: 8,
  NOSE: 9,
  HEX: 10,
};

export const MESH_COUNT = 11;

// mat4 (16) + vec4 colour (4)
export const STRIDE_FLOATS = 20;

// How many instances of each primitive one frame may carry.  A cap is a guard
// against a runaway emitter, not a budget: the biggest counts are the ones a
// battle actually reaches (boxes are hulls, parts and tracers' debris; the smoke
// sphere is drawn rarely but is the close-up puff).
const BLENDED_CAP = 12000;   // the impact/particle pass, same for every mesh

export const MESH_INFO = [
  { name: 'box', cap: 40000 },                          // hulls, parts, rubble
  { name: 'sphere', cap: 24000 },                       // wheels, joints, dots
  { name: 'cone', cap: 24000, overlay: 2 },             // muzzles, canopies, an arrow's head
  { name: 'cyl', cap: 24000, overlay: 1 },              // barrels, tubes, an arrow's shaft
  { name: 'rock', cap: 8000 },                          // boulders
  { name: 'pyramid', cap: 32000, unlit: true },         // tracers: an unlit box edge, no shading
  { name: 'smoke', cap: 64 },                           // the round sphere the close puffs use
  { name: 'facade', cap: 64, facade: true },            // a house wall, painted with storeys
  { name: 'wedge', cap: 2000 },                         // a mech's leg pedestal
  { name: 'nose', cap: 600 },                           // a cockpit
  { name: 'hex', cap: 6000 },                           // one tube of a launcher's rack
];
for (const info of MESH_INFO) info.blendedCap = BLENDED_CAP;

// The screen-space overlay (the threat arrows) draws out of its own two buffers,
// so its pieces are the two primitives `overlay` names, in piece order.
export const OVERLAY_CAP = 16;
