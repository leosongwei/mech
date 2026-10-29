"""The decoupled sight: print what the machine does when the crew looks away.

A mech's sight answers the view instantly; the hull swings after it at
`defs.MECH_HULL_RATE` and the arms elevate at `defs.MECH_ARM_RATE`
(`systems/gunnery.py`).  Because the sight head stands `defs.MECH_SIGHT_Z`
behind the hull's rotation axis, catching up walks the pilot's eye along an arc
as well.  Nothing in here is a screenshot: every claim the feature makes is a
number, so the whole thing is checked headless on the range map (nothing there
shoots back) and printed.

What it checks, in order:

  slew    a 90 degree flick of the view: how long the hull takes, and that its
          rate really is the configured one (and the arms' 20 deg/s on pitch)
  eye     the eye is on the sight's pivot, so a still hull turning its *sight*
          does not move it, while a hull turning under a sight that is already
          somewhere else carries it round an arc of exactly MECH_SIGHT_Z
  bore    a round fired mid-flick leaves along the *barrel*, not along the
          cross; once the machine has caught up the two agree again

Usage:
  .venv/bin/python tools/gunnery_check.py
  .venv/bin/python tools/gunnery_check.py --verbose
"""

from __future__ import annotations

import argparse
import math
import pathlib
import sys

ROOT = pathlib.Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT / "backend"))

from server import defs, mathutil as mu  # noqa: E402
from server.world import World  # noqa: E402

DT = 1.0 / 30.0
HULL_RATE = defs.MECH_HULL_RATE
ARM_RATE = defs.MECH_ARM_RATE
DEG = 180.0 / math.pi


class Check:
    def __init__(self) -> None:
        self.fails: list[str] = []

    def near(self, name: str, got: float, want: float, tol: float) -> None:
        ok = abs(got - want) <= tol
        print(f"  {'ok  ' if ok else 'FAIL'}  {name:<38} {got:12.4f} (want {want:.4f}"
              f" +-{tol:.4f})")
        if not ok:
            self.fails.append(name)


def _world() -> tuple[World, int]:
    world = World(seed=11, mode="range")
    pl = world.add_player("GUNNERY")
    pl.god = True                     # nothing on the range shoots, but be sure
    for _ in range(4):
        world.update(DT)              # settle the spawn poses
    return world, pl.mech


def _pose(world: World, eid: int) -> dict:
    e = world.ecs
    yaw = float(e["yaw"][eid])
    pos = e["pos"][eid]
    return dict(yaw=yaw, pitch=float(e["pitch"][eid]),
                syaw=float(e["sight_yaw"][eid]), spitch=float(e["sight_pitch"][eid]),
                eye=mu.sight_eye(pos, yaw), pos=(float(pos[0]), float(pos[1]), float(pos[2])))


def _settle(world: World, pl, ticks: int = 90, verbose: bool = False) -> dict:
    for _ in range(ticks):
        world.update(DT)
        world.events.clear()
        if verbose:
            p = _pose(world, pl.mech)
            print(f"    hull {p['yaw'] * DEG:8.2f}  sight {p['syaw'] * DEG:8.2f}"
                  f"  pitch {p['pitch'] * DEG:7.2f}  spitch {p['spitch'] * DEG:7.2f}")
    return _pose(world, pl.mech)


