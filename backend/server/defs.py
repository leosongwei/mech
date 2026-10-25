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

# mech module indices (health array slots)
MOD_CHASSIS = 0
MOD_TORSO = 1
MOD_LEFT = 2
MOD_RIGHT = 3
MODULE_NAMES = ["CHASSIS", "TORSO", "L.ARM", "R.ARM"]

# ----------------------------------------------------------------- weapons
WID_NONE = 0
WID_AC30 = 1
WID_MG127 = 2
WID_ROCKET = 3
WID_SACLOS = 4
WID_TOPATTACK = 5
WID_RIFLE556 = 6
WID_RPG = 7

# kind: bullet (ballistic), rocket (unguided thrust), saclos (beam riding),
#       top (infrared lock-on, top attack)
# cal: bore in mm, only used to size the tracer the browser draws
WEAPONS = [
    dict(wid=WID_NONE, key="none", name="EMPTY", kind="none", dmg=0, rpm=1, mag=0, reserve=0,
         reload=1, vel=1, spread=0, bias=0.0, g=0, thrust=0, maxvel=0, life=1, splash=0,
         splash_dmg=0, range=0, cost=0, ammo_cost=0, lock=0, lock_time=0, salvo=0, semi=0,
         color=(0.8, 0.8, 0.8), cal=0, desc="No weapon fitted"),
    # ranges/muzzle velocities follow real world hardware; range is the
    # distance at which the round still carries its nominal energy, while life
    # is a hand-written estimate of 10 km of flight: 10000 m / the round's
    # speed, rounded.  Nothing is meant to expire in the air inside the map
    dict(wid=WID_AC30, key="ac30", name="30mm AUTOCANNON", kind="bullet", dmg=44, rpm=190, mag=50,
         reserve=250, reload=2.8, vel=1080, spread=0.0011, bias=0.0, g=9.81, thrust=0, maxvel=0,
         life=9.3, splash=3.0, splash_dmg=16, range=3200, cost=300, ammo_cost=0.5, lock=0, lock_time=0, salvo=0,
         semi=0, color=(1.0, 0.82, 0.25), cal=30, desc="High velocity, 3.2 km effective"),
    # one belt of 1500, no reload: the gun runs dry and that is it.  1800 rpm is
    # exactly one round per tick, the fastest the 30 Hz clock can fire - twice
    # the cadence of the old 680 rpm (which quantised to one round every 3 ticks)
    dict(wid=WID_MG127, key="mg127", name="12.7mm MACHINE GUN", kind="bullet", dmg=15, rpm=1800, mag=1500,
         reserve=0, reload=0.0, vel=890, spread=0.00225, bias=0.0, g=9.81, thrust=0, maxvel=0,
         life=11.2, splash=0, splash_dmg=0, range=1800, cost=180, ammo_cost=0.1, lock=0, lock_time=0, salvo=0,
         semi=0, color=(1.0, 0.68, 0.20), cal=12.7, desc="1.8 km suppressive fire"),
    # S-8 class: a launch charge throws the 80 mm rocket out at ~55 m/s, the
    # motor burns for a second and it leaves at 610 m/s
    dict(wid=WID_ROCKET, key="rocket", name="UNGUIDED ROCKET POD", kind="rocket", dmg=155, rpm=600, mag=40,
         reserve=0, reload=0.0, vel=55, spread=0.003, bias=0.0429, g=9.81, thrust=560, maxvel=610,
         life=16.4, splash=8.0, splash_dmg=95, range=3200, cost=350, ammo_cost=4.0, lock=0, lock_time=0, salvo=1,
         semi=0, color=(1.0, 0.45, 0.15), cal=80,
         desc="40 rounds, hold to fire, sighted to 1 km"),
    # TOW class: 65 m/s out of the tube, the flight motor takes it to 278 m/s
    # about 2 s later, 3.75 km of wire
    dict(wid=WID_SACLOS, key="saclos", name="SACLOS ATGM", kind="saclos", dmg=430, rpm=40, mag=2, reserve=8,
         reload=4.2, vel=65, spread=0.003, bias=0.0, g=0.0, thrust=110, maxvel=278, life=36.0,
         wob=30.0, wob_f=1.5,
         splash=6.0, splash_dmg=150, range=3750, cost=500, ammo_cost=30.0, lock=0, lock_time=0, salvo=0, semi=0,
         color=(0.35, 1.0, 0.55), cal=152, desc="Beam riding, keep the sight on target"),
    # Javelin class: soft launch at 20 m/s, the motor lights off outside the
    # tube and cruises at 190 m/s, climbing 150 m above the target before the
    # dive; the seeker locks before the shot and needs the target in the circle
    dict(wid=WID_TOPATTACK, key="topattack", name="TOP-ATTACK IR ATGM", kind="top", dmg=520, rpm=32, mag=2,
         reserve=8, reload=4.6, vel=20, spread=0.002, bias=0.0, g=0.0, thrust=170, maxvel=190,
         life=52.6, climb=150.0, cruise=0.35, dive=220.0,
         splash=7.0, splash_dmg=190, range=2500, cost=700, ammo_cost=45.0, lock=1, lock_time=1.1, salvo=0,
         semi=0, color=(1.0, 0.35, 0.9), cal=127, desc="2.5 km lock-on, fire and forget"),
    dict(wid=WID_RIFLE556, key="rifle556", name="5.56mm RIFLE", kind="bullet", dmg=7, rpm=430, mag=30,
         reserve=900, reload=2.2, vel=900, spread=0.007, bias=0.0, g=9.81, thrust=0, maxvel=0,
         life=11.1, splash=0, splash_dmg=0, range=450, cost=-1, ammo_cost=0.05, lock=0, lock_time=0, salvo=0,
         semi=0, color=(1.0, 0.9, 0.5), cal=5.56, desc="450 m rifle"),
    dict(wid=WID_RPG, key="rpg", name="RPG LAUNCHER", kind="rocket", dmg=135, rpm=25, mag=1, reserve=900,
         reload=6.5, vel=50, spread=0.008, bias=0.0, g=9.81, thrust=380, maxvel=300, life=33.3,
         splash=6.0, splash_dmg=70, range=500, cost=-1, ammo_cost=8.0, lock=0, lock_time=0, salvo=0, semi=0,
         color=(1.0, 0.5, 0.2), cal=85, desc="500 m anti-tank rocket"),
]

