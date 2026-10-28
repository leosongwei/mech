"""AI for everything that is not player controlled.

Units acquire targets on a slow timer (0.45 s, LOS validated), engage with
lead & ballistic compensation and otherwise push along their lane towards the
enemy command base.
"""

from __future__ import annotations

import math
from typing import TYPE_CHECKING

import numpy as np

from .. import defs, mathutil as mu
from . import smoke

if TYPE_CHECKING:
    from ..world import World

ACQUIRE = 0.45
SPOT_MEMORY = 4.0              # seconds an AI keeps firing at a lost target's spot


def run(world: World, dt: float) -> None:
    e = world.ecs
    ids = world.movable()          # player and AI hulls alike, civilians included
    if ids.size == 0:
        return

    # the two views the per-entity loop below needs by id: everything else it
    # takes from `ecs.slot()` and indexes by row
    pos = e["pos"]
    vel = e["vel"]

    terrain = world.terrain
    # who may be shot at is a query, not a type switch: `combat` hulls only
    # (wrecks, corpses, civilians and smoke are not in here by construction).
    # One id-indexed array rather than a list per faction: `_acquire` reads it
    # for the cells around the shooter, not to filter a roster.  Built from the
    # archetypes so it costs no per-id gather.
    combat_fac = np.full(e.alive.size, -1, np.int8)
    for g in e.groups("combat", "faction"):
        combat_fac[g.ids] = g.bufs["faction"][:g.n]

    # per entity: one `slot` lookup, then every component is `bufs["x"][row]`.
    # The order is the query order (ascending ids) because this loop draws the
    # random numbers, so `groups` would reshuffle the whole battle.
    for eid in ids:
        b, row = e.slot(eid)
        if b.get("owner") is not None and int(b["owner"][row]) >= 0:
            continue                                   # player driven
        if "combat" not in b:
            _civ(world, eid, b, row, dt)
            continue

        t = int(b["etype"][row])
        fac = int(b["faction"][row])
        d = defs.UNIT_DEFS[t]
        speed = d["speed"]
        px, py, pz = float(b["pos"][row, 0]), float(b["pos"][row, 1]), float(b["pos"][row, 2])
        eye = (px, py + d["height"] * 0.75, pz)

        timer = b["ai_timer"]
        target = b["target"]
        spot_t = b["spot_t"]
        timer[row] -= dt
        tid = int(target[row])
        if timer[row] <= 0.0:
            timer[row] = ACQUIRE * world.rng.uniform(0.75, 1.35)
            tid = _acquire(world, fac, combat_fac, eye, d)
            target[row] = tid
        if tid >= 0:
            if not e.alive[tid] or int(e["faction"][tid]) == fac:
                target[row] = -1
                tid = -1

        moving = True
        seen = False
        if tid >= 0:
            tpos = pos[tid]
            tvel = vel[tid]
            dist = math.dist((eye[0], eye[2]), (float(tpos[0]), float(tpos[2])))
            if dist < d["ai_range"] and _sees(world, eye, (float(tpos[0]), float(tpos[1]) + d["height"] * 0.6, float(tpos[2]))):
                moving = dist > d["ai_range"] * 0.55
                seen = True
                # remember the spot: if the sight breaks (smoke, a crest) the
                # crew keeps shooting there for a few seconds
                b["spot_x"][row] = float(tpos[0])
                b["spot_y"][row] = float(tpos[1])
                b["spot_z"][row] = float(tpos[2])
                spot_t[row] = SPOT_MEMORY
                _engage(world, eid, b, row, tid, eye, d, dist, tpos, tvel, dt)
        if not seen and float(spot_t[row]) > 0.0:
            # lost it: hose the spot where it was last seen
            spot_t[row] = float(spot_t[row]) - dt
            dist = math.dist((eye[0], eye[2]),
                             (float(b["spot_x"][row]), float(b["spot_z"][row])))
            if dist < d["ai_range"]:
                _suppress(world, eid, b, row, eye, d, dist, dt)
                moving = False

        if moving:
            b["trigger"][row] = 0
            if "vehicle" in b:
                _advance_route(world, b, row, speed)   # a car follows the lane
            else:
                _advance(world, eid, b, row, speed, dt)
        elif "vehicle" in b:
            # close enough to shoot: a carrier brakes for that, it does not
            # roll on at the last commanded speed the way a walking hull's
            # leftover velocity does
            b["vel"][row, 0] = b["vel"][row, 2] = 0.0


