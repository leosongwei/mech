"""Battle raid trace: run a whole battle and draw where the raiders went.

A raider is the only hull in the game with a plan of its own
(`modes/battle/raids.py`): it is spawned at its own base, sent to cut one lane,
picks the weakest enemy town on that lane, plots a route across the open field
round the woods and the fighting, takes the town, and then goes looking for the
next weak spot on the same lane.  None of that is visible in a screenshot of the
battlefield, and the numbers that matter are all path-shaped: where the route
went, whether it crossed a forest, how long the march took, whether the town was
actually reached, and how often the plan had to be thrown away.

So this runs a battle headless - the real `World`, the real 30 Hz pipeline, the
real waves and AI - records every raider's plan and its driven trace, and draws
both on the map:

    python tools/raid_trace.py                       # 12 minutes -> /tmp/raid_trace.svg
    python tools/raid_trace.py --minutes 20 --seed 7 --out /tmp/raid7
    python tools/raid_trace.py --bbox 11300,11000,13600,13300   # street scale

It writes an SVG - the **force gradient** of both armies (the picture a raider
plans against), the woods, the three lanes, every raider's planned route dashed
and the trace it actually drove solid, the capture ring it was sent to hold, the
towns changing hands and an **x** where a hull died - plus a CSV of the per-sample
trace.  It prints the four things a raid is judged on, per raider: where it was
sent, how it moved (distance, mean speed, woods crossed, enemy exposure against
what the straight line would have been, how far off its own route it drifted),
how it fought (time with a target, time firing, lowest hull) and how it ended
(captured, held, destroyed at *t*).  A `--bbox` crop draws the map tile by tile,
so a town's streets can be read against the route.  No dependency beyond numpy:
the SVG is written by hand, and a PNG is one headless-chrome call away.

The battle itself takes about a third of the wall time it simulates - 12 minutes
of game is roughly four minutes of CPU - so the default is a short round; pass
`--minutes` for a longer one.
"""

from __future__ import annotations

import argparse
import math
import pathlib
import sys

import numpy as np

ROOT = pathlib.Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT / "backend"))

from server import defs  # noqa: E402
from server.modes.battle import raids  # noqa: E402
from server.terrain import TILE, TILE_BUILDING, TILE_FOREST, TILE_TREE  # noqa: E402
from server.world import World  # noqa: E402

DT = 1.0 / 30.0
SAMPLE = 5                     # ticks between two samples of a trace (6 Hz)
BLOCK = 8                      # tiles per background block (128 m)
WOOD_RGB = "#1d3a2a"
BUILD_RGB = "#2a2f38"
TRACE = ["#ff7b54", "#ffd166", "#6ee7ff", "#c792ff", "#7dffb0",
         "#ff5d8f", "#9ae6b4", "#f6ad55", "#63b3ed", "#f687b3"]
SIDE = {defs.FACTION_RED: "R", defs.FACTION_BLUE: "B"}


class Mission:
    """One raider's life: its plans and the trace it actually drove."""

    def __init__(self, eid: int, faction: int, party: int, lane: int) -> None:
        self.eid = eid
        self.faction = faction
        self.party = party
        self.lane = lane
        self.plans: list[dict] = []        # {'t', 'goal', 'path'}
        self.trace: list[tuple[float, float, float]] = []   # (t, x, z)
        self.wood = 0.0                    # metres driven through woods
        self.travelled = 0.0
        self.force: np.ndarray | None = None    # enemy force picture when it set off
        self.exposure = 0.0                # force x metres driven (see the summary)
        self.straight = 0.0                # what the straight line would have been
        self.deviation = 0.0               # worst metres off its own planned route
        self.arrived = -1.0                # t of the first sample inside the ring
        self.captured = -1.0               # t the goal town turned friendly
        self.held = 0.0                    # s spent holding the town it took
        self.engaged = 0.0                 # s with an enemy in its sights
        self.firing = 0.0                  # s with the trigger down
        self.hp_min = 0.0                  # lowest hull pool it was down to
        self.died = -1.0                   # t it was destroyed (-1: survived)
        self.end = 0.0

    @property
    def goal(self) -> int:
        return self.plans[-1]["goal"] if self.plans else raids.NO_TOWN

    def label(self, world: World) -> str:
        town = world.towns[self.goal] if self.goal != raids.NO_TOWN else None
        return f'{SIDE[self.faction]}{self.party}.{self.lane} {town["name"] if town else "BASE"}'


