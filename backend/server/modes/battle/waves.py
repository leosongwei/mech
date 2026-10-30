"""Battle mode: town capture and the reinforcements behind the front.

Both sides count along their own copy of a lane, so "advanced" always means
the largest index in that faction's list, never a mirrored position.

A lane supplies from its **front town only** - the furthest one still
connected to its own command base.  red - red - red - blue - red means the
last red town before the cut raises the troops and everything past the cut
(the captured town included) is silent.  A town with enemy hulls inside its
capture ring supplies nothing either: the column is raised one town further
back until the fight is over.
"""

from __future__ import annotations

import math
from typing import TYPE_CHECKING

import numpy as np

from ... import defs

if TYPE_CHECKING:
    from ...world import World

MECH_INTERVAL = 120.0         # command base: one mech down every lane
INF_INTERVAL = 60.0           # per supplying town: one infantry squad
APC_INTERVAL = 60.0           # per supplying town: one APC
INF_SQUAD = [defs.ETYPE_INF_ROCKET] * 6 + [defs.ETYPE_INF_RIFLE] * 4
CAPTURE_TIME = 9.0
CAPTURE_RADIUS = 170.0


def bind(world: World) -> None:
    """Attach every town to the lane it sits on and give it its own timers."""
    for town in world.towns:
        lane, i = _lane_of(world, town["x"], town["z"])
        town["lane"] = lane
        town["lane_i"] = i
        town["inf_t"] = INF_INTERVAL
        town["apc_t"] = APC_INTERVAL
        town["contested"] = False


def _lane_of(world: World, x: float, z: float) -> tuple[int, int]:
    """Lane and red-side way point index of the point nearest to (x, z)."""
    best = (0, 0, 1e18)
    for lane, pts in enumerate(world.lanes):
        for i, (px, pz) in enumerate(pts):
            d = (px - x) ** 2 + (pz - z) ** 2
            if d < best[2]:
                best = (lane, i, d)
    return best[0], best[1]


def run(world: World, dt: float) -> None:
    if world.winner >= 0:
        return
    _capture(world, dt)
    _base_waves(world, dt)
    _town_waves(world, dt)


def _capture(world: World, dt: float) -> None:
    e = world.ecs
    ids = world.combat_units()
    for town in world.towns:
        town["contested"] = False          # recomputed below, never stale
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
        # someone the town does not belong to is standing in the ring: the place
        # is being fought over, and no column is raised out of a firefight
        town["contested"] = bool((f != town["owner"]).any())
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


def _chain(world: World, faction: int, lane: int) -> list[dict]:
    """The towns on the lane still connected to the faction's base, listed the
    way that faction walks them: base end first, the front town last."""
    chain = [t for t in world.towns if t.get("lane") == lane]
    chain.sort(key=lambda t: t["lane_i"])              # red's base first
    if faction == defs.FACTION_BLUE:
        chain.reverse()                                # blue walks the other way
    out = []
    for town in chain:
        if town["owner"] != faction:
            break                                      # cut off from the base
        out.append(town)
    return out


def front_town(world: World, faction: int, lane: int) -> dict | None:
    """The furthest town on the lane that can still raise a column.

    The depots behind the front have already handed their troops forward, so a
    lane raises one column at a time instead of a column from every town it
    owns.  A captured town cuts the chain - the front falls back to the last
    town before it - and a town with the enemy inside its ring supplies
    nothing either, so the column comes from the one behind it until the
    fight is over.
    """
    for town in reversed(_chain(world, faction, lane)):
        if not town["contested"]:
            return town
    return None


def respawn_town(world: World, faction: int, x: float, z: float) -> dict | None:
    """The town a pilot who died at (x, z) comes back at.

    Three steps: **A**, the faction's own town nearest the hull that died (any
    lane - it only picks the lane); **B**, the town on A's lane nearest the
    death that is still connected to the base *and* not being fought over (A
    itself when it qualifies); **C**, one town back from B towards the base.
    So a death at a town never comes back at that town, and a town cut off from
    the base or under attack is skipped.  None means the base itself.
    """
    own = [t for t in world.towns if t["owner"] == faction]
    if not own:
        return None
    dead = (x, z)
    a = min(own, key=lambda t: math.dist((t["x"], t["z"]), dead))
    available = [t for t in _chain(world, faction, a["lane"]) if not t["contested"]]
    if not available:
        return None
    b = min(available, key=lambda t: math.dist((t["x"], t["z"]), dead))
    chain = _chain(world, faction, a["lane"])          # base end first
    i = chain.index(b)
    return chain[i - 1] if i > 0 else None


def clear_near(world: World, x: float, z: float,
               r0: float, r1: float) -> tuple[float, float]:
    """A free spot in the ring around (x, z); the point itself if none is."""
    for _ in range(16):
        ang = world.rng.random() * math.tau
        r = world.rng.uniform(r0, r1)
        sx, sz = x + math.cos(ang) * r, z + math.sin(ang) * r
        if world._clear_spot(sx, sz):
            return sx, sz
    return x, z


def _send(world: World, etype: int, faction: int, lane: int, x: float, z: float) -> int:
    """Spawn one unit and point it down its lane at the enemy base."""
    eid = world.spawn_unit(etype, faction, x, z, lane=lane)
    e = world.ecs
    ex, ez = world.terrain.bases[1 - faction]
    e["yaw"][eid] = math.atan2(ez - z, ex - x)
    e["leg_yaw"][eid] = e["yaw"][eid]
    # fresh out of the gate the machine looks where it is pointed: a sight left
    # on the spawn's random heading would swing the hull round for nothing
    e["sight_yaw"][eid] = e["yaw"][eid]
    return eid


def _base_waves(world: World, dt: float) -> None:
    """One mech per lane from each command base, once a minute."""
    world.wave_timer -= dt
    if world.wave_timer > 0.0:
        return
    world.wave_timer = MECH_INTERVAL
    world.wave += 1
    for faction in (defs.FACTION_RED, defs.FACTION_BLUE):
        bx, bz = world.terrain.bases[faction]
        for lane in range(len(world.lanes)):
            x, z = clear_near(world, bx, bz, 70.0, 130.0)
            _send(world, defs.ETYPE_MECH, faction, lane, x, z)


def _town_waves(world: World, dt: float) -> None:
    """The front town of every lane raises the next column."""
    for faction in (defs.FACTION_RED, defs.FACTION_BLUE):
        for lane in range(len(world.lanes)):
            town = front_town(world, faction, lane)
            if town is None:
                continue
            town["inf_t"] -= dt
            town["apc_t"] -= dt
            if town["inf_t"] > 0.0 and town["apc_t"] > 0.0:
                continue
            x, z = clear_near(world, town["x"], town["z"], 40.0, 70.0)
            if town["inf_t"] <= 0.0:
                town["inf_t"] = INF_INTERVAL
                for etype in INF_SQUAD:
                    sx, sz = clear_near(world, x, z, 5.0, 45.0)
                    _send(world, etype, faction, lane, sx, sz)
            if town["apc_t"] <= 0.0:
                town["apc_t"] = APC_INTERVAL
                _send(world, defs.ETYPE_APC, faction, lane, x, z)