def _sees(world: World, eye: tuple[float, float, float],
          point: tuple[float, float, float]) -> bool:
    """Line of sight: terrain, buildings *and* smoke."""
    if not world.terrain.los(eye, point):
        return False
    return not smoke.blocks(world, eye, point)


def _acquire(world: World, fac: int, combat_fac: np.ndarray,
             eye: tuple[float, float, float], d: dict) -> int:
    """Nearest hostile unit with line of sight.

    A hull may declare a restricted target list in its definition (`targets`):
    a rifleman only picks a fight with other infantry and leaves armour alone,
    because 15 hp in the open is not a threat to a tank and the crew knows it.

    The candidates come from the grid, not from a roster: a 5000-unit battle
    puts a few dozen ids in the box around the shooter instead of a few
    thousand in the array to sort.  The box covers the whole sight radius, so
    the answer is the same one the full scan gave.  No position is gathered
    here - the grid carries its own copy, in the same order as its ids.
    """
    grid = world.grid
    ci, cj = grid.cell_of(eye[0], eye[2])
    ids, p = grid.box(ci, cj, int(d["sight"] * grid.inv) + 1)
    if ids.size == 0:
        return -1
    cf = combat_fac[ids]
    keep = (cf >= 0) & (cf != fac)
    want = d.get("targets")
    if want:
        keep &= np.isin(world.ecs["etype"][ids], np.asarray(want, np.uint8))
    ds = np.hypot(p[:, 0] - eye[0], p[:, 2] - eye[2])
    keep &= ds <= d["sight"]
    if not keep.all():
        ids, p, ds = ids[keep], p[keep], ds[keep]
        if ids.size == 0:
            return -1
    for k in np.argsort(ds)[:6]:
        tid = int(ids[k])
        tp = p[k]
        if _sees(world, eye, (float(tp[0]), float(tp[1]) + 1.5, float(tp[2]))):
            return tid
    return -1


def _engage(world: World, eid: int, b: dict, row: int, tid: int,
            eye: tuple[float, float, float], d: dict, dist: float,
            tpos: np.ndarray, tvel: np.ndarray, dt: float) -> None:
    # aim at the intercept point of the primary weapon.  no gravity lift here:
    # the fire control zeroes the gun for the drop at shot time, so lifting the
    # aim as well would double compensate
    w0 = int(b["weapon"][row, 0]) or defs.WID_AC30
    wd0 = defs.WEAPONS[w0]
    speed = wd0["vel"]
    aim = mu.solve_lead(eye, (float(tpos[0]), float(tpos[1]) + 2.0, float(tpos[2])),
                        tvel, speed, 0.0)
    _aim_and_fire(world, eid, b, row, eye, d, dist, aim, dt)


def _suppress(world: World, eid: int, b: dict, row: int,
              eye: tuple[float, float, float], d: dict, dist: float,
              dt: float) -> None:
    """Keep firing at the spot where the target was last seen."""
    aim = (float(b["spot_x"][row]), float(b["spot_y"][row]) + 1.5,
           float(b["spot_z"][row]))
    _aim_and_fire(world, eid, b, row, eye, d, dist, aim, dt)


def _aim_and_fire(world: World, eid: int, b: dict, row: int,
                  eye: tuple[float, float, float], d: dict, dist: float,
                  aim: tuple[float, float, float], dt: float) -> None:
    yaw = b["yaw"]
    pitch = b["pitch"]
    turret = b["turret_yaw"]
    trigger = b["trigger"]
    etype = int(b["etype"][row])
    hp = b["hp"]
    weapon = b["weapon"]

    tx = aim[0] - eye[0]
    ty = aim[1] - eye[1]
    tz = aim[2] - eye[2]
    want_yaw, want_pitch = mu.angles_to(tx, ty, tz)

    if etype == defs.ETYPE_APC:
        # the hull of a wheeled carrier belongs to the driver: movement points
        # it where it rolls, and the crew turns nothing but the turret
        turret[row] = mu.turn_toward(float(turret[row]), want_yaw, 2.2 * dt)
    else:
        turn = 1.7 if etype == defs.ETYPE_MECH else 3.2
        yaw[row] = mu.turn_toward(float(yaw[row]), want_yaw, turn * dt)
    pitch[row] = max(-0.4, min(0.7, float(pitch[row]) + (want_pitch - float(pitch[row])) * min(1.0, 6.0 * dt)))

    aim_yaw = float(turret[row]) if etype == defs.ETYPE_APC else float(yaw[row])
    err = abs(mu.wrap_pi(want_yaw - aim_yaw))
    t = 0
    if err < 0.14:
        if hp[row, 0] > 0:
            t |= 1
        if etype == defs.ETYPE_MECH and hp[row, defs.MOD_RIGHT] > 0 and dist < 2200:
            w1 = int(weapon[row, 1])
            if w1 and defs.WEAPONS[w1]["kind"] in ("rocket", "saclos") and dist < defs.WEAPONS[w1]["range"]:
                t |= 2
        if etype == defs.ETYPE_APC and dist < 3500:
            t |= 2
    # a semi automatic arm only fires on the rising edge of the trigger, so
    # the AI has to let go of it every other tick
    if (t & 2) and (world.tick & 1) and defs.WEAPONS[int(weapon[row, 1])]["semi"]:
        t &= ~2
    trigger[row] = t


