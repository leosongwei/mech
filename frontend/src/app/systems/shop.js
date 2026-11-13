// The rearm shop: the draft loadout, its quote, and the one message that
// spends money.
//
// B opens a draft at a base or an owned town; nothing is charged until CONFIRM,
// and then the server prices the whole draft again and either takes it all or
// refuses it (see `Core/Shop.cs`).  While the panel is up the mouse is a cursor,
// so the trigger is off and the pointer lock is released - that half is the
// panel state machine's (`app/panels.js`), and this file is what the panel
// *does*.

import { emptyDraft, isEmpty, quickDraft, quote, toMessage } from '../../shop.js';
import { hudstate } from '../hudstate.js';
import { closePanel, openPanel } from '../panels.js';

// what `quote` / `toMessage` price a draft against: the arm tables, what the
// rack can be reloaded with here, and the hull the job is booked on
export function shopCfg(app) {
  return { weapons: app.defs.weapons, buyable: app.buyable,
           rearm: app.rearm, mech: app.defs.mech };
}

export function toggleShop(app) {
  if (!app.started) return;
  if (app.shopOpen) { closeShop(app); return; }
  if (app.settingsOpen) closePanel(app);
  if (!app.ownEntity()) return;
  // a base or an owned town, and whether a job is already running: the mode
  // answers both, the panel only asks
  if (!hudstate(app, app.ownEntity()).atBase) {
    app.message('REARM ONLY AT BASE OR AN OWNED TOWN', '#ff9a9a');
    return;
  }
  if (app.net.meta && app.net.meta.work) {
    app.message('ALREADY REARMING', '#ff9a9a');
    return;
  }
  app.shopDraft = emptyDraft();
  app.shopDrop = -1;
  openPanel(app, 'shop');
}

export function closeShop(app) {
  if (!app.shopOpen) return;
  closePanel(app);
}

export function confirmShop(app) {
  buy(app, app.shopDraft);
}

// Send one priced draft.  The server prices it again, charges it and books
// the work it takes (the mech is held still until the bars are full again).
export function buy(app, draft) {
  if (!app.shopOpen) return;
  if (isEmpty(draft)) { app.message('NOTHING TO BUY', '#ff9a9a'); return; }
  const q = quote(shopCfg(app), app.net.meta || {}, draft);
  if (!q.ok) {
    app.message(q.lines.length ? 'NOT ENOUGH MONEY' : 'NOTHING TO BUY', '#ff9a9a');
    return;
  }
  app.net.send(toMessage(draft));
  closeShop(app);
}

export function shopClick(app, x, y) {
  if (!app.shopOpen) return;
  const id = app.hud.hitUi(x, y);
  const d = app.shopDraft;
  if (!id) { app.shopDrop = -1; return; }
  if (id === 'disabled') return;              // greyed out: nothing to buy
  if (id.startsWith('pick:')) {
    // "pick:<arm>:<weapon>" - the entry a pulled-down list handed back
    const [, arm, wid] = id.split(':');
    d.arms[+arm] = +wid;
    app.shopDrop = -1;
    return;
  }
  if (id === 'drop0' || id === 'drop1') {
    const arm = id === 'drop0' ? 0 : 1;
    app.shopDrop = app.shopDrop === arm ? -1 : arm;
    return;
  }
  app.shopDrop = -1;
  if (id === 'smoke') d.smoke = !d.smoke;
  else if (id === 'repair') d.repair = !d.repair;
  else if (id === 'lrearm') d.rearm[0] = !d.rearm[0];
  else if (id === 'rrearm') d.rearm[1] = !d.rearm[1];
  else if (id === 'confirm') confirmShop(app);
  else if (id === 'quickall') buy(app, quickDraft());
  else if (id === 'cancel' || id === 'close') closeShop(app);
}
