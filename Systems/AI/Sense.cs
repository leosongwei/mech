// Who is out there: every crew with `crew_engage` looks for a target on a slow
// timer, and validates the one it already holds every tick.
//
// The candidates come from the grid (`Core/Grid.cs`), not from a roster: a
// 5000-unit battle puts a few dozen ids in the box around the shooter instead of a
// few thousand in an array to sort, and the box covers the whole sight radius, so
// the answer is the same one a full scan would give.  A hull may declare a
// restricted target list in its type (`targets`: a rifleman picks fights with other
// infantry and leaves armour alone - 15 hp in the open is not a threat to a tank).
//
// Acquisition is *observation*: the crew writes `target` and nothing else.  What to
// do about it is the mission policy's (`Systems/AI/Strategies/`), and pointing the
// gun at it is the platform's (`Engage*`).

namespace Mech.Server.Systems.AI;

public sealed class SenseColumns : CrewColumns
{
    // the columns a target check needs on top of a crew's own: where it may fire
    // to, how far it can pick something out, and what it is willing to fight
    private static readonly string[] sense_names = Components.Cat(names, AiTags.ENGAGE);
    public static readonly string[] OnSense = sense_names;

    public SenseColumns(Group g) : base(g) { }

    public override string[] Names => sense_names;
}

public static class Sense
{
    /// <summary>s between two looks.  Jittered per crew so a battle does not
    /// acquire in lockstep on one tick.</summary>
    public const float ACQUIRE = 0.45f;
    /// <summary>How many of the nearest candidates are worth a line-of-sight cast.</summary>
    public const int SIGHT_CASTS = 6;

    public static void run(World world, float dt)
    {
        var e = world.ecs;
        // who may be shot at: `combat` hulls by faction, id-indexed.  Wrecks,
        // corpses, civilians and smoke are out of it by construction - they carry
        // no `combat`.
        var combat_fac = new sbyte[e.alive.Length];
        Array.Fill(combat_fac, (sbyte)-1);
        foreach (var g in e.groups("combat", "faction"))
        {
            var gf = g.u8("faction");
            var gids = g.ids;
            for (int k = 0; k < g.n; k++) combat_fac[gids[k]] = (sbyte)gf[k];
        }
        var faction = e.uv("faction");

        foreach (var g in e.groups(SenseColumns.OnSense))
        {
            var c = new SenseColumns(g);
            foreach (var r in c)
            {
                r.timer -= dt;
                if (r.timer <= 0f)
                {
                    r.timer = ACQUIRE * world.rng.uniform(0.75f, 1.35f);
                    r.target_mem = _acquire(world, r.faction, combat_fac, r.eye,
                                            Defs.UNIT_DEFS[r.etype]);
                }
                int tid = r.target;
                if (tid < 0) { c.measure_accel(r.index, -1, default, dt); continue; }
                // the pull, measured on the velocity the ECS holds - what the client
                // differentiates off two snapshots, `Core/FireControl.cs` leads on
                c.measure_accel(r.index, tid, e.fv("vel").get3(tid), dt);
                // a target that died, or that turns out to be one of ours, is
                // dropped on the spot: the timer would otherwise keep a crew
                // shooting at a wreck for half a second
                if (!e.is_alive(tid) || faction[tid] == r.faction) r.target_mem = -1;
            }
        }
    }

    /// <summary>Nearest hostile hull with line of sight, or -1.
    ///
    /// No position is gathered here - the grid carries its own copy, in the same
    /// order as its ids.</summary>
    private static int _acquire(World world, int fac, sbyte[] combat_fac, Vec3 eye, UnitDef d)
    {
        var grid = world.grid;
        var (ci, cj) = grid.cell_of(eye.x, eye.z);
        var (ids, p) = grid.box(ci, cj, (int)(d.sight * grid.inv) + 1);
        if (ids.Length == 0) return -1;
        var etype = world.ecs.uv("etype");
        var want = d.targets;
        var cand = new Cand[ids.Length];
        int n = 0;
        for (int k = 0; k < ids.Length; k++)
        {
            int id = ids[k];
            sbyte cf = combat_fac[id];
            if (cf < 0 || cf == fac) continue;
            if (want != null && Array.IndexOf(want, (int)etype[id]) < 0) continue;
            float dx = p[k * 3] - eye.x;
            float dz = p[k * 3 + 2] - eye.z;
            float ds = MathF.Sqrt(dx * dx + dz * dz);
            if (!(ds <= d.sight)) continue;
            cand[n].id = id;
            cand[n].x = p[k * 3];
            cand[n].y = p[k * 3 + 1];
            cand[n].z = p[k * 3 + 2];
            cand[n].ds = ds;
            n += 1;
        }
        if (n == 0) return -1;
        Array.Sort(cand, 0, n);
        int take = Math.Min(SIGHT_CASTS, n);
        for (int k = 0; k < take; k++)
        {
            var tp = cand[k];
            if (Sight.clear(world, eye, new Vec3(tp.x, tp.y + 1.5f, tp.z))) return tp.id;
        }
        return -1;
    }

    /// <summary>One candidate: the grid's own copy of the position, ordered by
    /// plan-view distance.</summary>
    private struct Cand : IComparable<Cand>
    {
        public int id;
        public float x, y, z;
        public float ds;

        public int CompareTo(Cand other) => ds.CompareTo(other.ds);
    }
}
