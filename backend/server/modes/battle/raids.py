"""Battle mode: the raiders - one mech per lane, sent round the back.

The lanes are a grind.  Every hull in the battle walks down one of them, so the
fighting happens where the lanes meet and the winner is whoever feeds the front
faster.  The answer to that is to stop feeding it: `waves._front_town` only
counts towns still **connected to the command base**, so a town taken out of the
enemy's rear silences the lane beyond it - which is exactly what a raider is for.
Every `RAID_INTERVAL` a side sends out `RAID_SIZE` mechs, **one per lane, the
lanes drawn at random**: a raider crosses the open field *off* the lanes (the
lanes are where the fighting is), takes a town behind the enemy front on its own
lane, and when it is ours the raider picks the next weak spot on that lane and
carries on.

`RAID_SIZE` raiders, `RAID_SIZE` lanes - that is the whole idea.  A single party
walking to the one weakest town across the map is a party that can be met: two
hulls heading for two different lanes cannot be, and whichever lane they leave
alone is the lane the enemy got lucky with.

The mission needs a plan, and the plan is made twice over, both halves off the
same **force gradient** (`_gradient`): the enemy army spread over the ground its
own weapons can see, weighted by what each hull is worth - a ridge of high ground
along the front and thin ground in his rear, which is the whole map a raider has
to read.

* **Where** - `_pick_town` takes the enemy town *on this raider's lane* with the
  lowest gradient at it (the fewest guns that could shoot a hull standing in the
  square), plus a mild term per kilometre of march so a raider does not cross
  half the map to shave one defender off the score.
* **How** - `_astar` over a 128 m node grid whose cost is openness plus that same
  gradient: a node that is all trees costs `WOOD_COST` extra (a raider crossing a
  forest cannot see out of it and cannot use the speed it has), a node that is
  all buildings costs `WALL_COST`, and a node the enemy has guns on costs
  `GRADIENT_COST` per unit of force - so the route bends *round* the fighting
  instead of through it.  The node route is then shortened through open ground
  (`_shortcut`) into a polyline a hull can actually drive.

The hulls themselves are ordinary mechs - they acquire, lead, fire and die
through `systems/ai.py` like any other crew; what makes them raiders is the
`raider` tag, which takes them out of the lane AI's hands and puts them under
`_drive` here.  The mission itself is a two-state machine on `ai_state`: MARCH
along the route, then ASSAULT - stand in the capture ring and let
`waves._capture` do the work - and when the town turns friendly the raider plans
again.  `ai.run` keeps running the guns in both states, so a raider that runs
into something fights it instead of walking past it.

Nothing here is a new kind of unit: a raider is a `combat` hull, so it holds
ground, can be locked, can be shot and its wreck goes up like anyone else's.  It
is also the only unit in the battle that carries a plan of its own, and both
halves of that plan (`_route`, `_pick_town`) are pure functions of the map and
of the enemy's positions - `tools/raid_trace.py` runs a battle and draws where
the raiders actually went.
"""

from __future__ import annotations

import heapq
import math
from typing import TYPE_CHECKING

import numpy as np

from ... import defs
from ...terrain import (TILE, TILE_BASE, TILE_BUILDING, TILE_FOREST, TILE_ROCK,
                        TILE_SOLID, TILE_TREE)

if TYPE_CHECKING:
    from ...terrain import Terrain
    from ...world import World

RAID_INTERVAL = 120.0       # s between two parties, per faction
RAID_FIRST = 60.0           # s into the round the first one leaves
RAID_STAGGER = 10.0         # s between the two sides' clocks (one plan per tick)
RAID_SIZE = 2               # raiders per party: one per lane, lanes drawn at random
RAID_MAX = 6                # hulls on the mission before the base stops sending

STATE_MARCH = 0             # driving the route
STATE_ASSAULT = 1           # standing in the town ring, taking it
STATE_HOLD = 2              # the town is ours: stay in the ring and keep it
RAID_HOLD = 90.0            # s a raider holds the town it took before moving on

