"""Geometry / ballistics helpers shared by the weapon systems."""

from __future__ import annotations

import math

import numpy as np

from . import defs


def dir_from(yaw: float, pitch: float) -> tuple[float, float, float]:
    cp = math.cos(pitch)
    return (cp * math.cos(yaw), math.sin(pitch), cp * math.sin(yaw))


def yaw_of(dx: float, dz: float) -> float:
    return math.atan2(dz, dx)


def angles_to(dx: float, dy: float, dz: float) -> tuple[float, float]:
    """Yaw / pitch that point along the given vector."""
    horiz = math.hypot(dx, dz)
    yaw = math.atan2(dz, dx)
    pitch = math.atan2(dy, horiz)
    return yaw, pitch


def wrap_pi(a: float) -> float:
    while a > math.pi:
        a -= 2 * math.pi
    while a < -math.pi:
        a += 2 * math.pi
    return a


def turn_toward(cur: float, target: float, max_step: float) -> float:
    d = wrap_pi(target - cur)
    if abs(d) <= max_step:
        return target
    return wrap_pi(cur + math.copysign(max_step, d))


# The array forms exist for the systems that turn a whole archetype at once.
# Same answer as the scalar pair above, elementwise, including the snap onto
# the target once it is within one step.
def wrap_pi_arr(a: np.ndarray) -> np.ndarray:
    return np.mod(np.asarray(a, np.float32) + np.float32(math.pi),
                  np.float32(2.0 * math.pi)) - np.float32(math.pi)


def turn_toward_arr(cur: np.ndarray, target: np.ndarray,
                    max_step: np.ndarray) -> np.ndarray:
    d = wrap_pi_arr(np.asarray(target) - np.asarray(cur))
    stepped = wrap_pi_arr(np.asarray(cur) + np.copysign(max_step, d))
    return np.where(np.abs(d) <= max_step, target, stepped)


def muzzle_point(pos: np.ndarray, yaw: float, module: int,
                 arm_pitch: float = 0.0,
                 reach: float | None = None) -> tuple[float, float, float]:
    """World position of the barrel tip of the given mech arm.

    The tip sits `reach` metres out along the barrel (default: the arm's front
    face), and the barrel swings with `arm_pitch`, exactly like the client
    builds the models - so the round leaves the mouth it was drawn on.
    """
    off = defs.MECH_ARM_MUZZLE.get(module, (0.0, 4.3, 0.9))
    rx, ry, rz = off
    fwd = rz if reach is None else reach
    c, s = math.cos(yaw), math.sin(yaw)
    # local +z forward, +x right  -> world
    fx, fz = c, s
    rxw, rzw = -s, c
    return (pos[0] + rxw * rx + fx * fwd * math.cos(arm_pitch),
            pos[1] + ry + fwd * math.sin(arm_pitch),
            pos[2] + rzw * rx + fz * fwd * math.cos(arm_pitch))


def unit_spheres(etype: int, pos: np.ndarray,
                 yaw: float) -> list[tuple[int, tuple[float, float, float], float]]:
    """List of (module, center, radius) hit spheres for a unit type."""
    px, py, pz = float(pos[0]), float(pos[1]), float(pos[2])
    if etype == defs.ETYPE_MECH:
        c, s = math.cos(yaw), math.sin(yaw)
        rxw, rzw = -s, c
        out = []
        for mod, ox, oy, oz, r in defs.MECH_HITSPHERES:
            out.append((mod,
                        (px + rxw * ox + c * oz, py + oy, pz + rzw * ox + s * oz),
                        r))
        return out
    if etype == defs.ETYPE_APC:
        return [(0, (px, py + 1.1, pz), 1.7), (1, (px, py + 2.4, pz), 1.2)]
    if etype == defs.ETYPE_BASE:
        return [(0, (px, py + 5.0, pz), 8.5)]
    if etype == defs.ETYPE_BUNKER:
        # stepped blockhouse: a squat mass and the observation deck on top
        return [(0, (px, py + 4.0, pz), 5.0), (0, (px, py + 8.8, pz), 3.6)]
    if etype == defs.ETYPE_INF_RIFLE or etype == defs.ETYPE_INF_ROCKET or etype == defs.ETYPE_CIV:
        return [(0, (px, py + 0.9, pz), 0.65)]
    return [(0, (px, py + 1.0, pz), 1.5)]


def solve_lead(origin: np.ndarray, tpos: np.ndarray, tvel: np.ndarray | None,
               speed: float, gravity: float, iters: int = 3,
               ) -> tuple[float, float, float]:
    """Iteratively solve the intercept point for a ballistic projectile."""
    ox, oy, oz = origin
    tx, ty, tz = tpos
    vx, vz = (float(tvel[0]), float(tvel[2])) if tvel is not None else (0.0, 0.0)
    t = math.dist(origin, tpos) / max(speed, 1.0)
    ax, ay, az = tx, ty, tz
    for _ in range(iters):
        ax = tx + vx * t
        az = tz + vz * t
        ay = ty + 0.5 * gravity * t * t
        d = math.dist((ox, oy, oz), (ax, ay, az))
        t = d / max(speed, 1.0)
    return ax, ay, az


def ballistic_drop(gravity: float, flight: float) -> float:
    return 0.5 * gravity * flight * flight


def weapon_range(wid: int) -> float:
    return defs.WEAPONS[wid]["range"]
