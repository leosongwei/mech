"""Range targets: passive APC hulks that hold a post or drive a patrol.

The system keeps the range populated: every slot is either a stationary hulk
or a moving one, and a destroyed slot comes back a few seconds later at a new
random spot, so the target count per kind never drops.  Nothing here shoots
back - that is what the range is for.
"""

from __future__ import annotations

import math
from typing import TYPE_CHECKING

from ... import defs
from ... import terrain as terrain_mod

if TYPE_CHECKING:
    from ...world import World

STATIC_TARGETS = 10
MOVING_TARGETS = 2
RESPAWN_DELAY = 3.0
PATROL_SPEED = 8.0

MARGIN = 300.0              # keep hulks off the map edge
MIN_PLAYER_DIST = 350.0     # never pop one in the player's face
MIN_TARGET_DIST = 140.0     # keep the hulks from stacking up on each other
MAX_SLOPE = 18.0            # metres of height change over a 25 m step
PATROL_MIN = 200.0
PATROL_MAX = 420.0

TILE = terrain_mod.TILE


def run(world: World, dt: float) -> None:
    world.targets.update(world, dt)


class Targets:
    def __init__(self, world: World) -> None:
        self.slots = []
        posts = list(terrain_mod.RANGE_POSTS)
        for post in posts:
            if post.get("patrol"):
                lo, hi = post["patrol"]
                self.slots.append(dict(role="moving", x=post["x"], z=post["z"],
                                       lo=lo, hi=hi, dir=1.0, eid=-1, t=0.0))
            else:
                self.slots.append(dict(role="static", x=post["x"], z=post["z"],
                                       eid=-1, t=0.0))
        assert sum(s["role"] == "static" for s in self.slots) == STATIC_TARGETS
        assert sum(s["role"] == "moving" for s in self.slots) == MOVING_TARGETS
        for slot in self.slots:
            slot["eid"] = _spawn(world, slot)

    def update(self, world: World, dt: float) -> None:
        e = world.ecs
        for slot in self.slots:
            eid = int(slot["eid"])
            # `alive` is not enough.  Entity ids are handed out LIFO, so the id a
            # dead target leaves behind is taken by the very next entity that
            # spawns - and for a hull going up that is its own blast (a burst
            # entity, no `vel`, no `moves`).  Driving that one raised KeyError
            # once a tick, and because the exception aborts this loop the slot
            # was never dropped: the whole range world froze.  Ask the tag the
            # drive code actually needs instead.
            if eid >= 0 and e.alive[eid] and e.has(eid, "moves"):
                _drive(world, slot, eid, dt)
                continue
            slot["eid"] = -1
            slot["t"] = float(slot["t"]) - dt
            if slot["t"] <= 0.0:
                _place(world, slot)
                slot["eid"] = _spawn(world, slot)


def _spawn(world: World, slot: dict) -> int:
    x = float(slot["x"])
    z = float(slot["z"])
    eid = world.spawn_unit(defs.ETYPE_APC, defs.FACTION_BLUE, x, z)
    # face the player's cross so the hulls read clearly through the sight
    sx, sz = world.terrain.bases[defs.FACTION_RED]
    yaw = math.atan2(sz - z, sx - x) if slot["role"] == "static" else _leg_yaw(slot)
    e = world.ecs
    e["yaw"][eid] = yaw
    e["leg_yaw"][eid] = yaw
    e["turret_yaw"][eid] = yaw
    return eid


def _leg_yaw(slot: dict) -> float:
    """Heading of a patrol: along x or z, whichever the span runs on."""
    if slot.get("axis", "x") == "x":
        return 0.0 if slot["dir"] > 0 else math.pi
    return -math.pi / 2 if slot["dir"] > 0 else math.pi / 2