def run(world: World, minutes: float) -> tuple[list[Mission], list[dict]]:
    """Step the battle and record every raider; returns the missions and the
    town captures that happened along the way."""
    e = world.ecs
    missions: dict[tuple[int, int], Mission] = {}
    captures: list[dict] = []
    owners = [int(t["owner"]) for t in world.towns]
    last: dict[tuple, list] = {}          # the route last recorded per plan
    ticks = int(minutes * 60.0 / DT)

    for tick in range(ticks):
        world.update(DT)
        t = tick * DT

        for k, town in enumerate(world.towns):
            if int(town["owner"]) != owners[k]:
                captures.append(dict(t=t, name=town["name"], x=town["x"], z=town["z"],
                                     faction=int(town["owner"]), was=owners[k]))
                owners[k] = int(town["owner"])

        now: set[tuple[int, int]] = set()
        for eid in world.raiders():
            eid = int(eid)
            # keyed by entity *and* party: entity ids are reused once a hull is
            # gone, and the next raider to get the slot is a different mission
            mk = (eid, int(e["raid_party"][eid]))
            m = missions.get(mk)
            if m is None:
                m = missions[mk] = Mission(eid, int(e["faction"][eid]),
                                           int(e["raid_party"][eid]), int(e["lane"][eid]))
            now.add(mk)
            k = raids.key(world, eid)
            path = world.raid_paths.get(k)
            # a hull that has not been briefed yet has *no* plan (a party's
            # second hull waits `PLAN_GAP`), which is not the same thing as an
            # empty one: only a stored, changed route is a new plan
            if path is not None and path != last.get(k):
                last[k] = list(path)
                # a new plan on a town that is ours now: the job got done while
                # it was the target, which is the moment worth reporting
                old = m.goal
                if (old != raids.NO_TOWN and old < len(world.towns)
                        and int(world.towns[old]["owner"]) == m.faction):
                    m.captured = t
                m.plans.append(dict(t=t, goal=int(e["raid_goal"][eid]), path=list(path)))
                # the enemy picture as it was when this raider was briefed: the
                # route it drove is judged against the map it was given
                m.force = raids._gradient(world, m.faction)
                start = (m.trace[-1][1], m.trace[-1][2]) if m.trace else (
                    float(e["pos"][eid, 0]), float(e["pos"][eid, 2]))
                goal = world.towns[m.goal] if m.goal != raids.NO_TOWN else None
                end = (goal["x"], goal["z"]) if goal else world.terrain.bases[1 - m.faction]
                m.straight = _exposure(m.force, [start, end])
            # every tick, not just every sample: a hull that dies between two
            # samples is still a hull that died, and the fighting it did is the
            # reason it did
            if m.hp_min <= 0.0:
                m.hp_min = float(e["hp"][eid, 0])
            m.hp_min = min(m.hp_min, float(e["hp"][eid, 0]))
            if int(e["target"][eid]) >= 0:
                m.engaged += DT
            if int(e["trigger"][eid]):
                m.firing += DT
            if int(e["ai_state"][eid]) == raids.STATE_HOLD:
                m.held += DT
            if tick % SAMPLE:
                continue
            # the trace: real position, and how much of it was under a canopy
            p = e["pos"][eid]
            x, z = float(p[0]), float(p[2])
            if m.trace:
                ptx, ptz = m.trace[-1][1], m.trace[-1][2]
                step = math.dist((ptx, ptz), (x, z))
                m.travelled += step
                if _wood(world, x, z):
                    m.wood += step
                if m.force is not None:
                    m.exposure += _force_at(m.force, x, z) * step
                if m.plans:
                    m.deviation = max(m.deviation,
                                      _lateral(m.plans[-1]["path"], x, z))
            m.trace.append((t, x, z))
            m.end = t
            town = world.towns[m.goal] if m.goal != raids.NO_TOWN else None
            if town is not None and m.arrived < 0.0:
                if math.dist((x, z), (town["x"], town["z"])) <= raids.TOWN_STOP:
                    m.arrived = t

        # every mission that was alive and is not any more was destroyed - a
        # raider is an ordinary hull in the enemy's rear, so that is the normal
        # way a mission ends, and the plot has to show where
        for mk, m in missions.items():
            if m.died < 0.0 and mk not in now:
                m.died = t
    return list(missions.values()), captures


