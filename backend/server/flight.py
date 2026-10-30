"""Aerodynamics and guidance for a winged round - today the top-attack missile.

A guided round used to be a point moving at a constant speed along a hand-drawn
three-leg path (`_steer` toward a way point at a fixed 1.9 rad/s): no mass, no
angle of attack, no energy.  This module flies it instead.  Every tick it turns
a guidance demand into a lift coefficient, and the lift coefficient into drag,
and integrates thrust along the nose, lift square to the velocity, drag against
it and gravity.  The round climbs because the motor pushes it, glides because
altitude is worth range, and pays for a hard pull in speed.

Nothing here is state: the airframe is a pure function of (position, velocity,
age, target, defs).  That is what keeps the round in the archetype it was born
in (`components.MISSILE_COMPS`) - no `nose`, no `fuel`, no `cl` buffer.

    v = flight.step(p, v, age, trav, tgt_pos, tgt_vel, tgt_h, wd, dt)

Guidance is three phases and one aim:

  * BOOST    (motor lit)  the reference arc's slope, capped by the tube's own
                          quadrant (`loft`): a stable climb angle, all the way
                          up until the motor burns out;
  * GLIDE    (motor out)  the same arc, unpowered.  The far half of the arc is
                          the dive, so the round reaches the target already
                          pointed down `loft`-ish and never has to make a hard
                          turn where it has neither speed nor room;
  * TERMINAL (inside `pn_r`, and fast enough for a fin to bite) true
                          proportional navigation on the roof point, both
                          planes at once.

Horizontally the round is a pure pursuit in every phase: it points at where the
target is now, and only the last `pn_r` metres solve a lead.  Firing at a hull
a few hundred metres away is the case the arc cannot help with - the tube is
still elevated at `loft`, so the guidance demands a *downward* lift and the
round pushes its nose under the velocity (negative angle of attack, thrust
helping it down, drag against the pull).
"""

from __future__ import annotations

import math

RHO = 1.225                     # kg/m³, sea level - the apex is under 1 km
GRAVITY = 9.81                  # m/s²
MACH = 340.0                    # m/s, "0.9 M" in this model
K_H = 0.6                       # height-error gain of the arc tracker
K_P = 3.0                       # heading-error gain of the turn (1/s)
ELEV_MAX = 1.2                  # rad, the tracker's own clamp
MIN_REF = 60.0                  # m, floor on the height-error look-ahead


def _dot(a, b):
    return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]


def _cross(a, b):
    return (a[1] * b[2] - a[2] * b[1],
            a[2] * b[0] - a[0] * b[2],
            a[0] * b[1] - a[1] * b[0])


def _unit(a):
    n = math.sqrt(a[0] * a[0] + a[1] * a[1] + a[2] * a[2])
    if n < 1e-9:
        return (1.0, 0.0, 0.0)
    return (a[0] / n, a[1] / n, a[2] / n)


def _area(wd: dict) -> float:
    """Frontal area from the calibre - the round's own size, not a knob."""
    r = float(wd["dia"]) * 0.5
    return math.pi * r * r


def arc(r: float, above: float, r0: float, wd: dict) -> float:
    """Commanded flight-path angle of the reference arc, radians.

    The reference is a parabola over the launch range: it climbs at
    ``atan(4H/R0)`` off the rail and comes down at the same angle onto the
    target, where ``R0`` is the range to go plus the distance already flown
    (so it equals the range at the shot and breathes with a moving target).
    One number therefore sets both the climb and the attack angle, and `loft`
    is that number: ``H`` is capped so the arc's climb equals the tube's own
    quadrant.
    """
    u = min(1.0, max(0.0, r / r0))
    # apex: what the shot asks for, what the airframe can turn out of, and
    # enough height that the motor can lift it off the ground at all
    # (thrust/weight 11.2 -> below ~6 deg of climb the round sinks)
    h = min(float(wd["apex"]), 0.25 * math.tan(float(wd["loft"])) * r0,
            r0 * r0 / (8.0 * float(wd["turn_r"])))
    h = max(0.045 * r0, h)
    slope = 4.0 * h * (1.0 - 2.0 * u) / r0         # dh/dr, flight direction +
    # the tracker: the arc's local slope, corrected toward the reference height
    # so the round converges onto the arc instead of drifting parallel to it
    want = math.atan(-slope) + K_H * math.atan2(4.0 * h * u * (1.0 - u) - above,
                                                max(r, MIN_REF))
    return max(-ELEV_MAX, min(ELEV_MAX, want))


def lift_dir(vhat, want):
    """Unit direction square to the velocity that turns it toward `want`."""
    axis = _cross(vhat, want)
    n = math.sqrt(_dot(axis, axis))
    if n < 1e-6:
        return (0.0, 0.0, 0.0)
    return _unit(_cross((axis[0] / n, axis[1] / n, axis[2] / n), vhat))


def pn_accel(p, v, roof, tgt_vel, n_law: float):
    """True proportional navigation toward `roof`: the acceleration owed."""
    r = (roof[0] - p[0], roof[1] - p[1], roof[2] - p[2])
    r2 = _dot(r, r)
    if r2 < 1e-6:
        return (0.0, 0.0, 0.0)
    rhat = _unit(r)
    vrel = (tgt_vel[0] - v[0], tgt_vel[1] - v[1], tgt_vel[2] - v[2])
    omega = _cross(r, vrel)
    omega = (omega[0] / r2, omega[1] / r2, omega[2] / r2)
    vc = -_dot(rhat, vrel)
    a = _cross(omega, rhat)
    k = n_law * max(vc, 1.0)
    return (a[0] * k, a[1] * k, a[2] * k)


