"""Range mode: a 4 km test map, a fixed cross, passive APC targets."""

from __future__ import annotations

import math
from typing import TYPE_CHECKING, Callable

from ... import defs
from ... import terrain as terrain_mod
from ..base import Mode
from . import bunkers as bunkers_mod
from . import targets as targets_mod

if TYPE_CHECKING:
    from ...world import Player, World


class Range(Mode):
    name = "range"
    fixed_faction = defs.FACTION_RED
    start_money = 99999
    restart_respawn = 1.0
    rearm_anywhere = True
    # no smoke_cooldown override: the battle cooldown is short enough to
    # practise a pull here

    def terrain(self, seed: int) -> terrain_mod.Terrain:
        return terrain_mod.generate_range(seed)

    def setup(self, world: World) -> None:
        world.base_entity = {}
        world.targets = targets_mod.Targets(world)
        world.bunkers = bunkers_mod.place(world)

    def pre_systems(self) -> list[Callable[[World, float], None]]:
        return [targets_mod.run]

    def mech_spawn(self, world: World, player: Player) -> tuple[float, float, float]:
        x, z = terrain_mod.RANGE_SPAWN
        hx, hz, _ = terrain_mod.RANGE_HILL
        return x, z, math.atan2(hz - z, hx - x)