# weapons a player may buy, in hotbar order
BUYABLE = [WID_AC30, WID_MG127, WID_ROCKET, WID_SACLOS, WID_TOPATTACK]

# ------------------------------------------------------------------- shop
# A weapon is bought as "hardware + a full load of ammunition": the hardware
# is `cost`, the rounds are `ammo_cost` each, and both are listed so the price
# of a swap can be worked out line by line.  Selling back pays
# `SELL_REFUND` of the hardware value, scaled by how much of that arm is still
# there (a wrecked arm is worth nothing), plus half of the rounds left in it.
SELL_REFUND = 0.5             # fraction of the hardware value a trade-in pays

# fire control: guns and unguided rockets hold off for the drop so the round
# lands on whatever the muzzle points at.  Every ballistic weapon zeroes out
# to the same 4 km, and the elevation is capped so a wild solution cannot
# point the barrel at the sky
ZERO_RANGE = 4000.0      # metres, the muzzle ray's length
ZERO_MAX_ELEV = 1.05     # rad, ~60 deg

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
UNIT_DEFS = {
    # 15 hp is a deliberate knife edge: one 12.7 mm round (15) kills outright,
    # while a rifle (7) needs exactly three - two leave him standing.  Infantry
    # is therefore no threat to armour at all, and is gone the moment anything
    # with a heavy machine gun looks at it.
    ETYPE_INF_RIFLE: dict(name="RIFLEMAN", hp=(15, 0, 0, 0), speed=5.5, radius=0.55, height=1.8,
                          weapons=(WID_RIFLE556, WID_NONE), ai_range=420, sight=600, reward=45,
                          splat=1.0, targets=(ETYPE_INF_RIFLE, ETYPE_INF_ROCKET),
                          tags=("unit", "combat", "moves")),
    ETYPE_INF_ROCKET: dict(name="AT RIFLEMAN", hp=(15, 0, 0, 0), speed=5.0, radius=0.55, height=1.8,
                           weapons=(WID_RPG, WID_NONE), ai_range=480, sight=650, reward=70,
                           splat=1.0, tags=("unit", "combat", "moves")),
    ETYPE_APC: dict(name="APC", hp=(420, 140, 0, 0), speed=15.0, radius=2.2, height=2.8,
                    weapons=(WID_MG127, WID_SACLOS), ai_range=1800, sight=2600, reward=180,
                    splat=3.0, tags=("unit", "combat", "lockable", "moves")),
    ETYPE_MECH: dict(name="MECH", hp=(700, 900, 450, 450), speed=50.0, radius=1.9, height=6.5,
                     weapons=(WID_AC30, WID_ROCKET), ai_range=2600, sight=3400, reward=350,
                     splat=4.0, tags=("unit", "combat", "lockable", "moves")),
    # the objective: a target, but it never moves and cannot be locked
    ETYPE_BASE: dict(name="COMMAND BASE", hp=(6500, 0, 0, 0), speed=0.0, radius=7.0, height=12.0,
                     weapons=(WID_NONE, WID_NONE), ai_range=0, sight=0, reward=2000, splat=8.0,
                     tags=("unit", "combat")),
    ETYPE_CIV: dict(name="CIVILIAN", hp=(60, 0, 0, 0), speed=4.0, radius=0.5, height=1.7,
                    weapons=(WID_NONE, WID_NONE), ai_range=0, sight=60, reward=0, splat=1.0,
                    tags=("unit", "moves")),
    # inert: drawn by the client, but not a unit any more
    ETYPE_DEBRIS: dict(name="WRECK", hp=(0, 0, 0, 0), speed=0.0, radius=2.0, height=2.6,
                       weapons=(WID_NONE, WID_NONE), ai_range=0, sight=0, reward=0, splat=2.0,
                       tags=()),
    ETYPE_CORPSE: dict(name="CORPSE", hp=(0, 0, 0, 0), speed=0.0, radius=0.5, height=0.4,
                       weapons=(WID_NONE, WID_NONE), ai_range=0, sight=0, reward=0, splat=1.0,
                       tags=()),
    # concrete blockhouse: rounds stop on it, damage does not
    ETYPE_BUNKER: dict(name="BUNKER", hp=(999999, 0, 0, 0), speed=0.0, radius=6.0, height=9.0,
                       weapons=(WID_NONE, WID_NONE), ai_range=0, sight=0, reward=0, splat=6.0,
                       tags=("unit", "invulnerable")),
}

