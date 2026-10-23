"""Range mode: passive APC targets that hold a post or drive a patrol.

Only used when the server runs with ``--range``.  The targets never acquire or
fire, so every round can be judged against a stationary hull on the mountainside
and a moving one on the plain without return fire.  A destroyed target
reappears at its post a few seconds later, so a range session stays populated.
"""

from __future__ import annotations

import math

from .. import defs

RESPAWN_DELAY = 3.0
PATROL_SPEED = 8.0


def run(world, dt):
    if world.mode != "range":
        return
    for post in world.range_posts:
        eid = int(post["eid"])
        if eid >= 0 and world.ecs.alive[eid]:
            _drive(world, post, eid, dt)
            continue
        post["t"] = float(post["t"]) - dt
        if post["t"] <= 0.0:
            post["eid"] = _spawn(world, post)


def _spawn(world, post) -> int:
    x = float(post["x"])
    z = float(post["z"])
    eid = world.spawn_unit(defs.ETYPE_APC, defs.FACTION_BLUE, x, z)
    # face the player's cross so the hulls read clearly through the sight
    sx, sz = world.terrain.bases[defs.FACTION_RED]
    yaw = math.atan2(sz - z, sx - x)
    e = world.ecs
    e["yaw"][eid] = yaw
    e["leg_yaw"][eid] = yaw
    e["turret_yaw"][eid] = yaw
    return eid


def _drive(world, post, eid, dt):
    e = world.ecs
    pos = e["pos"]
    vel = e["vel"]
    vel[eid, 0] = 0.0
    vel[eid, 2] = 0.0
    span = post.get("patrol")
    if not span:
        return
    lo, hi = float(span[0]), float(span[1])
    d = float(post["dir"])
    x = float(pos[eid, 0])
    x += d * PATROL_SPEED * dt
    if x > hi:
        x, d = hi, -1.0
    elif x < lo:
        x, d = lo, 1.0
    post["dir"] = d
    pos[eid, 0] = x
    e["yaw"][eid] = 0.0 if d > 0 else math.pi
    e["leg_yaw"][eid] = e["yaw"][eid]
    e["turret_yaw"][eid] = e["yaw"][eid]
