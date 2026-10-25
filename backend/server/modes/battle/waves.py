"""Battle mode: town capture and the reinforcement waves behind the front.

Both sides count along their own copy of a lane, so "advanced" always means
the largest index in that faction's list, never a mirrored position.
"""

from __future__ import annotations

import math
from typing import TYPE_CHECKING

import numpy as np

from ... import defs

if TYPE_CHECKING:
    from ...world import World

WAVE_INTERVAL = 22.0
CAPTURE_TIME = 9.0
CAPTURE_RADIUS = 170.0
UNIT_CAP = 120


def run(world: World, dt: float) -> None:
    if world.winner >= 0:
        return
    _capture(world, dt)
    _waves(world, dt)


def _capture(world: World, dt: float) -> None:
    e = world.ecs
    ids = world.combat_units()
    if ids.size == 0:
        return
    pos = e["pos"]
    fac = e["faction"][ids]
    px = pos[ids, 0]
    pz = pos[ids, 2]
    for town in world.towns:
        near = (np.hypot(px - town["x"], pz - town["z"]) < CAPTURE_RADIUS)
        if not near.any():
            if town["owner"] >= 0:
                town["progress"] = max(0.0, town["progress"] - dt * 0.5)
            continue
        f = fac[near]
        red = bool((f == defs.FACTION_RED).any())
        blue = bool((f == defs.FACTION_BLUE).any())
        if red == blue:
            town["progress"] = max(0.0, town["progress"] - dt * 0.5)
            continue
        holder = defs.FACTION_RED if red else defs.FACTION_BLUE
        if town["owner"] == holder:
            town["progress"] = 0.0
            continue
        town["progress"] += dt
        if town["progress"] >= CAPTURE_TIME:
            town["owner"] = holder
            town["progress"] = 0.0
            world.emit("capture", x=town["x"], z=town["z"], faction=int(holder))


def _faction_count(world: World, faction: int) -> int:
    e = world.ecs
    ids = world.combat_units()
    return int((e["faction"][ids] == faction).sum())


def _front_index(world: World, faction: int, lane: int) -> int | None:
    """Lane index of the most advanced friendly unit on that lane (or None)."""
    e = world.ecs
    best = None
    for eid in world.combat_units():
        if int(e["faction"][eid]) != faction or int(e["etype"][eid]) == defs.ETYPE_BASE:
            continue
        if int(e["lane"][eid]) % len(world.lanes) != lane:
            continue
        i = int(e["path_i"][eid])
        best = i if best is None else max(best, i)
    return best


def _reinforcement_point(world: World, faction: int, lane: int) -> tuple[float, float]:
    """Waves are airlifted to just behind the current front line so the two
    sides keep meeting instead of walking 14 km towards each other."""
    base = world.terrain.bases[faction]
    pts = world.lanes[lane]
    if faction == defs.FACTION_BLUE:
        pts = world.lanes_rev[lane]
    i = _front_index(world, faction, lane)
    if i is None:
        return world._spawn_point(faction, base)
    i = max(0, min(len(pts) - 1, i - 10))
    px, pz = pts[i]
    for _ in range(16):
        ang = world.rng.random() * math.tau
        r = world.rng.uniform(20.0, 90.0)
        x, z = px + math.cos(ang) * r, pz + math.sin(ang) * r
        if world._clear_spot(x, z):
            return x, z
    return px, pz


def _waves(world: World, dt: float) -> None:
    world.wave_timer -= dt
    if world.wave_timer > 0.0:
        return
    world.wave_timer = WAVE_INTERVAL
    world.wave += 1
    n_lanes = len(world.lanes)
    for faction in (defs.FACTION_RED, defs.FACTION_BLUE):
        if _faction_count(world, faction) > UNIT_CAP:
            continue
        for lane in range(n_lanes):
            x, z = _reinforcement_point(world, faction, lane)
            comp = [defs.ETYPE_INF_RIFLE] * 4 + [defs.ETYPE_INF_ROCKET]
            if world.wave % 3 == 0:
                comp.append(defs.ETYPE_APC)
            if world.wave % 4 == 0:
                comp.append(defs.ETYPE_MECH)
            for etype in comp:
                sx, sz = x, z
                for _ in range(8):
                    ang = world.rng.random() * math.tau
                    r = world.rng.uniform(5.0, 45.0)
                    sx = x + math.cos(ang) * r
                    sz = z + math.sin(ang) * r
                    if world._clear_spot(sx, sz, 3.0):
                        break
                eid = world.spawn_unit(etype, faction, sx, sz, lane=lane)
                e = world.ecs
                e["path_i"][eid] = world.lane_index_near(lane, sx, sz, faction)
                e["yaw"][eid] = math.atan2(
                    (world.terrain.bases[1 - faction][1] - sz),
                    (world.terrain.bases[1 - faction][0] - sx))
                e["leg_yaw"][eid] = e["yaw"][eid]
