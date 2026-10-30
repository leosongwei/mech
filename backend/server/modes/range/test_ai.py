"""Ground AI for the hulls a pilot drops on the range himself.

The range's own fixtures - the passive APC targets and the missile-defence post
- are driven by their own systems and never pick a ground fight; that is what
the place is for.  A hull spawned from the console (`debug_spawn`) is a
different thing: it is there to be *tested against*, so it fights with the
ordinary crew any AI hull gets (`systems/ai.run`) - which is how an AI mech's
missile arm gets flown without waiting for a battle.

`debug_spawn` tags such a hull `range_drone` (`Mode.console_spawn_extra`), and
that tag is the whole test: `ai.run` takes the list of hulls to drive, so the
mode's targets, its defence post and any hull a tool spawned for a scripted
shot stay exactly as passive as they were.
"""

from __future__ import annotations

from typing import TYPE_CHECKING

from ...systems import ai

if TYPE_CHECKING:
    from ...world import World


def run(world: World, dt: float) -> None:
    e = world.ecs
    hulls = [int(eid) for eid in world.movable()
             if e.has(eid, "range_drone") and int(e["owner"][eid]) < 0]
    if hulls:
        ai.run(world, dt, hulls=hulls)
