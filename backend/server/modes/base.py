"""Rules that differ between game modes.

A mode owns the map, what a fresh round spawns, which order the shared systems
run in and the handful of rules the shared code has to ask about (fixed side,
starting money, rearm anywhere...).  Mechanics that all modes share -
ballistics, movement, lock-on, damage, income - stay in ``systems/``; anything
that only makes sense in one mode lives in that mode's folder as its own
systems, so adding a third mode never means growing an ``if`` here.
"""

from __future__ import annotations


class Mode:
    name = "mode"
    fixed_faction = None        # None: the lobby picks the side
    start_money = 600
    restart_respawn = 3.0       # seconds before players come back after a restart
    rearm_anywhere = False
    smoke_cooldown = None       # None: defs.SMOKE_COOLDOWN

    def terrain(self, seed):
        raise NotImplementedError

    def setup(self, world):
        """Build the battlefield for a fresh round (also used on restart)."""

    def pre_systems(self):
        """Mode systems that run before lock-on / weapons / movement."""
        return []

    def post_systems(self):
        """Mode systems that run after projectiles have been integrated."""
        return []

    def mech_spawn(self, world, player):
        """(x, z, yaw) for a (re)spawning player, or None for the default rule."""
        return None
