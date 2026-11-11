// How high to hold: the elevation, and the flight-time integrator it is solved
// against.
//
// The pieces: `solve_elevation` (the elevation that drops a round on a point),
// `flight_at` (where the round is after N metres, and when it gets there - the
// same 1/30 s semi-implicit Euler the rounds fly) and `zero_at` (the elevation for
// a gun zeroed to a distance).
//
// The law that uses them - a gun zeroed to Z, the sight line that puts its round
// on a moving target - is `Core/FireControl.cs`, and both the client and the crews
// solve it there.
//
// Only direct fire is zeroed: bullets and unguided rockets.  SACLOS and the
// infrared missile steer themselves and keep the plain aim.

namespace Mech.Server;

public static class Ballistics
{
    public const float DT = 1f / 30f;    // same step as the projectile integrator
    public const float MIN_DIST = 25f;   // closer than this the aim point is the machine itself
    public const int ITERS = 4;

    /// <summary>Where the round is - height and time of flight - once it has covered
    /// `dist` metres horizontally.
    ///
    /// Null when the round expires (or never gets that far) first.  This is the
    /// integrator the fire control solves against, and it is the same 1/30 s
    /// semi-implicit Euler the rounds themselves fly (`Systems/Flights.cs`).</summary>
    public static (float y, float t)? flight_at(Vec3 origin, float yaw, float pitch, WeaponDef wd,
                                                float dist)
    {
        var d = MathUtil.dir_from(yaw, pitch);
        float vx = d.x * wd.vel, vy = d.y * wd.vel, vz = d.z * wd.vel;
        float x = origin.x, y = origin.y, z = origin.z;
        float g = wd.g;
        float thrust = wd.thrust;
        float maxvel = wd.maxvel;
        float life = wd.life;
        float prev_h = 0f;
        float prev_y = y;
        float t = 0f;
        while (t < life)
        {
            if (thrust > 0f)
            {
                float speed = MathF.Sqrt(vx * vx + vy * vy + vz * vz);
                float k = MathF.Min(maxvel, speed + thrust * DT) / MathF.Max(speed, 1e-6f);
                vx *= k;
                vy *= k;
                vz *= k;
            }
            vy -= g * DT;
            x += vx * DT;
            y += vy * DT;
            z += vz * DT;
            t += DT;
            float h = MathF.Sqrt((x - origin.x) * (x - origin.x) + (z - origin.z) * (z - origin.z));
            if (h >= dist)
            {
                float span = h - prev_h;
                float f = span > 1e-6f ? (dist - prev_h) / span : 1f;
                // the time is interpolated the same way the height is: reporting the
                // end of the tick costs up to a whole tick of flight time, which
                // against a target climbing at 40 m/s is over a metre of lead
                return (prev_y + (y - prev_y) * f, t - DT + f * DT);
            }
            prev_h = h;
            prev_y = y;
        }
        return null;
    }

    /// <summary>Launch elevation that drops the round on `target`, or null.</summary>
    public static float? solve_elevation(Vec3 origin, Vec3 target, WeaponDef wd, float yaw,
                                         float pitch, float max_elev = Defs.ZERO_MAX_ELEV)
    {
        float dx = target.x - origin.x, dz = target.z - origin.z;
        float d = MathF.Sqrt(dx * dx + dz * dz);
        if (d < MIN_DIST) return null;
        float h = target.y - origin.y;
        float g = wd.g;
        float vel = wd.vel;
        if (g > 0f && vel > 0f && wd.thrust == 0f)
        {
            // direct shot: closed form, low arc; half a step of Euler error over a
            // 3 km flight is under a metre, so no refinement is needed.  Anything
            // past the round's own reach cannot be zeroed for at all
            if (d > vel * wd.life) return null;
            float v2 = vel * vel;
            float disc = v2 * v2 - g * (g * d * d + 2f * h * v2);
            if (disc < 0f) return null;
            return MathF.Atan2(v2 - MathF.Sqrt(disc), g * d);
        }
        // thrusting rocket: no closed form, and the altitude at range is a steep
        // function of the elevation, so walk it in with a secant instead of Newton
        // `flight_at` gives an absolute height, so the walk compares it against the
        // target's absolute height: `h` is the drop below the muzzle and only serves
        // the closed form above.  Mixing the two put the round one muzzle height
        // low, which for a slow launch is hundreds of metres short
        float t0 = pitch, t1 = pitch + 0.02f;
        var f0 = flight_at(origin, yaw, t0, wd, d);
        if (f0 is null) return null;
        for (int it = 0; it < ITERS + 2; it++)
        {
            var f1 = flight_at(origin, yaw, t1, wd, d);
            if (f1 is null) return null;
            float err = target.y - f1.Value.y;
            if (MathF.Abs(err) < 0.1f) return t1;
            float dy = f1.Value.y - f0.Value.y;
            if (MathF.Abs(dy) < 1e-6f) return null;
            float t2 = MathF.Max(-1.2f, MathF.Min(max_elev, t1 + err * (t1 - t0) / dy));
            t0 = t1;
            f0 = f1;
            t1 = t2;
        }
        return MathF.Abs(t1) <= max_elev ? t1 : null;
    }

    /// <summary>Elevation that puts the round on the sight line `distance` metres out.
    ///
    /// The aim point is a fixed way down the crosshair rather than the first thing
    /// the ray hits, so the solution depends on the pilot's setting and the hull's
    /// attitude only - never on whether a ridge, a tree or a hull happens to be in
    /// the way.  That is what stops the reticle jumping at a crest line.</summary>
    public static float? zero_at(Vec3 origin, float yaw, float pitch, WeaponDef wd,
                                 float distance, float max_elev = Defs.ZERO_MAX_ELEV)
    {
        var d = MathUtil.dir_from(yaw, pitch);
        var target = new Vec3(origin.x + d.x * distance,
                              origin.y + d.y * distance,
                              origin.z + d.z * distance);
        return solve_elevation(origin, target, wd, yaw, pitch, max_elev);
    }
}
