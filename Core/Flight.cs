// Aerodynamics and guidance for a winged round: one airframe, two guidance laws
// (the top attack and the beam rider).
//
// A guided round used to be a point moving at a constant speed along a
// hand-drawn path (`_steer` toward a way point at a fixed turn rate): no mass, no
// angle of attack, no energy.  This module flies it instead.  Every tick it turns
// a guidance demand into a lift coefficient, and the lift coefficient into drag,
// and integrates thrust along the nose, lift square to the velocity, drag against
// it and gravity.  A round climbs because the motor pushes it, glides because
// altitude is worth range, and pays for a hard pull in speed - which is why the
// *pull* is the fragile thing: it is `q*S*cl_max/m`, and `q` is speed squared.
//
// Nothing here is state: the airframe is a pure function of (position, velocity,
// age, target, defs).  That is what keeps the round in the archetype it was born
// in (`Components.MISSILE_COMPS`) - no `nose`, no `fuel`, no `cl` buffer.
//
//     v = Flight.step(p, v, age, trav, tgt_pos, tgt_vel, wd, dt)    the arc law
//     v = Flight.direct(p, v, age, aim, aim_vel, wd, dt)            the beam law
//
// Both return the new *velocity* and nothing else; the caller advances the
// position by it (`p1 = p0 + v * dt`), exactly as `Systems/Flights.cs` does.
// Only the demand differs - what the round *wants* to do this tick - and both
// demands are clipped by the same lift ceiling, so a slow round of either kind
// stops answering.
//
// The arc law is three phases and one aim:
//
//   * BOOST    (motor lit)  the reference arc's slope, capped by the tube's own
//                           quadrant (`loft`): a stable climb angle, all the way
//                           up until the motor burns out;
//   * GLIDE    (motor out)  the same arc, unpowered.  The far half of the arc is
//                           the dive, so the round reaches the target already
//                           pointed down `loft`-ish and never has to make a hard
//                           turn where it has neither speed nor room;
//   * TERMINAL (inside `pn_r`, and fast enough for a fin to bite) true
//                           proportional navigation on the roof point, both
//                           planes at once.
//
// Horizontally the round is a pure pursuit in every phase: it points at where the
// target is now, and only the last `pn_r` metres solve a lead.  Firing at a hull
// a few hundred metres away is the case the arc cannot help with - the tube is
// still elevated at `loft`, so the guidance demands a *downward* lift and the
// round pushes its nose under the velocity (negative angle of attack, thrust
// helping it down, drag against the pull).
//
// The direct law (`Flight.direct`) is a beam rider's, and it has no arc at all:
// the round flies at the point the launcher's laser leads to, and what it owes
// every tick is the turn onto that line *plus its own weight*.  So it holds
// altitude by flying at an angle of attack rather than by being pushed up, the
// angle costs induced drag, and the motor's burnout is the moment the flight
// starts to decay: the round slows, the lift ceiling falls with the square of
// the speed, and past the point where the wing can make 1 g it sags out of the
// beam instead of turning.

namespace Mech.Server;

public static class Flight
{
    public const float RHO = 1.225f;        // kg/m³, sea level - the apex is under 1 km
    public const float GRAVITY = 9.81f;     // m/s²
    public const float MACH = 340f;         // m/s, "0.9 M" in this model
    public const float K_H = 0.6f;          // height-error gain of the arc tracker
    public const float K_P = 3f;            // heading-error gain of the turn (1/s)
    public const float ELEV_MAX = 1.2f;     // rad, the tracker's own clamp
    public const float MIN_REF = 60f;       // m, floor on the height-error look-ahead
    public const float CLOS_K = 16f;        // beam rider: offset gain, in `t_go` units
                                            // (the law's damping is sqrt(K)/2)
    public const float CLOS_T_MIN = 0.15f;  // s, floor on the time left (it divides)

    private static float _dot(Vec3 a, Vec3 b) => a.x * b.x + a.y * b.y + a.z * b.z;

    private static Vec3 _cross(Vec3 a, Vec3 b) => new(a.y * b.z - a.z * b.y,
                                                      a.z * b.x - a.x * b.z,
                                                      a.x * b.y - a.y * b.x);

    private static Vec3 _unit(Vec3 a)
    {
        float n = MathF.Sqrt(a.x * a.x + a.y * a.y + a.z * a.z);
        if (n < 1e-9f) return new Vec3(1f, 0f, 0f);
        return new Vec3(a.x / n, a.y / n, a.z / n);
    }

    /// <summary>Frontal area from the calibre - the round's own size, not a knob.</summary>
    private static float _area(WeaponDef wd)
    {
        float r = wd.dia * 0.5f;
        return MathF.PI * r * r;
    }

