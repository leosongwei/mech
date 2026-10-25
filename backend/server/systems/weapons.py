"""Weapon handling: magazines, reloads, cooldowns and projectile spawning.

The system only *fires* stored intent (the trigger bits) - aiming lives in the
player-input / AI systems, ballistics live in the projectile system.
"""

from __future__ import annotations

import math
from typing import TYPE_CHECKING

import numpy as np

from .. import ballistics, components, defs, mathutil as mu

if TYPE_CHECKING:
    from ..world import World

KIND_ID = {"bullet": 0, "rocket": 1, "saclos": 2, "top": 3}


def aim_of(world: World, eid: int) -> tuple[tuple[float, float, float], float, float]:
    """(origin, yaw, pitch) used by every weapon of this unit."""
    e = world.ecs
    pos = e["pos"][eid]
    etype = int(e["etype"][eid])
    if etype == defs.ETYPE_MECH:
        yaw = float(e["yaw"][eid])
        pitch = float(e["pitch"][eid])
        origin = (float(pos[0]), float(pos[1]) + defs.MECH_EYE, float(pos[2]))
        return origin, yaw, pitch
    if etype == defs.ETYPE_APC:
        yaw = float(e["turret_yaw"][eid])
        pitch = float(e["pitch"][eid])
        h = defs.UNIT_DEFS[etype]["height"]
        origin = (float(pos[0]), float(pos[1]) + h * 0.85, float(pos[2]))
        return origin, yaw, pitch
    h = defs.UNIT_DEFS.get(etype, {}).get("height", 1.8)
    return ((float(pos[0]), float(pos[1]) + h * 0.8, float(pos[2])),
            float(e["yaw"][eid]), float(e["pitch"][eid]))


def run(world: World, dt: float) -> None:
    e = world.ecs
    ids = world.units()
    if ids.size == 0:
        return
    cool = e["cool"]
    reload_t = e["reload_t"]
    mag = e["mag"]
    ammo = e["ammo"]
    weapon = e["weapon"]
    trigger = e["trigger"]
    trigger_prev = e["trigger_prev"]
    hp = e["hp"]
    etype = e["etype"]
    owner = e["owner"]

    for eid in ids:
        et = int(etype[eid])
        for arm in (0, 1):
            if cool[eid, arm] > 0.0:
                cool[eid, arm] = max(0.0, float(cool[eid, arm]) - dt)
            if reload_t[eid, arm] > 0.0:
                reload_t[eid, arm] = float(reload_t[eid, arm]) - dt
                if reload_t[eid, arm] <= 0.0:
                    wid = int(weapon[eid, arm])
                    wd = defs.WEAPONS[wid]
                    take = min(int(wd["mag"]), int(ammo[eid, arm]))
                    mag[eid, arm] = take
                    ammo[eid, arm] = int(ammo[eid, arm]) - take
                continue
            wid = int(weapon[eid, arm])
            if wid == defs.WID_NONE:
                continue
            if not (int(trigger[eid]) >> arm) & 1:
                continue
            if et == defs.ETYPE_MECH:
                if hp[eid, defs.MOD_LEFT if arm == 0 else defs.MOD_RIGHT] <= 0.0:
                    continue
            wd = defs.WEAPONS[wid]
            # semi automatic arms fire once per pull: the trigger has to be
            # released and pressed again, holding it does nothing
            if wd["semi"] and (int(trigger_prev[eid]) >> arm) & 1:
                continue
            if int(mag[eid, arm]) <= 0:
                if int(ammo[eid, arm]) > 0:
                    reload_t[eid, arm] = wd["reload"]
                continue
            # a hair of float dust left over from the tick subtraction must
            # not cost a whole tick: 600 rpm on a 30 Hz clock is exactly 3
            # ticks, and 0.1 - 3 * dt rounds to a positive 1e-9
            if float(cool[eid, arm]) > 1e-4:
                continue
            target = -1
            if wd["lock"]:
                pl = world.players.get(int(owner[eid]))
                if pl is None or pl.lock_target < 0 or pl.lock_progress < 1.0:
                    continue
                if not e.alive[pl.lock_target]:
                    continue
                target = pl.lock_target
            fire(world, eid, arm, wid, wd, target)
            mag[eid, arm] = int(mag[eid, arm]) - 1
            cool[eid, arm] = 60.0 / max(wd["rpm"], 1.0)
        trigger_prev[eid] = int(trigger[eid])


def fire(world: World, eid: int, arm: int, wid: int, wd: dict, target: int = -1) -> None:
    """Spawn the projectile(s) of one trigger pull."""
    e = world.ecs
    pos = e["pos"]
    etype = int(e["etype"][eid])
    origin, yaw, pitch = aim_of(world, eid)
    if etype == defs.ETYPE_MECH:
        origin = mu.muzzle_point(pos[eid], yaw, defs.MOD_LEFT if arm == 0 else defs.MOD_RIGHT, pitch)

    kind = KIND_ID.get(wd["kind"], 0)
    n = max(1, int(wd["salvo"]))
    life = float(wd["life"])

    if kind in (2, 3):
        # guided rounds: launch upward, guidance takes over immediately
        pitch = max(pitch, 0.18)

    # direct fire is zeroed: hold off for the drop so the round lands where
    # the muzzle points.  `bias` was the launcher's mechanical sighting and is
    # absorbed by the solution; guided rounds keep both as they are.
    zero = None
    if kind in (0, 1):
        zero = ballistics.zero_pitch(world, origin, yaw, pitch, wd, ignore=eid,
                                     faction=int(e["faction"][eid]))

    for k in range(n):
        sp = float(wd["spread"]) * (1.0 if k == 0 else 0.6)
        dy = yaw + world.rng.gauss(0.0, sp)
        # `bias` is the mechanical elevation the launcher is sighted at: the
        # rocket pod is tilted up so its slow round meets the cannon at 1 km
        dp = (pitch + float(wd["bias"]) if zero is None else zero) + world.rng.gauss(0.0, sp)
        d = mu.dir_from(dy, dp)
        pid = e.create()
        for c in components.PROJ_COMPS:
            e.add_comp(pid, c)
        e["pos"][pid] = origin
        e["vel"][pid] = (d[0] * wd["vel"], d[1] * wd["vel"], d[2] * wd["vel"])
        e["faction"][pid] = e["faction"][eid]
        e["proj_kind"][pid] = kind
        e["proj_wid"][pid] = wid
        e["proj_life"][pid] = life
        e["proj_src"][pid] = eid
        e["proj_age"][pid] = 0.0
        e["proj_seed"][pid] = world.rng.random()
        e["target"][pid] = target
    world.emit("shot", x=origin[0], y=origin[1], z=origin[2], wid=int(wid), eid=int(eid), arm=int(arm))
