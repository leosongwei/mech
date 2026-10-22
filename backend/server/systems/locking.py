"""Infrared lock-on: the server owns the lock state, the client mirrors it.

A player keeps the sight on a target for ``lock_time`` seconds; the progress is
streamed back with the snapshot so the HUD can play the interrupted / steady
tone and draw the lock box.  A little hysteresis keeps the current target once
it is being tracked, so two nearby vehicles cannot make the tone stutter.
"""

from __future__ import annotations

import numpy as np

from .. import defs, mathutil as mu

CONE = 0.04            # radians off the sight line (~2.3 degrees)
KEEP_CONE = 0.10       # tracked target may drift this far before losing lock


def run(world, dt):
    e = world.ecs
    pos = e["pos"]
    yaw = e["yaw"]
    pitch = e["pitch"]
    faction = e["faction"]
    etype = e["etype"]
    ids = world.combat_units()

    for pl in world.players.values():
        eid = pl.mech
        if eid < 0 or not e.alive[eid]:
            continue
        wd = None
        for arm in (0, 1):
            w = int(e["weapon"][eid, arm])
            if w and defs.WEAPONS[w]["lock"] and e["hp"][eid, defs.MOD_LEFT if arm == 0 else defs.MOD_RIGHT] > 0:
                wd = defs.WEAPONS[w]
        if wd is None:
            pl.lock_target = -1
            pl.lock_progress = 0.0
            continue

        origin = (float(pos[eid, 0]), float(pos[eid, 1]) + defs.MECH_EYE, float(pos[eid, 2]))
        d = np.asarray(mu.dir_from(float(yaw[eid]), float(pitch[eid])), np.float32)

        def angle_to(tid):
            tp = pos[tid]
            v = np.array([tp[0] - origin[0], tp[1] + 1.5 - origin[1], tp[2] - origin[2]], np.float32)
            n = float(np.linalg.norm(v))
            if n < 1e-3:
                return 99.0, 0.0
            return float(np.arccos(np.clip(float(v @ d) / n, -1.0, 1.0))), n

        tracked = pl.lock_target
        if tracked >= 0:
            if (not e.alive[tracked]) or int(faction[tracked]) == int(faction[eid]):
                tracked = -1
                pl.lock_target = -1
                pl.lock_progress = 0.0
            else:
                ang, dist = angle_to(tracked)
                if ang > KEEP_CONE or dist > wd["range"]:
                    tracked = -1
                    pl.lock_target = -1
                    pl.lock_progress = 0.0

        if tracked < 0:
            best = -1
            if ids.size:
                v = (pos[ids] - np.asarray(origin, np.float32)).astype(np.float32)
                dist = np.linalg.norm(v, axis=1)
                ok = (dist > 20.0) & (dist < wd["range"]) & (faction[ids] != int(faction[eid]))
                et = etype[ids]
                ok &= (et != defs.ETYPE_CIV) & (et != defs.ETYPE_BASE)
                if ok.any():
                    idx = np.nonzero(ok)[0]
                    vv = v[idx] / dist[idx, None]
                    ang = np.arccos(np.clip(vv @ d, -1.0, 1.0))
                    j = int(np.argmin(ang))
                    if ang[j] < CONE:
                        best = int(ids[idx[j]])
            if best >= 0:
                pl.lock_target = best
                pl.lock_progress = 0.0
            else:
                pl.lock_progress = max(0.0, pl.lock_progress - dt * 2.0)
                continue

        pl.lock_progress = min(1.0, pl.lock_progress + dt / max(wd["lock_time"], 0.05))
