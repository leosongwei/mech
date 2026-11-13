// The camera, and the one world->screen projection in the client.
//
// `makeCamera` is the frame's view-projection; `project` is what the HUD, the
// pre-aim rings and the threat arrows ask to turn a world point into CSS pixels.
// It used to be three implementations - the HUD's private `_project`, a
// hand-written 4x4 multiply in `game._ringScreen`, and nothing at all in the
// passes - which is why a mark could land a pixel or two off the cross it was
// solved against.

import { mat4, multiply, perspective, lookAt } from '../core/math.js';

// `cam` is `{ eye, viewProj, fov, aspect }`, and `g.__cam` in the console
// replaces the frame's camera by calling this with a fake eye.
export function makeCamera(cam, eye, target, fov, aspect, near, far) {
  const proj = perspective(mat4(), fov, aspect, near, far);
  const view = lookAt(mat4(), eye, target, [0, 1, 0]);
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
