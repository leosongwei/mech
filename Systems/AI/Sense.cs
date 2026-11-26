// Who is out there: every crew with `crew_engage` looks for a target on a slow
// This is the crew's only eye on a hull, and the only writer of its belief
// (`ai_tgt`): direct sight or a current faction datalink refreshes the belief with
// the truth every tick.  When both sources are absent the copying stops and the
// point walks at the velocity it was last known with, for `MEMORY` seconds, before
// the crew gives the hull up.  `Engage` solves against that belief and nothing else;
// physical propagation still belongs to `Sight.clear`, so a datalink contact never
// makes a laser, seeker or warning receiver see through cover.
//
// The candidates come from the grid (`Core/Grid.cs`), not from a roster: a
// 5000-unit battle puts a few dozen ids in the box around the shooter instead of a
// few thousand in an array to sort, and the box covers the whole sight radius, so
// the answer is the same one a full scan would give.  A hull may declare a
// restricted target list in its type (`targets`: a rifleman picks fights with other
// infantry and leaves armour alone - 15 hp in the open is not a threat to a tank).
//
// Two things narrow that box down.  The *window*: a candidate has to sit inside the
// aperture the crew is looking through (`Watch.CONE`, 40 deg about `gaze_yaw`), so a
// crew finds what it is facing and nothing else - that is `Systems/AI/Watch.cs`'s
// half of the model and it gates *finding*, never *tracking*, so a hull already in
// hand stays in hand for as long as the guns remember it.  The *order*: candidates
// are ranked by `ThreatScore` and not by distance, and the hull in hand keeps it
// unless a newcomer is worth `ThreatScore.SWITCH` times more.
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

    /// <summary>The sighting wheel, where the machine has one: a man leaves no bearing
    /// behind (`Components.ALARM_COMPS`).</summary>
    public float[]? tgt;

    public SenseColumns(Group g) : base(g) { }

    public override string[] Names => sense_names;

    protected override void bind(Group g)
    {
        base.bind(g);
        tgt = g.has("tgt") ? g.f("tgt") : null;
    }
}

public static class Sense
{
    /// <summary>s between two looks.  Jittered per crew so a battle does not
    /// acquire in lockstep on one tick.</summary>
    public const float ACQUIRE = 0.45f;
    /// <summary>How many of the nearest candidates are worth a line-of-sight cast.</summary>
    public const int SIGHT_CASTS = 6;
    /// <summary>Threat weight of a contact known only through the faction datalink.</summary>
    public const float DATALINK_THREAT = 0.5f;

