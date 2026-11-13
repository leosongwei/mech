// The numbers more than one module needs, in one place.
//
// A constant lives here when two modules have to agree on it and neither owns the
// other: the sight's optics and the thing the sight is drawn on, the threat dial
// and the receiver that fills it, the look of the world's shake.  What belongs to
// one system stays in that system's file (`hud/layout.js` for the pixels,
// `audio/levels.js` for the distance laws, `app/systems/rwr.js` for the radar).
//
// The rule this file exists to keep: a module that needs a number imports it from
// here, never from the module that happens to use it - `hud` used to import the
// warning tone's period from `audio` and the optics steps from `input`.

export const TICK_HZ = 30;                  // the server's clock (`Netcode.TARGET_HZ`)
export const SNAP_HZ = 15;                  // snapshots a second (`Netcode.SNAP_EVERY` = 2 ticks)
export const BASE_FOV = 1.05;               // 1x vertical field of view, radians
export const AIM_LINE = 60000;              // metres, "infinity" for an unzeroed reticle
export const LEAD_TARGETS = 4;              // nearest few only, the rest is clutter
export const ZOOM_LEVELS = [1, 4, 10, 20];  // the sight's steps, and the ground's LOD index
export const RWR_RANGE = 3000;              // m, the threat dial's face and the receiver's
                                            // display ceiling: the snapshot's guided tier
                                            // (`Netcode.TIER_ROUND`), so every mark on the
                                            // dial is a round the wire would have sent

// How long a line lives on the message stack: a kill is read *after* the fight
// it belongs to (a battle writes one every few seconds), a notice about the
// machine in the pilot's hands is read as it happens.
export const KILL_LIFE_S = 20;
export const MSG_LIFE_S = 6;
// and how long `+$N` sits beside the money readout before it fades
export const MONEY_POP_S = 2.5;

// The alarm is the same detector as the track, run 12 dB looser: one frame, no
// track initiation, no angle association, no range solve.  So the distance at
// which a round is *heard* is sqrt(10^0.6) = 1.995 times the distance at which
// it is *placed* - 2728 m against 1368 m for a SACLOS, which is the whole of
// "bearing far out, fire control up close".  `rwr` applies it as
// `d^2 <= ALARM_GAIN * K * v_c`, and `v_c` is saturated at `v_ref` so that
// ceiling is a constant (2728 m) and can never cross `RWR_RANGE`.
export const ALARM_DB = 12;
export const ALARM_GAIN = Math.pow(10, ALARM_DB / 20);   // 3.98, on the d^2 scale
// The warning gate: the closest approach of a round's *horizontal* ray may come
// this near `er + WARN_MARGIN`.  A guided round 2 km out has a lateral error it
// is still correcting, so the tolerance opens out with range - at
// `d_h * sin(WARN_CONE)` - and folds back to the fixed margin inside ~0.5 km.
// 3 deg is the alarm's conservatism, not the seeker's field of view.  One
// tolerance, two rays: the *relative* one is `warn`, the round's own is `aim`.
export const WARN_MARGIN = 20;              // m of slack over the round's own `er`
export const WARN_CONE = 3 * Math.PI / 180; // rad, the gate's opening angle
// The bearing history: a round the receiver cannot place is tracked by the
// drift of its horizontal bearing alone.  `STEADY_T` is how much of that
// history a verdict needs; the threshold itself is `(er + WARN_MARGIN) / K`,
// which is the drift rate that puts the closest approach exactly at the margin
// on the `seen` boundary (`docs/product/rwr.md` §7.3).
export const BEAR_WIN = 0.6;                // s of bearing history kept per round
export const BEAR_MAX = 96;                 // safety cap on samples (a stalled tab)
export const STEADY_T = 0.2;                // s, the window a verdict is read over

// the threat arrows in the ring beside the warning dial: the nearest few rounds
// that are coming for this hull get a 3D arrow pointing at where they are on
// screen, so the pilot turns his sight onto them and shoots them down.  The ring
// is the dial's twin (`hud/layout.js arrowRingAt`) and every arrow of the frame
// springs from its centre, `ARROW_LEN` long, so three bearings read as three
// needles
export const RWR_ARROWS = 3;                // at most this many, nearest first
export const ARROW_LEN = 66;                // px, the centre of the ring to the tip
export const ARROW_HEAD = 26;               // px, the cone on the end
export const ARROW_WID = 14;                // px, the shaft's diameter
export const ARROW_RGB = [1.0, 0.29, 0.29];
export const LASER_RANGE = 3400;            // longest sight an enemy could shoot at
export const LASER_CONE = 0.05;             // rad, "his gun is pointed at me"

// settings keys and the ring the aim assist catches the cross with: a multiple
// of the plain pre-aim circle - wide grabs from further off, narrow asks the
// pilot to mean it
export const NAME_KEY = 'mech.name';        // the callsign, remembered across reloads
export const ASSIST_KEY = 'mech.aimassist'; // the aiming rule, remembered too
export const RING_KEY = 'mech.aimring';     // how wide the ring it snaps to is drawn
export const RING_MIN = 2;
export const RING_MAX = 8;
export const RING_DEFAULT = 4;

export const SMOKE_RGB = [0.58, 0.52, 0.44]; // dirt brown, like the charge it is
export const SMOKE_END_SHRINK = 0.35;        // s, last moments of a puff (it is opaque)

// A bang may stay in the air this long: the loudest thing a pilot can hear is a
// hull going up ~17 km out, and its wavefront takes 50 s to arrive - a shorter
// TTL would be the real cutoff and `SOUND_FLOOR` would never be reached.  The
// server gates events by the same two numbers (`defs.SOUND_TTL`).
export const SOUND_TTL = 50;                // s, a bang still in the air after this is dropped
export const EAR_NEAR = 1;                  // m, closer than this a bang has no side to be on

export const SHAKE_BOOM_BIG = 0.225;        // near blast, big round (a quarter of the old kick)
export const SHAKE_BOOM_SMALL = 0.0875;     // near blast, shell hit
export const SHAKE_LAUNCH = 0.5;            // own missile leaving the tube
export const SHAKE_GUN = 0.12;              // own cannon round, "slight" (a burst adds up)
export const SHAKE_HEAVY = 2.1;             // own 105 mm round: five times a gun's kick -
                                            // it is fired once every 5 s, so it can be huge
export const SHAKE_MAX = 2.4;               // ceiling on the accumulator, set so a 105 mm
                                            // round is felt in full instead of being cut off
