// The camera, and the one world->screen projection in the client.
//
// `makeCamera` is the frame's view-projection; `project` is what the HUD, the
// pre-aim rings and the threat arrows ask to turn a world point into CSS pixels;
// `syncCamera` hands the same matrices to the scene graph, so three's own
// frustum tests are made against the picture that is actually drawn.
//
// The game's camera is authored here, not by three: `cam` is a plain object the
// frame builds (`eye`, `view`, `proj`, `viewProj`, `invViewProj`), and the HUD
// reads the same numbers the world does. Three's camera is the consumer of the
// last of them.

import { mat4, multiply, perspective, lookAt } from '../core/math.js';

// `cam` is `{ eye, view, proj, viewProj, fov, aspect }`, and `g.__cam` in the
// console replaces the frame's camera by calling this with a fake eye.
export function makeCamera(cam, eye, target, fov, aspect, near, far) {
  const proj = perspective(cam.proj || mat4(), fov, aspect, near, far);
  const view = lookAt(cam.view || mat4(), eye, target, [0, 1, 0]);
  cam.proj = proj;
  cam.view = view;
  cam.viewProj = multiply(cam.viewProj || mat4(), proj, view);
  cam.eye = eye;
  return cam;
}

// A world point through a view-projection into CSS pixels: `out.x` / `out.y` are
// where it lands on a `w` x `h` canvas, `out.z` is the clip-space w (metres in
// front of the eye, which is what a caller tests to decide "behind the camera").
// Returns false, writing nothing useful, for a point at or behind the eye.
export function project(out, m, w, h, x, y, z) {
  const cx = m[0] * x + m[4] * y + m[8] * z + m[12];
  const cy = m[1] * x + m[5] * y + m[9] * z + m[13];
  const cw = m[3] * x + m[7] * y + m[11] * z + m[15];
  if (!(cw > 0.01)) return false;
  const iw = 1 / cw;
  out.x = (cx * iw * 0.5 + 0.5) * w;
  out.y = (1 - (cy * iw * 0.5 + 0.5)) * h;
  out.z = cw;
  return true;
}

// The frame's camera on the scene graph: the projection and the inverse view
// matrix, which is all three reads it for (frustum culling, and the built-in
// uniforms nothing here uses).
export function syncCamera(camera, cam) {
  camera.matrixWorldInverse.fromArray(cam.view);
  camera.matrixWorld.copy(camera.matrixWorldInverse).invert();
  camera.projectionMatrix.fromArray(cam.proj);
  camera.projectionMatrixInverse.copy(camera.projectionMatrix).invert();
}
