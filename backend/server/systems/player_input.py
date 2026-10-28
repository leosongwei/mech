"""Player intent -> ECS components (torso yaw, arm pitch, velocity, trigger)."""

from __future__ import annotations

import math
from typing import TYPE_CHECKING

from .. import defs, mathutil as mu

if TYPE_CHECKING:
    from ..world import World


def run(world: World, dt: float) -> None:
    e = world.ecs
    pos = e["pos"]
    vel = e["vel"]
    yaw = e["yaw"]
    pitch = e["pitch"]
    trigger = e["trigger"]
    etype = e["etype"]
    hp = e["hp"]
    work = e["work_remaining_time"]
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

        # a rearm job (systems/rearm.py) holds the machine where it stands and
        # kills the triggers: the pilot may still look around, nothing else
        busy = work[eid] > 0.0

        mx, mz = pl.in_move
        mag = math.hypot(mx, mz) if not busy else 0.0
        if mag > 0.001 and hp[eid, defs.MOD_CHASSIS] > 0.0:
            k = speed / max(mag, 1.0)
            vel[eid, 0] = mx * k
            vel[eid, 2] = mz * k
        else:
            vel[eid, 0] = 0.0
            vel[eid, 2] = 0.0

        t = 0
        if not busy and pl.fire[0] and hp[eid, defs.MOD_LEFT] > 0.0:
            t |= 1
        if not busy and pl.fire[1] and hp[eid, defs.MOD_RIGHT] > 0.0:
            t |= 2
        trigger[eid] = t
