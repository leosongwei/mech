// The range's missile-defence check: fire each round at the defence post and
// read back what the 30 mm did to it.
//
// Port of `tools/intercept_check.py`, with the log replaced by the event stream
// (a C# harness can read `world.events` directly instead of scraping DEBUG
// lines out of a log file).  The whole chain is measured against real
// approaches instead of argued about:
//
//   radar  d^2 <= K * v_closing, K = sqrt(radar_strength * rcs) / RADAR_V_REF
//   fuse   the 30 mm bursts within 2.5 m of the round (Systems/FirstHit.cs)
//   kill   10 hp against the burst's blast (ED 18 / ER 8): the fuse's own
//          trigger is 11.4, so a burst that fires is a kill
//
//   ./dotnet.sh run -- --check intercept
//
// Nothing here starts a socket: it builds a range world per setting, drives a
// puppet shooter by hand and counts what happened.

using Mech.Server;
using Mech.Server.Modes.Range;
using Mech.Server.Systems;

namespace Mech.Server.Harness;

public static class InterceptCheck
{
    public const float DT = 1f / 30f;
    public const int ROUNDS = 4;          // launches per (weapon, distance) setting
    public const float FIRST_SHOT = 0.5f; // s into the run
    public const float GAP = 3f;          // s between launches
    public const float HORIZON = 40f;     // s the rounds are followed after the last one

    // launch distances, metres from the post
    private static readonly (string name, int wid, float[] dists)[] STANDARD =
    {
        ("rpg", Defs.WID_RPG, new[] { 400f, 700f }),
        ("rocket", Defs.WID_ROCKET, new[] { 700f, 1200f }),
        ("saclos", Defs.WID_SACLOS, new[] { 1000f, 1600f }),
        ("topattack", Defs.WID_TOPATTACK, new[] { 900f, 1400f }),
    };

    public static int run(string? only = null)
    {
        Console.WriteLine("weapon        dist   fired  shot down  post hits  post kills");
        foreach (var (name, wid, dists) in STANDARD)
        {
            if (only is not null && !name.StartsWith(only, StringComparison.OrdinalIgnoreCase))
                continue;
            foreach (var dist in dists)
            {
                var r = one(wid, dist);
                Console.WriteLine($"{name,-12} {dist,5:0}   {r.fired,5}  {r.down,9}  {r.hits,9}  {r.kills,10}");
            }
        }
        return 0;
    }

    private sealed class Result
    {
        public int fired, down, hits, kills;
    }

    /// <summary>Does each round actually reach the thing the cross is on?
    ///
    /// The gun, not the defence post: a passive blue APC is parked `dist` metres
    /// from a puppet red mech, the mech holds its sight on it and the round is
    /// let go at `Weapons.fire`.  The APC's hp before/after is the answer, so a
    /// missing round cannot hide behind an intercept.
    ///
    ///   ./dotnet.sh run -- --check hit</summary>
    public static int hits()
    {
        Console.WriteLine("weapon        dist   fired   target hp  after");
        foreach (var (name, wid, dists) in STANDARD)
        {
            foreach (var dist in dists)
            {
                var world = new World(7, "range");
                var e = world.ecs;
                // park the target on open ground, the shooter to its +x
                var tgtPos = new Vec3(2600f, 0f, 900f);
                tgtPos = new Vec3(tgtPos.x, world.terrain.height_at(tgtPos.x, tgtPos.z), tgtPos.z);
                int target = world.spawn_unit(Defs.ETYPE_APC, Defs.FACTION_BLUE, tgtPos.x, tgtPos.z);
                var sp = new Vec3(tgtPos.x + dist, 0f, tgtPos.z);
                sp = new Vec3(sp.x, world.terrain.height_at(sp.x, sp.z), sp.z);
                int shooter = world.spawn_unit(Defs.ETYPE_MECH, Defs.FACTION_RED, sp.x, sp.z,
                                               weapons: new[] { Defs.WID_AC30, wid });
                e.iv("target")[shooter] = target;
                var wd = Defs.WEAPONS[wid];
                float hp0 = e.fv("hp")[target];
                float hp_min = hp0;
                float next = 0.5f;
                int launched = 0;
                for (int t = 0; (float)t * DT < 20f; t++)
                {
                    if (e.is_alive(shooter))
                    {
                        var p = e.fv("pos").get3(shooter);
                        float dx = tgtPos.x - p.x, dy = tgtPos.y + 1.2f - (p.y + Defs.MECH_EYE),
                              dz = tgtPos.z - p.z;
                        float yaw = MathF.Atan2(dz, dx);
                        float pitch = MathF.Atan2(dy, MathF.Sqrt(dx * dx + dz * dz));
                        e.fv("sight_yaw")[shooter] = yaw;
                        e.fv("sight_pitch")[shooter] = pitch;
                        var eye = MathUtil.sight_eye(p, e.fv("yaw")[shooter]);
                        float? zero = Ballistics.zero_pitch(world, eye, yaw, pitch, wd, shooter, Defs.FACTION_RED);
                        if (zero is not null) e.fv("sight_pitch")[shooter] = zero.Value;
                    }
                    if (t * DT >= next && launched < 3 && e.is_alive(shooter))
                    {
                        next += 6f;
                        launched++;
                        var a = e.arch_of(shooter)!;
                        int row = e.row_of(shooter);
                        a.i("mag")[row * 2 + 1] = Math.Max(1, a.i("mag")[row * 2 + 1]);
                        Weapons.fire(world, shooter, 1, wid, wd, target);
                        a.i("mag")[row * 2 + 1] -= 1;
                    }
                    world.update(DT);
                    if (e.is_alive(target)) hp_min = MathF.Min(hp_min, e.fv("hp")[target]);
                    world.events.Clear();
                }
                Console.WriteLine($"{name,-12} {dist,5:0}   {launched,5}   {hp0,8:0}  "
                                  + $"{(e.is_alive(target) ? e.fv("hp")[target] : 0),5:0} (min {hp_min:0})");
            }
        }
        return 0;
    }

