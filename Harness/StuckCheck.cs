// `--check stuck`: does a battle keep moving?
//
//   ./dotnet.sh run -- --check stuck --minutes 12
//
// Every other harness prices a mechanism; this one watches the war for the failure
// that no number in `--check battle` would show: a hull that was sent somewhere and
// never gets there.  The census reads the same at 8 minutes whether two crews are
// driving or nine of them are pushing at a wall, and the lane rows in `--check nav`
// only follow three hulls on their own.
//
// What it watches is *net travel*: each crewed hull's position is pinned to an
// anchor, the anchor moves when the hull has come 2 m off it, and the seconds since
// it last moved are what the clock counts.  A hull pushing at a house creeps and
// slips and never takes those 2 m; a hull walking a slow arc does.
//
// The clock alone is not a fault, because a crew is told to stand still all the
// time - a raider holding a town, a crew with its route done, the men inside a
// capture ring.  So a hull is *stuck* only when it is still **and** ordered
// somewhere: `nav_speed` above `CREEP` with a way point further than `ORDER_EPS`,
// which is the intent every walker and every car is really driving at
// (`Drive.target`).  Hulls never block each other - `Integrate.step` collides with
// terrain, buildings and trunks - so a hull that wants to go and does not is the
// ground or the route, never a queue.
//
// A pilot's machine is left out: it is a puppet with no crew, and it stands where
// its script left it.
//
// Two columns read the field: `still` is every hull that has not moved for
// `STILL_S`, ordered or not, and `stuck` is the ones among them that are being told
// to drive.  A fault is one of those for `STUCK_S` without a break; the accumulated
// clock is printed at the end, so a field that is merely resting reads differently
// from one that keeps jamming.

using Mech.Server.Systems;
using Mech.Server.Systems.AI;
using Mech.Server.Systems.Movement;

namespace Mech.Server.Harness;

public static class StuckCheck
{
    private const float DT = 1f / 30f;
    /// <summary>Metres of net travel that reset a hull's still clock: a hull
    /// pressing against a wall rocks around its own spot and never covers these.</summary>
    private const float MOVE_EPS = 2.0f;
    /// <summary>Metres: a way point this close is being arrived at, not driven to.</summary>
    private const float ORDER_EPS = 15f;
    /// <summary>m/s of `nav_speed` below which the crew is asking for a stop.</summary>
    private const float CREEP = 0.5f;
    /// <summary>Seconds still before a hull is worth printing.</summary>
    private const float STILL_S = 60f;
    /// <summary>Seconds still *while ordered* before it counts as a fault.</summary>
    private const float STUCK_S = 120f;
    /// <summary>Detail lines one minute may print.</summary>
    private const int SHOWN = 6;

    /// <summary>One hull's clock and the order it is under.</summary>
    private sealed class Track
    {
        public float ax, az;              // the anchor it has not left
        public float still;               // seconds since it last took MOVE_EPS
        public float worst;               // the longest still streak while ordered, ever
        public float jam;                 // seconds ordered and still, all of it
        public float crawl, crawl_worst;  // seconds ordered and making under a tenth of it
        public bool ordered;
        public float d;                   // metres to the point it is driving at
        public int route = -1, path = -1, path_end;
        public float want;                // |vel_cmd|, what the machine was told to make
        public float speed;
        public byte et;
        public float x, z;
        public float tx, tz;              // the point it is driving at
    }