def check_slew(check: Check, verbose: bool) -> None:
    print("\nslew - the sight answers, the metal catches up")
    world, mech = _world()
    pl = world.players[1]
    pl.in_yaw = float(world.ecs["yaw"][mech])
    pl.in_pitch = 0.0
    start = _settle(world, pl)

    # 90 degrees of view, then count the ticks the hull takes to get there
    pl.in_yaw = mu.wrap_pi(start["yaw"] + math.pi / 2)
    ticks = 0
    first = _pose(world, mech)
    step = abs(mu.wrap_pi(first["yaw"] - start["yaw"])) * DEG
    world.update(DT)
    world.events.clear()
    second = _pose(world, mech)
    per_tick = abs(mu.wrap_pi(second["yaw"] - first["yaw"])) * DEG
    print(f"  after one tick the hull has moved {per_tick:.2f} deg"
          f" ({per_tick / DT:.1f} deg/s, configured {HULL_RATE * DEG:.1f})")
    check.near("hull slew per tick", per_tick / DT, HULL_RATE * DEG, 0.5)
    while abs(mu.wrap_pi(_pose(world, mech)["yaw"] - pl.in_yaw)) > 1e-3 and ticks < 400:
        world.update(DT)
        world.events.clear()
        ticks += 1
    print(f"  90 deg of hull took {ticks} ticks = {ticks * DT:.2f} s"
          f" ({(math.pi / 2) / HULL_RATE:.2f} s at {HULL_RATE * DEG:.0f} deg/s)")
    check.near("90 deg flick, seconds", ticks * DT, (math.pi / 2) / HULL_RATE, 2 * DT)
    check.near("hull settled on the sight", abs(mu.wrap_pi(
        _pose(world, mech)["yaw"] - pl.in_yaw)), 0.0, 1e-3)

    # arms: 20 degrees a second, and they stop at their mechanical limits
    pl.in_pitch = 0.6
    p0 = _pose(world, mech)
    world.update(DT)
    world.events.clear()
    p1 = _pose(world, mech)
    check.near("arm slew per tick (deg/s)", (p1["pitch"] - p0["pitch"]) * DEG / DT,
               ARM_RATE * DEG, 0.5)
    t = 0
    while abs(_pose(world, mech)["pitch"] - min(0.6, defs.MECH_PITCH_MAX)) > 1e-3 and t < 400:
        world.update(DT)
        world.events.clear()
        t += 1
    print(f"  0 -> {min(0.6, defs.MECH_PITCH_MAX) * DEG:.1f} deg of elevation took"
          f" {t} ticks = {t * DT:.2f} s")
    check.near("arm climbed to the sight", _pose(world, mech)["pitch"],
               min(0.6, defs.MECH_PITCH_MAX), 1e-3)
    # ask for more than the mount can give: the arms sit on their stop
    pl.in_pitch = 1.2
    for _ in range(120):
        world.update(DT)
        world.events.clear()
    check.near("arm held at its upper stop", _pose(world, mech)["pitch"],
               defs.MECH_PITCH_MAX, 1e-3)
    check.near("the sight still looks up", _pose(world, mech)["spitch"], 1.2, 1e-4)


def check_eye(check: Check) -> None:
    print("\neye - the sight stands behind the hull's axis, so it rides the hull")
    world, mech = _world()
    pl = world.players[1]
    pl.in_yaw = float(world.ecs["yaw"][mech])
    pl.in_pitch = 0.0
    a = _settle(world, pl)

    # the pivot is the eye: turning the *head* about it must not move it, and
    # the hull carrying it must.  Both are properties of the geometry itself
    pos = a["pos"]
    same = mu.sight_eye(pos, a["yaw"])
    # float32 positions: a tenth of a millimetre at 4 km is not drift
    check.near("eye does not move with the sight", math.dist(same, a["eye"]), 0.0, 1e-3)
    r0 = math.dist((a["eye"][0], a["eye"][2]), (pos[0], pos[2]))
    check.near("eye radius, plan view", r0, abs(defs.MECH_SIGHT_Z), 1e-3)
    check.near("eye height", a["eye"][1] - pos[1], defs.MECH_EYE, 1e-3)
    # a hull yaw of +90 deg at yaw 0 puts the eye one MECH_SIGHT_Z to -z... and
    # the distance from the hull centre is the radius at every heading
    for k in range(8):
        th = k * math.pi / 4
        e = mu.sight_eye(pos, th)
        check.near(f"eye radius at heading {k * 45} deg",
                   math.dist((e[0], e[2]), (pos[0], pos[2])), abs(defs.MECH_SIGHT_Z), 1e-3)

    # the hull catching up under a sight already pointed elsewhere: the eye is
    # carried round an arc of that radius
    pl.in_yaw = mu.wrap_pi(a["syaw"] + 1.2)
    walked = 0.0
    prev = a["eye"]
    for _ in range(200):
        world.update(DT)
        world.events.clear()
        p = _pose(world, mech)
        walked += math.dist(prev, p["eye"])
        prev = p["eye"]
        if abs(mu.wrap_pi(p["yaw"] - pl.in_yaw)) < 1e-3:
            break
    print(f"  the hull came round, so the eye walked {walked:.3f} m of arc"
          f" (1.2 rad x {abs(defs.MECH_SIGHT_Z):.2f} m ="
          f" {1.2 * abs(defs.MECH_SIGHT_Z):.3f} m)")
    check.near("eye arc while the hull swings", walked, 1.2 * abs(defs.MECH_SIGHT_Z), 0.03)


