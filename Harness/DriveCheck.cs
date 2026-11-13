// `--check drive`: the mech's thrust model against its own theory.
//
// The mech does not roll up to a commanded speed, and it does not push with a
// constant force either: the legs are an engine whose power rises from
// `MECH_HP_LOW` to `MECH_HP_HIGH`, so the force is `P(v)/v` - which the drive's own
// 1.5 g limits from a standstill, and which the engine gives up to 0.81 g at the top
// (`Systems/Movement/Mech/`).
// Gravity spends or returns its share and the quadratic drag gives the machine its
// top end.  This check walks a hull on a treadmill - one spot, its own ground, the
// stick held - and prints what the simulation settles at next to what the law says
// it should, that law being solved here for the speed at which the net force is
// zero:
//
//   F(v)/m * cos(theta) - g sin(theta) - k v^2 = 0,   sin = s / sqrt(1+s^2)
//
// (`F` is the simulation's own `Mech.Thrust.Force`, so the check and the machine
// cannot describe two different drives.)
//
//   ./dotnet.sh run -- --check drive
//
// The treadmill is the point: reaching a drag balance takes thirty seconds and
// three kilometres of clear ground, and a 4 km map runs out of straight before
// that.  So the hull is put back on its spot every tick and keeps the velocity it
// made - the forces are the same, the ground is the same, and only the odometer is
// missing.  It also means the slope is the one the terrain really has there: every
// row prints the gradient measured under the hull, and the theory beside it is
// solved from that same gradient.

using Mech.Server.Systems.Movement;

namespace Mech.Server.Harness;

// the mech's own law, under the name this file reads it by
using MechThrust = Mech.Server.Systems.Movement.Mech.Thrust;

public static class DriveCheck
{
    private const float DT = 1f / 30f;
    /// <summary>s of simulated force per row: enough for a drag balance to settle in
    /// its fourth digit (the approach is an artanh, so the last km/h crawls).</summary>
    private const float SETTLE = 90f;
    /// <summary>m/s the cruise row asks the machine for.</summary>
    private const float CRUISE = 12f;

    public static int run()
    {
        var world = new World(seed: 7, mode: "range");
        var terrain = world.terrain;
        Console.WriteLine($"drive check: {terrain.w} x {terrain.d} tiles, "
                          + $"drag {Defs.MECH_DRAG:0.0000000} /m");
        int bad = curve();
        bad += design();
        // the steepest face the map has, and the flattest ground: the rows are the
        // law at whatever gradient is really there
        var (sx, sz, ss) = Pick(world, MathF.Tan(30f * MathF.PI / 180f));
        var (fx, fz, fs) = Pick(world, 0f);
        Console.WriteLine($"steepest spot ({sx:0}, {sz:0}) slope {ss:0.000}; "
                          + $"flattest ({fx:0}, {fz:0}) slope {fs:0.000}");
        Console.WriteLine("row              angle   theory  measured     km/h  0-100  to-full");

        bad += settle(world, "flat", fx, fz, +1f);
        bad += settle(world, "uphill", sx, sz, +1f);
        bad += settle(world, "downhill", sx, sz, -1f);
        bad += brake(world, fx, fz);
        bad += cruise(world, fx, fz);
        bad += park(world, "park up", sx, sz, +1f);
        bad += park(world, "park down", sx, sz, -1f);

        world.ecs.validate();
        Console.WriteLine(bad == 0 ? "\nall rows match the law" : $"\n{bad} row(s) off");
        return bad == 0 ? 0 : 1;
    }

