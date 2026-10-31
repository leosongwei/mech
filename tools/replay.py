"""Scripted battle replay: a sanity harness, not a golden test.

The world is deterministic (fixed seed, fixed 30 Hz step, one entry point
``World.update``), so a scripted session can be re-run after a refactor and the
numbers compared.  It is deliberately tolerant: a rewrite of the ECS is allowed
to move a value in the last few digits, it is not allowed to stop spawning
puffs, stop killing units or empty the sky.

Per tick it records a handful of aggregates - entity counts per kind, total hp,
the sum of every position and velocity, the pilot's money / kills / deaths.
Enough to notice that a subsystem went quiet, cheap enough to print.

    python tools/replay.py               # run and compare with the fixture
    python tools/replay.py --save        # rewrite tools/fixtures/replay_battle.json
    python tools/replay.py -v            # print every tick

Determinism rests on three things, so do not break them: ``world.rng`` is
seeded (``random.Random(2024)``), entity ids are handed out ascending and
reused LIFO, and ``ecs.query`` returns ids in ascending order.
"""

from __future__ import annotations

import argparse
import json
import math
import pathlib
import sys

import numpy as np

ROOT = pathlib.Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT / "backend"))

from server import combat, defs  # noqa: E402
from server.world import World  # noqa: E402

FIXTURE = ROOT / "tools" / "fixtures" / "replay_battle.json"
TICKS = 900                       # 30 s of battle at 30 Hz
DT = 1.0 / 30.0

# series compared as floats, with a tolerance.  `vel_*` is the sum of every
# entity's real velocity (`vel`, not `vel_cmd`): the aggregate of what the whole
# field is actually doing, which is what a refactor of the velocity components
# moves first
FLOAT_SERIES = ("hp_total", "pos_x", "pos_z", "vel_x", "vel_z")
# series compared as counts, at most one off
COUNT_SERIES = ("n_units", "n_combat", "n_proj", "n_smoke", "n_canister",
                "n_debris", "n_all", "n_raider", "wave", "kills", "deaths")