    /// <summary>Commanded flight-path angle of the reference arc, radians.
    ///
    /// The reference is a parabola over the launch range: it climbs at
    /// `atan(4H/R0)` off the rail and comes down at the same angle onto the
    /// target, where `R0` is the range to go plus the distance already flown (so
    /// it equals the range at the shot and breathes with a moving target).  One
    /// number therefore sets both the climb and the attack angle, and `loft` is
    /// that number: `H` is capped so the arc's climb equals the tube's own
    /// quadrant.</summary>
    public static float arc(float r, float above, float r0, WeaponDef wd)
    {
        float u = MathF.Min(1f, MathF.Max(0f, r / r0));
        // apex: what the shot asks for, what the airframe can turn out of, and
        // enough height that the motor can lift it off the ground at all
        // (thrust/weight 11.2 -> below ~6 deg of climb the round sinks)
        float h = MathF.Min(wd.apex, 0.25f * MathF.Tan(wd.loft) * r0);
        h = MathF.Min(h, r0 * r0 / (8f * wd.turn_r));
        h = MathF.Max(0.045f * r0, h);
        float slope = 4f * h * (1f - 2f * u) / r0;      // dh/dr, flight direction +
        // the tracker: the arc's local slope, corrected toward the reference height
        // so the round converges onto the arc instead of drifting parallel to it
        float want = MathF.Atan(-slope)
                     + K_H * MathF.Atan2(4f * h * u * (1f - u) - above, MathF.Max(r, MIN_REF));
        return MathF.Max(-ELEV_MAX, MathF.Min(ELEV_MAX, want));
    }

    /// <summary>Unit direction square to the velocity that turns it toward `want`.</summary>
    public static Vec3 lift_dir(Vec3 vhat, Vec3 want)
    {
        var axis = _cross(vhat, want);
        float n = MathF.Sqrt(_dot(axis, axis));
        if (n < 1e-6f) return Vec3.zero;
        return _unit(_cross(_unit(axis), vhat));
    }

    /// <summary>True proportional navigation toward `roof`: the acceleration owed.</summary>
    public static Vec3 pn_accel(Vec3 p, Vec3 v, Vec3 roof, Vec3 tgt_vel, float n_law)
    {
        var r = new Vec3(roof.x - p.x, roof.y - p.y, roof.z - p.z);
        float r2 = _dot(r, r);
        if (r2 < 1e-6f) return Vec3.zero;
        var rhat = _unit(r);
        var vrel = new Vec3(tgt_vel.x - v.x, tgt_vel.y - v.y, tgt_vel.z - v.z);
        var omega = _cross(r, vrel);
        omega = new Vec3(omega.x / r2, omega.y / r2, omega.z / r2);
        float vc = -_dot(rhat, vrel);
        var a = _cross(omega, rhat);
        float k = n_law * MathF.Max(vc, 1f);
        return new Vec3(a.x * k, a.y * k, a.z * k);
    }

    // ------------------------------------------------------------------
    // the two guidance laws
    // ------------------------------------------------------------------

    /// <summary>Fly the top attack for one tick: the ARC law.  Returns the new
    /// velocity.
    ///
    /// `p` is the round's position, `v` its velocity, `age` the seconds it has
    /// been in the air, `trav` the ground distance it has covered (both already
    /// tracked by `projectiles`).  `tgt_pos` is the *point* the seeker is staring
    /// at - the deck of the hull, or where that deck should be now that the hull
    /// is not visible - and `tgt_vel` the velocity it is drifting at.  Both are
    /// null only for a round that never had a lock: guidance stops and it is a
    /// rock with a warhead.
    ///
    /// Nothing here knows whether the hull is still there.  A stale stare point is
    /// a point like any other, which is what keeps the guidance (and the dive) the
    /// same through a lost lock as through a held one.</summary>
    public static Vec3 step(Vec3 p, Vec3 v, float age, float trav, Vec3? tgt_pos,
                            Vec3? tgt_vel, WeaponDef wd, float dt)
    {
        var (a_dir, a_want) = _arc_demand(p, v, age, trav, tgt_pos, tgt_vel, wd);
        return _airframe(v, age, wd, dt, a_dir, a_want);
    }

