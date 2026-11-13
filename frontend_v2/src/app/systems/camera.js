// The frame's camera: the cockpit, and the orbit a dead pilot gets instead.
//
// `camera(app, own, dt)` answers the `cam` every pass draws through - `eye`,
// `viewProj`, `invViewProj` and the `far` / `fov` / `aspect` it was built with -
// and it is the only place the view is turned: `input.viewYaw` is the sight's
// heading and `pitch + freePitch * 0.8` its elevation, so the reticle is the
// centre of the screen at every zoom.

import { BASE_FOV } from '../../core/config.js';
import { clamp, dirFrom, invert4, mat4 } from '../../core/math.js';
import { makeCamera } from '../../render/camera.js';

export function camera(app, own, dt) {
  const cam = { viewProj: mat4(), invViewProj: mat4() };
  // the canvas the passes draw into, in CSS pixels: the projection's aspect is
  // the shape the picture is squeezed into, whatever the device ratio draws it at
  const cw = app.renderer.canvas.clientWidth;
  const ch = app.renderer.canvas.clientHeight;
  if (!own) {
    // death cam: a slow orbit round the place the hull fell.  `deathSpot`
    // starts on this side's base and the first live frame takes it over
    const at = app.deathSpot;
    const bx = at.x;
    const bz = at.z;
    const t = app.net.meta ? app.net.meta.time || 0 : 0;
    const ang = t * 0.15;
    const eye = [bx + Math.cos(ang) * 160, app.map.heightAt(bx, bz) + 90,
                 bz + Math.sin(ang) * 160];
    const fov = 1.0;
    const aspect = cw / ch;
    makeCamera(cam, eye, [bx, app.map.heightAt(bx, bz) + 10, bz], fov, aspect, 1,
      app.renderer.far);
    cam.fov = fov;
    cam.aspect = aspect;
    cam.far = app.renderer.far;
    invert4(cam.invViewProj, cam.viewProj);
    return cam;
  }
  // cockpit camera: the eye is the commander's sight head, on its optical
  // axis (`defs.mech.eye`), and it looks exactly where the arms point, so
  // the reticle is the centre of the screen at every zoom.  The machine
  // itself is drawn in full (no separate view model), which is what makes
  // the barrels, the hull and the muzzle flashes all sit where the rounds
  // really come out - the only piece left out of the pilot's own machine is
  // the sight head the eye sits in front of (scene.mech).
  let yaw = app.input.viewYaw;
  let pitch = clamp(app.input.pitch + app.input.freePitch * 0.8, -1.35, 1.35);
  if (app.shake > 0.01) {
    // angular kick, scaled by the magnification so recoil does not blind
    // the pilot through a 20x sight
    const s = app.shake * 0.02 / app.zoom;
    yaw += (Math.random() - 0.5) * s;
    pitch += (Math.random() - 0.5) * s;
  }
  const dir = dirFrom(yaw, pitch);
  const eye = app.sightEye(own);
  const aspect = cw / Math.max(1, ch);
  const fov = clamp(BASE_FOV / app.zoom, 0.02, BASE_FOV);
  makeCamera(cam, eye, [eye[0] + dir[0], eye[1] + dir[1], eye[2] + dir[2]], fov,
    aspect, 0.4, app.renderer.far);
  cam.fov = fov;
  cam.aspect = aspect;
  cam.far = app.renderer.far;
  invert4(cam.invViewProj, cam.viewProj);
  return cam;
}
