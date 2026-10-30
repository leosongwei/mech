"""Missile interception on the range: fire each round at the defence post and
read back what the 30 mm did to it.

The range carries one AI mech whose only job is to shoot missiles down
(`modes/range/interceptor.py`), so the whole chain can be measured against real
approaches instead of argued about:

    radar    d^2 <= K * v_closing, K = sqrt(radar_strength * rcs) / RADAR_V_REF
    fuse     the 30 mm bursts within 4 m of the round (systems/proximity.py)
    kill     10 hp against the burst's blast (AD 15 / ER 10): 2.67 m or closer

For every weapon and launch distance it fires a few rounds, follows each one
until it is gone, and prints what happened to it: how much health the 30 mm took
off it, at what fuse gap, and whether the round came down.

    .venv/bin/python tools/intercept_check.py
    .venv/bin/python tools/intercept_check.py -v          # every log line
    .venv/bin/python tools/intercept_check.py --weapon saclos --dist 1200
"""

from __future__ import annotations

import argparse
import logging
import math
import pathlib
import sys

ROOT = pathlib.Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT / "backend"))

from server import defs  # noqa: E402
from server.modes.range import interceptor as interceptor_mod  # noqa: E402
from server.systems import weapons as weapons_mod  # noqa: E402
from server.world import World  # noqa: E402

DT = 1.0 / 30.0
ROUNDS = 4                       # launches per (weapon, distance) setting
FIRST_SHOT = 0.5                 # s into the run
GAP = 3.0                        # s between launches
HORIZON = 40.0                   # s the rounds are followed after the last one
STANDARD = {                     # launch distances, metres from the post
    "rpg": (400.0, 700.0),
    "rocket": (700.0, 1200.0),
    "saclos": (1000.0, 1600.0),
    "topattack": (900.0, 1400.0),
}


class Catcher(logging.Handler):
    """Keeps every log line the damage and intercept paths write."""

    def __init__(self) -> None:
        super().__init__(level=logging.INFO)
        self.lines: list[str] = []

    def emit(self, record: logging.LogRecord) -> None:
        self.lines.append(record.getMessage())

    def take(self) -> list[str]:
        out, self.lines = self.lines, []
        return out


def _aim(shooter: int, target: int, world: World) -> None:
    """Point the shooter's sight - and its bore, so the round leaves on it."""
    e = world.ecs
    sp = e["pos"][shooter]
    tp = e["pos"][target]
    th = defs.UNIT_DEFS[int(e["etype"][target])]["height"]
    eye_y = float(sp[1]) + defs.MECH_EYE
    dx = float(tp[0]) - float(sp[0])
    dy = float(tp[1]) + th * 0.75 - eye_y
    dz = float(tp[2]) - float(sp[2])
    yaw = math.atan2(dz, dx)
    pitch = math.atan2(dy, math.hypot(dx, dz))
    for name in ("yaw", "leg_yaw", "turret_yaw", "sight_yaw"):
        e[name][shooter] = yaw
    e["pitch"][shooter] = pitch
    e["sight_pitch"][shooter] = pitch


def shoot(wid: int, dist: float, catch: Catcher, seed: int = 3,
          verbose: bool = False) -> dict:
    """Fire `ROUNDS` rounds of `wid` at a fresh post and report what happened."""
    world = World(seed=seed, mode="range")
    e = world.ecs
    post = world.interceptor.eid
    px, pz = float(e["pos"][post, 0]), float(e["pos"][post, 2])
    sx, sz = px - dist * 0.80, pz + dist * 0.60      # open ground, south-west
    shooter = world.spawn_unit(defs.ETYPE_MECH, defs.FACTION_RED, sx, sz,
                               weapons=[wid, defs.WID_NONE])
    wd = defs.WEAPONS[wid]
    catch.take()

    rounds: list[dict] = []
    by_id: dict[int, dict] = {}
    clock = 0.0
    next_shot = FIRST_SHOT
    while clock < FIRST_SHOT + ROUNDS * GAP + HORIZON:
        clock += DT
        _aim(shooter, post, world)               # SACLOS rides this sight line
        if len(rounds) < ROUNDS and clock >= next_shot:
            next_shot += GAP
            before = {int(x) for x in world.interceptable()}
            weapons_mod.fire(world, shooter, 0, wid, wd, post)
            for pid in (int(x) for x in world.interceptable()):
                if pid in before:
                    continue
                rec = dict(pid=pid, hp=float(wd["aim_hp"]), hist=[], down=False,
                           gone=False, t_down=0.0, reached=False)
                rounds.append(rec)
                by_id[pid] = rec               # a freed id may be handed out again
        world.update(DT)
        world.events.clear()
        for line in catch.take():
            if verbose:
                print("   " + line)
            _note(line, by_id, clock)
        for rec in rounds:
            if not rec["gone"] and not e.alive[rec["pid"]]:
                rec["gone"] = True
                rec["reached"] = not rec["down"]
    return dict(wid=wid, dist=dist, rounds=rounds,
                post_hp=float(e["hp"][post]) if e.alive[post] else 0.0,
                post_alive=bool(e.alive[post]))


