"""Range mode: a 4 km test map, a fixed cross, passive APC targets."""

from __future__ import annotations

import math

from ... import defs
from ... import terrain as terrain_mod
from ..base import Mode
from . import targets as targets_mod


class Range(Mode):
    name = "range"
    fixed_faction = defs.FACTION_RED
    start_money = 99999
    restart_respawn = 1.0
    rearm_anywhere = True

    def terrain(self, seed):
        return terrain_mod.generate_range(seed)

    def setup(self, world):
        world.base_entity = {}
        world.targets = targets_mod.Targets(world)

    def pre_systems(self):
        return [targets_mod.run]

    def mech_spawn(self, world, player):
        x, z = terrain_mod.RANGE_SPAWN
        hx, hz, _ = terrain_mod.RANGE_HILL
        return x, z, math.atan2(hz - z, hx - x)
