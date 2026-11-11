// The crew's intent, read by the machine: which point to steer at, and the legs
// that walk a hull towards it.
//
// A crew says *where* (`nav_route`, or `nav_x` / `nav_z`) and *how fast*
// (`nav_speed`); it says nothing about how a chassis gets there.  This file is the
// one translation every driver shares - route cursor, next way point, and the fan
// a walking hull tries when the step ahead of it is blocked - and the wheeled
// driver takes it from there with its own steering.

namespace Mech.Server.Systems.Movement;

public static class Drive
{
    /// <summary>How close a walking hull wants to come to its way point before it
    /// counts as reached (`nav` step, metres).</summary>
    public const float LEGS_ARRIVE = 55.0f;

    // the fan a walking hull tries when the step ahead of it is blocked: straight
    // out to the side first, then wider, and finally the way it came - the way out
    // of a court is usually the way in
    private static readonly float[] FAN =
        { 0.5f, -0.5f, 1.0f, -1.0f, 1.6f, -1.6f, 2.2f, -2.2f, MathF.PI };
    // m: the steps a fan bearing has to be clear for
    private static readonly float[] PROBE = { 0.8f, 2.0f, 4.0f };

    /// <summary>Whether a hull of radius `rad` fits at (x, z): the samples the
    /// integration uses, plus the ground height - a mech has legs and climbs a
    /// raised spot, but a step high enough to swallow the hull is a wall.</summary>
    public static bool clear(Terrain terrain, float x, float z, float y, float rad)
    {
        if (terrain.blocked(x, z) || terrain.blocked(x + rad, z) ||
            terrain.blocked(x - rad, z) || terrain.blocked(x, z + rad) ||
            terrain.blocked(x, z - rad))
            return false;
        if (terrain.trunk_blocked(x, z, rad)) return false;
        return MathF.Abs(terrain.height_at(x, z) - y) < 4f;
    }

    /// <summary>Whether a hull can drive at least a couple of metres this way.
    ///
    /// Tested *along the first few metres*, not at one point in front: the
    /// integration moves one step at a time and refuses any step where the hull does
    /// not fit, so a fan that only looks 14 m out will happily steer through a house
    /// whose far side is open.</summary>
    private static bool clear_run(Terrain terrain, float x, float z, float y, float rad,
                                  float dx, float dz)
    {
        foreach (float step in PROBE)
            if (!clear(terrain, x + dx * step, z + dz * step, y, rad)) return false;
        return true;
    }

    /// <summary>The point this hull is driving at, and the speed it may make.
    ///
    /// A route is followed with the cursor in `path_i`; a point (`nav_route` = -1)
    /// is steered at directly.  `radius` is the acceptance radius: way points
    /// inside it (or behind the hull, `Nav.passed`) are dropped, which is what lets
    /// a car carry speed into a corner and cut across the inside of it.</summary>
    public static void target(World world, MoveRow r, float radius, out float ax, out float az)
    {
        var route = Nav.get(world, r.nav_route);
        if (route is null)
        {
            ax = r.nav_x;
            az = r.nav_z;
            return;
        }
        r.path_i = Nav.advance(route, r.path_i, r.pos.x, r.pos.z, radius);
        (ax, az) = Nav.at(route, r.path_i);
    }

    /// <summary>Walk a hull at its intent: the command is the direction to the way
    /// point at `nav_speed`, fanned out when the step ahead is blocked.
    ///
    /// A walking hull's machine is its legs, so the command *is* the velocity it
    /// will make - `Mech.run` puts the ramp and the leg alignment on top of it.</summary>
    public static void legs(World world, MoveRow r, float dt)
    {
        float speed = r.nav_speed;
        if (speed <= 0f)
        {
            r.vel_cmd = new Vec3(0f, 0f, 0f);
            return;
        }
        speed = MathF.Min(speed, Defs.UNIT_DEFS[r.etype].speed);
        target(world, r, LEGS_ARRIVE, out float ax, out float az);
        float px = r.pos.x, pz = r.pos.z;
        float dx = ax - px, dz = az - pz;
        float d = MathF.Sqrt(dx * dx + dz * dz);
        if (d < 1e-3f)
        {
            r.vel_cmd = new Vec3(0f, 0f, 0f);
            return;
        }
        dx /= d;
        dz /= d;
        // obstacle avoidance: fan out when the ground ahead is not clear for a
        // hull this size
        var terrain = world.terrain;
        float rad = Defs.UNIT_DEFS[r.etype].radius * 0.6f;
        float y = r.pos.y;
        if (!clear_run(terrain, px, pz, y, rad, dx, dz))
        {
            float bese = MathF.Atan2(dz, dx);
            // The escape is the *smallest course change* that clears, with the legs the
            // hull already has as the tie-break: the bearing it is already turning into
            // wins while it stays clear, so a hull that has to pass a house commits to
            // one side of it and walks that side.  A fixed list of bearings read in
            // order hands a hull between two clear ones the one that comes first, which
            // alternates every few ticks - and a ramp that reverses itself leaves the
            // hull grinding in place beside its own base for the rest of the battle.
            float swing_best = float.MaxValue;
            for (int k = 0; k < FAN.Length; k++)
            {
                float a = bese + FAN[k];
                float nx = MathF.Cos(a), nz = MathF.Sin(a);
                if (!clear_run(terrain, px, pz, y, rad, nx, nz)) continue;
                float swing = MathF.Abs(MathUtil.wrap_pi(a - r.leg_yaw));
                if (swing >= swing_best) continue;
                swing_best = swing;
                dx = nx;
                dz = nz;
            }
        }
        r.vel_cmd = new Vec3(dx * speed, 0f, dz * speed);
    }
}
