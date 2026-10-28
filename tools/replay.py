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

from server.world import World  # noqa: E402

FIXTURE = ROOT / "tools" / "fixtures" / "replay_battle.json"
TICKS = 900                       # 30 s of battle at 30 Hz
DT = 1.0 / 30.0

# series compared as floats, with a tolerance
FLOAT_SERIES = ("hp_total", "pos_x", "pos_z", "vel_x", "vel_z")
# series compared as counts, at most one off
COUNT_SERIES = ("n_units", "n_combat", "n_proj", "n_smoke", "n_canister",
                "n_debris", "n_all", "wave", "kills", "deaths")


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
                world.damage(int(eid), 0, 9999.0, -1)
                break


# ----------------------------------------------------------------------
# per-tick summary
# ----------------------------------------------------------------------
def summary(world: World, pl) -> dict[str, float]:
    e = world.ecs
    ids = e.query("pos")                      # everything that has a position
    pos = e["pos"][ids].astype(np.float64) if len(ids) else np.zeros((0, 3))
    vel = e["vel"][ids].astype(np.float64) if len(ids) else np.zeros((0, 3))
    hp = e["hp"][e.query("hp")].astype(np.float64)
    return dict(
        n_all=int(ids.size),
        n_units=int(e.query("unit").size),
        n_combat=int(e.query("combat").size),
        n_proj=int(e.query("proj_kind").size),
        n_smoke=int(e.query("smoke").size),
        n_canister=int(e.query("canister").size),
        n_debris=int(e.query("wreck_t").size),
        hp_total=float(hp.sum()),
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


def run(ticks: int = TICKS, verbose: bool = False) -> list[dict[str, float]]:
    _cache_after_growth()
    world = World(seed=11, mode="battle")
    pl = world.add_player("REPLAY")           # spawns the mech too
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
