"""Fire control: find what the muzzle is pointing at, and how high to hold.

The sight point is the first thing the ray from the muzzle runs into - the
ground, a building tile, a tree trunk or a unit's hit sphere - out to
``defs.ZERO_RANGE``.
The solver then returns the launch elevation that puts the round on that
point, mirroring the integrator in ``systems/projectiles.py`` (1/30 s
semi-implicit Euler, same gravity and, for rockets, the same thrust curve).

Only direct fire is zeroed: bullets and unguided rockets.  SACLOS and the
infrared missile steer themselves and keep the plain aim.
"""

from __future__ import annotations

import math
from typing import TYPE_CHECKING

from . import defs, mathutil as mu

if TYPE_CHECKING:
    from .world import World

DT = 1 / 30.0            # same step as the projectile integrator
MARCH = 8.0              # sight-ray resolution, metres (half a 16 m tile)
MIN_DIST = 25.0          # closer than this the sight point is the mech itself
ITERS = 4


def _ray_sphere(o: tuple[float, float, float], d: tuple[float, float, float],
                c: tuple[float, float, float], r: float) -> float | None:
    """Distance along the ray to the near intersection, or None."""
    ox, oy, oz = o[0] - c[0], o[1] - c[1], o[2] - c[2]
    b = ox * d[0] + oy * d[1] + oz * d[2]
    disc = b * b - (ox * ox + oy * oy + oz * oz - r * r)
    if disc < 0.0:
        return None
    t = -b - math.sqrt(disc)
    return t if t >= 0.0 else None


def _unit_hit(world: World, origin: tuple[float, float, float],
              direction: tuple[float, float, float], max_dist: float, ignore: int,
              faction: int = -1) -> tuple[tuple[float, float, float] | None, float]:
    e = world.ecs
    ids = world.units()
    if ids.size == 0:
        return None, max_dist
    pos = e["pos"]
    px = pos[ids, 0] - origin[0]
    pz = pos[ids, 2] - origin[2]
    near = ids[(px * px + pz * pz) < (max_dist + 12.0) ** 2]
    best, best_t = None, max_dist
    for uid in near:
        uid = int(uid)
        if uid == ignore:
            continue
        if faction >= 0 and int(e["faction"][uid]) == faction:
            continue                    # own rounds pass through their own side
        yaws = float(e["yaw"][uid])
        for _mod, c, r in mu.unit_spheres(int(e["etype"][uid]), pos[uid], yaws):
            t = _ray_sphere(origin, direction, c, r)
            if t is not None and t < best_t:
                best_t = t
                best = (origin[0] + direction[0] * t, origin[1] + direction[1] * t,
                        origin[2] + direction[2] * t)
    return best, best_t


def sight_point(world: World, origin: tuple[float, float, float],
                direction: tuple[float, float, float], max_dist: float,
                ignore: int = -1, faction: int = -1) -> tuple[float, float, float] | None:
    """First contact of the muzzle ray, or None if it reaches max_dist."""
    unit_pt, limit = _unit_hit(world, origin, direction, max_dist, ignore, faction)
    terrain = world.terrain
    t0 = 0.0
    while t0 < limit:
        t1 = min(limit, t0 + MARCH)
        x = origin[0] + direction[0] * t1
        y = origin[1] + direction[1] * t1
        z = origin[2] + direction[2] * t1
        if not terrain.inside(x, z):
            # the ray left the map: off the field there is nothing to zero on,
            # so the shot just follows the aim line (the round expires in the
            # air or drops away on its own)
            return None
        # a tree trunk is a metre wide, far thinner than MARCH, so it is solved
        # over the whole step rather than probed at its end: zeroing past a
        # trunk the round is going to burst in would make every shot at a tree
        # fall short of the crosshair
        seg = terrain.trunk_hit(
            (origin[0] + direction[0] * t0, origin[1] + direction[1] * t0,
             origin[2] + direction[2] * t0), (x, y, z))
        trunk_t = t0 + seg[1] * (t1 - t0) if seg[0] else None
        if y <= terrain.height_at(x, z) or terrain.blocked(x, z):
            a, b = t0, t1
            for _ in range(6):
                m = 0.5 * (a + b)
                mx = origin[0] + direction[0] * m
                my = origin[1] + direction[1] * m
                mz = origin[2] + direction[2] * m
                if my > terrain.height_at(mx, mz) and not terrain.blocked(mx, mz):
                    a = m
                else:
                    b = m
            t = 0.5 * (a + b)
            if trunk_t is not None and trunk_t < t:
                return (origin[0] + direction[0] * trunk_t,
                        origin[1] + direction[1] * trunk_t,
                        origin[2] + direction[2] * trunk_t)
            return (origin[0] + direction[0] * t, origin[1] + direction[1] * t,
                    origin[2] + direction[2] * t)
        if trunk_t is not None:
            return seg[2]
        t0 = t1
    return unit_pt


