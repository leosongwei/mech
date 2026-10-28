"""Projectile integration, guidance, collision and damage application."""

from __future__ import annotations

import logging
import math
from typing import TYPE_CHECKING

import numpy as np

from .. import defs, mathutil as mu
from . import smoke
from .weapons import aim_of

if TYPE_CHECKING:
    from ..world import World

log = logging.getLogger("mech.smoke")


def _norm(v: np.ndarray | tuple[float, float, float]) -> tuple[tuple[float, float, float], float]:
    n = math.sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2])
    if n < 1e-6:
        return (1.0, 0.0, 0.0), 0.0
    return (v[0] / n, v[1] / n, v[2] / n), n


def _steer(v: np.ndarray, desired: tuple[float, float, float], speed: float,
           turn_rate: float, dt: float) -> tuple[float, float, float]:
    cur, _ = _norm(v)
    dn, want = _norm(desired)
    if want < 1e-3:                     # already on the sight line
        return (cur[0] * speed, cur[1] * speed, cur[2] * speed)
    dot = max(-1.0, min(1.0, cur[0] * dn[0] + cur[1] * dn[1] + cur[2] * dn[2]))
    ang = math.acos(dot)
    if ang < 1e-4:
        nd = dn
    else:
        f = min(1.0, turn_rate * dt / ang)
        nd, _ = _norm((cur[0] + (dn[0] - cur[0]) * f,
                       cur[1] + (dn[1] - cur[1]) * f,
                       cur[2] + (dn[2] - cur[2]) * f))
    return (nd[0] * speed, nd[1] * speed, nd[2] * speed)


