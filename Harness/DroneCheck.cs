// `--check drone`: the loitering munition, row by row.
//
// Five readings, each one a question the design page answers by geometry
// (`docs/plans/drone.md`): the airframe's numbers at its cruise, what the optics can
// find and how long they take to find it, the height it holds over the terrain, the
// circle it settles into, and the dive it finishes with.
//
// Every row builds its own range world with the range's shooters taken out - the
// dummies do not need to see the round and the defence post would shoot at it - and
// then flies the munition by hand: one `spawn_round`, `world.update` until the row
// has its answer.  What is printed is the state of *that* round, so a row can say
// "the lock came at 380 m, 6.2 s in" without anything else in the world taking part.
//
//   ./dotnet.sh run -- --check drone

using Mech.Server;
using Mech.Server.Systems;

namespace Mech.Server.Harness;

public static class DroneCheck
{
    private const float DT = 1f / 30f;
    private static readonly int WID = Defs.WID_LOITER;
    private static readonly WeaponDef WD = Defs.WEAPONS[WID];

    public static int run()
    {
        Console.WriteLine("\n== the airframe at its cruise ==");
        _cruise();
        Console.WriteLine("\n== the search: distance and time to the first lock ==");
        _scan();
        Console.WriteLine("\n== the height band over 2 km ==");
        _transit();
        Console.WriteLine("\n== the circle, five minutes of it ==");
        _loiter();
        Console.WriteLine("\n== the dive ==");
        _dive();
        Console.WriteLine("\n== the trigger: with a ring and without one ==");
        _trigger();
        return 0;
    }

    /// <summary>The search, three hull types at three lateral offsets, six times each.
    ///
    /// One pass over one hull is a lottery: the cone is 8 deg wide and the axis a
    /// given bearing is in it for `8 / 40` s, twice every four seconds, while the
    /// ring is over that hull for a second and a half to three.  What the row reports
    /// is therefore the rate, and the distance and time of the passes that did find
    /// it.  A hull that is never found is a hull the optics cannot recognise, which is
    /// what the 1 %-of-field-of-view rule is for.
    /// </summary>
    private static void _scan()
    {
        const int PHASES = 6;
        Console.WriteLine($"{PHASES} passes per row, 900 m of straight flight over the hull");
        Console.WriteLine("target       offset m   found   lock at m   lock at s");
        foreach (var (name, etype) in new[]
                 {
                     ("mech", Defs.ETYPE_MECH), ("apc", Defs.ETYPE_APC),
                     ("rifleman", Defs.ETYPE_INF_RIFLE),
                 })
        {
            foreach (float off in new[] { 0f, 200f, -200f })
            {
                int found = 0;
                var dists = new List<float>();
                var times = new List<float>();
                for (int k = 0; k < PHASES; k++)
                {
                    var world = _range();
                    float hx = 2400f, hz = 900f;
                    // The target stands 900 m down the line of flight, and each pass
                    // puts it 20 m further out: the sweep is a function of the round's
                    // age, so moving the window the target is inside of moves the phase
                    // the optics meet it at.  Twenty metres is half a second of flight
                    // and the sweep crosses a bearing every two, so six passes sample
                    // the whole cycle.
                    world.spawn_unit(etype, Defs.FACTION_BLUE, hx + 900f + k * 20f,
                                     hz + off, crew: false);
                    // the munition flies east at its cruise, over the target, and its
                    // circle is 4 km further on: nothing but the search decides when it
                    // sees anything
                    var p = new Vec3(hx, world.terrain.height_at(hx, hz) + Defs.DRONE_AGL, hz);
                    int round = world.spawn_round(-1, Defs.FACTION_RED, WID, Defs.PROJ_DRONE,
                                                  p, new Vec3(Defs.DRONE_CRUISE, 0f, 0f),
                                                  WD.life, -1,
                                                  orbit: new Vec3(hx + 4000f, 0f, hz));
                    var (dist, secs) = _until_target(world, round, 90f);
                    if (secs < 0f) continue;
                    found++;
                    dists.Add(dist);
                    times.Add(secs);
                }
                Console.WriteLine($"{name,-12} {off,8:0}   {found,2}/{PHASES}   "
                                  + $"{_median(dists),9:0}   {_median(times),9:0.0}");
            }
        }
    }

