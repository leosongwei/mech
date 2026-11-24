// The aperture: what a crew can find, and how fast the looking turns.
//
// Five rows, one each for the facts the model is made of (`docs/plans/AI_sight_plan.md`):
//
//   front     a target in the window is acquired in one scan, not in a sweep
//   behind    a target outside the pattern is never acquired, however long it waits
//   slew      the head turns at 150 deg/s - 90 deg is 0.6 s, not one tick
//   flinch    a round that goes past turns the head to the bearing it came from
//   burst     ten rounds from one bearing turn it once, not ten times
//   score     the threat table, against the worked example in the plan
//
// The scenarios are built the way `FcsCheck` builds its own: a range world, one
// crew, and whatever the row is about.  A crew is a *crew* - `crew: true` plus
// `crew_engage`, which is the tag `Systems/AI/Sense.cs` queries - and the targets
// are puppets.

using Mech.Server.Systems;
using Mech.Server.Systems.AI;

namespace Mech.Server.Harness;

public static class GazeCheck
{
    private const float DT = 1f / 30f;

    public static int run()
    {
        var world = new World(7, "range");
        var e = world.ecs;
        var (sx, sz) = Spot(world);
        Console.WriteLine($"gaze: crew at ({sx:0}, {sz:0}), "
                          + $"window ±{Watch.CONE * 180f / MathF.PI:0} deg, "
                          + $"{Watch.RATE * 180f / MathF.PI:0} deg/s");
        int bad = 0;

        // ---- front: in the window, acquired on the first scan -----------------
        {
            int crew = Crew(world, sx, sz);
            int target = world.spawn_unit(Defs.ETYPE_APC, Defs.FACTION_BLUE, sx + 1500f, sz,
                                          -1, crew: false);
            float t = 0f;
            while (t < 20f && e.iv("target")[crew] < 0)
            {
                Hold(world, crew, target);
                world.update(DT);
                t += DT;
            }
            int got = e.iv("target")[crew];
            // the sweep starts on the right-hand sector, so the nose is looked at
            // from `Watch.STEP` s on: one cycle (4 sectors) is the worst case
            bool ok = got == target && t <= Watch.STEP * 2f + 1f;
            Console.WriteLine($"front     target 1500 m dead ahead: found after {t:0.00} s"
                              + $" (nose is sector {Array.IndexOf(Watch.PATTERN, 0f)} of the"
                              + $" {Watch.STEP:0} s sweep)  {(ok ? "ok" : "GAP")}");
            if (!ok) bad++;
            Clean(world, crew, target);
        }

        // ---- behind: outside the pattern, never acquired ----------------------
        {
            int crew = Crew(world, sx, sz);
            int target = world.spawn_unit(Defs.ETYPE_APC, Defs.FACTION_BLUE, sx - 900f, sz,
                                          -1, crew: false);
            float t = 0f, lo = 9f, hi = -9f;
            while (t < 20f && e.iv("target")[crew] < 0)
            {
                Hold(world, crew, target);
                world.update(DT);
                t += DT;
                float rel = MathUtil.wrap_pi(e.fv("gaze_yaw")[crew]) * 180f / MathF.PI;
                lo = MathF.Min(lo, rel);
                hi = MathF.Max(hi, rel);
            }
            bool never = e.iv("target")[crew] < 0;
            Console.WriteLine($"behind    target 900 m dead astern: {(never ? "never found" : "FOUND")}"
                              + $" in 20 s, aperture swept {lo:0}..{hi:0} deg  "
                              + $"{(never ? "ok" : "GAP")}");
            if (!never) bad++;
            Clean(world, crew, target);
        }

        // ---- slew: 150 deg/s, so a right angle is 0.6 s ----------------------
        {
            int crew = Crew(world, sx, sz);
            Hold(world, crew, -1);
            world.update(DT);                          // one tick of the pattern
            // force the head where the pattern is not, and ask for a right angle
            e.fv("gaze_yaw")[crew] = 0f;
            float want = MathF.PI / 2f;
            float t = 0f;
            // the pattern own sector is what the crew will be told to look at, so
            // the row reads the *machine*: hand it an intent and time the turn
            while (t < 2f && MathF.Abs(MathUtil.wrap_pi(want - e.fv("gaze_yaw")[crew])) > 0.01f)
            {
                e.fv("gaze_yaw")[crew] = MathUtil.turn_toward(
                    e.fv("gaze_yaw")[crew], want, Watch.RATE * DT);
                t += DT;
            }
            float theory = (MathF.PI / 2f) / Watch.RATE;
            bool ok = MathF.Abs(t - theory) <= 2f * DT;
            Console.WriteLine($"slew      90 deg: {t:0.00} s measured, {theory:0.00} s at "
                              + $"{Watch.RATE * 180f / MathF.PI:0} deg/s  {(ok ? "ok" : "GAP")}");
            if (!ok) bad++;
            Clean(world, crew, -1);
        }

        // ---- flinch: a round going past turns the head to its bearing --------
        {
            int crew = Crew(world, sx, sz);
            Hold(world, crew, -1);
            world.update(DT);
            // the search pattern has the head at `leg_yaw + 90` = +90 deg; the fire
            // comes from -x, so the right angle to turn is 90 deg again
            float yaw0 = e.fv("gaze_yaw")[crew];
            Flare(world, crew);                        // a round travelling +x
            // the leg is 30 m long and the round starts 60 m out, so the pass itself
            // is a tick or two away: step until the crew has heard it
            float got = 0f;
            for (int k = 0; k < 6 && e.fv("flinch")[crew] <= 0f; k++) world.update(DT);
            got = e.fv("flinch").get(crew, 1);
            float t = 6f * DT;
            while (t < 3f && MathF.Abs(MathUtil.wrap_pi(MathF.PI - e.fv("gaze_yaw")[crew])) > 0.05f)
            {
                Hold(world, crew, -1);
                world.update(DT);
                t += DT;
            }
            bool found = MathF.Abs(MathUtil.wrap_pi(MathF.PI - e.fv("gaze_yaw")[crew])) <= 0.05f;
            bool alarm = MathF.Abs(MathUtil.wrap_pi(MathF.PI - got)) <= 0.05f;
            Console.WriteLine($"flinch    round from -x: alarm bearing {got:0.00} rad, "
                              + $"head {yaw0:0.00} -> {e.fv("gaze_yaw")[crew]:0.00} rad "
                              + $"after {t:0.00} s  {(found && alarm ? "ok" : "GAP")}");
            if (!(found && alarm)) bad++;
            Clean(world, crew, -1);
        }

        // ---- burst: one bearing is one event --------------------------------
        {
            int crew = Crew(world, sx, sz);
            Hold(world, crew, -1);
            world.update(DT);
            // prime it: the first round has to land before the burst means anything
            Flare(world, crew);
            for (int k = 0; k < 6 && e.fv("flinch")[crew] <= 0f; k++) world.update(DT);
            float bearing = e.fv("flinch").get(crew, 1);
            int changes = 0;
            for (int k = 0; k < 12; k++)
            {
                Flare(world, crew);
                Hold(world, crew, -1);
                world.update(DT);
                float now = e.fv("flinch").get(crew, 1);
                if (MathF.Abs(MathUtil.wrap_pi(now - bearing)) > 1e-4f)
                {
                    changes++;
                    bearing = now;
                }
            }
            Console.WriteLine($"burst     12 rounds from one bearing: {changes} extra turn(s)  "
                              + $"{(changes == 0 ? "ok" : "GAP")}");
            if (changes != 0) bad++;
            Clean(world, crew, -1);
        }

        // ---- score: the worked example in the plan --------------------------
        {
            // a mech crew's own `sight` is 3500 m
            float sight = Defs.UNIT_DEFS[Defs.ETYPE_MECH].sight;
            float mech = ThreatScore.of(Defs.ETYPE_MECH, 3000f, sight);
            float apc = ThreatScore.of(Defs.ETYPE_APC, 800f, sight);
            float foot = ThreatScore.of(Defs.ETYPE_INF_RIFLE, 200f, sight);
            bool ok = MathF.Abs(mech - 0.400f) < 1e-3f && MathF.Abs(apc - 0.452f) < 1e-3f
                      && MathF.Abs(foot - 0.099f) < 1e-3f && apc > mech && mech > foot;
            Console.WriteLine($"score     mech@3000 {mech:0.000}, apc@800 {apc:0.000} (higher), "
                              + $"rifle@200 {foot:0.000}  {(ok ? "ok" : "GAP")}");
            if (!ok) bad++;
        }

        Console.WriteLine(bad == 0
            ? "gaze: the aperture finds what it faces, turns at 150 deg/s, and one burst is one turn"
            : $"gaze: {bad} gap(s) - a crew that finds what it is not looking at, or a head that does not turn");
        return bad == 0 ? 0 : 1;
    }

