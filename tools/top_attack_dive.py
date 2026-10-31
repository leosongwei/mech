"""Re-fly the top-attack dives the range recorded, and judge the guidance.

`modes/range/record.py` writes one JSONL line a tick while a top-attack round is
in the air: the round's state (`pos`, `vel`, `proj_age`, `proj_trav`, `target`,
`tgt_mem`, `tgt_vel`) and the true state of every hull a seeker could hold.  That
is enough to fly the same shot again under a different guidance law without
touching the world: every tick hands `flight.step` the state the real round had
and takes the velocity it answers with.

The miss distance is how close the round came to the *hull* - the re-flown
trajectory measured against the recorded truth track of the target, tick by tick
- because that is what the engine's hit spheres sit on (the seeker aims 6 m up,
at the deck, which is why a round 5 m from the stare point can still be a body
hit).  It answers "where did this guidance law put the round", and it answers it
on two inputs, which are the whole of the argument it exists for:

    --vel recorded   the velocity the *old* engine handed the seeker: a hull's
                     `vel` component, which was the velocity it had been *told*
                     to make (a full 50 m/s the instant a pilot nudged the
                     stick, while the machine crawled at 0.3)
    --vel truth      the velocity the hull was actually moving at, differenced
                     out of its own recorded track

On the recorded ranges (5 dives at a hull that mostly stood still) the first
misses by 8-36 m and no `pn_n`/`pn_r`/`pn_v` can rescue it; the second lands
inside the hull.  A trace written *after* the fix records `vel` as the truth, so
the two columns agree - the tool then only measures the guidance itself.

    .venv/bin/python tools/top_attack_dive.py                  # both, per flight
    .venv/bin/python tools/top_attack_dive.py --sweep          # the pn_* grid
    .venv/bin/python tools/top_attack_dive.py --pn-n 2 --pn-r 800
    .venv/bin/python tools/top_attack_dive.py --trace /tmp/other.jsonl -v
"""

from __future__ import annotations

import argparse
import json
import math
import pathlib
import sys

ROOT = pathlib.Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT / "backend"))

from server import defs, flight  # noqa: E402

TRACE = "/tmp/mech-range-trace.jsonl"
DT = 1.0 / 30.0
# The tool reports two distances and no verdict.  The hull is measured at its
# origin, the aim at the deck the seeker stares at (6 m up a mech); whether that
# is a *kill* depends on the engine's hit spheres and the blast, and the trace
# carries the real answer per flight ("the real round burst ..."), so the number
# here is "where this law put the round", not a death certificate.


def load(path: str) -> tuple[list[list[tuple[dict, dict]]], dict, list[dict]]:
    """Split a trace into flights: [(trace line, missile row)] per tick.

    A flight is one round's life: same entity id, `proj_age` climbing.  Ids are
    reused by the ECS, so the id alone would merge two shots into one.
    """
    rows = [json.loads(line) for line in open(path, encoding="utf-8")]
    header = rows[0] if rows and rows[0].get("kind") == "header" else {}
    body = rows[1:] if header else rows
    track: dict[int, dict[int, dict]] = {}
    for r in body:
        for u in r["units"]:
            track.setdefault(int(u["id"]), {})[int(r["tick"])] = u
    flights: list[list[tuple[dict, dict]]] = []
    for r in body:
        for m in r["missiles"]:
            last = flights[-1][-1][1] if flights else None
            if (last is not None and last["id"] == m["id"]
                    and m["proj_age"] > last["proj_age"]):
                flights[-1].append((r, m))
            else:
                flights.append([(r, m)])
    return flights, track, body


def truth_vel(track: dict, tid: int, tick: int) -> tuple[float, float, float]:
    """The hull's real velocity, from its own recorded positions."""
    tr = track.get(tid, {})
    a, b = tr.get(tick - 1), tr.get(tick + 1)
    if a is None or b is None:
        return (0.0, 0.0, 0.0)
    return tuple((b["pos"][i] - a["pos"][i]) / (2.0 * DT) for i in range(3))