def _advance(world: World, eid: int, b: dict, row: int, speed: float,
             dt: float) -> None:
    pos = b["pos"]
    vel = b["vel"]
    terrain = world.terrain
    fac = int(b["faction"][row])
    lane_i = int(b["lane"][row])
    pts = world.lanes[lane_i % len(world.lanes)]
    if fac == defs.FACTION_BLUE:
        pts = world.lanes_rev[lane_i % len(world.lanes)]
    i = int(b["path_i"][row])
    if i >= len(pts):
        i = len(pts) - 1
    wx, wz = pts[i]
    px, pz = float(pos[row, 0]), float(pos[row, 2])
    dx = wx - px
    dz = wz - pz
    if dx * dx + dz * dz < 55.0 * 55.0:
        if i < len(pts) - 1:
            i += 1
            b["path_i"][row] = i
            wx, wz = pts[i]
            dx = wx - px
            dz = wz - pz
        elif i >= len(pts) - 1:
            bx, bz = terrain.bases[1 - fac]
            dx, dz = bx - px, bz - pz
    d = math.hypot(dx, dz)
    if d < 1e-3:
        vel[row, 0] = vel[row, 2] = 0.0
        return
    dx, dz = dx / d, dz / d
    # simple obstacle avoidance: fan out when the step ahead is blocked
    step = 14.0
    if terrain.blocked(px + dx * step, pz + dz * step):
        base = math.atan2(dz, dx)
        for off in (0.5, -0.5, 1.0, -1.0, 1.6, -1.6, 2.2, -2.2):
            a = base + off
            nx, nz = math.cos(a), math.sin(a)
            if not terrain.blocked(px + nx * step, pz + nz * step):
                dx, dz = nx, nz
                break
    speed *= 1.0
    vel[row, 0] = dx * speed
    vel[row, 2] = dz * speed


# ---------------------------------------------------------------------------
# wheeled route following
# ---------------------------------------------------------------------------
# A hull with an 11 m turning radius cannot be steered at a point: it has to
# follow a line.  The lane *is* that line - it is the road, and the map keeps
# the lanes clear of houses and trees - so a vehicle is given the lane as a
# reference path, aims at a lookahead point on it and brakes for the corners it
# can see coming.  locomotion (systems/movement.py) turns the aim into throttle
# and steering; nothing here sets a velocity of its own.

def _seg(pts: list, k: int) -> float:
    (ax, az), (bx, bz) = pts[k], pts[k + 1]
    return math.hypot(bx - ax, bz - az)


def _route_project(pts: list, i: int, px: float, pz: float) -> tuple[int, float]:
    """Where the hull sits on the lane: (segment index, fraction along it).

    Only the three segments around the index it is driving towards are tested -
    a hull on its lane is within a segment of it - which keeps this cheap
    enough to run for every APC every tick.
    """
    best, bk, bt = 1e18, 0, 0.0
    for k in range(max(0, i - 1), min(len(pts) - 2, i + 1) + 1):
        (ax, az), (bx, bz) = pts[k], pts[k + 1]
        sx, sz = bx - ax, bz - az
        ll = sx * sx + sz * sz
        t = 0.0 if ll <= 1e-9 else max(0.0, min(1.0, ((px - ax) * sx + (pz - az) * sz) / ll))
        d = (px - ax - sx * t) ** 2 + (pz - az - sz * t) ** 2
        if d < best:
            best, bk, bt = d, k, t
    return bk, bt


