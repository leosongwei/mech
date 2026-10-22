"""Player intent -> ECS components (torso yaw, arm pitch, velocity, trigger)."""

from __future__ import annotations

import math

from .. import defs, mathutil as mu


def run(world, dt):
    e = world.ecs
    pos = e["pos"]
    vel = e["vel"]
    yaw = e["yaw"]
    pitch = e["pitch"]
    trigger = e["trigger"]
    etype = e["etype"]
    hp = e["hp"]
    speed = defs.UNIT_DEFS[defs.ETYPE_MECH]["speed"]

    for pl in world.players.values():
        eid = pl.mech
        if eid < 0 or not e.alive[eid]:
            continue
        if int(etype[eid]) != defs.ETYPE_MECH:
            continue

        # torso follows the mouse, arms follow mouse-Y
        yaw[eid] = pl.in_yaw
        pitch[eid] = max(-0.45, min(0.75, pl.in_pitch))

        mx, mz = pl.in_move
        mag = math.hypot(mx, mz)
        if mag > 0.001 and hp[eid, defs.MOD_CHASSIS] > 0.0:
            k = speed / max(mag, 1.0)
            vel[eid, 0] = mx * k
            vel[eid, 2] = mz * k
        else:
            vel[eid, 0] = 0.0
            vel[eid, 2] = 0.0

        t = 0
        if pl.fire[0] and hp[eid, defs.MOD_LEFT] > 0.0:
            t |= 1
        if pl.fire[1] and hp[eid, defs.MOD_RIGHT] > 0.0:
            t |= 2
        trigger[eid] = t