THREAT_KM = 0.15            # score added per km of march (two empty towns, pick the near one)
NAV_CELL = 128.0            # m per navigation node (8 tiles, and 16 km / 128 m is exact)
WOOD_COST = 8.0             # extra cost of a node that is all trees
WALL_COST = 8.0             # extra cost of a node that is all buildings
# Extra cost per unit of enemy force on a node - the whole "do not cross the
# enemy" rule, and the one number here that was measured rather than reasoned.
# A raider sent to a town behind the blue front (seed 11, six minutes in) walks
# a straight line of 13.8 km through an average force of 6.0: it crosses the army
# itself.  At 0.35 that becomes 15.5 km (1.13x, 310 s at 50 m/s) through 1.9 -
# the route slides to where the line is thinnest.  Past ~0.5 the answer flips
# over to "go round the whole army": 25.8 km (1.87x, 516 s) through 0.3, which is
# nine minutes of marching to avoid a crossing that costs a raider nothing it
# cannot shoot back at.  So this stays on the near side of that cliff: bend round
# the thick of it, use the thin spot, arrive.
GRADIENT_COST = 0.35        # extra cost per unit of enemy force gradient on a node
GRADIENT_CAP = 12.0         # gradient above this is all the same: it is the front
# A* heuristic weight.  A node costs 1 on empty ground and up to `GRADIENT_CAP`
# in front of the enemy, so an unscaled octile estimate is so optimistic that the
# search expands half the map before it commits: 32 ms for the longest route
# measured.  Weighting it by 1.6 (standard weighted A*) brings that to 20 ms and
# found the same routes in the calibration - bend 1.22x the straight line round
# the enemy instead of 1.25x, through a third of the fire.
ASTAR_WEIGHT = 1.6
ARRIVE = 25.0               # m: a way point this close counts as reached
HANDOVER = 400.0            # m from the town middle where the street grid takes over
WINDOW_PAD = 320.0          # m of extra window around a street plan
STREET_TREE_COST = 3.0      # extra cost of a street-grid node under a canopy
TOWN_STOP = 150.0           # m from the town middle: inside the 170 m capture ring,
                            # but at its edge - a raider does not drive into the
                            # blocks of a town it is taking (300-650 m of houses)
BASE_STOP = 900.0           # m off the enemy command base: close enough to shoot it
REPLAN = 12.0               # s before a raider whose plan failed tries again
PLAN_GAP = 0.35             # s between two hulls of a party getting their orders
RESCUE = 2.0                # s before a stuck hull may be re-routed again

# What one hull is worth in the force gradient (`_gradient`).  The gradient is
# "how much of the enemy army would see a raider standing here", so a mech that
# outguns and outranges a carrier counts triple a rifleman's nuisance value -
# and the command base, which has no guns of its own, counts for nothing (it is
# filtered out by its empty weapon anyway: the table only names the shooters).
FORCE_WEIGHT = {
    defs.ETYPE_MECH: 3.0,
    defs.ETYPE_APC: 2.0,
    defs.ETYPE_INF_ROCKET: 1.0,
    defs.ETYPE_INF_RIFLE: 0.5,
}

STUCK_RATE = 0.25           # rolling slower than this share of the command is "pushing"
STUCK_FAST = 8.0            # m/s - above that a hull is going somewhere, whatever it was asked
STUCK_SKIP = 2.0            # s against something before the way point is abandoned
STUCK_REPLAN = 6.0          # s before the route itself is judged wrong

NO_TOWN = 255               # `raid_goal`: there is nothing to take, so go for the base

# the components a raider carries on top of its type's bundle: the mission tag
# and the plan it is executing (`components.py`)
_COMPS = ("raider", "raid_goal", "raid_i", "raid_party", "raid_stuck", "raid_t")

_PROBE = (0.8, 2.0, 4.0)     # m: the steps the obstacle fan must find clear
_NEIGH = ((1, 0), (-1, 0), (0, 1), (0, -1), (1, 1), (1, -1), (-1, 1), (-1, -1))
_ROOT2 = math.sqrt(2.0)


def bind(world: World) -> None:
    """Give the round its raid clock and the plans, one per raider.

    The clock is per faction and the two are staggered by `RAID_STAGGER`: a plan
    is ~15 ms of A* and both sides planning in the same tick would be one late
    tick every two minutes for no reason, while ten seconds apart is invisible.

    A plan belongs to a *hull* (`(faction, `raid_party`, lane)`), not to the
    faction: the raiders of one launch walk to two different lanes and are
    kilometres apart when the next launch gets its orders, so a shared plan would
    send the old one to a route that starts behind it - which is exactly what
    "the raider stopped advancing" looks like from outside.
    """
    world.raid_timer = {f: RAID_FIRST + f * RAID_STAGGER
                        for f in (defs.FACTION_RED, defs.FACTION_BLUE)}
    world.raid_paths = {}                 # (faction, party, lane) -> [(x, z), ...]
    world.raid_retry = {}                 # (faction, party, lane) -> s until the next attempt
    world.raid_serial = 0


def run(world: World, dt: float) -> None:
    if world.winner >= 0:
        return
    _launch(world, dt)
    _drive(world, dt)


# ---------------------------------------------------------------------------
# the clock
# ---------------------------------------------------------------------------
def key(world: World, eid: int) -> tuple[int, int, int]:
    """The name of this raider's plan: its side, its party and the lane it raids."""
    e = world.ecs
    return int(e["faction"][eid]), int(e["raid_party"][eid]), int(e["lane"][eid])


def _raiders(world: World, faction: int) -> np.ndarray:
    """The faction's raiders, alive (a query: a wreck carries no tags at all)."""
    ids = world.raiders()
    if ids.size == 0:
        return ids
    return ids[world.ecs["faction"][ids] == faction]