    /// <summary>The transit row: 2 km of straight flight over whatever the map has,
    /// with the height it keeps.</summary>
    private static void _transit()
    {
        var world = _range();
        float hx = 600f, hz = 2600f;
        var p = new Vec3(hx, world.terrain.height_at(hx, hz) + Defs.DRONE_AGL, hz);
        int round = world.spawn_round(-1, Defs.FACTION_RED, WID, Defs.PROJ_DRONE, p,
                                      new Vec3(Defs.DRONE_CRUISE, 0f, 0f), WD.life, -1,
                                      orbit: new Vec3(hx + 4000f, 0f, hz));
        var agl = _fly(world, round, 60f, out float speed_min, out float speed_max);
        _report_band("transit", agl, speed_min, speed_max);
    }

    /// <summary>The circle: fired unlocked at a point 400 m off, so it arrives, turns
    /// onto the circle and stays there for the rest of the battery.</summary>
    private static void _loiter()
    {
        var world = _range();
        float hx = 2000f, hz = 2000f;
        var p = new Vec3(hx, world.terrain.height_at(hx, hz) + Defs.DRONE_AGL, hz);
        var orbit = new Vec3(hx + 400f, 0f, hz);
        int round = world.spawn_round(-1, Defs.FACTION_RED, WID, Defs.PROJ_DRONE, p,
                                      new Vec3(Defs.DRONE_CRUISE, 0f, 0f), WD.life, -1,
                                      orbit: orbit);
        float entered = -1f;
        float turned = 0f, prev_yaw = 0f;
        bool steering = false;
        var agl = new List<float>();
        var speeds = new List<float>();
        for (int t = 0; (float)t * DT < 300f; t++)
        {
            var (pos, vel) = _state(world.ecs, round);
            if (t > 0 && vel.len > 1f)
            {
                float yaw = MathF.Atan2(vel.z, vel.x);
                if (steering) turned += MathF.Abs(Wrap(yaw - prev_yaw));
                prev_yaw = yaw;
                steering = true;
            }
            world.update(DT);
            if (!world.ecs.is_alive(round)) break;
            var (p1, v1) = _state(world.ecs, round);
            if (world.ecs.iv("target")[round] >= 0) break;   // a lock ends the circle
            if (entered < 0f
                && MathUtil.dist2d(p1.x, p1.z, orbit.x, orbit.z) <= Defs.DRONE_ORBIT_R)
                entered = (t + 1) * DT;
            agl.Add(p1.y - world.terrain.height_at(p1.x, p1.z));
            speeds.Add(v1.len);
        }
        float laps = turned / (2f * MathF.PI);
        Console.WriteLine($"entered the circle at {entered,5:0.0} s, "
                          + $"turned {laps:0.00} laps");
        // the settled part: one lap to arrive and turn onto the ring, the rest of the
        // battery on it
        int settled = Math.Min(agl.Count - 1, Math.Max(0, (int)(40f / DT)));
        int n = agl.Count - settled;
        _report_band("loiter", agl.GetRange(settled, n),
                     Min(speeds, settled), Max(speeds, settled));
    }

    /// <summary>The dive: a hull under the nose, a lock already carried, and what the
    /// round arrives with.</summary>
    private static void _dive()
    {
        var world = _range();
        float hx = 2400f, hz = 900f;
        float gx = world.terrain.height_at(hx, hz);
        var p = new Vec3(hx, gx + Defs.DRONE_AGL, hz);
        int target = world.spawn_unit(Defs.ETYPE_APC, Defs.FACTION_BLUE, hx + 500f, hz,
                                      crew: false);
        var deck = Seeker.roof(world, target);
        int round = world.spawn_round(-1, Defs.FACTION_RED, WID, Defs.PROJ_DRONE, p,
                                      new Vec3(Defs.DRONE_CRUISE, 0f, 0f), WD.life,
                                      target, mem: deck, memVel: Vec3.zero,
                                      orbit: new Vec3(deck.x, 0f, deck.z));
        float secs = -1f, speed = 0f, angle = 0f;
        for (int t = 0; (float)t * DT < 60f; t++)
        {
            var (p0, v0) = _state(world.ecs, round);
            world.update(DT);
            if (!world.ecs.is_alive(round))
            {
                secs = t * DT;
                speed = v0.len;
                // the descent angle: the velocity's own
                angle = MathF.Atan2(-v0.y, MathF.Sqrt(v0.x * v0.x + v0.z * v0.z))
                        * 180f / MathF.PI;
                break;
            }
            if (world.ecs.iv("target")[round] < 0) break;     // lost the lock
        }
        Console.WriteLine($"dive from {Defs.DRONE_AGL:0} m: {secs,5:0.0} s at "
                          + $"{speed:0.0} m/s down a {angle:0} deg line, "
                          + $"target hp {(world.ecs.is_alive(target) ? world.ecs.fv("hp")[target] : 0f):0}");
    }

