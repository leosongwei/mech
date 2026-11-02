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

The second channel is the same question asked of the *rounds in the air*: a
missile carries its own pool of health (`components.MISSILE_COMPS`), so a 30 mm
burst that fused on it takes it off the field a tick later - `_kill_round`,
which is deliberately not `_kill`: no warhead, no wreck, no credit.
"""

from __future__ import annotations

import logging
from typing import TYPE_CHECKING

import numpy as np

from .. import components, defs
from . import blasts

if TYPE_CHECKING:
    from ..world import World

log = logging.getLogger("mech.intercept")


def run(world: World, dt: float) -> None:
    e = world.ecs
    ids = world.units()
    if ids.size:
        hp = e["hp"]
        for eid in ids[hp[ids] <= 0.0]:
            _kill(world, int(eid))
    # second channel: a round in the air carries health too.  Same question,
    # same one-writer rule - `combat.apply` took the points off, this decides
    # that the pool is empty - but a very different death: a shot-down missile
    # does not go up as a warhead.
    mis = world.interceptable()
    if mis.size:
        hp = e["hp"]
        for eid in mis[hp[mis] <= 0.0]:
            _kill_round(world, int(eid))


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
    feed = _feed(world, eid, d, credit)
    _award(world, eid, credit, faction, d, feed)
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


def _kill_round(world: World, eid: int) -> None:
    """A missile is holed: it falls out of the sky, it does not detonate.

    No warhead, no wreck, no kill money, no bearing on the match.  That is
    deliberate: a SACLOS carries ED 80 over 6 m, so a round destroyed 300 m out
    that still went off would make shooting it down pointless - the point of the
    intercept is that the *guidance* dies with the round.  Two pilots are told the
    identity of the shooter and the round's owner (`pid` / `firer`) so a client
    can address anything it wants to them, and nothing else is owed: a battle has
    AI crews shooting missiles down all over the map, so the event carries no cue
    of its own - the round's small `boom` (emitted just below) is the whole of
    what anybody hears, wherever they are.
    """
    e = world.ecs
    if not e.alive[eid]:
        return
    wid = int(e["proj_wid"][eid])
    wd = defs.WEAPONS[wid] if 0 <= wid < len(defs.WEAPONS) else {}
    pos = e["pos"][eid].copy()
    # who killed it: `combat.apply` credited the round that took the last point
    # off (`last_hit` is a missile's own column, -1 when an AI crew fired it)
    pid = int(e["last_hit"][eid])
    # who fired it: the hull behind `proj_src`.  A missile outlives the hull
    # that launched it, so a dead launcher answers -1 and its pilot is told
    # nothing - there is no ear to tell.
    src = int(e["proj_src"][eid])
    firer = int(e["owner"][src]) if src >= 0 and e.alive[src] else -1
    # debug only: the small boom below is what everybody hears (the sparks the
    # client draws around it are the only other sign), and the interception
    # itself is settled - `tools/intercept_check.py` is the one reader left and
    # it raises the logger to DEBUG itself
    log.debug("intercept: %s round %d shot down at (%.1f, %.1f, %.1f)",
              wd.get("name", "?"), eid, pos[0], pos[1], pos[2])
    world.emit("boom", x=float(pos[0]), y=float(pos[1]), z=float(pos[2]), r=3.0,
               wid=int(wd.get("wid", 0)), proj=eid, small=1)
    world.emit("intercept", x=float(pos[0]), y=float(pos[1]), z=float(pos[2]),
               proj=eid, wid=int(wd.get("wid", 0)), pid=pid, firer=firer,
               faction=int(e["faction"][eid]))
    e.destroy(eid)


def _award(world: World, eid: int, pid: int, faction: int, d: dict,
           feed: bool) -> None:
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
    # `feed` says whether the kill line already carries the money (`_feed`); when
    # it does not - the pilot shot an infantryman, which is not announced - the
    # client still owes him the sound of the till
    world.emit("kill", pid=pid, victim=owner if owner >= 0 else -1,
               reward=reward, etype=int(world.ecs["etype"][eid]), feed=1 if feed else 0)


def _named(world: World, name_id: int, etype: int, bot: bool) -> str:
    """How the kill feed calls a hull: its name, or its type if it has none."""
    name = world.name_of(int(name_id))
    if name:
        return f"{name}[bot]" if bot else name
    return str((defs.UNIT_DEFS.get(int(etype)) or {}).get("name", "?"))


def _feed(world: World, eid: int, d: dict, credit: int) -> bool:
    """Announce a kill on the wire: who killed whom, with what.

    A line is for what a pilot has a stake in, and nothing else.  Infantry stay
    off it - a battle kills them by the dozen and the line would bury everything
    else - and so does AI fighting AI, which is most of the war and none of a
    pilot's business.  All that is left is a kill with a pilot on one end of it:
    his own, or the one that took his hull away from him.

    Returns whether a line went out, which is what tells `_award` the money is
    already on it.
    """
    e = world.ecs
    if e.has(eid, "person") or not e.has(eid, "last_name"):
        return False
    owner = int(e["owner"][eid]) if e.has(eid, "owner") else -1
    if credit < 0 and owner < 0:
        return False            # two crews, no pilot anywhere in it
    faction = int(e["faction"][eid])
    kfac = int(e["last_fac"][eid])
    if kfac not in (defs.FACTION_RED, defs.FACTION_BLUE) or kfac == faction:
        return False            # uncrewed burst, or an accident inside the family
    killer = _named(world, int(e["last_name"][eid]), int(e["last_etype"][eid]),
                    bot=credit < 0)
    wid = int(e["last_wid"][eid])
    wd = defs.WEAPONS[wid] if 0 <= wid < len(defs.WEAPONS) else {}
    world.emit("killfeed", killer=killer, kfac=kfac,
               victim=_named(world, int(e["name_id"][eid]), int(e["etype"][eid]),
                             bot=owner < 0),
               vfac=faction, w=str(wd.get("name", "")), pid=int(credit),
               reward=int(d.get("reward", 0)))
    return True


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
