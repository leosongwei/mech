// Everything that walks: a rifleman or an anti-tank man (the same body carrying
// a different weapon), a civilian, the corpse one of them leaves, and the
// blockhouse on a ridge.
//
// sphere head, cone torso, cylinder weapon

import { MESH } from '../../render/mesh-table.js';
import { FORWARD, at } from '../helpers.js';
import { CONCRETE, CONCRETE_D, SOOT, facColor, dim } from '../palette.js';

export function infantry(b, e, rifle) {
  const fac = facColor(e);
  const yaw = e.yaw;
  at(b, MESH.CONE, e, yaw, 0, 0, 0.55, 0, 0.75, 1.15, 0.9, fac);
  at(b, MESH.SPHERE, e, yaw, 0, 0, 1.38, 0, 0.52, 0.52, 0.52, [0.45, 0.38, 0.30]);
  at(b, MESH.SPHERE, e, yaw, 0, 0, 1.62, 0, 0.56, 0.24, 0.56, [0.28, 0.30, 0.26]);
  if (rifle) {
    at(b, MESH.CYL, e, yaw, FORWARD, 0.22, 1.15, 0.55, 0.34, 2.1, 0.34, [0.26, 0.29, 0.20]);
    at(b, MESH.CONE, e, yaw, FORWARD, 0.22, 1.15, 1.7, 0.30, 0.55, 0.30, [0.7, 0.25, 0.15]);
  } else {
    at(b, MESH.CYL, e, yaw, FORWARD, 0.24, 1.05, 0.5, 0.11, 1.15, 0.11, [0.10, 0.10, 0.10]);
  }
}

export function civilian(b, e) {
  const fac = facColor(e);
  at(b, MESH.CONE, e, e.yaw, 0, 0, 0.52, 0, 0.7, 1.05, 0.8, fac);
  at(b, MESH.SPHERE, e, e.yaw, 0, 0, 1.28, 0, 0.46, 0.5, 0.46, [0.58, 0.44, 0.36]);
}

// Infantry toppled over: the body lies along its last facing.
export function corpse(b, e) {
  const fac = dim(facColor(e), 0.45);
  const yaw = e.yaw;
  at(b, MESH.CONE, e, yaw, FORWARD, 0, 0.24, 0, 0.6, 1.55, 0.6, fac);
  at(b, MESH.SPHERE, e, yaw, 0, 0.95, 0.26, 0, 0.46, 0.4, 0.46, [0.32, 0.27, 0.22]);
  at(b, MESH.CYL, e, yaw, FORWARD, -0.4, 0.14, 0.1, 0.1, 1.1, 0.1, SOOT);
}

// Indestructible blockhouse on a ridge: a squat concrete drum with an
// observation deck and a red cap, so the zeroing reference reads at 2 km
export function bunker(b, e) {
  const yaw = e.yaw;
  at(b, MESH.CYL, e, yaw, 0, 0, 4.0, 0, 11, 8, 11, CONCRETE);
  at(b, MESH.CYL, e, yaw, 0, 0, 9.3, 0, 8, 2.6, 8, CONCRETE_D);
  at(b, MESH.BOX, e, yaw, 0, 0, 10.9, 0, 3.4, 0.6, 3.4, [0.78, 0.18, 0.12]);
  for (let i = 0; i < 4; i++) {                 // firing slit on each face
    at(b, MESH.BOX, e, yaw + i * Math.PI / 2, 0, 0, 5.0, 5.5, 3.6, 1.0, 0.5, SOOT);
  }
}
