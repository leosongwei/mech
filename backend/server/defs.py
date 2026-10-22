"""Static game data: factions, entity types, unit statistics and weapons.

Everything here is also sent to the browser so client prediction uses exactly
the same ballistics numbers as the server.
"""

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
ETYPE_DEBRIS = 7          # inert wreck, purely visual

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
WEAPONS = [
    dict(wid=WID_NONE, key="none", name="EMPTY", kind="none", dmg=0, rpm=1, mag=0, reserve=0,
         reload=1, vel=1, spread=0, bias=0.0, g=0, thrust=0, maxvel=0, life=1, splash=0,
         splash_dmg=0, range=0, cost=0, lock=0, lock_time=0, salvo=0, semi=0,
         color=(0.8, 0.8, 0.8), desc="No weapon fitted"),
    # ranges/muzzle velocities follow real world hardware; range is the
    # distance at which the round still carries its nominal energy
    dict(wid=WID_AC30, key="ac30", name="30mm AUTOCANNON", kind="bullet", dmg=44, rpm=190, mag=50,
         reserve=250, reload=2.8, vel=1080, spread=0.0022, bias=0.0, g=9.81, thrust=0, maxvel=0,
         life=4.0, splash=3.0, splash_dmg=16, range=3200, cost=300, lock=0, lock_time=0, salvo=0,
         semi=0, color=(1.0, 0.82, 0.25), desc="High velocity, 3.2 km effective"),
    dict(wid=WID_MG127, key="mg127", name="12.7mm MACHINE GUN", kind="bullet", dmg=15, rpm=680, mag=120,
         reserve=720, reload=3.4, vel=890, spread=0.0045, bias=0.0, g=9.81, thrust=0, maxvel=0,
         life=3.0, splash=0, splash_dmg=0, range=1800, cost=180, lock=0, lock_time=0, salvo=0,
         semi=0, color=(1.0, 0.68, 0.20), desc="1.8 km suppressive fire"),
    dict(wid=WID_ROCKET, key="rocket", name="UNGUIDED ROCKET POD", kind="rocket", dmg=155, rpm=90, mag=20,
         reserve=0, reload=0.0, vel=80, spread=0.0025, bias=0.0431, g=9.81, thrust=420, maxvel=520,
         life=9.0, splash=8.0, splash_dmg=95, range=4000, cost=350, lock=0, lock_time=0, salvo=1,
         semi=1, color=(1.0, 0.45, 0.15),
         desc="Single shot, sighted to 1 km, drops past that"),
    dict(wid=WID_SACLOS, key="saclos", name="SACLOS ATGM", kind="saclos", dmg=430, rpm=40, mag=2, reserve=8,
         reload=4.2, vel=70, spread=0.003, bias=0.0, g=0.0, thrust=190, maxvel=330, life=18.0,
         splash=6.0, splash_dmg=150, range=3750, cost=500, lock=0, lock_time=0, salvo=0, semi=0,
         color=(0.35, 1.0, 0.55), desc="Beam riding, keep the sight on target"),
    dict(wid=WID_TOPATTACK, key="topattack", name="TOP-ATTACK IR ATGM", kind="top", dmg=520, rpm=32, mag=2,
         reserve=8, reload=4.6, vel=55, spread=0.002, bias=0.0, g=0.0, thrust=170, maxvel=300,
         life=22.0, splash=7.0, splash_dmg=190, range=4750, cost=700, lock=1, lock_time=1.1, salvo=0,
         semi=0, color=(1.0, 0.35, 0.9), desc="4.75 km lock-on, fire and forget"),
    dict(wid=WID_RIFLE556, key="rifle556", name="5.56mm RIFLE", kind="bullet", dmg=7, rpm=430, mag=30,
         reserve=900, reload=2.2, vel=900, spread=0.014, bias=0.0, g=9.81, thrust=0, maxvel=0,
         life=2.2, splash=0, splash_dmg=0, range=450, cost=-1, lock=0, lock_time=0, salvo=0,
         semi=0, color=(1.0, 0.9, 0.5), desc="450 m rifle"),
    dict(wid=WID_RPG, key="rpg", name="RPG LAUNCHER", kind="rocket", dmg=135, rpm=25, mag=1, reserve=900,
         reload=6.5, vel=50, spread=0.016, bias=0.0, g=9.81, thrust=380, maxvel=300, life=6.0,
         splash=6.0, splash_dmg=70, range=500, cost=-1, lock=0, lock_time=0, salvo=0, semi=0,
         color=(1.0, 0.5, 0.2), desc="500 m anti-tank rocket"),
]

# weapons a player may buy, in hotbar order
BUYABLE = [WID_AC30, WID_MG127, WID_ROCKET, WID_SACLOS, WID_TOPATTACK]

# -------------------------------------------------------------- unit types
UNIT_DEFS = {
    ETYPE_INF_RIFLE: dict(name="RIFLEMAN", hp=(80, 0, 0, 0), speed=5.5, radius=0.55, height=1.8,
                          weapons=(WID_RIFLE556, WID_NONE), ai_range=420, sight=600, reward=45,
                          splat=1.0),
    ETYPE_INF_ROCKET: dict(name="AT RIFLEMAN", hp=(80, 0, 0, 0), speed=5.0, radius=0.55, height=1.8,
                           weapons=(WID_RPG, WID_NONE), ai_range=480, sight=650, reward=70,
                           splat=1.0),
    ETYPE_APC: dict(name="APC", hp=(420, 140, 0, 0), speed=15.0, radius=2.2, height=2.8,
                    weapons=(WID_MG127, WID_SACLOS), ai_range=1800, sight=2600, reward=180,
                    splat=3.0),
    ETYPE_MECH: dict(name="MECH", hp=(700, 900, 450, 450), speed=16.0, radius=1.9, height=6.5,
                     weapons=(WID_AC30, WID_ROCKET), ai_range=2600, sight=3400, reward=350,
                     splat=4.0),
    ETYPE_BASE: dict(name="COMMAND BASE", hp=(6500, 0, 0, 0), speed=0.0, radius=7.0, height=12.0,
                     weapons=(WID_NONE, WID_NONE), ai_range=0, sight=0, reward=2000, splat=8.0),
    ETYPE_CIV: dict(name="CIVILIAN", hp=(60, 0, 0, 0), speed=4.0, radius=0.5, height=1.7,
                    weapons=(WID_NONE, WID_NONE), ai_range=0, sight=60, reward=0, splat=1.0),
    ETYPE_DEBRIS: dict(name="WRECK", hp=(10 ** 6, 0, 0, 0), speed=0.0, radius=2.0, height=3.0,
                       weapons=(WID_NONE, WID_NONE), ai_range=0, sight=0, reward=0, splat=2.0),
}

# mech geometry (metres, relative to the ground point under the entity)
MECH_HITSPHERES = [
    # (module, x right offset, y height, z forward offset, radius)
    (MOD_CHASSIS, 0.0, 1.45, 0.0, 1.45),
    (MOD_TORSO, 0.0, 4.15, 0.0, 1.30),
    (MOD_LEFT, -1.95, 4.30, 0.10, 0.85),
    (MOD_RIGHT, 1.95, 4.30, 0.10, 0.85),
]
MECH_EYE = 5.05
MECH_ARM_MUZZLE = {MOD_LEFT: (-1.95, 4.30, 0.9), MOD_RIGHT: (1.95, 4.30, 0.9)}

RESPAWN_TIME = 8.0
BUILD_TIME = 5.0
