// The settings panel: the two volume sliders, the aim assist and its ring, and
// the debug knobs.
//
// The gear at the top right - pushed in from the corner to make room for the
// warning circle - opens it.  It needs a free pointer, so it is clickable right
// after ESC; like the rearm panel it owns the mouse while it is up, and the
// panel state machine (`app/panels.js`) is what owns the pointer.

import { ASSIST_KEY, RING_DEFAULT, RING_KEY, RING_MAX, RING_MIN } from '../../core/config.js';
import { clamp } from '../../core/math.js';
import { load, save } from '../../core/storage.js';
import { closePanel, openPanel } from '../panels.js';

// The aiming rule ships switched on: the assist is what makes the pre-aim rings
// usable on a moving hull, and a pilot who wants the cross free turns it off.
export function savedAssist() {
  return load(ASSIST_KEY, '1') !== '0';
}

// How wide the ring the cross is caught by is drawn, as kept in storage.
export function savedRing() {
  const v = parseFloat(load(RING_KEY, ''));
  return isFinite(v) ? clamp(v, RING_MIN, RING_MAX) : RING_DEFAULT;
}

export function toggleSettings(app) {
  if (!app.started) return;
  if (app.settingsOpen) { closeSettings(app); return; }
  if (app.shopOpen) closePanel(app);
  openPanel(app, 'settings');
}

export function closeSettings(app) {
  if (!app.settingsOpen) return;
  closePanel(app);
}

// The aiming rule, remembered across reloads the way the volume is.  Dropping
// the snap with it means the guns return to the view the moment it goes off.
export function setAimAssist(app, on) {
  app.aimAssist = !!on;
  app.aimSnap = null;
  save(ASSIST_KEY, app.aimAssist ? '1' : '0');
}

// V throws the same switch the settings panel's AIM ASSIST button does, for the
// pilot who wants the cross back under his own hand without dropping the pointer
// and clicking through the panel.
export function toggleAssist(app) {
  setAimAssist(app, !app.aimAssist);
  app.message(`AIM ASSIST ${app.aimAssist ? 'ON' : 'OFF'}`, '#a9f0c0');
}

// How wide the ring the cross is caught by is drawn - the setting that trades
// reach against precision.  Half multiples, kept in storage beside the rule.
export function setRing(app, v) {
  app.assistRing = clamp(Math.round(v * 2) / 2, RING_MIN, RING_MAX);
  app.aimSnap = null;
  save(RING_KEY, String(app.assistRing));
}

// The panel's three sliders are one control: the bar is absolute - where the
// click landed across its rect, between the ends it is drawn over - and the two
// buttons step by their own amount.  The ring's bar carries its ends on the
// rect (`lo` / `hi`); the two volumes are 0..1 bars that carry none.  Answers
// true when the id was this slider's.
function slider(app, id, x, key, step, get, apply) {
  if (id !== key && id !== `${key}-` && id !== `${key}+`) return false;
  const r = app.hud[`${key}Rect`];
  const dir = id === `${key}-` ? -step : id === `${key}+` ? step : 0;
  const lo = r && r.lo !== undefined ? r.lo : 0;
  const hi = r && r.hi !== undefined ? r.hi : 1;
  const v = dir ? get() + dir : r && r.w ? lo + (x - r.x) / r.w * (hi - lo) : 0;
  apply(v);
  return true;
}

export function settingsClick(app, x, y) {
  if (!app.settingsOpen) return;
  const id = app.hud.hitUi(x, y);
  if (!id) return;
  // the master volume: the bar is 0..1, the two buttons a 5% step
  if (slider(app, id, x, 'vol', 0.05,
             () => app.audio.volume, (v) => app.audio.setVolume(v))) return;
  // the threat receiver's own slider, same control, separate bus
  if (slider(app, id, x, 'alarm', 0.05,
             () => app.audio.alarmVolume, (v) => app.audio.setAlarmVolume(v))) return;
  if (id === 'assist') {
    setAimAssist(app, !app.aimAssist);
    return;
  }
  // the ring's width: the bar absolute between its two ends, the buttons a half
  // multiple - `setRing` rounds and clamps whatever the control read out
  if (slider(app, id, x, 'ring', 0.5,
             () => app.assistRing, (v) => setRing(app, v))) return;
  if (id === 'respawn') {
    app.net.send({ t: 'respawn' });
  } else if (id === 'restart') {
    app.net.send({ t: 'debug_restart' });
  } else if (id === 'god') {
    const on = !(app.net.meta && app.net.meta.god);
    app.net.send({ t: 'debug_god', on });
  } else if (id === 'close') {
    closeSettings(app);
  }
}
