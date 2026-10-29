"""Rules that differ between game modes.

A mode owns the map, what a fresh round spawns, which order the shared systems
run in and the handful of rules the shared code has to ask about (fixed side,
starting money, rearm anywhere...).  Mechanics that all modes share -
ballistics, movement, lock-on, damage, income - stay in ``systems/``; anything
that only makes sense in one mode lives in that mode's folder as its own
systems, so adding a third mode never means growing an ``if`` here.
"""

from __future__ import annotations

from typing import TYPE_CHECKING, Callable

from .. import defs

if TYPE_CHECKING:
    from ..terrain import Terrain
    from ..world import Player, World


class Mode:
    name: str = "mode"
    fixed_faction: int | None = None     # None: the lobby picks the side
    start_money: int = 600
    restart_respawn: float = 3.0         # seconds before players come back after a restart
    rearm_anywhere: bool = False
    smoke_cooldown: float | None = None  # None: defs.SMOKE_COOLDOWN

    def terrain(self, seed: int) -> Terrain:
        raise NotImplementedError

    def setup(self, world: World) -> None:
        """Build the battlefield for a fresh round (also used on restart)."""

    def pre_systems(self) -> list[Callable[[World, float], None]]:
        """Mode systems that run before lock-on / weapons / movement."""
        return []

    def post_systems(self) -> list[Callable[[World, float], None]]:
        """Mode systems that run after projectiles have been integrated."""
        return []

    def mech_spawn(self, world: World, player: Player) -> tuple[float, float, float] | None:
        """(x, z, yaw) for a (re)spawning player, or None for the default rule."""
        return None

    def respawn_delay(self, world: World, player: Player, x: float, z: float) -> float:
        """Seconds a pilot waits after a hull died at (x, z)."""
        return defs.RESPAWN_TIME