def _wood(world: World, x: float, z: float) -> bool:
    return world.terrain.tile_type(x, z) in (TILE_TREE, TILE_FOREST)


def _force_at(force: np.ndarray, x: float, z: float) -> float:
    i = min(force.shape[1] - 1, max(0, int(x / raids.NAV_CELL)))
    j = min(force.shape[0] - 1, max(0, int(z / raids.NAV_CELL)))
    return float(force[j, i])


def _exposure(force: np.ndarray, pts: list[tuple[float, float]]) -> float:
    """Mean enemy force along a polyline, sampled every 64 m: how many guns, on
    average, were pointed at the ground the route covers."""
    tot = span = 0.0
    for a, b in zip(pts, pts[1:]):
        d = math.dist(a, b)
        n = max(1, int(d / 64.0))
        for k in range(n):
            tot += _force_at(force, a[0] + (b[0] - a[0]) * k / n,
                             a[1] + (b[1] - a[1]) * k / n) * d / n
            span += d / n
    return tot / max(span, 1e-9)


def _lateral(pts: list[tuple[float, float]], x: float, z: float) -> float:
    """Distance from (x, z) to the nearest point of a polyline."""
    if len(pts) < 2:
        return 0.0
    best = 1e18
    for a, b in zip(pts, pts[1:]):
        dx, dz = b[0] - a[0], b[1] - a[1]
        L = dx * dx + dz * dz
        t = 0.0 if L < 1e-9 else max(0.0, min(1.0, ((x - a[0]) * dx + (z - a[1]) * dz) / L))
        best = min(best, math.hypot(x - (a[0] + dx * t), z - (a[1] + dz * t)))
    return best


# ----------------------------------------------------------------------
# plotting: hand-written SVG, no dependency
# ----------------------------------------------------------------------
def _blocks(mask: np.ndarray, block: int = BLOCK) -> list[tuple[int, int, int]]:
    """Run-length merge a tile mask into (row, start, length) blocks of `block`.

    A 1000x1000 tile mask drawn tile by tile is 15 MB of SVG; merged per row of
    128 m blocks it is a few hundred rectangles.  A zoomed plot asks for
    `block=1` instead - at street scale the blocks *are* the thing being looked at
    (a town's houses and the streets between them), and the perimeter of a crop is
    short enough that the tile count stays small.
    """
    h, w = mask.shape
    rows = (h + block - 1) // block
    cols = (w + block - 1) // block
    pad = np.zeros((rows * block, cols * block), bool)
    pad[:h, :w] = mask
    grid = pad.reshape(rows, block, cols, block).any(axis=(1, 3))
    out = []
    for j in range(rows):
        i = 0
        while i < cols:
            if not grid[j, i]:
                i += 1
                continue
            k = i
            while k < cols and grid[j, k]:
                k += 1
            out.append((j, i, k - i))
            i = k
    return out


