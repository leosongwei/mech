// Where each round's tick-leg ends: one gatherer per shape, one record.
//
// Every system here answers the same question - "the earliest thing this leg ran
// into, and where" - and answers it by writing `first_hit_record` through
// `if (t < first_hit_t)`.  None of them destroys anything, so the order they run
// in decides the cost of a compare and nothing else; `Systems/Impact.cs` is the
// one reader and the one that ends the round.
//
// The shapes are queried at their own granularity: the ground is a continuous
// field and is sampled, a building is a whole tile and is solved as a slab, a
// trunk is a metre-wide cylinder and is solved as one, a unit is a sphere.  Where
// `t` comes from is therefore different in each system and comparable between
// them, which is the whole of what the record is for.

using Microsoft.Extensions.Logging;

namespace Mech.Server.Systems;

public static class FirstHit
{
    // the half-width of a round that has no `hit_r` of its own (`Sweep.HIT_R` is
    // 0 for a bullet, so this is the number its table row would carry)
    private const float PR_BULLET = 0.35f;
    private const float PR_ROUND = 0.6f;

    /// <summary>Step one: the ground, which is a continuous field and is sampled.
    ///
    /// One bulk call for the whole tick's legs (`ground_hits_many`), because a
    /// hundred short legs make one walk instead of a hundred.  The coarse cull it
    /// uses is the block maximum of ground *and* whatever stands on it, which is
    /// conservative for a heightfield and therefore exact: a sample above it
    /// cannot be below the ground either.</summary>
    public static class Ground
    {
        public static void run(World world, float dt)
        {
            var e = world.ecs;
            var sw = world.sweep;
            int n = sw.n;
            if (n == 0) return;
            world.terrain.ground_hits_many(sw.p0, sw.p1, n, 9f, sw.hit, sw.thr);
            for (int i = 0; i < n; i++)
            {
                if (!sw.hit[i]) continue;
                var slot = e.slot(sw.ids[i]);
                if (!slot.ok) continue;
                float t = sw.thr[i];
                _record(slot.a, slot.row, t, _at(sw, i, t), Defs.HIT_GROUND, -1, 0);
            }
        }
    }

    /// <summary>Step two: buildings and tree trunks, which are structures and are
    /// solved - a building as a slab from the terrain to its roof, a trunk as the
    /// cylinder it is.
    ///
    /// Both are tile-shaped, so the query is the tiles the leg crosses and never a
    /// point sample: a tile that holds no structure costs one byte test, and the
    /// legs that cross none - almost all of them, out in the field - cost the walk
    /// and nothing else.  `Terrain.structure_hit` is the door; this system knows
    /// nothing about the bitmaps behind it.</summary>
    public static class Structures
    {
        public static void run(World world, float dt)
        {
            var e = world.ecs;
            var sw = world.sweep;
            int n = sw.n;
            if (n == 0) return;
            for (int i = 0; i < n; i++)
            {
                var slot = e.slot(sw.ids[i]);
                if (!slot.ok) continue;
                var p0 = new Vec3(sw.p0[i * 3], sw.p0[i * 3 + 1], sw.p0[i * 3 + 2]);
                var p1 = new Vec3(sw.p1[i * 3], sw.p1[i * 3 + 1], sw.p1[i * 3 + 2]);
                if (!world.terrain.structure_hit(p0, p1, out float t, out Vec3 pt,
                                                 out byte tile, out int _)) continue;
                byte kind = tile == Terrain.TILE_BUILDING ? Defs.HIT_BUILDING
                                                          : Defs.HIT_TRUNK;
                _record(slot.a, slot.row, t, pt, kind, -1, 0);
            }
        }
    }

