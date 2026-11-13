// Every pixel the HUD has an opinion about, in one file.
//
// The look constants used to sit at the top of `hud.js` next to the class, and
// every other one was an inline number in the block that happened to need it -
// so "where does the minimap sit" was answered by reading `_minimap`, and the
// same 16 px margin appeared in four places.  Nothing here is computed from
// another number: a ladder is named, not unified, because the blocks were laid
// out to their own edges and moving one would move the picture.
//
// The two functions at the bottom are where the whole screen is measured from:
// the warning dial in the top right corner and the arrow ring beside it.

import { TAU } from '../core/math.js';

export { TAU };

export const MESSAGE_FONT = '12px "Courier New", monospace';
export const MESSAGE_BOLD = `bold ${MESSAGE_FONT}`;
export const MESSAGE_COLOR = '#cfe6d6';
// the kill line's plate: a little wider than the text and tall enough to hold a
// 12px line with its descenders (`KILL_TOP` is from the baseline)
export const KILL_PAD = 6;
export const KILL_TOP = 13;
export const KILL_H = 17;

export const ARM_COLORS = ['#5fe6ff', '#ffb04a'];
export const ARM_NAMES = ['L', 'R'];
export const FAR_COLOR = '#ff4a4a';        // distance past the fire control's reach
export const LEAD_COLOR = '#f2f6f4';       // the box round a hull with a pre-aim solution
export const LEAD_RING = 7;         // px, radius of a pre-aim circle; the aim
                                    // assist widens it (`game._ringPx`)
// How close the two centres of one target's pair may come, as a multiple of the
// ring radius: the pair overlaps instead of standing tangent (`game._layoutPair`).
// 1.0 puts each centre on the other ring's own arc - they are still two circles
// cut by one white line, and the cross no longer has to travel two full radii
// out to the side to pick one.
export const LEAD_PAIR_SEP = 1.0;
export const RADAR_PX = 78;                // px, outer radius of the threat circle
export const RADAR_MARGIN = 22;            // px between the dial and the screen edges
export const GEAR_EDGE = 8;                // px from the top and right edges to the gear

// The warning dial and, beside it, the ring the threat arrows are arranged in -
// the two are the same size and level, so they read as one instrument: the dial
// says where the marks are, the ring says which way to look.
export const ARROW_R = RADAR_PX;    // the arrow ring is the dial's twin
export const RING_GAP = 16;                // px between the two circles
export const RING_ALARM_W = 12.8;          // px, the rim's width while a round is inbound

// ---------------------------------------------------------------- the ladders
// The map in the corner, the bar over the middle of the screen, the arm panels,
// the sight block, the speed and hull bars under the map, the message stack and
// the status lines down the left and right edges.

export const MINIMAP = { size: 210, x: 16, y: 16 };

export const TOPBAR = {
  wave_y: 26, time_y: 42, note_y: 58,       // WAVE / clock / TARGETS, centred
  base_y: 58,                               // the first integrity bar
  base_w: 150, base_h: 5, base_step: 18, base_label_dy: 3,
};

export const ARMS = { w: 250, h: 56, y_off: 96, gap: 10 };

// The sight block: the aim-assist row, the optics row and the zero row, one
// `slot` wide per cell, all centred on the screen's middle.
export const OPTICS = {
  slot: 44, y_off: 140,                      // the optics row's baseline
  assist_dy: 34,                            // the assist row, above it
  zero_dy: 34,                              // the zero row, below it
  live_dx: 10, live_dy: 5,                  // the live magnification, to the right
};

export const SPEED = { x: 16, w: 168, h: 20, y_off: 164, bar_dy: 16, bar_h: 3, text_dy: 12 };

export const MODULES = { x: 16, w: 168, h: 15, y_off: 118, step: 20 };

export const MESSAGES = { y: 62, step: 17, cap: 8, gap: 10 };

export const STATUS = {
  x: 16, money_y: 252, kills_y: 270,
  board_y: 292, board_step: 15,
  rearm_dy: 10, rearm_step: 22,
  net_y: 16, net_step: 16,                  // from the bottom edge up
};

export const WORK = { w_max: 420, w_frac: 0.44, h: 10, y_frac: 0.78, pad: 3,
  label_dy: 9, note_dy: 16 };

export const DEATH = { title_dy: 20, redeploy_dy: 16, note_dy: 44, note2_dy: 64 };

export const GAMEOVER = { y_frac: 0.3, verdict_dy: 30, timer_dy: 56 };

export const HIT_FLASH = { r0: 0.22, r1: 0.62, max_alpha: 0.5 };

export const SHOP = { w_max: 980, w_frac: 0.9, h_max: 700, h_frac: 0.92 };
export const SETTINGS = { w_max: 560, w_frac: 0.84 };

// The two mouse panels' shells.  They differ by their scrim and their body and
// by nothing else, so `canvas.panel` takes one of these instead of a colour
// pair per call.
export const PANEL_SHOP = { scrim: 'rgba(4,8,10,0.66)', body: 'rgba(10,18,20,0.95)' };
export const PANEL_SETTINGS = { scrim: 'rgba(4,8,10,0.55)', body: 'rgba(10,18,20,0.96)' };

// The dial and the ring beside it, each answered into its own reused object:
// both are the same size and level, and a frame asks for them from three blocks
// (`messages`, `dial`, the renderer's arrow pass), so the pair is two cached
// points and never an object per call site.
const dialOut = { x: 0, y: 0 };
const arrowOut = { x: 0, y: 0 };

export function dialAt(w) {
  dialOut.x = w - RADAR_PX - RADAR_MARGIN;
  dialOut.y = RADAR_PX + RADAR_MARGIN + 10;
  return dialOut;
}

export function arrowRingAt(w) {
  const d = dialAt(w);
  arrowOut.x = d.x - RADAR_PX - RING_GAP - ARROW_R;
  arrowOut.y = d.y;
  return arrowOut;
}
