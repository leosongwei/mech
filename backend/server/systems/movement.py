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
        # a wheeled hull is the exception: its `leg_yaw` is not a decoration
        # that follows the velocity, it *is* the steering state (`_vehicle`)
        if "vehicle" in g.bufs:
            continue
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


def _vehicle(vel: np.ndarray, drive: np.ndarray, veh: np.ndarray,
             leg: np.ndarray, yaw_buf: np.ndarray,
             row: int, dt: float, top: float) -> tuple[float, float]:
    """One tick of wheeled locomotion: ``(vx, vz)`` along the hull.

    The command is the same `vel` every other hull gets - a direction and a
    speed - but a car can only honour the part of it that lies along its own
    axis, so the direction becomes a steering angle and the speed a throttle.
    `leg_yaw` is the hull (state, not a rendering hint) and ``drive[0]`` the
    signed speed rolling along it; `yaw` is mirrored from it because that is
    the field the client draws a carrier's hull on.

    Two things fall out of that and are worth saying out loud: the hull cannot
    turn at a standstill (the yaw rate is speed / turn radius), and it never
    drives fast in reverse - the gearbox has `APC_REVERSE` of top speed and the
    only thing that uses it is backing off something the nose is against.  A
    goal behind the hull is taken by turning round for it: the brake grows with
    the heading error, so a U-turn comes down to the radius rather than being
    swung at top speed.
    """
    cx, cz = float(vel[row, 0]), float(vel[row, 2])
    u = float(drive[row, 0])
    yaw = float(leg[row])
    mag = math.hypot(cx, cz)
    err = 0.0
    target = 0.0
    if float(veh[row, 1]) > 0.0:
        # the nose is against something: back off the way we came, wheels held
        # over so the hull is pointing somewhere else when it comes back out
        veh[row, 1] = float(veh[row, 1]) - dt
        target = -top * defs.APC_REVERSE
        err = defs.APC_STEER_LOCK if err >= 0.0 else -defs.APC_STEER_LOCK
    elif mag > 0.05:
        err = mu.wrap_pi(math.atan2(cz, cx) - yaw)
        # brake into a bend, and harder the worse the heading error is - a
        # couple of degrees of correction on a straight is not a reason to
        # give away any speed at all, a half turn is
        target = mag * (1.0 - 0.55 * min(1.0, max(0.0, abs(err) - 0.4) / 1.2))
    if target * u >= 0.0 and abs(target) > abs(u):
        # throttle: thrust minus drag, so the hull pulls hardest off the line
        # and the last few km/h take as long as the first thirty
        acc = defs.APC_THRUST * max(0.0, 1.0 - (u / defs.APC_V_INF) ** 2)
    else:
        acc = defs.APC_BRAKE
    step = acc * dt
    u += max(-step, min(step, target - u))
    # steering: the wheels saturate at APC_STEER_LOCK of heading error, and a
    # hull turns at (speed / radius) - but never harder than APC_LAT_ACCEL of
    # side load, which is what stops it swinging from lock to lock at speed
    rate = min(abs(u) / defs.APC_TURN_R, defs.APC_LAT_ACCEL / max(abs(u), 0.5))
    steer = max(-1.0, min(1.0, err / defs.APC_STEER_LOCK))
    yaw = mu.wrap_pi(yaw + rate * steer * dt)
    drive[row, 0] = u
    leg[row] = yaw
    yaw_buf[row] = yaw
    return math.cos(yaw) * u, math.sin(yaw) * u


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
        veh = g.bufs["veh"]
        leg = g.bufs["leg_yaw"]
        yaw = g.bufs["yaw"]
        hp = g.bufs["hp"]
        etype = g.bufs["etype"]
        wheeled = "vehicle" in g.bufs
        for row in range(n):
            et = int(etype[row])
            x = float(pos[row, 0])
            z = float(pos[row, 2])
            vx = float(vel[row, 0])
            vz = float(vel[row, 2])
            if wheeled:
                # an APC drives itself along its own hull: forward, back and
                # turning, never sideways
                vx, vz = _vehicle(vel, drive, veh, leg, yaw, row, dt,
                                  defs.UNIT_DEFS[et]["speed"])
            elif et == defs.ETYPE_MECH:
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
                if hp[row] <= 0.0:
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
                # a hull that made no headway is against something, however the
                # axis-wise slide dressed it up
                step = math.hypot(nx - x, nz - z)
                if et == defs.ETYPE_MECH and step <= 0.0:
                    drive[row, 0] = drive[row, 1] = 0.0   # a wall stops the hull
                elif wheeled:
                    # blocked means it asked for a stride and got a fraction of
                    # it - measuring absolute metres would call a slow crawl in
                    # the open a wall, and a heavy hull pulls away slowly
                    stuck = step < 0.3 * math.hypot(vx, vz) * dt
                    # nose against something: count it, and once the crew has
                    # been pushing for a moment back out with the wheels held
                    # over, so the next try aims somewhere else
                    if not stuck or float(veh[row, 1]) > 0.0:
                        veh[row, 2] = 0.0                 # rolling again
                    else:
                        veh[row, 2] = float(veh[row, 2]) + dt
                        if float(veh[row, 2]) >= defs.APC_BLOCKED:
                            veh[row, 1] = defs.APC_BACKOUT
                            veh[row, 2] = 0.0
                            drive[row, 0] = 0.0
                pos[row, 0] = nx
                pos[row, 2] = nz
                x, z = nx, nz
            pos[row, 1] = terrain.height_at(x, z)
