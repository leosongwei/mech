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


def sight_eye(pos: np.ndarray, hull_yaw: float) -> tuple[float, float, float]:
    """World position of the commander's sight head: the pilot's eye.

    The sight is bolted to the *hull*, `defs.MECH_SIGHT_Z` behind the hull's own
    rotation axis and `defs.MECH_EYE` up, so the eye is not a point in space -
    it is carried round by the hull.  That is the geometry the decoupled sight
    exists for: swinging the hull underneath a sight already pointing somewhere
    else walks the eye along an arc, and the view slides with it.

    The head's own rotation does not move it: it turns about the pillar it
    stands on, and the eye is that pillar.
    """
    c, s = math.cos(hull_yaw), math.sin(hull_yaw)
    oz = defs.MECH_SIGHT_Z
    return (pos[0] + c * oz, pos[1] + defs.MECH_EYE, pos[2] + s * oz)


def muzzle_point(pos: np.ndarray, yaw: float, module: int,
                 arm_pitch: float = 0.0,
                 reach: float | None = None,
                 right: float = 0.0, up: float = 0.0) -> tuple[float, float, float]:
    """World position of the barrel tip of the given mech arm.

    The tip sits `reach` metres out along the barrel (default: the arm's front
    face), and the barrel swings with `arm_pitch`, exactly like the client
    builds the models - so the round leaves the mouth it was drawn on.

    `right` and `up` move the mouth off the arm's centre line in the arm's own
    frame: a rocket pod is a lattice of tubes and each round leaves its own,
    so the offset is taken along the *pitched* arm (up turns with the bore)
    exactly as the client places its tubes.
    """
    off = defs.MECH_ARM_MUZZLE.get(module, (0.0, 4.3, 0.9))
    rx, ry, rz = off
    fwd = rz if reach is None else reach
    c, s = math.cos(yaw), math.sin(yaw)
    # local +z forward, +x right  -> world
    fx, fz = c, s
    rxw, rzw = -s, c
    cp, sp = math.cos(arm_pitch), math.sin(arm_pitch)
    out = fwd * cp - up * sp            # how far out along the ground track
    high = fwd * sp + up * cp           # how far up the bore line has climbed
    return (pos[0] + rxw * (rx + right) + fx * out,
            pos[1] + ry + high,
            pos[2] + rzw * (rx + right) + fz * out)


_GEOM_CACHE: dict[int, tuple[np.ndarray, np.ndarray, np.ndarray]] = {}


def sphere_geom(etype: int) -> tuple[np.ndarray, np.ndarray, np.ndarray]:
    """``(local[n,3], radius[n], module[n])`` for a unit type - built once.

    `local` is (right, up, forward) in the hull's own frame, straight out of
    ``defs.UNIT_DEFS[etype]["spheres"]``.  Cached, because the hit geometry of a
    type never changes: the projectile sweep asks for it once a tick and would
    otherwise rebuild the same tuples for every hull on the field.
    """
    g = _GEOM_CACHE.get(etype)
    if g is None:
        d = defs.UNIT_DEFS.get(etype) or {}
        rows = d.get("spheres") or ((0, 0.0, 1.0, 0.0, 1.5),)
        arr = np.asarray(rows, np.float64)
        g = (np.ascontiguousarray(arr[:, 1:4]),
             np.ascontiguousarray(arr[:, 4]),
             np.ascontiguousarray(arr[:, 0]).astype(np.int32))
        _GEOM_CACHE[etype] = g
    return g


def sphere_world(local: np.ndarray, pos: np.ndarray, yaw: np.ndarray) -> np.ndarray:
    """``(m,3)`` local offsets -> ``(m*n,3)`` world centres for m hulls.

    One hull's spheres come out contiguously, in the order of `local`, which is
    how the collision sweep keeps its rows aligned with its unit ids.  The
    rotation is the hull's own: +x right, +z forward, exactly what
    ``compose`` in the client builds a model with.
    """
    c = np.asarray(np.cos(yaw), np.float64)
    s = np.asarray(np.sin(yaw), np.float64)
    px = np.asarray(pos, np.float64)[:, None, :]
    right, up, forward = local[:, 0], local[:, 1], local[:, 2]
    x = px[:, :, 0] - s[:, None] * right[None, :] + c[:, None] * forward[None, :]
    y = px[:, :, 1] + up[None, :]
    z = px[:, :, 2] + c[:, None] * right[None, :] + s[:, None] * forward[None, :]
    return np.stack((x, y, z), axis=-1).reshape(-1, 3)


def solve_lead(origin: np.ndarray, tpos: np.ndarray, tvel: np.ndarray | None,
               speed: float, gravity: float, iters: int = 3,
               ) -> tuple[float, float, float]:
    """Iteratively solve the intercept point for a ballistic projectile.

    All three components of the target's velocity are honoured: a hull drives
    on the flat (its `vel[1]` is 0, so nothing changes for one), a missile in
    the air climbs or dives and *that* is the component a gunner has to lead.
    `gravity` is the drop of *our* round over the flight time, not the target's
    - the fire control zeroes the barrel for it at shot time.
    """
    ox, oy, oz = origin
    tx, ty, tz = tpos
    vx, vy, vz = ((float(tvel[0]), float(tvel[1]), float(tvel[2]))
                  if tvel is not None else (0.0, 0.0, 0.0))
    t = math.dist(origin, tpos) / max(speed, 1.0)
    ax, ay, az = tx, ty, tz
    for _ in range(iters):
        ax = tx + vx * t
        az = tz + vz * t
        ay = ty + vy * t + 0.5 * gravity * t * t
        d = math.dist((ox, oy, oz), (ax, ay, az))
        t = d / max(speed, 1.0)
    return ax, ay, az


def ballistic_drop(gravity: float, flight: float) -> float:
    return 0.5 * gravity * flight * flight


def weapon_range(wid: int) -> float:
    return defs.WEAPONS[wid]["range"]
