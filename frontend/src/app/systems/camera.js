// The frame's camera: the cockpit, the orbit a dead pilot gets instead, and the
// console's free camera when one is set (`freeCamera` below).
//
// `camera(app, own, dt)` answers the `cam` every pass draws through - `eye`,
// `viewProj`, `invViewProj` and the `far` / `fov` / `aspect` it was built with -
// and it is the only place the view is turned: `input.viewYaw` is the sight's
// heading and `pitch + freePitch * 0.8` its elevation, so the reticle is the
// centre of the screen at every zoom.

import { BASE_FOV } from '../../core/config.js';
import { clamp, dirFrom, invert4, mat4 } from '../../core/math.js';
import { makeCamera } from '../../render/camera.js';

// The console's free camera (`g.freeCam`, the spec `app.freecam` holds): a fixed
// eye anywhere in the world, or one parked on a hull that tracks it as it
// drives - the way to look at a machine instead of out of one.  A parked eye is
// solved off the entity's drawn pose every frame, so a walking mech's arms are
// followed rather than left behind, and the eye looks at the hull's centre (its
// own `height` off the unit table) whichever way it turns.
//
// `yaw` on a parked eye is the direction *from the hull to the eye*, so the
// eye stays on that side of the machine as it drives; left out it is the hull's
// own yaw + 0.9 rad, a three-quarter view from the front left.  `zoom` is the
// optics step the eye is drawn with (1 = the plain 1x field of view), which is
// what makes a barrel or a rack readable from twelve metres.
export function freeCamera(app, cam, cw, ch, spec) {
  let eye = spec.eye || [spec.x || 0, spec.y || 0, spec.z || 0];
  let yaw = spec.yaw === undefined ? 0 : spec.yaw;
  let pitch = spec.pitch === undefined ? -0.1 : spec.pitch;
  if (spec.id !== undefined) {
    const t = app.net.entities.get(spec.id);
    if (t) {
      const bearing = spec.yaw === undefined ? (t.yaw || 0) + 0.9 : spec.yaw;
      const dist = spec.dist === undefined ? 12 : spec.dist;
      const height = spec.height === undefined ? 2.6 : spec.height;
      eye = [t.x + Math.cos(bearing) * dist, (t.y || 0) + height,
             t.z + Math.sin(bearing) * dist];
      const aimY = (t.y || 0) + ((app.defs.units[t.type] || {}).height || 3) * 0.5;
      const dx = t.x - eye[0], dy = aimY - eye[1], dz = t.z - eye[2];
      yaw = Math.atan2(dz, dx);
      pitch = Math.atan2(dy, Math.hypot(dx, dz));
    }
  }
  // what was actually used, so `g.freeCam()` answers where the eye really is
  spec.eye = eye;
  spec.look = [yaw, pitch];
  const dir = dirFrom(yaw, pitch);
  const aspect = cw / Math.max(1, ch);
  const fov = clamp(BASE_FOV / (spec.zoom || 1), 0.02, BASE_FOV);
  makeCamera(cam, eye, [eye[0] + dir[0], eye[1] + dir[1], eye[2] + dir[2]], fov,
    aspect, 0.4, app.renderer.far);
  cam.fov = fov;
  cam.aspect = aspect;
  cam.far = app.renderer.far;
  invert4(cam.invViewProj, cam.viewProj);
  return cam;
}

export function camera(app, own, dt) {
  const cam = { viewProj: mat4(), invViewProj: mat4() };
  // the canvas the passes draw into, in CSS pixels: the projection's aspect is
  // the shape the picture is squeezed into, whatever the device ratio draws it at
  const cw = app.renderer.canvas.clientWidth;
  const ch = app.renderer.canvas.clientHeight;
  // the console's own eye answers first: it stands where it was put whatever
  // the pilot is doing - dead, respawning, or looking through a 20x sight
  if (app.freecam) return freeCamera(app, cam, cw, ch, app.freecam);
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
