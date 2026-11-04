// Headless harnesses: build a world, step it, print what happened.
//
// The Python backend has `tools/replay.py`, `tools/raid_trace.py`,
// `tools/intercept_check.py` and friends for this; in C# they are one console
// path on the same binary, because the whole simulation is in-process anyway:
//
//   ./dotnet.sh run -- --check battle --minutes 5 --players 4
//   ./dotnet.sh run -- --check range  --minutes 2
//
// The point is not a golden number - the RNG streams differ from Python's, and
// the porting rules allow that - but that a full battle runs without an
// exception, that the ECS bookkeeping survives (`ecs.validate` at the end), that
// the census matures the way AGENTS.md says it does (the field fills over the
// first minutes), and that the rates are sane.  Every ten seconds of simulated
// time it prints one line, so a hang or a blow-up (units without hp, a runaway
// spawn count, money going to infinity) is visible without a profiler.

using Mech.Server;
using Mech.Server.Systems;

namespace Mech.Server.Harness;

public static class Check
{
    /// <summary>The same scripted pilots `tools/profile_battle.py` flies, so the
    /// two backends are timed on the identical scenario: a slowly panning view,
    /// a walking hull, two firing windows and four smoke pulls.</summary>
    internal static void drive(World world, Player pl, int tick)
    {
        if (pl.mech < 0) return;
        pl.in_yaw = 0.4f + 0.02f * tick + (pl.faction == 1 ? 1.3f : 0f);
        pl.in_pitch = -0.06f;
        pl.in_move = (tick / 60) % 2 == 0 ? (1f, 0f) : (0f, 1f);
        pl.zoom = 4f;
        pl.zero_range = 800f;
        pl.fire = (tick >= 20 && tick < 400, tick >= 90 && tick < 260);
        if (tick is 30 or 200 or 420 or 700) pl.smoke_q = true;
    }

