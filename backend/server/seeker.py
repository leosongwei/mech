"""The IR seeker's lock: what a top-attack round is flying at.

A guided round does not need its target to survive the flight.  The seeker
carries two numbers (components ``tgt_mem`` / ``tgt_vel``): the point it stares
at - the deck of the hull it is going to dive on - and the velocity that hull
had the last time it was seen.  While the lock holds, both are refreshed every
tick off the live hull; the moment something cuts the sight line (a puff, a
ridge, a tree, or the hull dying) the stare point keeps walking at that last
velocity, and *guidance never notices*: `flight.step` is handed a point and a
velocity, and a stale point is a point like any other.

That is the whole of the difference between a missile and a rock, and it is
also what makes re-acquisition one question: is there an enemy hull I can see
inside ``defs.SEEKER_CONE`` of my stare line?  If there is, it is the target
now - whichever hull it happens to be.  A hull that drives into the shot's line
while the smoke is up is as good a target as the one that pulled the smoke, and
the code does not care to tell them apart: it asks for an enemy, not for *the*
enemy.

The cone is measured from the seeker's own eye, against the line eye -> stare
point, so it re-locks onto what is in front of it, not onto what is merely near
the estimate: a hull beside the round is not a target until the round turns
onto it.  Losers of the sweep are not ordered beyond that - the one nearest the
stare line goes first, and the first one with a clear sight line is taken.
"""

from __future__ import annotations

import logging
import math
from typing import TYPE_CHECKING

import numpy as np

from . import defs
from .systems import sight

if TYPE_CHECKING:
    from ..world import World

# the smoke module's logger: a missile losing its lock has always been logged
# here, and the range's smoke test reads the line straight out of the log
log = logging.getLogger("mech.smoke")


def roof(world: World, tid: int) -> tuple[float, float, float]:
    """The point the seeker aims at on a hull: its deck, not its tracks."""
    e = world.ecs
    p = e["pos"][tid]
    h = defs.UNIT_DEFS.get(int(e["etype"][tid]), {}).get("height", 6.0)
    return (float(p[0]), float(p[1]) + defs.AIM_ROOF * h, float(p[2]))


def stare(world: World, pid: int) -> tuple[float, float, float]:
    """The point this round is staring at right now (its memory as it stands)."""
    m = world.ecs["tgt_mem"][pid]
    return (float(m[0]), float(m[1]), float(m[2]))


def track(world: World, pid: int, p0, dt: float):
    """One tick of the seeker: hold the lock, or find a new one.

    Returns the point to guide on, or ``None`` for a round that has nothing to
    stare at (it was fired without a lock, which a lock weapon cannot be).  The
    round's own components are the seeker's state, so this is idempotent per
    tick and needs nothing remembered anywhere else.
    """
    e = world.ecs
    pos = e["pos"]
    vel = e["vel"]
    faction = e["faction"]
    target = e["target"]
    mem = e["tgt_mem"]
    memv = e["tgt_vel"]
    mine = int(faction[pid])

    # ---- the lock we had, if it is still a lock -------------------------
    tid = int(target[pid])
    if tid >= 0 and (not e.alive[tid] or int(faction[tid]) == mine):
        tid = -1
    spot = None
    if tid >= 0:
        spot = roof(world, tid)
        why = sight.why(world, p0, spot)
        if why:
            # the seeker is blind: log it (the smoke test on the range is read
            # straight out of the server log, at DEBUG) and fall through to the
            # memory
            log.debug("seeker of missile %d lost the lock on hull %d: %s on the "
                     "line of sight (%.0f m out, %.1f s in the air)",
                     pid, tid, why, math.dist(p0, spot), float(e["proj_age"][pid]))
            world.emit("seeker", x=float(p0[0]), y=float(p0[1]), z=float(p0[2]),
                       faction=mine, tid=int(tid), proj=int(pid), why=why,
                       dist=round(math.dist(p0, spot), 1))
            spot = None
    if spot is not None:
        target[pid] = tid
        mem[pid] = spot
        memv[pid] = vel[tid]
        return spot

    # ---- no lock: stare at where the hull should be ---------------------
    target[pid] = -1
    if not mem[pid].any():
        return None                         # nothing was ever locked
    m = mem[pid] + memv[pid] * dt           # the memory keeps flying the hull
    mem[pid] = m
    spot = (float(m[0]), float(m[1]), float(m[2]))

    # ---- and take whatever enemy it can see in the cone ----------------
    tid = acquire(world, p0, spot, mine)
    if tid < 0:
        return spot
    spot = roof(world, tid)
    target[pid] = tid
    mem[pid] = spot
    memv[pid] = vel[tid]
    log.debug("seeker of missile %d re-locked on hull %d (%.0f m out, %.1f s in "
             "the air)", pid, tid, math.dist(p0, spot), float(e["proj_age"][pid]))
    return spot


def acquire(world: World, p0, spot, mine: int) -> int:
    """First enemy hull with a clear sight line inside the cone, or -1.

    The candidates are judged on the deck they would be dived on (the point the
    seeker looks at), not on their tracks: at 1 km the two are a third of a
    degree apart, which is a fifth of the cone.
    """
    e = world.ecs
    ids = world.lockable_units()
    if ids.size == 0:
        return -1
    d = np.asarray(spot, np.float32) - np.asarray(p0, np.float32)
    n = float(np.linalg.norm(d))
    if n < 1e-3:
        return -1
    d /= n
    c = np.asarray(e["pos"][ids], np.float32)
    heights = np.asarray([defs.UNIT_DEFS.get(int(k), {}).get("height", 6.0)
                          for k in np.asarray(e["etype"][ids])], np.float32)
    c[:, 1] += defs.AIM_ROOF * heights
    v = c - np.asarray(p0, np.float32)
    dist = np.linalg.norm(v, axis=1)
    ok = (dist > 1e-3) & (np.asarray(e["faction"][ids]) != mine)
    if not ok.any():
        return -1
    ang = np.arccos(np.clip((v @ d) / np.maximum(dist, 1e-3), -1.0, 1.0))
    ok &= ang <= defs.SEEKER_CONE
    if not ok.any():
        return -1
    # nearest to the stare line first, but a hull in cover is never a
    # candidate: take the first one the seeker can actually see
    for j in np.nonzero(ok)[0][np.argsort(ang[ok])]:
        p = (float(c[j, 0]), float(c[j, 1]), float(c[j, 2]))
        if sight.clear(world, p0, p):
            return int(ids[j])
    return -1