    /// <summary>The power curve, at the speeds a pilot reads off the dial: what the
    /// engine is rated at, what the legs may actually put down, and what it feels
    /// like.</summary>
    private static int curve()
    {
        float cap = Defs.MECH_G_LIMIT * Defs.GRAVITY * Defs.MECH_MASS;
        // where the curve drops under the machine's g limit: below this it is a flat
        // 1.5 g, above it the engine
        float lo = 0.1f, hi = 200f;
        for (int k = 0; k < 80; k++)
        {
            float mid = 0.5f * (lo + hi);
            if (MechThrust.Force(mid) >= cap - 1e-3f) lo = mid;
            else hi = mid;
        }
        Console.WriteLine($"power curve: {Defs.MECH_HP_LOW:0} hp at a standstill rising to "
                          + $"{Defs.MECH_HP_HIGH:0} hp at {Defs.MECH_RATED_V * 3.6f:0} km/h, "
                          + $"mass {Defs.MECH_MASS / 1000f:0.0} t, drive capped at "
                          + $"{Defs.MECH_G_LIMIT:0.0} g = {cap / 1000f:0} kN "
                          + $"({cap / Defs.MECH_MASS:0.0} m/s^2) up to {lo * 3.6f:0} km/h");
        Console.WriteLine("  km/h     hp    force    accel");
        foreach (float v in new[] { 0f, 10f, 20f, 21.2f, 30f, 40f, Defs.MECH_RATED_V, 61.11f })
        {
            float f = MechThrust.Force(v);
            // what the curve is rated at there, which is more than the legs may use
            // while the g limit is what they are against
            float hp = Defs.MECH_HP_LOW + (Defs.MECH_HP_HIGH - Defs.MECH_HP_LOW)
                                         * MathF.Min(v / Defs.MECH_RATED_V, 1f);
            Console.WriteLine($"  {v * 3.6f,5:0} {hp,6:0} {f / 1000f,7:0} kN "
                              + $"{f / Defs.MECH_MASS,5:0.00} m/s^2 "
                              + $"({f / Defs.MECH_MASS / Defs.GRAVITY:0.00} g)");
        }
        return 0;
    }

    /// <summary>The two figures the law is anchored on: 8000 hp at the rated speed,
    /// and a flat balance that lands on that same speed.</summary>
    private static int design()
    {
        float peak = MechThrust.Force(Defs.MECH_RATED_V) * Defs.MECH_RATED_V / Defs.HORSEPOWER;
        float flat = Balance(0f);
        bool ok = MathF.Abs(peak - Defs.MECH_HP_HIGH) < 1f
                  && MathF.Abs(flat - Defs.MECH_RATED_V) < 0.5f;
        Console.WriteLine($"design          {peak:0} hp at {Defs.MECH_RATED_V * 3.6f:0} km/h, "
                          + $"flat balance {flat * 3.6f:0.0} km/h (rated {Defs.MECH_RATED_V * 3.6f:0})"
                          + (ok ? "" : "   <-- off"));
        return ok ? 0 : 1;
    }

    /// <summary>The speed a slope settles at, off the constants alone: the root of the
    /// net force, bisected.  Uphill is a positive `s` (rise over run along the way
    /// the hull is going).</summary>
    private static float Balance(float s)
    {
        float cos = 1f / MathF.Sqrt(1f + s * s);
        float Net(float v) => MechThrust.Force(v) / Defs.MECH_MASS * cos
                              - Defs.GRAVITY * s * cos - Defs.MECH_DRAG * v * v;
        float lo = 0.01f, hi = 200f;
        for (int k = 0; k < 80; k++)
        {
            float mid = 0.5f * (lo + hi);
            if (Net(mid) > 0f) lo = mid;
            else hi = mid;
        }
        return 0.5f * (lo + hi);
    }

    /// <summary>Find the spot whose slope along +x is nearest `want`, with the
    /// magnitude the terrain really has there.</summary>
    private static (float x, float z, float slope) Pick(World world, float want)
    {
        var t = world.terrain;
        float bx = t.w * Terrain.TILE * 0.5f, bz = t.d * Terrain.TILE * 0.5f;
        float best = float.MaxValue, bs = 0f;
        for (float z = 2f * Terrain.TILE; z < (t.d - 2) * Terrain.TILE; z += Terrain.TILE * 0.5f)
        {
            for (float x = 2f * Terrain.TILE; x < (t.w - 2) * Terrain.TILE; x += Terrain.TILE * 0.5f)
            {
                if (t.blocked(x, z) || t.blocked(x + 4f, z) || t.blocked(x, z + 4f)) continue;
                float s = t.slope_along(x, z, 1f, 0f);
                float err = MathF.Abs(s - want);
                if (err >= best) continue;
                // a hull is 6.5 m across: a spot whose shoulders fall away is not the
                // face it reads
                float shoulder = (t.slope_along(x, z + 4f, 1f, 0f)
                                  + t.slope_along(x, z - 4f, 1f, 0f)) * 0.5f;
                if (MathF.Abs(shoulder - s) > 0.15f) continue;
                best = err;
                bs = s;
                bx = x;
                bz = z;
            }
        }
        return (bx, bz, bs);
    }

