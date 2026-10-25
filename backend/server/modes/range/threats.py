"""Range threats: one IR top-attack missile every few seconds.

The range otherwise never shoots back, so this is the only way to meet a real
seeker: every ``THREAT_PERIOD`` seconds a kind-3 round is launched from the
hill top and locked onto the player's mech.  It is an ordinary projectile -
it climbs, breaks over onto the roof, and goes dumb the moment the smoke
blocks its line of sight (``systems/projectiles.py``), which is exactly the
thing the hot smoke exists to test.
"""

from __future__ import annotations

import logging
import math
from typing import TYPE_CHECKING

from ... import components, defs
from ... import mathutil as mu
from ... import terrain as terrain_mod

if TYPE_CHECKING:
    from ...world import World

log = logging.getLogger("mech.range")

THREAT_PERIOD = 10.0        # seconds between launches
THREAT_ALT = 8.0            # metres above the hill top the round leaves from
THREAT_PITCH = 0.35         # soft launch, points the seeker at the sky


def run(world: World, dt: float) -> None:
    world.threats.update(world, dt)


class Threats:
    def __init__(self, world: World) -> None:
        self.timer = THREAT_PERIOD * 0.5    # first one comes early
        self.count = 0

    def update(self, world: World, dt: float) -> None:
        self.timer -= dt
        if self.timer > 0.0:
            return
        self.timer += THREAT_PERIOD
        e = world.ecs
        for pl in world.players.values():
            eid = pl.mech
            if eid >= 0 and e.alive[eid]:
                self.launch(world, eid)

    def launch(self, world: World, eid: int) -> None:
        """Throw one missile at the mech `eid` from the hill top."""
        e = world.ecs
        hx, hz, peak = terrain_mod.RANGE_HILL
        origin = (hx, peak + THREAT_ALT, hz)
        tp = e["pos"][eid]
        yaw = math.atan2(float(tp[2]) - hz, float(tp[0]) - hx)
        d = mu.dir_from(yaw, THREAT_PITCH)
        wid = defs.WID_TOPATTACK
        wd = defs.WEAPONS[wid]
        pid = e.create()
        for c in components.PROJ_COMPS:
            e.add_comp(pid, c)
        e["pos"][pid] = origin
        e["vel"][pid] = (d[0] * wd["vel"], d[1] * wd["vel"], d[2] * wd["vel"])
        e["faction"][pid] = defs.FACTION_BLUE
        e["proj_kind"][pid] = 3
        e["proj_wid"][pid] = wid
        e["proj_life"][pid] = wd["life"]
        e["proj_src"][pid] = -1                 # nobody to credit the kill to
        e["proj_age"][pid] = 0.0
        e["proj_seed"][pid] = world.rng.random()
        e["target"][pid] = eid
        self.count += 1
        log.info("threat #%d: top-attack missile %d launched from the hill at "
                 "mech %d, %.0f m out", self.count, pid, eid,
                 math.dist(origin, (float(tp[0]), float(tp[1]), float(tp[2]))))
        world.emit("shot", x=origin[0], y=origin[1], z=origin[2], wid=wid,
                   eid=-1, arm=0, proj=int(pid))
