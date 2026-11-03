// Record what a top-attack round actually did, and what it was chasing.
//
// Tuning the dive is a question about a whole pursuit, not about the last frame:
// a proportional-navigation law is judged on how far the round is led astray by a
// hull that stops, turns or fakes a turn, and on where it is when the target does
// it.  None of that survives in the log - the per-weapon lines are DEBUG, they
// carry no track, and the file they land in is thrown away every run.  So the
// range records the simulation itself instead: one JSONL line a tick while a
// top-attack round is in the air, the round's own state plus the true state of
// every hull it could be after.
//
//     {"time":12.43,"tick":373,"missiles":[...],"units":[...],"events":[...]}
//
// The missile rows are the seeker's state, under the component names it lives in -
// `tgt_mem` is the stare *point* (the memory, deck height), `tgt_vel` the velocity
// that memory is drifting at and `target` the hull it holds, so `target == -1` is
// a blinded round flying its memory.  The unit rows are ground truth, which is the
// half the round cannot see.  With both, a flight can be replayed through
// `flight.step` off the recorded state and compared with what the real one did.
//
// **A tick with no top-attack round on it is not recorded.**  The dive is the only
// thing the numbers are for, so a 30 mm burst or a SACLOS on its way to the
// defence post leaves nothing behind, and the file stays a few hundred kB for an
// evening of flying instead of a few hundred MB.
//
// Enabled by `MECH_RECORD` (`dev.sh range` passes 1; `MECH_RECORD=0` turns it
// off), path `MECH_RECORD_PATH` (default `/tmp/mech-range-trace.jsonl`).
//
// Ported from `backend/server/modes/range/record.py`.

using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Mech.Server.Modes.Range;

/// <summary>A JSONL trace of the range's top-attack rounds and the hulls they chase.</summary>
public sealed class Record
{
    public const string DEFAULT_PATH = "/tmp/mech-range-trace.jsonl";
    public const string KIND = "top";               // the round this file exists for
    public static readonly string[] KEEP_EVENTS = { "boom", "seeker", "intercept" };
    private static readonly HashSet<string> OFF = new() { "", "0", "off", "no", "false" };
    private static readonly HashSet<string> ON = new() { "1", "on", "yes", "true" };
    // what travels with the round: enough to re-fly it, nothing about its hp pool
    public static readonly string[] ROUND_KEYS =
    {
        "kind", "loft", "apex", "turn_r", "pn_n", "pn_r", "pn_v",
        "mass", "dia", "cl_max", "cl_alpha", "cd0", "k_cl", "maxvel",
        "thrust", "burn", "ignite", "life", "range", "ed", "er",
    };

    private static readonly ILogger log = MechLog.get("mech.record");

    // Python hangs the open trace off `world.record`; the world has no such slot
    // here, so the traces are kept in a table keyed by the world they belong to.
    private static readonly ConditionalWeakTable<World, Record> _open = new();

    public string path;
    public HashSet<int> recent = new();    // rounds on the previous line
    private readonly StreamWriter fh;

    /// <summary>Open a trace when `MECH_RECORD` asks for one, else null.</summary>
    public static Record? start(World world)
    {
        string flag = (Environment.GetEnvironmentVariable("MECH_RECORD") ?? "").Trim();
        if (flag.Length == 0 || OFF.Contains(flag.ToLowerInvariant())) return null;
        string path = (Environment.GetEnvironmentVariable("MECH_RECORD_PATH") ?? "").Trim();
        if (!ON.Contains(flag.ToLowerInvariant()))
            path = path.Length > 0 ? path : flag;   // MECH_RECORD=/tmp/x.jsonl works too
        return new Record(path.Length > 0 ? path : DEFAULT_PATH, world);
    }

    /// <summary>`if getattr(world, "record", None) is None: world.record = start(world)`
    /// - a restart keeps writing to the same file.</summary>
    public static void attach(World world)
    {
        if (_open.TryGetValue(world, out _)) return;
        var rec = start(world);
        if (rec is not null) _open.Add(world, rec);
    }

    /// <summary>The mode's post system: sample the tick, if a trace is open.</summary>
    public static void run(World world, float dt)
    {
        if (_open.TryGetValue(world, out var rec)) rec.sample(world, dt);
    }