def _closest_on_leg(p0, p1, q) -> float:
    """How close the leg ``p0 -> p1`` passes to ``q``.

    A missile covers 8 m a tick at terminal speed, so sampling the *ends* of the
    leg is a coin toss between a hit and a 5 m miss: the closest approach happens
    inside one tick and has to be solved, exactly as the engine's hit-sphere
    sweep does it.
    """
    seg = (p1[0] - p0[0], p1[1] - p0[1], p1[2] - p0[2])
    l2 = seg[0] ** 2 + seg[1] ** 2 + seg[2] ** 2
    if l2 < 1e-9:
        return math.dist(p0, q)
    t = ((q[0] - p0[0]) * seg[0] + (q[1] - p0[1]) * seg[1]
         + (q[2] - p0[2]) * seg[2]) / l2
    t = max(0.0, min(1.0, t))
    return math.dist((p0[0] + seg[0] * t, p0[1] + seg[1] * t, p0[2] + seg[2] * t), q)


def fly(seg, track, vel_src: str, wd: dict) -> dict:
    """Re-fly one recorded flight.  Returns the miss distance and the divergence.

    The recurrence has to match `projectiles.run` to the tick - measured
    against a recorded flight, `v(i+1) = step(pos(i), vel(i), proj_age(i),
    proj_trav(i), tgt_mem(i))` agrees to 0.1 m/s, while taking the age or the
    stare point one tick off disagrees by 3.7 m/s, i.e. by the 10 m a missile
    covers in a tick.  `err` is the largest distance between the re-flown
    trajectory and the recorded one: it is the control that says the model really
    is the engine and not a look-alike, and it should stay at zero.
    """
    p = tuple(seg[0][1]["pos"])
    v = tuple(seg[0][1]["vel"])
    best, best_i, err = float("inf"), 0, 0.0
    deck = float("inf")
    p_next = None
    for i, (r, m) in enumerate(seg):
        aim = tuple(m["tgt_mem"])
        tid = int(m["target"])
        # two distances, both solved against the leg the round covered: the hull
        # (its origin, where the engine's hit spheres sit) and the deck it was
        # staring at (6 m up a mech).  Neither is a verdict - the recorded burst
        # above is - but together they say where the law put the round.
        hull = track.get(tid, {}).get(int(r["tick"]))
        hull_p = tuple(hull["pos"]) if hull is not None else aim
        if p_next is not None:
            d = _closest_on_leg(p, p_next, hull_p)
            dq = _closest_on_leg(p, p_next, aim)
        else:
            d, dq = math.dist(p, hull_p), math.dist(p, aim)
        deck = min(deck, dq)
        if d < best:
            best, best_i = d, i
        err = max(err, math.dist(p, tuple(m["pos"])))
        if vel_src == "truth" and tid >= 0:
            tv = truth_vel(track, tid, int(r["tick"]))
        else:
            tv = tuple(m["tgt_vel"])
        v = flight.step(p, v, float(m["proj_age"]), float(m["proj_trav"]),
                        aim, tv, wd, DT)
        p_next = (p[0] + v[0] * DT, p[1] + v[1] * DT, p[2] + v[2] * DT)
        p = p_next
        # once it is well past the hull there is nothing left to measure
        if i > 6 and d > best + 25.0:
            break
    return dict(miss=best, deck=deck, i=best_i, err=err, end=p, n=len(seg))


def recorded_impact(body: list[dict], seg) -> float:
    """How close the *real* round got to the hull, from its own burst event.

    The trace carries the round's `boom` (its position at impact or battery
    end), so the engine's own outcome is in the file: this is the control the
    model is read against, not a second opinion.
    """
    pid = seg[0][1]["id"]
    tid = int(seg[0][1]["target"])
    # the burst lands on the line *after* the flight's last row: the round is
    # destroyed inside `projectiles`, so that tick carries no missile row at all
    last = int(seg[-1][0]["tick"])
    for r in body:
        if int(r["tick"]) < last:
            continue
        for ev in r.get("events", ()):
            if ev.get("k") == "boom" and int(ev.get("proj", -1)) == pid:
                hull = next((u for u in r["units"] if int(u["id"]) == tid), None)
                if hull is None:
                    # the hull left the record on the tick it went up, i.e. the
                    # round killed it
                    return float("-inf")
                return math.dist((ev["x"], ev["y"], ev["z"]), tuple(hull["pos"]))
    return float("nan")


