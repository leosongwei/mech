"""Wreck and corpse cleanup.

A destroyed unit leaves a hulk behind (``systems/mortality.py``): infantry topple
and vanish after ``CORPSE_TIME``, vehicles and mechs keep smoking for
``WRECK_TIME``.  Both are inert - they are not part of ``world.units()``, so
nothing can target, hit or capture with them.
"""

from __future__ import annotations

from typing import TYPE_CHECKING

if TYPE_CHECKING:
    from ..world import World


def run(world: World, dt: float) -> None:
    e = world.ecs
    ids = e.query("wreck_t")
    if ids.size == 0:
        return
    t = e["wreck_t"]
    for eid in ids:
        t[eid] = float(t[eid]) - dt
        if t[eid] <= 0.0:
            e.destroy(eid)
