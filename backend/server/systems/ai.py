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

    # only two views survive at the top: the per-entity loop below takes its
    # components from `ecs.slot()` and indexes them by row
    pos = e["pos"]
    vel = e["vel"]
    faction = e["faction"]

    terrain = world.terrain
    # who may be shot at is a query, not a type switch: `combat` hulls only
    # (wrecks, corpses, civilians and smoke are not in here by construction)
    foes = {f: [] for f in (defs.FACTION_RED, defs.FACTION_BLUE)}
    for eid in world.combat_units():
        f = int(faction[eid])
        if f in foes:
            foes[1 - f].append(eid)
    foe_arr = {f: np.asarray(v, np.int32) for f, v in foes.items()}

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
            tid = _acquire(world, eid, foe_arr[fac], eye, d)
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
            _advance(world, eid, b, row, speed, dt)


def _sees(world: World, eye: tuple[float, float, float],
          point: tuple[float, float, float]) -> bool:
    """Line of sight: terrain, buildings *and* smoke."""
    if not world.terrain.los(eye, point):
        return False
    return not smoke.blocks(world, eye, point)


def _acquire(world: World, eid: int, foe_ids: np.ndarray,
             eye: tuple[float, float, float], d: dict) -> int:
    """Nearest hostile unit with line of sight.

    A hull may declare a restricted target list in its definition (`targets`):
    a rifleman only picks a fight with other infantry and leaves armour alone,
    because 15 hp in the open is not a threat to a tank and the crew knows it.
    """
    if foe_ids.size == 0:
        return -1
    e = world.ecs
    want = d.get("targets")
    if want:
        foe_ids = foe_ids[np.isin(e["etype"][foe_ids], np.asarray(want, np.uint8))]
        if foe_ids.size == 0:
            return -1
    pos = e["pos"]
    ds = np.hypot(pos[foe_ids, 0] - pos[eid, 0], pos[foe_ids, 2] - pos[eid, 2])
    order = np.argsort(ds)[:6]
    terrain = world.terrain
    for k in order:
        if ds[k] > d["sight"]:
            break
        tid = int(foe_ids[k])
        tp = pos[tid]
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

    turn = 3.2 if etype != defs.ETYPE_MECH else 1.7
    yaw[row] = mu.turn_toward(float(yaw[row]), want_yaw, turn * dt)
    pitch[row] = max(-0.4, min(0.7, float(pitch[row]) + (want_pitch - float(pitch[row])) * min(1.0, 6.0 * dt)))
    if etype == defs.ETYPE_APC:
        turret[row] = mu.turn_toward(float(turret[row]), want_yaw, 2.2 * dt)

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