def _route_aim(pts: list, k: int, t: float, look: float) -> tuple[float, float, int]:
    """The point ``look`` metres further along the lane, and its segment.

    Arc length, not a straight line to the next way point: the lane's points
    are 60 m apart, so aiming at one of them would make the aim jump a whole
    corner at a time - exactly the corner a car has to start turning early for.
    """
    rem = look
    while k < len(pts) - 1:
        seg = _seg(pts, k)
        left = seg * (1.0 - t)
        if left >= rem and seg > 1e-6:
            (ax, az), (bx, bz) = pts[k], pts[k + 1]
            f = t + rem / seg
            return ax + (bx - ax) * f, az + (bz - az) * f, k
        rem -= left
        k += 1
        t = 0.0
    return pts[-1][0], pts[-1][1], len(pts) - 1


def _route_speed(pts: list, k: int, t: float, top: float) -> float:
    """How fast the hull may be rolling now and still make the bends ahead.

    A bend's curvature is the heading it turns over the length it turns in, and
    a hull carries ``sqrt(accel / curvature)`` into it - a 15 m arc taken at
    8 m/s.  Every vertex inside APC_CORNER_HORIZON sets a ceiling that rises
    with the distance to it (``v^2 = v_bend^2 + 2 a d``), so the car brakes
    coming in and rolls out of the bend instead of arriving at full speed with
    a turning radius that cannot hold it.
    """
    limit = top
    d = _seg(pts, k) * (1.0 - t)          # metres to the end of this segment
    while k + 2 < len(pts) and d < defs.APC_CORNER_HORIZON:
        (ax, az), (bx, bz), (cx, cz) = pts[k], pts[k + 1], pts[k + 2]
        ang = abs(mu.wrap_pi(math.atan2(cz - bz, cx - bx) -
                             math.atan2(bz - az, bx - ax)))
        cur = ang / max(1.0, 0.5 * (_seg(pts, k) + _seg(pts, k + 1)))
        if cur > 1e-5:
            vb = min(top, math.sqrt(defs.APC_LAT_ACCEL / cur))
            limit = min(limit, math.sqrt(vb * vb + 2.0 * defs.APC_BRAKE * d))
        k += 1
        d += _seg(pts, k)
    return limit


def _advance_route(world: World, b: dict, row: int, speed: float) -> None:
    """Aim a wheeled hull down its lane - the direction, not the throttle."""
    pos = b["pos"]
    vel = b["vel"]
    fac = int(b["faction"][row])
    lane = int(b["lane"][row]) % len(world.lanes)
    pts = world.lanes_rev[lane] if fac == defs.FACTION_BLUE else world.lanes[lane]
    px, pz = float(pos[row, 0]), float(pos[row, 2])
    i = max(0, min(int(b["path_i"][row]), len(pts) - 2))
    k, t = _route_project(pts, i, px, pz)
    # the aim sits APC_ROUTE_LOOK seconds of the *actual* rolling speed ahead:
    # far enough to anticipate, near enough to turn into the bend it is in (a
    # long lookahead cuts the inside of a corner by look^2 / 8R whatever the
    # steering does)
    look = max(defs.APC_ROUTE_LOOK_MIN,
               min(defs.APC_ROUTE_LOOK_MAX,
                   max(8.0, abs(float(b["drive"][row, 0]))) * defs.APC_ROUTE_LOOK))
    ax, az, _ = _route_aim(pts, k, t, look)
    # the end of the lane is the enemy side of the map: the last stretch runs
    # at the command base, which is what a lane actually leads to
    if (pts[-1][0] - px) ** 2 + (pts[-1][1] - pz) ** 2 <= look * look:
        ax, az = world.terrain.bases[1 - fac]
    b["path_i"][row] = k
    dx, dz = ax - px, az - pz
    d = math.hypot(dx, dz)
    if d < 1e-3:
        vel[row, 0] = vel[row, 2] = 0.0
        return
    v = _route_speed(pts, k, t, speed)
    vel[row, 0] = dx / d * v
    vel[row, 2] = dz / d * v


def _civ(world: World, eid: int, b: dict, row: int, dt: float) -> None:
    pos = b["pos"]
    vel = b["vel"]
    timer = b["ai_timer"]
    timer[row] -= dt
    if timer[row] > 0.0:
        return
    timer[row] = world.rng.uniform(1.5, 4.5)
    dx = float(b["home_x"][row]) - float(pos[row, 0])
    dz = float(b["home_z"][row]) - float(pos[row, 2])
    if dx * dx + dz * dz > 240.0 * 240.0:
        a = math.atan2(dz, dx)
    else:
        a = world.rng.random() * math.tau
    vel[row, 0] = math.cos(a) * 2.0
    vel[row, 2] = math.sin(a) * 2.0
    b["yaw"][row] = a