    /// <summary>Hold the stick at the top and read the speed it settles at.</summary>
    private static int settle(World world, string name, float x, float z, float dir)
    {
        var (eid, speed, t100, tfull) = Run(world, x, z, dir, full: true, seconds: SETTLE);
        float s = world.terrain.slope_along(x, z, dir, 0f);
        float want = Balance(s);
        float err = MathF.Abs(speed - want) / MathF.Max(want, 1f);
        bool ok = err <= 0.02f;
        float deg = MathF.Atan(MathF.Abs(s)) * 180f / MathF.PI;
        Console.WriteLine($"{name,-14} {deg,5:0}   {want,7:0.00} {speed,9:0.00} "
                          + $"{speed * 3.6f,8:0} {Clock(t100),6} {Clock(tfull),8}"
                          + (ok ? "" : "   <-- off"));
        world.ecs.destroy(eid);
        return ok ? 0 : 1;
    }

    /// <summary>From a settled 180 km/h with the stick released: how far it runs out.</summary>
    private static int brake(World world, float x, float z)
    {
        var (eid, speed, _, _) = Run(world, x, z, 1f, full: true, seconds: 30f);
        var pos = world.ecs.fv("pos");
        var cmd = world.ecs.fv("vel_cmd");
        float x0 = pos.get(eid, 0), z0 = pos.get(eid, 2);
        float t = 0f;
        while (speed > 0.05f && t < 20f)
        {
            cmd.set(eid, 0, 0f);
            cmd.set(eid, 2, 0f);
            world.update(DT);
            sweep(world, eid);
            var v = world.ecs.fv("vel");
            speed = MathF.Sqrt(v.get(eid, 0) * v.get(eid, 0) + v.get(eid, 2) * v.get(eid, 2));
            t += DT;
        }
        float d = MathUtil.dist2d(x0, z0, pos.get(eid, 0), pos.get(eid, 2));
        // the brake alone takes v^2 / 2a; the drag charging the whole way shortens it,
        // so the row is a band rather than a figure
        float want = 50f * 50f / (2f * Defs.MECH_BRAKE);
        bool ok = d < want && d > 0.5f * want;
        Console.WriteLine($"{"brake",-14} {"",5}   {"",7} run out from 180 km/h: {d:0.0} m in "
                          + $"{t:0.00} s (brake alone {want:0.0} m){(ok ? "" : "   <-- off")}");
        world.ecs.destroy(eid);
        return ok ? 0 : 1;
    }

    /// <summary>A cruise speed short of the top: the governor holds it on the flat.</summary>
    private static int cruise(World world, float x, float z)
    {
        var (eid, speed, _, _) = Run(world, x, z, 1f, full: false, seconds: 40f);
        bool ok = MathF.Abs(speed - CRUISE) < 0.6f;
        Console.WriteLine($"{"cruise",-14} {"",5}   {"",7} asked {CRUISE:0} m/s, held {speed:0.00} m/s"
                          + (ok ? "" : "   <-- off"));
        world.ecs.destroy(eid);
        return ok ? 0 : 1;
    }

