"""Battle mode: the 16 km three-lane front, bases, towns and waves."""

from __future__ import annotations

import math
from typing import TYPE_CHECKING, Callable

from ... import defs
from ... import terrain as terrain_mod
from ..base import Mode
from ...systems import ai
from . import raids, waves

if TYPE_CHECKING:
    from ...world import World


class Battle(Mode):
    name = "battle"
    start_money = 600
    restart_respawn = 3.0

    def terrain(self, seed: int) -> terrain_mod.Terrain:
        return terrain_mod.generate(seed)

    def setup(self, world: World) -> None:
        world.wave = 0
        world.wave_timer = 8.0
        world.base_entity = {}
        for town in world.towns:
            town["owner"] = -1
            town["progress"] = 0.0
        waves.bind(world)
        raids.bind(world)
        self._spawn_bases(world)
        self._spawn_civilians(world)
        self._assign_home_towns(world)

    def pre_systems(self) -> list[Callable[[World, float], None]]:
        # the battlefield AI drives every non-player hull: it wants to run
        # before lock-on/weapons/movement so its velocity and trigger land in
        # the same tick.  The raiding parties steer themselves first and shoot
        # through `ai.run` after - that system owns every crew's guns, including
        # the parties' (`ai_state`, `raider`).
        return [raids.run, ai.run]

    def post_systems(self) -> list[Callable[[World, float], None]]:
        return [waves.run]

    # ------------------------------------------------------------------
    # battlefield
    # ------------------------------------------------------------------
    def _spawn_bases(self, world: World) -> None:
        for faction, (bx, bz) in world.terrain.bases.items():
            eid = world.spawn_unit(defs.ETYPE_BASE, faction, bx, bz)
            world.base_entity[faction] = eid
            # decorative guard turret mechs stay at home
            for _ in range(2):
                ang = world.rng.random() * math.tau
                world.spawn_unit(defs.ETYPE_MECH, faction,
                                 bx + math.cos(ang) * 55.0, bz + math.sin(ang) * 55.0,
                                 hp_scale=1.0, lane=0)

    def _spawn_civilians(self, world: World) -> None:
        for town in world.towns:
            for _ in range(world.rng.randint(1, 3)):
                ang = world.rng.random() * math.tau
                r = world.rng.uniform(15.0, 70.0)
                world.spawn_unit(defs.ETYPE_CIV, defs.FACTION_CIV,
                                 town["x"] + math.cos(ang) * r, town["z"] + math.sin(ang) * r)

    def _assign_home_towns(self, world: World) -> None:
        """Both sides start holding their closest towns: staging areas for
        reinforcements and forward respawn points."""
        for faction in (defs.FACTION_RED, defs.FACTION_BLUE):
            base = world.terrain.bases[faction]
            nearest = sorted(world.towns, key=lambda t: math.dist((t["x"], t["z"]), base))
            for town in nearest[:3]:
                town["owner"] = faction
                town["progress"] = 0.0
