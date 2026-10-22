"""AI for everything that is not player controlled.

Units acquire targets on a slow timer (0.45 s, LOS validated), engage with
lead & ballistic compensation and otherwise push along their lane towards the
enemy command base.
"""

from __future__ import annotations

import math

import numpy as np

from .. import defs, mathutil as mu

ACQUIRE = 0.45


def run(world, dt):
    e = world.ecs
    ids = world.units()
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

    terrain = world.terrain
    foes = {f: [] for f in (defs.FACTION_RED, defs.FACTION_BLUE)}
    civ_units = []
    for eid in ids:
        f = int(faction[eid])
        t = int(etype[eid])
        if t == defs.ETYPE_CIV:
            civ_units.append(eid)
        if f in foes and t != defs.ETYPE_CIV:
            foes[1 - f].append(eid)
    foe_arr = {f: np.asarray(v, np.int32) for f, v in foes.items()}

    for eid in ids:
        t = int(etype[eid])
        if int(owner[eid]) >= 0:
            continue                                   # player driven
        if t == defs.ETYPE_BASE:
            continue
        if t == defs.ETYPE_CIV:
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
        if tid >= 0:
            tpos = pos[tid]
            tvel = vel[tid]
            dist = math.dist((eye[0], eye[2]), (float(tpos[0]), float(tpos[2])))
            if dist < d["ai_range"] and terrain.los(eye, (float(tpos[0]), float(tpos[1]) + d["height"] * 0.6, float(tpos[2]))):
                moving = dist > d["ai_range"] * 0.55
                _engage(world, eid, tid, eye, d, dist, tpos, tvel, dt)
            else:
                moving = True

        if moving:
            trigger[eid] = 0
            _advance(world, eid, speed, dt)

    _turn_legs(world, dt)


def _acquire(world, eid, foe_ids, eye, d):
    """Nearest hostile unit with line of sight."""
    if foe_ids.size == 0:
        return -1
    e = world.ecs
    pos = e["pos"]
    ds = np.hypot(pos[foe_ids, 0] - pos[eid, 0], pos[foe_ids, 2] - pos[eid, 2])
    order = np.argsort(ds)[:6]
    terrain = world.terrain
    for k in order:
        if ds[k] > d["sight"]:
            break
        tid = int(foe_ids[k])
        tp = pos[tid]
        if terrain.los(eye, (float(tp[0]), float(tp[1]) + 1.5, float(tp[2]))):
            return tid
    return -1


def _engage(world, eid, tid, eye, d, dist, tpos, tvel, dt):
    e = world.ecs
    yaw = e["yaw"]
    pitch = e["pitch"]
    turret = e["turret_yaw"]
    trigger = e["trigger"]
    etype = int(e["etype"][eid])
    hp = e["hp"]
    weapon = e["weapon"]

    # aim at the intercept point of the primary weapon
    w0 = int(weapon[eid, 0]) or defs.WID_AC30
    wd0 = defs.WEAPONS[w0]
    speed = wd0["vel"]
    aim = mu.solve_lead(eye, (float(tpos[0]), float(tpos[1]) + 2.0, float(tpos[2])),
                        tvel, speed, wd0["g"])
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
    trigger[eid] = t


def _advance(world, eid, speed, dt):
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


def _civ(world, eid, civ_units, foe_arr, dt):
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


def _turn_legs(world, dt):
    e = world.ecs
    ids = world.units()
    vel = e["vel"]
    leg = e["leg_yaw"]
    etype = e["etype"]
    for eid in ids:
        t = int(etype[eid])
        if t == defs.ETYPE_BASE:
            continue
        vx = float(vel[eid, 0])
        vz = float(vel[eid, 2])
        if abs(vx) < 0.05 and abs(vz) < 0.05:
            continue
        want = math.atan2(vz, vx)
        rate = 2.6 if t == defs.ETYPE_MECH else 6.0
        leg[eid] = mu.turn_toward(float(leg[eid]), want, rate * dt)