def run(world: World, dt: float) -> None:
    e = world.ecs
    pids = world.projectiles()
    if pids.size == 0:
        return
    units = world.units()
    terrain = world.terrain
    pos = e["pos"]
    vel = e["vel"]
    faction = e["faction"]
    kind = e["proj_kind"]
    wid = e["proj_wid"]
    life = e["proj_life"]
    src = e["proj_src"]
    age = e["proj_age"]
    seed = e["proj_seed"]
    prev = e["proj_prev"]
    target = e["target"]
    etype = e["etype"]
    yaw = e["yaw"]

    # ---- hit spheres of every unit, gathered once per tick --------------
    sph = sphere_table(world)

    for pid in pids:
        if not e.alive[pid]:
            continue
        wd = defs.WEAPONS[int(wid[pid])]
        k = int(kind[pid])
        age[pid] = float(age[pid]) + dt
        life[pid] = float(life[pid]) - dt
        if life[pid] <= 0.0:
            e.destroy(pid)
            continue

        p0 = pos[pid].copy()
        prev[pid] = p0                              # the leg the prox fuse solves
        v = vel[pid].copy()
        sp, _ = _norm(v)
        speed = float(np.linalg.norm(v))

        if k == 0:                                  # ballistic bullet
            v[1] -= wd["g"] * dt
        elif k == 1:                                # unguided rocket
            speed = min(wd["maxvel"], speed + wd["thrust"] * dt)
            v = np.asarray(sp, np.float32) * speed
            v[1] -= wd["g"] * dt
        elif k == 2:                                # SACLOS, rides the sight line
            shooter = int(src[pid])
            aim_pt = None
            if shooter >= 0 and e.alive[shooter] and int(etype[shooter]) not in (defs.ETYPE_BASE,):
                origin, ay, ap = aim_of(world, shooter)
                d = mu.dir_from(ay, ap)
                rel = p0 - np.asarray(origin, np.float32)
                along = float(rel[0] * d[0] + rel[1] * d[1] + rel[2] * d[2])
                # steer for a point well *ahead* on the sight line: the foot of
                # the perpendicular sits 90 deg off the round's heading, so
                # aiming there commands a full-rate turn for any lateral error
                # and the round slithers across the beam instead of settling.
                lead = max(0.0, along) + max(90.0, speed * 1.2)
                aim_pt = (origin[0] + d[0] * lead, origin[1] + d[1] * lead,
                          origin[2] + d[2] * lead)
            speed = min(wd["maxvel"], speed + wd["thrust"] * dt)
            if aim_pt is not None:
                desired = (aim_pt[0] - p0[0], aim_pt[1] - p0[1], aim_pt[2] - p0[2])
                v = np.asarray(_steer(v, desired, speed, 1.35, dt), np.float32)
                # thrust misalignment / crosswind: a slow push square to the
                # sight line, seeded per round, that the guidance has to chase
                # down. the round wobbles on the way out instead of cutting a
                # perfect line; the amplitude stays bounded because the beam
                # always wins -- spread alone would just be noise
                wob = wd.get("wob", 0.0)
                side = math.hypot(d[0], d[2])
                if wob and side > 1e-3:
                    s = float(seed[pid])                       # random per round
                    f = wd.get("wob_f", 1.0) * (0.75 + 0.5 * s)
                    ph = s * math.tau
                    a = (wob * (0.7 + 0.6 * ((s * 3.7) % 1.0))
                         * (math.sin(age[pid] * f * 2.1 + ph)
                            + 0.55 * math.sin(age[pid] * f * 5.3 + ph * 2.3)))
                    v[0] -= d[2] / side * a * dt
                    v[2] += d[0] / side * a * dt
            else:
                v = np.asarray(sp, np.float32) * speed
        elif k == 3:                                # top attack IR missile
            tid = int(target[pid])
            speed = min(wd["maxvel"], speed + wd["thrust"] * dt)
            # the seeker needs to see the hull: a smoke puff or a ridge on the
            # line of sight blinds it, and the round coasts on its last heading
            # like it does when the target dies
            why = ""
            if tid >= 0 and e.alive[tid]:
                tp = pos[tid]
                th = defs.UNIT_DEFS.get(int(etype[tid]), {}).get("height", 6.0)
                # what the seeker looks at: the roof it is going to dive on
                sight = (float(tp[0]), float(tp[1]) + th * 0.92, float(tp[2]))
                if smoke.blocks(world, p0, sight):
                    why = "smoke"
                elif not terrain.los(p0, sight, 8.0):
                    why = "terrain"
            if why:
                # the seeker is blind: log it, the smoke test on the range is
                # read straight out of the server log
                log.info("seeker of missile %d lost the lock on mech %d: %s "
                         "on the line of sight (%.0f m out, %.1f s in the air)",
                         pid, tid, why, math.dist(p0, sight), float(age[pid]))
                world.emit("seeker", x=float(p0[0]), y=float(p0[1]), z=float(p0[2]),
                           faction=int(faction[pid]), tid=int(tid), proj=int(pid),
                           why=why, dist=round(math.dist(p0, sight), 1))
                target[pid] = -1
                tid = -1
            if tid >= 0 and e.alive[tid]:
                tp = pos[tid]
                th = defs.UNIT_DEFS.get(int(etype[tid]), {}).get("height", 6.0)
                horiz = math.hypot(float(tp[0]) - p0[0], float(tp[2]) - p0[2])
                # pop up to `climb` above the target while there is still
                # `dive` metres to run, then break over onto the roof: steering
                # at a point `cruise` of the way out keeps the climb steep
                if horiz < float(wd.get("dive", 520.0)):
                    aim_pt = (float(tp[0]), float(tp[1]) + th * 0.92, float(tp[2]))
                else:
                    f = float(wd.get("cruise", 0.35))
                    aim_pt = (p0[0] + (float(tp[0]) - p0[0]) * f,
                              float(tp[1]) + float(wd.get("climb", 260.0)),
                              p0[2] + (float(tp[2]) - p0[2]) * f)
                desired = (aim_pt[0] - p0[0], aim_pt[1] - p0[1], aim_pt[2] - p0[2])
                v = np.asarray(_steer(v, desired, speed, 1.9, dt), np.float32)
            else:
                v = np.asarray(sp, np.float32) * speed
                v[1] -= 9.81 * dt
        vel[pid] = v
        p1 = p0 + v * dt
        pos[pid] = p1

        # ---- terrain / building collision --------------------------------
        gh, gt, gp = terrain.ground_hit(p0, p1, 9.0)
        best_t = gt if gh else 2.0
        best_unit = -1
        best_mod = 0
        # ---- unit collision ---------------------------------------------
        if sph[0].shape[0]:
            seg = p1 - p0
            l2 = float(seg @ seg)
            if l2 > 1e-6:
                rel = sph[0] - p0
                tt = np.clip((rel @ seg) / l2, 0.0, 1.0)
                closest = p0 + seg[None, :] * tt[:, None]
                dd = np.linalg.norm(sph[0] - closest, axis=1)
                pr = 0.35 if k == 0 else 0.6
                m = dd < (sph[1] + pr)
                m &= sph[4] != int(faction[pid])
                if int(age[pid]) < 0.12:
                    m &= sph[2] != int(src[pid])
                if m.any():
                    cand = np.nonzero(m)[0]
                    j = int(cand[np.argmin(tt[cand])])
                    if float(tt[j]) < best_t:
                        best_t = float(tt[j])
                        best_unit = int(sph[2][j])
                        best_mod = int(sph[3][j])
        if best_t <= 1.0:
            hitp = (float(p0[0] + (p1[0] - p0[0]) * best_t),
                    float(p0[1] + (p1[1] - p0[1]) * best_t),
                    float(p0[2] + (p1[2] - p0[2]) * best_t))
            _impact(world, hitp, wd, pid, best_unit, best_mod, sph)
            e.destroy(pid)