# repair: welding a mech from nothing back to full armour costs
# REPAIR_FULL_COST, split evenly over its 2500 points (chassis 700 + torso 900 +
# both arms 450).  Kept as a total so the bill stays sane if the hp table moves.
REPAIR_FULL_COST = 400.0
REPAIR_COST_PER_HP = REPAIR_FULL_COST / max(1.0, float(sum(UNIT_DEFS[ETYPE_MECH]["hp"])))

# mech geometry (metres, relative to the ground point under the entity)
MECH_HITSPHERES = [
    # (module, x right offset, y height, z forward offset, radius)
    (MOD_CHASSIS, 0.0, 1.45, 0.0, 1.45),
    (MOD_TORSO, 0.0, 4.15, 0.0, 1.30),
    (MOD_LEFT, -1.95, 4.30, 0.10, 0.85),
    (MOD_RIGHT, 1.95, 4.30, 0.10, 0.85),
]
MECH_EYE = 5.05
# the hull rolls up to speed instead of snapping to it: 0-100 km/h in ~3 s,
# 0-180 in ~5.4 s
MECH_ACCEL = 9.3           # m/s^2, top speed 180 km/h
MECH_ARM_MUZZLE = {MOD_LEFT: (-1.95, 4.30, 0.9), MOD_RIGHT: (1.95, 4.30, 0.9)}

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
SMOKE_CHARGES = 10            # pulls per life (a full rack, same as a reload)
SMOKE_COOLDOWN = 18.0         # seconds between pulls
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

RESPAWN_TIME = 8.0
BUILD_TIME = 5.0
CORPSE_TIME = 5.0          # fallen infantry lingers this long
WRECK_TIME = 10.0          # burning hulls linger this long

# optics / IR seeker
BASE_FOV = 1.05            # vertical field of view at 1x, radians
LOCK_FRAC = 0.05           # lock circle diameter as a fraction of the view width
