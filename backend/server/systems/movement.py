"""Ground movement: integrates velocity, follows the terrain, drives the legs."""

from __future__ import annotations

import math
from typing import TYPE_CHECKING

import numpy as np

from .. import defs, mathutil as mu
from ..terrain import TILE

if TYPE_CHECKING:
    from ..terrain import Terrain
    from ..world import World


def _blocked(terrain: Terrain, x: float, z: float, rad: float) -> bool:
    return (terrain.blocked(x, z) or terrain.blocked(x + rad, z) or
            terrain.blocked(x - rad, z) or terrain.blocked(x, z + rad) or
            terrain.blocked(x, z - rad) or
            # trunks are thinner than those samples: distance tested
            terrain.trunk_blocked(x, z, rad))


def _turn_legs(world: World, dt: float) -> None:
    """Swing every unit's legs/steering towards the direction it was told to go.

    Locomotion, not AI: the hull turns its own legs in every mode, which is why
    this lives here and not in the AI system.  Nothing here looks at another
    entity, so the whole archetype turns in one pass.
    """
    for g in world.ecs.groups("moves", "vel", "leg_yaw", "etype"):
        n = g.n
        vel = g.bufs["vel"]
        leg = g.bufs["leg_yaw"][:n]
        etype = g.bufs["etype"][:n]
        vx, vz = vel[:n, 0], vel[:n, 2]
        moving = (np.abs(vx) >= 0.05) | (np.abs(vz) >= 0.05)
        if not moving.any():
            continue
        want = np.arctan2(vz, vx)
        rate = np.where(etype == defs.ETYPE_MECH, 2.6, 6.0) * np.float32(dt)
        turned = mu.turn_toward_arr(leg, want, rate)
        leg[moving] = turned[moving]


def run(world: World, dt: float) -> None:
    e = world.ecs
    if e.count("moves") == 0:
        return
    _turn_legs(world, dt)

    terrain = world.terrain
    maxx = terrain.w * TILE - 2 * TILE
    maxz = terrain.d * TILE - 2 * TILE
    # `moves` is the query; every group below owns the whole unit bundle, so
    # the buffers are taken once per group and the loop only walks rows
    for g in e.groups("moves", "pos", "vel", "drive", "leg_yaw", "hp", "etype"):
        n = g.n
        pos = g.bufs["pos"]
        vel = g.bufs["vel"]
        drive = g.bufs["drive"]
        leg = g.bufs["leg_yaw"]
        hp = g.bufs["hp"]
        etype = g.bufs["etype"]
        for row in range(n):
            et = int(etype[row])
            x = float(pos[row, 0])
            z = float(pos[row, 2])
            vx = float(vel[row, 0])
            vz = float(vel[row, 2])
            if et == defs.ETYPE_MECH:
                # a mech rolls up to the speed it was asked for instead of
                # snapping to it: 0-100 km/h takes about 3 s, so leaning on the
                # stick is a surge rather than a teleport.  Legs lined up with
                # the motion carry the hull all the way; a mech that fights its
                # legs (or lost the chassis) tops out lower, and the ramp aims
                # at that reduced speed.
                if abs(vx) > 1e-3 or abs(vz) > 1e-3:
                    align = math.cos(mu.wrap_pi(math.atan2(vz, vx) - float(leg[row])))
                    f = 0.45 + 0.55 * max(0.0, align)
                else:
                    f = 1.0
                if hp[row, defs.MOD_CHASSIS] <= 0.0:
                    f *= 0.2
                ax = float(drive[row, 0])
                az = float(drive[row, 1])
                dx = vx * f - ax
                dz = vz * f - az
                step = math.hypot(dx, dz)
                limit = defs.MECH_ACCEL * dt
                if step > limit:
                    dx *= limit / step
                    dz *= limit / step
                ax += dx
                az += dz
                drive[row, 0] = ax
                drive[row, 1] = az
                vx, vz = ax, az
            if abs(vx) > 0.01 or abs(vz) > 0.01:
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
                if et == defs.ETYPE_MECH and nx == x and nz == z:
                    drive[row, 0] = drive[row, 1] = 0.0   # a wall stops the hull
                pos[row, 0] = nx
                pos[row, 2] = nz
                x, z = nx, nz
            pos[row, 1] = terrain.height_at(x, z)
