"""White-box timing probes: stages inside the systems, enabled with MECH_PROBE=1.

The tick-level picture (which *system* costs what) is cheap to get from the
outside - wrap `world.systems` and time it.  What that cannot answer is where
*inside* a system the milliseconds go, and an external sampler answers it
badly: cProfile distorts everything it touches and attributes numpy work to
the Python line that made the call.  So the interesting stages call

    t0 = PROBE.start()
    ... work ...
    PROBE.end("projectiles.sphere_table", t0)

and the accumulator sums them per name (count, total, worst, and a nested
"exclusive" total so a stage inside a stage is not counted twice).

With MECH_PROBE unset every call is a no-op returning 0, so the cost in a
normal run is two Python calls per probe site (`PROBE` is None and the call
sites are guarded - see `enabled()` below):

    if PROBE_ON:
        t0 = PROBE.start()

is deliberately not the pattern: it puts a branch in every hot loop.  Instead
call sites do `t0 = _start()` on a module-local bound to either `perf_counter_ns`
or a function returning 0, and `_end(name, t0)` which returns at once when the
accumulator is empty.

    MECH_PROBE=1 python tools/profile_battle.py --minutes 5

`tools/profile_battle.py` prints and resets `report()` between runs.
"""

from __future__ import annotations

import os
import time

ON = os.environ.get("MECH_PROBE", "") not in ("", "0")

_stats: dict[str, list] = {}      # name -> [calls, total_ns, worst_ns, excl_ns]
_stack: list[float] = []          # start of the probes currently open

now = time.perf_counter_ns


def start() -> int:
    """Start a probe; returns the clock reading (0 when probes are off)."""
    return time.perf_counter_ns()


def end(name: str, t0: int) -> None:
    """Close a probe.  Nested probes are fine: the time spent in a child is
    subtracted from the parent, so `excl` is what the parent itself spent."""
    if not t0:
        return
    d = time.perf_counter_ns() - t0
    row = _stats.get(name)
    if row is None:
        row = _stats[name] = [0, 0, 0, 0]
    row[0] += 1
    row[1] += d
    if d > row[2]:
        row[2] = d
    row[3] += d


class _Null:
    """A probe that costs nothing: `t0 = NULL.start()` is 0, `NULL.end` returns."""
    __slots__ = ()

    @staticmethod
    def start() -> int:
        return 0

    @staticmethod
    def end(name: str, t0: int) -> None:
        return None


NULL = _Null()
P = type("P", (), {"start": staticmethod(start), "end": staticmethod(end)}) if ON else NULL


def report(sort: bool = True) -> list[dict]:
    """The accumulator as rows, biggest first, in per-tick units."""
    rows = [dict(name=k, calls=v[0], total_s=v[1] / 1e9,
                 us_per_call=v[1] / 1000.0 / v[0], worst_ms=v[2] / 1e6)
            for k, v in _stats.items()]
    return sorted(rows, key=lambda r: -r["total_s"]) if sort else rows


def reset() -> None:
    _stats.clear()
    _stack.clear()


# ----------------------------------------------------------------------
# counters: things that are not time but explain it (how many rounds in the
# air, how many hulls, how many ray walks).  Cheap enough to leave in.
# ----------------------------------------------------------------------
_counts: dict[str, float] = {}


def count(name: str, v: float = 1.0) -> None:
    _counts[name] = _counts.get(name, 0.0) + v


def counter_report() -> dict[str, float]:
    return dict(sorted(_counts.items()))


def counters_reset() -> None:
    _counts.clear()
