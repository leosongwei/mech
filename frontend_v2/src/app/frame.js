// The frame: one ordered list of stages, and nothing else.
//
// The old `Game.frame()` was a 64-line method that did the timing, the input
// read, the interpolation, the sound field, the net send, the damage check, the
// particles, the camera, five aiming passes and the draw - in an order that only
// a comment explained.  Here the order *is* the list, so a reader (and
// `AGENTS.md`) can see what a frame is, and a stage can be lifted out or left out
// without touching its neighbours.
//
// The order is the old one, stage for stage:
//
//   time    the frame's clock and the decay of everything with a life
//   input   the optics step and the smoothed magnification
//   net     interpolate the newest snapshot into what is drawn
//   sound   the acoustic field: play what the wavefront has reached
//   send    the 30 Hz input message (and the ack the clock is timed on)
//   own     the pilot's hull: pose, death spot, the panel and damage checks
//   fx      particles, muzzle flashes' clock, missile trails, wreck smoke
//   camera  cockpit or death orbit; the HUD's own viewport follows it
//   aim     the sight, the centre cross and each arm's impact marker
//   sense   the receiver, then the rings it feeds, then the two trills
//   render  emit the world into the instance batches and draw it, then the HUD
//
// `sense` is after `aim` because the pre-aim boxes are drawn on what the receiver
// has placed, and because an arrow springs from the eye the sight is mounted on.

import { TICK_HZ } from '../core/config.js';
import { emitProjectiles, emitSmoke, emitUnits } from '../scene/index.js';
import { missileTrails, wreckSmoke } from '../fx/dispatch.js';
import { checkDamage, noLockFeedback, updateLockTone } from './systems/feedback.js';
import { hudstate } from './hudstate.js';

// the frame's shared scratch: one object, reused, so a stage takes its inputs by
// reference instead of by argument list
const ctx = { app: null, dt: 0, now: 0, own: null, cam: null };

function tick(app) {
  const { dt } = ctx;
  app.fps = app.fps * 0.92 + (1 / Math.max(dt, 1e-3)) * 0.08;
  for (const m of app.messages) m.life -= dt;
  app.messages = app.messages.filter((m) => m.life > 0);
  for (const p of app.moneyPops) p.life -= dt;
  app.moneyPops = app.moneyPops.filter((p) => p.life > 0);
  app.hitFlash = Math.max(0, app.hitFlash - dt * 1.6);
  app.shake = Math.max(0, app.shake - dt * 2.2);
}

function readInput(app) {
  const { dt } = ctx;
  app.input.update(dt);
  if (app.input.zoomIdx !== app.zoomIdx) {
    app.zoomIdx = app.input.zoomIdx;
    app.renderer.setZoomLevel(app.zoomIdx);
  }
  app.zoom += (app.input.zoom - app.zoom) * (1 - Math.exp(-dt * 22));
  app.zeroRange = app.input.zeroRange;
}

function netIn(app) {
  app.net.interpolate(1 - Math.exp(-ctx.dt * 14));
}

function sound(app) {
  // the pilot's eye is where the wavefront has to have arrived, and the sound
  // field reads the live position and the live yaw for exactly that reason.
  // Its clock is seconds (`performance.now() / 1000`, the same one `push`
  // stamps); `ctx.now` is milliseconds, because the models' gatling spin reads
  // it that way.
  app.audio.field.update(ctx.own, app.input.viewYaw, ctx.now / 1000);
}

function sendInput(app) {
  const { dt } = ctx;
  app.sendAcc += dt;
  if (app.sendAcc < 1 / TICK_HZ) return;
  app.sendAcc = 0;
  const [mx, mz] = app.input.moveVector();
  const aim = app.aim.sightAim();
  const msg = {
    t: 'in',
    yaw: aim.yaw,
    pitch: aim.pitch,
    mx, mz,
    fl: app.shopOpen ? false : app.input.fireL,
    fr: app.shopOpen ? false : app.input.fireR,
    // the IR lock circle is a fixed slice of the screen: the server needs the
    // magnification and the viewport shape to match it
    z: app.zoom,
    a: app.glCanvas.clientWidth / Math.max(1, app.glCanvas.clientHeight),
    // sight setting the gun is zeroed to, so the server fires the barrel
    // elevation the reticle promises
    zr: app.input.zeroRange,
    // hot smoke: one pull per press, cleared as soon as it is on the wire
    q: app.shopOpen ? false : app.input.qPressed,
    // the newest server tick this client has seen: the frame clock reads it, and
    // every binary frame echoes it back (that is what RTT is timed on).  `ack()`
    // stamps the moment this value first goes out.
    st: app.net.ack(),
  };
  app.net.send(msg);
  app.input.qPressed = false;
}

function ownState(app) {
  const own = app.ownEntity();
  ctx.own = own;
  // the last place a pilot saw his own hull: the hull leaves the snapshot the
  // moment it dies, so the death cam reads the spot off this instead
  if (own) app.deathSpot = { x: own.x, z: own.z };
  if (app.shopOpen && !own) app.closeShop();
  checkDamage(app, own);
  noLockFeedback(app, own, ctx.dt);
}

function fxStep(app) {
  const { dt } = ctx;
  app.particles.update(dt);
  for (const [id, f] of app.flashes) {
    f.t -= dt;
    if (f.t <= 0) app.flashes.delete(id);
  }
  missileTrails(app);
  wreckSmoke(app, dt);
}

function cameraStage(app) {
  const cam = app.camera(ctx.own, ctx.dt);
  ctx.cam = cam;
  app.hud.resize();
  app.hud.vp = cam.viewProj;
}

function aimStage(app) {
  app.aim.run(ctx.own);
}

function senseStage(app) {
  // the radar first: the pre-aim boxes are drawn on what it has seen
  app.rwr.update(app, ctx.own);
  app.lead.update(ctx.own);
  app.lead.screen(ctx.own);
  app.rwr.alarms(app, ctx.own);
  updateLockTone(app, ctx.own);
  app.rwr.arrows(app);
}

function render(app) {
  const { own, cam } = ctx;
  const ownId = own ? own.id : -1;
  app.renderer.draw(cam, (b) => {
    emitUnits(b, app.net.entities, {
      defs: app.defs, flashes: app.flashes, ownId, now: ctx.now,
    });
    emitProjectiles(b, app.net.projectiles, { defs: app.defs });
    emitSmoke(b, app.net.smoke);
    app.particles.emit(b);
  });
  app.hud.draw(hudstate(app, own), cam.viewProj);
}

export const STAGES = [
  ['time', tick],
  ['input', readInput],
  ['net', netIn],
  ['sound', sound],
  ['send', sendInput],
  ['own', ownState],
  ['fx', fxStep],
  ['camera', cameraStage],
  ['aim', aimStage],
  ['sense', senseStage],
  ['render', render],
];

export function frame(app) {
  const now = performance.now();
  ctx.app = app;
  ctx.now = now;
  ctx.dt = Math.min(0.1, (now - app.last) / 1000);
  app.last = now;
  for (const [, stage] of STAGES) stage(app);
  requestAnimationFrame(() => frame(app));
}
