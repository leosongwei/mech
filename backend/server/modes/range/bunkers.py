"""Indestructible bunkers on the range ridge lines: fire-control references.

Each one sits on a local high point, so from the firing line its silhouette
stands against the sky.  A round that lands on it proves the zeroing solution;
one that falls short or sails over is read against the slope behind it.  The
concrete takes no damage - the range is for measuring, not for scoring.
"""

from __future__ import annotations

import math

from ... import defs

# search boxes (x, z) for the local high point, metres on the map
BUNKER_ANCHORS = (
    (1000.0, 980.0),     # main summit
    (1500.0, 1250.0),    # south-east ridge, the near flank facing the cross
    (600.0, 1750.0),     # west ridge
    (700.0, 2000.0),     # southern spur
    (1400.0, 400.0),     # north shoulder
)

SEARCH = 160.0           # half width of the search box around an anchor
GRID = 10.0              # resolution of that search


def place(world):
    """Spawn one bunker per anchor and return their entity ids."""
    e = world.ecs
    terrain = world.terrain
    sx, sz = terrain.bases[defs.FACTION_RED]
    ids = []
    for ax, az in BUNKER_ANCHORS:
        x, z = _high_point(terrain, ax, az)
        eid = world.spawn_unit(defs.ETYPE_BUNKER, defs.FACTION_NATURE, x, z)
        yaw = math.atan2(sz - z, sx - x)          # face the firing line
        e["yaw"][eid] = yaw
        e["turret_yaw"][eid] = yaw
        e["leg_yaw"][eid] = yaw
        ids.append(eid)
    return ids


def _high_point(terrain, ax, az):
    """Highest tile in the anchor's search box: the crest it should sit on."""
    best_h = -1.0
    best = (ax, az)
    n = int(SEARCH / GRID)
    for i in range(-n, n + 1):
        x = ax + i * GRID
        for j in range(-n, n + 1):
            z = az + j * GRID
            h = terrain.height_at(x, z)
            if h > best_h:
                best_h = h
                best = (x, z)
    return best
