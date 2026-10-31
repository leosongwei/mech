"""Static game data: factions, entity types, unit statistics and weapons.

Everything here is also sent to the browser so client prediction uses exactly
the same ballistics numbers as the server.
"""

import math

# ---------------------------------------------------------------- factions
FACTION_RED = 0
FACTION_BLUE = 1
FACTION_CIV = 2
FACTION_NATURE = 3

FACTION_COLORS = {
    FACTION_RED: (0.85, 0.24, 0.18),
    FACTION_BLUE: (0.22, 0.44, 0.92),
    FACTION_CIV: (0.62, 0.36, 0.85),
    FACTION_NATURE: (0.90, 0.90, 0.88),
}
FACTION_NAMES = {FACTION_RED: "Red", FACTION_BLUE: "Blue", FACTION_CIV: "Civilians", FACTION_NATURE: "Nature"}

# ------------------------------------------------------------ entity types
ETYPE_NONE = 0
ETYPE_INF_RIFLE = 1
ETYPE_INF_ROCKET = 2
ETYPE_APC = 3
ETYPE_MECH = 4
ETYPE_BASE = 5
ETYPE_CIV = 6
ETYPE_DEBRIS = 7          # inert wreck (vehicle / mech hulk), purely visual
ETYPE_CORPSE = 8          # fallen infantry, purely visual
ETYPE_BUNKER = 9          # indestructible building, a zeroing reference on the range

# mech module indices (hit geometry and health array slots).  A hit on either
# body sphere - the chassis or the torso on top of it - is charged to the one
# hull pool at MOD_CHASSIS; MOD_TORSO only exists so the geometry can name the
# upper sphere, and its health slot is unused.
MOD_CHASSIS = 0
MOD_TORSO = 1
MOD_LEFT = 2
MOD_RIGHT = 3
MODULE_NAMES = ["HULL", "TORSO", "L.ARM", "R.ARM"]

# ----------------------------------------------------------------- weapons
WID_NONE = 0
WID_AC30 = 1
WID_MG127 = 2
WID_ROCKET = 3
WID_SACLOS = 4
WID_TOPATTACK = 5
WID_RIFLE556 = 6
WID_RPG = 7
WID_G105 = 8
# the APC's own defence missile: the same round as the mech's SACLOS, fired out
# of its turret one round every 5 s (see the entry in WEAPONS)
WID_APC_ATGM = 9

# kind: bullet (ballistic), rocket (unguided thrust), saclos (beam riding),
#       top (infrared lock-on, top attack)
# cal: bore in mm, only used to size the tracer the browser draws
# gatling: a rotary gun, client-only: the arm's model draws six barrels round
#          the bore and turns them while the arm is firing
# heavy:   a single big barrel on a boxed receiver, client-only: the 105 mm is
#          modelled to its own numbers (scene.js `GUN105_*`) instead of the
#          30 mm's slim tube
# reach:   metres from the shoulder that the bore ends at, when the weapon is
#          longer than its kind's figure in MECH_ARM_REACH (the 105 mm's box
#          ends at 0.90 m and its barrel runs 5 m out from there).  Both
#          `weapons.fire` and the client's muzzle read it, so a round leaves
#          the mouth it was drawn on.
# ap / ad: Armour Piercing power and the Armour-piercing Damage it delivers
#          behind the plate, both measured against a target's `dp`
# ed / er: Explosive Damage at the centre of the blast and the radius (m) it
#          falls off to nothing over, measured against a target's `de`
# prox:    proximity fuse radius (m) - the round bursts when its flight path
#          passes that close to enemy *personnel* (systems/proximity.py).
#          Absent/0 = impact fuse only.  A hull never sets it off.
# rack:    `rack(...)` (below) for a launcher that carries its rounds in tubes
#          - a round leaves its own tube.  Absent = the round leaves the bore.
#
# ---------------------------------------------------------------- tube racks
# A launcher that carries its rounds in tubes is described once, here, and both
# halves read that one description: the server fires the n-th round out of the
# tube the lattice puts it in (`rack_tube`), the client draws the same lattice
# and the body around it from the same numbers (`scene.js armModel`), and the
# mouth of the tubes *is* the weapon's `reach`, so a round leaves the mouth it
# was drawn on.  A new racked launcher is therefore a data change, not code.


def rack(cols: int, rows: int, count: int | None = None, *, bore: float = 0.19,
         length: float = 1.30, centre: float = 0.75, wall: float = 0.04,
         pitch: float | None = None, end: float = 0.10, frame: float = 0.10,
         elev: float = 0.0, bolted: bool = False,
         box: tuple[float, float, float] | None = None) -> dict:
    """Describe a tube launcher: the lattice, the tubes and the body round them.

    cols / rows  the lattice - `count` rounds are carried (default: every cell)
    bore         tube outside diameter, m
    length       tube length, m
    wall         gap between two tube walls, m  ->  pitch = bore + wall
    pitch        centre spacing, when the tubes are drawn overlapping (the
                 rocket nest packs its cells 0.155 m apart on a 0.27 m bore)
    centre       how far the launcher sits along the bore, m
    end          how far the tubes poke out of each end face, m
    frame        how much wider the body is than the lattice, per side, m
    elev         the launcher's own elevation, rad: what it adds to the angle
                 of the arm it is hung on (0 = mounted flat, the tubes follow
                 the arm exactly - depression included)
    bolted       the tubes are fixed at `elev` and the arm's slewing does not
                 move them at all: the top attack's 42 deg loft is a property
                 of the launcher, not of where the pilot is looking
    box          explicit (length, width, height) when the drawn body is not
                 the one the lattice derives

    The body is a box around the cells and nothing more: the lattice plus
    `frame`, `end` shorter than the tubes at each end - so it reads as a
    launcher with mouths rather than as a solid block with dots on it.
    """
    cells = int(cols) * int(rows)
    n = cells if count is None else int(count)
    if not 0 < n <= cells:
        raise ValueError(f"rack: {n} rounds do not fit {cols}x{rows} cells")
    p = float(bore + wall if pitch is None else pitch)
    lat_w = (int(cols) - 1) * p + float(bore)
    lat_h = (int(rows) - 1) * p + float(bore)
    bl, bw, bh = float(length) - 2.0 * float(end), lat_w + 2.0 * float(frame), lat_h + 2.0 * float(frame)
    if box is not None:
        bl, bw, bh = (float(v) for v in box)
    return dict(cols=int(cols), rows=int(rows), count=n, bore=float(bore),
                length=float(length), pitch=p, centre=float(centre),
                mouth=float(centre) + float(length) * 0.5, elev=float(elev),
                bolted=bool(bolted), box_len=bl, box_w=bw, box_h=bh)