def _launch(world: World, dt: float) -> None:
    for k, left in world.raid_retry.items():
        world.raid_retry[k] = max(0.0, left - dt)
    e = world.ecs
    for faction in (defs.FACTION_RED, defs.FACTION_BLUE):
        world.raid_timer[faction] -= dt
        if world.raid_timer[faction] > 0.0:
            continue
        _prune(world)              # a party that is gone leaves no plan behind
        world.raid_timer[faction] = RAID_INTERVAL
        if _raiders(world, faction).size >= RAID_MAX:
            continue                       # the ones already out are still working
        lanes = world.rng.sample(range(len(world.lanes)),
                                 min(RAID_SIZE, len(world.lanes)))
        party = world.raid_serial % 256
        world.raid_serial += 1
        bx, bz = world.terrain.bases[faction]
        # one nav grid per launch: the two raiders of a party read the same
        # enemy picture, so building it twice would be 6 ms for nothing
        nav, force = _nav(world, faction)
        for n, lane in enumerate(lanes):
            x, z = _spawn_near(world, bx, bz, 90.0, 200.0)
            eid = world.spawn_unit(defs.ETYPE_MECH, faction, x, z, lane=lane,
                                   extra=_COMPS)
            e["raider"][eid] = 1
            e["raid_party"][eid] = party
            e["raid_goal"][eid] = NO_TOWN
            e["raid_i"][eid] = 0
            e["raid_stuck"][eid] = 0.0
            e["raid_t"][eid] = RAID_HOLD
            e["ai_state"][eid] = STATE_MARCH
            if n == 0:
                _replan(world, eid, nav, force)
            else:
                # one plan per tick: two A* runs and two nav grids in the same
                # tick is 45 ms of a 33 ms frame, and a third of a second between
                # two hulls getting their orders is invisible (`_drive` plans it
                # as soon as the timer runs out, because it has no route yet)
                world.raid_retry[key(world, eid)] = PLAN_GAP * n


def _prune(world: World) -> None:
    """Forget the plans of parties with no hull left alive.

    A raider dying is the normal end of a mission - it is a `combat` hull in the
    enemy's rear and the reinforcements it just cut off come for it - so the plans
    of dead parties have to be dropped, or every launch of a fifteen minute round
    would leave two route lists behind for the rest of it.
    """
    live = {key(world, int(eid)) for eid in world.raiders()}
    for k in [k for k in world.raid_paths if k not in live]:
        del world.raid_paths[k]
    for k in [k for k in world.raid_retry if k not in live]:
        del world.raid_retry[k]


def _spawn_near(world: World, x: float, z: float,
                r0: float, r1: float) -> tuple[float, float]:
    """A free spot in the ring around (x, z); the same rule the waves use."""
    for _ in range(16):
        ang = world.rng.random() * math.tau
        r = world.rng.uniform(r0, r1)
        sx, sz = x + math.cos(ang) * r, z + math.sin(ang) * r
        if world._clear_spot(sx, sz):
            return sx, sz
    return x, z


# ---------------------------------------------------------------------------
# the mission
# ---------------------------------------------------------------------------
def _replan(world: World, eid: int, nav: Nav | None = None,
            force: np.ndarray | None = None) -> None:
    """Pick this raider's next weak spot on its lane and plot a route to it.

    The route is planned in two halves: the strategic grid carries it across the
    open field, and the last `HANDOVER` metres - the part that runs into a town -
    are planned on the 16 m street grid, because at 128 m a town is a smear of
    half walls and a plan drawn on it strands a hull against a house.
    """
    e = world.ecs
    k = key(world, eid)
    if nav is None:
        nav, force = _nav(world, k[0])
    world.raid_retry[k] = REPLAN
    goal = _pick_town(world, k[0], k[2], nav, force)
    p = e["pos"][eid]
    start = (float(p[0]), float(p[2]))
    if goal == NO_TOWN:
        end = world.terrain.bases[1 - k[0]]
    else:
        town = world.towns[goal]
        end = (town["x"], town["z"])
    world.raid_paths[k] = _route(world.terrain, nav, start, end,
                                 None if goal == NO_TOWN else _handover(start, end))
    e["raid_goal"][eid] = goal
    e["raid_i"][eid] = 0
    e["raid_stuck"][eid] = 0.0
    e["raid_t"][eid] = RAID_HOLD
    e["ai_state"][eid] = STATE_MARCH


def _rescue(world: World, eid: int, path: list) -> None:
    """Plan a way out of whatever this hull has driven into, on the street grid.

    A raider that has pushed at something for `STUCK_REPLAN` seconds is not on the
    line it was given - it is behind a house, in a court, or on the wrong side of
    a wall the strategic grid did not resolve.  So the way out is planned where
    the walls are: the 16 m grid of the ground it is standing on, to the next way
    point it still has to reach.  Cheap (a small window, a few hundred nodes), so
    it can be run the moment a hull needs it instead of waiting out the `REPLAN`
    timer on a full re-plan.
    """
    e = world.ecs
    k = key(world, eid)
    world.raid_retry[k] = RESCUE
    b, row = e.slot(eid)
    px, pz = float(b["pos"][row, 0]), float(b["pos"][row, 2])
    goal = int(b["raid_goal"][row])
    town = _town(world, goal)
    if town is not None:
        end = (float(town["x"]), float(town["z"]))
    else:
        end = world.terrain.bases[1 - int(b["faction"][row])]
    i = int(b["raid_i"][row])
    target = path[i] if i < len(path) else end
    grid = StreetNav(world.terrain, (px, pz), target)
    legs = _legs(world.terrain, grid, (px, pz), target)
    if len(legs) >= 2:
        world.raid_paths[k] = legs + list(path[i + 1:])
        b["raid_i"][row] = 0
    b["raid_stuck"][row] = 0.0