    public Record(string path, World world)
    {
        this.path = path;
        // Python opens the file "w" - a run starts a fresh trace, it does not grow
        // one run's file into the next - and line buffered, which `AutoFlush` is:
        // a tool tails this file while the range is flying.
        fh = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write,
                                            FileShare.ReadWrite))
        {
            AutoFlush = true,
        };
        _line(_header(world));
    }

    // -- writing ------------------------------------------------------
    private void _line(Dictionary<string, object?> row) =>
        fh.WriteLine(JsonSerializer.Serialize(row));

    private Dictionary<string, object?> _header(World world)
    {
        var wd = _top();                    // the round this file is about
        float dt = world.dt > 0f ? world.dt : 1f / 30f;
        log.LogInformation("range trace -> {path}", path);
        return new Dictionary<string, object?>
        {
            ["kind"] = "header",
            ["when"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            ["mode"] = world.mode,
            ["tick_hz"] = MathF.Round(1f / dt, 3),
            ["round"] = wd.key,
            ["params"] = _params(wd),
        };
    }

    /// <summary>The top-attack round's defs - the one thing this file is about.</summary>
    private static WeaponDef _top()
    {
        foreach (var wd in Defs.WEAPONS)
            if (wd.kind == KIND) return wd;
        throw new InvalidOperationException($"no weapon of kind '{KIND}'");
    }

    /// <summary>`{k: wd[k] for k in ROUND_KEYS if k in wd}` - every key of the
    /// Python table is a field here, so the filter is the table itself.</summary>
    private static Dictionary<string, object?> _params(WeaponDef wd) => new()
    {
        ["kind"] = wd.kind, ["loft"] = wd.loft, ["apex"] = wd.apex, ["turn_r"] = wd.turn_r,
        ["pn_n"] = wd.pn_n, ["pn_r"] = wd.pn_r, ["pn_v"] = wd.pn_v,
        ["mass"] = wd.mass, ["dia"] = wd.dia, ["cl_max"] = wd.cl_max,
        ["cl_alpha"] = wd.cl_alpha, ["cd0"] = wd.cd0, ["k_cl"] = wd.k_cl,
        ["maxvel"] = wd.maxvel, ["thrust"] = wd.thrust, ["burn"] = wd.burn,
        ["ignite"] = wd.ignite, ["life"] = wd.life, ["range"] = wd.range,
        ["ed"] = wd.ed, ["er"] = wd.er,
    };

    // -- sampling -----------------------------------------------------
    /// <summary>Python's `Record.run`; renamed because `run(world, dt)` is already
    /// the static system this mode registers.</summary>
    public void sample(World world, float dt)
    {
        var e = world.ecs;
        var wid = e.uv("proj_wid");
        var missiles = new List<Dictionary<string, object?>>();
        foreach (var pid in world.projectiles())
        {
            var w = Defs.WEAPONS[wid[pid]];
            if (w.kind != KIND) continue;       // not the round this file is about: no track
            missiles.Add(new Dictionary<string, object?>
            {
                ["id"] = pid,
                ["pos"] = _p(e, "pos", pid),
                ["vel"] = _p(e, "vel", pid),
                ["proj_age"] = MathF.Round(e.fv("proj_age")[pid], 3),
                ["proj_trav"] = MathF.Round(e.fv("proj_trav")[pid], 2),
                ["target"] = e.iv("target")[pid],
                ["tgt_mem"] = _p(e, "tgt_mem", pid),
                ["tgt_vel"] = _p(e, "tgt_vel", pid, 3),
                ["hp"] = MathF.Round(e.fv("hp")[pid], 2),
                ["proj_src"] = e.iv("proj_src")[pid],
                ["proj_pid"] = e.iv("proj_pid")[pid],
            });
        }
        // the tick's own events about these rounds: where one went off, a seeker
        // losing or retaking a lock, a round shot down.  Read against the rounds
        // written on the previous line as well, because a round that goes off is
        // destroyed inside `projectiles` - its burst is the one tick on which it has
        // no row of its own, and that burst is the impact point
        var watch = new HashSet<int>();
        foreach (var m in missiles) watch.Add((int)m["id"]!);
        watch.UnionWith(recent);
        var events = new List<Dictionary<string, object?>>();
        foreach (var ev in world.events)
        {
            if (!ev.TryGetValue("k", out var k) || k is not string kind) continue;
            if (Array.IndexOf(KEEP_EVENTS, kind) < 0) continue;
            int proj = ev.TryGetValue("proj", out var pv) ? _int_of(pv) : -1;
            if (watch.Contains(proj)) events.Add(ev);
        }
        if (missiles.Count > 0)
        {
            recent = new HashSet<int>();
            foreach (var m in missiles) recent.Add((int)m["id"]!);
        }
        else if (events.Count > 0) recent.Clear();   // that was the end of the round
        else return;                                // nothing diving: this tick is not worth a line

        // the hulls: everything a seeker could hold (a round that loses one lock may
        // take another), not just the one it is holding
        var units = new List<Dictionary<string, object?>>();
        var etype = e.uv("etype");
        var faction = e.uv("faction");
        foreach (var uid in world.lockable_units())
        {
            if (!e.is_alive(uid)) continue;
            units.Add(new Dictionary<string, object?>
            {
                ["id"] = uid,
                ["etype"] = (int)etype[uid],
                ["faction"] = (int)faction[uid],
                ["pos"] = _p(e, "pos", uid),
                ["vel"] = _p(e, "vel", uid),
                ["hp"] = MathF.Round(e.fv("hp")[uid], 1),
            });
        }
        _line(new Dictionary<string, object?>
        {
            ["time"] = MathF.Round(world.time, 3),
            ["tick"] = world.tick,
            ["missiles"] = missiles,
            ["units"] = units,
            ["events"] = events,
        });
    }

    public void close()
    {
        fh.Flush();
        fh.Dispose();
    }

    // -- helpers ------------------------------------------------------
    /// <summary>`[round(float(x), n) for x in a]`: a 3-vector off a component.</summary>
    private static float[] _p(ECS e, string name, int eid, int n = 2)
    {
        var v = e.fv(name);
        return new[]
        {
            MathF.Round(v.get(eid, 0), n),
            MathF.Round(v.get(eid, 1), n),
            MathF.Round(v.get(eid, 2), n),
        };
    }

    /// <summary>`int(ev["proj"])`: the event table carries boxed numbers.</summary>
    private static int _int_of(object? v) => v switch
    {
        int i => i,
        long l => (int)l,
        float f => (int)f,
        double d => (int)d,
        _ => -1,
    };
}