def rack_tube(fired: int, spec: dict) -> tuple[float, float]:
    """(right, up) of the tube the `fired`-th round of a rack leaves from.

    Bottom row first, left to right - so a forty-cell nest with 32 rockets in it
    has its lowest eight cells empty and a four-cell rack empties its lower pair
    first.  `count` may be smaller than the lattice (`rack` refuses anything
    that does not fit); the walk then simply stops before the top row.
    """
    cols, count = int(spec["cols"]), int(spec["count"])
    pitch = float(spec["pitch"])
    i = int(fired) % count
    col, row = i % cols, i // cols
    return ((col - (cols - 1) / 2.0) * pitch,
            (row - (int(spec["rows"]) - 1) / 2.0) * pitch)


# The rocket nest: forty rounds, forty cells, five across and eight high.  The
# cells are 0.135 m hexagons packed 0.155 m apart (a little air between them) in
# a 0.82 m body, which is what makes the face read as a roll of tubes rather
# than as a slab with dots on it.
RACK_ROCKET = rack(5, 8, 40, bore=0.135, pitch=0.155, length=2.15, centre=0.60,
                   box=(1.90, 0.82, 1.26))
# The top-attack rack: four tubes as big as the round in them - 0.20 m across
# and 1.40 m long - a hand's width between centres, and a body a wall thick
# round the whole lattice (2 x 2 at 0.20 m needs 0.50 m, so 0.74 m is frame and
# armour).  The body is centred on the arm's own axis (`centre` 0: the shoulder
# is its middle, not its rear end), and the tubes are a fifth longer than the
# body, hanging out of *both* end faces - so the mouths read from in front and
# from behind, and a launcher on an arm you can walk round is a row of tubes,
# not a solid block.  The launcher is bolted at its own quadrant (`elev`,
# 42 deg): the tubes do not follow the arm, which is why the missile always
# leaves at that angle whatever the pilot's cross is doing - and why the drawn
# tubes have to stand at the same angle.
#
# WORKAROUND (2026-10-30): the machine's own model was never dimensioned
# against the weapons hung on it, so a launcher drawn at its real 0.20 x 1.40 m
# reads as a toy next to a 3.6 m hull, and at twice that it swamps it.  The
# tubes sit at 1.2x real - 0.24 m across and 1.70 m long - in a lattice of 2 x 4
# = eight rounds.  Nothing else about the machine is re-dimensioned; when the
# hull is, these numbers come down to a real ATGM launcher (0.20 x 1.40).
#
# The cells are packed the way the rocket nest packs its forty - its 0.155 m
# pitch on a 0.135 m bore is 1.15 tube widths, and that is what makes a face
# read as a bundle of tubes instead of a grid of dots.  Same ratio here: eight
# 0.24 m tubes on a 0.28 m pitch, two across and four high, in a body that is
# nothing but a 0.025 m sheet-metal edge round them (0.53 x 1.09 x 1.20 m).
# Not a wall-spaced lattice (a 0.60 pitch and a 0.24 frame on the doubled bore):
# that came out 1.48 m wide and sat 0.17 m *inside* the hull, and it read as
# eight dots in a big empty box.
RACK_TOP = rack(2, 4, 8, bore=0.24, pitch=0.28, length=1.70, centre=0.0, end=0.25,
                frame=0.025, elev=0.73299, bolted=True)
# The SACLOS rack: the same tubes and the same packing, four across and four
# high - sixteen rounds in one flat pack.  It is not bolted: a beam rider has
# to clear the arm on a soft launch, so the launcher sits 0.18 rad (10 deg)
# above whatever the arm is aimed at - and the arm still depresses, the tube a
# tenth of the way down with it.  The round leaves along the tube, so the same
# bias applies to both, and the guidance absorbs it the moment it is out.
RACK_SACLOS = rack(4, 4, 16, bore=0.24, pitch=0.28, length=1.70, centre=0.0,
                   end=0.25, frame=0.025, elev=0.18)


