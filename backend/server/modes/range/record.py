"""Record what a top-attack round actually did, and what it was chasing.

Tuning the dive is a question about a whole pursuit, not about the last frame:
a proportional-navigation law is judged on how far the round is led astray by a
hull that stops, turns or fakes a turn, and on where it is when the target does
it.  None of that survives in the log - the per-weapon lines are DEBUG, they
carry no track, and the file they land in is thrown away every run.  So the
range records the simulation itself instead: one JSONL line a tick while a
top-attack round is in the air, the round's own state plus the true state of
every hull it could be after.

    {"time": 12.43, "tick": 373, "missiles": [...], "units": [...], "events": [...]}

The missile rows are the seeker's state, under the component names it lives in -
`tgt_mem` is the stare *point* (the memory, deck height), `tgt_vel` the velocity
that memory is drifting at and `target` the hull it holds, so `target == -1` is
a blinded round flying its memory.  The unit rows are ground truth, which is the
half the round cannot see.  With both, a flight can be replayed through
`flight.step` off the recorded state and compared with what the real one did.

**A tick with no top-attack round on it is not recorded.**  The dive is the only
thing the numbers are for, so a 30 mm burst or a SACLOS on its way to the
defence post leaves nothing behind, and the file stays a few hundred kB for an
evening of flying instead of a few hundred MB.

Enabled by `MECH_RECORD` (`dev.sh range` passes 1; `MECH_RECORD=0` turns it
off), path `MECH_RECORD_PATH` (default `/tmp/mech-range-trace.jsonl`).
"""

from __future__ import annotations

import json
import logging
import os
import time
from typing import TYPE_CHECKING

from ... import defs

if TYPE_CHECKING:
    from ...world import World

log = logging.getLogger("mech.record")

DEFAULT_PATH = "/tmp/mech-range-trace.jsonl"
KIND = "top"                   # the round this file exists for
KEEP_EVENTS = ("boom", "seeker", "intercept")
OFF = {"", "0", "off", "no", "false"}
ON = {"1", "on", "yes", "true"}
# what travels with the round: enough to re-fly it, nothing about its hp pool
ROUND_KEYS = ("kind", "loft", "apex", "turn_r", "pn_n", "pn_r", "pn_v",
              "mass", "dia", "cl_max", "cl_alpha", "cd0", "k_cl", "maxvel",
              "thrust", "burn", "ignite", "life", "range", "ed", "er")


def start(world: World) -> Record | None:
    """Open a trace when `MECH_RECORD` asks for one, else None."""
    flag = os.environ.get("MECH_RECORD", "").strip()
    if not flag or flag.lower() in OFF:
        return None
    path = os.environ.get("MECH_RECORD_PATH", "").strip()
    if flag.lower() not in ON:
        path = path or flag                  # MECH_RECORD=/tmp/x.jsonl works too
    return Record(path or DEFAULT_PATH, world)


def run(world: World, dt: float) -> None:
    """The mode's post system: sample the tick, if a trace is open."""
    rec = getattr(world, "record", None)
    if rec is not None:
        rec.run(world, dt)


def _p(a, n: int = 2) -> list[float]:
    return [round(float(x), n) for x in a]


def _top() -> dict:
    """The top-attack round's defs - the one thing this file is about."""
    for wd in defs.WEAPONS:
        if wd["kind"] == KIND:
            return wd
    raise KeyError(KIND)


class Record:
    """A JSONL trace of the range's top-attack rounds and the hulls they chase."""

    def __init__(self, path: str, world: World) -> None:
        self.path = path
        self.recent: set[int] = set()    # rounds on the previous line
        self.fh = open(path, "w", buffering=1, encoding="utf-8")
        self._line(self._header(world))

    # -- writing ------------------------------------------------------
    def _line(self, row: dict) -> None:
        self.fh.write(json.dumps(row, separators=(",", ":")) + "\n")

    def _header(self, world: World) -> dict:
        wd = _top()                       # the round this file is about
        dt = float(getattr(world, "dt", 0.0) or 0.0) or 1.0 / 30.0
        log.info("range trace -> %s", self.path)
        return dict(kind="header", when=time.strftime("%Y-%m-%d %H:%M:%S"),
                    mode=world.mode, tick_hz=round(1.0 / dt, 3), round=wd["key"],
                    params={k: wd[k] for k in ROUND_KEYS if k in wd})

    # -- sampling -----------------------------------------------------
    def run(self, world: World, dt: float) -> None:
        e = world.ecs
        wid = e["proj_wid"]
        missiles = []
        for pid in world.projectiles():
            pid = int(pid)
            w = defs.WEAPONS[int(wid[pid])]
            if w["kind"] != KIND:
                continue            # not the round this file is about: no track
            missiles.append(dict(
                id=pid,
                pos=_p(e["pos"][pid]), vel=_p(e["vel"][pid]),
                proj_age=round(float(e["proj_age"][pid]), 3),
                proj_trav=round(float(e["proj_trav"][pid]), 2),
                target=int(e["target"][pid]),
                tgt_mem=_p(e["tgt_mem"][pid]),
                tgt_vel=_p(e["tgt_vel"][pid], 3),
                hp=round(float(e["hp"][pid]), 2),
                proj_src=int(e["proj_src"][pid]),
                proj_pid=int(e["proj_pid"][pid])))
        # the tick's own events about these rounds: where one went off, a seeker
        # losing or retaking a lock, a round shot down.  Read against the rounds
        # written on the previous line as well, because a round that goes off is
        # destroyed inside `projectiles` - its burst is the one tick on which it
        # has no row of its own, and that burst is the impact point
        watch = {r["id"] for r in missiles} | self.recent
        events = [ev for ev in world.events
                  if ev.get("k") in KEEP_EVENTS
                  and int(ev.get("proj", -1)) in watch]
        if missiles:
            self.recent = {r["id"] for r in missiles}
        elif events:
            self.recent = set()         # that was the end of the round
        else:
            return                      # nothing diving: this tick is not worth a line

        # the hulls: everything a seeker could hold (a round that loses one lock
        # may take another), not just the one it is holding
        ids = sorted(int(x) for x in world.lockable_units() if bool(e.alive[x]))
        units = []
        for uid in ids:
            units.append(dict(
                id=uid, etype=int(e["etype"][uid]),
                faction=int(e["faction"][uid]),
                pos=_p(e["pos"][uid]), vel=_p(e["vel"][uid]),
                hp=round(float(e["hp"][uid]), 1)))
        self._line(dict(time=round(world.time, 3), tick=world.tick,
                        missiles=missiles, units=units, events=events))

    def close(self) -> None:
        if not self.fh.closed:
            self.fh.close()
