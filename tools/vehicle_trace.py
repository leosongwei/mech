"""Range APC trace: drive one carrier round a test route and draw it.

The range map is the flat test bench, and the routes below are synthetic ones:
a 100 m square (four right angles, a closed lap), a winding road, and a dense
zigzag laid out every 25 m.  They are plain way point lists with sharp corners,
exactly what a lane is, so the route follower in ``systems/ai.py`` drives them
the way it drives the lanes of the battle map - including cutting the inside of
a corner, off the road, which is what a car does with one.

    python tools/vehicle_trace.py                     # the 100 m square
    python tools/vehicle_trace.py --route dense       # a way point every 25 m
    python tools/vehicle_trace.py --route winding     # the long winding road
    python tools/vehicle_trace.py --out /tmp/run1     # choose the basename

It writes an SVG - the way points, the reference route, the driven trace
coloured by speed and a velocity vector every few dozen metres - plus a CSV of
the per-tick state, and prints a summary (lap time, mean speed, worst deviation
off the route, steering reversals, speed through the corners).  No dependency
beyond numpy: the SVG is written by hand.
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
from server.systems import ai  # noqa: E402
from server.world import World  # noqa: E402

DT = 1.0 / 30.0
LANE_INDEX = 3                 # an extra lane, past the three the range ships
VEC_SCALE = 1.2                # s: a velocity vector is this much travel long
VEC_SPACING_MIN = 18.0         # m of trace between two velocity vectors

# A 100 m square, started in the middle of one side so the start is not a
# corner.  The lap is 400 m: 50 + 100 + 100 + 100 + 50.
SQUARE = [
    (1550.0, 3000.0), (1600.0, 3000.0), (1600.0, 3100.0), (1500.0, 3100.0),
    (1500.0, 3000.0), (1550.0, 3000.0),
]

# Gentle zigzag east, an S-bend tighter than a road, a hairpin, a square corner
# south and a long straight back west - 3.7 km of every kind of corner.
WINDING = [
    (700.0, 3400.0), (1200.0, 3400.0),
    (1350.0, 3300.0), (1500.0, 3400.0), (1650.0, 3300.0), (1800.0, 3400.0),
    (2100.0, 3400.0), (2340.0, 3340.0), (2400.0, 3220.0),
    (2400.0, 3000.0), (2260.0, 3000.0), (2200.0, 2930.0), (2260.0, 2860.0),
    (2400.0, 2860.0), (2400.0, 2600.0), (1500.0, 2600.0),
]

# A way point every 25 m along a zigzag: the dense-line case, where the spacing
# is well inside the follower's acceptance radius and every vertex is an acute
# corner.  Nothing here is drivable by steering at the points one at a time.
DENSE = [(1000.0 + 25.0 * i, 2500.0 + (60.0 if i % 2 else 0.0)) for i in range(33)]

ROUTES = {"square": SQUARE, "winding": WINDING, "dense": DENSE}


def build(world: World, route: list[tuple[float, float]]) -> list[tuple[float, float]]:
    """The way point line, registered as a lane on the world exactly as given."""
    pts = list(route)
    world.lanes.append(list(pts))
    world.lanes_rev.append(list(reversed(pts)))
    return list(pts)


def lateral(pts: list[tuple[float, float]], x: float, z: float) -> float:
    """Distance from (x, z) to the reference route."""
    best = 1e18
    for (ax, az), (bx, bz) in zip(pts[:-1], pts[1:]):
        sx, sz = bx - ax, bz - az
        ll = sx * sx + sz * sz
        t = 0.0 if ll <= 1e-9 else max(0.0, min(1.0, ((x - ax) * sx + (z - az) * sz) / ll))
        best = min(best, (x - ax - sx * t) ** 2 + (z - az - sz * t) ** 2)
    return math.sqrt(best)


def run(ticks: int, route: list[tuple[float, float]]
        ) -> tuple[list[dict], list[tuple[float, float]]]:
    world = World(seed=7, mode="range")
    e = world.ecs
    pts = build(world, route)

    x, z = pts[0]
    eid = world.spawn_unit(defs.ETYPE_APC, defs.FACTION_RED, x, z, lane=LANE_INDEX)
    # face down the first leg, and clear the range's own blue targets out of
    # the way: nothing here shoots, but a clean plot beats a cluttered one
    e["yaw"][eid] = e["leg_yaw"][eid] = e["turret_yaw"][eid] = math.atan2(
        pts[1][1] - z, pts[1][0] - x)
    for other in list(world.units()):
        if int(other) != eid:
            e.destroy(int(other))

    speed = defs.UNIT_DEFS[defs.ETYPE_APC]["speed"]
    total = sum(math.dist(a, b) for a, b in zip(pts[:-1], pts[1:]))
    bufs, row = e.slot(eid)
    log: list[dict] = []
    travelled = 0.0
    prev = (x, z)
    for tick in range(ticks):
        # the AI is not part of the range pipeline (nothing there shoots back),
        # so the route follower is stepped by hand - the same call the battle
        # mode's pre-system makes
        ai._advance_route(world, bufs, row, speed)
        world.update(DT)
        world.events.clear()
        px, pz = float(e["pos"][eid, 0]), float(e["pos"][eid, 2])
        travelled += math.dist(prev, (px, pz))
        prev = (px, pz)
        u = float(e["drive"][eid, 0])
        log.append(dict(
            tick=tick, t=tick * DT,
            x=px, y=float(e["pos"][eid, 1]), z=pz,
            yaw=float(e["leg_yaw"][eid]),
            u=u, cmd=math.hypot(float(e["vel"][eid, 0]), float(e["vel"][eid, 2])),
            lat=lateral(pts, px, pz),
            travelled=travelled,
        ))
        # the follower marks the lane finished by running `path_i` one past the
        # last way point; past that it is driving at the command base, which is
        # not part of the route
        if int(bufs["path_i"][row]) >= len(pts):
            break
    return log, pts


# ----------------------------------------------------------------------
# plotting: hand-written SVG, no dependency
# ----------------------------------------------------------------------
SPEED_LO = (0.25, 0.55, 0.95)      # cold blue at the slowest corner
SPEED_HI = (0.92, 0.30, 0.12)      # hot red at top speed


def _mix(t: float) -> str:
    t = max(0.0, min(1.0, t))
    rgb = [SPEED_LO[i] + (SPEED_HI[i] - SPEED_LO[i]) * t for i in range(3)]
    return "rgb(%d,%d,%d)" % tuple(int(round(c * 255)) for c in rgb)


def _arrow(x: float, z: float, dx: float, dz: float, length: float,
           color: str, width: float) -> list[str]:
    """A line from (x, z) along the unit direction (dx, dz), with a head."""
    tipx, tipz = x + dx * length, z + dz * length
    out = [f'<line x1="{x:.1f}" y1="{z:.1f}" x2="{tipx:.1f}" y2="{tipz:.1f}"'
           f' stroke="{color}" stroke-width="{width}" stroke-linecap="round"/>']
    base = math.atan2(dz, dx) + math.pi
    head = min(11.0, 0.45 * length)
    for side in (-1.0, 1.0):
        wing = base + side * 0.44
        out.append(f'<line x1="{tipx:.1f}" y1="{tipz:.1f}"'
                   f' x2="{tipx + math.cos(wing) * head:.1f}"'
                   f' y2="{tipz + math.sin(wing) * head:.1f}"'
                   f' stroke="{color}" stroke-width="{width}"/>')
    return out


def svg(log: list[dict], pts: list[tuple[float, float]],
        route: list[tuple[float, float]], title: str) -> str:
    xs = [p[0] for p in pts] + [r["x"] for r in log]
    zs = [p[1] for p in pts] + [r["z"] for r in log]
    span = max(max(xs) - min(xs), max(zs) - min(zs))
    pad = max(22.0, 0.3 * span)
    x0, x1, z0, z1 = min(xs) - pad, max(xs) + pad, min(zs) - pad, max(zs) + pad
    scale = 1000.0 / max(x1 - x0, z1 - z0)          # px per metre
    w, h = (x1 - x0) * scale, (z1 - z0) * scale
    top = 122.0                                     # band for title + legend
    out: list[str] = [
        f'<svg xmlns="http://www.w3.org/2000/svg" width="{w:.0f}" height="{h + top:.0f}"'
        f' viewBox="0 0 {w:.0f} {h + top:.0f}">',
        '<rect width="100%" height="100%" fill="#10151c"/>',
        f'<text x="24" y="40" fill="#e8eef6" font-family="monospace"'
        f' font-size="26">{title}</text>',
        '<g font-family="monospace" font-size="14">',
        '<text x="24" y="70" fill="#8fa3b6">o way point</text>',
        '<text x="150" y="70" fill="#3f4c5a">-.- control line</text>',
        '<text x="300" y="70" fill="#5d7387">- - the way point line</text>',
        '<text x="580" y="70" fill="#c8d6e2">trace, coloured by speed</text>',
        f'<text x="24" y="94" fill="#7dffb0">velocity vector: length ='
        f' {VEC_SCALE:.1f} s of travel at the speed it is drawn at</text>',
        '</g>',
        f'<g transform="translate(0,{top})">',
    ]

    def P(x: float, z: float) -> tuple[float, float]:
        return (x - x0) * scale, (z - z0) * scale

    # grid: a line every decade of the span
    step = 10.0 ** math.floor(math.log10(span)) / 2.0
    g = math.ceil(x0 / step) * step
    while g < x1:
        u, v = P(g, z0)
        out.append(f'<line x1="{u:.1f}" y1="{v:.1f}" x2="{u:.1f}" y2="{h:.1f}"'
                   ' stroke="#1d2733" stroke-width="1"/>')
        out.append(f'<text x="{u + 4:.1f}" y="{h - 6:.0f}" fill="#41505f"'
                   f' font-family="monospace" font-size="12">{g:.0f}</text>')
        g += step
    g = math.ceil(z0 / step) * step
    while g < z1:
        u, v = P(x0, g)
        out.append(f'<line x1="0" y1="{v:.1f}" x2="{w:.1f}" y2="{v:.1f}"'
                   ' stroke="#1d2733" stroke-width="1"/>')
        out.append(f'<text x="6" y="{v - 5:.1f}" fill="#41505f"'
                   f' font-family="monospace" font-size="12">{g:.0f}</text>')
        g += step

    # the control line: the way points as they were given, before rounding
    d = " ".join(f'{"M" if i == 0 else "L"}{P(*p)[0]:.1f},{P(*p)[1]:.1f}'
                 for i, p in enumerate(route))
    out.append(f'<path d="{d}" fill="none" stroke="#3f4c5a" stroke-width="1.4"'
               ' stroke-dasharray="3 9"/>')

    # reference route, rounded to the radius a hull actually turns in
    d = " ".join(f'{"M" if i == 0 else "L"}{P(*p)[0]:.1f},{P(*p)[1]:.1f}'
                 for i, p in enumerate(pts))
    out.append(f'<path d="{d}" fill="none" stroke="#5d7387" stroke-width="2"'
               ' stroke-dasharray="10 7"/>')

    # the driven trace, one short segment per tick coloured by speed.  The ramp
    # runs from the speed the hull holds through its own tightest corner
    # (sqrt(lateral accel x radius)) to top speed, so braking into a bend is
    # visible instead of everything sitting at the hot end.
    top_speed = defs.UNIT_DEFS[defs.ETYPE_APC]["speed"]
    lo_speed = math.sqrt(defs.APC_LAT_ACCEL * defs.APC_TURN_R)
    for a, b in zip(log[:-1], log[1:]):
        (ax, az), (bx, bz) = P(a["x"], a["z"]), P(b["x"], b["z"])
        out.append(f'<line x1="{ax:.1f}" y1="{az:.1f}" x2="{bx:.1f}" y2="{bz:.1f}"'
                   f' stroke="{_mix((abs(a["u"]) - lo_speed) / (top_speed - lo_speed))}"'
                   ' stroke-width="3.2" stroke-linecap="round"/>')

    # way points: the corners the route was built from, numbered
    out.append('<g font-family="monospace" font-size="13" fill="#c8d6e2">')
    seen: list[tuple[float, float]] = []
    for i, (wx, wz) in enumerate(route):
        if any(math.dist((wx, wz), q) < 1.0 for q in seen):
            continue                        # a closed lap repeats its start
        seen.append((wx, wz))
        u, v = P(wx, wz)
        out.append(f'<circle cx="{u:.1f}" cy="{v:.1f}" r="4.5" fill="none"'
                   ' stroke="#8fa3b6" stroke-width="1.6"/>')
        out.append(f'<text x="{u + 8:.1f}" y="{v - 7:.1f}">W{i}</text>')
    out.append('</g>')

    # velocity vectors: direction and (by length) magnitude of the real
    # velocity, sampled along the trace
    spacing = max(VEC_SPACING_MIN, span / 14.0)
    nxt = spacing * 0.5
    for r in log:
        if r["travelled"] < nxt:
            continue
        nxt += spacing
        speed = abs(r["u"])
        if speed < 0.05:
            continue
        u, v = P(r["x"], r["z"])
        dx = math.cos(r["yaw"]) * (1.0 if r["u"] >= 0.0 else -1.0)
        dz = math.sin(r["yaw"]) * (1.0 if r["u"] >= 0.0 else -1.0)
        out.extend(_arrow(u, v, dx, dz, speed * VEC_SCALE * scale, "#7dffb0", 2.0))

    # start / end
    sx, sz = P(log[0]["x"], log[0]["z"])
    ex, ez = P(log[-1]["x"], log[-1]["z"])
    out.append(f'<circle cx="{sx:.1f}" cy="{sz:.1f}" r="6" fill="#39d98a"/>')
    out.append(f'<circle cx="{ex:.1f}" cy="{ez:.1f}" r="6" fill="#ff5d5d"/>')
    out.append('</g></svg>')
    return "\n".join(out)


def main(argv: list[str]) -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--route", choices=sorted(ROUTES), default="square")
    ap.add_argument("--ticks", type=int, default=9000)
    ap.add_argument("--out", default="/tmp/apc_trace")
    args = ap.parse_args(argv[1:])

    log, pts = run(args.ticks, ROUTES[args.route])

    csv = pathlib.Path(args.out + ".csv")
    csv.write_text("tick,t,x,y,z,yaw_deg,speed,cmd_speed,lat_err,travelled\n" + "".join(
        f'{r["tick"]},{r["t"]:.3f},{r["x"]:.2f},{r["y"]:.2f},{r["z"]:.2f},'
        f'{math.degrees(r["yaw"]):.1f},{r["u"]:.2f},{r["cmd"]:.2f},{r["lat"]:.2f},'
        f'{r["travelled"]:.2f}\n' for r in log))

    mean = sum(abs(r["u"]) for r in log) / len(log)
    worst = max(r["lat"] for r in log)
    # a weave shows up as the steering changing direction twice in a row: count
    # the sign changes of the yaw rate, ignoring the ones too small to see
    rate = np.diff(np.unwrap(np.array([r["yaw"] for r in log]))) / DT
    swing = int(np.sum((np.sign(rate[1:]) != np.sign(rate[:-1])) &
                       (np.abs(rate[1:]) > 0.02) & (np.abs(rate[:-1]) > 0.02)))
    print(f"steering reversals (weave signature): {swing}"
          f"   peak yaw rate {np.abs(rate).max():5.2f} rad/s")
    cruise = [abs(r["u"]) for r in log if r["t"] > 2.0] or [0.0]
    print(f"route {args.route}: {len(log)} ticks ({log[-1]['t']:.1f} s),"
          f" {log[-1]['travelled']:.1f} m travelled")
    print(f"mean speed {mean:5.2f} m/s   top {max(abs(r['u']) for r in log):5.2f} m/s"
          f"   slowest after the start {min(cruise):5.2f} m/s")
    print(f"deviation off the route: mean {sum(r['lat'] for r in log) / len(log):5.2f} m"
          f"   worst {worst:5.2f} m")

    # how fast it carries through each corner: the slowest point within 45 m
    # of a way point, and what it was doing 45-70 m before it
    print("corner speeds:")
    seen: list[tuple[float, float]] = [ROUTES[args.route][0]]
    for i, (wx, wz) in enumerate(ROUTES[args.route]):
        if i == 0 or any(math.dist((wx, wz), q) < 1.0 for q in seen):
            continue                      # the start is not a corner
        if i == len(ROUTES[args.route]) - 1 and math.dist(
                ROUTES[args.route][0], ROUTES[args.route][-1]) > 1.0:
            continue                      # neither is the end of an open route
        seen.append((wx, wz))
        apex = [r for r in log
                if r["t"] > 1.0 and math.dist((r["x"], r["z"]), (wx, wz)) < 45.0]
        entry = [r for r in log
                 if r["t"] > 1.0 and 45.0 <= math.dist((r["x"], r["z"]), (wx, wz)) < 70.0]
        if not apex:
            continue
        v = min(abs(r["u"]) for r in apex)
        v0 = sum(abs(r["u"]) for r in entry) / len(entry) if entry else float("nan")
        print(f"  W{i:<2d} ({wx:7.0f},{wz:7.0f})  entry {v0 * 3.6:5.1f} km/h"
              f" -> apex {v * 3.6:5.1f} km/h ({v:4.1f} m/s)")

    path = pathlib.Path(args.out + ".svg")
    path.write_text(svg(log, pts, ROUTES[args.route],
                        f"APC {args.route} - {log[-1]['t']:.0f} s,"
                        f" mean {mean:.1f} m/s"))
    print(f"\nwrote {path} and {csv}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv))