WEAPONS = [
    dict(wid=WID_NONE, key="none", name="EMPTY", kind="none", ap=0, ad=0, ed=0, er=0,
         rpm=1, mag=0, reserve=0, reload=1, vel=1, spread=0, bias=0.0, g=0, thrust=0, maxvel=0,
         life=1, range=0, cost=0, ammo_cost=0, lock=0, lock_time=0, salvo=0, semi=0,
         color=(0.8, 0.8, 0.8), cal=0, desc="No weapon fitted"),
    # ranges/muzzle velocities follow real world hardware; range is the
    # distance at which the round still carries its nominal energy, while life
    # is a hand-written estimate of 10 km of flight: 10000 m / the round's
    # speed, rounded.  Nothing is meant to expire in the air inside the map
    # one belt of 300, no reload: the magazine boundary is gone, so a pull of
    # the trigger runs the whole load downrange and the gun only goes quiet
    # when the arm is dry.  Full load is still 300 rounds, so the price of a
    # fresh weapon and its ammunition did not move
    # the round carries an airburst fuse: 4 m off an enemy rifleman it goes off
    # instead of planting itself in the dirt behind him, so a burst of cannon
    # fire sweeps infantry up without needing a direct hit on a 0.65 m sphere.
    # Only people set it off - an APC at the same distance is ignored and waits
    # for the impact fuse.
    dict(wid=WID_AC30, key="ac30", name="30mm AUTOCANNON", kind="bullet", ap=15, ad=10, ed=15, er=10.0,
         rpm=190, mag=300, reserve=0, reload=0.0, vel=1080, spread=0.0011, bias=0.0, g=9.81,
         thrust=0, maxvel=0, life=9.3, range=3200, cost=300, ammo_cost=0.5, lock=0, lock_time=0,
         salvo=0, semi=0, color=(1.0, 0.82, 0.25), cal=30, prox=4.0,
         desc="High velocity, 3.2 km effective"),
    # one belt of 1500, no reload: the gun runs dry and that is it.  1800 rpm is
    # exactly one round per tick, the fastest the 30 Hz clock can fire - twice
    # the cadence of the old 680 rpm (which quantised to one round every 3 ticks)
    # it is a rotary gun (`gatling`, a client-only flag): six barrels on a ring,
    # so the client draws it turning while this arm is firing and puts the
    # muzzle flash on whichever barrel has come up to the top of the ring
    dict(wid=WID_MG127, key="mg127", name="12.7mm MACHINE GUN", kind="bullet", ap=8, ad=10, ed=0, er=0,
         rpm=1800, mag=1500, reserve=0, reload=0.0, vel=890, spread=0.00225, bias=0.0, g=9.81,
         thrust=0, maxvel=0, life=11.2, range=1800, cost=180, ammo_cost=0.1, lock=0, lock_time=0,
         salvo=0, semi=0, color=(1.0, 0.68, 0.20), cal=12.7, gatling=True,
         desc="1.8 km suppressive fire"),
    # S-8 class: a launch charge throws the 80 mm rocket out at ~55 m/s, the
    # motor burns for a second and it leaves at 610 m/s.  A small shaped charge
    # plus a lot of frag: it is the pod's *burst*, not the single round, that
    # kills - three rockets on the hull, two on an arm
    dict(wid=WID_ROCKET, key="rocket", name="UNGUIDED ROCKET POD", kind="rocket", ap=40, ad=20, ed=35, er=10.0,
         rpm=600, mag=40, reserve=0, reload=0.0, vel=55, spread=0.003, bias=0.0429, g=9.81, thrust=560,
         maxvel=610, life=16.4, range=3200, cost=350, ammo_cost=4.0, lock=0, lock_time=0, salvo=1,
         semi=0, color=(1.0, 0.45, 0.15), cal=80, rcs=0.005, rack=RACK_ROCKET,
         desc="40 rounds, hold to fire, sighted to 1 km"),
    # TOW class: 40 m/s soft launch, then 100 m/s^2 of boost for 5.6 s takes it
    # to its 600 m/s cruise - and that is where the motor burns out, 1.8 km down
    # range.  After that it coasts on the airframe alone, and a missile is a
    # blunt thing: quadratic drag (`drag` per metre) bleeds it back down, ~385
    # m/s by the far end of the wire.  The wire itself is 4 km (`maxdist`): the
    # round cooks off where it runs out, so the reach is the wire and not the
    # motor.  Fast out, lazy in, and a long shot is a commitment rather than a
    # sniper rifle.  A tandem HEAT warhead: AD 120 is past every hull pool in
    # the game (mech/APC 100, arm 50), so one hit takes a machine apart without
    # doing the base's 4000 hp much of a favour - the price is that the operator
    # has to hold the sight on it
    #
    # Sixteen rounds in a flat 4 x 4 pack, the same tubes as the top attack and
    # the same mechanism: one round per tube, no magazine, no reload, and 0.3 s
    # between launches (`rpm` 200) - a beam rider's burst is a magazine's worth
    # of commitments fired a sight-correction apart.
    dict(wid=WID_SACLOS, key="saclos", name="SACLOS ATGM", kind="saclos", ap=800, ad=120, ed=80, er=6.0,
         rpm=200, mag=16, reserve=0, reload=0.0, vel=40, spread=0.003, bias=0.0, g=0.0, thrust=100,
         burn=5.6, drag=0.0002, maxvel=600, life=36.0, wob=30.0, wob_f=1.5, maxdist=4000.0,
         range=4000, cost=500, ammo_cost=30.0, lock=0, lock_time=0, salvo=0, semi=0,
         color=(0.35, 1.0, 0.55), cal=152, rcs=0.02, rack=RACK_SACLOS,
         desc="Beam riding, keep the sight on target"),    # Javelin class, flown as an aeroplane rather than as a curve: the tube is
    # elevated to `loft`, a 20 m/s soft launch, then 3.4 s of motor (11.2 g)
    # that burns out at 0.9 M - `flight.py` has the whole model.  From there it
    # is an unpowered glide that trades the climb for range, arriving down a
    # 35-42 deg attack line, with the last 300 m on proportional navigation.
    # `mass`/`dia`/`cd0`/`k_cl`/`cl_alpha`/`cl_max` are the airframe, not
    # tuning: a hard pull really costs the speed it costs (an induced CD of 3
    # against a profile drag of 0.75 at the 17.9 deg stop).
    #
    # The range ceiling is the battery (`life`, 30 s), not the motor: a parked
    # hull dies at 4.3 km, one running away at 3.5-4 km, and past that the
    # warhead cooks off in the air (projectiles' battery branch).  Lock range is
    # the seeker's own `range` - 4 km, which is what the shot can actually
    # reach.  Fire and forget, AD 120 takes any hull in one hit; the price is
    # the lock, the 5-27 s flight and the climb the target can hide behind -
    # measured envelope in doc/top_attack_plan.md, tool tools/top_attack_trace.py
    #
    # Eight rounds in a 2 x 4 rack and nothing behind them: no magazine, no
    # reload, 0.5 s between launches (`rpm` 120).  The rack is the rocket pod's
    # mechanism, not a second one - one round per tube, out of the tube it was
    # drawn in - and a fresh rack is bought at the base like a fresh belt.
    # `body` is how the round itself is drawn, (across, long): a 1.3 m missile
    # in a 1.4 m tube, so what leaves the launcher is the size of the mouth it
    # leaves from.  (`dia` is not that - it is the aero reference area, 127 mm,
    # and moving it would move the measured envelope.)
    dict(wid=WID_TOPATTACK, key="topattack", name="TOP-ATTACK IR ATGM", kind="top", ap=900, ad=120, ed=60,
         er=7.0, rpm=120, mag=8, reserve=0, reload=0.0, vel=20, spread=0.002, bias=0.0,
         g=9.81, thrust=110.0,
         maxvel=306.0, life=30.0, range=4000, cost=700, ammo_cost=45.0,
         mass=20.0, dia=0.127, cd0=0.75, k_cl=0.12, cl_alpha=16.0, cl_max=5.0,
         burn=3.4, ignite=0.15, loft=RACK_TOP["elev"], apex=950.0, turn_r=700.0,
         pn_n=4.0, pn_r=300.0, pn_v=120.0, rack=RACK_TOP, body=(0.18, 1.30),
         lock=1, lock_time=0.6, salvo=0, semi=0, color=(1.0, 0.35, 0.9), cal=127, rcs=0.02,
         desc="4 km lock-on, fire and forget"),
    dict(wid=WID_RIFLE556, key="rifle556", name="5.56mm RIFLE", kind="bullet", ap=5, ad=4, ed=0, er=0,
         rpm=430, mag=30, reserve=900, reload=2.2, vel=900, spread=0.007, bias=0.0, g=9.81, thrust=0,
         maxvel=0, life=11.1, range=450, cost=-1, ammo_cost=0.05, lock=0, lock_time=0, salvo=0,
         semi=0, color=(1.0, 0.9, 0.5), cal=5.56, desc="450 m rifle"),
    dict(wid=WID_RPG, key="rpg", name="RPG LAUNCHER", kind="rocket", ap=100, ad=40, ed=20, er=3.0,
         rpm=25, mag=1, reserve=900, reload=6.5, vel=50, spread=0.008, bias=0.0, g=9.81, thrust=380,
         maxvel=300, life=33.3, range=500, cost=-1, ammo_cost=8.0, lock=0, lock_time=0, salvo=0,
         semi=0, color=(1.0, 0.5, 0.2), cal=85, rcs=0.002, desc="500 m anti-tank rocket"),
    # 105 mm smoothbore: a tank gun hung on a mech arm, not an autocannon.  One
    # round in the breech, three seconds of autoloader between shots, twelve
    # rounds in the rack - so the whole arm is twelve trigger pulls a battle,
    # and the magazine is 1 on purpose (`mag` 1 / `reserve` 11 is what makes the
    # reload the cycle: a shot, then the countdown, then another).  1800 m/s of
    # APFSDS: 24 m of drop at 4 km is the fire control's problem, not the
    # pilot's, and 0.3 mrad of spread is a coin across 3 km.  AD 200 is past
    # every hull pool in the game and AP 120 past the base's 80 of plate, so it
    # is the one gun that takes a mech, an APC or a chunk of the enemy base
    # apart with a single round - which is what a $800 barrel and $600 of
    # ammunition buys.
    dict(wid=WID_G105, key="g105", name="105mm SMOOTHBORE", kind="bullet", ap=120, ad=200, ed=15, er=3.0,
         rpm=20, mag=1, reserve=11, reload=3.0, vel=1800, spread=0.0003, bias=0.0, g=9.81,
         thrust=0, maxvel=0, life=5.6, range=4000, cost=800, ammo_cost=60.0, lock=0, lock_time=0,
         salvo=0, semi=0, color=(1.0, 0.90, 0.55), cal=105, heavy=True, reach=6.45,
         desc="APFSDS, 3 s autoloader"),
    # The APC's own anti-tank missile: the same airframe, warhead and beam as
    # the mech's SACLOS, forked so the armoured car is not handed a mech's
    # launcher.  Everything is that round; the one difference is the cycle -
    # 12 rpm, one round every 5 s, because a two-tube turret reloads by hand and
    # an APC that could put sixteen beam riders into the air a sight-correction
    # apart out-trades the machine that is supposed to kill it.  The `rack` is
    # only here for `elev`: it holds the tube at the 10 deg a soft launch needs.
    #
    # Last in the list on purpose: `WEAPONS` is indexed by `wid` (`MAG_OF`, the
    # client's `defs.weapons[wid]`), so the order *is* the id space.
    dict(wid=WID_APC_ATGM, key="apc_atgm", name="APC ATGM", kind="saclos",
         ap=800, ad=120, ed=80, er=6.0,
         rpm=12, mag=16, reserve=0, reload=0.0, vel=40, spread=0.003, bias=0.0, g=0.0,
         thrust=100, burn=5.6, drag=0.0002, maxvel=600, life=36.0, wob=30.0, wob_f=1.5,
         maxdist=4000.0, range=4000, cost=-1, ammo_cost=0.0, lock=0, lock_time=0,
         salvo=0, semi=0, color=(0.35, 1.0, 0.55), cal=152, rcs=0.02, rack=RACK_SACLOS,
         desc="Beam riding, one round every five seconds"),
]