def _note(line: str, by_id: dict, clock: float) -> None:
    """Fold one log line into the record of the round it is about."""
    if line.startswith("missile "):
        parts = line.split()
        try:
            pid = int(parts[1])
        except (IndexError, ValueError):
            return
        rec = by_id.get(pid)
        if rec is None:
            return
        src = line.split("(", 1)[1].split(")", 1)[0] if "(" in line else ""
        hp = float(line.rsplit("->", 1)[1])
        rec["hist"].append((clock, rec["hp"] - hp, src, hp))
        rec["hp"] = hp
    elif line.startswith("intercept: "):
        try:
            pid = int(line.split(" round ")[1].split()[0])
        except (IndexError, ValueError):
            return
        rec = by_id.get(pid)
        if rec is not None:
            rec["down"] = True
            rec["t_down"] = clock


def _timeline(rec: dict) -> str:
    if not rec["hist"]:
        return "never touched"
    parts = [f"-{dmg:.0f}" for _, dmg, _, _ in rec["hist"]]
    gaps = [src for _, _, src, _ in rec["hist"]]
    return " ".join(parts) + "   (" + ", ".join(gaps) + ")"


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--weapon", default="all",
                    help="rpg / rocket / saclos / topattack / all")
    ap.add_argument("--dist", type=float, default=0.0,
                    help="launch distance in metres (0 = the standard set)")
    ap.add_argument("-v", "--verbose", action="store_true")
    args = ap.parse_args()

    catch = Catcher()
    for name in ("mech.intercept", "mech.prox"):
        lg = logging.getLogger(name)
        lg.addHandler(catch)
        lg.setLevel(logging.INFO)

    wanted = [w for w in defs.WEAPONS if defs.is_interceptable(int(w["wid"]))]
    if args.weapon != "all":
        wanted = [w for w in wanted if w["key"] == args.weapon]

    total = dict(shots=0, touched=0, downed=0, through=0, flying=0, fin=0)
    for w in wanted:
        dists = [args.dist] if args.dist else STANDARD.get(w["key"], (800.0,))
        for dist in dists:
            res = shoot(int(w["wid"]), dist, catch, verbose=args.verbose)
            rounds = res["rounds"]
            touched = sum(1 for r in rounds if r["hist"])
            downed = sum(1 for r in rounds if r["down"])
            through = sum(1 for r in rounds if r["reached"])
            flying = sum(1 for r in rounds if not r["gone"])
            print(f"\n{w['name']} @ {dist:.0f} m  "
                  f"(rcs {w['rcs']}, {w['maxvel']} m/s, "
                  f"found at {defs.radar_range(defs.UNIT_DEFS[defs.ETYPE_MECH]['radar_strength'], w['rcs']) * math.sqrt(min(w['maxvel'], defs.RADAR_V_REF) / defs.RADAR_V_REF):.0f} m)")
            for i, r in enumerate(rounds):
                state = "DOWNED" if r["down"] else ("through" if r["reached"] else "in the air")
                print(f"   round {i + 1}: {_timeline(r):<46} {state}"
                      f"{f' at {r['t_down']:.1f} s' if r['down'] else ''}")
            if not res["post_alive"]:
                print(f"   the post was destroyed (back in "
                      f"{interceptor_mod.RESPAWN_AFTER:.0f} s)")
            total["fin"] += res["post_hp"] < 100.0
            total["shots"] += len(rounds)
            total["touched"] += touched
            total["downed"] += downed
            total["through"] += through
            total["flying"] += flying

    print(f"\n{total['shots']} rounds fired: {total['touched']} met by the fuse, "
          f"{total['downed']} shot down, {total['through']} got through, "
          f"{total['flying']} still in the air at the horizon")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