    public static int run(int minutes, int seed = 11)
    {
        var world = new World(seed, "battle");
        world.add_player("BOT0");                       // a pilot in the field, as the battle runs
        var e = world.ecs;
        var crews = Components.Cat(MoveColumns.names, "arm_hp", AiTags.CREW);    // the mech
        var cars = Components.Cat(MoveColumns.names, "vehicle", AiTags.CREW);    // an APC
        var track = new Dictionary<int, Track>();
        var faults = new Dictionary<int, Track>();      // ever ordered and still for STUCK_S

        Console.WriteLine($"stuck check: {minutes} min of a battle on seed {seed}, "
                          + $"{STILL_S:0} s still to read, {STUCK_S:0} s while ordered is a fault");
        Console.WriteLine("t      crews  mech  apc  ordered  still  stuck  crawl   worst   walked");

        int ticks = (int)(minutes * 60f / DT);
        float walked = 0f;
        int mechs = 0, apcs = 0;
        for (int t = 0; t < ticks; t++)
        {
            world.update(DT);
            mechs = apcs = 0;
            foreach (var g in e.groups(crews)) Step(world, g, track, ref mechs, ref walked);
            foreach (var g in e.groups(cars)) Step(world, g, track, ref apcs, ref walked);
            if (t % 30 != 29) continue;
            foreach (var (eid, k) in track)
            {
                if (!k.ordered) continue;
                if (k.still >= STILL_S)
                {
                    k.jam += 1f;
                    if (k.still >= STUCK_S) faults[eid] = k;
                }
                k.crawl = k.want > 1f && k.speed < 0.1f * k.want ? k.crawl + 1f : 0f;
                if (k.crawl > k.crawl_worst) k.crawl_worst = k.crawl;
            }
            if (t % 1800 != 1799) continue;             // the line is a minute
            track = Prune(e, track);
            Minute(world, track, mechs, apcs, walked);
            walked = 0f;
        }

        world.ecs.validate();
        Console.WriteLine();
        if (faults.Count == 0)
            Console.WriteLine($"no crewed hull was ever ordered somewhere and still for "
                              + $"{STUCK_S:0} s without a break");
        else
            Console.WriteLine($"{faults.Count} crewed hull(s) ordered somewhere and still for "
                              + $"{STUCK_S:0} s or more:");
        foreach (var (eid, k) in faults)
            Console.WriteLine($"{Detail(world, eid, k)}, {Ground(world, k)}"
                              + $"{(k.still >= STUCK_S ? "   <-- stuck" : "")}");
        foreach (var (eid, k) in track.OrderByDescending(v => v.Value.jam).Take(5))
            if (k.jam > 0f && !faults.ContainsKey(eid))
                Console.WriteLine($"{Detail(world, eid, k)}, {Ground(world, k)}"
                                  + $"   ({k.jam:0} s of it, in bits)");
        Console.WriteLine($"\n{track.Count} crewed hulls alive at the end "
                          + $"(mech {mechs}, apc {apcs})");
        return faults.Count == 0 ? 0 : 1;
    }

    /// <summary>One tick of one archetype: the anchor, the clock, and the order.</summary>
    private static void Step(World world, Group g, Dictionary<int, Track> track,
                             ref int count, ref float walked)
    {
        var c = new MoveColumns(g);
        foreach (var r in c)
        {
            count += 1;
            int eid = r.eid;
            var p = r.pos;
            if (!track.TryGetValue(eid, out var k)) track[eid] = k = new Track { ax = p.x, az = p.z };
            k.et = r.etype;
            k.x = p.x;
            k.z = p.z;
            float moved = MathUtil.dist2d(k.ax, k.az, p.x, p.z);
            if (moved > MOVE_EPS)
            {
                k.ax = p.x;
                k.az = p.z;
                k.still = 0f;
                walked += moved;
            }
            else k.still += DT;
            var (ax, az) = Aim(world, r);
            k.tx = ax;
            k.tz = az;
            k.d = MathUtil.dist2d(p.x, p.z, ax, az);
            k.route = r.nav_route;
            k.path = Math.Max(r.path_i, 0);
            k.path_end = k.route >= 0 ? world.routes[k.route].pts.Count : 0;
            k.ordered = r.nav_speed > CREEP && k.d > ORDER_EPS;
            if (k.ordered && k.still > k.worst) k.worst = k.still;
            k.want = MathF.Sqrt(r.vel_cmd.x * r.vel_cmd.x + r.vel_cmd.z * r.vel_cmd.z);
            k.speed = MathF.Sqrt(r.vel.x * r.vel.x + r.vel.z * r.vel.z);
        }
    }

