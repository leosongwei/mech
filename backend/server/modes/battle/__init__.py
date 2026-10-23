"""Battle mode: the 16 km three-lane front, bases, towns and waves."""

from __future__ import annotations

import math

from ... import defs
from ... import terrain as terrain_mod
from ..base import Mode
from . import waves


class Battle(Mode):
    name = "battle"
    start_money = 600
    restart_respawn = 3.0

    def terrain(self, seed):
        return terrain_mod.generate(seed)

    def setup(self, world):
        world.wave = 0
        world.wave_timer = 8.0
        world.base_entity = {}
        for town in world.towns:
            town["owner"] = -1
            town["progress"] = 0.0
        self._spawn_bases(world)
        self._spawn_civilians(world)
        self._assign_home_towns(world)

    def post_systems(self):
        return [waves.run]

    # ------------------------------------------------------------------
    # battlefield
    # ------------------------------------------------------------------
    def _spawn_bases(self, world):
        for faction, (bx, bz) in world.terrain.bases.items():
            eid = world.spawn_unit(defs.ETYPE_BASE, faction, bx, bz)
            world.base_entity[faction] = eid
            # decorative guard turret mechs stay at home
            for _ in range(2):
                ang = world.rng.random() * math.tau
                world.spawn_unit(defs.ETYPE_MECH, faction,
                                 bx + math.cos(ang) * 55.0, bz + math.sin(ang) * 55.0,
                                 hp_scale=1.0, lane=0)

    def _spawn_civilians(self, world):
        for town in world.towns:
            for _ in range(world.rng.randint(1, 3)):
                ang = world.rng.random() * math.tau
                r = world.rng.uniform(15.0, 70.0)
                world.spawn_unit(defs.ETYPE_CIV, defs.FACTION_CIV,
                                 town["x"] + math.cos(ang) * r, town["z"] + math.sin(ang) * r)

    def _assign_home_towns(self, world):
        """Both sides start holding their closest towns: staging areas for
        reinforcements and forward respawn points."""
        for faction in (defs.FACTION_RED, defs.FACTION_BLUE):
            base = world.terrain.bases[faction]
            nearest = sorted(world.towns, key=lambda t: math.dist((t["x"], t["z"]), base))
            for town in nearest[:3]:
                town["owner"] = faction
                town["progress"] = 0.0