    /// <summary>Step three: units, and the missiles in the air, which are spheres -
    /// solved against the tick's hit-sphere table with the relative motion of both
    /// bodies, so a 20 m/tick missile is judgeable against a 2.5 m fuse.</summary>
    public static class Units
    {
        public static void run(World world, float dt)
        {
            var e = world.ecs;
            var sw = world.sweep;
            int n = sw.n;
            if (n == 0) return;
            var sph = world.sphere_table(dt);
            if (sph.n == 0) return;
            float reach = Sweep.reach(sph);
            var stamp = new int[sph.n];
            var cand = new int[sph.n];
            int tag = 1;
            for (int i = 0; i < n; i++)
            {
                var slot = e.slot(sw.ids[i]);
                if (!slot.ok) continue;
                var a = slot.a;
                int row = slot.row;
                var p0 = new Vec3(sw.p0[i * 3], sw.p0[i * 3 + 1], sw.p0[i * 3 + 2]);
                var p1 = new Vec3(sw.p1[i * 3], sw.p1[i * 3 + 1], sw.p1[i * 3 + 2]);
                float segx = p1.x - p0.x, segy = p1.y - p0.y, segz = p1.z - p0.z;
                float l2 = segx * segx + segy * segy + segz * segz;
                if (l2 <= 1e-6f) continue;
                // the half-width the sweep tests against: a bullet is a sliver, a
                // missile is a body.  Asked as a tag (`Defs.KIND_TAGS`), never as
                // a `proj_kind` value.
                float pr = a.has("bullet") ? PR_BULLET : PR_ROUND;
                tag += 1;
                int nc = Sweep.LegSpheres(sph, p0, p1, pr + reach, stamp, tag, cand);
                if (nc == 0) continue;
                float best_tt = 2f;
                int best_i = -1;
                int my_faction = a.u8("faction")[row];
                float my_age = a.f("proj_age")[row];
                int my_src = a.i("proj_src")[row];
                for (int j = 0; j < nc; j++)
                {
                    int s = cand[j];
                    if (sph.factions[s] == my_faction) continue;
                    // a round does not hit the hull that just fired it
                    if (my_age < 0.12f && sph.ids[s] == my_src) continue;
                    Sweep.Closest(sph, s, p0, segx, segy, segz, out float tt, out float dd);
                    if (!(dd < sph.radii[s] + pr)) continue;
                    if (tt < best_tt)
                    {
                        best_tt = tt;
                        best_i = s;
                    }
                }
                if (best_i < 0) continue;
                _record(a, row, best_tt, _at(sw, i, best_tt), Defs.HIT_UNIT,
                        sph.ids[best_i], sph.modules[best_i]);
            }
        }
    }

    /// <summary>The point a fraction of the way along leg `i`.</summary>
    private static Vec3 _at(SweepScratch sw, int i, float t)
    {
        float p0x = sw.p0[i * 3], p0y = sw.p0[i * 3 + 1], p0z = sw.p0[i * 3 + 2];
        float p1x = sw.p1[i * 3], p1y = sw.p1[i * 3 + 1], p1z = sw.p1[i * 3 + 2];
        return new Vec3(p0x + (p1x - p0x) * t, p0y + (p1y - p0y) * t,
                        p0z + (p1z - p0z) * t);
    }

    /// <summary>The airburst fuse: a round that passes close enough to a man or a
    /// missile goes off *there*, not at the end of the tick.
    ///
    /// A cannon round covers 36 m in one tick, so a distance test at the tick
    /// boundaries would step clean over a rifleman standing 4 m off the path.
    /// Both bodies are straight and constant-speed inside one 1/30 s, so the
    /// closest approach is closed-form (`Sweep.Closest`) and the burst lands on
    /// it.
    ///
    /// Two target sets light it: `person` - a man is a man whatever he carries,
    /// while an APC 4 m from the path leaves the fuse cold and gets the impact
    /// fuse instead - and `interceptable`, the counter-battery half.  The target it
    /// fuses on is settled by `Impact` at the gap measured here, because by the
    /// time a burst entity is judged a 600 m/s missile is twenty metres out of a
    /// four metre radius.</summary>
    public static class Fuse
    {
        private static readonly ILogger log = MechLog.get("mech.prox");