    /// <summary>Seed the sighting wheel with current datalink contacts that this crew
    /// cannot see directly.  `Datalink.run` has rebuilt `link_seen` for this tick, so a
    /// clear bit is already stale; the wheel receives a bearing and a weak weight, never
    /// an id.  `Watch` consumes it before `Sense` scans the aperture it turned.</summary>
    public static void link_wheel(World world)
    {
        var e = world.ecs;
        var etype = e.uv("etype");
        var faction = e.uv("faction");
        int[] ids = Array.Empty<int>();
        float[] points = Array.Empty<float>();
        foreach (var g in e.groups(SenseColumns.OnSense))
        {
            var c = new SenseColumns(g);
            foreach (var r in c)
            {
                if (c.tgt is null || r.timer > 0f) continue;
                var own = Defs.UNIT_DEFS[r.etype];
                var want = own.targets;
                float range = Math.Min(own.sight, Datalink.RANGE);
                var (ci, cj) = world.grid.cell_of(r.eye.x, r.eye.z);
                int n = world.grid.box_into(ci, cj, (int)(range * world.grid.inv) + 1,
                                            ref ids, ref points);
                for (int k = 0; k < n; k++)
                {
                    int target = ids[k];
                    if (!e.is_alive(target) || faction[target] == r.faction
                        || !Datalink.known(world, r.faction, target)) continue;
                    int type = etype[target];
                    if (want != null && Array.IndexOf(want, type) < 0) continue;
                    var tpos = new Vec3(points[k * 3], points[k * 3 + 1], points[k * 3 + 2]);
                    var td = Defs.UNIT_DEFS[type];
                    float dx = tpos.x - r.eye.x, dy = tpos.y + td.height * 0.5f - r.eye.y;
                    float dz = tpos.z - r.eye.z;
                    float ds = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
                    if (!(ds <= range)) continue;
                    float worth = ThreatScore.of(type, ds, own.sight);
                    if (worth <= 0f) continue;
                    var at = new Vec3(tpos.x, tpos.y + td.height * 0.6f, tpos.z);
                    if (Visibility.source(world, r.eid, target, at) != VisibilitySource.Datalink)
                        continue;
                    Wheel.mark(c.tgt, r.index, Wheel.slot_of(MathF.Atan2(dz, dx)),
                               Wheel.TGT_LIFE, Wheel.TGT_W * worth * DATALINK_THREAT);
                }
            }
        }
    }

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
                        var seen = _sees(world, r, tid, tpos, Defs.UNIT_DEFS[e.uv("etype")[tid]], own);
                        if (seen != VisibilitySource.None)
                        {
                            // known: the belief is the truth, and the pull is measured
                            var tvel = e.fv("vel").get3(tid);
                            r.believe(tpos, tvel, e.uv("etype")[tid]);
                            c.measure_accel(r.index, tid, tvel, dt);
                            _sight(c, r, tpos, e.uv("etype")[tid], own, seen);
                        }
                        else
                        {
                            // neither direct sight nor datalink knows it: the memory
                            // walks at what it was doing, the pull ages out (no fresh
                            // sample), and after `Crew.MEMORY` s the hull is forgotten
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
                    int found = _acquire(world, r, combat_fac, own, out float worth,
                                         out VisibilitySource source);
                    // the hull in hand keeps it unless the new one is clearly worth
                    // more (`ThreatScore.SWITCH`): without the margin two hulls of
                    // equal score would trade the crew back and forth every scan
                    if (found >= 0 && found != r.target &&
                        worth > _worth(world, r, own) * ThreatScore.SWITCH)
                    {
                        var tpos = e.fv("pos").get3(found);
                        var tvel = e.fv("vel").get3(found);
                        r.target_mem = found;
                        r.believe(tpos, tvel, e.uv("etype")[found]);
                        c.measure_accel(r.index, found, tvel, dt);
                        _sight(c, r, tpos, e.uv("etype")[found], own, source);
                    }
                }
            }
        }
    }

    /// <summary>Leave the bearing behind: a known hull puts its direction on the
    /// sighting wheel, weighted by how much it is worth, so that a crew whose belief
    /// runs out goes back to look there instead of sweeping blind (`Watch` branch 2,
    /// `Wheel.tgt`).  Datalink evidence is weaker than a direct sighting; a man has no
    /// wheel and leaves nothing.</summary>
    private static void _sight(SenseColumns c, CrewRow r, Vec3 tpos, int type, UnitDef own,
                               VisibilitySource source)
    {
        if (c.tgt is null) return;
        float dx = tpos.x - r.eye.x, dz = tpos.z - r.eye.z;
        float dy = tpos.y + Defs.UNIT_DEFS[type].height * 0.5f - r.eye.y;
        float ds = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
        float weight = Wheel.TGT_W * ThreatScore.of(type, ds, own.sight);
        if (source == VisibilitySource.Datalink) weight *= DATALINK_THREAT;
        Wheel.mark(c.tgt, r.index, Wheel.slot_of(MathF.Atan2(dz, dx)), Wheel.TGT_LIFE, weight);
    }

    /// <summary>Does this crew know the hull in hand: inside its engagement range and
    /// visible directly or through the current faction datalink.  Physical propagation
    /// still belongs to `Sight.clear`; `Visibility.source` only adds shared knowledge.
    ///
    /// The range is the same quantity the fire solution gates on
    /// (`Engage.ground_aim`): the eye to the hull's geometric centre, in three
    /// dimensions, so a hull on a ridge is neither "seen" at a range it cannot be
    /// shot at nor the other way round.  The *cast* is aimed at the target's own
    /// body, 60 % of its height up, so a hull looking at a rifleman looks at a man
    /// and not at a metre of air above him.</summary>
    private static VisibilitySource _sees(World world, CrewRow r, int target, Vec3 tpos,
                                          UnitDef td, UnitDef own)
    {
        float dx = tpos.x - r.eye.x, dz = tpos.z - r.eye.z;
        float dy = tpos.y + td.height * 0.5f - r.eye.y;
        if (dx * dx + dy * dy + dz * dz > (float)own.ai_range * own.ai_range)
            return VisibilitySource.None;
        return Visibility.source(world, r.eid, target,
                                 new Vec3(tpos.x, tpos.y + td.height * 0.6f, tpos.z));
    }

    /// <summary>The hostile hull worth working most, or -1.
    ///
    /// No position is gathered here - the grid carries its own copy, in the same
    /// order as its ids.  The distance is the one `_sees` measures: eye to the
    /// hull's geometric centre, so the scan radius and the engagement radius answer
    /// the same question.  The order is `ThreatScore` and not distance: the hull
    /// that can hurt the crew most is the one it works first, and the six
    /// visibility casts go to the six best of them rather than the six closest.</summary>
    private static int _acquire(World world, CrewRow r, sbyte[] combat_fac, UnitDef d,
                                out float score, out VisibilitySource source)
    {
        score = -1f;
        source = VisibilitySource.None;
        int found = -1;
        var grid = world.grid;
        var eye = r.eye;
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
            if (cf < 0 || cf == r.faction) continue;
            if (want != null && Array.IndexOf(want, (int)etype[id]) < 0) continue;
            float dx = p[k * 3] - eye.x;
            float dz = p[k * 3 + 2] - eye.z;
            // the window: only what the crew is actually looking at can be found
            // (`Systems/AI/Watch.cs`).  The bearing is horizontal - the aperture is
            // a bearing, and every target in the game is on the ground
            if (MathF.Abs(MathUtil.wrap_pi(MathF.Atan2(dz, dx) - r.gaze)) > Watch.CONE) continue;
            float dy = p[k * 3 + 1] + Defs.UNIT_DEFS[etype[id]].height * 0.5f - eye.y;
            float ds = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
            if (!(ds <= d.sight)) continue;
            float worth = ThreatScore.of(etype[id], ds, d.sight);
            if (worth <= 0f) continue;               // not worth working at all
            cand[n].id = id;
            cand[n].x = p[k * 3];
            cand[n].y = p[k * 3 + 1];
            cand[n].z = p[k * 3 + 2];
            cand[n].worth = worth;
            n += 1;
        }
        if (n == 0) return -1;
        Array.Sort(cand, 0, n);
        int take = Math.Min(SIGHT_CASTS, n);
        for (int k = 0; k < take; k++)
        {
            var tp = cand[k];
            var visible = Visibility.source(world, r.eid, tp.id,
                                            new Vec3(tp.x, tp.y + 1.5f, tp.z));
            if (visible == VisibilitySource.None) continue;
            float worth = tp.worth;
            if (visible == VisibilitySource.Datalink) worth *= DATALINK_THREAT;
            // equal evidence goes to the hull the crew can see directly
            if (worth > score || (MathF.Abs(worth - score) <= 1e-6f
                                  && visible == VisibilitySource.Direct))
            {
                score = worth;
                source = visible;
                found = tp.id;
            }
        }
        return found;
    }

    /// <summary>What the hull in hand is worth, on the candidate scale: 0 when the
    /// crew holds nothing.  Measured on the belief, because that is what the crew
    /// has - a target it has lost sight of is ranked where it thinks it is.  A fresh
    /// datalink belief carries the same discount as a datalink candidate; an expired
    /// link leaves the ordinary memory comparison unchanged.</summary>
    private static float _worth(World world, CrewRow r, UnitDef own)
    {
        if (!r.has_belief) return 0f;
        var tpos = r.belief_pos;
        float dx = tpos.x - r.eye.x, dz = tpos.z - r.eye.z;
        float dy = tpos.y + Defs.UNIT_DEFS[r.belief_type].height * 0.5f - r.eye.y;
        float worth = ThreatScore.of(r.belief_type, MathF.Sqrt(dx * dx + dy * dy + dz * dz),
                                     own.sight);
        if (r.target >= 0 && r.belief_age == 0f && Datalink.known(world, r.faction, r.target))
        {
            var truth = world.ecs.fv("pos").get3(r.target);
            var td = Defs.UNIT_DEFS[world.ecs.uv("etype")[r.target]];
            var at = new Vec3(truth.x, truth.y + td.height * 0.6f, truth.z);
            if (Visibility.source(world, r.eid, r.target, at) == VisibilitySource.Datalink)
                worth *= DATALINK_THREAT;
        }
        return worth;
    }

    /// <summary>One candidate: the grid's own copy of the position, ordered by what
    /// it is worth (`ThreatScore`).</summary>
    private struct Cand : IComparable<Cand>
    {
        public int id;
        public float x, y, z;
        public float worth;

        public int CompareTo(Cand other) => other.worth.CompareTo(worth);
    }
}
