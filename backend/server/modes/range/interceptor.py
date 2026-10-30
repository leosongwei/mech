"""A missile-defence post on the range: one AI mech that shoots down what is
fired at it and does nothing else.

The range's rule is that nothing there shoots back, so this hull runs the
*missile* half of the AI on its own (`systems/ai.intercept`) and none of the
ground half: it never acquires a target, never advances along a lane and never
touches the player.  What it does have is a 30 mm with its 4 m airburst fuse and
a mech's radar (`defs.UNIT_DEFS[ETYPE_MECH]["radar_strength"]`), which is
exactly what an incoming SACLOS, top-attack, S-8 or RPG has to get past.

It is the testbed for the radar/intercept numbers: fire each round at it from
each approach and the server log says what happened (a `prox fuse:` line for a
burst, `missile N took ...` for the health a missile lost, `intercept: ...` for
one that came down).
"""

from __future__ import annotations

import math
from typing import TYPE_CHECKING

from ... import defs
from ...systems import ai

if TYPE_CHECKING:
    from ...world import World

# stand-off post: on the plain, ~870 m north-west of the firing line, so every
# weapon in the game reaches it with room to be intercepted on the way in
INTERCEPTOR_POST = (1550.0, 2250.0)
RESPAWN_AFTER = 5.0        # seconds before a shot-down post is stood back up
# a cannon and nothing else: the hull is here to intercept, not to fight
LOADOUT = (defs.WID_AC30, defs.WID_NONE)


class Interceptor:
    def __init__(self, world: World) -> None:
        self.eid = -1
        self.t = 0.0
        self.spawn(world)

    def spawn(self, world: World) -> int:
        x, z = INTERCEPTOR_POST
        eid = world.spawn_unit(defs.ETYPE_MECH, defs.FACTION_BLUE, x, z,
                               weapons=list(LOADOUT))
        # face the firing line so the crew's sight starts where the shooting is
        sx, sz = world.terrain.bases[defs.FACTION_RED]
        yaw = math.atan2(sz - z, sx - x)
        e = world.ecs
        e["yaw"][eid] = yaw
        e["leg_yaw"][eid] = yaw
        e["sight_yaw"][eid] = yaw
        self.eid = eid
        self.t = 0.0
        return eid

    def update(self, world: World, dt: float) -> None:
        e = world.ecs
        # the same id-reuse trap as the targets: a destroyed hull's id is taken
        # by the blast it left behind, and "it is alive" would then mean "a burst
        # entity stands where the post used to be" - it would never respawn
        if self.eid >= 0 and not (e.alive[self.eid] and e.has(self.eid, "moves")):
            self.eid = -1                  # shot to pieces: post it again shortly
            self.t = RESPAWN_AFTER
        if self.eid < 0:
            self.t = float(self.t) - dt
            if self.t <= 0.0:
                self.spawn(world)
        ai.intercept(world, dt)


def run(world: World, dt: float) -> None:
    world.interceptor.update(world, dt)