def step(p, v, age: float, trav: float, tgt_pos, tgt_vel, tgt_h: float,
         wd: dict, dt: float):
    """Fly one round for one tick.  Returns the new velocity.

    `p` is the round's position, `v` its velocity, `age` the seconds it has been
    in the air, `trav` the ground distance it has covered (both already tracked
    by `projectiles`).  `tgt_pos` / `tgt_vel` are `None` once the seeker has
    lost the lock or the target is gone: guidance stops and the round is a
    rock with a warhead.
    """
    speed = math.sqrt(_dot(v, v))
    vhat = _unit(v)
    q = 0.5 * RHO * speed * speed
    s = _area(wd)
    lift_max = q * s * float(wd["cl_max"]) / float(wd["mass"])

    a_dir = (0.0, 0.0, 0.0)
    a_want = 0.0
    if tgt_pos is not None:
        dx = tgt_pos[0] - p[0]
        dz = tgt_pos[2] - p[2]
        r = math.hypot(dx, dz)
        roof_y = tgt_pos[1] + 0.92 * tgt_h
        above = p[1] - roof_y
        r0 = max(trav + r, 1.0)
        if r <= float(wd["pn_r"]) and speed >= float(wd["pn_v"]):
            roof = (tgt_pos[0], roof_y, tgt_pos[2])
            a = pn_accel(p, v, roof, tgt_vel, float(wd["pn_n"]))
            # the fins can only pull square to the flight path
            a = (a[0] - vhat[0] * _dot(a, vhat), a[1] - vhat[1] * _dot(a, vhat),
                 a[2] - vhat[2] * _dot(a, vhat))
            a_want = math.sqrt(_dot(a, a))
            a_dir = _unit(a) if a_want > 1e-9 else (0.0, 0.0, 0.0)
        else:
            elev = arc(r, above, r0, wd)
            if age <= float(wd.get("ignite", 0.0)) + float(wd.get("burn", 0.0)):
                # the motor is still lit: the tube's quadrant is a ceiling on
                # the climb, so a close target pulls the nose down here
                elev = min(elev, float(wd["loft"]))
            if r > 1e-3:
                hx, hz = dx / r, dz / r
            else:
                hx, hz = 1.0, 0.0
            want = (hx * math.cos(elev), math.sin(elev), hz * math.cos(elev))
            a_dir = lift_dir(vhat, want)
            # the pull owed is proportional to the heading error and capped by
            # what the airframe can make at this speed: the round crabs onto
            # the arc rather than snapping onto it
            axis = _cross(vhat, want)
            beta = math.asin(min(1.0, math.sqrt(_dot(axis, axis))))
            a_want = K_P * speed * beta
        a_want = min(a_want, lift_max)

    # ---- airframe ------------------------------------------------------
    cl = 0.0
    alpha = 0.0
    if a_want > 1e-9 and q * s > 1e-9:
        cl = min(float(wd["cl_max"]), float(wd["mass"]) * a_want / (q * s))
        alpha = cl / float(wd["cl_alpha"])          # the needed angle of attack
        # the nose leads the velocity toward the lift: a pull lifts the nose
        # above the flight path, a pushover puts it under, and the thrust
        # follows it either way
        ca, sa = math.cos(alpha), math.sin(alpha)
        nose = (vhat[0] * ca + a_dir[0] * sa, vhat[1] * ca + a_dir[1] * sa,
                vhat[2] * ca + a_dir[2] * sa)
    else:
        a_want = 0.0
        nose = vhat
    push = 0.0
    if (age > float(wd.get("ignite", 0.0))
            and age <= float(wd.get("ignite", 0.0)) + float(wd.get("burn", 0.0))
            and speed < float(wd["maxvel"])):
        push = float(wd["thrust"]) * float(wd["mass"])       # N along the nose
    mfrac = 1.0 / float(wd["mass"])
    # drag polar: profile drag, the induced price of the lift just commanded
    # (squared, so a pushover costs what a pull-up costs) and the transonic rise
    cd = (float(wd["cd0"]) + float(wd["k_cl"]) * cl * cl
          + 0.4 * max(0.0, speed / MACH - 0.80) ** 2)
    drag = q * s * cd
    lift = q * s * cl                                # = a_want·m, capped by cl_max
    ax = (push * nose[0] - drag * vhat[0]) * mfrac + a_dir[0] * lift * mfrac
    ay = (push * nose[1] - drag * vhat[1]) * mfrac + a_dir[1] * lift * mfrac - GRAVITY
    az = (push * nose[2] - drag * vhat[2]) * mfrac + a_dir[2] * lift * mfrac
    nv = (v[0] + ax * dt, v[1] + ay * dt, v[2] + az * dt)
    speed = math.sqrt(_dot(nv, nv))
    cap = float(wd["maxvel"])
    if speed > cap:                                  # structural / motor limit
        k = cap / speed
        nv = (nv[0] * k, nv[1] * k, nv[2] * k)
    return nv
