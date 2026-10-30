"""Fly the top-attack missile headless: the range envelope, a table and a plot.

It is the measurement the plan was tuned with (`doc/top_attack_plan.md` §7): run
`flight.step` the way `systems/projectiles.py` runs it, over a spread of ranges
against a parked hull, a hull running away and a hull crossing, and print what
came of it - apex, time of flight, impact speed, dive angle, miss distance, and
whether the battery ran out first.

    python tools/top_attack_trace.py                    # tables + /tmp/top_attack.svg
    python tools/top_attack_trace.py --out /tmp/t.svg   # write the plot elsewhere
    python tools/top_attack_trace.py --range 4000 --bearing 25 -v

The SVG is hand-written (no matplotlib here): a plan view, the height profile and
the speed / angle-of-attack history, all from the same traces.  Render it with
headless chrome if a PNG is wanted:

    google-chrome --headless --screenshot=/tmp/t.png --window-size=1420,940 \
        --user-data-dir=/tmp/chrome-prof file:///tmp/top_attack.svg
"""

from __future__ import annotations

import argparse
import math
import pathlib
import sys

ROOT = pathlib.Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT / "backend"))

from server import defs, flight  # noqa: E402

DT = 1.0 / 30.0
MECH_H = defs.UNIT_DEFS[defs.ETYPE_MECH]["height"]
APC_H = defs.UNIT_DEFS[defs.ETYPE_APC]["height"]
WD = defs.WEAPONS[defs.WID_TOPATTACK]


def fly(target, rng=0.0, tv=(0.0, 0.0, 0.0), hull_h=MECH_H, wd=WD, trace=None):
    """One round, flown exactly the way `projectiles.run` flies it."""
    p = [0.0, 0.0, 0.0]
    gamma = float(wd["loft"])
    tgt = [float(target[0]), float(target[1]), float(target[2])]
    # the round leaves along the bore, i.e. on the bearing to the target: a shot
    # that has to turn 20 deg first is not what firing at a hull looks like
    brg = math.atan2(tgt[2], tgt[0])
    v = [wd["vel"] * math.cos(gamma) * math.cos(brg), wd["vel"] * math.sin(gamma),
         wd["vel"] * math.cos(gamma) * math.sin(brg)]
    life = float(wd["life"])
    t = 0.0
    trav = 0.0
    apex = 0.0
    dmin, dmin_t, v_end, g_end = 1e9, 0.0, 0.0, 0.0
    burst = False
    while t < life:
        tp = [tgt[0] + tv[0] * t, tgt[1] + tv[1] * t, tgt[2] + tv[2] * t]
        v = flight.step(p, v, t, trav, tp, tv, hull_h, wd, DT)
        p = [p[0] + v[0] * DT, p[1] + v[1] * DT, p[2] + v[2] * DT]
        speed = math.sqrt(v[0] ** 2 + v[1] ** 2 + v[2] ** 2)
        trav += speed * DT
        t += DT
        apex = max(apex, p[1])
        roof = (tp[0], tp[1] + 0.92 * hull_h, tp[2])
        d = math.dist(p, roof)
        if d < dmin:
            dmin, dmin_t, v_end = d, t, speed
            g_end = math.degrees(math.atan2(v[1], math.hypot(v[0], v[2])))
        if trace is not None:
            trace.append((round(t, 2), round(p[0], 1), round(p[1], 1), round(p[2], 1),
                          round(speed, 1),
                          round(math.degrees(math.atan2(v[1], math.hypot(v[0], v[2]))), 1),
                          round(_alpha(p, v, tp, tv, hull_h, wd, t, trav, speed), 1)))
        if d < 1.5 or (p[1] <= tp[1] + 0.5 and t > 0.5):
            break
        if t + DT >= life:
            burst = True          # the battery ran out: the warhead goes off
            break
    return dict(range=round(rng), t=round(t, 1), apex=round(apex), dmin=round(dmin, 1),
                dmin_t=round(dmin_t, 1), v_end=round(v_end), dive=round(g_end, 1),
                burst=burst, hit=dmin < 3.0, p=list(p))