def _town(world: World, goal: int) -> dict | None:
    return world.towns[goal] if 0 <= goal < len(world.towns) else None


def _needs_plan(world: World, eid: int) -> bool:
    """True when this raider has to be briefed again.

    Four cases: it has no route at all (a party's second hull is briefed a beat
    after the first); its target is not an enemy town any more and it is still
    marching at it (the front moved while it walked); its hold on the town it took
    has run out; or the enemy holds no towns left, so the base is the target.
    """
    e = world.ecs
    if key(world, eid) not in world.raid_paths:
        return True
    state = int(e["ai_state"][eid])
    if state == STATE_HOLD:
        return float(e["raid_t"][eid]) <= 0.0
    goal = int(e["raid_goal"][eid])
    if goal == NO_TOWN:
        return False
    town = _town(world, goal)
    if town is None:
        return True
    return state == STATE_MARCH and int(town["owner"]) == int(e["faction"][eid])


def _drive(world: World, dt: float) -> None:
    e = world.ecs
    for eid in world.raiders():
        eid = int(eid)
        k = key(world, eid)
        ready = world.raid_retry.get(k, 0.0) <= 0.0
        if ready and float(e["raid_stuck"][eid]) > STUCK_REPLAN:
            # it is against something the plan did not see: find the way out
            # where the walls are, before re-planning the whole march
            _rescue(world, eid, world.raid_paths.get(k) or [])
        elif ready and _needs_plan(world, eid):
            _replan(world, eid)
        _one(world, eid, world.raid_paths.get(k) or [], dt)


def _one(world: World, eid: int, path: list, dt: float) -> None:
    """One tick of the mission: march the route, then hold the town.

    The velocity is the only thing this writes - the guns are `ai.run`'s, which
    is why a raider that meets something shoots back with a full crew instead of
    driving past it.  Standing still in the ring is what takes a town
    (`waves._capture`), so ASSAULT is "get inside the ring and stop".
    """
    e = world.ecs
    b, row = e.slot(eid)
    pos = b["pos"]
    vel = b["vel"]
    px, pz = float(pos[row, 0]), float(pos[row, 2])
    goal = int(b["raid_goal"][row])
    town = _town(world, goal)
    faction = int(b["faction"][row])
    if town is not None:
        tx, tz, stop = float(town["x"]), float(town["z"]), TOWN_STOP
    else:
        tx, tz = world.terrain.bases[1 - faction]
        stop = BASE_STOP
    speed = defs.UNIT_DEFS[defs.ETYPE_MECH]["speed"]

    _watch_stuck(b, row, dt)

    state = int(b["ai_state"][row])
    if state == STATE_MARCH:
        i = int(b["raid_i"][row])
        while i < len(path) and _behind(px, pz, path, i):
            i += 1
        b["raid_i"][row] = i
        if i < len(path):
            ax, az = path[i]
        else:
            ax, az = tx, tz
            if math.hypot(tx - px, tz - pz) <= stop:
                # the route is done and the ring is underfoot: take the town
                b["ai_state"][row] = STATE_ASSAULT
                vel[row, 0] = vel[row, 2] = 0.0
                return
    else:
        # ASSAULT and HOLD are the same move - stay in the ring - and differ
        # only in what ends them: the capture (a town that is ours) starts the
        # hold, and the hold runs out into the next target.
        if state == STATE_ASSAULT and town is not None and int(town["owner"]) == faction:
            b["ai_state"][row] = STATE_HOLD
            b["raid_t"][row] = RAID_HOLD
        elif state == STATE_HOLD:
            b["raid_t"][row] = float(b["raid_t"][row]) - dt
        if math.hypot(tx - px, tz - pz) <= stop:
            vel[row, 0] = vel[row, 2] = 0.0
            return
        ax, az = tx, tz

    dx, dz = ax - px, az - pz
    d = math.hypot(dx, dz)
    if d < 1e-3:
        vel[row, 0] = vel[row, 2] = 0.0
        return
    dx, dz = _steer(world.terrain, px, pz, dx / d, dz / d, float(pos[row, 1]))
    vel[row, 0] = dx * speed
    vel[row, 2] = dz * speed


