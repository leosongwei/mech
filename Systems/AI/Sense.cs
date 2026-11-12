// Who is out there: every crew with `crew_engage` looks for a target on a slow
// timer, and what it *knows* about the one it holds is refreshed every tick - or
// stops being refreshed, which is the whole point.
//
// This is the crew's only eye on a hull, and the only writer of its belief
// (`ai_tgt`): while the line of sight is clear the belief is the truth, copied in
// every tick; the moment the sight is cut (smoke, a crest, a house) the copying
// stops and the point walks at the velocity it was last seen with, for `MEMORY`
// seconds, before the crew gives the hull up.  `Engage` solves against that belief
// and nothing else, so a screen of smoke is a screen for the guns too, and a crew
// leads a hull it has lost sight of the way a missile's seeker does
// (`Core/Seeker.cs`).
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

        foreach (var g in e.groups(SenseColumns.OnSense))
        {
            var c = new SenseColumns(g);
            foreach (var r in c)
            {
                var own = Defs.UNIT_DEFS[r.etype];
                r.timer -= dt;
                int tid = r.target;

                // ---- 1. the hull it is working: one look, every tick -----------
                if (tid >= 0)
                {
                    // `combat_fac` is this tick's answer to "is this still a hull that
                    // may be fought": an id recycled into a wreck, a corpse or a smoke
                    // puff answers -1, and a hull of our own side is dropped on the spot
                    // (a wreck the crew shot is the case the timer used to keep firing at)
                    if (combat_fac[tid] < 0 || combat_fac[tid] == r.faction)
                    {
                        tid = -1;
                        r.target_mem = -1;
                    }
                    else
                    {
                        var tpos = e.fv("pos").get3(tid);
                        if (_sees(world, r, tpos, Defs.UNIT_DEFS[e.uv("etype")[tid]], own))
                        {
                            // seen: the belief is the truth, and the pull is measured
                            var tvel = e.fv("vel").get3(tid);
                            r.believe(tpos, tvel, e.uv("etype")[tid]);
                            c.measure_accel(r.index, tid, tvel, dt);
                        }
                        else
                        {
                            // the sight is broken: the memory walks at what it was
                            // doing, the pull ages out (no fresh sample), and after
                            // `Crew.MEMORY` s the hull is a spot, not a target
                            r.walk_belief(dt);
                            if (r.belief_age > Crew.MEMORY) { tid = -1; r.target_mem = -1; }
                        }
                    }
                }

                // ---- 2. nothing in hand: the memory walks itself out ----------
                if (tid < 0)
                {
                    if (!r.has_belief) c.measure_accel(r.index, -1, default, dt);
                    else if (r.belief_age <= Crew.MEMORY) r.walk_belief(dt);
                    else r.forget();
                }

                // ---- 3. the scan clock: somebody new, if somebody is visible --
                if (r.timer <= 0f)
                {
                    r.timer = ACQUIRE * world.rng.uniform(0.75f, 1.35f);
                    int found = _acquire(world, r.faction, combat_fac, r.eye, own);
                    if (found >= 0 && found != r.target)
                    {
                        var tpos = e.fv("pos").get3(found);
                        var tvel = e.fv("vel").get3(found);
                        r.target_mem = found;
                        r.believe(tpos, tvel, e.uv("etype")[found]);
                        c.measure_accel(r.index, found, tvel, dt);
                    }
                }
            }
        }
    }

    /// <summary>Does this crew have the hull in hand: inside its engagement range and
    /// through the one line of sight (`Systems/Sight.cs` - terrain, canopies, smoke).
    ///
    /// The range is the same quantity the fire solution gates on
    /// (`Engage.ground_aim`): the eye to the hull's geometric centre, in three
    /// dimensions, so a hull on a ridge is neither "seen" at a range it cannot be
    /// shot at nor the other way round.  The *cast* is aimed at the target's own
    /// body, 60 % of its height up, so a hull looking at a rifleman looks at a man
    /// and not at a metre of air above him.</summary>
    private static bool _sees(World world, CrewRow r, Vec3 tpos, UnitDef td, UnitDef own)
    {
        float dx = tpos.x - r.eye.x, dz = tpos.z - r.eye.z;
        float dy = tpos.y + td.height * 0.5f - r.eye.y;
        if (dx * dx + dy * dy + dz * dz > (float)own.ai_range * own.ai_range) return false;
        return Sight.clear(world, r.eye, new Vec3(tpos.x, tpos.y + td.height * 0.6f, tpos.z));
    }

    /// <summary>Nearest hostile hull with line of sight, or -1.
    ///
    /// No position is gathered here - the grid carries its own copy, in the same
    /// order as its ids.  The distance is the one `_sees` measures: eye to the
    /// hull's geometric centre, so the scan radius and the engagement radius answer
    /// the same question.</summary>
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
            float dy = p[k * 3 + 1] + Defs.UNIT_DEFS[etype[id]].height * 0.5f - eye.y;
            float dz = p[k * 3 + 2] - eye.z;
            float ds = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
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