    /// <summary>Stopping on a hillside: brake to rest with the stick released, then
    /// stay there.
    ///
    /// A hull that was parked on a hill and slid down it would be a billiard ball, so
    /// the legs carry the hill: `Slope` spends gravity on a hull that is *going*
    /// somewhere, and a hull told nothing is standing on its legs.  What this row
    /// measures is the whole chain agreeing - the brake takes it to rest, the slope
    /// stops pulling, and ten seconds later the machine is on the same metre of
    /// ground.</summary>
    private static int park(World world, string name, float x, float z, float dir)
    {
        var (eid, speed, _, _) = Run(world, x, z, dir, full: true, seconds: 20f);
        var pos = world.ecs.fv("pos");
        var cmd = world.ecs.fv("vel_cmd");
        float t = 0f;
        while (t < 20f)                                   // let go, and let it stop
        {
            cmd.set(eid, 0, 0f);
            cmd.set(eid, 2, 0f);
            world.update(DT);
            sweep(world, eid);
            t += DT;
            if (Speed(world, eid) < 0.05f) break;
        }
        float x0 = pos.get(eid, 0), z0 = pos.get(eid, 2);
        float h0 = world.terrain.height_at(x0, z0);
        for (int k = 0; k < 300; k++)                     // ten seconds parked
        {
            world.update(DT);
            sweep(world, eid);
        }
        float drift = MathUtil.dist2d(x0, z0, pos.get(eid, 0), pos.get(eid, 2));
        float drop = world.terrain.height_at(pos.get(eid, 0), pos.get(eid, 2)) - h0;
        float s = world.terrain.slope_along(x, z, dir, 0f);
        float deg = MathF.Atan(MathF.Abs(s)) * 180f / MathF.PI;
        bool ok = drift < 0.5f && MathF.Abs(drop) < 0.5f;
        Console.WriteLine($"{name,-14} {deg,5:0}   stopped from {speed * 3.6f:0} km/h in {t:0.0} s, "
                          + $"then 10 s parked: moved {drift:0.00} m, height {drop:+0.00;-0.00} m"
                          + (ok ? "" : "   <-- it slid"));
        world.ecs.destroy(eid);
        return ok ? 0 : 1;
    }

    /// <summary>The hull's own speed, off the velocity it is actually making.</summary>
    private static float Speed(World world, int eid)
    {
        var v = world.ecs.fv("vel");
        return MathF.Sqrt(v.get(eid, 0) * v.get(eid, 0) + v.get(eid, 2) * v.get(eid, 2));
    }

    /// <summary>One run: a hull on a treadmill, a command held, and the speed it makes.</summary>
    private static (int eid, float speed, float t100, float tfull) Run(
        World world, float x, float z, float dir, bool full, float seconds)
    {
        float top = Defs.UNIT_DEFS[Defs.ETYPE_MECH].speed;
        var eid = world.spawn_unit(Defs.ETYPE_MECH, Defs.FACTION_BLUE, x, z, crew: false);
        var cmd = world.ecs.fv("vel_cmd");
        var vel = world.ecs.fv("vel");
        float t = 0f, speed = 0f, t100 = -1f, tfull = -1f;
        while (t < seconds)
        {
            cmd.set(eid, 0, dir * (full ? top : CRUISE));
            cmd.set(eid, 2, 0f);
            Pin(world, eid, x, z);
            speed = MathF.Sqrt(vel.get(eid, 0) * vel.get(eid, 0) + vel.get(eid, 2) * vel.get(eid, 2));
            if (t100 < 0f && speed >= 100f / 3.6f) t100 = t;
            if (tfull < 0f && full && speed >= top * 0.99f) tfull = t;
            t += DT;
        }
        return (eid, speed, t100 < 0f ? 0f : t100, tfull);
    }

    /// <summary>Put the hull back on its spot - position and the terrain's own height,
    /// the velocity it made kept - and step the world.</summary>
    private static void Pin(World world, int eid, float x, float z)
    {
        var pos = world.ecs.fv("pos");
        pos.set(eid, 0, x);
        pos.set(eid, 1, world.terrain.height_at(x, z));
        pos.set(eid, 2, z);
        world.update(DT);
        sweep(world, eid);
    }

    /// <summary>The range's own fixtures keep respawning; a hull standing next to the
    /// treadmill would be a second thing in every count.</summary>
    private static void sweep(World world, int eid)
    {
        foreach (int id in world.units())
            if (id != eid) world.ecs.destroy(id);
    }

    private static string Clock(float t) => t <= 0f ? "-" : $"{t:0.0}s";
}