    /// <summary>Fly a beam rider for one tick: the DIRECT law, a command-to-line-of-
    /// sight ride.  Returns the new velocity.
    ///
    /// A beam rider is a flat, fast round pointed at what it is going to hit, so its
    /// law is the opposite of the arc's: no climb, no glide, no dive.  It is told
    /// where the laser *is* - the ray from the launcher's aperture (`origin`) to the
    /// point `Systems/Beams.cs` leads the target to (`aim`) - and its whole job is to
    /// sit on that ray:
    ///
    ///   * the offset from the line, and the rate it is growing at, nulled over the
    ///     time left before the target is reached (`a = K(d + ḋ·t_go)/t_go²`).  A
    ///     round that only chases the *point* lags the beam for its whole flight -
    ///     measured, 18 m off at 3 km with the round arriving with no time left;
    ///   * its own weight.  A missile is not a rock: it carries itself, so `g` in
    ///     the plane square to the flight path is part of the demand, and the angle
    ///     of attack that buys it is real angle of attack - induced drag included.
    ///     That is the whole of "the round sags when it is slow": under about
    ///     100 m/s the wing cannot make 1 g any more at all, and it drops out of the
    ///     beam with no authority to get back into it.
    ///
    /// `aim` null means the beam is gone.  The round has nothing to fly to and no
    /// lift demand, so it keeps its heading, drags down and falls.</summary>
    public static Vec3 direct(Vec3 p, Vec3 v, float age, Vec3 origin, Vec3? aim,
                              WeaponDef wd, float dt)
    {
        var (a_dir, a_want) = _direct_demand(p, v, origin, aim, wd);
        return _airframe(v, age, wd, dt, a_dir, a_want);
    }

    // ------------------------------------------------------------------
    // the two demands: what the guidance wants, square to the flight path
    // ------------------------------------------------------------------

    /// <summary>The arc law's demand: the reference parabola while the round is
    /// far out, proportional navigation inside `pn_r`.</summary>
    private static (Vec3 dir, float mag) _arc_demand(Vec3 p, Vec3 v, float age, float trav,
                                                     Vec3? tgt_pos, Vec3? tgt_vel,
                                                     WeaponDef wd)
    {
        var vhat = _unit(v);
        float speed = MathF.Sqrt(_dot(v, v));
        float lift_max = _lift_max(wd, speed);
        var a_dir = Vec3.zero;
        float a_want = 0f;
        if (tgt_pos is null) return (a_dir, a_want);
        var tp = tgt_pos.Value;
        float dx = tp.x - p.x;
        float dz = tp.z - p.z;
        float r = MathF.Sqrt(dx * dx + dz * dz);
        float above = p.y - tp.y;
        float r0 = MathF.Max(trav + r, 1f);
        if (r <= wd.pn_r && speed >= wd.pn_v)
        {
            var a = pn_accel(p, v, tp, tgt_vel ?? Vec3.zero, wd.pn_n);
            // the fins can only pull square to the flight path
            float d = _dot(a, vhat);
            a = new Vec3(a.x - vhat.x * d, a.y - vhat.y * d, a.z - vhat.z * d);
            a_want = MathF.Sqrt(_dot(a, a));
            a_dir = a_want > 1e-9f ? _unit(a) : Vec3.zero;
        }
        else
        {
            float elev = arc(r, above, r0, wd);
            if (age <= wd.ignite + wd.burn)
            {
                // the motor is still lit: the tube's quadrant is a ceiling on
                // the climb, so a close target pulls the nose down here
                elev = MathF.Min(elev, wd.loft);
            }
            float hx, hz;
            if (r > 1e-3f)
            {
                hx = dx / r;
                hz = dz / r;
            }
            else
            {
                hx = 1f;
                hz = 0f;
            }
            float ce = MathF.Cos(elev), se = MathF.Sin(elev);
            var want = new Vec3(hx * ce, se, hz * ce);
            a_dir = lift_dir(vhat, want);
            // the pull owed is proportional to the heading error and capped by
            // what the airframe can make at this speed: the round crabs onto
            // the arc rather than snapping onto it
            var axis = _cross(vhat, want);
            float beta = MathF.Asin(MathF.Min(1f, MathF.Sqrt(_dot(axis, axis))));
            a_want = K_P * speed * beta;
        }
        return (a_dir, MathF.Min(a_want, lift_max));
    }