# ------------------------------------------------------- missiles as targets
# A guided or rocket round is a *thing in the air*, not just a trajectory: it
# carries a radar cross-section (`rcs`, m^2, what the RWR and an AI's radar see
# it by), a collision sphere and a small pool of health, so it can be shot
# down.  `hit_r` is the half-calibre, i.e. the round's actual frontal size - not
# a playability knob; the 30 mm's 4 m proximity fuse is the intended way to
# bring one down, and a direct hit on a 0.08 m ball is a sniper's shot.
#
# `aim_hp` 10 against the 30 mm burst (ED 15 / ER 10): a fuse at 4 m takes 8
# points off (it survives), 2.67 m kills it.  Deliberate - the fuse starts the
# burst, the fragments are what bring the round down.
INTERCEPT_KINDS = ("rocket", "saclos", "top")

for _w in WEAPONS:
    if _w["kind"] in INTERCEPT_KINDS:
        _w.setdefault("rcs", 0.0)
        _w.setdefault("aim_hp", 10.0)
        _w.setdefault("aim_dp", 1.0)
        _w.setdefault("aim_de", 1.0)
        _w["hit_r"] = _w["cal"] / 2000.0


def is_interceptable(wid: int) -> bool:
    """May this weapon's rounds be locked, fused and shot down?"""
    return WEAPONS[wid]["kind"] in INTERCEPT_KINDS


# ------------------------------------------------------------------- radar
# What a platform sees a missile by.  `plan.md` gave the half of the equation
# that answers "how bright is it at this range"; the other half is that a
# doppler receiver only sees what is *moving towards it*, so the echo is scaled
# by (closing speed / V_REF)^2 and the test is `echo >= 1`, i.e.
#
#     radar_strength * rcs / d^4 * (v_closing / RADAR_V_REF)^2 >= 1
#
# which is solved for distance as `d^2 <= K * v_closing` with
#
#     K = sqrt(radar_strength * rcs) / RADAR_V_REF
#
# so nothing on the hot path needs a square root and nothing needs a threshold
# constant.  `RADAR_V_REF` is not a physical quantity and not a tuning knob: it
# is the closing speed for which the discovery range is exactly
# (strength * rcs)^(1/4) - it only makes the speed term dimensionless, so
# changing it is the same as rescaling every `radar_strength` at once.
# 600 m/s is the game's fastest sustained round (SACLOS cruise, S-8 peak).
RADAR_V_REF = 600.0


def radar_k(strength: float, rcs: float) -> float:
    """The constant of `d^2 <= K * v_closing` for one (platform, round) pair."""
    return math.sqrt(max(strength, 0.0) * max(rcs, 0.0)) / RADAR_V_REF


def radar_range(strength: float, rcs: float) -> float:
    """Discovery distance against a round closing at RADAR_V_REF, metres."""
    return (max(strength, 0.0) * max(rcs, 0.0)) ** 0.25


# ------------------------------------------------------------------ damage
# A hit resolves in two independent halves and the target takes the sum:
#
#   penetration   the round's AP against the target's DP.  A round that beats
#                 DP outright puts its full AD through the plate; one that only
#                 beats half of it shaves AD/5 (12.7 mm against a mech: 2
#                 points); anything duller does not even scratch the paint.
#   blast         the charge's ED against the target's DE, falling off linearly
#                 to nothing at ER.  Explosives always tear *something* off, so
#                 the result never drops below a fifth of the charge that
#                 actually arrived.
def pen_damage(ap: float, ad: float, dp: float) -> float:
    if ap > dp:
        return ad
    if ap > dp * 0.5:
        return ad / 5.0
    return 0.0


