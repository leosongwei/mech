"""Proximity fuse: the airburst rounds go off on *people* - and on missiles.

A cannon round covers 36 m in one tick, so a distance test at the tick
boundaries would step clean over a rifleman standing 4 m off the path.  The
fuse therefore solves the relative motion of the round and its target across
the leg the round actually flew this tick (``proj_prev`` -> ``pos``) instead of
sampling it: both are straight and constant-speed inside one 1/30 s, so the
closest approach is closed-form, and if it is inside the fuse radius the round
goes off *there* - not at the end of the tick, 36 m past him, which is where an
impact fuse would have planted it in the dirt.

Two kinds of target set it off:

* ``person`` - a man is a man whatever he carries.  Armour is deliberately not
  tagged: an APC 4 m from the path leaves the fuse cold and waits for the
  impact fuse, which is what keeps the cannon a penetration weapon against
  vehicles and an area weapon against infantry.
* ``interceptable`` - a missile in the air (``components.MISSILE_COMPS``).  This
  is the counter-battery half: a 30 mm burst that passes within four metres of
  an incoming ATGM lights on it, and the fragments put ten points of blast into
  a round that has eight of health at that range.  A missile moves 20 m a tick,
  so this is also the case the relative solve exists for - measured against a
  *stationary* hull the fuse would fire on the phase of the clock.

The target a round fused on is settled *here*, at the moment it fused: the
burst entity that follows is judged a stage later, against positions gathered
for the whole tick, and by then a 600 m/s missile is twenty metres out of a
four metre radius.  So the fused hull takes `combat.splash` at the exact gap,
and the burst is told to spare it.
"""

from __future__ import annotations

import logging
import math
from typing import TYPE_CHECKING

import numpy as np

from .. import combat, defs
from . import projectiles

if TYPE_CHECKING:
    from ..world import World

log = logging.getLogger("mech.prox")


def run(world: World, dt: float) -> None:
    e = world.ecs
    pids = world.projectiles()
    if pids.size == 0:
        return
    wid = e["proj_wid"]
    fused = [(int(pid), defs.WEAPONS[int(wid[pid])]) for pid in pids
             if float(defs.WEAPONS[int(wid[pid])].get("prox", 0.0)) > 0.0]
    if not fused:                               # nothing in the air carries a fuse
        return
    people = e.query("person")
    missiles = world.interceptable()
    if people.size == 0 and missiles.size == 0:
        return

    # the two target sets, as rows of the tick's sphere table: the ids are
    # global, so a projectile id never collides with a hull's
    targets = np.concatenate((people, missiles)).astype(np.int32)
    sph = world.sphere_table(dt)
    rows = np.nonzero(np.isin(sph[2], targets))[0]
    if rows.size == 0:
        return
    centres = sph[7][rows].astype(np.float64)   # where each target starts the tick
    disp = sph[8][rows].astype(np.float64)      # and the ground it covers in it
    radii = sph[1][rows].astype(np.float64)
    pfaction = sph[4][rows]
    puid = sph[2][rows]

    pos = e["pos"]
    prev = e["proj_prev"]
    faction = e["faction"]

    for pid, wd in fused:
        if not e.alive[pid]:
            continue
        prox = float(wd["prox"])
        p0 = np.asarray(prev[pid], np.float64)
        p1 = np.asarray(pos[pid], np.float64)
        seg = p1 - p0
        l2 = float(seg @ seg)
        if l2 < 1e-9:                           # barely moved: nothing to solve
            continue
        rel = centres - p0
        w = seg[None, :] - disp
        w2 = np.einsum("ij,ij->i", w, w)
        # time of closest approach, as a fraction of this tick, clamped to the
        # leg itself: for a round that slows to a stop the closest point is an
        # end, not a projection
        t = np.clip(np.einsum("ij,ij->i", rel, w) / np.maximum(w2, 1e-9), 0.0, 1.0)
        gap = np.linalg.norm(rel - w * t[:, None], axis=1) - radii
        gap = np.where(pfaction != int(faction[pid]), gap, np.inf)
        k = int(np.argmin(gap))
        if not (gap[k] <= prox):
            continue                            # nobody inside the fuse radius
        f = float(t[k])
        burst = p0 + seg * f
        tid = int(puid[k])
        human = e.has(tid, "person")
        # the round goes off where it fused, so the blast, the splash and the
        # flash all belong to that point rather than to the end of the tick
        pos[pid] = burst
        # DEBUG, like every weapon-level line: a burst is an ordinary event and
        # this geometry dump only earns its place when the fuse is being hunted
        log.debug("prox fuse: %s round %d burst at (%.1f, %.1f, %.1f), %.2f m "
                 "off %s %d - %.1f m into a %.1f m leg from "
                 "(%.1f, %.1f, %.1f), round flew on to (%.1f, %.1f, %.1f)",
                 wd["name"], pid, burst[0], burst[1], burst[2], gap[k],
                 "person" if human else "missile", tid,
                 f * math.sqrt(l2), math.sqrt(l2), p0[0], p0[1], p0[2],
                 p1[0], p1[1], p1[2])
        # a missile that has already left the burst by the time `blasts` runs is
        # settled here, at the gap it was at when the fuse fired; a man stands
        # still and is left to the burst like he always was
        if not human:
            # the credit is the round's own (`proj_pid`, frozen at the muzzle),
            # not a lookup on the hull that fired it: that hull may be gone
            pl = int(e["proj_pid"][pid])
            by = (int(e["proj_name"][pid]), int(faction[pid]), int(e["proj_etype"][pid]))
            combat.splash(world, tid, defs.MOD_CHASSIS, wd, float(gap[k]), pl,
                          src=f"prox {float(gap[k]):.1f} m", by=by)
        # the round goes off where it fused: one entry point for "a round
        # detonates", so the flash, the burst entity and the credit all come
        # out the same way a direct hit's do
        projectiles.impact(world, (float(burst[0]), float(burst[1]), float(burst[2])),
                           wd, int(pid), skip=tid if not human else -1)
        e.destroy(pid)
