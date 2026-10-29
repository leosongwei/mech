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
    from ...world import Player, World


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
    # players
    # ------------------------------------------------------------------
    def mech_spawn(self, world: World, player: Player) -> tuple[float, float, float]:
        """Where a pilot's machine appears.

        After a death: one town back down the lane the hull died on, the base
        if there is none behind it.  On the first deploy: the most advanced
        town still connected to the base, so a fresh pilot steps into the fight
        rather than walking 5 km out of the gate.
        """
        if player.death_pos is None:
            town = self._lead_town(world, player.faction)
        else:
            town = waves.respawn_town(world, player.faction, *player.death_pos)
        if town is None:
            x, z = world.base_spawn(player.faction)
        else:
            # a ring around the town, never its centre: the middle of a town is
            # exactly where the buildings and the plaza crowd are
            x, z = waves.clear_near(world, town["x"], town["z"], 40.0, 70.0)
        ex, ez = world.terrain.bases[1 - player.faction]
        return x, z, math.atan2(ez - z, ex - x)

    def _lead_town(self, world: World, faction: int) -> dict | None:
        """The front town nearest the enemy base on any lane, or None when
        every one of them is a stone's throw from their guns (900 m)."""
        enemy = world.terrain.bases[1 - faction]
        best, best_d = None, 1e18
        for lane in range(len(world.lanes)):
            town = waves.front_town(world, faction, lane)
            if town is None:
                continue
            d = math.dist((town["x"], town["z"]), enemy)
            if d < best_d:
                best, best_d = town, d
        return best if best is not None and best_d > 900.0 else None

    def respawn_delay(self, world: World, player: Player, x: float, z: float) -> float:
        """The wait is the walk back: long at home, short on the enemy's step.

        Interpolated on how far from the enemy command base the hull died, so a
        death on their doorstep is a 3 s turn-round and one at your own base is
        40 s without a machine.
        """
        home = world.terrain.bases[player.faction]
        front = world.terrain.bases[1 - player.faction]
        span = max(1.0, math.dist(home, front))
        k = min(1.0, math.dist((x, z), front) / span)
        return defs.RESPAWN_AT_FRONT + (defs.RESPAWN_AT_HOME - defs.RESPAWN_AT_FRONT) * k

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