def blast_damage(ed: float, de: float, er: float, dist: float) -> float:
    if ed <= 0.0:
        return 0.0
    if er > 0.0:
        e = ed * max(0.0, 1.0 - max(dist, 0.0) / er)
    else:
        e = ed if dist <= 0.0 else 0.0
    return max(e - de, e / 5.0)


def hit_damage(wd: dict, dp: float, de: float, dist: float = 0.0) -> float:
    """Damage one hit of `wd` does to armour `dp`/`de`, `dist` metres from the blast."""
    return (pen_damage(float(wd["ap"]), float(wd["ad"]), float(dp))
            + blast_damage(float(wd["ed"]), float(de), float(wd["er"]), dist))


# A wreck goes up with its own fuel and ammunition: ER 30 m at ED 10, blast
# only (a burst, not a round through a plate) - `systems/mortality.py` spawns it
# from the dying type's `death["blast"]`.  It is the one blast in the game that
# takes no sides:
# what stands within thirty metres of a mech that just came apart is in the
# wrong place whatever colour it is painted, so crews of the dead machine's own
# faction are cooked too.  ED 10 over 30 m is a wide, thin shock - a scorch on
# armour (2 points at contact) and a wound on men (9 at contact, 1 hp left), so
# it finishes what was already broken instead of being a second warhead.
# `er` doubles as the radius of the fireball the client draws, which is why the
# two hulls carry it as their `splat`.
DEATH_BLAST = {"ed": 10.0, "er": 30.0, "ad": 0.0}
# how long the dead thing's remains linger: a fallen man topples and is gone in
# five seconds, a hull keeps smoking for ten.  Each type's `death["wreck"]`
# names its own (see UNIT_DEFS) - systems/debris.py counts them down.
CORPSE_TIME = 5.0
WRECK_TIME = 10.0


# weapons a player may buy, in hotbar order
BUYABLE = [WID_AC30, WID_MG127, WID_ROCKET, WID_SACLOS, WID_TOPATTACK, WID_G105]

# what a mech leaves the lobby with: the loadout is not a menu choice - it is
# bought back at the wrench (shop) like any other piece of equipment - and it is
# what a pilot comes back with after dying: the machine he was killed in is
# gone, hardware and all
DEFAULT_LOADOUT = [WID_AC30, WID_ROCKET]

# ---- AI crews ------------------------------------------------------------
# An AI crew has no economy to shop with, so a varied arsenal is bought with a
# die roll instead: every mech hull fielded without an explicit loadout
# (`World.spawn_unit`) draws one template off this table.  Weights are shares of
# a single draw and have to add up to 1.
AI_LOADOUTS = (
    (0.30, (WID_AC30, WID_ROCKET)),        # the standard pair
    (0.20, (WID_AC30, WID_AC30)),          # twin cannon: twice the rate, no rocket
    (0.20, (WID_MG127, WID_ROCKET)),       # cheap gun, same rocket nest
    (0.10, (WID_AC30, WID_SACLOS)),        # a beam rider in the right arm
    (0.10, (WID_AC30, WID_G105)),          # 105 mm: one-round kills at 2.6 km
    (0.10, (WID_AC30, WID_TOPATTACK)),     # fire and forget, over the roof
)


def ai_loadout(rng) -> tuple[int, int]:
    """One template off `AI_LOADOUTS`, drawn with `rng.random()`.

    The draw is part of the simulation (it comes off `world.rng`), so a list of
    templates in a different order fields a different battle.
    """
    r = rng.random()
    for share, loadout in AI_LOADOUTS:
        r -= share
        if r <= 0.0:
            return loadout
    return AI_LOADOUTS[-1][1]


# seconds between two launches of a lock-on round by an AI crew.  A rack of
# eight fire-and-forget rounds at the rack's own 0.5 s cycle is four seconds of
# war and an empty arm for the rest of the battle, so the crew launches one,
# watches it arrive and picks the next moment (`systems/weapons.py`).  A beam
# rider is not on this clock: its price is holding the sight on the target, so
# it fires at the rack's own rate.
AI_LAUNCH_GAP = 15.0

# ------------------------------------------------------------------- shop
# A weapon is bought as "hardware + a full load of ammunition": the hardware
# is `cost`, the rounds are `ammo_cost` each, and both are listed so the price
# of a swap can be worked out line by line.  Selling back pays
# `SELL_REFUND` of the hardware value, scaled by how much of that arm is still
# there (a wrecked arm is worth nothing), plus half of the rounds left in it.
SELL_REFUND = 0.5             # fraction of the hardware value a trade-in pays

# fire control: guns and unguided rockets hold off for the drop so the round
# meets the sight line at the zeroed range.  The range is a fixed setting the
# pilot steps with CTRL+wheel - solving it against whatever the crosshair
# happens to sit on made the reticle jump every time a hull crossed a ridge,
# which is exactly the moment a shot has to be taken.  The elevation is capped
# so a wild setting cannot point the barrel at the sky.
ZERO_RANGE = 4000.0      # metres, the muzzle ray's length (the sight's reach)
ZERO_MAX_ELEV = 1.05     # rad, ~60 deg
ZERO_STEPS = (500.0, 1000.0, 1500.0, 2000.0, 2500.0, 3000.0, 3500.0, 4000.0)
ZERO_DEFAULT = 1000.0    # metres the sight is set to at spawn