def _drive(world: World, slot: dict, eid: int, dt: float) -> None:
    """Ask a patrolling hull for its speed - the hull drives itself.

    A target is a fixture, not a crew, but it is still a car: it is *told* a
    velocity per tick (`vel_cmd`, the same component a pilot's stick writes) and
    `movement` gets it there.  Writing `pos` here instead would put the hull's
    position out of step with its velocity, and every reader of the pair - the
    hit-sphere sweep, an IR seeker's lead, the wire - would be told it was
    standing still while it crawled past.
    """
    e = world.ecs
    cmd = e["vel_cmd"]
    cmd[eid, 0] = cmd[eid, 2] = 0.0
    if slot["role"] != "moving":
        return
    lo, hi = float(slot["lo"]), float(slot["hi"])
    d = float(slot["dir"])
    axis = slot.get("axis", "x")
    k = 0 if axis == "x" else 2
    v = float(e["pos"][eid, k])
    if v > hi:
        v, d = hi, -1.0
    elif v < lo:
        v, d = lo, 1.0
    slot["dir"] = d
    # the hull is told a speed down the patrol axis and drives itself there: at
    # the end of a leg that is a direction reversal, which the wheeled model
    # takes as a U-turn at its own radius (the steering is its state, not ours)
    cmd[eid, k] = d * PATROL_SPEED


def _place(world: World, slot: dict) -> None:
    """Pick a fresh random spot for a destroyed slot."""
    if slot["role"] == "moving":
        x, z, axis, lo, hi = _random_patrol(world)
        slot.update(x=x, z=z, axis=axis, lo=lo, hi=hi, dir=1.0)
    else:
        x, z = _random_spot(world)
        slot.update(x=x, z=z)


def _random_spot(world: World) -> tuple[float, float]:
    size = world.terrain.w * TILE
    px, pz = world.terrain.bases[defs.FACTION_RED]
    others = _occupied(world)
    for _ in range(60):
        x = world.rng.uniform(MARGIN, size - MARGIN)
        z = world.rng.uniform(MARGIN, size - MARGIN)
        if math.dist((x, z), (px, pz)) < MIN_PLAYER_DIST:
            continue
        if any(math.dist((x, z), p) < MIN_TARGET_DIST for p in others):
            continue
        if not _flat_enough(world, x, z):
            continue
        return x, z
    # nothing worked (a very crowded map): fall back to the plain far side
    return size * 0.75, size * 0.5


def _random_patrol(world: World) -> tuple[float, float, str, float, float]:
    """A patrol leg somewhere random: 400-840 m of straight, flat ground."""
    size = world.terrain.w * TILE
    x, z = _random_spot(world)
    axis = "x" if world.rng.random() < 0.5 else "z"
    half = world.rng.uniform(PATROL_MIN, PATROL_MAX)
    c = x if axis == "x" else z
    lo = max(MARGIN, c - half)
    hi = min(size - MARGIN, c + half)
    if hi - lo < 2.0 * PATROL_MIN:      # ran into the edge: use the other axis
        axis = "z" if axis == "x" else "x"
        c = z if axis == "z" else x
        lo = max(MARGIN, c - half)
        hi = min(size - MARGIN, c + half)
    return x, z, axis, lo, hi


def _occupied(world: World) -> list[tuple[float, float]]:
    """Spots a fresh target may not reuse: other live APCs and the wrecks of the
    ones just destroyed (a new hull on top of its own burning hulk reads as a
    single glitchy vehicle)."""
    e = world.ecs
    ids = world.all_units()
    kind = e["etype"][ids]
    take = (kind == defs.ETYPE_APC) | (kind == defs.ETYPE_DEBRIS) | (kind == defs.ETYPE_CORPSE)
    return [(float(e["pos"][eid, 0]), float(e["pos"][eid, 2])) for eid in ids[take]]


def _flat_enough(world: World, x: float, z: float) -> bool:
    h = world.terrain.height_at(x, z)
    for dx, dz in ((25.0, 0.0), (-25.0, 0.0), (0.0, 25.0), (0.0, -25.0)):
        if abs(world.terrain.height_at(x + dx, z + dz) - h) > MAX_SLOPE:
            return False
    return True
