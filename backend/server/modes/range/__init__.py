"""Range mode: a 4 km test map, a fixed cross, passive APC targets, one
missile-defence post that shoots down what is fired at it."""

from __future__ import annotations

import math
from typing import TYPE_CHECKING, Callable

from ... import defs
from ... import terrain as terrain_mod
from ..base import Mode
from . import bunkers as bunkers_mod
from . import interceptor as interceptor_mod
from . import targets as targets_mod
from . import test_ai as test_ai_mod

if TYPE_CHECKING:
    from ...world import Player, World


class Range(Mode):
    name = "range"
    # The side is the lobby's choice, not the mode's: two browsers on one range
    # is how the whole *receiving* end gets tested - one pilot fires a missile,
    # the other watches his own radar light up and shoots it down with the
    # 30 mm.  AUTO still balances the sides, so a second pilot lands opposite
    # the first by itself.  The targets and the defence post are BLUE, so a
    # blue pilot finds them friendly and red is the side that shoots at them.
    fixed_faction = None
    start_money = 99999
    restart_respawn = 1.0
    rearm_anywhere = True
    # a hull from the console is the one thing here that fights: the tag is what
    # `test_ai` drives (see the module), and the mode's own targets and post are
    # never tagged, so they stay as passive as they were
    console_spawn_extra = ("range_drone",)
    # no smoke_cooldown override: the battle cooldown is short enough to
    # practise a pull here

    def terrain(self, seed: int) -> terrain_mod.Terrain:
        return terrain_mod.generate_range(seed)

    def setup(self, world: World) -> None:
        world.base_entity = {}
        world.targets = targets_mod.Targets(world)
        world.bunkers = bunkers_mod.place(world)
        # one AI hull that does nothing but shoot missiles down: the range's
        # place to measure the radar and the fuse against a real approach
        world.interceptor = interceptor_mod.Interceptor(world)

    def pre_systems(self) -> list[Callable[[World, float], None]]:
        return [targets_mod.run, interceptor_mod.run, test_ai_mod.run]

    def mech_spawn(self, world: World, player: Player) -> tuple[float, float, float]:
        x, z = terrain_mod.RANGE_SPAWN
        hx, hz, _ = terrain_mod.RANGE_HILL
        # one berth per pilot round the firing line, so two browsers do not
        # materialise inside each other on the one spawn point
        a = 0.9 * (player.pid % 6)
        x += math.cos(a) * 18.0
        z += math.sin(a) * 18.0
        return x, z, math.atan2(hz - z, hx - x)
