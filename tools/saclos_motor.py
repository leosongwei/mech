#!/usr/bin/env python3
"""The beam rider's motor, worked out before it is flown.

`Core/Flight.cs` flies a SACLOS round as a 45 kg aeroplane: thrust along the
nose, lift square to the velocity, drag = q*S*(cd0 + k_cl*CL^2 + the transonic
rise), gravity always.  This is that airframe reduced to straight and level -
the beam keeps the round on the line, so the lift it has to make is its own
weight and holding it costs the induced drag `k_cl` is in the model for - and
integrated with the same numbers, so a motor can be priced before it is built:
how many seconds of what thrust put the clamp (`maxvel`) exactly at burnout,
which is where every second of motor is spent and none of it is wasted.

The shipped SACLOS motor came out of this: 100 m/s^2 for 7.3 s after a 0.4 s
coast, which is 600 m/s at 2.63 km / 7.74 s, and `--check beam` then measured
600 m/s at 2630 m / 7.70 s in the real world.  Drag is left exactly as the
flight model has it; the burn is what the drag makes it.

    python3 tools/saclos_motor.py                     # every row below
    python3 tools/saclos_motor.py --thrust 124 --burn 5.5
    python3 tools/saclos_motor.py --tune 100          # the burn for a thrust
"""

import argparse

# the airframe, straight out of `Defs.WEAPONS[WID_SACLOS]`
RHO = 1.225          # kg/m^3, sea level
G = 9.81             # m/s^2
MACH = 340.0         # m/s, the model's speed of sound (`Flight.MACH`)
MASS = 45.0          # kg     `mass`
DIA = 0.13           # m      `dia`, the aero reference
CD0 = 0.60           #        `cd0`
K_CL = 0.12          #        `k_cl`, the induced-drag factor
CL_ALPHA = 15.0      # /rad   `cl_alpha`
CL_MAX = 4.5         #        `cl_max`
VEL = 40.0           # m/s    the soft launch, `vel`
MAXVEL = 600.0       # m/s    the motor's ceiling, `maxvel`
LIFT_FLOOR = 110.0   # m/s    below this the wing cannot hold 1 g at all

MILEPOSTS = (200, 500, 1000, 2000, 3000, 3200)
DT = 1.0 / 300.0     # s, finer than the game's tick, so the numbers are the law's
AREA = 3.141592653589793 * (DIA * 0.5) ** 2


def fly(thrust, burn, ignite, t_max=20.0, dt=DT):
    """Straight and level: [(t, x, v)], plus the peak, and the range mileposts."""
    v, x, t = VEL, 0.0, 0.0
    path, reached = [], {}
    peak, peak_t, peak_x = v, 0.0, 0.0
    while t < t_max:
        q = 0.5 * RHO * v * v
        cl = min(CL_MAX, MASS * G / (q * AREA))     # lift to hold 1 g, capped
        alpha = cl / CL_ALPHA
        over = max(0.0, v / MACH - 0.80)            # the transonic rise
        drag = q * AREA * (CD0 + K_CL * cl * cl + 0.4 * over * over)
        lit = ignite <= t <= ignite + burn and v < MAXVEL
        push = thrust if lit else 0.0
        v = max(0.0, min(MAXVEL, v + (push * (1.0 - 0.5 * alpha * alpha)
                                      - drag / MASS) * dt))
        x += v * dt
        t += dt
        path.append((t, x, v))
        if v > peak:
            peak, peak_t, peak_x = v, t, x
        for m in MILEPOSTS:
            if m not in reached and x >= m:
                reached[m] = (t, v)
        # the flight is over once the motor is out and the wing has given up
        if t > ignite + burn and v < LIFT_FLOOR:
            break
    return path, reached, (peak, peak_t, peak_x)


def report(label, thrust, burn, ignite):
    _, reached, (peak, pt, px) = fly(thrust, burn, ignite)
    print(f"{label:<22} thrust {thrust:>5.0f} m/s2  burn {burn:>4.1f} s  "
          f"ignite {ignite:>4.2f} s ({VEL * ignite:>4.0f} m of coast)")
    print(f"{'':<22} peak {peak:>4.0f} m/s at {px:>5.0f} m / {pt:>4.2f} s"
          f"{'  (the clamp)' if peak >= MAXVEL - 0.5 else ''}")
    cells = []
    for m in MILEPOSTS:
        if m in reached:
            t, v = reached[m]
            cells.append(f"{m / 1000.0:>4.1f} km {v:>5.0f} m/s {t:>5.2f} s")
        else:
            cells.append(f"{m / 1000.0:>4.1f} km     -")
    for i in range(0, len(cells), 3):
        print(f"{'':<22} " + "   ".join(cells[i:i + 3]))
    print()


def burn_for(thrust, ignite, target=MAXVEL, lo=0.2, hi=30.0):
    """The burn a motor of `thrust` needs to be at `target` when it burns out."""
    for _ in range(50):
        mid = 0.5 * (lo + hi)
        if fly(thrust, mid, ignite)[2][0] < target:
            lo = mid
        else:
            hi = mid
    return 0.5 * (lo + hi)


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--thrust", type=float, help="m/s^2, flat, for one row")
    ap.add_argument("--burn", type=float, default=7.3, help="s of motor (with --thrust)")
    ap.add_argument("--ignite", type=float, default=0.4, help="s of coast before the motor")
    ap.add_argument("--tune", type=float, help="print the burn that m/s^2 needs")
    args = ap.parse_args()

    if args.tune:
        b = burn_for(args.tune, args.ignite)
        print(f"{args.tune:g} m/s^2 needs {b:.2f} s of motor "
              f"({b + args.ignite:.2f} s after the shot) to be at {MAXVEL:g} m/s "
              f"as it burns out")
        report("that motor", args.tune, round(b, 2), args.ignite)
        return
    if args.thrust:
        report("as asked", args.thrust, args.burn, args.ignite)
        return

    print(f"airframe: {MASS:g} kg, dia {DIA:g} m (S {AREA:.4f} m^2), cd0 {CD0:g}, "
          f"k_cl {K_CL:g}, cl_max {CL_MAX:g}, cl_alpha {CL_ALPHA:g}/rad")
    print(f"drag alone: {AREA * 0.5 * RHO * MAXVEL ** 2 * CD0 / MASS:5.1f} m/s^2 "
          f"at {MAXVEL:g} m/s, plus "
          f"{AREA * 0.5 * RHO * 300 ** 2 * K_CL * (MASS * G / (0.5 * RHO * 300 ** 2 * AREA)) ** 2 / MASS:5.2f} "
          f"for the wing at 300\n")

    print("== what 100 m/s^2 buys, as the burn gets longer ==")
    for burn in (2.0, 4.0, 5.5, 6.0, 7.0, 7.3, 8.0):
        peak, pt, px = fly(100.0, burn, 0.4)[2]
        print(f"   burn {burn:>4.1f} s -> peak {peak:>4.0f} m/s at {px:>5.0f} m / {pt:>4.2f} s")

    print("\n== what a five and a half second burn would cost ==")
    b = burn_for(124.0, 0.4)
    print(f"   124 m/s^2  -> {b:.2f} s to the clamp")

    print("\n== the motor in the game ==")
    report("shipped (1.35.0)", 100.0, 7.3, 0.4)
    print("== for the record, the one it replaced ==")
    report("flat 300 (1.34.x)", 300.0, 2.0, 0.10)


if __name__ == "__main__":
    main()
