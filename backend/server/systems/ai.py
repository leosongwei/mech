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

    pos = e["pos"]
    vel = e["vel"]
    yaw = e["yaw"]
    pitch = e["pitch"]
    turret = e["turret_yaw"]
    etype = e["etype"]
    faction = e["faction"]
    owner = e["owner"]
    hp = e["hp"]
    timer = e["ai_timer"]
    target = e["target"]
    trigger = e["trigger"]
    path_i = e["path_i"]
    lane = e["lane"]
    spot_x = e["spot_x"]
    spot_y = e["spot_y"]
    spot_z = e["spot_z"]
    spot_t = e["spot_t"]

    terrain = world.terrain
    # who may be shot at is a query, not a type switch: `combat` hulls only
    # (wrecks, corpses, civilians and smoke are not in here by construction)
    foes = {f: [] for f in (defs.FACTION_RED, defs.FACTION_BLUE)}
    for eid in world.combat_units():
        f = int(faction[eid])
        if f in foes:
            foes[1 - f].append(eid)
    foe_arr = {f: np.asarray(v, np.int32) for f, v in foes.items()}

    # anything that drives itself is ours to steer; a hull without `combat`
    # is a civilian, which only wanders
    civ_units = [int(i) for i in ids if not e.has(i, "combat")]

    for eid in ids:
        t = int(etype[eid])
        if int(owner[eid]) >= 0:
            continue                                   # player driven
        if not e.has(eid, "combat"):
            _civ(world, eid, civ_units, foe_arr, dt)
            continue

        fac = int(faction[eid])
        d = defs.UNIT_DEFS[t]
        speed = d["speed"]
        eye = (pos[eid, 0], pos[eid, 1] + d["height"] * 0.75, pos[eid, 2])

        timer[eid] -= dt
        tid = int(target[eid])
        if timer[eid] <= 0.0:
            timer[eid] = ACQUIRE * world.rng.uniform(0.75, 1.35)
            tid = _acquire(world, eid, foe_arr[fac], eye, d)
            target[eid] = tid
        if tid >= 0:
            if not e.alive[tid] or int(faction[tid]) == fac:
                target[eid] = -1
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
                spot_x[eid] = float(tpos[0])
                spot_y[eid] = float(tpos[1])
                spot_z[eid] = float(tpos[2])
                spot_t[eid] = SPOT_MEMORY
                _engage(world, eid, tid, eye, d, dist, tpos, tvel, dt)
        if not seen and float(spot_t[eid]) > 0.0:
            # lost it: hose the spot where it was last seen
            spot_t[eid] = float(spot_t[eid]) - dt
            dist = math.dist((eye[0], eye[2]), (float(spot_x[eid]), float(spot_z[eid])))
            if dist < d["ai_range"]:
                _suppress(world, eid, eye, d, dist, dt)
                moving = False

        if moving:
            trigger[eid] = 0
            _advance(world, eid, speed, dt)


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


def _engage(world: World, eid: int, tid: int, eye: tuple[float, float, float],
            d: dict, dist: float, tpos: np.ndarray, tvel: np.ndarray,
            dt: float) -> None:
    e = world.ecs
    weapon = e["weapon"]

    # aim at the intercept point of the primary weapon.  no gravity lift here:
    # the fire control zeroes the gun for the drop at shot time, so lifting the
    # aim as well would double compensate
    w0 = int(weapon[eid, 0]) or defs.WID_AC30
    wd0 = defs.WEAPONS[w0]
    speed = wd0["vel"]
    aim = mu.solve_lead(eye, (float(tpos[0]), float(tpos[1]) + 2.0, float(tpos[2])),
                        tvel, speed, 0.0)
    _aim_and_fire(world, eid, eye, d, dist, aim, dt)


def _suppress(world: World, eid: int, eye: tuple[float, float, float], d: dict,
              dist: float, dt: float) -> None:
    """Keep firing at the spot where the target was last seen."""
    e = world.ecs
    aim = (float(e["spot_x"][eid]), float(e["spot_y"][eid]) + 1.5, float(e["spot_z"][eid]))
    _aim_and_fire(world, eid, eye, d, dist, aim, dt)