    /// <summary>The minute's line, and the hulls that are not moving in it.</summary>
    private static void Minute(World world, Dictionary<int, Track> track,
                               int mechs, int apcs, float walked)
    {
        int ordered = 0, still = 0, stuck = 0, crawl = 0;
        var worst = 0f;
        foreach (var k in track.Values)
        {
            if (k.ordered) ordered += 1;
            if (k.crawl >= 60f) crawl += 1;
            if (k.still < STILL_S) continue;
            still += 1;
            if (k.still > worst) worst = k.still;
            if (k.ordered) stuck += 1;
        }
        Console.WriteLine($"t={world.time,4:0}s {track.Count,6} {mechs,5} {apcs,4} {ordered,8} "
                          + $"{still,6} {stuck,6} {crawl,6} {worst,7:0} {walked / 1000f,7:0.0} km");
        foreach (var (eid, k) in track.OrderByDescending(v => v.Value.still).Take(SHOWN))
            if (k.still >= STILL_S) Console.WriteLine($"{Detail(world, eid, k)}, {Ground(world, k)}");
    }

    /// <summary>A hull that has died takes its clock with it - a respawn is a new
    /// machine with a new place, and its old anchor says nothing about it.</summary>
    private static Dictionary<int, Track> Prune(ECS ecs, Dictionary<int, Track> track)
    {
        var keep = new Dictionary<int, Track>();
        foreach (var (eid, k) in track)
            if (eid < ecs.alive.Length && ecs.alive[eid]) keep[eid] = k;
        return keep;
    }

    /// <summary>The point this hull is driving at, read without touching the driver's
    /// own cursor (`Drive.target` advances it).</summary>
    private static (float x, float z) Aim(World world, MoveRow r)
    {
        var route = Nav.get(world, r.nav_route);
        return route is null ? (r.nav_x, r.nav_z) : Nav.at(route, r.path_i);
    }

    /// <summary>What the ground says at a stuck hull: whether it is standing in a
    /// wall, which of the eight bearings it could step on, the slope the way it was
    /// going, the gradient its own chassis refuses, and how far the nearest base is.
    ///
    /// The bearing test is the machine's own (`Integrate.blocked` at the radius a
    /// hull's step is tested with), so "no way out" here means the same thing it
    /// means to `Integrate.step`.</summary>
    private static string Ground(World world, Track k)
    {
        var t = world.terrain;
        float rad = Defs.UNIT_DEFS[k.et].radius * 0.6f;
        float grad = Defs.max_gradient(k.et);
        var parts = new List<string>();
        if (t.blocked(k.x, k.z)) parts.Add("standing in a wall");
        // the same five-sample test `Integrate.step` refuses a step on: a hull whose
        // *own* footprint overlaps one can never take the first step anywhere
        if (Integrate.blocked(t, k.x, k.z, rad)) parts.Add("its own footprint overlaps a wall");
        int free = 0;
        for (int b = 0; b < 8; b++)
        {
            float a = b * MathF.PI / 4f;
            float nx = k.x + MathF.Cos(a) * 2f, nz = k.z + MathF.Sin(a) * 2f;
            if (!Integrate.blocked(t, nx, nz, rad)
                && !Integrate.too_steep(t, k.x, t.height_at(k.x, k.z), k.z, nx, nz, grad))
                free += 1;
        }
        parts.Add(free == 0 ? "no way out of the spot" : $"{free} of 8 bearings free");
        float near = float.MaxValue;
        foreach (var b in t.bases.Values)
        {
            float d = MathUtil.dist2d(k.x, k.z, b.x, b.z);
            if (d < near) near = d;
        }
        if (near < float.MaxValue) parts.Add($"base {near:0} m");
        float dx = k.tx - k.x, dz = k.tz - k.z;
        float d2 = MathF.Sqrt(dx * dx + dz * dz);
        if (d2 > 1e-3f)
            parts.Add($"slope {MathF.Atan(t.slope_along(k.x, k.z, dx / d2, dz / d2)) * 180f / MathF.PI:0} deg "
                      + $"(limit {MathF.Atan(grad) * 180f / MathF.PI:0})");
        return string.Join(", ", parts);
    }

    private static string Detail(World world, int eid, Track k)
        => $"  {(k.et == Defs.ETYPE_MECH ? "mech" : "apc"),-4} eid={eid,-6} "
           + $"at ({k.x:0}, {k.z:0}) still {k.still,4:0} s, want {k.want:0.0} m/s made "
           + $"{k.speed:0.0}, {k.d:0} m to "
           + (k.route >= 0 ? $"route {k.route} {k.path}/{k.path_end}" : "its point")
           + (k.ordered ? "" : " (no order)")
           + (k.crawl >= 60f ? $", crawling {k.crawl:0} s" : "");
}