def _watch_stuck(b: dict, row: int, dt: float) -> None:
    """Count the seconds a hull spends pushing at something it cannot pass.

    A mech asks for a velocity, `movement` integrates what the ground allows, and
    the two are in `vel` and `drive` - so a commanded hull whose real speed is a
    quarter of the command is against a wall, a trunk or a house.  After
    `STUCK_SKIP` seconds of that the way point it is heading for is abandoned (the
    grid put it somewhere a 1.9 m mech does not fit), and `_needs_plan` replans
    the whole route after `STUCK_REPLAN`, which is the honest fix for a hull that
    has driven into a court with one way out.
    """
    vel, drive = b["vel"], b["drive"]
    want = math.hypot(float(vel[row, 0]), float(vel[row, 2]))
    real = math.hypot(float(drive[row, 0]), float(drive[row, 1]))
    stuck = b["raid_stuck"]
    # `STUCK_FAST` keeps the launch out of it: a mech off the mark is under a
    # quarter of the speed it asked for for the first second or so, and pulling
    # away from a standstill is not pushing at a wall
    if want < 1.0 or real >= min(STUCK_RATE * want, STUCK_FAST):
        stuck[row] = 0.0
        return
    stuck[row] = float(stuck[row]) + dt
    if float(stuck[row]) >= STUCK_SKIP:
        stuck[row] = float(stuck[row]) - STUCK_SKIP
        b["raid_i"][row] = int(b["raid_i"][row]) + 1


def _behind(px: float, pz: float, path: list, i: int) -> bool:
    """True when way point ``i`` is behind the hull and can be dropped.

    Inside the acceptance radius, or already past the line through the point
    square to the leg arriving from the previous one - the same rule the wheeled
    route follower uses (`ai._passed`), so a hull that overshot in a fight does
    not turn back for a corner it will never need.
    """
    wx, wz = path[i]
    if (px - wx) ** 2 + (pz - wz) ** 2 <= ARRIVE * ARRIVE:
        return True
    if i <= 0:
        return False
    lx, lz = wx - path[i - 1][0], wz - path[i - 1][1]
    return (lx * lx + lz * lz) > 1e-9 and ((px - wx) * lx + (pz - wz) * lz) > 0.0


def _steer(terrain: Terrain, px: float, pz: float, dx: float, dz: float,
           y: float) -> tuple[float, float]:
    """The direction to walk: the straight line, or the first clear fan offset.

    Clearance is tested *along the first few metres*, not at one point in front:
    `movement` moves a hull one 1.6 m step at a time and refuses any step where
    the hull does not fit, so a fan that only looks 4 m out will happily steer
    through a house whose far side is open - and the hull grinds against the
    corner for ever.  The reverse bearings are in the fan as well, because the way
    out of a court is usually the way in.
    """
    if _clear_run(terrain, px, pz, dx, dz, y):
        return dx, dz
    base = math.atan2(dz, dx)
    for off in (0.5, -0.5, 1.0, -1.0, 1.6, -1.6, 2.2, -2.2, math.pi):
        a = base + off
        nx, nz = math.cos(a), math.sin(a)
        if _clear_run(terrain, px, pz, nx, nz, y):
            return nx, nz
    return dx, dz


def _clear_run(terrain: Terrain, x: float, z: float, dx: float, dz: float,
               y: float) -> bool:
    """Whether a mech can drive at least a couple of metres this way."""
    for step in _PROBE:
        if not _clear(terrain, x + dx * step, z + dz * step, y):
            return False
    return True


def _clear(terrain: Terrain, x: float, z: float, y: float) -> bool:
    """Whether a hull of mech size fits at (x, z) - the samples `movement` uses.

    A raised spot is not an obstacle for a mech (it has legs), but a step so high
    that the hull would be inside the ground is, so the ground height is checked
    against a mech's own step as well.
    """
    rad = defs.UNIT_DEFS[defs.ETYPE_MECH]["radius"] * 0.6
    if (terrain.blocked(x, z) or terrain.blocked(x + rad, z) or
            terrain.blocked(x - rad, z) or terrain.blocked(x, z + rad) or
            terrain.blocked(x, z - rad)):
        return False
    if terrain.trunk_blocked(x, z, rad):
        return False
    return abs(terrain.height_at(x, z) - y) < 4.0


# ---------------------------------------------------------------------------
# planning: the enemy picture, the choice of town, the route
# ---------------------------------------------------------------------------
class Nav:
    """A 128 m node grid over the tile map: what a raider may cross and what it
    would rather not.

    `cost` is the static half of the route cost - one for open ground, more for
    woods or buildings - and `open` is what A* is allowed to stand on at all.  It
    is rebuilt per plan, not per tick: the map does not change (the debug tree
    knob repaints tiles, and the next plan is the earliest anything could know).
    """

    __slots__ = ("cw", "cd", "cell_m", "x0", "z0", "open", "cost")

    def __init__(self, terrain: Terrain, force: np.ndarray) -> None:
        k = int(NAV_CELL / TILE)
        cw, cd = terrain.w // k, terrain.d // k
        tiles = terrain.tiles[:cd * k, :cw * k]
        solid = np.isin(tiles, (TILE_BUILDING, TILE_ROCK, TILE_SOLID, TILE_BASE))
        wood = (tiles == TILE_TREE) | (tiles == TILE_FOREST)
        build = tiles == TILE_BUILDING
        solid = solid.reshape(cd, k, cw, k).mean(axis=(1, 3))
        wood = wood.reshape(cd, k, cw, k).mean(axis=(1, 3))
        build = build.reshape(cd, k, cw, k).mean(axis=(1, 3))
        self.cw, self.cd = cw, cd
        self.cell_m, self.x0, self.z0 = NAV_CELL, 0.0, 0.0
        self.open = solid < 0.5            # drivable unless half of the node is wall
        self.cost = np.where(
            self.open,
            1.0 + WOOD_COST * wood + WALL_COST * build
            + GRADIENT_COST * np.minimum(GRADIENT_CAP, force),
            np.inf).astype(np.float32)

    def cell(self, x: float, z: float) -> tuple[int, int]:
        i = min(self.cw - 1, max(0, int((x - self.x0) / self.cell_m)))
        j = min(self.cd - 1, max(0, int((z - self.z0) / self.cell_m)))
        return i, j

    def at(self, i: int, j: int) -> tuple[float, float]:
        return self.x0 + (i + 0.5) * self.cell_m, self.z0 + (j + 0.5) * self.cell_m