# -------------------------------------------------------------- unit types
# `tags` is an entity's *identity*, the `etype` is its *data*.  The tags are
# component names baked into the entity when it spawns, and every system asks
# for the tag it needs instead of switching on the type:
#   unit          a hull on the battlefield (rounds stop on it, it has an hp bar)
#   combat        fights and may be fought - the only thing AI or a player may
#                 pick as a target, and the only thing that holds a town
#   lockable      an IR seeker can track it
#   moves         has locomotion, `movement.run` integrates it
#   invulnerable  rounds stop on it, damage does nothing
# Anything without `unit` (a smoke puff, a shell) can never be mistaken for a
# soldier, because no query ever returns it for one.
#
# `dp` / `de` are the two defences every hit is measured against: `dp` soaks
# penetration (see `pen_damage`), `de` soaks blast (`blast_damage`).  They are
# copied into the `armor` component at spawn, so the damage path never reads a
# definition dict.
#
# `hp` is *the* health of the entity - one number, and `hp <= 0` is what kills
# it.  A hull built out of separately damaged modules declares `arms=(left,
# right)` as well and gets a second and a third pool for them: the factory adds
# `components.ARM_COMPS`, so the archetype itself carries `arm_hp` and every
# system tests *that* rather than the unit's type.  The mech's torso has no pool
# at all: it is hit geometry that charges the hull (`MOD_TORSO`).
#
# `death` is the recipe `systems/mortality.py` follows when the hull goes:
#   blast=(ed, er)      it bursts on the spot, no sides spared (systems/blasts.py)
#   loud="blast"/"big"  which cue the client plays and how hard the screen kicks
#   wreck=(etype, life, kind)  what is left behind, and the wire `kind` byte
#   ends_match=True     killing this one decides the round
# Anything missing means "none of that".
#
# `spheres` is the hit geometry: (module, right, up, forward, radius) in the
# hull's own frame, solved against a line, not sampled - one table per type,
# cached as numpy by `mathutil.sphere_geom`.
#
# `person` is what a proximity fuse may go off on (systems/proximity.py): a man
# is a man whether he carries a rifle, an RPG or a shopping bag.  Armour is not
# tagged, so an APC 4 m off the path leaves the fuse cold.
#
# `radar_strength` (m^4) is the platform's self-defence radar; absent = none, so
# a rifleman, a base or a blockhouse never sees a missile coming and never
# shoots at one (see the radar block below).
MECH_HITSPHERES = [
    # (module, x right offset, y height, z forward offset, radius)
    (MOD_CHASSIS, 0.0, 1.45, 0.0, 1.45),
    (MOD_TORSO, 0.0, 4.15, 0.0, 1.30),
    (MOD_LEFT, -1.43, 3.55, 0.10, 0.85),
    (MOD_RIGHT, 1.43, 3.55, 0.10, 0.85),
]

UNIT_DEFS = {
    # 10 hp is a deliberate knife edge: one 12.7 mm round (AD 10) kills
    # outright, while a rifle (AD 4) needs exactly three - two leave him
    # standing.  Infantry is therefore no threat to armour at all, and is gone
    # the moment anything with a heavy machine gun looks at it.
    ETYPE_INF_RIFLE: dict(name="RIFLEMAN", hp=10, dp=1, de=1,
                          speed=5.5, radius=0.55, height=1.8,
                          weapons=(WID_RIFLE556, WID_NONE), ai_range=420, sight=600, reward=45,
                          splat=1.0, targets=(ETYPE_INF_RIFLE, ETYPE_INF_ROCKET),
                          spheres=((MOD_CHASSIS, 0.0, 0.9, 0.0, 0.65),),
                          death=dict(loud="boom", wreck=(ETYPE_CORPSE, CORPSE_TIME, 0)),
                          tags=("unit", "combat", "moves", "person")),
    ETYPE_INF_ROCKET: dict(name="AT RIFLEMAN", hp=10, dp=1, de=1,
                           speed=5.0, radius=0.55, height=1.8,
                           weapons=(WID_RPG, WID_NONE), ai_range=480, sight=650, reward=70,
                           splat=1.0,
                           spheres=((MOD_CHASSIS, 0.0, 0.9, 0.0, 0.65),),
                           death=dict(loud="boom", wreck=(ETYPE_CORPSE, CORPSE_TIME, 0)),
                           tags=("unit", "combat", "moves", "person")),
    # one hull pool: the turret is hit geometry, not a second health bar.
    # `vehicle` is what makes it steer instead of strafe (systems/movement.py)
    # and follow the lane as a path instead of aiming at its goal (systems/ai.py)
    # 100 km/h on the governor: the fastest thing on the lanes by a wide margin,
    # but it takes its time getting there (see APC_THRUST)
    ETYPE_APC: dict(name="APC", hp=100, dp=10, de=20,
                    speed=100.0 / 3.6, radius=2.2, height=2.8,
                    # self-defence radar: half the mech's discovery range, i.e.
                    # a sixteenth of its `radar_strength` (d^4), so the warning
                    # time is halved as well
                    radar_strength=3.125e12,
                    weapons=(WID_MG127, WID_APC_ATGM), ai_range=1800, sight=2600, reward=180,
                    splat=DEATH_BLAST["er"],
                    spheres=((MOD_CHASSIS, 0.0, 1.1, 0.0, 1.7),
                             (MOD_CHASSIS, 0.0, 2.4, 0.0, 1.2)),
                    death=dict(blast=(DEATH_BLAST["ed"], DEATH_BLAST["er"]), loud="blast",
                               wreck=(ETYPE_DEBRIS, WRECK_TIME, 1)),
                    tags=("unit", "combat", "lockable", "moves", "vehicle")),
    # hull pool 100 (chassis + torso), one arm 50 each; armour of a light tank
    ETYPE_MECH: dict(name="MECH", hp=100, arms=(50, 50), dp=10, de=20,
                     speed=50.0, radius=1.9, height=6.5,
                     # 398 / 713 / 1000 / 563 m against the RPG / S-8 / SACLOS /
                     # top-attack round (`defs.radar_range`); see the radar
                     # block below for why it is this number
                     radar_strength=5.0e13,
                     weapons=(WID_AC30, WID_ROCKET), ai_range=2600, sight=3400, reward=350,
                     splat=DEATH_BLAST["er"], spheres=tuple(MECH_HITSPHERES),
                     death=dict(blast=(DEATH_BLAST["ed"], DEATH_BLAST["er"]), loud="blast",
                                wreck=(ETYPE_DEBRIS, WRECK_TIME, 2)),
                     tags=("unit", "combat", "lockable", "moves")),
    # the objective: a target, but it never moves and cannot be locked.  4000 hp
    # behind 80 of plate: neither 80 mm rockets (AP 40) nor 30 mm rounds (AP 15)
    # get through, so both only do their blast - which prices the base at
    # exactly 267 rockets (15 each) or 1334 cannon rounds (3 each)
    ETYPE_BASE: dict(name="COMMAND BASE", hp=4000, dp=80, de=20,
                     speed=0.0, radius=7.0, height=12.0,
                     weapons=(WID_NONE, WID_NONE), ai_range=0, sight=0, reward=2000, splat=8.0,
                     spheres=((MOD_CHASSIS, 0.0, 5.0, 0.0, 8.5),),
                     death=dict(loud="big", ends_match=True),
                     tags=("unit", "combat")),
    ETYPE_CIV: dict(name="CIVILIAN", hp=10, dp=1, de=1,
                    speed=4.0, radius=0.5, height=1.7,
                    weapons=(WID_NONE, WID_NONE), ai_range=0, sight=60, reward=0, splat=1.0,
                    spheres=((MOD_CHASSIS, 0.0, 0.9, 0.0, 0.65),),
                    death=dict(loud="boom", wreck=(ETYPE_CORPSE, CORPSE_TIME, 0)),
                    tags=("unit", "moves", "person")),
    # inert: drawn by the client, but not a unit any more
    ETYPE_DEBRIS: dict(name="WRECK", hp=0, dp=0, de=0,
                       speed=0.0, radius=2.0, height=2.6,
                       weapons=(WID_NONE, WID_NONE), ai_range=0, sight=0, reward=0, splat=2.0,
                       spheres=((MOD_CHASSIS, 0.0, 1.0, 0.0, 1.5),),
                       death=None, tags=()),
    ETYPE_CORPSE: dict(name="CORPSE", hp=0, dp=0, de=0,
                       speed=0.0, radius=0.5, height=0.4,
                       weapons=(WID_NONE, WID_NONE), ai_range=0, sight=0, reward=0, splat=1.0,
                       spheres=((MOD_CHASSIS, 0.0, 1.0, 0.0, 1.5),),
                       death=None, tags=()),
    # concrete blockhouse: rounds stop on it, damage does not
    ETYPE_BUNKER: dict(name="BUNKER", hp=999999, dp=1000, de=1000,
                       speed=0.0, radius=6.0, height=9.0,
                       weapons=(WID_NONE, WID_NONE), ai_range=0, sight=0, reward=0, splat=6.0,
                       # stepped blockhouse: a squat mass and the deck on top
                       spheres=((MOD_CHASSIS, 0.0, 4.0, 0.0, 5.0),
                                (MOD_CHASSIS, 0.0, 8.8, 0.0, 3.6)),
                       death=None, tags=("unit", "invulnerable")),
}

