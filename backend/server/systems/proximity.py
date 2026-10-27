"""Proximity fuse: the airburst rounds go off on *people*, not on the ground.

A cannon round covers 36 m in one tick, so a distance test at the tick
boundaries would step clean over a rifleman standing 4 m off the path.  The
fuse therefore solves the leg the round actually flew this tick
(``proj_prev`` -> ``pos``) instead of sampling it: it takes the point of that
leg where the round came closest to an enemy, and if that point is inside the
fuse radius the round goes off *there* - not at the end of the tick, 36 m past
him, which is where an impact fuse would have planted it in the dirt.

Only ``person`` entities may set it off (riflemen, AT riflemen, civilians).
Armour is deliberately not tagged: an APC 4 m from the path leaves the fuse
cold and waits for the impact fuse, which is what keeps the cannon a
penetration weapon against vehicles and an area weapon against infantry.
"""

from __future__ import annotations

import logging
import math
from typing import TYPE_CHECKING

import numpy as np

from .. import defs, mathutil as mu
from . import projectiles

if TYPE_CHECKING:
    from ..world import World

log = logging.getLogger("mech.prox")


def _person_spheres(world: World, people: np.ndarray
                    ) -> tuple[np.ndarray, np.ndarray, np.ndarray, np.ndarray]:
    """Every hit sphere of every man on the field, as flat arrays."""
    e = world.ecs
    pos = e["pos"]
    yaw = e["yaw"]
    etype = e["etype"]
    faction = e["faction"]
    c = []
    r = []
    f = []
    u = []
    for uid in people:
        for _mod, centre, rad in mu.unit_spheres(int(etype[uid]), pos[uid], float(yaw[uid])):
            c.append(centre)
            r.append(rad)
            f.append(int(faction[uid]))
            u.append(int(uid))
    return (np.asarray(c, np.float64).reshape(-1, 3), np.asarray(r, np.float64),
            np.asarray(f, np.int32), np.asarray(u, np.int32))


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
    if people.size == 0:
        return

    pos = e["pos"]
    prev = e["proj_prev"]
    faction = e["faction"]
    centres, radii, pfaction, puid = _person_spheres(world, people)

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
        # foot of the perpendicular from each man onto the leg, clamped to the
        # leg itself: for a round that slows to a stop the closest point is an
        # end, not a projection
        t = np.clip((rel @ seg) / l2, 0.0, 1.0)
        gap = np.linalg.norm(rel - seg[None, :] * t[:, None], axis=1) - radii
        gap = np.where(pfaction != int(faction[pid]), gap, np.inf)
        k = int(np.argmin(gap))
        if not (gap[k] <= prox):
            continue                            # nobody inside the fuse radius
        f = float(t[k])
        burst = p0 + seg * f
        # the round goes off where it fused, so the blast, the splash and the
        # flash all belong to that point rather than to the end of the tick
        pos[pid] = burst
        log.info("prox fuse: %s round %d burst at (%.1f, %.1f, %.1f), %.2f m "
                 "off person %d - %.1f m into a %.1f m leg from "
                 "(%.1f, %.1f, %.1f), round flew on to (%.1f, %.1f, %.1f)",
                 wd["name"], pid, burst[0], burst[1], burst[2], gap[k], int(puid[k]),
                 f * math.sqrt(l2), math.sqrt(l2), p0[0], p0[1], p0[2],
                 p1[0], p1[1], p1[2])
        projectiles._impact(world, (float(burst[0]), float(burst[1]), float(burst[2])),
                            wd, int(pid))
        e.destroy(pid)