    /// <summary>The two ways a pilot lets one go: with the ring on a hull, and with
    /// nothing locked at all.  `@lock = 2` is the middle of the three states - the
    /// sight still answers a lock, and the trigger does not wait for one - so both
    /// pulls have to put a round in the air, and what the round carries has to differ
    /// exactly as the two missions do.</summary>
    private static void _trigger()
    {
        foreach (bool withLock in new[] { true, false })
        {
            var world = new World(7, "range");
            var e = world.ecs;
            var pl = world.add_player("MUNITION");
            pl.loadout = new[] { Defs.WID_AC30, WID };
            int row = e.row_of(pl.mech);
            var hull = e.arch_of(pl.mech)!;
            hull.u8("weapon")[row * 2 + 1] = (byte)WID;
            hull.i("mag")[row * 2 + 1] = WD.mag;

            var mech = e.fv("pos").get3(pl.mech);
            int tid = world.spawn_unit(Defs.ETYPE_APC, 1 - pl.faction,
                                       mech.x + 900f, mech.z, crew: false);
            var tgt = e.fv("pos");
            int round = -1;
            for (int t = 0; t < 60 * 6 && round < 0; t++)
            {
                var t0 = tgt.get3(tid);
                var eye = MathUtil.sight_eye(e.fv("pos").get3(pl.mech),
                                             e.fv("yaw")[pl.mech]);
                float dx = t0.x - eye.x, dz = t0.z - eye.z, dy = t0.y + 2f - eye.y;
                // the ring is the pilot's (`Systems/Locking.cs`), and he holds it on
                // the hull for a second and a half before he pulls.  Without one he
                // aims clear of the hull - a ring on it would be a lock whatever the
                // pilot did with `lock_target`, because the cross claims what it is on
                pl.in_yaw = MathF.Atan2(dz, dx) + (withLock ? 0f : 0.8f);
                pl.in_pitch = MathF.Atan2(dy, MathF.Sqrt(dx * dx + dz * dz)) * 0.5f;
                pl.lock_target = withLock ? tid : -1;
                pl.fire = (false, t > 45);
                world.update(DT);
                if (world.projectiles().Length > 0) round = world.projectiles()[^1];
            }
            if (round < 0)
            {
                Console.WriteLine($"{(withLock ? "locked" : "unlocked"),-9} no round");
                continue;
            }
            var o = e.fv("orbit").get3(round);
            var rp = e.fv("pos").get3(round);
            int rtgt = e.iv("target")[round];
            float toHull = MathUtil.dist2d(o.x, o.z, tgt.get3(tid).x, tgt.get3(tid).z);
            float toReach = MathUtil.dist2d(o.x, o.z, rp.x, rp.z);
            Console.WriteLine($"{(withLock ? "locked" : "unlocked"),-9} round out, "
                              + $"target {rtgt,3} ({(rtgt == tid ? "the hull" : "none")}), "
                              + $"circle {toReach,5:0} m out, {toHull,5:0} m from the hull");
        }
    }

    /// <summary>The airframe's numbers where the design page derives them: level
    /// flight at the cruise needs this CL, this CD and this much thrust.</summary>
    private static void _cruise()
    {
        float v = Defs.DRONE_CRUISE;
        float q = 0.5f * Flight.RHO * v * v;
        float s = Flight.area(WD);
        float cl = WD.mass * Flight.GRAVITY / (q * s);
        float cd = WD.cd0 + WD.k_cl * cl * cl;
        float need = q * s * cd / WD.mass;
        float lift_max = Flights.lift_accel(WD, q, WD.cl_max);
        Console.WriteLine($"cruise {v:0.0} m/s: CL {cl:0.00}, CD {cd:0.00}, "
                          + $"thrust {need:0.0} m/s2 of {WD.thrust:0.0} "
                          + $"(throttle {need / WD.thrust:0.00}), lift ceiling {lift_max:0.0} m/s2");
    }

