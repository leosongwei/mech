"""Ground movement: integrates velocity, follows the terrain, drives the legs."""

from __future__ import annotations

import math

from .. import defs, mathutil as mu
from ..terrain import TILE


def _blocked(terrain, x, z, rad) -> bool:
    return (terrain.blocked(x, z) or terrain.blocked(x + rad, z) or
            terrain.blocked(x - rad, z) or terrain.blocked(x, z + rad) or
            terrain.blocked(x, z - rad))


def run(world, dt):
    e = world.ecs
    ids = world.units()
    if ids.size == 0:
        return
    pos = e["pos"]
    vel = e["vel"]
    etype = e["etype"]
    leg = e["leg_yaw"]
    hp = e["hp"]
    terrain = world.terrain
    maxx = terrain.w * TILE - 2 * TILE
    maxz = terrain.d * TILE - 2 * TILE

    for eid in ids:
        et = int(etype[eid])
        x = float(pos[eid, 0])
        z = float(pos[eid, 2])
        if et == defs.ETYPE_BASE:
            pos[eid, 1] = terrain.height_at(x, z)
            continue

        vx = float(vel[eid, 0])
        vz = float(vel[eid, 2])
        if abs(vx) > 0.01 or abs(vz) > 0.01:
            if et == defs.ETYPE_MECH:
                align = math.cos(mu.wrap_pi(math.atan2(vz, vx) - float(leg[eid])))
                f = 0.45 + 0.55 * max(0.0, align)
                if hp[eid, defs.MOD_CHASSIS] <= 0.0:
                    f *= 0.2
                vx *= f
                vz *= f
            nx = min(max(x + vx * dt, TILE), maxx)
            nz = min(max(z + vz * dt, TILE), maxz)
            rad = defs.UNIT_DEFS[et]["radius"] * 0.6
            if _blocked(terrain, nx, nz, rad):
                if not _blocked(terrain, nx, z, rad):
                    nz = z
                elif not _blocked(terrain, x, nz, rad):
                    nx = x
                else:
                    nx, nz = x, z
            pos[eid, 0] = nx
            pos[eid, 2] = nz
            x, z = nx, nz
        pos[eid, 1] = terrain.height_at(x, z)