    /// <summary>Run a scripted battle as fast as the CPU allows.</summary>
    public static int battle(int minutes, int players, int seed = 11, bool perf = false)
    {
        var t0 = DateTime.UtcNow;
        var world = new World(seed, "battle");
        Console.WriteLine($"world built in {(DateTime.UtcNow - t0).TotalMilliseconds:0} ms: "
                          + $"{world.terrain.w}x{world.terrain.d} tiles, {world.towns.Count} towns, "
                          + $"{world.lanes.Count} lanes");
        var pilots = new List<Player>();
        for (int k = 0; k < players; k++) pilots.Add(world.add_player($"BOT{k}"));
        Console.WriteLine($"pipeline: {string.Join(", ", system_names(world))}");

        const float dt = 1f / 30f;
        int ticks = (int)(minutes * 60f / dt);
        var counts = new Dictionary<string, int>();
        var _view = new Netcode.SnapshotView();
        float next_report = 0f;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int next_minute = 60;
        double last_ms = 0.0;
        int last_tick = 0;
        // the loop also pays for the wire: one snapshot every 2 ticks, one meta
        // per pilot every 3 (`SNAP_EVERY`/`META_EVERY`), timed apart from the tick
        double wireMs = 0.0;
        var wsw = new System.Diagnostics.Stopwatch();
        for (int t = 0; t < ticks; t++)
        {
            foreach (var pl in pilots) drive(world, pl, t);
            try
            {
                world.update(dt);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"tick {world.tick} threw: {ex}");
                return 1;
            }
            if (world.tick % 2 == 0)
            {
                wsw.Restart();
                // the wire is per pilot now: one view, rebuilt every frame
                _view.at(world, pilots[0]);
                var contacts = world.tick % Netcode.CONTACT_EVERY == 0
                    ? Netcode.contacts_bytes(world) : null;
                Netcode.snapshot_bytes(world, _view, contacts);
                wsw.Stop();
                wireMs += wsw.Elapsed.TotalMilliseconds;
            }
            if (perf && world.tick % 3 == 0)
            {
                wsw.Restart();
                foreach (var pl in pilots) Netcode.meta_payload(world, pl);
                wsw.Stop();
                wireMs += wsw.Elapsed.TotalMilliseconds;
            }
            if (!perf) tally(world, counts);
            if (perf && (int)world.time >= next_minute)
            {
                next_minute += 60;
                double ms = sw.Elapsed.TotalMilliseconds - last_ms;
                int dticks = world.tick - last_tick;
                Console.WriteLine($"    min {next_minute / 60,2}:  {(ms / Math.Max(dticks, 1)),7:0.00} ms/tick  "
                                  + $"entities {world.all_units().Length,5} combat {world.combat_units().Length,4} "
                                  + $"proj {world.projectiles().Length,4}");
                last_ms = sw.Elapsed.TotalMilliseconds;
                last_tick = world.tick;
            }
            if (world.time >= next_report && !perf)
            {
                next_report += 30f;
                report(world);
            }
        }
        sw.Stop();
        double rate = ticks / sw.Elapsed.TotalSeconds;
        Console.WriteLine($"-- {minutes} min simulated in {sw.Elapsed.TotalSeconds:0.0} s "
                          + $"({rate:0} ticks/s, {1000.0 / rate:0.00} ms/tick incl. wire, "
                          + $"{wireMs / ticks:0.000} ms/tick of it the wire)");
        if (!perf) Console.WriteLine("events: " + tallyline(counts));
        world.ecs.validate();
        Console.WriteLine("ecs.validate() ok");
        census(world);
        return 0;
    }

    /// <summary>Very small probe: what a handful of AI hulls are actually doing.</summary>
    public static int diag(int minutes, int seed = 11)
    {
        var world = new World(seed, "battle");
        world.add_player("BOT0");
        const float dt = 1f / 30f;
        int ticks = (int)(minutes * 60f / dt);
        var e = world.ecs;
        for (int t = 0; t < ticks; t++)
        {
            world.update(dt);
            if (t % 300 != 299) continue;
            Console.WriteLine($"-- tick {world.tick} t={world.time:0}s");
            int shown = 0;
            foreach (var eid in world.combat_units())
            {
                if (shown++ >= 6) break;
                var a = e.arch_of(eid);
                if (a is null) continue;
                int row = e.row_of(eid);
                Console.WriteLine($"  eid={eid} et={a.get_u8("etype", row)} fac={a.get_u8("faction", row)} "
                                  + $"hp={a.get_f("hp", row):0} target={a.get_i("target", row)} "
                                  + $"trig={a.get_u8("trigger", row)} mag={a.i("mag")[row*2]}/{a.i("mag")[row*2+1]} "
                                  + $"yaw={a.get_f("yaw", row):0.00} syaw={a.get_f("sight_yaw", row):0.00} "
                                  + $"pitch={a.get_f("pitch", row):0.00} pos=({a.get_f("pos", row, 0):0},{a.get_f("pos", row, 2):0}) "
                                  + $"state={a.get_u8("ai_state", row)} spot_t={a.get_f("spot_t", row):0.0}");
            }
        }
        return 0;
    }

    /// <summary>Run the weapon range: a pilot mech, a missile at the defence post.</summary>
    public static int range(int minutes, int seed = 7)
    {
        var world = new World(seed, "range");
        Console.WriteLine($"range map: {world.terrain.w}x{world.terrain.d} tiles, "
                          + $"{world.terrain.bases.Count} bases, mode {world.mode}");
        var pl = world.add_player("PROBE");
        pl.money = 99999f;
        Console.WriteLine($"pilot mech {pl.mech}, pipeline: {string.Join(", ", system_names(world))}");

        const float dt = 1f / 30f;
        int ticks = (int)(minutes * 60f / dt);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int fired = 0;
        for (int t = 0; t < ticks; t++)
        {
            // one top-attack every 6 s, aimed at the defence post, to exercise
            // the seeker, the flight model, the fuse and the intercept
            if (t % 180 == 60 && pl.mech >= 0 && world.ecs.alive[pl.mech])
            {
                pl.fire = (false, true);
            }
            else
            {
                pl.fire = (false, false);
            }
            world.update(dt);
            fired += count_fx(world, "shot");
        }
        sw.Stop();
        Console.WriteLine($"-- {minutes} min simulated in {sw.Elapsed.TotalSeconds:0.0} s "
                          + $"({ticks / sw.Elapsed.TotalSeconds:0} ticks/s)");
        Console.WriteLine($"shots launched: {fired}, mine alive: {pl.mech}");
        world.ecs.validate();
        Console.WriteLine("ecs.validate() ok");
        census(world);
        return 0;
    }

    // fx events are the only record of what the battle *did* (the server clears
    // them every tick after broadcasting, so a headless run must do the same)
    private static void tally(World world, Dictionary<string, int> counts)
    {
        foreach (var ev in world.events)
        {
            if (!ev.TryGetValue("k", out var k) || k is not string kind) continue;
            counts[kind] = counts.TryGetValue(kind, out int c) ? c + 1 : 1;
        }
        world.events.Clear();
    }

    private static string tallyline(Dictionary<string, int> counts) =>
        string.Join(" ", counts.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value}"));

    private static int count_fx(World world, string kind)
    {
        int n = 0;
        foreach (var ev in world.events)
            if (ev.TryGetValue("k", out var k) && (string?)k == kind) n++;
        world.events.Clear();
        return n;
    }

    private static IEnumerable<string> system_names(World world)
    {
        var names = new List<string>();
        foreach (var s in world.systems) names.Add(s.Method.DeclaringType?.Name ?? "?");
        return names;
    }

    private static void report(World world)
    {
        int units = world.ecs.count("unit");
        int proj = world.projectiles().Length;
        int owned = 0, contested = 0;
        foreach (var t in world.towns)
        {
            if (t.owner >= 0) owned++;
            if (t.contested) contested++;
        }
        string money = string.Join(" ", world.players.Values.Select(p => $"{p.name}:${p.money:0}/{p.kills}k"));
        Console.WriteLine($"t={world.time:0}s tick={world.tick} wave={world.wave} "
                          + $"units={units} proj={proj} raiders={world.raiders().Length} "
                          + $"towns={owned}/{world.towns.Count} contested={contested} "
                          + $"winner={world.winner} | {money}");
    }

    private static void census(World world)
    {
        var byType = new SortedDictionary<int, int>();
        foreach (var eid in world.all_units())
        {
            int et = world.ecs.uv("etype")[eid];
            byType[et] = byType.TryGetValue(et, out int c) ? c + 1 : 1;
        }
        Console.WriteLine("census (etype: n): " + string.Join(", ", byType.Select(kv => $"{kv.Key}:{kv.Value}")));
        Console.WriteLine($"entities={world.ecs._high - 0} alive archetypes={world.ecs.archetypes.Count}");
    }
}
