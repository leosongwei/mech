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
from . import sight

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
            if "raider" in b:
                # a raiding party has a route of its own, off the lanes: the
                # mode's raid system (modes/battle/raids.py) writes its
                # velocity.  Everything above - acquire, lead, fire - it shoots
                # with the same crew as any other hull.
                pass
            elif "vehicle" in b:
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
    """Line of sight: whatever the shared sight rule says (ground, buildings,
    tree canopies and smoke)."""
    return sight.clear(world, eye, point)


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
# wheeled route following
# ---------------------------------------------------------------------------
# A hull with an 11 m turning radius cannot be steered at a point: it has to
# follow a *line of way points*, and the line the lanes hand it is dense and full
# of sharp corners.  The rule that makes such a line drivable is the acceptance
# radius: a way point that comes within `_arrive_radius(speed)` of the hull
# counts as reached and is dropped, so the hull is already heading for the one
# after it while it is still tens of metres short of the first.  That is what a driver does at a
# right angle - aim for the far side and swing across the inside - and it means
# the hull cheerfully leaves the road on the way through, which is fine: the
# road is not the constraint, the hull's turning radius is.
#
# Locomotion (systems/movement.py) turns the aim into throttle and steering;
# nothing here sets a velocity of its own.

def _seg(pts: list, k: int) -> float:
    (ax, az), (bx, bz) = pts[k], pts[k + 1]
    return math.hypot(bx - ax, bz - az)


def _corner_speed(ang: float, l1: float, l2: float, top: float) -> float:
    """The fastest a hull can take a corner that turns by ``ang`` radians.

    How much room the legs give is irrelevant - a kink is a kink however long
    the straight either side of it - so the corner is defined by how far the
    hull is allowed to cut inside it (APC_CUT, metres).  A fillet of radius R
    misses the vertex by ``R * (1 / cos(ang / 2) - 1)``, so the cut budget caps
    R and the hull's own turning radius floors it; the bend is then driven at
    ``sqrt(APC_LAT_ACCEL * R)``.  A right angle comes out at ~11 m/s cutting
    8 m off the inside - the turn anyone makes at a street corner - and a 20
    degree kink costs nothing at all, as it should.
    """
    if ang < 1e-3:
        return top
    over = 1.0 / math.cos(0.5 * min(ang, math.pi * 0.999)) - 1.0
    radius = defs.APC_CUT / over if over > 1e-6 else 1e9
    radius = max(defs.APC_TURN_R, radius)
    return min(top, math.sqrt(defs.APC_LAT_ACCEL * radius))


def _route_speed(pts: list, i: int, px: float, pz: float, top: float) -> float:
    """How fast the hull may be rolling now and still make the corners ahead.

    Every corner inside APC_CORNER_HORIZON sets a ceiling that rises with the
    distance to it (``v^2 = v_corner^2 + 2 a d``), so the car brakes coming in
    and rolls out of the bend instead of arriving at full speed with a turning
    radius that cannot hold it.
    """
    limit = top
    # start at the way point *before* the one being aimed at: the acceptance
    # radius drops a corner as soon as the hull is within ~12 m of it, and the
    # braking for it has to have happened well before that
    k = max(1, i - 1)
    d = math.dist((px, pz), pts[k])           # metres to the corner itself
    while k + 1 < len(pts) and d < defs.APC_CORNER_HORIZON:
        (ax, az), (bx, bz), (cx, cz) = pts[k - 1], pts[k], pts[k + 1]
        ang = abs(mu.wrap_pi(math.atan2(cz - bz, cx - bx) -
                             math.atan2(bz - az, bx - ax)))
        vb = _corner_speed(ang, _seg(pts, k - 1), _seg(pts, k), top)
        if vb < top:
            limit = min(limit, math.sqrt(vb * vb + 2.0 * defs.APC_BRAKE * d))
        k += 1
        d += _seg(pts, k - 1)
    return limit


def _arrive_radius(speed: float) -> float:
    """How close a way point has to come to count as reached, for this speed.

    The radius has to grow with the speed: a hull at 100 km/h needs ~30 m of arc
    to turn at all, so aiming at a point 10 m away only makes it overshoot and
    come back, while a hull crawling through a tight corner can and should
    follow the line.  ``radius = clamp(speed * time, floor, cap)`` is the
    lookahead rule of a pure-pursuit tracker, and it keeps the same behaviour at
    both ends of the speed range.
    """
    r = abs(speed) * defs.APC_ARRIVE_LOOK
    return max(defs.APC_ARRIVE_MIN, min(defs.APC_ARRIVE_MAX, r))


def _passed(pts: list, i: int, px: float, pz: float, radius: float) -> bool:
    """True when way point ``i`` is behind the hull of a given acceptance radius.

    Either it is inside that radius, or the hull has driven past the line through
    it that is square to the leg arriving from the previous point.  The first is
    what lets a car carry speed into a corner and cut across the inside of it;
    the second is what keeps a car that cut *wide* of a corner - or was shoved
    off the line - from turning back to collect it.
    """
    dx, dz = px - pts[i][0], pz - pts[i][1]
    if dx * dx + dz * dz <= radius * radius:
        return True
    if i <= 0:
        return False
    lx, lz = pts[i][0] - pts[i - 1][0], pts[i][1] - pts[i - 1][1]
    return (lx * lx + lz * lz) > 1e-9 and (dx * lx + dz * lz) > 0.0


def _advance_route(world: World, b: dict, row: int, speed: float) -> None:
    """Aim a wheeled hull at its next way point - the direction, not the throttle."""
    pos = b["pos"]
    vel = b["vel"]
    fac = int(b["faction"][row])
    lane = int(b["lane"][row]) % len(world.lanes)
    pts = world.lanes_rev[lane] if fac == defs.FACTION_BLUE else world.lanes[lane]
    px, pz = float(pos[row, 0]), float(pos[row, 2])
    # `path_i` runs to len(pts), one past the end: that is the "lane finished"
    # mark, and it has to be sticky - deciding per tick between the last way
    # point and the base made a hull orbit the circle between them for ever
    i = max(0, min(int(b["path_i"][row]), len(pts)))
    radius = _arrive_radius(float(b["drive"][row, 0]))
    while i < len(pts) - 1 and _passed(pts, i, px, pz, radius):
        i += 1
    if i == len(pts) - 1 and _passed(pts, i, px, pz, radius):
        i = len(pts)
    b["path_i"][row] = i
    if i >= len(pts):
        # the end of the lane is the enemy side of the map: the last stretch
        # runs at the command base, which is what a lane actually leads to
        ax, az = world.terrain.bases[1 - fac]
        v = speed
    else:
        ax, az = pts[i]
        v = _route_speed(pts, i, px, pz, speed)
    dx, dz = ax - px, az - pz
    d = math.hypot(dx, dz)
    if d < 1e-3:
        vel[row, 0] = vel[row, 2] = 0.0
        return
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