def main(argv: list[str]) -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--trace", default=TRACE)
    ap.add_argument("--vel", choices=("recorded", "truth", "both"), default="both")
    ap.add_argument("--pn-n", type=float, default=None)
    ap.add_argument("--pn-r", type=float, default=None)
    ap.add_argument("--pn-v", type=float, default=None)
    ap.add_argument("--sweep", action="store_true",
                    help="grid over pn_n x pn_r, at pn_v from the trace header")
    ap.add_argument("-v", "--verbose", action="store_true")
    args = ap.parse_args(argv)

    if not pathlib.Path(args.trace).exists():
        print(f"no trace at {args.trace} - start the range with MECH_RECORD=1")
        return 1
    flights, track, body = load(args.trace)
    if not flights:
        print(f"{args.trace}: no top-attack flight in it")
        return 1
    params = dict(defs.WEAPONS[defs.WID_TOPATTACK])

    def wd_for(n=None, r=None, v=None) -> dict:
        wd = dict(params)
        if n is not None:
            wd["pn_n"] = n
        if r is not None:
            wd["pn_r"] = r
        if v is not None:
            wd["pn_v"] = v
        return wd

    print(f"{args.trace}: {len(flights)} flight(s)")
    for k, seg in enumerate(flights):
        tid = int(seg[0][1]["target"])
        rng = math.dist(seg[0][1]["pos"], tuple(seg[0][1]["tgt_mem"]))
        real = recorded_impact(body, seg)
        if real == float("-inf"):
            burst = "killed the hull"
        elif real != real:
            burst = "never went off (battery?)"
        else:
            burst = f"{real:.1f} m from the hull"
        print(f"\nflight {k}: hull {tid}, launched {rng:.0f} m out, "
              f"{len(seg) / 30.0:.1f} s of trace; the real round burst: {burst}")

    for vel_src in (("recorded", "truth") if args.vel == "both" else (args.vel,)):
        wd = wd_for(args.pn_n, args.pn_r, args.pn_v)
        out = [fly(seg, track, vel_src, wd) for seg in flights]
        print(f"\nPN fed the {vel_src:>8} velocity "
              f"(pn_n={wd['pn_n']}, pn_r={wd['pn_r']}, pn_v={wd['pn_v']}):")
        for k, (o, seg) in enumerate(zip(out, flights)):
            print(f"  flight {k}: {o['miss']:6.2f} m from the hull, "
                  f"{o['deck']:5.2f} m from the deck it aimed at, "
                  f"closest at {o['i'] / 30.0:4.1f} s")
            if args.verbose:
                print(f"      last position {tuple(round(c, 2) for c in o['end'])}")
        print(f"  worst {max(o['miss'] for o in out):.2f} m from the hull"
              f"   mean {sum(o['miss'] for o in out) / len(out):.2f} m"
              f"   worst deck {max(o['deck'] for o in out):.2f} m")

    if args.sweep:
        print("\nsweep, worst miss over the flights (m from the hull) - "
              "pn_r across, pn_n down")
        rules = (200.0, 300.0, 450.0, 600.0, 800.0)
        print("pn_n\\pn_r " + "".join(f"{r:>10.0f}" for r in rules))
        for vel_src in ("recorded", "truth"):
            print(f"  -- {vel_src} velocity --")
            for n in (1.0, 2.0, 3.0, 4.0, 5.0, 6.0):
                row = ""
                for r in rules:
                    wd = wd_for(n, r)
                    worst = max(fly(seg, track, vel_src, wd)["miss"] for seg in flights)
                    row += f"{worst:>10.2f}"
                print(f"{n:>9.0f} " + row)
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