# ----------------------------------------------------------------------
# the scripted session
# ----------------------------------------------------------------------
def drive(world: World, pl, tick: int) -> None:
    """One tick of pilot input.  Pure function of ``tick`` - no wall clock, no
    random, so the same call sequence always produces the same battle."""
    pl.in_yaw = 0.4 + 0.02 * tick
    pl.in_pitch = -0.06
    pl.in_move = (1.0, 0.0) if (tick // 60) % 2 == 0 else (0.0, 1.0)
    pl.zoom = 4.0
    pl.zero_range = 800.0
    pl.fire = (20 <= tick < 400, 90 <= tick < 260)
    if tick in (30, 200, 420, 700):
        pl.smoke_q = True                     # canisters -> pop -> fade

    px = float(world.ecs["pos"][pl.mech, 0])
    pz = float(world.ecs["pos"][pl.mech, 2])
    if tick == 40:                            # something to shoot at
        world.spawn_unit(3, 1, px + 500.0, pz + 120.0)
        world.spawn_unit(1, 1, px + 260.0, pz + 60.0)
        # a hostile man 300 m in front of that carrier: the AI then has to
        # *stop* it, which is the one path where a wheeled hull brakes instead
        # of following its lane (and where a row/id mix-up in the AI loop
        # reaches into the wrong entity, so it is worth covering)
        world.spawn_unit(1, 0, px + 790.0, pz + 160.0)
    if tick % 45 == 20:                       # steady churn of targets
        world.spawn_unit(3 if (tick // 45) % 3 == 0 else 1, 1,
                         px + 320.0 + tick % 90, pz - 140.0)
    if tick % 100 == 60:                      # force a kill: corpse / wreck path
        for eid in world.combat_units():
            if int(world.ecs["faction"][eid]) != int(pl.faction):
                combat.apply(world, int(eid), defs.MOD_CHASSIS, 9999.0, -1)
                break


# ----------------------------------------------------------------------
# per-tick summary
# ----------------------------------------------------------------------
def summary(world: World, pl) -> dict[str, float]:
    e = world.ecs
    ids = e.query("pos")                      # everything that has a position
    pos = e["pos"][ids].astype(np.float64) if len(ids) else np.zeros((0, 3))
    vel = e["vel"][ids].astype(np.float64) if len(ids) else np.zeros((0, 3))
    hp = float(e["hp"][e.query("hp")].astype(np.float64).sum())
    armed = e.query("arm_hp")
    if armed.size:
        hp += float(e["arm_hp"][armed].astype(np.float64).sum())
    return dict(
        n_all=int(ids.size),
        n_units=int(e.query("unit").size),
        n_combat=int(e.query("combat").size),
        n_proj=int(e.query("proj_kind").size),
        n_smoke=int(e.query("smoke").size),
        n_canister=int(e.query("canister").size),
        n_debris=int(e.query("wreck_t").size),
        n_raider=int(e.query("raider").size),
        hp_total=hp,
        pos_x=float(pos[:, 0].sum()), pos_z=float(pos[:, 2].sum()),
        vel_x=float(vel[:, 0].sum()), vel_z=float(vel[:, 2].sum()),
        wave=int(world.wave), kills=int(pl.kills), deaths=int(pl.deaths),
    )


def _cache_after_growth() -> None:
    """Regression: a cached buffer list must not survive an archetype growing.

    Spawns past the first size swap in a longer array; a `View` that cached the
    short one writes out of bounds (or silently misses) from the next tick on.
    """
    from server.ecs import ECS
    e = ECS()
    e.register("pos", "<f4", (3,))
    view = e["pos"]
    first = e.create(("pos",))
    view[first, 0] = 1.0
    many = [e.create(("pos",)) for _ in range(400)]      # forces several resizes
    for k, eid in enumerate(many):
        view[eid, 0] = float(k)
    assert view[first, 0] == 1.0, "the pre-growth read was lost"
    assert view[many[-1], 0] == 399.0, "the post-growth write went nowhere"
    e.validate()


def _death_blast() -> None:
    """Regression: a destroyed hull bursts for ER 30 m / ED 10 (`systems/mortality.py` -> `systems/blasts.py`).

    The blast takes no sides, so the hull parked beside the dead machine - its
    own faction - has to take damage off it; a man four metres away and another
    twenty metres out both have to be hit by the shock (wounded, not killed:
    ED 10 tops out at 9 and a man has 10); a hull sixty metres out has to be
    untouched, and the burst must leave its own hulk behind rather than eat it.
    """
    from server import defs
    world = World(seed=3, mode="range")
    e = world.ecs
    dead = world.spawn_unit(defs.ETYPE_MECH, 1, 600.0, 600.0)
    beside = world.spawn_unit(defs.ETYPE_APC, 1, 605.0, 600.0)
    far = world.spawn_unit(defs.ETYPE_APC, 1, 660.0, 600.0)
    man = world.spawn_unit(defs.ETYPE_INF_RIFLE, 0, 600.0, 604.0)
    man_far = world.spawn_unit(defs.ETYPE_INF_RIFLE, 0, 620.0, 600.0)
    combat.apply(world, dead, defs.MOD_CHASSIS, 9999.0)
    # one tick for `mortality` to judge the kill and leave the burst behind it,
    # one more for `blasts` to go off with it: a chain reaction is a tick apart
    # by construction, which is the whole reason explosions are entities
    world.update(DT)
    world.update(DT)
    # the dead ids come back as the wrecks (LIFO reuse), so "dead" means "no
    # longer a unit", not "no longer alive"
    assert not e.has(dead, "unit"), "the mech did not die"
    assert e.has(beside, "unit") and float(e["hp"][beside]) < 100.0, \
        "the blast did not touch the hull beside it"
    assert e.has(man, "unit") and float(e["hp"][man]) < 10.0, \
        "the blast did not reach the man four metres away"
    assert e.has(man_far, "unit") and float(e["hp"][man_far]) < 10.0, \
        "the blast did not reach a man twenty metres out"
    assert float(e["hp"][far]) == 100.0, "the blast reached a hull 60 m away"
    assert len(e.query("wreck_t")) == 1, "the burning hulk did not leave a wreck"
    e.validate()


def _damage_model() -> None:
    """Regression: one hp pool per hull, and arm pools only where arms exist.

    `hp` is the health and `hp <= 0` is the death for *every* unit; the mech is
    the only hull that carries `arm_hp`, and the three rules that make an arm
    worth shooting are: a hit on it stays off the hull, losing it costs its
    ammunition, and a round into an arm that is already gone goes inwards.
    """
    world = World(seed=3, mode="range")
    e = world.ecs
    mech = world.spawn_unit(defs.ETYPE_MECH, 1, 600.0, 600.0)
    assert float(e["hp"][mech]) == 100.0 and float(e["hpmax"][mech]) == 100.0
    assert e["arm_hp"][mech].tolist() == [50.0, 50.0]
    assert e["armor"][mech].tolist() == [10.0, 20.0]

    combat.apply(world, mech, defs.MOD_LEFT, 20.0, 1)
    assert float(e["hp"][mech]) == 100.0, "an arm hit reached the hull"
    assert float(e["arm_hp"][mech, 0]) == 30.0
    combat.apply(world, mech, defs.MOD_LEFT, 100.0, 2)          # the arm comes off
    assert float(e["arm_hp"][mech, 0]) == 0.0
    assert int(e["mag"][mech, 0]) == 0 and int(e["ammo"][mech, 0]) == 0, \
        "the lost arm kept its rounds"
    assert not combat.arm_alive(world, mech, 0), "a lost arm can still fire"
    assert combat.arm_alive(world, mech, 1)
    assert float(e["hp"][mech]) == 100.0, "losing an arm is not losing the hull"
    combat.apply(world, mech, defs.MOD_LEFT, 30.0, 3)           # arm gone -> hull
    assert float(e["hp"][mech]) == 70.0, "a round into a lost arm skipped the hull"
    combat.apply(world, mech, defs.MOD_TORSO, 10.0, 4)          # torso is hit geometry
    assert float(e["hp"][mech]) == 60.0, "the torso did not charge the hull"
    assert int(e["last_hit"][mech]) == 4

    # a single-pool hull has no arms, and its hits land on the one pool
    apc = world.spawn_unit(defs.ETYPE_APC, 0, 700.0, 700.0)
    assert not e.has(apc, "arm_hp"), "an APC grew arm pools"
    wd = defs.WEAPONS[defs.WID_AC30]
    before = float(e["hp"][apc])
    combat.hit(world, apc, defs.MOD_CHASSIS, wd, 0.0, -1)
    assert abs((before - float(e["hp"][apc])) - defs.hit_damage(wd, 10.0, 20.0, 0.0)) < 1e-4, \
        "a direct hit did not take exactly one hit's worth"
    assert float(e["hp"][apc]) > 0.0

    # concrete: rounds stop on it, the hp never moves
    bunker = world.spawn_unit(defs.ETYPE_BUNKER, 0, 900.0, 900.0)
    combat.apply(world, bunker, defs.MOD_CHASSIS, 1e6)
    assert float(e["hp"][bunker]) == 999999.0, "the bunker took damage"
    e.validate()


def _raid_plan() -> None:
    """Regression: a raider is briefed, and its route goes where it can drive.

    A raider plans off the map and off the enemy's positions (`modes/battle/
    raids.py`), and the two things that can silently break are the plan (an empty
    route, or a march in the wrong direction) and the two-level split (a street
    leg that starts at the wrong end, so the way points run backwards into the
    town).  The route has to start under the raider, end at the town it was sent
    to, and no way point may be inside a building.
    """
    import math
    from server.modes.battle import raids
    from server.terrain import TILE_BUILDING
    world = World(seed=11, mode="battle")
    world.raid_timer = {0: 0.0, 1: 1e9}       # one side only: the route is checked
    for _ in range(60):
        world.update(DT)
    ids = world.raiders()
    assert ids.size == raids.RAID_SIZE, f"{ids.size} raiders, want {raids.RAID_SIZE}"
    world.ecs.validate()
    for eid in ids:
        eid = int(eid)
        k = raids.key(world, eid)
        path = world.raid_paths.get(k) or []
        assert len(path) >= 2, f"raider {eid} was given no route"
        p = world.ecs["pos"][eid]
        assert math.dist(path[0], (float(p[0]), float(p[2]))) < 200.0, \
            "the route does not start under the raider"
        goal = int(world.ecs["raid_goal"][eid])
        if goal != raids.NO_TOWN:
            town = world.towns[goal]
            assert math.dist(path[-1], (town["x"], town["z"])) < 1.0, \
                "the route does not end at the town"
            assert int(town["owner"]) == 1 - int(world.ecs["faction"][eid]), \
                "the raider was sent to a town that is not the enemy's"
            assert world.ecs["lane"][eid] == town.get("lane"), \
                "the raider's lane and its target's lane disagree"
        for x, z in path[1:-1]:
            assert world.terrain.tile_type(x, z) != TILE_BUILDING, \
                f"way point ({x:.0f}, {z:.0f}) is inside a building"


def _radar() -> None:
    """Regression: the radar equation, against the table it was designed from.

    `d^2 <= K * v_closing` with `K = sqrt(radar_strength * rcs) / RADAR_V_REF`.
    The discovery distances are the ones the design was tuned to (398 / 713 /
    1000 / 563 m for a mech, exactly half for a carrier), a round that is
    opening out is never seen, and closing faster is seen further out.
    """
    import math
    mech = defs.UNIT_DEFS[defs.ETYPE_MECH]["radar_strength"]
    apc = defs.UNIT_DEFS[defs.ETYPE_APC]["radar_strength"]
    assert mech == 5.0e13 and apc == 3.125e12, "the radar strengths moved"

    def seen(strength: float, rcs: float, d: float, vc: float) -> bool:
        k = math.sqrt(strength * rcs) / defs.RADAR_V_REF
        return d * d <= k * vc

    want = [(defs.WID_RPG, 300.0, 398.0), (defs.WID_ROCKET, 610.0, 713.0),
            (defs.WID_SACLOS, 600.0, 1000.0), (defs.WID_TOPATTACK, 190.0, 563.0)]
    for wid, v, dist in want:
        rcs = defs.WEAPONS[wid]["rcs"]
        for strength, scale in ((mech, 1.0), (apc, 0.5)):
            got = defs.radar_range(strength, rcs) * math.sqrt(v / defs.RADAR_V_REF)
            assert abs(got - dist * scale) < 1.0, \
                f"{defs.WEAPONS[wid]['key']} at {got:.1f} m, want {dist * scale:.1f}"
            assert seen(strength, rcs, got - 5.0, v), "not seen just inside the range"
            assert not seen(strength, rcs, got + 5.0, v), "seen past the range"
            # opening out or crossing: the right-hand side is <= 0, so no test
            # clause is needed and none may creep in
            assert not seen(strength, rcs, 10.0, -1.0), "a receding round was seen"
            assert not seen(strength, rcs, 10.0, 0.0), "a crossing round was seen"
    # head-on the hull's own speed is part of the closing speed
    assert seen(mech, defs.WEAPONS[defs.WID_SACLOS]["rcs"], 1041.0 - 5.0, 650.0)
    # the collision sphere is the half-calibre, never a playability knob
    for w in defs.WEAPONS:
        if defs.is_interceptable(int(w["wid"])):
            assert abs(w["hit_r"] - w["cal"] / 2000.0) < 1e-9, "hit_r moved"
            assert w["aim_hp"] == 10.0 and w["aim_dp"] == 1.0 and w["aim_de"] == 1.0
        else:
            assert "rcs" not in w, f"{w['key']} got a radar signature"


def _intercept() -> None:
    """Regression: a missile is a target with 10 hit points, not a trajectory.

    A direct 30 mm round is worth 24 points against it (10 behind the plate plus
    the burst) and kills; the *fuse* is what arm's-length interception is for,
    and its blast is 14 at contact, 10 at 2.67 m and 8 at the 4 m edge - so a
    burst at the edge of the fuse leaves it flying.  What is left behind is
    nothing: no warhead, no wreck, no credit.
    """
    import logging

    class Catch(logging.Handler):
        def __init__(self) -> None:
            super().__init__()
            self.lines: list[str] = []

        def emit(self, record: logging.LogRecord) -> None:
            self.lines.append(record.getMessage())

    world = World(seed=3, mode="range")
    e = world.ecs
    wd = defs.WEAPONS[defs.WID_AC30]
    e.destroy(world.interceptor.eid)
    world.interceptor.eid = -1
    world.interceptor.t = 1e9
    catch = Catch()
    logging.getLogger("mech.intercept").addHandler(catch)
    logging.getLogger("mech.intercept").setLevel(logging.DEBUG)

    def missile(x: float, z: float) -> int:
        from server import components
        mid = e.create(components.MISSILE_COMPS)
        wid = defs.WID_SACLOS
        e["pos"][mid] = (x, float(world.terrain.height_at(x, z)) + 2.0, z)
        e["vel"][mid] = (0.0, 0.0, 0.0)
        e["faction"][mid] = 1
        e["proj_kind"][mid] = 2
        e["proj_wid"][mid] = wid
        e["proj_life"][mid] = 30.0
        e["proj_src"][mid] = -1
        e["target"][mid] = -1
        e["hp"][mid] = defs.WEAPONS[wid]["aim_hp"]
        e["hpmax"][mid] = e["hp"][mid]
        e["armor"][mid] = (defs.WEAPONS[wid]["aim_dp"], defs.WEAPONS[wid]["aim_de"])
        return mid

    # a direct hit takes it apart outright
    mid = missile(3000.0, 3000.0)
    combat.hit(world, mid, defs.MOD_CHASSIS, wd, 0.0, -1)
    assert float(e["hp"][mid]) <= 0.0, "a direct 30 mm round did not kill a missile"
    e.destroy(mid)

    # the blast at the edge of the fuse: 8 points, it flies on; at 2.67 m: dead
    mid = missile(3000.0, 3000.0)
    combat.splash(world, mid, defs.MOD_CHASSIS, wd, 4.0, -1)
    assert abs(float(e["hp"][mid]) - 2.0) < 1e-4, "the 4 m edge was not 8 points"
    assert e.alive[mid], "a burst at the edge of the fuse killed it"
    combat.splash(world, mid, defs.MOD_CHASSIS, wd, 2.67, -1)
    assert float(e["hp"][mid]) <= 0.0, "2.67 m did not kill it"
    # judged a stage later, and it leaves neither a burst nor a wreck behind it
    e.query("unit")
    n_blast = len(e.query("blast"))
    world.update(DT)
    assert not e.alive[mid], "the shot-down missile was still in the air"
    assert len(e.query("blast")) == n_blast, "a shot-down missile went up"
    assert len(e.query("wreck_t")) == 0, "a shot-down missile left a wreck"

    # the cue is addressed: `pid` is the pilot the burst is credited to and
    # `firer` the pilot whose round it was.  They are the only two clients that
    # sound the shot-down tone (`frontend/src/game.js::_fx`) - without that the
    # whole battle chimes once per AI interception on the map.
    mid = missile(3000.0, 3000.0)
    launcher = world.spawn_unit(defs.ETYPE_MECH, 1, 1000.0, 3000.0, owner=7)
    e["proj_src"][mid] = launcher
    world.events.clear()
    combat.apply(world, mid, defs.MOD_CHASSIS, 999.0, 5)
    world.update(DT)
    cue = [q for q in world.events if q.get("k") == "intercept"]
    assert cue, "a shot-down missile raised no intercept cue"
    assert cue[-1]["pid"] == 5 and cue[-1]["firer"] == 7, \
        f"the cue was addressed to pid={cue[-1]['pid']} firer={cue[-1]['firer']}"
    world.events.clear()
    logging.getLogger("mech.intercept").removeHandler(catch)
    e.validate()


def _speed_invariant() -> None:
    """Regression: one position and one velocity, on everything that moves.

    The component set *is* the entity's type, so a new mine, drone or shell that
    forgets one of the pair stays invisible until something asks for the velocity
    it does not have - which is how the top attack spent months leading a hull's
    *command* instead of its motion.  The rules (`components.py`):

        has(vel)      => has(pos), and both are 3-wide
        has(moves)    => has(pos) and has(vel) and has(vel_cmd)
        has(proj_kind) or has(canister) => has(pos) and has(vel)

    Checked on the declared component lists (the factory's own input) and then
    on a live world, so both a new list and a new spawn path are caught.
    """
    from server import components as C

    def check(name: str, comps) -> None:
        comps = set(comps)
        if "vel" in comps:
            assert "pos" in comps, f"{name} carries a velocity with no place"
        if "moves" in comps:
            assert {"pos", "vel", "vel_cmd"} <= comps, f"{name} is a mover without pos/vel/vel_cmd"
        if "proj_kind" in comps or "canister" in comps:
            assert {"pos", "vel"} <= comps, f"{name} is a round/canister without pos+vel"

    for name in ("UNIT_COMPS", "CIV_COMPS", "DEBRIS_COMPS", "BLAST_COMPS",
                 "PROJ_COMPS", "MISSILE_COMPS", "CANISTER_COMPS", "PUFF_COMPS"):
        check(name, getattr(C, name))
    assert "pos" in C.UNIT_COMPS and "vel" in C.UNIT_COMPS
    assert "moves" in defs.UNIT_DEFS[defs.ETYPE_MECH]["tags"]

    world = World(seed=5, mode="battle")
    e = world.ecs
    for _ in range(90):                    # let a few rounds and puffs exist
        world.update(DT)
        world.events.clear()

    for arch in e.archetypes:
        if arch.n == 0:
            continue
        bufs = arch.bufs
        check(f"archetype {sorted(k for k in bufs if k in ('moves', 'proj_kind', 'canister', 'vel'))}",
              bufs)
        if "vel" in bufs:
            assert bufs["vel"].shape[1] == 3, "vel is not 3-wide"
        if "pos" in bufs:
            assert bufs["pos"].shape[1] == 3, "pos is not 3-wide"

    # ...and the velocity is the motion, not the order: a mech told to run flat
    # out crawls off the mark at `MECH_ACCEL`, so `vel` may not equal `vel_cmd`
    # on the first ticks even though the command is already at top speed.  The
    # range is the world for it: its fixtures never drive a hull the test owns.
    world = World(seed=5, mode="range")
    e = world.ecs
    mech = world.spawn_unit(defs.ETYPE_MECH, defs.FACTION_RED, 900.0, 2400.0)
    top = float(defs.UNIT_DEFS[defs.ETYPE_MECH]["speed"])
    e["vel_cmd"][mech] = (top, 0.0, 0.0)
    world.update(DT)
    v = e["vel"][mech]
    got = math.hypot(float(v[0]), float(v[2]))
    assert got <= defs.MECH_ACCEL * DT * 1.5, \
        f"vel jumped to {got:.1f} m/s on the first tick; it is the command again"
    for _ in range(60):
        world.update(DT)
    got = math.hypot(float(e["vel"][mech, 0]), float(e["vel"][mech, 2]))
    assert 0.3 * top < got < 1.1 * top, f"the hull never got going: {got:.1f} m/s"
    world.events.clear()
    e.validate()


def _fuse_geometry() -> None:
    """Regression: the fuse solves relative motion, not a standing target.

    A 600 m/s missile and a 1080 m/s round meeting head-on close 56 m in one
    tick, and the round's fuse arm is 4 m.  Solving the leg against the target's
    *end* position puts the two 6 m apart at the end of the tick and 50 m apart
    at the start - neither is inside 4 m, so the old stationary rule misses the
    crossing entirely; the relative solve finds the 2 m pass in the middle of it
    and bursts there.
    """
    import logging
    from server import components

    class Catch(logging.Handler):
        def __init__(self) -> None:
            super().__init__()
            self.lines: list[str] = []

        def emit(self, record: logging.LogRecord) -> None:
            self.lines.append(record.getMessage())

    world = World(seed=3, mode="range")
    e = world.ecs
    if world.interceptor:
        e.destroy(world.interceptor.eid)
        world.interceptor.eid = -1
        world.interceptor.t = 1e9              # keep the post out of this test
    h = float(world.terrain.height_at(3000.0, 3000.0)) + 2.0
    mid = e.create(components.MISSILE_COMPS)
    e["pos"][mid] = (1000.0 + 50.0, h, 3000.0 + 2.0)
    e["vel"][mid] = (-600.0, 0.0, 0.0)
    e["faction"][mid] = 1
    e["proj_kind"][mid] = 2
    e["proj_wid"][mid] = defs.WID_SACLOS
    e["proj_life"][mid] = 30.0
    e["proj_src"][mid] = -1
    e["hp"][mid] = 10.0
    e["hpmax"][mid] = 10.0
    e["armor"][mid] = (1.0, 1.0)
    rid = e.create(components.PROJ_COMPS)
    e["pos"][rid] = (1000.0, h, 3000.0)
    e["vel"][rid] = (1080.0, 0.0, 0.0)
    e["faction"][rid] = 0
    e["proj_kind"][rid] = 0
    e["proj_wid"][rid] = defs.WID_AC30
    e["proj_life"][rid] = 9.0
    e["proj_src"][rid] = -1

    catch = Catch()
    logging.getLogger("mech.intercept").addHandler(catch)
    logging.getLogger("mech.intercept").setLevel(logging.DEBUG)
    world.update(DT)
    logging.getLogger("mech.intercept").removeHandler(catch)

    # 36 m and 20 m of travel: they are 6 m apart at the end of the tick and
    # 50 m apart at the start, which is the case the standing-target solve misses
    took = [ln for ln in catch.lines if ln.startswith("missile ")]
    assert took, ("the fuse never fired: a round and a missile crossing inside "
                  "one tick were solved as if the target stood still")
    gap = float(took[0].split("prox ", 1)[1].split(" m")[0])
    assert gap <= 4.0, f"the fuse burst {gap:.2f} m off, past its own radius"
    assert abs(gap - 2.0) < 0.5, f"the burst was {gap:.2f} m off, want ~2 m"
    assert not e.alive[rid] or float(e["hp"][rid]) < 10.0, "the missile took nothing"
    e.validate()


def _range_slots() -> None:
    """Regression: a range post survives its hull's id being handed out again.

    Entity ids are reused LIFO, so the id a dead target leaves behind is taken by
    the next thing that spawns - for a hull going up, its own blast entity.  The
    range keeps raw ids in its slots (the 12 APC targets, the defence post) and
    used to trust `alive`: driving a blast raised KeyError once a tick and, since
    the exception aborts the slot loop, the whole world froze for good.  Kill a
    target, tick past the wreck, and the slot has to be a live APC again.
    """
    world = World(seed=3, mode="range")
    e = world.ecs
    slot = world.targets.slots[0]
    victim = int(slot["eid"])
    combat.apply(world, victim, defs.MOD_CHASSIS, 9999.0)
    world.update(DT)                       # mortality kills it, blast takes its id
    world.update(DT)                       # the old code raised here, every tick
    for _ in range(120):                   # 4 s: the slot's respawn delay is 3
        world.update(DT)
        world.events.clear()
    back = int(slot["eid"])
    assert back != victim or e.alive[back], "the slot never came back"
    assert e.has(back, "moves") and int(e["etype"][back]) == defs.ETYPE_APC, \
        "the slot is not pointing at a target any more"
    live = [s for s in world.targets.slots
            if e.alive[int(s["eid"])] and e.has(int(s["eid"]), "moves")]
    assert len(live) == 12, f"{len(live)} targets alive, want 12"
    # the defence post is respawned on the same rule
    post = world.interceptor.eid
    combat.apply(world, post, defs.MOD_CHASSIS, 9999.0)
    for _ in range(240):                   # 8 s > the 5 s it waits
        world.update(DT)
        world.events.clear()
    new_post = world.interceptor.eid
    assert new_post >= 0 and e.alive[new_post] and e.has(new_post, "moves"), \
        "the defence post did not come back"
    e.validate()


def run(ticks: int = TICKS, verbose: bool = False) -> list[dict[str, float]]:
    _cache_after_growth()
    _damage_model()
    _death_blast()
    _raid_plan()
    _radar()
    _intercept()
    _fuse_geometry()
    _range_slots()
    _speed_invariant()
    world = World(seed=11, mode="battle")
    pl = world.add_player("REPLAY")           # spawns the mech too
    # the first raiding party leaves on its own clock (60 s into the round, well
    # past this harness's 30 s), so it is brought forward: two hulls that plan a
    # route, drive it and take a town are the part of the battle nothing else
    # here exercises
    world.raid_timer = {0: 0.2, 1: 0.4}
    out = []
    for tick in range(ticks):
        drive(world, pl, tick)
        world.update(DT)
        world.events.clear()                  # client-facing only
        out.append(summary(world, pl))
        if verbose:
            print(f"{tick:5d}  " + "  ".join(f"{k}={v:.2f}" if isinstance(v, float)
                                             else f"{k}={v}" for k, v in out[-1].items()))
        if tick % 150 == 0:
            world.ecs.validate()
    world.ecs.validate()
    return out


# ----------------------------------------------------------------------
# comparison
# ----------------------------------------------------------------------
def compare(got: list[dict], want: list[dict]) -> int:
    if len(got) != len(want):
        print(f"length differs: got {len(got)} ticks, fixture has {len(want)}")
        return 1

    # the counts must be there, give or take one entity; the sums may drift a
    # little, but not so much that the battle went somewhere else
    worst: dict[str, float] = {}
    bad: dict[str, list[int]] = {}
    for tick, (g, w) in enumerate(zip(got, want)):
        for k in COUNT_SERIES:
            if abs(g[k] - w[k]) > 1:
                bad.setdefault(k, []).append(tick)
        for k in FLOAT_SERIES:
            scale = max(1.0, abs(w[k]))
            dev = abs(g[k] - w[k]) / scale
            if dev > 1e-2:
                bad.setdefault(k, []).append(tick)
            worst[k] = max(worst.get(k, 0.0), dev)

    if not bad:
        drifts = ", ".join(f"{k} max {100 * worst[k]:.4f}%" for k in FLOAT_SERIES)
        print(f"ok - {len(got)} ticks match ({drifts})")
        return 0

    print(f"{len(bad)} series out of tolerance:")
    for k, ticks in bad.items():
        print(f"  {k:<12} {len(ticks):4d} ticks, first at {ticks[0]:4d}"
              f"   got {got[ticks[0]][k]!r} want {want[ticks[0]][k]!r}")
    print(f"\nre-run with  python tools/replay.py -v  and look at tick {bad[next(iter(bad))][0]}")
    return 1


def main(argv: list[str]) -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--ticks", type=int, default=TICKS)
    ap.add_argument("--save", action="store_true")
    ap.add_argument("--fixture", default=str(FIXTURE))
    ap.add_argument("-v", "--verbose", action="store_true")
    args = ap.parse_args(argv[1:])

    got = run(args.ticks, args.verbose)
    path = pathlib.Path(args.fixture)

    if args.save:
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(json.dumps(got))
        print(f"saved {len(got)} ticks -> {path}")
        return 0
    if not path.exists():
        print(f"no fixture at {path} (run with --save)")
        return 0
    return compare(got, json.loads(path.read_text()))


if __name__ == "__main__":
    raise SystemExit(main(sys.argv))