def _heat(force: np.ndarray, faction: int, px: float, x0: float, z0: float,
          levels: int = 6) -> list[str]:
    """The force gradient as one translucent square per navigation node.

    The scale is logarithmic (`log1p`) and capped at the 97th percentile of the
    non-empty nodes: a gradient is dominated by the two or three places where an
    army has piled up, and on a linear scale the rest of the map - which is what
    the reader is trying to judge a route against - is a uniform dark wash.  The
    levels are quantized and grouped, so 15 000 nodes cost six `<g>` elements
    instead of 15 000 opacity attributes; `faction` is the army whose force it is,
    which is only the colour (red raiders read the blue army, and the other way).
    """
    hue = "224,74,52" if faction == defs.FACTION_RED else "79,125,240"
    lit = force[force > 0.05]
    if lit.size == 0:
        return []
    peak = max(float(np.percentile(lit, 97)), 0.3)
    level = np.clip(np.log1p(np.minimum(force, peak)) / math.log1p(peak), 0.0, 1.0)
    level = np.ceil(level * levels).astype(np.int8)
    cell = raids.NAV_CELL * px
    out: list[str] = []
    for k in range(1, levels + 1):
        rows, cols = np.where((level == k) & (force > 0.05))
        if rows.size == 0:
            continue
        alpha = 0.06 + 0.34 * k / levels
        out.append(f'<g fill="rgba({hue},{alpha:.2f})">')
        out += [f'<rect x="{i * cell - x0 * px:.1f}" y="{j * cell - z0 * px:.1f}"'
                f' width="{cell + 0.6:.1f}" height="{cell + 0.6:.1f}"/>'
                for j, i in zip(rows, cols)]
        out.append('</g>')
    return out


