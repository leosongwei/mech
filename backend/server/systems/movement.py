"""Ground movement: integrates velocity, follows the terrain, drives the legs."""

from __future__ import annotations

import math
from typing import TYPE_CHECKING

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
    this lives here and not in the AI system.
    """
    e = world.ecs
    ids = world.movable()
    vel = e["vel"]
    leg = e["leg_yaw"]
    etype = e["etype"]
    for eid in ids:
        t = int(etype[eid])
        vx = float(vel[eid, 0])
        vz = float(vel[eid, 2])
        if abs(vx) < 0.05 and abs(vz) < 0.05:
            continue
        want = math.atan2(vz, vx)
        rate = 2.6 if t == defs.ETYPE_MECH else 6.0
        leg[eid] = mu.turn_toward(float(leg[eid]), want, rate * dt)


def run(world: World, dt: float) -> None:
    e = world.ecs
    ids = world.movable()          # only hulls with locomotion are integrated
    if ids.size == 0:
        return
    _turn_legs(world, dt)
    pos = e["pos"]
    vel = e["vel"]
    drive = e["drive"]
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
        vx = float(vel[eid, 0])
        vz = float(vel[eid, 2])
        if et == defs.ETYPE_MECH:
            # a mech rolls up to the speed it was asked for instead of snapping
            # to it: 0-100 km/h takes about 3 s, so leaning on the stick is a
            # surge rather than a teleport.  Legs lined up with the motion carry
            # the hull all the way; a mech that fights its legs (or lost the
            # chassis) tops out lower, and the ramp aims at that reduced speed.
            if abs(vx) > 1e-3 or abs(vz) > 1e-3:
                align = math.cos(mu.wrap_pi(math.atan2(vz, vx) - float(leg[eid])))
                f = 0.45 + 0.55 * max(0.0, align)
            else:
                f = 1.0
            if hp[eid, defs.MOD_CHASSIS] <= 0.0:
                f *= 0.2
            ax = float(drive[eid, 0])
            az = float(drive[eid, 1])
            dx = vx * f - ax
            dz = vz * f - az
            step = math.hypot(dx, dz)
            limit = defs.MECH_ACCEL * dt
            if step > limit:
                dx *= limit / step
                dz *= limit / step
            ax += dx
            az += dz
            drive[eid, 0] = ax
            drive[eid, 1] = az
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
                drive[eid, 0] = drive[eid, 1] = 0.0     # a wall stops the hull
            pos[eid, 0] = nx
            pos[eid, 2] = nz
            x, z = nx, nz
        pos[eid, 1] = terrain.height_at(x, z)
