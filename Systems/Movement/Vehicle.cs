// A wheeled hull's locomotion, and the path following that goes with it.
//
// A car cannot be steered at a point: it rolls along its own axis, turns at
// `speed / APC_TURN_R` and answers a yaw command with a steering angle.  So the
// crew's intent arrives as a way point (`Systems/Movement/Support/Drive.cs`) and
// this file does the two things a car's driver does with it - keep the throttle at
// a speed it can still hold *through the corners ahead* (that is `Nav.route_speed`,
// the hull's own envelope, applied here and never by the crew), and steer.
//
// It is the same rear-axle bicycle model either way: the command is a direction
// and a speed, the hull honours only the part that lies along its axis, and the
// gearbox has `APC_REVERSE` of top speed which nothing but backing off a wall uses.
// A goal behind the hull is taken by turning round for it - the brake grows with
// the heading error, so a U-turn comes down to the hull's radius.

using Mech.Server.Systems.AI;

namespace Mech.Server.Systems.Movement;

public static class Vehicle
{
    public static void run(World world, float dt)
    {
        foreach (var g in world.ecs.groups(MoveColumns.OnVehicle))
        {
            var c = new MoveColumns(g);
            bool crew = g.has(AiTags.CREW);
            foreach (var r in c)
            {
                float top = Defs.UNIT_DEFS[r.etype].speed;
                float speed = crew ? r.nav_speed : 0f;
                if (!crew)
                {
                    // a puppet's carrier: its command is already in `vel_cmd`
                }
                else if (speed <= 0f)
                {
                    // hold: the order is nothing, and the machine brakes
                    r.vel_cmd = new Vec3(0f, 0f, 0f);
                }
                else
                {
                    speed = MathF.Min(speed, top);
                    var p = r.pos;
                    float radius = Nav.arrive_radius(
                        MathUtil.dist2d(0f, 0f, r.vel.x, r.vel.z));
                    Drive.target(world, r, radius, out float ax, out float az);
                    var route = Nav.get(world, r.nav_route);
                    if (route is not null)
                        speed = Nav.route_speed(route, r.path_i, p.x, p.z, speed);
                    float dx = ax - p.x, dz = az - p.z;
                    float d = MathF.Sqrt(dx * dx + dz * dz);
                    r.vel_cmd = d < 1e-3f ? new Vec3(0f, 0f, 0f) : new Vec3(dx / d * speed, 0f, dz / d * speed);
                }
                var (vx, vz) = wheels(r, dt, top);
                Integrate.step(world, r, vx, vz, wheeled: true, dt);
            }
        }
    }

    /// <summary>One tick of wheeled locomotion: `(vx, vz)` along the hull.
    ///
    /// The command is the same `vel_cmd` every other hull gets - a direction and a
    /// speed - but a car can only honour the part of it that lies along its own
    /// axis, so the direction becomes a steering angle and the speed a throttle.
    ///
    /// Two things fall out of that and are worth saying out loud: the hull cannot
    /// turn at a standstill (the yaw rate is speed / turn radius), and it never
    /// drives fast in reverse.</summary>
    private static (float vx, float vz) wheels(MoveRow r, float dt, float top)
    {
        var cmd = r.vel_cmd;
        float cx = cmd.x, cz = cmd.z;
        float u = r.wheel_speed;
        float yaw = r.leg_yaw;
        float mag = MathF.Sqrt(cx * cx + cz * cz);
        float err = 0f;
        float target = 0f;
        if (r.backout_t > 0f)
        {
            // the nose is against something: back off the way we came, wheels held
            // over so the hull is pointing somewhere else when it comes back out
            r.backout_t = r.backout_t - dt;
            target = -top * Defs.APC_REVERSE;
            err = Defs.APC_STEER_LOCK;
        }
        else if (mag > 0.05f)
        {
            err = MathUtil.wrap_pi(MathF.Atan2(cz, cx) - yaw);
            // brake into a bend, and harder the worse the heading error is - a
            // couple of degrees of correction on a straight is not a reason to
            // give away any speed at all, a half turn is
            target = mag * (1f - 0.55f * MathF.Min(1f, MathF.Max(0f, MathF.Abs(err) - 0.4f) / 1.2f));
        }
        float acc;
        if (target * u >= 0f && MathF.Abs(target) > MathF.Abs(u))
        {
            // throttle: thrust minus drag, so the hull pulls hardest off the line
            // and the last few km/h take as long as the first thirty
            float ratio = u / Defs.APC_V_INF;
            acc = Defs.APC_THRUST * MathF.Max(0f, 1f - ratio * ratio);
        }
        else
        {
            acc = Defs.APC_BRAKE;
        }
        float step = acc * dt;
        u += MathF.Max(-step, MathF.Min(step, target - u));
        // steering: the wheels saturate at `APC_STEER_LOCK` of heading error, and a
        // hull turns at (speed / radius) - but never harder than `APC_LAT_ACCEL` of
        // side load, which is what stops it swinging from lock to lock at speed
        float rate = MathF.Min(MathF.Abs(u) / Defs.APC_TURN_R,
                               Defs.APC_LAT_ACCEL / MathF.Max(MathF.Abs(u), 0.5f));
        float steer = MathF.Max(-1f, MathF.Min(1f, err / Defs.APC_STEER_LOCK));
        yaw = MathUtil.wrap_pi(yaw + rate * steer * dt);
        r.wheel_speed = u;
        r.leg_yaw = yaw;
        r.yaw = yaw;
        return (MathF.Cos(yaw) * u, MathF.Sin(yaw) * u);
    }
}