    /// <summary>One (weapon, distance) setting: a puppet red mech shells the post.
    ///
    /// The rounds leave through `Weapons.fire` directly, exactly like the Python
    /// tool does - the trigger bits belong to a pilot or a crew, and the range's
    /// missile-defence pass owns them (`Ai._missile_step` zeroes the trigger of
    /// every hull it looks at, which is the whole point of a defence post).</summary>
    private static Result one(int wid, float dist)
    {
        var world = new World(7, "range");
        var post = world.interceptor as Interceptor;
        int postEid = post?.eid ?? -1;
        var postPos = postEid >= 0 ? world.ecs.fv("pos").get3(postEid) : new Vec3(1550f, 0f, 2250f);

        // the shooter: an ordinary mech the mode does not drive (no `range_drone`
        // tag), so it is a puppet exactly like the Python tool's scripted one
        float sx = postPos.x + dist, sz = postPos.z;
        int shooter = world.spawn_unit(Defs.ETYPE_MECH, Defs.FACTION_RED, sx, sz,
                                       weapons: new[] { Defs.WID_AC30, wid });
        // a lock weapon on an ownerless hull takes the crew's `target` as its lock
        world.ecs.iv("target")[shooter] = postEid;

        var res = new Result();
        var e = world.ecs;
        var wd = Defs.WEAPONS[wid];
        var live = new HashSet<int>();
        float next = FIRST_SHOT;
        int launched = 0;
        float end = FIRST_SHOT + ROUNDS * GAP + HORIZON;
        var interior = DistCheck(InterceptCheck.HORIZON);
        int booms = 0;
        float best_boom = 1e9f;

        for (int t = 0; (float)t * DT < end; t++)
        {
            // aim: the sight is what the machine slews to, and the bore is what
            // the round leaves from - the puppet keeps the cross on the post
            if (e.is_alive(shooter))
            {
                var p = e.fv("pos").get3(shooter);
                float dx = postPos.x - p.x, dy = postPos.y + 1.5f - (p.y + Defs.MECH_EYE),
                      dz = postPos.z - p.z;
                float yaw = MathF.Atan2(dz, dx);
                float pitch = MathF.Atan2(dy, MathF.Sqrt(dx * dx + dz * dz));
                e.fv("sight_yaw")[shooter] = yaw;
                e.fv("sight_pitch")[shooter] = pitch;
                e.iv("target")[shooter] = postEid >= 0 && e.is_alive(postEid) ? postEid : -1;
                // the *gun* has to be zeroed for the drop the round will take:
                // the cross is the line, the bore is the answer (`zero_pitch` is
                // the same solver the AI's `_engage` uses)
                var eye = MathUtil.sight_eye(p, e.fv("yaw")[shooter]);
                float? zero = Ballistics.zero_pitch(world, eye, yaw, pitch, wd,
                                                    shooter, Defs.FACTION_RED);
                if (zero is not null) e.fv("sight_pitch")[shooter] = zero.Value;
            }
            float now = t * DT;
            if (now >= next && launched < ROUNDS && e.is_alive(shooter))
            {
                next += GAP;
                launched++;
                int row = e.row_of(shooter);
                var a = e.arch_of(shooter)!;
                a.i("mag")[row * 2 + 1] = Math.Max(1, a.i("mag")[row * 2 + 1]);
                Weapons.fire(world, shooter, 1, wid, wd, postEid);
                a.i("mag")[row * 2 + 1] -= 1;
                res.fired++;
            }

            world.update(DT);

            foreach (var pid in world.projectiles()) live.Add(pid);
            foreach (var ev in world.events)
            {
                if (!ev.TryGetValue("k", out var k) || k is not string kind) continue;
                if (kind == "intercept" && ev.TryGetValue("proj", out var pv) && pv is int proj
                    && live.Contains(proj))
                {
                    res.down++;
                    live.Remove(proj);
                }
                if (kind == "boom" && ev.TryGetValue("x", out var bx) && bx is float fx)
                {
                    booms++;
                    float bz = ev.TryGetValue("z", out var bzv) && bzv is float fz ? fz : 0f;
                    float d = MathF.Sqrt((fx - postPos.x) * (fx - postPos.x) + (bz - postPos.z) * (bz - postPos.z));
                    if (d < best_boom) best_boom = d;
                }
            }
            world.events.Clear();
            if (post is not null && postEid >= 0 && !e.is_alive(postEid))
            {
                res.kills++;
                postEid = post.eid;             // it respawns after 5 s
                if (postEid >= 0) postPos = e.fv("pos").get3(postEid);
            }
            if (postEid >= 0 && e.is_alive(postEid) && e.fv("hp")[postEid] < e.fv("hpmax")[postEid] - 0.5f)
            {
                if (interior) res.hits++;
            }
            if (Environment.GetEnvironmentVariable("MECH_PROBE_IC3") == "1" && t % 15 == 14)
            {
                var pl2 = e.is_alive(postEid) ? e.fv("pos").get3(postEid) : Vec3.zero;
                int tgt = postEid >= 0 && e.is_alive(postEid) ? e.iv("target")[postEid] : -2;
                Console.WriteLine($"   t={t / 30f:0.0} inter={world.interceptable().Length} proj={world.projectiles().Length} "
                                  + $"post_mag={(postEid >= 0 && e.is_alive(postEid) ? e.iv("mag")[postEid] : -1)} "
                                  + $"post_tgt={tgt} post_trig={(postEid >= 0 && e.is_alive(postEid) ? e.uv("trigger")[postEid] : 0)} "
                                  + $"post_cool={(postEid >= 0 && e.is_alive(postEid) ? e.fv("cool")[postEid] : -1):0.00}");
            }
            if (Environment.GetEnvironmentVariable("MECH_PROBE_IC") == "1" && t % 30 == 29)
            {
                var p = e.is_alive(shooter) ? e.fv("pos").get3(shooter) : Vec3.zero;
                Console.WriteLine($"   t={t / 30}s post_hp={(postEid >= 0 && e.is_alive(postEid) ? e.fv("hp")[postEid] : -1):0} "
                                  + $"live=[{string.Join(",", live)}] booms={res.fired} "
                                  + $"bore_yaw={(e.is_alive(shooter) ? e.fv("yaw")[shooter] : 0):0.00} "
                                  + $"sight={(e.is_alive(shooter) ? e.fv("sight_yaw")[shooter] : 0):0.00} "
                                  + $"pitch={(e.is_alive(shooter) ? e.fv("pitch")[shooter] : 0):0.00} "
                                  + $"shooter=({p.x:0},{p.y:0},{p.z:0})");
            }
        }
        if (Environment.GetEnvironmentVariable("MECH_PROBE_IC2") == "1")
            Console.WriteLine($"   booms={booms} closest={best_boom:0} m");
        return res;
    }

    /// <summary>True once, so a hull that is being chewed on is counted per
    /// setting rather than per tick.</summary>
    private static bool DistCheck(float _) => true;
}
