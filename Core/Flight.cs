// The airframe's arithmetic: six terms, and the one flight that needs them outside a
// world.
//
// In the simulation a round is flown by eleven passes (`Systems/Flights/`), one per
// term and one that spends their sum.  This file is those terms as functions of
// numbers, because one caller has no world: the fire control solves a barrel's angle
// by flying the round it is about to fire (`Ballistics.flight_at`), and `--check fcs`
// and `tests/backend` read that arc.  `airframe` below is the six terms in the order
// the passes run them, so the arc the barrel was aimed on and the arc the round flies
// cannot drift apart.
//
//     v = Flight.slide(v, age, wd, dt)                     an unguided round
//     v = Flight.airframe(v, age, wd, dt, a_dir, a_want)   ...or one the wing is steering
//
// Both return the new *velocity* and nothing else; the caller advances the position by
// it (`p1 = p0 + v * dt`), exactly as `Systems/Flights/Airframe.cs` does.
//
// A round climbs because the motor pushes it, glides because altitude is worth range,
// and pays for a hard pull in speed - which is why the *pull* is the fragile thing: it
// is `q*S*cl_max/m`, and `q` is speed squared.  The two guided laws that turn a target
// into a demand are `Systems/Flights/Top.cs` and `Systems/Flights/Saclos.cs`.

namespace Mech.Server;

public static class Flight
{
    public const float RHO = 1.225f;        // kg/m³, sea level - the apex is under 1 km
    public const float GRAVITY = 9.81f;     // m/s²
    public const float MACH = 340f;         // m/s, "0.9 M" in this model

    /// <summary>Frontal area from the calibre - the round's own size, not a knob.</summary>
    private static float _area(WeaponDef wd)
    {
        float r = wd.dia * 0.5f;
        return MathF.PI * r * r;
    }

    /// <summary>The velocity's direction and the dynamic pressure - what every force
    /// is written against.</summary>
    public static (Vec3 vhat, float q) airflow(Vec3 v)
    {
        float speed = v.len;
        return (v.unit, 0.5f * RHO * speed * speed);
    }

    /// <summary>The lift coefficient the demand asks for, and the nose that follows
    /// it: a pull lifts the nose above the flight path, a pushover puts it under, and
    /// the thrust goes where the nose points either way.
    ///
    /// Nothing asked for means no lift and a nose on the flight path (`CL = 0`), so an
    /// unguided round is this function with `a_want = 0`.</summary>
    public static (float cl, Vec3 nose) aoa(Vec3 vhat, Vec3 a_dir, float a_want,
                                           float q, WeaponDef wd)
    {
        float s = _area(wd);
        if (a_want <= 1e-9f || q * s <= 1e-9f) return (0f, vhat);
        float cl = MathF.Min(wd.cl_max, wd.mass * a_want / (q * s));
        float alpha = cl / wd.cl_alpha;               // the angle of attack it costs
        float ca = MathF.Cos(alpha), sa = MathF.Sin(alpha);
        return (cl, new Vec3(vhat.x * ca + a_dir.x * sa,
                             vhat.y * ca + a_dir.y * sa,
                             vhat.z * ca + a_dir.z * sa));
    }

    /// <summary>m/s² of lift at this coefficient - the wing's own force.  At `cl_max`
    /// this is the ceiling on everything the guidance may ask for, which is why a slow
    /// round stops answering.</summary>
    public static float lift_accel(WeaponDef wd, float q, float cl)
        => q * _area(wd) * cl / wd.mass;

    /// <summary>m/s² the drag comes off at: profile drag, the induced price of the
    /// lift just commanded (squared, so a pushover costs what a pull-up costs) and
    /// the transonic rise.</summary>
    public static float drag_accel(WeaponDef wd, float speed, float q, float cl)
    {
        float over = MathF.Max(0f, speed / MACH - 0.80f);
        float cd = wd.cd0 + wd.k_cl * cl * cl + 0.4f * over * over;
        return q * _area(wd) * cd / wd.mass;
    }

    /// <summary>m/s² along the nose, while the motor is lit and the structure is not
    /// already at its limit.  `throttle` is 1 for a motor that burns flat out for its
    /// window and 0 for a round that has none.</summary>
    public static float thrust_accel(WeaponDef wd, float age, float speed, float throttle)
    {
        if (throttle <= 0f) return 0f;
        if (age <= wd.ignite || age > wd.ignite + wd.burn) return 0f;
        if (speed >= wd.maxvel) return 0f;
        return wd.thrust * throttle;
    }

    /// <summary>The airframe's own ceiling: beyond `maxvel` the velocity keeps its
    /// direction and loses the excess.  This is the structure's limit, not the motor's,
    /// so it is a clamp on the tick's result rather than a force.</summary>
    public static Vec3 cap(Vec3 v, WeaponDef wd)
    {
        float speed = v.len;
        if (speed <= wd.maxvel) return v;
        float k = wd.maxvel / speed;
        return new Vec3(v.x * k, v.y * k, v.z * k);
    }

    /// <summary>One tick of the airframe: the demand becomes a lift coefficient, the
    /// coefficient an angle of attack and an induced drag, and thrust along the nose,
    /// lift square to the velocity, drag against it and gravity are integrated.
    ///
    /// `a_dir` is a unit direction square to the flight path and `a_want` the m/s² of
    /// lift asked for there; nothing asked for (`a_want` 0) is an unguided round.</summary>
    public static Vec3 airframe(Vec3 v, float age, WeaponDef wd, float dt,
                                Vec3 a_dir, float a_want)
    {
        float speed = v.len;
        var (vhat, q) = airflow(v);
        var (cl, nose) = aoa(vhat, a_dir, a_want, q, wd);
        float push = thrust_accel(wd, age, speed, 1f);
        float lift = lift_accel(wd, q, cl);
        float drag = drag_accel(wd, speed, q, cl);
        var acc = a_dir * lift - vhat * drag + nose * push;
        acc = new Vec3(acc.x, acc.y - GRAVITY, acc.z);
        var nv = new Vec3(v.x + acc.x * dt, v.y + acc.y * dt, v.z + acc.z * dt);
        return cap(nv, wd);
    }

    /// <summary>Fly an unguided round for one tick: the same airframe with nothing
    /// asked of it, which is the whole difference between a rocket and a missile.
    ///
    /// A rocket has no seeker, no beam and no wing: there is no point to fly to and
    /// no lift to spend, so its nose stays on the flight path, thrust goes along it
    /// for `burn` seconds and drag comes off it from the speed it is doing - profile
    /// drag plus the transonic rise, from the same polar the two guided kinds pay.
    /// Past burnout the speed *decays*: 610 m/s at the end of the pod's motor is
    /// ~550 m/s at 1 km, and the round is 6.6 m lower there than a constant-speed
    /// one would be, which is what the fire control is solved against.
    ///
    /// The round's own size is `dia`, its weight `mass`, and `maxvel` stays what it
    /// is for the guided kinds - the structure's ceiling, not the motor's.
    ///
    /// `age` is the seconds the round has been in the air, so the motor's window is
    /// `ignite` .. `ignite + burn`; `Ballistics.flight_at` calls this same function
    /// for the fire control's integrator, which is what keeps the barrel's angle and
    /// the round's arc the same line.</summary>
    public static Vec3 slide(Vec3 v, float age, WeaponDef wd, float dt)
        => airframe(v, age, wd, dt, Vec3.zero, 0f);
}
