"""Death: the one place a hull stops being a hull.

`combat.apply` takes hit points off and decides nothing; this system asks the
only question there is - is the pool empty - once per tick, for every unit on
the field, and then follows the type's own `death` recipe:

    world.destroy          the hull is gone (an entity that no longer exists is
                           the whole of "dead" - there is no state flag)
    _leave_wreck           what is left: a corpse, a smoking hulk, or nothing
    blasts.spawn           a wreck goes up, and the burst is judged next tick
    _award / _plan_respawn three match rules: kill money, the walk back, victory

Keeping the judgement here rather than inside `apply` is what makes the damage
path re-entrant-free: no system destroys an entity while walking its own list.
The price is that a hull at 0 hp still exists for the rest of the tick, which
is why this runs after every damage source (`blasts`) and before the mode's own
post-systems (`waves` counts hulls to take a town).
"""

from __future__ import annotations

from typing import TYPE_CHECKING

import numpy as np

from .. import components, defs
from . import blasts

if TYPE_CHECKING:
    from ..world import World


def run(world: World, dt: float) -> None:
    e = world.ecs
    ids = world.units()
    if ids.size == 0:
        return
    hp = e["hp"]
    for eid in ids[hp[ids] <= 0.0]:
        _kill(world, int(eid))


def _kill(world: World, eid: int) -> None:
    e = world.ecs
    if not e.alive[eid]:
        return
    etype = int(e["etype"][eid])
    faction = int(e["faction"][eid])
    pos = e["pos"][eid].copy()
    d = defs.UNIT_DEFS.get(etype) or {}
    dd = d.get("death") or {}
    blast = dd.get("blast")

    world.emit("boom", x=float(pos[0]), y=float(pos[1]), z=float(pos[2]),
               r=float(d.get("splat", 2.0)),
               big=1 if (blast or dd.get("loud") == "big") else 0,
               blast=1 if blast else 0,     # the client draws a fireball for this one
               faction=faction)

    credit = int(e["last_hit"][eid]) if e.has(eid, "last_hit") else -1
    _award(world, eid, credit, faction, d)
    _plan_respawn(world, eid, pos)
    if dd.get("ends_match"):
        winner = 1 - faction if faction in (defs.FACTION_RED, defs.FACTION_BLUE) else -1
        world.winner = winner
        world.end_timer = 40.0
        world.emit("gameover", winner=winner)

    _leave_wreck(world, dd.get("wreck"), faction, pos, float(e["yaw"][eid]))
    e.destroy(eid)
    if blast:
        # no sides spared: what stands within thirty metres of a hull that just
        # came apart is in the wrong place whatever colour it is painted
        blasts.spawn(world, pos, blast, friendly=True, pid=credit, faction=faction)


def _award(world: World, eid: int, pid: int, faction: int, d: dict) -> None:
    """Kill money, for a pilot on the other side - a hull is worth its `reward`."""
    if pid < 0 or pid not in world.players:
        return
    killer = world.players[pid]
    if killer.faction == faction or faction not in (defs.FACTION_RED, defs.FACTION_BLUE):
        return
    reward = int(d.get("reward", 0))
    killer.money += reward
    killer.kills += 1
    killer.score += reward
    owner = int(world.ecs["owner"][eid])
    world.emit("kill", pid=pid, victim=owner if owner >= 0 else -1,
               reward=reward, etype=int(world.ecs["etype"][eid]))


def _plan_respawn(world: World, eid: int, pos: np.ndarray) -> None:
    """Where it died decides how long the walk back is - the mode says how long."""
    e = world.ecs
    owner = int(e["owner"][eid])
    if owner < 0:
        return
    pl = world.players.get(owner)
    if pl is None:
        return
    pl.deaths += 1
    pl.mech = -1
    pl.death_pos = (float(pos[0]), float(pos[2]))
    pl.respawn = world.mode_mod.respawn_delay(world, pl, float(pos[0]), float(pos[2]))


def _leave_wreck(world: World, wreckspec, faction: int, pos: np.ndarray,
                 yaw: float) -> None:
    """What the dead thing leaves behind: `(etype, seconds, wire kind)` or None."""
    if not wreckspec:
        return
    dtype, life, kind = wreckspec
    e = world.ecs
    eid = e.create(components.DEBRIS_COMPS)
    e["pos"][eid] = (float(pos[0]), world.terrain.height_at(float(pos[0]), float(pos[2])),
                     float(pos[2]))
    e["yaw"][eid] = yaw
    e["leg_yaw"][eid] = yaw
    e["turret_yaw"][eid] = yaw
    e["etype"][eid] = dtype
    e["faction"][eid] = faction
    e["wreck_t"][eid] = life
    e["wreck_kind"][eid] = kind
