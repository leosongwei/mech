// The modal panels: one state machine for the rearm shop and the settings menu.
//
// `openPanel` / `closePanel` are the six lines that open a panel and the five
// that close it - `input.uiOpen` / `uiKind`, the keys the keyboard is still
// holding, both trigger bits, the latched smoke pull and the pointer lock - which
// the old client carried a copy of in `_shopToggle`, `_settingsToggle`,
// `_shopClose` and `_settingsClose`.  A panel opens over another one by closing
// it first, and a close hands the mouse back to the cockpit.
//
// The import goes one way: the two panels' systems (`app/systems/shop.js`,
// `app/systems/settings.js`) call in here, and this file reaches back for a
// panel's own click handler through the app (`app.shopClick`, ...), so a panel's
// gates stay with the panel.

// `hud.hot` is the frame's list of clickable boxes (`hud/canvas.js HitList`).
// The list a panel handed out while it was up is dead the moment it closes, so
// it is emptied here rather than surviving into the next frame.
function clearHot(hud) {
  if (hud.hot.reset) hud.hot.reset();
  else hud.hot.length = 0;
}

// pointer lock is refused in automated tabs and while the panel is up; a
// rejected promise there is expected, not an error to log
export function lockPointer(app) {
  try {
    const p = app.glCanvas.requestPointerLock();
    if (p && p.catch) p.catch(() => {});
  } catch (e) { /* not enabled, nothing to do */ }
}

// which panel is up, `'shop'` / `'settings'`, or null for the cockpit
export function panelOpen(app) {
  return app.shopOpen ? 'shop' : app.settingsOpen ? 'settings' : null;
}

export function openPanel(app, kind) {
  // one panel at a time: the panel that was up closes itself down, pointer and
  // all, and the lock is released again below for the one opening
  if (panelOpen(app)) closePanel(app);
  if (kind === 'shop') app.shopOpen = true;
  else app.settingsOpen = true;
  app.input.uiOpen = true;
  app.input.uiKind = kind;
  app.input.keys.clear();
  app.input.fireL = app.input.fireR = false;
  app.input.qPressed = false;
  if (document.pointerLockElement) document.exitPointerLock();
}

export function closePanel(app) {
  if (!panelOpen(app)) return;
  app.shopOpen = false;
  app.settingsOpen = false;
  app.shopDrop = -1;
  app.input.uiOpen = false;
  app.input.uiKind = null;
  clearHot(app.hud);
  if (app.ownEntity() && app.started) lockPointer(app);
}

export function togglePanel(app, kind) {
  if (!app.started) return;             // a panel belongs to a match
  if (panelOpen(app) === kind) { closePanel(app); return; }
  openPanel(app, kind);
}

// clicks on the HUD while the pointer is free; true when the click was taken.
// The gear at the top right is the one button out here - everything else is the
// cockpit's, and answering false is what grabs the pointer back for it.
export function hudClick(app, x, y) {
  const r = app.hud.gearRect;
  if (!r || x < r.x || x > r.x + r.w || y < r.y || y > r.y + r.h) return false;
  togglePanel(app, 'settings');
  return true;
}

// the canvas click while a panel is up: the pointer is a cursor, and the panel
// that owns it gets the hit test
export function onUiClick(app, x, y) {
  if (app.settingsOpen) app.settingsClick(x, y);
  else app.shopClick(x, y);
}
