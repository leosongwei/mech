"""Income and player respawns - the two economy rules every mode shares."""

from __future__ import annotations

from typing import TYPE_CHECKING

if TYPE_CHECKING:
    from ..world import World

INCOME = 4.0        # $/s


def run(world: World, dt: float) -> None:
    if world.winner >= 0:
        return
    for pl in world.players.values():
        pl.money += INCOME * dt
        if pl.mech >= 0 and world.ecs.alive[pl.mech]:
            continue
        pl.respawn -= dt
        if pl.respawn <= 0.0:
            world.spawn_mech(pl)
            world.emit("respawn", pid=pl.pid)