        public static void run(World world, float dt)
        {
            var e = world.ecs;
            var sw = world.sweep;
            int n = sw.n;
            if (n == 0) return;

            var people = e.query("person");
            var missiles = world.interceptable();
            if (people.Length == 0 && missiles.Length == 0) return;

            // the two target sets, as rows of the tick's sphere table: the ids are
            // global, so a projectile id never collides with a hull's
            var target_set = new HashSet<int>(people);
            foreach (int m in missiles) target_set.Add(m);
            var sph = world.sphere_table(dt);
            int nrow = 0;
            var rows = new int[sph.n];
            for (int s = 0; s < sph.n; s++)
                if (target_set.Contains(sph.ids[s])) rows[nrow++] = s;
            if (nrow == 0) return;

            for (int i = 0; i < n; i++)
            {
                int pid = sw.ids[i];
                var slot = e.slot(pid);
                if (!slot.ok) continue;
                var a = slot.a;
                int row = slot.row;
                var wd = Defs.WEAPONS[a.u8("proj_wid")[row]];
                float prox = wd.prox;
                if (prox <= 0f) continue;                 // no fuse in this round
                var p0 = new Vec3(sw.p0[i * 3], sw.p0[i * 3 + 1], sw.p0[i * 3 + 2]);
                float segx = sw.p1[i * 3] - p0.x;
                float segy = sw.p1[i * 3 + 1] - p0.y;
                float segz = sw.p1[i * 3 + 2] - p0.z;
                float leg2 = segx * segx + segy * segy + segz * segz;
                if (leg2 < 1e-9f) continue;               // barely moved: nothing to solve
                float leg = MathF.Sqrt(leg2);
                int mine = a.u8("faction")[row];

                // time of closest approach, as a fraction of this tick, clamped to
                // the leg itself: for a round that slows to a stop the closest
                // point is an end, not a projection
                float best_gap = float.PositiveInfinity;
                float best_t = 0f;
                int best_row = -1;
                for (int j = 0; j < nrow; j++)
                {
                    int s = rows[j];
                    // a burst never fuses on its own side
                    if (sph.factions[s] == mine) continue;
                    Sweep.Closest(sph, s, p0, segx, segy, segz, out float tt, out float dd);
                    float gap = dd - sph.radii[s];
                    if (gap < best_gap)
                    {
                        best_gap = gap;
                        best_t = tt;
                        best_row = s;
                    }
                }
                if (best_row < 0 || !(best_gap <= prox)) continue;
                var burst = new Vec3(p0.x + segx * best_t, p0.y + segy * best_t,
                                     p0.z + segz * best_t);
                int tid = sph.ids[best_row];
                bool human = e.has(tid, "person");
                // DEBUG, like every weapon-level line: a burst is an ordinary
                // event and this geometry dump only earns its place when the fuse
                // is being hunted
                if (log.IsEnabled(LogLevel.Debug))
                {
                    log.LogDebug("prox fuse: {name} round {pid} burst at ({bx:F1}, {by:F1}, "
                                 + "{bz:F1}), {gap:F2} m off {what} {tid} - {into:F1} m into a "
                                 + "{leg:F1} m leg from ({x0:F1}, {y0:F1}, {z0:F1}), round "
                                 + "flew on to ({x1:F1}, {y1:F1}, {z1:F1})",
                                 wd.name, pid, burst.x, burst.y, burst.z, best_gap,
                                 human ? "person" : "missile", tid, best_t * leg, leg,
                                 p0.x, p0.y, p0.z, p0.x + segx, p0.y + segy, p0.z + segz);
                }
                if (!(best_t < a.f("first_hit_t")[row])) continue;
                a.f("first_hit_t")[row] = best_t;
                a.f("first_hit_pos")[row * 3 + 0] = burst.x;
                a.f("first_hit_pos")[row * 3 + 1] = burst.y;
                a.f("first_hit_pos")[row * 3 + 2] = burst.z;
                a.u8("first_hit_kind")[row] = Defs.HIT_FUSE;
                a.i("first_hit_unit")[row] = tid;
                a.u8("first_hit_mod")[row] = 0;
                a.f("first_hit_gap")[row] = best_gap;
            }
        }
    }

    /// <summary>Write a contact into the round's record if it is earlier than the
    /// one already there.  Every gatherer is this call and nothing else.</summary>
    private static void _record(Archetype a, int row, float t, Vec3 p, byte kind,
                               int unit, int mod)
    {
        if (!(t < a.f("first_hit_t")[row])) return;
        a.f("first_hit_t")[row] = t;
        a.f("first_hit_pos")[row * 3 + 0] = p.x;
        a.f("first_hit_pos")[row * 3 + 1] = p.y;
        a.f("first_hit_pos")[row * 3 + 2] = p.z;
        a.u8("first_hit_kind")[row] = kind;
        a.i("first_hit_unit")[row] = unit;
        a.u8("first_hit_mod")[row] = (byte)mod;
    }
}