class StreetNav:
    """The street grid: one node per 16 m map tile, over a window around a town.

    The strategic grid cannot see a street.  At 128 m a node is only "open"
    unless half of it is wall, so a town is a smear it will happily plan a route
    through - and the hull that drives that route ends up against a house with
    nothing to do but push.  This is the same idea one level down: a node per map
    tile, buildings and rock impassable, the streets and the plaza open, the woods
    expensive (a mech may drive under a canopy, it does not want to).  It is built
    for the window it is needed in - a town and its approach, or the ground
    around a hull that has got itself stuck - so it is about the size of the
    strategic grid and costs about the same to search.

    Cost per node is `1 + STREET_TREE_COST` of wood: the streets are all equal as
    far as a walking hull is concerned, and a route through a park is a route
    through a park.
    """

    __slots__ = ("cw", "cd", "cell_m", "x0", "z0", "open", "cost")

    def __init__(self, terrain: Terrain, a: tuple[float, float],
                 b: tuple[float, float], pad: float = WINDOW_PAD) -> None:
        x0 = max(0.0, min(a[0], b[0]) - pad)
        z0 = max(0.0, min(a[1], b[1]) - pad)
        x1 = min(terrain.w * TILE, max(a[0], b[0]) + pad + TILE)
        z1 = min(terrain.d * TILE, max(a[1], b[1]) + pad + TILE)
        i0, j0 = int(x0 / TILE), int(z0 / TILE)
        i1, j1 = int(x1 / TILE), int(z1 / TILE)
        sub = terrain.tiles[j0:j1, i0:i1]
        solid = np.isin(sub, (TILE_BUILDING, TILE_ROCK, TILE_SOLID, TILE_BASE))
        wood = (sub == TILE_TREE) | (sub == TILE_FOREST)
        self.cw, self.cd = i1 - i0, j1 - j0
        self.cell_m, self.x0, self.z0 = TILE, i0 * TILE, j0 * TILE
        self.open = ~solid
        self.cost = np.where(self.open, 1.0 + STREET_TREE_COST * wood,
                             np.inf).astype(np.float32)

    def cell(self, x: float, z: float) -> tuple[int, int]:
        i = min(self.cw - 1, max(0, int((x - self.x0) / self.cell_m)))
        j = min(self.cd - 1, max(0, int((z - self.z0) / self.cell_m)))
        return i, j

    def at(self, i: int, j: int) -> tuple[float, float]:
        return self.x0 + (i + 0.5) * self.cell_m, self.z0 + (j + 0.5) * self.cell_m


def _nav(world: World, faction: int) -> tuple[Nav, np.ndarray]:
    force = _gradient(world, faction)
    return Nav(world.terrain, force), force