    /// <summary>One crew on the range, aimed down +x with its aperture on its nose.
    /// Aimed once: the sweep is `Watch`'s from the next tick on, and a harness that
    /// re-aimed every tick would be measuring its own hand instead of the model.</summary>
    private static int Crew(World world, float x, float z)
    {
        int eid = world.spawn_unit(Defs.ETYPE_MECH, Defs.FACTION_RED, x, z, -1,
                                   weapons: new[] { Defs.WID_AC30, Defs.WID_NONE },
                                   extra: new[] { AiTags.ENGAGE }, crew: true);
        Check.aim_east(world, eid);
        return eid;
    }

    /// <summary>The field belongs to the row: everything but what it is about is
    /// taken off, the same rule `FcsCheck` follows.</summary>
    private static void Hold(World world, int keepA, int keepB)
    {
        var e = world.ecs;
        foreach (int id in world.units())
        {
            if (id == keepA || id == keepB) continue;
            if (e.uv("etype")[id] == Defs.ETYPE_BASE) continue;
            e.destroy(id);
        }
    }

    /// <summary>A round that goes past the crew at arm's length, travelling +x, so
    /// the bearing it came from is -x (`pi`).  A bullet, because the cheap case is
    /// the one that has to work: an autocannon burst is what a crew actually hears.
    /// </summary>
    private static void Flare(World world, int crew)
    {
        var e = world.ecs;
        var p = e.fv("pos").get3(crew);
        // 12 m to the side of the hull and level with its shoulder: inside `WHIZ`,
        // well outside anything a bullet can hit - a round that hits is destroyed by
        // `Impact` a system before this one and never gets to be a near miss
        world.spawn_round(-1, Defs.FACTION_BLUE, Defs.WID_AC30, 0,
                          new Vec3(p.x - 60f, p.y + 3f, p.z + 12f),
                          new Vec3(900f, 0f, 0f), 2f, -1);
    }

