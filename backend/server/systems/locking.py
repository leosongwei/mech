"""Infrared lock-on: the server owns the lock state, the client mirrors it.

The seeker only sees what sits inside the reticle circle: a target has to
stay within 5% of the view width (``defs.LOCK_FRAC``) for ``lock_time``
seconds, and the lock drops the moment it leaves the circle - there is no
hysteresis to hold a strafing hull.  Infantry is too small and too cold for
the seeker: only vehicles and mechs can be locked.

The circle is a fixed fraction of the screen, so the angular cone shrinks as
the optics zoom in; the pilot sends the zoom and the viewport aspect with
every input message and the server keeps both numbers the same as the client.
"""

from __future__ import annotations

import math
from typing import TYPE_CHECKING

import numpy as np

from .. import defs, mathutil as mu

if TYPE_CHECKING:
    from ..world import Player, World

MIN_ZOOM = 1.0
MAX_ZOOM = 20.0
MIN_ASPECT = 0.4
MAX_ASPECT = 4.0
MIN_DIST = 20.0

def cone_of(pl: Player) -> float:
    """Angular radius of the reticle circle, in radians."""
    zoom = min(max(float(getattr(pl, "zoom", 1.0)), MIN_ZOOM), MAX_ZOOM)
    aspect = min(max(float(getattr(pl, "aspect", 16.0 / 9.0)), MIN_ASPECT), MAX_ASPECT)
    fov = defs.BASE_FOV / zoom
    return math.atan(math.tan(fov * 0.5) * defs.LOCK_FRAC * aspect)


def run(world: World, dt: float) -> None:
    e = world.ecs
    pos = e["pos"]
    yaw = e["yaw"]
    pitch = e["pitch"]
    faction = e["faction"]
    hp = e["hp"]
    ids = world.lockable_units()

    for pl in world.players.values():
        eid = pl.mech
        if eid < 0 or not e.alive[eid]:
            continue
        wd = None
        for arm in (0, 1):
            w = int(e["weapon"][eid, arm])
            if w and defs.WEAPONS[w]["lock"] and hp[eid, defs.MOD_LEFT if arm == 0 else defs.MOD_RIGHT] > 0:
                wd = defs.WEAPONS[w]
        if wd is None:
            pl.lock_target = -1
            pl.lock_progress = 0.0
            continue

        origin = (float(pos[eid, 0]), float(pos[eid, 1]) + defs.MECH_EYE, float(pos[eid, 2]))
        d = np.asarray(mu.dir_from(float(yaw[eid]), float(pitch[eid])), np.float32)
        cone = cone_of(pl)

        def angle_to(tid: int) -> tuple[float, float]:
            tp = pos[tid]
            v = np.array([tp[0] - origin[0], tp[1] + 1.5 - origin[1], tp[2] - origin[2]], np.float32)
            n = float(np.linalg.norm(v))
            if n < 1e-3:
                return 99.0, 0.0
            return float(np.arccos(np.clip(float(v @ d) / n, -1.0, 1.0))), n

        tracked = pl.lock_target
        if tracked >= 0:
            # the hull left the circle (or died / turned friendly): drop it
            if (not e.alive[tracked]) or int(faction[tracked]) == int(faction[eid]) \
                    or not e.has(tracked, "lockable"):
                tracked = -1
            else:
                ang, dist = angle_to(tracked)
                if ang > cone or dist > wd["range"]:
                    tracked = -1
            if tracked < 0:
                pl.lock_target = -1
                pl.lock_progress = 0.0

        if tracked < 0:
            best = -1
            best_ang = cone
            if ids.size:
                v = (pos[ids] - np.asarray(origin, np.float32)).astype(np.float32)
                dist = np.linalg.norm(v, axis=1)
                ok = (dist > MIN_DIST) & (dist < wd["range"]) & (faction[ids] != int(faction[eid]))
                if ok.any():
                    idx = np.nonzero(ok)[0]
                    vv = v[idx] / dist[idx, None]
                    ang = np.arccos(np.clip(vv @ d, -1.0, 1.0))
                    j = int(np.argmin(ang))
                    if ang[j] < best_ang:
                        best = int(ids[idx[j]])
            if best >= 0:
                pl.lock_target = best
                pl.lock_progress = 0.0
            else:
                pl.lock_progress = max(0.0, pl.lock_progress - dt * 2.0)
                continue

        pl.lock_progress = min(1.0, pl.lock_progress + dt / max(wd["lock_time"], 0.05))