def _gradient(world: World, faction: int) -> np.ndarray:
    """The enemy force gradient: how much of his army would see a hull here.

    Not a count of hulls in a box - that weights a rifleman 700 m away the same
    as a mech on top of you.  Each hull is spread over the ground its own weapons
    can see (`UNIT_DEFS[...]["sight"]`) with a triangular falloff, weighted by
    what it is worth (`FORCE_WEIGHT`), so a node reads "defenders that could
    shoot me here" and the value fades with distance the way a threat does.  The
    sum over the map *is* the shape of the enemy's position: a ridge of high
    ground along the front, thin in his rear - the two things a raider has to
    read off it.

    One binned count per hull type, four integral images: the whole field costs a
    few milliseconds, which is why the route cost and the choice of town can both
    be an array index.
    """
    k = int(NAV_CELL / TILE)
    e = world.ecs
    force = np.zeros((world.terrain.d // k, world.terrain.w // k), np.float32)
    ids = world.combat_units()
    if ids.size:
        ids = ids[e["faction"][ids] == 1 - faction]
    if ids.size == 0:
        return force
    etype, p = e["etype"][ids], e["pos"][ids]
    cd, cw = force.shape
    i = np.clip((p[:, 0] * (1.0 / NAV_CELL)).astype(np.int32), 0, cw - 1)
    j = np.clip((p[:, 2] * (1.0 / NAV_CELL)).astype(np.int32), 0, cd - 1)
    for t in np.unique(etype):
        d = defs.UNIT_DEFS[int(t)]
        reach = float(d.get("sight", 0.0))
        if reach <= 0.0 or d["weapons"][0] == defs.WID_NONE:
            continue                       # no guns, no threat: the command base
        count = np.zeros((cd, cw), np.float32)
        sel = etype == t
        np.add.at(count, (j[sel], i[sel]), 1.0)
        force += FORCE_WEIGHT.get(int(t), 1.0) * _spread(count, reach)
    return force


def _spread(count: np.ndarray, reach: float) -> np.ndarray:
    """A binned count spread over the ground of `reach` metres around it.

    A triangle, made by convolving a box with itself, because that is what an
    integral image can do in one pass: a hull sitting on a node reads 1.0 there
    and 0 at `reach`, so the gradient has a slope instead of a cliff at the edge
    of a window.
    """
    h = max(1, int(round(reach / (2.0 * NAV_CELL))))
    w = 2 * h + 1
    return _box(_box(count, h), h) / float(w * w)


def _box(a: np.ndarray, r: int) -> np.ndarray:
    """Sum of ``a`` over a (2r+1) square window, through one integral image."""
    cd, cw = a.shape
    p = np.zeros((cd + 1, cw + 1), np.float32)
    p[1:, 1:] = a.cumsum(0).cumsum(1)
    i0 = np.clip(np.arange(cw) - r, 0, cw)
    i1 = np.clip(np.arange(cw) + r + 1, 0, cw)
    j0 = np.clip(np.arange(cd) - r, 0, cd)
    j1 = np.clip(np.arange(cd) + r + 1, 0, cd)
    return (p[np.ix_(j1, i1)] - p[np.ix_(j0, i1)]
            - p[np.ix_(j1, i0)] + p[np.ix_(j0, i0)])


def _pick_town(world: World, faction: int, lane: int, nav: Nav,
               force: np.ndarray) -> int:
    """The enemy town on this raider's lane with the least of his army about it.

    The rear is where he is thin - the front is where his hulls are - so the
    weakest town is the one behind his line, and taking it cuts the chain that
    feeds the front (`waves._front_town`).  The score is the force gradient *at
    the town* (`_gradient`: defenders that could shoot a hull standing there),
    plus `THREAT_KM` per kilometre of march so two towns that are equally empty
    do not send a raider across the map for nothing.

    A lane with nothing left to cut on it hands the raider the weakest town of
    *any* lane instead - a hull sent out with no target is a hull wasted - and
    only when the enemy holds no towns at all does it go for the command base.
    """
    bx, bz = world.terrain.bases[faction]
    best, best_score = NO_TOWN, math.inf          # on the raider's own lane
    other, other_score = NO_TOWN, math.inf        # anywhere
    for k, town in enumerate(world.towns):
        if int(town["owner"]) != 1 - faction:
            continue
        i, j = nav.cell(float(town["x"]), float(town["z"]))
        score = float(force[j, i]) + THREAT_KM * math.dist(
            (town["x"], town["z"]), (bx, bz)) / 1000.0
        if score < other_score:
            other, other_score = k, score
        if town.get("lane") == lane and score < best_score:
            best, best_score = k, score
    return best if best != NO_TOWN else other


def _route(terrain: Terrain, nav: Nav, start: tuple[float, float],
           end: tuple[float, float], street: tuple[float, float] | None = None,
           ) -> list[tuple[float, float]]:
    """Way points from ``start`` to ``end``, planned at two scales.

    The strategic grid carries the raider across the open field to `street`
    (`_handover`), and from there the street grid takes it the last few hundred
    metres into the town - one plan, one list of way points, and the driver does
    not need to know which half it is on.
    """
    if street is None:
        return _legs(terrain, nav, start, end)
    pts = _legs(terrain, nav, start, street)
    town = StreetNav(terrain, street, end)
    legs = _legs(terrain, town, street, end)
    return pts[:-1] + legs if legs else pts


def _legs(terrain: Terrain, grid, start: tuple[float, float],
          end: tuple[float, float]) -> list[tuple[float, float]]:
    """Way points for one leg: A* on `grid`, then shortened through open ground."""
    a = _drivable(grid, grid.cell(*start))
    b = _drivable(grid, grid.cell(*end))
    if a is None or b is None:
        return []
    cells = _astar(grid, a, b)
    if not cells:
        return []
    pts = [grid.at(i, j) for i, j in cells]
    pts[0] = (float(start[0]), float(start[1]))
    pts[-1] = (float(end[0]), float(end[1]))
    return _shortcut(terrain, pts)


def _handover(start: tuple[float, float],
              end: tuple[float, float]) -> tuple[float, float]:
    """Where the street plan takes over from the strategic one.

    `HANDOVER` metres out from the town middle, on the line the raider arrives
    along: far enough out that the strategic grid's last decision is made in open
    country, close enough that the street plan sees the town's edge and the ring
    it has to hold.
    """
    d = math.dist(start, end)
    if d <= HANDOVER + 1.0:
        return (float(start[0]), float(start[1]))
    f = (d - HANDOVER) / d
    return (start[0] + (end[0] - start[0]) * f, start[1] + (end[1] - start[1]) * f)


def _drivable(nav: Nav, cell: tuple[int, int], reach: int = 6) -> tuple[int, int] | None:
    """The nearest node A* may stand on: a base's own tiles are not drivable, and
    a raider starts and ends its plan in the middle of one."""
    i0, j0 = cell
    for r in range(reach + 1):
        best, best_d = None, 1e18
        for j in range(max(0, j0 - r), min(nav.cd, j0 + r + 1)):
            for i in range(max(0, i0 - r), min(nav.cw, i0 + r + 1)):
                if r and max(abs(i - i0), abs(j - j0)) != r:
                    continue                   # only the ring of this radius
                if nav.open[j, i]:
                    d = (i - i0) ** 2 + (j - j0) ** 2
                    if d < best_d:
                        best, best_d = (i, j), d
        if best is not None:
            return best
    return None


def _astar(nav, start: tuple[int, int], goal: tuple[int, int]) -> list[tuple[int, int]]:
    """The cheapest node route between two cells, 8-connected and weighted.

    A node diagonal only counts when both of its shared edges are drivable, so a
    route can never slip through the corner between two buildings.  The arrays
    are turned into plain python lists first: this loop runs tens of thousands of
    steps and a numpy scalar costs more to read than the arithmetic around it.
    """
    cw, cd = nav.cw, nav.cd
    cost = nav.cost.ravel().tolist()
    n_nodes = cw * cd
    s, g = start[1] * cw + start[0], goal[1] * cw + goal[0]
    if s == g:
        return [start]
    gx, gy = goal
    diag = _ROOT2 - 1.0
    gs = [math.inf] * n_nodes
    prev = [-1] * n_nodes
    done = bytearray(n_nodes)
    gs[s] = 0.0

    def h(i: int, j: int) -> float:
        dx, dy = i - gx, j - gy
        dx = dx if dx >= 0 else -dx
        dy = dy if dy >= 0 else -dy
        return ASTAR_WEIGHT * ((dx if dx > dy else dy) + diag * (dy if dx > dy else dx))

    heap = [(h(start[0], start[1]), s)]
    while heap:
        _, n = heapq.heappop(heap)
        if done[n]:
            continue
        done[n] = 1
        if n == g:
            break
        ni, nj = n % cw, n // cw
        base = gs[n]
        for di, dj in _NEIGH:
            i2, j2 = ni + di, nj + dj
            if i2 < 0 or j2 < 0 or i2 >= cw or j2 >= cd:
                continue
            m = j2 * cw + i2
            c = cost[m]
            if c == math.inf or done[m]:
                continue
            if di and dj and (cost[nj * cw + i2] == math.inf
                              or cost[j2 * cw + ni] == math.inf):
                continue                       # no corner cutting between two walls
            ng = base + c * (_ROOT2 if di and dj else 1.0)
            if ng < gs[m]:
                gs[m] = ng
                prev[m] = n
                heapq.heappush(heap, (ng + h(i2, j2), m))
    if not done[g]:
        return []
    out = []
    n = g
    while n >= 0:
        out.append((n % cw, n // cw))
        n = prev[n]
    out.reverse()
    return out


def _shortcut(terrain: Terrain, pts: list[tuple[float, float]],
              look: int = 4) -> list[tuple[float, float]]:
    """Straighten the node route into a line a hull can drive.

    A* answers a staircase of 128 m nodes; driven as given, a mech would walk the
    staircase.  So each way point is dropped when the one `look` ahead of it is
    still in a straight line of open ground - and "open" here excludes woods as
    well as walls, because crossing the fields is the whole point of the route
    (`_open_line`).  The look-ahead is short on purpose: the grid route is
    already diagonal wherever the ground is, and every extra point costs a line
    test of a kilometre through the tile map.
    """
    if len(pts) <= 2:
        return list(pts)
    out = [tuple(pts[0])]
    k = 0
    while k < len(pts) - 1:
        n = min(len(pts) - 1, k + look)
        while n > k + 1 and not _open_line(terrain, pts[k], pts[n]):
            n -= 1
        out.append(tuple(pts[n]))
        k = n
    return out


def _open_line(terrain: Terrain, a: tuple[float, float], b: tuple[float, float]) -> bool:
    """A straight run of ground a raider may cross: no wall, no trunk, no wood."""
    dx, dz = b[0] - a[0], b[1] - a[1]
    n = max(1, int(math.hypot(dx, dz) / TILE))
    for k in range(1, n):
        x, z = a[0] + dx * k / n, a[1] + dz * k / n
        if terrain.blocked(x, z):
            return False
        if terrain.tile_type(x, z) in (TILE_TREE, TILE_FOREST):
            return False
    return True
