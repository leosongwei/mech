"""Headless battle profiling: run the simulation as fast as the CPU allows.

The server loop in ``main.py`` sleeps to hold 30 Hz.  That tells you nothing
about what the simulation costs, so this tool drops the sleep and measures:

  * wall time and ticks/s for a whole run of N minutes of *game* time,
  * per-system cost (every entry of ``world.systems`` plus the grid build and
    the rest of ``World.update``), as total / mean per tick / worst tick,
  * a cProfile call-level breakdown of the same run,
  * a per-minute timeline (entities alive, tick cost) so growth shows up.

Two scripted pilots fly the battle (one per side) with deterministic input, so
the runs are identical apart from the wall clock.

    python tools/profile_battle.py --minutes 5
    python tools/profile_battle.py --minutes 5,10,20,30 --out /tmp/prof.json
    python tools/profile_battle.py --minutes 5 --no-profile
"""

from __future__ import annotations

import argparse
import cProfile
import io
import json
import pathlib
import pstats
import sys
import time

import numpy as np

ROOT = pathlib.Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT / "backend"))

from server.world import World  # noqa: E402

DT = 1.0 / 30.0
TICKS_PER_MIN = 30 * 60


# ----------------------------------------------------------------------
# scripted pilots - deterministic, so every duration replays the same battle
# ----------------------------------------------------------------------
def drive(world: World, pl, tick: int) -> None:
    if pl.mech < 0:
        return
    pl.in_yaw = 0.4 + 0.02 * tick + (1.3 if pl.faction == 1 else 0.0)
    pl.in_pitch = -0.06
    pl.in_move = (1.0, 0.0) if (tick // 60) % 2 == 0 else (0.0, 1.0)
    pl.zoom = 4.0
    pl.zero_range = 800.0
    pl.fire = (20 <= tick < 400, 90 <= tick < 260)
    if tick in (30, 200, 420, 700):
        pl.smoke_q = True


def make_world(seed: int) -> tuple[World, list]:
    world = World(seed=seed, mode="battle")
    pilots = [world.add_player("RED-1"), world.add_player("BLUE-1")]
    return world, pilots


def census(world: World) -> dict[str, int]:
    e = world.ecs
    return dict(
        n_all=int(e.query().size),
        n_units=int(e.query("unit").size),
        n_combat=int(e.query("combat").size),
        n_proj=int(e.query("proj_kind").size),
        n_smoke=int(e.query("smoke").size),
        n_debris=int(e.query("debris").size),
        n_raider=int(e.query("raider").size),
        n_blast=int(e.query("blast_t").size),
        wave=int(world.wave),
    )


# ----------------------------------------------------------------------
# one timed run
# ----------------------------------------------------------------------
def run(minutes: float, seed: int, profile: bool = False) -> dict:
    ticks = int(round(minutes * TICKS_PER_MIN))
    world, pilots = make_world(seed)

    # time every stage of the pipeline: grid build, each system, and the rest
    # of World.update.  perf_counter_ns around ~15 calls a tick is ~2 us/tick,
    # i.e. noise next to a millisecond-scale tick.
    names: list[str] = []
    acc: dict[str, float] = {}
    worst: dict[str, float] = {}
    grid_acc = [0.0]
    grid_worst = [0.0]

    def timed(fn, name):
        def wrapper(w, dt):
            t0 = time.perf_counter_ns()
            fn(w, dt)
            dt_ns = time.perf_counter_ns() - t0
            acc[name] = acc.get(name, 0.0) + dt_ns
            if dt_ns > worst.get(name, 0.0):
                worst[name] = dt_ns
        return wrapper

    for i, fn in enumerate(world.systems):
        name = getattr(fn, "__module__", "?").rsplit(".", 1)[-1] + "." + fn.__name__
        names.append(name)
        world.systems[i] = timed(fn, name)

    # Grid has __slots__, so the build is timed at class level; a fresh world
    # per run means the counters are never shared between runs.
    from server.grid import Grid
    grid_build = Grid.build

    def timed_grid(self, *a, **kw):
        t0 = time.perf_counter_ns()
        grid_build(self, *a, **kw)
        d = time.perf_counter_ns() - t0
        grid_acc[0] += d
        if d > grid_worst[0]:
            grid_worst[0] = d
    Grid.build = timed_grid

    timeline: list[dict] = []
    update_ns = 0
    minute_ns = 0
    sample = TICKS_PER_MIN

    # the wire is part of the server's tick too: main.broadcast packs one
    # snapshot every SNAP_EVERY ticks and one meta per pilot every META_EVERY.
    # `netcode.STATIC` is built once at import, so it is not in here.
    from server import netcode
    from server.main import SNAP_EVERY, META_EVERY
    net_ns = 0
    snap_n = 0

    prof = cProfile.Profile() if profile else None
    if prof:
        prof.enable()
    wall0 = time.perf_counter()
    for tick in range(ticks):
        for pl in pilots:
            drive(world, pl, tick)
        t0 = time.perf_counter_ns()
        world.update(DT)
        d = time.perf_counter_ns() - t0
        update_ns += d
        minute_ns += d
        if (tick + 1) % SNAP_EVERY == 0:
            t0 = time.perf_counter_ns()
            netcode.snapshot_bytes(world)
            net_ns += time.perf_counter_ns() - t0
            snap_n += 1
            t0 = time.perf_counter_ns()
            for pl in pilots:
                netcode.meta_payload(world, pl)
            net_ns += time.perf_counter_ns() - t0
        if (tick + 1) % sample == 0:
            timeline.append(dict(minute=(tick + 1) // sample,
                                 ms_per_tick=minute_ns / 1e6 / sample,
                                 **census(world)))
            minute_ns = 0
    wall = time.perf_counter() - wall0
    if prof:
        prof.disable()

    total = sum(acc.values()) + grid_acc[0]
    sys_rows = []
    for name in names:
        ns = acc.get(name, 0.0)
        sys_rows.append(dict(name=name,
                             total_s=ns / 1e9,
                             us_per_tick=ns / 1000.0 / ticks,
                             share=ns / total if total else 0.0,
                             worst_ms=worst.get(name, 0.0) / 1e6))
    sys_rows.append(dict(name="grid.build", total_s=grid_acc[0] / 1e9,
                         us_per_tick=grid_acc[0] / 1000.0 / ticks,
                         share=grid_acc[0] / total if total else 0.0,
                         worst_ms=grid_worst[0] / 1e6))
    unaccounted = update_ns - total
    sys_rows.append(dict(name="(update overhead)", total_s=unaccounted / 1e9,
                         us_per_tick=unaccounted / 1000.0 / ticks,
                         share=unaccounted / update_ns if update_ns else 0.0,
                         worst_ms=0.0))
    sys_rows.sort(key=lambda r: -r["total_s"])

    out = dict(minutes=minutes, ticks=ticks, seed=seed,
               wall_s=wall, tick_rate=ticks / wall,
               ms_per_tick=1000.0 * wall / ticks,
               sim_seconds=ticks * DT,
               realtime_factor=ticks * DT / wall,
               update_ms_per_tick=update_ns / 1e6 / ticks,
               netcode_ms_per_tick=net_ns / 1e6 / ticks,
               netcode_share_of_tick=net_ns / (update_ns + net_ns) if update_ns else 0.0,
               systems=sys_rows,
               timeline=timeline)
    if prof:
        s = io.StringIO()
        st = pstats.Stats(prof, stream=s).sort_stats("tottime")
        st.print_stats(40)
        out["profile"] = s.getvalue()
        s2 = io.StringIO()
        pstats.Stats(prof, stream=s2).sort_stats("cumulative").print_stats(40)
        out["profile_cumulative"] = s2.getvalue()
    return out


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--minutes", default="5", help="comma separated, e.g. 5,10,20,30")
    ap.add_argument("--seed", type=int, default=11)
    ap.add_argument("--out", default="/tmp/profile_battle.json")
    ap.add_argument("--no-profile", action="store_true", help="skip cProfile")
    a = ap.parse_args()

    runs = []
    for m in [float(v) for v in a.minutes.split(",")]:
        print(f"=== {m:g} min =========================================", flush=True)
        r = run(m, a.seed, profile=not a.no_profile)
        runs.append(r)
        print(f"  {r['ticks']} ticks in {r['wall_s']:.1f} s "
              f"= {r['ms_per_tick']:.3f} ms/tick, {r['tick_rate']:.0f} ticks/s, "
              f"{r['realtime_factor']:.0f}x realtime", flush=True)
        for row in r["systems"][:12]:
            print(f"    {row['name']:<22} {row['share']*100:5.1f}%  "
                  f"{row['us_per_tick']:8.1f} us/tick  worst {row['worst_ms']:7.2f} ms",
                  flush=True)
        print(f"    {'netcode.wire':<22} {r['netcode_share_of_tick']*100:5.1f}%  "
              f"{r['netcode_ms_per_tick']*1000:8.1f} us/tick", flush=True)
        pathlib.Path(a.out).write_text(json.dumps(runs, indent=1))
    print("wrote", a.out)


if __name__ == "__main__":
    main()
