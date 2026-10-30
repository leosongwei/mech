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

# series compared as floats, with a tolerance
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


def run(ticks: int = TICKS, verbose: bool = False) -> list[dict[str, float]]:
    _cache_after_growth()
    _damage_model()
    _death_blast()
    _raid_plan()
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
