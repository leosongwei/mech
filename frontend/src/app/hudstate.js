// The state object the HUD draws from - the one contract between the app and the
// two-dimensional overlay.
//
// It is built in five small pieces because the old version was one 84-line
// object literal with fifty keys, and everything that was wrong with it came from
// that shape: the "am I at a base" rule (a mode question) sat between the armour
// bars and the net stats, and the rearm bill was priced twice per frame inside
// the literal.  The builders below are `own`, `aim`, `rwr`, `text` and the mode
// flags, and `hudstate()` is only the merge.
//
// Everything here is a *read* of the app: no builder writes a field, and the one
// expensive thing in the whole file (`quote`, twice, for the rearm panel) only
// runs while that panel is open.

import { RWR_RANGE } from '../core/config.js';
import { quickDraft, quote } from '../shop.js';

// Where the rearm panel may be opened: the range mode rearm anywhere, a battle
// only inside 220 m of your own base ring or a town your side owns.  The mode
// answers it (this is the client side of `rearm_anywhere` / the capture rule).
export function atBase(app, own, faction) {
  if (app.range) return true;         // range mode: rearm anywhere
  if (!own) return false;
  const b = app.init.bases[faction];
  if (Math.hypot(own.x - b[0], own.z - b[1]) < 220) return true;
  for (const t of app.net.meta.towns || []) {
    if (t.owner === faction && Math.hypot(own.x - t.x, own.z - t.z) < 220) return true;
  }
  return false;
}

// The pilot's own machine: speed, armour, the sight's readings.
function ownState(app, own) {
  return {
    own,
    dead: !own,
    // the hull's ground speed: the snapshot velocity is the one it is really
    // moving at (it ramps up over three seconds), `mv*` its smoothed copy
    speed: own ? Math.hypot(own.mvx || 0, own.mvz || 0) * 3.6 : 0,
    mechHp: (app.defs.mech && app.defs.mech.hp) || [100, 0, 50, 50],
    // two headings on the glass: the centre cross is the view, the white one the
    // snapped aim the guns are actually laid on
    aim: app.aimSnap
      ? app.hud._project(app.aimSnap.x, app.aimSnap.y, app.aimSnap.z)
      : null,
    crosshairs: app.crosshairs,
    sightRange: app.sightRange,
    lock: app.lockInfo,
    lockFrac: app.lockFrac,
  };
}

// The receiver: the dial's marks, its rim, and the ring the arrows live in.
function rwrState(app) {
  const rwr = app.rwr;
  return {
    threats: rwr.threats,
    // metres at the edge of the threat dial: a constant face (`RWR_RANGE`), so
    // the corner label is the same in every machine and a mark's distance from
    // the middle is its real distance
    radarScale: RWR_RANGE,
    // the one boolean the rim and the trill both read
    radarAlarm: rwr.alarm,
    // the hull's own echo floor - a contact inside this has a position, one
    // outside it does not (the AI and the RWR solve the same equation)
    radarRing: rwr.ring,
    lasers: rwr.lasers,
  };
}

// The settings and the panels that are up.
function textState(app, faction) {
  const m = app.net.meta || {};
  const shopOpen = app.shopOpen;
  return {
    meta: m,
    net: { rtt: app.net.rttMs, tps: app.net.tps, loss: app.net.loss, held: app.net.held },
    weapons: app.defs.weapons,
    units: app.defs.units,
    buyable: app.buyable,
    rearm: app.rearm,
    towns: m.towns || [],
    playerFaction: faction,
    smoke: m.smoke === undefined ? null : m.smoke,
    smokeCd: m.smoke_cd || 0,
    messages: app.messages,
    moneyPops: app.moneyPops,
    winner: m.winner !== undefined ? m.winner : app.winner,
    shopOpen,
    settingsOpen: app.settingsOpen,
    volume: app.audio.volume,
    alarmVolume: app.audio.alarmVolume,
    debug: app.debug,
    shopDraft: app.shopDraft,
    shopDrop: app.shopDrop,
    // the panel's bill: priced only while it is up, and only when the draft or
    // the machine moved (the old version priced the same draft three times a
    // frame, one of them for a button it had not drawn yet)
    shopQuote: shopOpen ? shopBill(app, m, app.shopDraft) : null,
    // the one-click REPAIR & REARM button prices its own draft, so it can show
    // what it would cost and how long it would take
    shopQuick: shopOpen ? shopBill(app, m, quickDraft()) : null,
    assistOn: app.aimAssist,
    ringScale: app.assistRing,
    zoom: app.zoom,
    zoomIdx: app.zoomIdx,
    zeroRange: app.zeroRange,
    zeroIdx: app.input.zeroIdx,
    zeroSteps: app.input.zeroSteps,
    mouse: [app.input.mx, app.input.my],
    range: app.range,
    hitFlash: app.hitFlash,
    helpOpen: app.helpOpen,
    stats: { fps: app.fps, draws: app.renderer.stats.draws, instances: app.renderer.stats.instances },
  };
}

// A draft's price, memoised for the frame: the panel asks for the same draft
// twice (the changes column and the confirm button) and the quote is a loop over
// every line.
let billDraft = null;
let billMeta = null;
let billOut = null;
function shopBill(app, meta, draft) {
  if (billDraft === draft && billMeta === meta) return billOut;
  billDraft = draft;
  billMeta = meta;
  billOut = quote(app.shopCfg(), meta, draft);
  return billOut;
}

export function hudstate(app, own = app.ownEntity()) {
  const m = app.net.meta || {};
  const faction = m.faction !== undefined ? m.faction : app.playerFaction;
  return {
    ...textState(app, faction),
    ...ownState(app, own),
    ...rwrState(app),
    entities: app.net.entities,
    contacts: app.net.contacts,
    leads: app.leads,
    atBase: atBase(app, own, faction),
  };
}