    private static void Clean(World world, params int[] keep)
    {
        var e = world.ecs;
        foreach (int id in world.units())
        {
            if (Array.IndexOf(keep, id) >= 0) continue;
            if (e.uv("etype")[id] == Defs.ETYPE_BASE) continue;
            e.destroy(id);
        }
        foreach (int id in world.projectiles()) e.destroy(id);
    }

    /// <summary>Open, flat ground with 1.6 km of clear line down +x - the most the
    /// range's 4 km map can give, and more than a row needs.</summary>
    private static (float x, float z) Spot(World world)
    {
        var t = world.terrain;
        for (float x = 700f; x < t.w * Terrain.TILE - 1700f; x += 200f)
        {
            for (float z = 700f; z < t.d * Terrain.TILE - 700f; z += 200f)
            {
                float h = t.height_at(x, z);
                bool open = true;
                for (float d = 0f; d <= 1600f && open; d += 100f)
                {
                    float px = x + d;
                    if (t.blocked(px, z)) { open = false; break; }
                    if (MathF.Abs(t.height_at(px, z) - h) > 6f) { open = false; break; }
                    if (!t.los(new Vec3(x, h + Defs.MECH_EYE, z),
                               new Vec3(px, t.height_at(px, z) + 1.4f, z), 12f)) open = false;
                }
                if (open) return (x, z);
            }
        }
        return (1200f, 1200f);
    }
}
