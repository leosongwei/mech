// The key map, as data.
//
// One entry per `e.code` the client cares about: what the key does (`action`),
// whether the browser's own handling of it has to be swallowed (`prevent`), and
// whether it is a one-shot - the browser repeats keydown while a key is held, so
// `once` drops the repeats and the game sees one pull.  `hold` is the movement
// vocabulary: those codes land in `Input.keys` and `moveVector()` reads them.
//
// The table is the whole of what used to be an if-chain in `Input._key`, which
// is the one thing a new binding should never have to touch again: `lookup()`
// answers "what does this code do in this panel", and `Input` dispatches on the
// answer.

export const KEYS = {
  KeyW: { hold: true, prevent: true },
  KeyA: { hold: true, prevent: true },
  KeyS: { hold: true, prevent: true },
  KeyD: { hold: true, prevent: true },
  Space: { hold: true, prevent: true },
  Tab: { hold: true, prevent: true },
  KeyQ: { action: 'smoke', once: true },      // one pull of the smoke rack, sent once
  KeyB: { action: 'shop', prevent: true },    // open / close the rearm panel
  KeyH: { action: 'help' },
  KeyV: { action: 'assist', once: true },     // the cross's snap to a pre-aim ring
  AltLeft: { action: 'freeLook', prevent: true },
  AltRight: { action: 'freeLook', prevent: true },
};

// What a panel owns while it is up.  The panel is consulted before the table
// above, so a key it names is taken away from the game for as long as it is
// open; a key it does not name falls through to the game unchanged.
//
// The settings panel is modal: `lookup()` swallows every key it does not name,
// so nothing but ESC reaches the game from there.
export const PANEL = {
  settings: {
    Escape: { action: 'settings', prevent: true },
  },
  // ENTER confirms the draft, ESC and B drop the panel, a digit picks the
  // weapon for an arm (SHIFT for the right one)
  shop: {
    Enter: { action: 'shopConfirm', prevent: true },
    Escape: { action: 'shopCancel', prevent: true },
    KeyB: { action: 'shopCancel', prevent: true },
    Digit1: { action: 'pick', prevent: true },
    Digit2: { action: 'pick', prevent: true },
    Digit3: { action: 'pick', prevent: true },
    Digit4: { action: 'pick', prevent: true },
    Digit5: { action: 'pick', prevent: true },
    Digit6: { action: 'pick', prevent: true },
  },
};

// A key the open panel keeps for itself and does nothing with: the settings
// panel's every key that is not its own close binding.
export const SWALLOW = { swallow: true };

// Which panel kinds are modal - they take the whole keyboard, not a list of it.
const MODAL = new Set(['settings']);

// The pointer's two buttons: LMB fires the left arm, RMB the right.  Both come
// up on mouseup and whenever the pointer lock is lost, so a trigger is never
// left latched by a browser that ate the release.
export const BUTTONS = { 0: 'fireL', 2: 'fireR' };

/**
 * What a code does right now: the open panel's binding if it has one, else the
 * game's, else `SWALLOW` for a modal panel, else null.
 *
 * @param {string} code  `KeyboardEvent.code`
 * @param {string|null} panel  `'shop'` / `'settings'` while one is up
 */
export function lookup(code, panel) {
  if (panel) {
    const own = PANEL[panel];
    const entry = own && own[code];
    if (entry) return entry;
    if (MODAL.has(panel)) return SWALLOW;
  }
  return KEYS[code] || null;
}

// Digit1..Digit6 pick the weapon for an arm; the panel decides which arm
// SHIFT is.  Returns the index the panel speaks in (0-based), -1 for anything
// else.
export function pickArm(code) {
  return /^Digit[1-6]$/.test(code) ? parseInt(code.slice(5), 10) - 1 : -1;
}