    // ------------------------------------------------------------------ plumbing

    /// <summary>The range, with its two shooters taken out: nothing here may touch the
    /// round being measured.</summary>
    private static World _range()
    {
        var world = new World(7, "range");
        world.systems.Remove(Modes.Range.Targets.run);
        world.systems.Remove(Modes.Range.Interceptor.run);
        // and the range's own dummies go too: a munition searches for whatever is
        // there, and a row that left them standing would measure the range's layout
        // instead of its own
        foreach (int eid in world.units()) world.ecs.destroy(eid);
        return world;
    }

    /// <summary>Fly until the munition's own optics take something, and report the
    /// distance it was at and the seconds it took.</summary>
    private static (float dist, float secs) _until_target(World world, int round,
                                                          float limit)
    {
        for (int t = 0; (float)t * DT < limit; t++)
        {
            world.update(DT);
            if (!world.ecs.is_alive(round)) return (0f, -1f);
            int tid = world.ecs.iv("target")[round];
            if (tid < 0) continue;
            var (p, _) = _state(world.ecs, round);
            return (MathUtil.dist(p, Seeker.roof(world, tid)), (t + 1) * DT);
        }
        return (0f, -1f);
    }

    /// <summary>Fly for `seconds` and return the height above ground it held, with the
    /// speed range alongside.</summary>
    private static List<float> _fly(World world, int round, float seconds,
                                    out float speed_min, out float speed_max)
    {
        var agl = new List<float>();
        speed_min = float.MaxValue; speed_max = 0f;
        for (int t = 0; (float)t * DT < seconds; t++)
        {
            world.update(DT);
            if (!world.ecs.is_alive(round)) break;
            var (p, v) = _state(world.ecs, round);
            agl.Add(p.y - world.terrain.height_at(p.x, p.z));
            speed_min = MathF.Min(speed_min, v.len);
            speed_max = MathF.Max(speed_max, v.len);
        }
        return agl;
    }

    /// <summary>The height band, as a reader: the median, the extremes and how much
    /// of the flight was outside 150-250 m.</summary>
    private static void _report_band(string label, List<float> agl,
                                     float speed_min, float speed_max)
    {
        if (agl.Count == 0)
        {
            Console.WriteLine($"{label,-8} nothing recorded");
            return;
        }
        var sorted = new List<float>(agl);
        sorted.Sort();
        float med = sorted[sorted.Count / 2];
        int outside = 0;
        foreach (float h in agl)
            if (h < Defs.DRONE_AGL_MIN || h > Defs.DRONE_AGL_MAX) outside++;
        Console.WriteLine($"{label,-8} AGL min {sorted[0],5:0} m, median {med,5:0} m, "
                          + $"max {sorted[^1],5:0} m, outside the band "
                          + $"{100f * outside / agl.Count,4:0.0} %, "
                          + $"speed {speed_min:0.0}-{speed_max:0.0} m/s "
                          + $"({agl.Count * DT:0} s flown)");
    }

    private static (Vec3 pos, Vec3 vel) _state(ECS e, int round)
    {
        if (!e.is_alive(round)) return (Vec3.zero, Vec3.zero);
        var a = e.arch_of(round)!;
        int row = e.row_of(round);
        return (new Vec3(a.f("pos")[row * 3], a.f("pos")[row * 3 + 1], a.f("pos")[row * 3 + 2]),
                new Vec3(a.f("vel")[row * 3], a.f("vel")[row * 3 + 1],
                         a.f("vel")[row * 3 + 2]));
    }

    private static float Min(List<float> xs, int from)
        => xs.Count <= from ? 0f : xs.GetRange(from, xs.Count - from).Min();

    private static float Max(List<float> xs, int from)
        => xs.Count <= from ? 0f : xs.GetRange(from, xs.Count - from).Max();

    /// <summary>The middle reading of a list, 0 for an empty one.</summary>
    private static float _median(List<float> xs)
    {
        if (xs.Count == 0) return 0f;
        var copy = new List<float>(xs);
        copy.Sort();
        return copy[copy.Count / 2];
    }

    private static float Wrap(float a)
    {
        while (a > MathF.PI) a -= 2f * MathF.PI;
        while (a < -MathF.PI) a += 2f * MathF.PI;
        return a;
    }
}