# repair: welding a mech from nothing back to full armour costs
# REPAIR_FULL_COST, split evenly over its 200 points (hull 100 + both arms 50).
# Kept as a total so the bill stays sane if the hp table moves.
REPAIR_FULL_COST = 400.0
REPAIR_COST_PER_HP = REPAIR_FULL_COST / max(
    1.0, float(UNIT_DEFS[ETYPE_MECH]["hp"] + sum(UNIT_DEFS[ETYPE_MECH]["arms"])))

# ---- rearm work (seconds at the wrench) ---------------------------------
# Buying is not free in *time* either (systems/rearm.py): the hull stands where
# it is with the wrench out and its triggers dead, so a repair inside an owned
# town no longer doubles as a mid-fight reload.  Fitting a weapon and loading
# its racks are two jobs, hence a swap costs both.
REARM_SWAP_S = 2.0                 # fitting one weapon into an arm
REARM_AMMO_S = 4.0                 # loading a full load of rounds into one arm
REARM_REPAIR_PER_100HP_S = 2.0     # welding armour back on, per 100 hp

# mech geometry (metres, relative to the ground point under the entity).
# `MECH_HITSPHERES` itself is declared above `UNIT_DEFS`, because the mech's
# `spheres` table is built from it.
MECH_EYE = 5.05
# The commander's panoramic sight is a free turret standing on the roof, and it
# is *not* on the hull's own rotation axis: it is bolted `MECH_SIGHT_Z` behind
# it, which is where the model draws it and where the pilot's eye sits.  That
# offset is the whole point of the decoupling - the sight answers the view
# instantly, the hull and the arms slew to catch up (systems/gunnery.py), and
# because the two axes differ, a hull catching up underneath a sight that
# already points elsewhere walks the sight - and the eye - along an arc.
MECH_SIGHT_Z = -0.62       # m, the sight head's pivot behind the hull centre
MECH_HULL_RATE = math.radians(80.0)   # rad/s the hull swings after the sight
MECH_ARM_RATE = math.radians(50.0)    # rad/s the arms elevate after the sight
# the mechanical elevation limits of the arm mounts: the sight may look at the
# sky, the barrels cannot follow it there
MECH_PITCH_MIN = -0.45
MECH_PITCH_MAX = 0.75
# the hull rolls up to speed instead of snapping to it: 0-100 km/h in ~3 s,
# 0-180 in ~5.4 s
MECH_ACCEL = 9.3           # m/s^2, top speed 180 km/h

# ---- wheeled hulls ------------------------------------------------------
# An APC is tagged `vehicle`, and a vehicle steers instead of strafing.  It is
# a rear-axle bicycle model: the hull rolls only along its own axis and turns
# at (speed / APC_TURN_R) per second, so it can neither pivot on the spot nor
# slide sideways - the two things a hovercraft-like `vel` vector lets a hull
# do.  The turning radius is why the AI follows the lane as a *path*: pointing
# a car at its goal drives it into the first house on the way.
APC_TURN_R = 11.0          # m, the tightest curve the hull can hold
# Thrust against drag, not a fixed ramp: the engine pushes with APC_THRUST m/s^2
# and the hull sheds APC_THRUST / APC_V_INF^2 per (m/s)^2 of speed, so the pull
# is strongest off the line and fades out towards APC_V_INF.  The two numbers
# are set from the two things that matter: the governor (the `speed` in
# UNIT_DEFS, 100 km/h) and the 0-100 km/h time of 20 s, with the drag balance
# 20 % above the governor so the last km/h is actually reachable.
#   t(0 -> v) = (V / a) * artanh(v / V)   with V = APC_V_INF
APC_V_INF = 33.33          # m/s, where thrust and drag would cancel (1.2 x top)
APC_THRUST = 2.0           # m/s^2 at a standstill - 0-100 km/h in 20.0 s
APC_BRAKE = 8.0            # m/s^2, it sheds speed faster than it picks it up
APC_REVERSE = 0.4          # fraction of top speed the crew backs up at
APC_STEER_LOCK = 0.30      # rad of heading error that puts the wheels on full lock
APC_BACKOUT = 1.2          # s of reverse when the nose is against something
APC_BLOCKED = 0.5          # s of pushing against a wall before the crew gives up
# The acceptance radius of the way point tracker: a way point this close to the
# hull counts as reached and the follower aims at the next one.  It is what lets
# a car take a dense line of sharp corners at speed - it cuts the inside of every
# one of them, off the road if it likes, instead of trying to turn on the spot at
# a vertex.  It has to *grow with the speed*, or a fast hull never gets close
# enough to the line and a slow one throws away the corners it could have taken:
# `radius = clamp(speed * time, floor, cap)` is the pure-pursuit lookahead rule.
APC_ARRIVE_LOOK = 1.1      # s of travel: 10 m at 9 m/s, 30 m at 27 m/s
APC_ARRIVE_MIN = 10.0      # m, slow: follow the line closely
APC_ARRIVE_MAX = 30.0      # m, quick: 30 m of arc is all the wheels can do anyway
# How far inside a corner the hull is allowed to cut, in metres.  It is the one
# knob that trades corner speed against holding the line: a fillet of radius R
# misses the vertex by R * (1 / cos(angle / 2) - 1), so 8 m of budget puts a
# right angle at ~11 m/s and a 20 degree kink at no limit at all.
APC_CUT = 8.0              # m
APC_CORNER_HORIZON = 140.0 # m of lane the corner brake looks down
APC_LAT_ACCEL = 6.0        # m/s^2 of side load a bend is taken at - the speed
                           # a curve is driven at is sqrt(lat / its curvature)
