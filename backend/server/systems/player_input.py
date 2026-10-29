"""Player intent -> ECS components (sight, velocity, trigger).

The pilot only ever asks for two things: where to *look* and where to drive.
Looking writes the sight (`sight_yaw` / `sight_pitch`); the hull and the arms
slew after it at the machine's own rates in `systems/gunnery.py` and are the
only thing a weapon or a camera reads.
"""

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
    sight_yaw = e["sight_yaw"]
    sight_pitch = e["sight_pitch"]
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

        # the sight answers the mouse instantly; the hull and the arms are
        # what has to catch up with it (systems/gunnery.py), and the arms stop
        # at their own elevation limits on the way
        sight_yaw[eid] = pl.in_yaw
        sight_pitch[eid] = pl.in_pitch

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