def sphere_table(world: World) -> tuple:
    """``(centres, radii, units, modules, factions)`` for every unit this tick.

    One pass over the battlefield per tick, shared by the collision sweep and
    every blast - rebuilding it per impact is what made a rocket into a crowd
    cost a full pass each time."""
    e = world.ecs
    units = world.units()
    pos = e["pos"]
    yaw = e["yaw"]
    etype = e["etype"]
    faction = e["faction"]
    sc = []
    sr = []
    su = []
    sm = []
    sf = []
    for uid in units:
        et = int(etype[uid])
        uid = int(uid)
        for mod, c, r in mu.unit_spheres(et, pos[uid], float(yaw[uid])):
            sc.append(c)
            sr.append(r)
            su.append(uid)
            sm.append(mod)
            sf.append(int(faction[uid]))
    return (np.asarray(sc, np.float32).reshape(-1, 3),
            np.asarray(sr, np.float32),
            np.asarray(su, np.int32),
            np.asarray(sm, np.int32),
            np.asarray(sf, np.int32))


def _impact(world: World, p: tuple[float, float, float], wd: dict, proj_id: int,
            direct_unit: int = -1, direct_mod: int = 0,
            sph: tuple | None = None) -> None:
    e = world.ecs
    shooter = int(e["proj_src"][proj_id])
    pid = int(e["owner"][shooter]) if shooter >= 0 and e.alive[shooter] else -1
    if pid < 0 and shooter >= 0:
        pid = -1
    r = float(wd["er"])
    world.emit("boom", x=p[0], y=p[1], z=p[2], r=r, wid=int(wd["wid"]),
               big=1 if r >= 5.0 else 0, proj=int(proj_id))
    if direct_unit >= 0:
        world.hit(direct_unit, direct_mod, wd, 0.0, pid)
    if r <= 0.0 or float(wd["ed"]) <= 0.0:
        return
    sfact = int(e["faction"][shooter]) if shooter >= 0 and e.alive[shooter] else -1
    # One distance per hit sphere, straight off the table the run loop already
    # built this tick: rebuilding every unit's spheres per impact made a single
    # burst cost a full pass over the battlefield, which is what a rocket into a
    # crowd turned into a 30 ms tick.
    sph_c, sph_r, sph_u, sph_m, sph_f = sph if sph is not None else sphere_table(world)
    d = np.sqrt(((sph_c - np.asarray(p, np.float32)) ** 2).sum(axis=1)) - sph_r
    hit = d <= r
    if direct_unit >= 0:
        hit &= sph_u != direct_unit
    if sfact >= 0:
        hit &= sph_f != sfact
    if not hit.any():
        return
    # one splash per unit, from its closest sphere
    idx = np.nonzero(hit)[0]
    order = idx[np.lexsort((d[idx], sph_u[idx]))]
    first = np.ones(order.size, bool)
    first[1:] = sph_u[order][1:] != sph_u[order][:-1]
    for k in order[first]:
        world.splash(int(sph_u[k]), int(sph_m[k]), wd, max(float(d[k]), 0.0), pid)