def _alpha(p, v, tp, tv, hull_h, wd, age, trav, speed):
    """The angle of attack the round is flying at (for the plot only)."""
    vhat = (v[0] / speed, v[1] / speed, v[2] / speed)
    dx, dz = tp[0] - p[0], tp[2] - p[2]
    r = math.hypot(dx, dz)
    roof_y = tp[1] + 0.92 * hull_h
    r0 = max(trav + r, 1.0)
    if r <= float(wd["pn_r"]) and speed >= float(wd["pn_v"]):
        a = flight.pn_accel(p, v, (tp[0], roof_y, tp[2]), tv, float(wd["pn_n"]))
        a = (a[0] - vhat[0] * flight._dot(a, vhat), a[1] - vhat[1] * flight._dot(a, vhat),
             a[2] - vhat[2] * flight._dot(a, vhat))
        want = flight._unit(a) if flight._dot(a, a) > 1e-18 else (0.0, 0.0, 0.0)
    else:
        elev = flight.arc(r, p[1] - roof_y, r0, wd)
        if age <= float(wd.get("ignite", 0.0)) + float(wd.get("burn", 0.0)):
            elev = min(elev, float(wd["loft"]))
        hx, hz = (dx / r, dz / r) if r > 1e-3 else (1.0, 0.0)
        want = (hx * math.cos(elev), math.sin(elev), hz * math.cos(elev))
    a_dir = flight.lift_dir(vhat, want)
    q = 0.5 * flight.RHO * speed * speed
    s = flight._area(wd)
    axis = flight._cross(vhat, flight._unit(want))
    beta = math.asin(min(1.0, math.sqrt(flight._dot(axis, axis))))
    cl = min(float(wd["cl_max"]),
             float(wd["mass"]) * flight.K_P * speed * beta / (q * s)) if q * s else 0.0
    up = (0.0, 1.0 - vhat[1] * vhat[1], -vhat[1] * vhat[2])
    side = 1.0 if flight._dot(a_dir, up) >= 0.0 else -1.0
    return side * math.degrees(cl / float(wd["cl_alpha"]))


# --------------------------------------------------------------- the tables
STATIC = (400, 1000, 2000, 3000, 4000, 4300, 4500, 5000)
MOVING = (1000, 2000, 3000, 3500, 4000, 4300)


def tables(bearing=20.0):
    b = math.radians(bearing)
    print(f"{defs.WEAPONS[defs.WID_TOPATTACK]['name']}: "
          f"{WD['mass']:.0f} kg, {WD['dia'] * 1000:.0f} mm, CD0 {WD['cd0']}, "
          f"CLmax {WD['cl_max']} (aoa {math.degrees(WD['cl_max'] / WD['cl_alpha']):.1f} deg), "
          f"thrust {WD['thrust'] * WD['mass']:.0f} N for {WD['burn']} s, "
          f"loft {math.degrees(WD['loft']):.0f} deg, battery {WD['life']:.0f} s")
    print("\nstatic hull")
    for r in STATIC:
        o = fly((r * math.cos(b), 0.0, r * math.sin(b)), r)
        print(f"  {r / 1000:4.1f} km  apex {o['apex']:4d} m  TOF {o['t']:5.1f} s  "
              f"impact {o['v_end']:3d} m/s  dive {o['dive']:6.1f}deg  "
              f"miss {o['dmin']:6.1f} m  " +
              ("BATTERY" if o["burst"] else ("hit" if o["hit"] else "MISS")))
    print("\nmoving hull (14 m/s)")
    for r in MOVING:
        tgt = (r * math.cos(b), 0.0, r * math.sin(b))
        a = fly(tgt, r, (14.0, 0.0, 0.0))
        c = fly(tgt, r, (0.0, 0.0, 14.0))
        print(f"  {r / 1000:4.1f} km  away miss {a['dmin']:6.1f} m "
              f"{'BATTERY' if a['burst'] else ('hit' if a['hit'] else 'MISS'):7s}"
              f"   across miss {c['dmin']:6.1f} m "
              f"{'BATTERY' if c['burst'] else ('hit' if c['hit'] else 'MISS')}")


# ---------------------------------------------------------------- the plot
W, H = 1420, 940
SHOTS = [
    (400, 6, (0, 0, 0), "0.4 km pushover"),
    (1000, 15, (0, 0, 0), "1.0 km"),
    (2000, 25, (0, 0, 0), "2.0 km"),
    (3000, 35, (0, 0, 0), "3.0 km"),
    (4000, 45, (0, 0, 0), "4.0 km"),
    (3000, 55, (14.0, 0.0, 0.0), "3.0 km, hull runs 14 m/s"),
    (3000, 65, (0.0, 0.0, 14.0), "3.0 km, hull crosses"),
    (4500, 75, (0, 0, 0), "4.5 km, battery dies first"),
]
COL = ["#e4572e", "#f3a712", "#2aa876", "#3d7dca", "#8e5bd2",
       "#c02f6b", "#5aa02c", "#00b3c4"]