    /// <summary>The direct law's demand: the pull back onto the laser line, plus the
    /// weight the wing is carrying.</summary>
    private static (Vec3 dir, float mag) _direct_demand(Vec3 p, Vec3 v, Vec3 origin,
                                                       Vec3? aim, WeaponDef wd)
    {
        if (aim is null) return (Vec3.zero, 0f);
        var vhat = _unit(v);
        float speed = MathF.Sqrt(_dot(v, v));
        float lift_max = _lift_max(wd, speed);
        // the beam: where the round is along it, how far off it, and how fast that
        // gap is opening
        var to_aim = new Vec3(aim.Value.x - origin.x, aim.Value.y - origin.y,
                              aim.Value.z - origin.z);
        float line_len = MathF.Sqrt(_dot(to_aim, to_aim));
        var line = line_len > 1e-3f ? _unit(to_aim) : new Vec3(1f, 0f, 0f);
        var from_origin = new Vec3(p.x - origin.x, p.y - origin.y, p.z - origin.z);
        float along = _dot(from_origin, line);
        var off = new Vec3(from_origin.x - line.x * along,
                           from_origin.y - line.y * along,
                           from_origin.z - line.z * along);
        float v_along = _dot(v, line);
        var off_rate = new Vec3(v.x - line.x * v_along, v.y - line.y * v_along,
                                v.z - line.z * v_along);
        // the time this round has left to be on the line: what is left of the beam,
        // over the speed it is getting there at
        float t_go = MathF.Max((line_len - along) / MathF.Max(speed, 1f), CLOS_T_MIN);
        float k = CLOS_K / (t_go * t_go);
        var a = new Vec3(-k * (off.x + off_rate.x * t_go),
                         -k * (off.y + off_rate.y * t_go),
                         -k * (off.z + off_rate.z * t_go));
        // the weight, in the plane the wing can push in: on a level round that is
        // the full 1 g straight up, on a diving one only its square-to-the-path
        // part
        var g = new Vec3(0f, GRAVITY, 0f);
        float gv = _dot(g, vhat);
        a = new Vec3(a.x - vhat.x * gv, a.y + GRAVITY - vhat.y * gv, a.z - vhat.z * gv);
        float d = _dot(a, vhat);
        a = new Vec3(a.x - vhat.x * d, a.y - vhat.y * d, a.z - vhat.z * d);
        float mag = MathF.Min(MathF.Sqrt(_dot(a, a)), lift_max);
        return (mag > 1e-9f ? _unit(a) : Vec3.zero, mag);
    }

    /// <summary>m/s² of lift the wing can make at this speed - the ceiling on
    /// everything the guidance asks for, and the reason a slow round stops
    /// answering the beam.</summary>
    private static float _lift_max(WeaponDef wd, float speed)
    {
        float q = 0.5f * RHO * speed * speed;
        return q * _area(wd) * wd.cl_max / wd.mass;
    }

    // ------------------------------------------------------------------
    // the airframe: a demand in, a new velocity out
    // ------------------------------------------------------------------

    /// <summary>One tick of the airframe both laws share: the demand becomes a
    /// lift coefficient, the coefficient an angle of attack and an induced drag,
    /// and thrust along the nose, lift square to the velocity, drag against it and
    /// gravity are integrated.</summary>
    private static Vec3 _airframe(Vec3 v, float age, WeaponDef wd, float dt,
                                  Vec3 a_dir, float a_want)
    {
        float speed = MathF.Sqrt(_dot(v, v));
        var vhat = _unit(v);
        float q = 0.5f * RHO * speed * speed;
        float s = _area(wd);
        float cl = 0f;
        float alpha = 0f;
        Vec3 nose;
        if (a_want > 1e-9f && q * s > 1e-9f)
        {
            cl = MathF.Min(wd.cl_max, wd.mass * a_want / (q * s));
            alpha = cl / wd.cl_alpha;             // the needed angle of attack
            // the nose leads the velocity toward the lift: a pull lifts the nose
            // above the flight path, a pushover puts it under, and the thrust
            // follows it either way
            float ca = MathF.Cos(alpha), sa = MathF.Sin(alpha);
            nose = new Vec3(vhat.x * ca + a_dir.x * sa,
                            vhat.y * ca + a_dir.y * sa,
                            vhat.z * ca + a_dir.z * sa);
        }
        else
        {
            a_want = 0f;
            nose = vhat;
        }
        float push = 0f;
        if (age > wd.ignite && age <= wd.ignite + wd.burn && speed < wd.maxvel)
            push = wd.thrust * wd.mass;                  // N along the nose
        float mfrac = 1f / wd.mass;
        // drag polar: profile drag, the induced price of the lift just commanded
        // (squared, so a pushover costs what a pull-up costs) and the transonic rise
        float over = MathF.Max(0f, speed / MACH - 0.80f);
        float cd = wd.cd0 + wd.k_cl * cl * cl + 0.4f * over * over;
        float drag = q * s * cd;
        float lift = q * s * cl;                   // = a_want·m, capped by cl_max
        float ax = (push * nose.x - drag * vhat.x) * mfrac + a_dir.x * lift * mfrac;
        float ay = (push * nose.y - drag * vhat.y) * mfrac + a_dir.y * lift * mfrac - GRAVITY;
        float az = (push * nose.z - drag * vhat.z) * mfrac + a_dir.z * lift * mfrac;
        var nv = new Vec3(v.x + ax * dt, v.y + ay * dt, v.z + az * dt);
        speed = MathF.Sqrt(_dot(nv, nv));
        float cap = wd.maxvel;
        if (speed > cap)                              // structural / motor limit
        {
            float k = cap / speed;
            nv = new Vec3(nv.x * k, nv.y * k, nv.z * k);
        }
        return nv;
    }
}