def svg(world: World, missions: list[Mission], captures: list[dict], title: str,
        bbox: tuple[float, float, float, float] | None = None) -> str:
    x0, z0, x1, z1 = bbox or (0.0, 0.0, world.terrain.w * TILE, world.terrain.d * TILE)
    px = 1080.0 / (x1 - x0)                       # px per metre
    pad, top = 40.0, 150.0                        # margin, band for title + legend
    w, h = (x1 - x0) * px, (z1 - z0) * px
    out: list[str] = [
        f'<svg xmlns="http://www.w3.org/2000/svg" width="{w + 2 * pad:.0f}"'
        f' height="{h + top + pad:.0f}" viewBox="0 0 {w + 2 * pad:.0f} {h + top + pad:.0f}">',
        '<rect width="100%" height="100%" fill="#0d1117"/>',
        f'<text x="{pad:.0f}" y="44" fill="#e8eef6" font-family="monospace"'
        f' font-size="{min(26.0, 0.6 * w / max(len(title), 1) * 1.6):.1f}">'
        f'{title}</text>',
        f'<g transform="translate({pad:.0f},{top:.0f})">',
    ]

    def P(x: float, z: float) -> tuple[float, float]:
        return (x - x0) * px, (z - z0) * px

    cell_px = BLOCK * TILE * px                     # overridden when zoomed in

    def rect(j: int, i: int, n: int, fill: str) -> str:
        c = cell_px if block == BLOCK else TILE * px
        return (f'<rect x="{i * c - x0 * px:.2f}" y="{j * c - z0 * px:.2f}"'
                f' width="{n * c:.2f}" height="{c:.2f}" fill="{fill}"/>')

    # one block per 128 m at map scale, per map *tile* when zoomed in: a town is
    # houses and the streets between them, and that is exactly what a street-level
    # plot is for
    block = 1 if (x1 - x0) < 3000.0 else BLOCK
    tiles = world.terrain.tiles
    out += [rect(j, i, n, WOOD_RGB) for j, i, n in
            _blocks((tiles == TILE_TREE) | (tiles == TILE_FOREST), block)]
    out += [rect(j, i, n, "#39414d") for j, i, n in
            _blocks(tiles == TILE_BUILDING, block)]

    # the force gradient: how much of each army would see a hull standing here.
    # Two layers, one per side, because that is what a raider reads - its own
    # enemy's picture - and the shape of the two fronts is the whole problem.
    for faction in (defs.FACTION_RED, defs.FACTION_BLUE):
        out += _heat(raids._gradient(world, faction), faction, px, x0, z0)

    # the three lanes: the front every raider is deliberately *not* walking
    for pts in world.lanes:
        d = " ".join(f"{P(x, z)[0]:.1f},{P(x, z)[1]:.1f}" for x, z in pts[::2])
        out.append(f'<polyline points="{d}" fill="none" stroke="#4a5568"'
                   ' stroke-width="1.4" stroke-dasharray="6 6"/>')
    for faction, (bx, bz) in world.terrain.bases.items():
        u, v = P(bx, bz)
        out.append(f'<rect x="{u - 7:.1f}" y="{v - 7:.1f}" width="14" height="14"'
                   f' fill="none" stroke="{"#e05252" if faction == defs.FACTION_RED else "#4f7df0"}"'
                   ' stroke-width="2.4"/>')
    for town in world.towns:
        u, v = P(town["x"], town["z"])
        out.append(f'<circle cx="{u:.1f}" cy="{v:.1f}" r="9" fill="none"'
                   ' stroke="#5d7387" stroke-width="1.2"/>')
        out.append(f'<text x="{u + 12:.1f}" y="{v + 4:.1f}" fill="#5d7387"'
                   ' font-family="monospace" font-size="11">'
                   f'{town["name"]}</text>')

    # plans (dashed) and driven traces (solid), one colour per raider
    for n, m in enumerate(missions):
        colour = TRACE[n % len(TRACE)]
        for plan in m.plans:
            if len(plan["path"]) < 2:
                continue
            d = " ".join(f"{P(x, z)[0]:.1f},{P(x, z)[1]:.1f}" for x, z in plan["path"])
            out.append(f'<polyline points="{d}" fill="none" stroke="{colour}"'
                       ' stroke-width="1.0" stroke-dasharray="4 7" opacity="0.55"/>')
        if len(m.trace) < 2:
            continue
        d = " ".join(f"{P(x, z)[0]:.1f},{P(x, z)[1]:.1f}" for _t, x, z in m.trace)
        out.append(f'<polyline points="{d}" fill="none" stroke="{colour}"'
                   ' stroke-width="1.8" stroke-linejoin="round" opacity="0.95"/>')
        sx, sz = P(m.trace[0][1], m.trace[0][2])
        out.append(f'<circle cx="{sx:.1f}" cy="{sz:.1f}" r="4" fill="{colour}"/>')
        if m.goal != raids.NO_TOWN:
            goal = world.towns[m.goal]
            gx, gz = P(goal["x"], goal["z"])
            out.append(f'<circle cx="{gx:.1f}" cy="{gz:.1f}" r="{raids.TOWN_STOP * px:.1f}"'
                       f' fill="none" stroke="{colour}" stroke-width="1.6" opacity="0.9"/>')
            out.append(f'<text x="{gx:.1f}" y="{gz - raids.TOWN_STOP * px - 5:.1f}"'
                       f' fill="{colour}" font-family="monospace" font-size="12"'
                       f' text-anchor="middle">{m.label(world)}</text>')

    for m in missions:
        if m.died < 0.0 or not m.trace:
            continue
        u, v = P(m.trace[-1][1], m.trace[-1][2])
        r = 7.0
        out.append(f'<path d="M{u - r:.1f},{v - r:.1f} L{u + r:.1f},{v + r:.1f}'
                   f' M{u + r:.1f},{v - r:.1f} L{u - r:.1f},{v + r:.1f}"'
                   ' stroke="#ff3b30" stroke-width="2.4"/>')

    for cap in captures:
        u, v = P(cap["x"], cap["z"])
        out.append(f'<circle cx="{u:.1f}" cy="{v:.1f}" r="4"'
                   f' fill="{"#e05252" if cap["faction"] == defs.FACTION_RED else "#4f7df0"}"'
                   ' opacity="0.9"/>')

    # legend
    out.append('</g><g font-family="monospace" font-size="14" transform='
               f'"translate({pad:.0f},{top - 62:.0f})">')
    out.append('<text x="0" y="0" fill="#2f5540">wood</text>')
    out.append('<text x="60" y="0" fill="#4f7df0">force gradient: blue army'
               '</text>')
    out.append('<text x="300" y="0" fill="#e05252">red army</text>')
    out.append('<text x="420" y="0" fill="#5d7387">lane (the front a raider is'
               ' deliberately not walking)</text>')
    out.append('<text x="0" y="18" fill="#c8d6e2">- driven trace, one colour per'
               ' raider: solid = where it went, dashed = the route it was given</text>')
    out.append(f'<text x="0" y="36" fill="#8fa3b6">circle = the capture ring it was'
               f' sent to hold ({raids.TOWN_STOP:.0f} m stop); a filled dot is a town'
               ' changing hands</text>')
    out.append('</g></svg>')
    return "\n".join(out)