# The shoulder joint of each arm: lateral offset, height, and the forward
# offset of the arm's front face (the round leaves from the weapon's own mouth,
# `MECH_ARM_REACH` metres further out along the barrel).
MECH_ARM_MUZZLE = {MOD_LEFT: (-1.43, 3.55, 0.9), MOD_RIGHT: (1.43, 3.55, 0.9)}
# How far the bore ends past the shoulder, per weapon kind - the same numbers
# the client builds the models from (`scene.js armModel`), so a round really
# leaves the mouth of the barrel it was drawn on.  A 4.1 m cannon and a 1.8 m
# nest differ by more than two metres; using one figure for both put the
# rockets inside their own tubes and the cannon shells well short of the brake.
# A launcher made of tubes does not need an entry: its `rack` carries the
# mouth (`rack["mouth"]`) and the round leaves from there.
MECH_ARM_REACH = {
    "bullet": 4.30,       # 30 mm: just inside the muzzle brake
    "saclos": 1.95,       # guided box: the launch tube
    "none": 1.80,
}
# ------------------------------------------------------------------ smoke
# hot smoke countermeasure: a short fan of canisters thrown out of the hull,
# each popping 50 m away into one big opaque puff.  A dozen of them are enough
# to close the whole front hemisphere, and because they are drawn opaque the
# seams do not show and no blending pass is needed.
SMOKE_RANGE = 50.0            # metres of flight before a canister pops
SMOKE_SPEED = 46.0            # m/s out of the tube (~1.1 s of flight)
SMOKE_LIFE = 5.0              # seconds the smoke stays up
SMOKE_FLAT = (45.0, 45.0)     # deployed puff: semiaxes h/v, metres (a sphere)
SMOKE_START = 0.35            # fraction of SMOKE_FLAT the burst starts at
SMOKE_BURST = 2.2             # 1/s; how fast the burst eases out
SMOKE_TIME_JITTER = 0.10      # +-fraction of the flight time, per canister
SMOKE_SIZE_JITTER = 0.10      # +-fraction of the puff size, per canister
SMOKE_CHARGES = 10            # pulls per life (a full rack, same as a reload)
SMOKE_COOLDOWN = 0.5          # seconds between pulls
SMOKE_REFILL_COST = 250       # $ for a fresh rack at the base
SMOKE_REFILL_CHARGES = 10     # pulls the refilled rack holds


def _smoke_fan() -> tuple[tuple[float, float], ...]:
    """Canister lattice covering the whole front hemisphere.

    Three rows of elevation plus one puff over the roof.  Every puff is a
    sphere of ``SMOKE_FLAT`` radius seen from 50 m, which is ~50 deg wide on
    its own, so a handful of them overlap into a solid wall.
    """
    fan = []
    for el in (12.0, 40.0, 68.0):
        n = max(1, int(round(6 * math.cos(math.radians(el)))))
        for i in range(n):
            fan.append((-90.0 + (i + 0.5) * 180.0 / n, el))
    fan.append((0.0, 90.0))
    return tuple(fan)


SMOKE_FAN = _smoke_fan()
SMOKE_COUNT = len(SMOKE_FAN)

RESPAWN_TIME = 8.0        # a mode's own rule (Mode.respawn_delay) may override it
# Battle: the wait is the walk back to the front, so it is short on the enemy's
# doorstep and long at home - interpolated on where the hull died.
RESPAWN_AT_FRONT = 3.0    # killed at the enemy command base
RESPAWN_AT_HOME = 40.0    # killed at your own command base
BUILD_TIME = 5.0

# optics / IR seeker
BASE_FOV = 1.05            # vertical field of view at 1x, radians
LOCK_FRAC = 0.05           # lock circle diameter as a fraction of the view width
AIM_ROOF = 0.92            # where a seeker looks at a hull: 92% of its height
# A missile's own seeker does not need its target to survive the flight.  With
# the lock gone it stares at where the hull should be, and takes the first
# enemy hull it can see inside this cone around that stare line (`seeker.py`).
# 3 deg is about the angular width of the round's own dispersion.
SEEKER_CONE = math.radians(3.0)

# ---------------------------------------------------------------- acoustics
# Whether a bang is worth putting on the wire is decided here, not by the
# client: a 30 mm round going off 7 km away is a packet nobody hears.  What the
# ear gets is `sound_gain`, and `frontend/src/audio.js` mirrors these numbers
# line for line (`attenuation`, `SOUND_LEVEL`) - the client plays at exactly the
# level the server tested.
#
# Spreading is inverse square *compressed*: over a 16 km map a true 1/r is 84 dB
# of dynamic range, which is silence.  ROLLOFF 0.6 keeps the shape of the near
# field (a gun at 100 m really is quieter than one at 20 m) and still leaves a
# hull 8 km out audible.  A muzzle is not a point source, so nothing falls off
# inside REF_DIST.
SOUND_REF_DIST = 25.0      # m, where a source is at its full level
SOUND_ROLLOFF = 0.60       # 1.0 = inverse square, 0 = no falloff at all
SOUND_FLOOR = 0.02         # arriving gain under this is not sent at all
SOUND_SPEED = 343.0        # m/s in air at 20 C (mirrors audio.js)
# How long an event may stay in the air.  The loudest cue a pilot can hear is a
# hull going up, good to ~17 km - which takes 50 s to arrive, so a shorter TTL
# would be the real cutoff and the level test above would be dead letters.
SOUND_TTL = 50.0           # s (mirrors game.js)
# how loud each cue is at SOUND_REF_DIST (mirrors audio.js SOUND_LEVEL)
SOUND_LEVEL = {"shot": 0.60, "boom": 0.70, "big": 0.80, "blast": 1.00}


def sound_gain(dist: float, level: float = 1.0) -> float:
    """Gain of a `level` loud source `dist` metres from the ear."""
    return level * (SOUND_REF_DIST / max(SOUND_REF_DIST, float(dist))) ** SOUND_ROLLOFF
