// Placing a piece: the two ways a model turns an offset in its own frame into a
// world point, and the two numbers the sight is mounted on.
//
// Local frame: +X forward, +Y up, +Z right (`core/math.js` `compose`).  `at`
// turns the offset with the same yaw it turns the mesh with, which is what
// everything bolted to a hull wants; `headAt` is the sight head's own case and
// the one place where the pivot and the piece turn on different angles.

// The commander's panoramic sight stands on the roof with its optical axis on
// the machine's eye point (`defs.MECH_EYE`, mirrored to the client as
// `defs.mech.eye`): the cockpit camera sits exactly there, at the sight window,
// so the pilot's own head is left out of the draw list - it would be drawn
// round the camera - while every other machine keeps it.  This number and
// MECH_EYE move together.
export const SIGHT_Y = 5.05;
// The pillar the head turns on, in the hull's own frame: `defs.MECH_SIGHT_Z`,
// the same pivot the server puts the pilot's eye on.  The two numbers move
// together, and the geometry only reads right if they do.
export const SIGHT_Z = -0.62;

export const FORWARD = -Math.PI / 2;   // rotates a Y-axis primitive onto +X

export function at(b, mesh, e, yaw, pitch, ox, oy, oz, sx, sy, sz, col, alpha) {
  const c = Math.cos(yaw), s = Math.sin(yaw);
  const x = e.x + (-s) * ox + c * oz;
  const z = e.z + c * ox + s * oz;
  b.put(mesh, x, e.y + oy, z, yaw, pitch, sx, sy, sz,
    col[0], col[1], col[2], alpha === undefined ? 1 : alpha);
}

// A piece of the sight head.  `at` turns the *offset* with the same yaw it
// turns the mesh with, which is right for anything bolted to a hull - and
// wrong for the head, whose pivot is fixed to the hull while the head turns on
// it: the head must rotate on the pillar, not orbit the machine.  So the
// pivot's place comes from the hull's yaw and only the offset from the
// pivot - `right` / `up` / `fwd` in the head's own frame, pitch and all -
// follows the sight's.
export function headAt(b, mesh, e, hullYaw, syaw, spitch,
                      right, up, fwd, sx, sy, sz, col, meshPitch) {
  const ch = Math.cos(hullYaw), sh = Math.sin(hullYaw);
  const px = e.x + ch * SIGHT_Z, pz = e.z + sh * SIGHT_Z;   // the pillar
  const cp = Math.cos(spitch), sp = Math.sin(spitch);
  const out = fwd * cp - up * sp;         // out along the head's ground track
  const high = fwd * sp + up * cp;        // up the pitched head axis
  const cy = Math.cos(syaw), sn = Math.sin(syaw);
  const x = px + (-sn) * right + cy * out;
  const z = pz + cy * right + sn * out;
  b.put(mesh, x, e.y + SIGHT_Y + high, z, syaw,
    meshPitch === undefined ? spitch : meshPitch, sx, sy, sz, col[0], col[1], col[2], 1);
}