def _altitude_at(origin: tuple[float, float, float], yaw: float, pitch: float,
                 wd: dict, dist: float) -> float | None:
    """Height of the round once it has covered `dist` metres horizontally.

    None when the round expires (or never gets that far) first.
    """
    d = mu.dir_from(yaw, pitch)
    v = [d[0] * wd["vel"], d[1] * wd["vel"], d[2] * wd["vel"]]
    vx, vy, vz = v
    x, y, z = origin
    g = float(wd["g"])
    thrust = float(wd.get("thrust", 0.0))
    maxvel = float(wd.get("maxvel", 0.0))
    life = float(wd["life"])
    prev_h = 0.0
    prev_y = y
    t = 0.0
    while t < life:
        if thrust > 0.0:
            speed = math.sqrt(vx * vx + vy * vy + vz * vz)
            k = min(maxvel, speed + thrust * DT) / max(speed, 1e-6)
            vx *= k
            vy *= k
            vz *= k
        vy -= g * DT
        x += vx * DT
        y += vy * DT
        z += vz * DT
        t += DT
        h = math.hypot(x - origin[0], z - origin[2])
        if h >= dist:
            span = h - prev_h
            f = (dist - prev_h) / span if span > 1e-6 else 1.0
            return prev_y + (y - prev_y) * f
        prev_h, prev_y = h, y
    return None


def solve_elevation(origin: tuple[float, float, float],
                    target: tuple[float, float, float], wd: dict, yaw: float,
                    pitch: float) -> float | None:
    """Launch elevation that drops the round on `target`, or None."""
    d = math.hypot(target[0] - origin[0], target[2] - origin[2])
    if d < MIN_DIST:
        return None
    h = target[1] - origin[1]
    g = float(wd["g"])
    vel = float(wd["vel"])
    if g > 0.0 and vel > 0.0 and not float(wd.get("thrust", 0.0)):
        # direct shot: closed form, low arc; half a step of Euler error over a
        # 3 km flight is under a metre, so no refinement is needed.  Anything
        # past the round's own reach cannot be zeroed for at all
        if d > vel * float(wd["life"]):
            return None
        disc = vel ** 4 - g * (g * d * d + 2.0 * h * vel * vel)
        if disc < 0.0:
            return None
        return math.atan2(vel * vel - math.sqrt(disc), g * d)
    # thrusting rocket: no closed form, and the altitude at range is a steep
    # function of the elevation, so walk it in with a secant instead of Newton
    # `_altitude_at` gives an absolute height, so the walk compares it against
    # the target's absolute height: `h` is the drop below the muzzle and only
    # serves the closed form above.  Mixing the two put the round one muzzle
    # height low, which for a slow launch is hundreds of metres short
    t0, t1 = float(pitch), float(pitch) + 0.02
    y0 = _altitude_at(origin, yaw, t0, wd, d)
    if y0 is None:
        return None
    for _ in range(ITERS + 2):
        y1 = _altitude_at(origin, yaw, t1, wd, d)
        if y1 is None:
            return None
        err = float(target[1]) - y1
        if abs(err) < 0.1:
            return t1
        dy = y1 - y0
        if abs(dy) < 1e-6:
            return None
        t2 = max(-1.2, min(defs.ZERO_MAX_ELEV, t1 + err * (t1 - t0) / dy))
        t0, y0, t1 = t1, y1, t2
    return t1 if abs(t1) <= defs.ZERO_MAX_ELEV else None


def zero_pitch(world: World, origin: tuple[float, float, float], yaw: float,
               pitch: float, wd: dict, ignore: int = -1,
               faction: int = -1) -> float | None:
    """Elevation for a shot zeroed onto whatever the crosshair sits on.

    Only the AI still fires this way: solving against the sight point makes the
    reticle dance whenever a hull crosses a ridge, so a pilot's gun holds a
    fixed sight setting instead (`zero_at`).
    """
    sp = sight_point(world, origin, mu.dir_from(yaw, pitch), defs.ZERO_RANGE, ignore, faction)
    if sp is None:
        return None
    return solve_elevation(origin, sp, wd, yaw, pitch)


def zero_at(origin: tuple[float, float, float], yaw: float, pitch: float,
            wd: dict, distance: float) -> float | None:
    """Elevation that puts the round on the sight line `distance` metres out.

    The aim point is a fixed way down the crosshair rather than the first thing
    the ray hits, so the solution depends on the pilot's setting and the hull's
    attitude only - never on whether a ridge, a tree or a hull happens to be in
    the way.  That is what stops the reticle jumping at a crest line.
    """
    d = mu.dir_from(yaw, pitch)
    target = (origin[0] + d[0] * distance,
              origin[1] + d[1] * distance,
              origin[2] + d[2] * distance)
    return solve_elevation(origin, target, wd, yaw, pitch)