def _aim_and_fire(world: World, eid: int, eye: tuple[float, float, float],
                  d: dict, dist: float, aim: tuple[float, float, float],
                  dt: float) -> None:
    e = world.ecs
    yaw = e["yaw"]
    pitch = e["pitch"]
    turret = e["turret_yaw"]
    trigger = e["trigger"]
    etype = int(e["etype"][eid])
    hp = e["hp"]
    weapon = e["weapon"]

    tx = aim[0] - eye[0]
    ty = aim[1] - eye[1]
    tz = aim[2] - eye[2]
    want_yaw, want_pitch = mu.angles_to(tx, ty, tz)

    turn = 3.2 if etype != defs.ETYPE_MECH else 1.7
    yaw[eid] = mu.turn_toward(float(yaw[eid]), want_yaw, turn * dt)
    pitch[eid] = max(-0.4, min(0.7, float(pitch[eid]) + (want_pitch - float(pitch[eid])) * min(1.0, 6.0 * dt)))
    if etype == defs.ETYPE_APC:
        turret[eid] = mu.turn_toward(float(turret[eid]), want_yaw, 2.2 * dt)

    aim_yaw = float(turret[eid]) if etype == defs.ETYPE_APC else float(yaw[eid])
    err = abs(mu.wrap_pi(want_yaw - aim_yaw))
    t = 0
    if err < 0.14:
        if hp[eid, 0] > 0:
            t |= 1
        if etype == defs.ETYPE_MECH and hp[eid, defs.MOD_RIGHT] > 0 and dist < 2200:
            w1 = int(weapon[eid, 1])
            if w1 and defs.WEAPONS[w1]["kind"] in ("rocket", "saclos") and dist < defs.WEAPONS[w1]["range"]:
                t |= 2
        if etype == defs.ETYPE_APC and dist < 3500:
            t |= 2
    # a semi automatic arm only fires on the rising edge of the trigger, so
    # the AI has to let go of it every other tick
    if (t & 2) and (world.tick & 1) and defs.WEAPONS[int(weapon[eid, 1])]["semi"]:
        t &= ~2
    trigger[eid] = t


def _advance(world: World, eid: int, speed: float, dt: float) -> None:
    e = world.ecs
    pos = e["pos"]
    vel = e["vel"]
    terrain = world.terrain
    fac = int(e["faction"][eid])
    lane_i = int(e["lane"][eid])
    pts = world.lanes[lane_i % len(world.lanes)]
    if fac == defs.FACTION_BLUE:
        pts = world.lanes_rev[lane_i % len(world.lanes)]
    i = int(e["path_i"][eid])
    if i >= len(pts):
        i = len(pts) - 1
    wx, wz = pts[i]
    dx = wx - pos[eid, 0]
    dz = wz - pos[eid, 2]
    if dx * dx + dz * dz < 55.0 * 55.0:
        if i < len(pts) - 1:
            i += 1
            e["path_i"][eid] = i
            wx, wz = pts[i]
            dx = wx - pos[eid, 0]
            dz = wz - pos[eid, 2]
        elif i >= len(pts) - 1:
            bx, bz = terrain.bases[1 - fac]
            dx, dz = bx - pos[eid, 0], bz - pos[eid, 2]
    d = math.hypot(dx, dz)
    if d < 1e-3:
        vel[eid, 0] = vel[eid, 2] = 0.0
        return
    dx, dz = dx / d, dz / d
    # simple obstacle avoidance: fan out when the step ahead is blocked
    step = 14.0
    if terrain.blocked(pos[eid, 0] + dx * step, pos[eid, 2] + dz * step):
        base = math.atan2(dz, dx)
        for off in (0.5, -0.5, 1.0, -1.0, 1.6, -1.6, 2.2, -2.2):
            a = base + off
            nx, nz = math.cos(a), math.sin(a)
            if not terrain.blocked(pos[eid, 0] + nx * step, pos[eid, 2] + nz * step):
                dx, dz = nx, nz
                break
    speed *= 1.0
    vel[eid, 0] = dx * speed
    vel[eid, 2] = dz * speed


def _civ(world: World, eid: int, civ_units: list[int],
         foe_arr: dict[int, np.ndarray], dt: float) -> None:
    e = world.ecs
    pos = e["pos"]
    vel = e["vel"]
    timer = e["ai_timer"]
    timer[eid] -= dt
    if timer[eid] > 0.0:
        return
    timer[eid] = world.rng.uniform(1.5, 4.5)
    hx = float(e["home_x"][eid])
    hz = float(e["home_z"][eid])
    dx = hx - pos[eid, 0]
    dz = hz - pos[eid, 2]
    if dx * dx + dz * dz > 240.0 * 240.0:
        a = math.atan2(dz, dx)
    else:
        a = world.rng.random() * math.tau
    vel[eid, 0] = math.cos(a) * 2.0
    vel[eid, 2] = math.sin(a) * 2.0
    e["yaw"][eid] = a