def _t(x, y, s, size=13, anchor="start", fill="#c8d2dc", weight="normal"):
    return (f'<text x="{x:.1f}" y="{y:.1f}" font-family="monospace" font-size="{size}" '
            f'text-anchor="{anchor}" fill="{fill}" font-weight="{weight}">{s}</text>')


def plot(out):
    runs = []
    for rng, brg, tv, lab in SHOTS:
        b = math.radians(brg)
        tr = []
        o = fly((rng * math.cos(b), 0.0, rng * math.sin(b)), rng, tv=tv, trace=tr)
        runs.append((lab, (rng * math.cos(b), 0.0, rng * math.sin(b)), tv, tr, o))

    body = [f'<rect width="{W}" height="{H}" fill="#101418"/>']
    # plan view
    mx0, mx1, my0, my1 = 90, 600, 40, H - 40
    R = max(math.dist((0, 0, 0), tgt) for _, tgt, _, _, _ in runs)
    sc = min(mx1 - mx0, my1 - my0) / 2 / R
    cx, cy = (mx0 + mx1) / 2, (my0 + my1) / 2
    body.append(_t(62, 26, "PLAN VIEW  (x/z, launch at centre)", 15, weight="bold"))
    for rr in (1000, 2000, 3000, 4000, 5000):
        body.append(f'<circle cx="{cx:.1f}" cy="{cy:.1f}" r="{rr * sc:.1f}" fill="none" '
                    f'stroke="#1e2a36"/>')
        body.append(_t(cx, cy - rr * sc, f"{rr / 1000:.0f} km", 10, "middle", "#3f4d5c"))
    body.append(f'<circle cx="{cx:.1f}" cy="{cy:.1f}" r="4" fill="#9fe"/>')
    for i, (lab, tgt, tv, tr, o) in enumerate(runs):
        c = COL[i % len(COL)]
        pts = " ".join(f"{cx + q[1] * sc:.1f},{cy + q[3] * sc:.1f}" for q in tr[::2])
        body.append(f'<polyline points="{pts}" fill="none" stroke="{c}" stroke-width="1.7"/>')
        tx, ty = cx + tgt[0] * sc, cy + tgt[2] * sc
        body.append(f'<rect x="{tx - 3.5:.1f}" y="{ty - 3.5:.1f}" width="7" height="7" '
                    f'fill="none" stroke="{c}" stroke-width="1.7"/>')
        if tv[0] or tv[2]:
            a = math.atan2(tv[2], tv[0])
            body.append(f'<line x1="{tx:.1f}" y1="{ty:.1f}" '
                        f'x2="{tx + math.cos(a) * 150 * sc:.1f}" '
                        f'y2="{ty + math.sin(a) * 150 * sc:.1f}" stroke="{c}" '
                        f'stroke-width="1.7" stroke-dasharray="3,3"/>')
        body.append(_t(tx + 7, ty - 6, f"{i + 1}", 11, fill=c))

    # profile
    px0, px1, py0, py1 = 700, W - 40, 60, 470
    D = max(math.dist((0, 0, 0), tgt) for _, tgt, _, _, _ in runs) * 1.05
    A = 1000.0
    body.append(_t(px0, 26, "PROFILE  (height vs ground distance)", 15, weight="bold"))
    for k in range(0, 5):
        h = A * k / 4
        y = py1 - h / A * (py1 - py0)
        body.append(f'<line x1="{px0}" y1="{y:.1f}" x2="{px1}" y2="{y:.1f}" stroke="#1e2a36"/>')
        body.append(_t(px0 - 8, y + 4, f"{h:.0f}", 10, "end", "#4a5866"))
    for k in range(0, 6):
        d = 1000 * k
        x = px0 + d / D * (px1 - px0)
        body.append(f'<line x1="{x:.1f}" y1="{py0}" x2="{x:.1f}" y2="{py1}" stroke="#161f28"/>')
        body.append(_t(x, py1 + 15, f"{d / 1000:.0f}", 10, "middle", "#4a5866"))
    body.append(_t((px0 + px1) / 2, py1 + 30, "ground distance (km)", 11, "middle", "#7b8b9b"))
    body.append(_t(px0 - 40, (py0 + py1) / 2, "m", 11, "middle", "#7b8b9b"))
    for i, (lab, tgt, tv, tr, o) in enumerate(runs):
        c = COL[i % len(COL)]
        pts = " ".join(f"{px0 + math.hypot(q[1], q[3]) / D * (px1 - px0):.1f},"
                       f"{py1 - q[2] / A * (py1 - py0):.1f}" for q in tr[::2])
        body.append(f'<polyline points="{pts}" fill="none" stroke="{c}" stroke-width="1.6"/>')
        body.append(_t(px1 - 2, py0 + 10 + i * 13,
                       f"{i + 1}. {lab}: apex {o['apex']} m, TOF {o['t']} s, "
                       f"impact {o['v_end']} m/s, dive {o['dive']:.0f}deg, "
                       f"miss {o['dmin']} m", 10, "end", c))

    # speed and angle of attack
    sx0, sx1, sy0, sy1 = px0, px1, 560, 860
    T = max(max(q[0] for q in tr) for _, _, _, tr, _ in runs) * 1.03
    body.append(_t(sx0, 545, "SPEED (m/s, thick) and AoA (deg, thin, +/-18 clamped)", 15,
                   weight="bold"))
    for v in (0, 85, 170, 255, 340):
        y = sy1 - v / 340.0 * (sy1 - sy0)
        body.append(f'<line x1="{sx0}" y1="{y:.1f}" x2="{sx1}" y2="{y:.1f}" stroke="#1e2a36"/>')
        body.append(_t(sx0 - 8, y + 4, f"{v:3.0f}", 10, "end", "#4a5866"))
        body.append(_t(sx1 + 6, y + 4, f"{(v - 170) / 170 * 18:+.0f}deg", 9, "start", "#3d4a58"))
    for k in range(0, 7):
        tt = T * k / 6
        x = sx0 + tt / T * (sx1 - sx0)
        body.append(f'<line x1="{x:.1f}" y1="{sy0}" x2="{x:.1f}" y2="{sy1}" stroke="#161f28"/>')
        body.append(_t(x, sy1 + 15, f"{tt:.0f}s", 10, "middle", "#4a5866"))
    y = sy1 - 306 / 340.0 * (sy1 - sy0)
    body.append(f'<line x1="{sx0}" y1="{y:.1f}" x2="{sx1}" y2="{y:.1f}" stroke="#5a3a3a" '
                f'stroke-dasharray="5,4"/>')
    body.append(_t(sx1 - 2, y - 4, "0.9 M", 10, "end", "#a05a5a"))
    for i, (lab, tgt, tv, tr, o) in enumerate(runs):
        c = COL[i % len(COL)]
        pts = " ".join(f"{sx0 + q[0] / T * (sx1 - sx0):.1f},"
                       f"{sy1 - q[4] / 340.0 * (sy1 - sy0):.1f}" for q in tr)
        body.append(f'<polyline points="{pts}" fill="none" stroke="{c}" stroke-width="2"/>')
        pts = " ".join(f"{sx0 + q[0] / T * (sx1 - sx0):.1f},"
                       f"{sy1 - (170 + q[6] / 18 * 170) / 340.0 * (sy1 - sy0):.1f}"
                       for q in tr)
        body.append(f'<polyline points="{pts}" fill="none" stroke="{c}" stroke-width="0.8" '
                    f'opacity="0.55"/>')
    body.append(_t(W / 2, H - 10,
                   f"m {WD['mass']:.0f} kg   D {WD['dia'] * 1000:.0f} mm   CD0 {WD['cd0']}   "
                   f"k {WD['k_cl']}   CLmax {WD['cl_max']} "
                   f"(aoa {math.degrees(WD['cl_max'] / WD['cl_alpha']):.1f} deg)   "
                   f"T {WD['thrust'] * WD['mass']:.0f} N   burn {WD['burn']} s   "
                   f"loft {math.degrees(WD['loft']):.0f} deg", 11, "middle", "#5b6773"))
    open(out, "w").write(f'<svg xmlns="http://www.w3.org/2000/svg" width="{W}" height="{H}">'
                         + "".join(body) + "</svg>")
    print(f"\nwrote {out}")


def main() -> None:
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--out", default="/tmp/top_attack.svg", help="plot path")
    ap.add_argument("--no-plot", action="store_true")
    ap.add_argument("--bearing", type=float, default=20.0)
    args = ap.parse_args()
    tables(args.bearing)
    if not args.no_plot:
        plot(args.out)


if __name__ == "__main__":
    main()