def _fire(world: World, pl, arm: int = 0) -> tuple[list[int], dict]:
    """Pull one trigger, tick it through, hand back the rounds it made.

    The pose comes back as it was *on the firing tick*: the hull is still
    slewing, so reading it a tick later would credit the barrel with a degree
    and a half of movement it never had when the round left.
    """
    before = {int(i) for i in world.projectiles()}
    fire = [False, False]
    fire[arm] = True
    pl.fire = (fire[0], fire[1])
    world.update(DT)
    world.events.clear()
    pose = _pose(world, pl.mech)
    pids = [int(i) for i in world.projectiles() if int(i) not in before]
    pl.fire = (False, False)
    world.update(DT)                     # a released trigger re-arms the edge
    world.events.clear()
    return pids, pose


def check_bore(check: Check, verbose: bool) -> None:
    print("\nbore - a round leaves along the barrel, the cross promises the sight")
    world, mech = _world()
    pl = world.players[1]
    e = world.ecs
    e["weapon"][mech, 0] = defs.WID_AC30            # a cannon: pure ballistics
    e["weapon"][mech, 1] = defs.WID_NONE
    pl.in_yaw = float(e["yaw"][mech])
    pl.in_pitch = 0.0
    _settle(world, pl)

    # flick the view and pull the trigger on the next tick: the hull is a degree
    # and a half behind the cross, and the round has to leave with the barrel
    pl.in_yaw = mu.wrap_pi(pl.in_yaw + math.pi / 2)
    pids, p = _fire(world, pl)
    print(f"  firing: hull {p['yaw'] * DEG:.2f} deg, sight {p['syaw'] * DEG:.2f} deg"
          f" - {abs(mu.wrap_pi(p['syaw'] - p['yaw'])) * DEG:.2f} deg apart")
    if not pids:
        print("  FAIL  no round left the barrel")
        check.fails.append("no round")
        return
    pid = pids[0]
    v = e["vel"][pid]
    shot_yaw, shot_pitch = mu.angles_to(float(v[0]), float(v[1]), float(v[2]))
    print(f"  the first round left at {shot_yaw * DEG:.2f} deg / {shot_pitch * DEG:.2f}"
          f" deg (bore {p['yaw'] * DEG:.2f} / {p['pitch'] * DEG:.2f})")
    check.near("round flies along the bore yaw", abs(mu.wrap_pi(shot_yaw - p["yaw"])) * DEG,
               0.0, 0.2)
    print(f"  and it is {abs(mu.wrap_pi(p['syaw'] - shot_yaw)) * DEG:.2f} deg behind the"
          f" cross - the lag the pilot sees in the impact marker")

    # let it settle: now the round and the cross agree
    for _ in range(200):
        world.update(DT)
        world.events.clear()
        if abs(mu.wrap_pi(_pose(world, mech)["yaw"] - pl.in_yaw)) < 1e-3:
            break
    for _ in range(30):                  # cooldown (190 rpm) and the edge
        world.update(DT)
        world.events.clear()
    pids, p = _fire(world, pl)
    if not pids:
        print("  FAIL  the second round never left")
        check.fails.append("no second round")
        return
    v = e["vel"][pids[0]]
    shot_yaw, _ = mu.angles_to(float(v[0]), float(v[1]), float(v[2]))
    print(f"  caught up: hull {p['yaw'] * DEG:.2f}, sight {p['syaw'] * DEG:.2f},"
          f" round {shot_yaw * DEG:.2f} deg")
    check.near("round on the sight once settled", abs(mu.wrap_pi(shot_yaw - p["syaw"])) * DEG,
               0.0, 0.2)
    if verbose:
        print(f"    round ids: {pids}")


def main(argv: list[str]) -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("-v", "--verbose", action="store_true")
    args = ap.parse_args(argv[1:])
    print("gunnery: hull {:.1f} deg/s, arms {:.1f} deg/s, sight pivot {:.2f} m behind"
          " the hull axis".format(HULL_RATE * DEG, ARM_RATE * DEG, defs.MECH_SIGHT_Z))
    check = Check()
    check_slew(check, args.verbose)
    check_eye(check)
    check_bore(check, args.verbose)
    print()
    if check.fails:
        print(f"{len(check.fails)} check(s) failed: {', '.join(check.fails)}")
        return 1
    print("all checks passed")
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv))