def main(argv: list[str]) -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--minutes", type=float, default=12.0)
    ap.add_argument("--seed", type=int, default=11)
    ap.add_argument("--out", default="/tmp/raid_trace")
    ap.add_argument("--bbox", default="", metavar="X0,Z0,X1,Z1",
                    help="plot only this rectangle of the map, in metres")
    args = ap.parse_args(argv[1:])

    world = World(seed=args.seed, mode="battle")
    print(f"battle, seed {args.seed}: {len(world.towns)} towns,"
          f" {len(world.lanes)} lanes; simulating {args.minutes:.0f} min ({args.minutes / 3:.0f} min CPU)...")
    missions, captures = run(world, args.minutes)

    rows = ["faction,party,lane,goal,distance_m,time_s,wood_m,exposure,straight,"
            "deviation_m,replans,arrived_s,captured_s,held_s,engaged_s,hp_min,died_s"]
    launched = len(missions)
    dead = sum(1 for m in missions if m.died >= 0.0)
    got = sum(1 for m in missions if m.captured >= 0.0)
    held = sum(1 for m in missions if m.held > 1.0)
    print(f"\n{launched} raiders launched, {got} took their town, {held} held it,"
          f" {dead} destroyed ({100.0 * dead / max(launched, 1):.0f}%)"
          f"   |   {len(captures)} towns changed hands in the round")
    for m in missions:
        if len(m.trace) < 2:
            continue
        town = world.towns[m.goal] if m.goal != raids.NO_TOWN else None
        span = m.trace[-1][0] - m.trace[0][0]
        exposure = m.exposure / max(m.travelled, 1.0)
        print(f'  {m.label(world):<12s} driven {m.travelled / 1000.0:5.1f} km in'
              f' {span:5.0f} s ({m.travelled / max(span, 1.0):4.1f} m/s),'
              f' woods {m.wood / 1000.0:4.2f} km,'
              f' enemy {exposure:4.2f} vs straight {m.straight:4.2f},'
              f' off route {m.deviation:4.0f} m,'
              f' in a fight {m.engaged:4.0f} s (firing {m.firing:4.0f} s),'
              f' hp min {m.hp_min:3.0f},'
              f' held {m.held:3.0f} s,'
              f' {"KILLED %4.0f s" % m.died if m.died >= 0 else "survived"},'
              f' replans {len(m.plans) - 1:2d}'
              f'{"" if town is None else "  -> " + town["name"]}')
        rows.append(f'{m.faction},{m.party},{m.lane},{m.goal},{m.travelled:.1f},'
                    f'{span:.1f},{m.wood:.1f},{exposure:.3f},{m.straight:.3f},'
                    f'{m.deviation:.1f},{len(m.plans) - 1},'
                    f'{m.arrived:.1f},{m.captured:.1f},{m.held:.1f},'
                    f'{m.engaged:.1f},{m.hp_min:.0f},{m.died:.1f}')

    csv = pathlib.Path(args.out + ".csv")
    csv.write_text("\n".join(rows) + "\n")
    path = pathlib.Path(args.out + ".svg")
    bbox = tuple(float(v) for v in args.bbox.split(",")) if args.bbox else None
    path.write_text(svg(world, missions, captures,
                        f"raiders, seed {args.seed}, {args.minutes:.0f} min -"
                        f" {launched} launched, {got} towns taken,"
                        f" {dead} hulls lost ({100.0 * dead / max(launched, 1):.0f}%)",
                        bbox))
    print(f"\nwrote {path} and {csv}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv))
