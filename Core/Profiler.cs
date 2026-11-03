// White-box timing probes: stages inside the systems, enabled with MECH_PROBE=1.
//
// The tick-level picture (which *system* costs what) is cheap to get from the
// outside - wrap `world.systems` and time it.  What that cannot answer is where
// *inside* a system the milliseconds go, and an external sampler answers it
// badly: a profiler distorts everything it touches.  So the interesting stages
// call
//
//     long t0 = Profiler.P.start();
//     ... work ...
//     Profiler.P.end("projectiles.sphere_table", t0);
//
// and the accumulator sums them per name (count, total, worst, and a nested
// "exclusive" total so a stage inside a stage is not counted twice).
//
// With MECH_PROBE unset `Profiler.P` is the null probe: `start()` is 0 and
// `end` returns at once, so the cost in a normal run is two calls per probe site.
// A `if (Profiler.ON)` at every call site is deliberately not the pattern: it
// puts a branch in every hot loop.
//
//     MECH_PROBE=1 ./dotnet.sh run -- --port 8011 --range
//
// `Tools/Replay.cs` and `Tools/RangeCheck.cs` print and reset `report()` between
// runs.

using System.Diagnostics;

namespace Mech.Server;

/// <summary>A probe: `start()` reads the clock, `end(name, t0)` banks the
/// difference.  The null implementation makes both free.</summary>
public interface IProbe
{
    long start();
    void end(string name, long t0);
}

/// <summary>A probe that costs nothing: `t0 = instance.start()` is 0,
/// `end` returns.</summary>
public sealed class NullProbe : IProbe
{
    public static readonly NullProbe instance = new();

    public long start() => 0;

    public void end(string name, long t0) { }
}

/// <summary>The real probe: the accumulators behind `Profiler.report()`.</summary>
public sealed class TimedProbe : IProbe
{
    public long start() => Profiler.now();

    public void end(string name, long t0) => Profiler.end(name, t0);
}

public static class Profiler
{
    public static readonly bool ON;           // set from MECH_PROBE, below

    // name -> [calls, total_ns, worst_ns, excl_ns]
    private static readonly Dictionary<string, long[]> _stats = new();
    // start of the probes currently open
    private static readonly List<long> _stack = new();

    /// <summary>The clock, in nanoseconds (`time.perf_counter_ns`).  The cast
    /// through `double` is the one place a `double` is forced: the stopwatch
    /// counts in its own tick unit, not in nanoseconds.</summary>
    private static readonly double _ns_per_tick = 1_000_000_000.0 / Stopwatch.Frequency;

    public static long now() => (long)(Stopwatch.GetTimestamp() * _ns_per_tick);

    /// <summary>The door a system instruments itself with: `Profiler.P.start()`.</summary>
    public static readonly IProbe P;

    static Profiler()
    {
        string env = Environment.GetEnvironmentVariable("MECH_PROBE") ?? "";
        ON = env != "" && env != "0";
        P = ON ? new TimedProbe() : (IProbe)NullProbe.instance;
    }

    /// <summary>Start a probe; returns the clock reading (0 when probes are off).</summary>
    public static long start() => now();

    /// <summary>Close a probe.  Nested probes are fine: the time spent in a child
    /// is subtracted from the parent, so `excl` is what the parent itself spent.
    ///
    /// (The Python original carries the same docstring over a body that adds the
    /// whole duration to `excl` as well - the nesting arithmetic was never
    /// written.  Ported as it stands; `report()` does not print the column.)</summary>
    public static void end(string name, long t0)
    {
        if (t0 == 0) return;
        long d = now() - t0;
        if (!_stats.TryGetValue(name, out var row))
        {
            row = new long[4];
            _stats[name] = row;
        }
        row[0] += 1;
        row[1] += d;
        if (d > row[2]) row[2] = d;
        row[3] += d;
    }

    /// <summary>The accumulator as rows, biggest first, in per-tick units.</summary>
    public static List<Dictionary<string, object>> report(bool sort = true)
    {
        var rows = new List<Dictionary<string, object>>(_stats.Count);
        foreach (var (name, v) in _stats)
        {
            rows.Add(new Dictionary<string, object>
            {
                ["name"] = name,
                ["calls"] = v[0],
                ["total_s"] = v[1] / 1e9f,
                ["us_per_call"] = v[0] > 0 ? v[1] / 1000f / v[0] : 0f,
                ["worst_ms"] = v[2] / 1e6f,
            });
        }
        if (sort) rows.Sort((a, b) => ((float)b["total_s"]).CompareTo((float)a["total_s"]));
        return rows;
    }

    public static void reset()
    {
        _stats.Clear();
        _stack.Clear();
    }

    // ----------------------------------------------------------------------
    // counters: things that are not time but explain it (how many rounds in the
    // air, how many hulls, how many ray walks).  Cheap enough to leave in.
    // ----------------------------------------------------------------------
    private static readonly Dictionary<string, float> _counts = new();

    public static void count(string name, float v = 1f)
    {
        _counts.TryGetValue(name, out float cur);
        _counts[name] = cur + v;
    }

    public static SortedDictionary<string, float> counter_report()
    {
        var outv = new SortedDictionary<string, float>(StringComparer.Ordinal);
        foreach (var (k, v) in _counts) outv[k] = v;
        return outv;
    }

    public static void counters_reset() => _counts.Clear();
}
