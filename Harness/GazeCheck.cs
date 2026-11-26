// The aperture and the alarm wheel, one line per rule.
//
// Every line is one rule of `Systems/AI/Watch.cs` + `Systems/AI/Support/Wheel.cs` on
// the range map, with the field cleared down to what the line is about - the rule
// `FcsCheck` and `BeamCheck` follow (`Check.aim_east` points the four angles at the
// target so a row measures the thing it is named after).  The datalink rows use a
// real smoke screen and a second vehicle to separate shared knowledge from sight.
//
//   ./dotnet.sh run -- --check gaze

using Mech.Server.Systems;
using Mech.Server.Systems.AI;

namespace Mech.Server.Harness;

public static class GazeCheck
{
    private const float DT = 1f / 30f;

    public static int run()
    {
        var world = new World(7, "range");
        // the range keeps twelve hulks, a patrol mech and a squad standing on it, and
        // respawns each three seconds: a stray hulk becomes a target and a target
        // outranks the wheel, which is the opposite of what these rows measure
        world.systems.Remove(Modes.Range.Targets.run);
        // ...and the defence post shoots the designators these rows put on the field
        world.systems.Remove(Modes.Range.Interceptor.run);
        var e = world.ecs;
        var (sx, sz) = Spot(world);
        Console.WriteLine($"gaze: crew at ({sx:0}, {sz:0}), window ±{Watch.CONE * 180f / MathF.PI:0} deg, "
                          + $"{Watch.RATE * 180f / MathF.PI:0} deg/s, {Wheel.SLOTS} slots of"
                          + $" {Wheel.STEP * 180f / MathF.PI:0} deg");
        int bad = 0;
        foreach (var kv in world.base_entity)

        // ---- sweep: the stalest slot of the front half, never the rear ---------
        {
            int crew = Crew(world, sx, sz);
            Clean(world, crew);
            var seen = new bool[Wheel.SLOTS];
            int rear = 0;
            for (int t = 0; t < (int)(40f / DT); t++)
            {
                Step(world, crew);
                int s = Wheel.slot_of(e.fv("gaze_yaw")[crew]);
                seen[s] = true;
                if (!Wheel.in_front(s, 0f)) rear++;
            }
            int front = 0, total = 0;
            for (int s = 0; s < Wheel.SLOTS; s++)
                if (Wheel.in_front(s, 0f)) { total++; if (seen[s]) front++; }
            bool ok = rear == 0 && front == total;
            Line("sweep", $"40 s: {front}/{total} front slots visited, rear selected {rear} tick(s)",
                 ok, ref bad);
            Clean(world, -1);
        }

        // ---- front: what the window covers is found on the first scan ----------
        {
            int crew = Crew(world, sx, sz);
            Clean(world, crew);
            int target = world.spawn_unit(Defs.ETYPE_APC, Defs.FACTION_BLUE, sx + 1500f, sz,
                                          -1, crew: false);
            float t = 0f;
            while (t < 10f && e.iv("target")[crew] < 0)
            {
                Step(world, crew); t += DT;
            }
            bool ok = e.iv("target")[crew] == target && t <= 2f;
            Line("front", $"target 1500 m dead ahead: found after {t:0.00} s", ok, ref bad);
            Clean(world, -1);
        }

        // ---- behind: the rear is never swept, so it is never found -------------
        {
            int crew = Crew(world, sx, sz);
            Clean(world, crew);
            int target = world.spawn_unit(Defs.ETYPE_APC, Defs.FACTION_BLUE, sx - 900f, sz,
                                          -1, crew: false);
            float t = 0f, lo = 9f, hi = -9f;
            while (t < 20f && e.iv("target")[crew] != target)
            {
                Step(world, crew); t += DT;
                float rel = MathUtil.wrap_pi(Gaze(world, crew)) * 180f / MathF.PI;
                lo = MathF.Min(lo, rel); hi = MathF.Max(hi, rel);
            }
            bool never = e.iv("target")[crew] != target;
            Line("behind", $"target 900 m dead astern: {(never ? "never found" : "FOUND")},"
                           + $" head swept {lo:0}..{hi:0} deg", never, ref bad);
            Clean(world, -1);
        }

        // ---- slew: 150 deg/s, so a right angle is 0.6 s ------------------------
        {
            int crew = Crew(world, sx, sz);
            Clean(world, crew);
            e.fv("gaze_yaw")[crew] = 0f;
            float want = MathF.PI / 2f, t = 0f, g = 0f;
            while (t < 2f && MathF.Abs(MathUtil.wrap_pi(want - g)) > 0.01f)
            {
                g = MathUtil.turn_toward(g, want, Watch.RATE * DT);
                t += DT;
            }
            float theory = (MathF.PI / 2f) / Watch.RATE;
            bool ok = MathF.Abs(t - theory) <= 2f * DT;
            Line("slew", $"90 deg: {t:0.00} s measured, {theory:0.00} s at"
                         + $" {Watch.RATE * 180f / MathF.PI:0} deg/s", ok, ref bad);
            Clean(world, -1);
        }

        // ---- bullet: a near miss puts its slot on the wheel and the head follows
        {
            int crew = Crew(world, sx, sz);
            Clean(world, crew);
            world.update(DT); Pin(world, crew);
            float from = MathF.PI * 0.5f;                    // fire from +z
            int slot = Wheel.slot_of(from);
            Flare(world, crew, from);
            float t = 0f;
            bool alarmed = false;
            while (t < 4f && MathF.Abs(MathUtil.wrap_pi(Gaze(world, crew) - Wheel.centre(slot))) > 0.15f)
            {
                Step(world, crew); t += DT;
                if (State(world, crew) is AiGaze.ALARM or AiGaze.ALARM_TURN) alarmed = true;
            }
            bool hot = Left(world, crew, "alarm", slot) > 0f;
            bool on = MathF.Abs(MathUtil.wrap_pi(Gaze(world, crew) - Wheel.centre(slot))) <= 0.15f;
            Line("bullet", $"near miss from +z: slot {Left(world, crew, "alarm", slot):0.00} s left,"
                           + $" head on it after {t:0.00} s, state {alarmed}",
                 hot && on && alarmed, ref bad);
            Clean(world, -1);
        }

        // ---- burst: one direction is one event, however many rounds ------------
        {
            int crew = Crew(world, sx, sz);
            Clean(world, crew);
            world.update(DT); Pin(world, crew);
            float from = MathF.PI * 0.5f;
            int changes = 0, last = -1;
            bool took = false;
            for (int k = 0; k < 12; k++)
            {
                Flare(world, crew, from);
                for (int i = 0; i < 3; i++) Step(world, crew);
                int now = Wheel.slot_of(e.fv("gaze_cmd")[crew]);
                if (took && now != last) changes++;
                if (now == Wheel.slot_of(from)) took = true;
                last = now;
            }
            Line("burst", $"12 rounds from one bearing: {changes} change(s) of slot",
                 changes == 0, ref bad);
            Clean(world, -1);
        }

        // ---- two-way: fire from both sides is two slots, and both are reached --
        {
            int crew = Crew(world, sx, sz);
            Clean(world, crew);
            world.update(DT); Pin(world, crew);
            int a = Wheel.slot_of(MathF.PI * 0.5f), b = Wheel.slot_of(-MathF.PI * 0.5f);
            bool hit_a = false, hit_b = false;
            int ticks = 0, turning = 0;
            for (int k = 0; k < 14; k++)
            {
                Flare(world, crew, (k % 2 == 0) ? MathF.PI * 0.5f : -MathF.PI * 0.5f);
                for (int i = 0; i < 11; i++)                       // 0.35 s a side
                {
                    Step(world, crew);
                    float g = Gaze(world, crew);
                    if (MathF.Abs(MathUtil.wrap_pi(g - Wheel.centre(a))) < 0.2f) hit_a = true;
                    if (MathF.Abs(MathUtil.wrap_pi(g - Wheel.centre(b))) < 0.2f) hit_b = true;
                    ticks++; if (State(world, crew) == AiGaze.ALARM_TURN) turning++;
                }
            }
            Line("two-way", $"alternating +z / -z: reached both slots {hit_a && hit_b},"
                            + $" in flight {100f * turning / Math.Max(ticks, 1):0}% of the time",
                 hit_a && hit_b, ref bad);
            Clean(world, -1);
        }

        // ---- mark: a stronger report takes the slot, a weaker one renews it ----
        {
            var buf = new float[Wheel.SLOTS * 2];
            int s = Wheel.slot_of(0f);
            Wheel.mark(buf, 0, s, Wheel.ALARM_LIFE, Wheel.BULLET_W);
            Wheel.mark(buf, 0, s, Wheel.ALARM_LIFE * 2f, Wheel.BLAST_W);
            bool burst_lost = MathF.Abs(buf[s * 2 + 1] - Wheel.BULLET_W) < 1e-6f;
            Wheel.mark(buf, 0, s, Wheel.ALARM_LIFE, Wheel.LASER_W);
            bool laser_won = MathF.Abs(buf[s * 2 + 1] - Wheel.LASER_W) < 1e-6f;
            Line("mark", "a burst does not displace a bullet, a laser does",
                 burst_lost && laser_won, ref bad);
        }

        // ---- laser: a line held on the hull is an attack -----------------------
        {
            int crew = Crew(world, sx, sz);
            Clean(world, crew);
            int painter = Painter(world, crew, MathF.PI);
            int slot = Wheel.slot_of(MathF.PI);
            float t = 0f;
            while (t < 2f && LaserLeft(world, crew, slot) <= 0f)
            {
                Lay(world, painter, crew);
                Step(world, crew); t += DT;
            }
            bool armed = MathF.Abs(t - Suppress.PAINT_ARM) <= 3f * DT;
            // the head ends up in the *slot*, not on the designator: once it has
            // turned far enough the painter itself is inside the window, and from
            // there the belief takes over and aims at the hull (`AiGaze.TARGET`)
            float turn = 0f;
            while (turn < 3f && Wheel.slot_of(Gaze(world, crew)) != slot)
            {
                Lay(world, painter, crew); Step(world, crew); turn += DT;
            }
            bool on = Wheel.slot_of(Gaze(world, crew)) == slot;
            Line("laser", $"beam held from astern: armed after {t:0.00} s"
                          + $" ({Suppress.PAINT_ARM:0.00} demanded), head on it after {turn:0.00} s",
                 armed && on, ref bad);
            Clean(world, -1);
        }

        // ---- laser-off: a beam that keeps breaking is not an attack ------------
        {
            int crew = Crew(world, sx, sz);
            Clean(world, crew);
            int painter = Painter(world, crew, MathF.PI);
            int slot = Wheel.slot_of(MathF.PI);
            float peak = 0f;
            byte worst = AiGaze.SWEEP;
            for (int k = 0; k < 5; k++)
            {
                for (int i = 0; i < 9; i++)                          // 0.30 s on
                {
                    Lay(world, painter, crew); Step(world, crew);
                    peak = MathF.Max(peak, LaserLeft(world, crew, slot));
                    if (State(world, crew) != AiGaze.SWEEP) worst = State(world, crew);
                }
                for (int i = 0; i < 9; i++)                          // 0.30 s off
                {
                    e.row_f(painter, "sight_yaw", out int q3)[q3] += 0.5f;
                    Step(world, crew);
                    if (State(world, crew) != AiGaze.SWEEP) worst = State(world, crew);
                }
            }
            Line("laser-off", $"0.30 s on / 0.30 s off: peak {peak:0.00} s,"
                              + $" state stayed {(worst == AiGaze.SWEEP ? "SWEEP" : "alarmed")}",
                 peak <= 0f && worst == AiGaze.SWEEP, ref bad);
            Clean(world, -1);
        }

        // ---- laser-cross: two designators on two bearings arm nothing ----------
        {
            int crew = Crew(world, sx, sz);
            Clean(world, crew);
            int p1 = Painter(world, crew, MathF.PI);
            int p2 = Painter(world, crew, -MathF.PI * 0.8f);
            int s1 = Wheel.slot_of(MathF.PI), s2 = Wheel.slot_of(-MathF.PI * 0.8f);
            for (int k = 0; k < 10; k++)
            {
                for (int i = 0; i < 9; i++)                          // p1 on
                {
                    Lay(world, p1, crew);
                    e.row_f(p2, "sight_yaw", out int q2)[q2] += 0.5f;
                    Step(world, crew);
                }
                for (int i = 0; i < 9; i++)                          // p2 on
                {
                    Lay(world, p2, crew);
                    e.row_f(p1, "sight_yaw", out int q1)[q1] += 0.5f;
                    Step(world, crew);
                }
            }
            bool quiet = LaserLeft(world, crew, s1) <= 0f && LaserLeft(world, crew, s2) <= 0f;
            Line("laser-cross", "two designators alternating on two bearings:"
                                + $" best slot {MathF.Max(LaserLeft(world, crew, s1), LaserLeft(world, crew, s2)):0.00} s",
                 quiet, ref bad);
            Clean(world, -1);
        }

        // ---- sighting: a hull that was seen leaves its bearing behind ----------
        {
            int crew = Crew(world, sx, sz);
            Clean(world, crew);
            int target = world.spawn_unit(Defs.ETYPE_APC, Defs.FACTION_BLUE, sx + 1500f, sz,
                                          -1, crew: false);
            int slot = Wheel.slot_of(0f);
            float t = 0f;
            while (t < 4f && State(world, crew) != AiGaze.TARGET)
            {
                Step(world, crew); t += DT;
            }
            bool engaged = State(world, crew) == AiGaze.TARGET;
            e.destroy(target);
            // look away by hand: the wheel has to be what brings the head back
            e.fv("gaze_cmd")[crew] = -MathF.PI * 0.5f;
            float after = 0f;
            for (int i = 0; i < 120; i++)                      // the belief walks out (4 s)
            { Step(world, crew); after += DT; }
            bool back = MathF.Abs(MathUtil.wrap_pi(Gaze(world, crew) - Wheel.centre(slot))) < 0.25f
                        && State(world, crew) != AiGaze.SWEEP;
            float left = Left(world, crew, "tgt", slot);
            for (int i = 0; i < 300; i++)                      // ...and the sighting expires (10 s)
            { Step(world, crew); after += DT; }
            bool ended = State(world, crew) == AiGaze.SWEEP && Left(world, crew, "tgt", slot) <= 0f;
            Line("sighting", $"killed at {t:0.00} s, went back to the bearing {back},"
                             + $" then swept {ended}", engaged && back && ended, ref bad);
            Clean(world, -1);
        }

        // ---- target-first: a belief outranks every wheel -----------------------
        {
            int crew = Crew(world, sx, sz);
            Clean(world, crew);
            int target = world.spawn_unit(Defs.ETYPE_APC, Defs.FACTION_BLUE, sx + 1500f, sz,
                                          -1, crew: false);
            float t = 0f;
            while (t < 4f && State(world, crew) != AiGaze.TARGET)
            {
                world.update(DT); Pin(world, crew); t += DT;
            }
            Flare(world, crew, MathF.PI * 0.5f);                 // fire from the flank
            bool stayed = true;
            for (int i = 0; i < 30; i++)
            {
                Step(world, crew);
                if (State(world, crew) != AiGaze.TARGET) stayed = false;
            }
            Line("target-first", $"shot at from +z while engaged: state stayed TARGET {stayed}",
                 stayed, ref bad);
            Clean(world, -1);
        }

        // ---- datalink-wheel: smoke hides a hull outside the window, a friendly
        // observer keeps the contact live, and the weak bearing turns the head ----
        {
            int crew = Crew(world, sx, sz);
            Clean(world, crew);
            float bearing = MathF.PI, distance = 1500f;
            var (target, _, _) = LinkTarget(world, crew, bearing, distance);
            int slot = Wheel.slot_of(bearing);
            float t = 0f;
            bool turned = false;
            while (t < 5f && e.iv("target")[crew] != target)
            {
                Step(world, crew); t += DT;
                if (State(world, crew) is AiGaze.ALARM or AiGaze.ALARM_TURN) turned = true;
            }
            var (eye, _, _) = Weapons.aim_of(world, crew);
            var at = Body(world, target);
            bool screened = Sight.why(world, eye, at) == "smoke";
            bool linked = Datalink.known(world, Defs.FACTION_RED, target);
            bool only = Visibility.source(world, crew, target, at) == VisibilitySource.Datalink;
            bool acquired = e.iv("target")[crew] == target;
            bool fresh = acquired && e.fv("ai_tgt").get(crew, 6) == 0f;
            bool on = acquired && MathF.Abs(MathUtil.wrap_pi(Gaze(world, crew) - bearing))
                      <= Watch.CONE;
            var tp = e.fv("pos").get3(target);
            var td = Defs.UNIT_DEFS[Defs.ETYPE_APC];
            float dx = tp.x - eye.x, dy = tp.y + td.height * 0.5f - eye.y;
            float dz = tp.z - eye.z;
            float expected = Wheel.TGT_W
                             * ThreatScore.of(Defs.ETYPE_APC, MathF.Sqrt(dx * dx + dy * dy + dz * dz),
                                              Defs.UNIT_DEFS[Defs.ETYPE_MECH].sight)
                             * Sense.DATALINK_THREAT;
            float weight = e.fv("tgt").get(crew, slot * 2 + 1);
            bool weak = MathF.Abs(weight - expected) < 1e-4f;
            Line("link-wheel", $"smoke {screened}, source {(only ? "datalink" : "other")},"
                                + $" turned {turned}, acquired {acquired} at {t:0.00} s,"
                                + $" fresh {fresh}, wheel {weight:0.000}/{expected:0.000}",
                 screened && linked && only && turned && acquired && fresh && on && weak, ref bad);
            Clean(world, -1);
        }

        // ---- datalink-direct: a direct hull in the same bearing replaces a held
        // datalink belief, and its stronger evidence owns their shared wheel slot -
        {
            int crew = Crew(world, sx, sz);
            Clean(world, crew);
            float bearing = 5f * MathF.PI / 180f, direct_bearing = 15f * MathF.PI / 180f;
            float distance = 1500f;
            var (linked, _, _) = LinkTarget(world, crew, bearing, distance);
            float t = 0f;
            while (t < 5f && e.iv("target")[crew] != linked)
            {
                Step(world, crew); t += DT;
            }
            bool linked_first = e.iv("target")[crew] == linked;
            int direct = Hull(world, crew, direct_bearing, distance);
            float switch_t = 0f;
            while (switch_t < 3f && e.iv("target")[crew] != direct)
            {
                Step(world, crew); switch_t += DT;
            }
            bool switched = e.iv("target")[crew] == direct;
            bool visible = Visibility.source(world, crew, direct, Body(world, direct))
                           == VisibilitySource.Direct;
            int slot = Wheel.slot_of(bearing);
            bool same_slot = slot == Wheel.slot_of(direct_bearing);
            var (eye, _, _) = Weapons.aim_of(world, crew);
            var tp = e.fv("pos").get3(direct);
            var td = Defs.UNIT_DEFS[Defs.ETYPE_APC];
            float dx = tp.x - eye.x, dy = tp.y + td.height * 0.5f - eye.y;
            float dz = tp.z - eye.z;
            float expected = Wheel.TGT_W
                             * ThreatScore.of(Defs.ETYPE_APC, MathF.Sqrt(dx * dx + dy * dy + dz * dz),
                                              Defs.UNIT_DEFS[Defs.ETYPE_MECH].sight);
            float weight = e.fv("tgt").get(crew, slot * 2 + 1);
            bool strong = MathF.Abs(weight - expected) < 1e-4f;
            Line("link-direct", $"linked first {linked_first}, direct won {switched} after"
                                 + $" {switch_t:0.00} s, same slot {same_slot},"
                                 + $" wheel {weight:0.000}/{expected:0.000}",
                 linked_first && switched && visible && same_slot && strong, ref bad);
            Clean(world, -1);
        }

        // ---- datalink-memory: the current bit clears with its last observer; the
        // belief walks for Crew.MEMORY instead of receiving a hidden TTL refresh ----
        {
            int crew = Crew(world, sx, sz);
            Clean(world, crew);
            var (target, observer, _) = LinkTarget(world, crew, 0f, 1500f);
            float t = 0f;
            while (t < 5f && e.iv("target")[crew] != target)
            {
                Step(world, crew); t += DT;
            }
            bool acquired = e.iv("target")[crew] == target;
            var remembered = new Vec3(e.fv("ai_tgt").get(crew, 0), e.fv("ai_tgt").get(crew, 1),
                                      e.fv("ai_tgt").get(crew, 2));
            e.destroy(observer);
            var truth = e.fv("pos").get3(target);
            e.fv("pos").set3(target, truth.x + 100f, truth.y, truth.z);
            e.fv("vel").set3(target, 0f, 0f, 0f);
            e.fv("vel_cmd").set3(target, 0f, 0f, 0f);
            float memory_t = 0f;
            while (memory_t < 0.75f)
            {
                Step(world, crew); memory_t += DT;
            }
            bool dropped = !Datalink.known(world, Defs.FACTION_RED, target);
            var walked = new Vec3(e.fv("ai_tgt").get(crew, 0), e.fv("ai_tgt").get(crew, 1),
                                  e.fv("ai_tgt").get(crew, 2));
            bool walking = e.iv("target")[crew] == target && e.fv("ai_tgt").get(crew, 6) >= 0.5f
                           && MathUtil.dist(walked, remembered) < 0.01f;
            float walked_age = e.fv("ai_tgt").get(crew, 6);
            while (memory_t < 3.5f)
            {
                Step(world, crew); memory_t += DT;
            }
            bool forgot = e.iv("target")[crew] < 0 && e.fv("ai_tgt").get(crew, 7) < 0.5f;
            Line("link-memory", $"acquired {acquired}, link dropped {dropped}, walked"
                                 + $" {walking} at {walked_age:0.00} s,"
                                 + $" forgot by {memory_t:0.00} s {forgot}",
                 acquired && dropped && walking && forgot, ref bad);
            Clean(world, -1);
        }

        // ---- foot: a man carries no wheel, and still sweeps --------------------
        {
            int crew = world.spawn_unit(Defs.ETYPE_INF_RIFLE, Defs.FACTION_RED, sx, sz, -1,
                                        extra: new[] { AiTags.ENGAGE }, crew: true);
            Check.aim_east(world, crew);
            Clean(world, crew);
            bool no_wheel = !e.has(crew, "alarm") && !e.has(crew, "laser") && !e.has(crew, "tgt");
            bool sweeps = e.has(crew, "eye");
            for (int i = 0; i < 90; i++) Step(world, crew);
            bool search = State(world, crew) == AiGaze.SWEEP;
            Line("foot", $"rifleman: no wheel {(no_wheel ? "yes" : "NO")}, sweeps {(sweeps ? "yes" : "NO")},"
                         + $" state {(search ? "SWEEP" : "other")}",
                 no_wheel && sweeps && search, ref bad);
            Clean(world, -1);
        }

        // ---- score: the worked example of `ThreatScore` ------------------------
        {
            float sight = Defs.UNIT_DEFS[Defs.ETYPE_MECH].sight;
            float mech = ThreatScore.of(Defs.ETYPE_MECH, 3000f, sight);
            float apc = ThreatScore.of(Defs.ETYPE_APC, 800f, sight);
            float foot = ThreatScore.of(Defs.ETYPE_INF_RIFLE, 200f, sight);
            bool ok = MathF.Abs(mech - 0.400f) < 1e-3f && MathF.Abs(apc - 0.452f) < 1e-3f
                      && MathF.Abs(foot - 0.099f) < 1e-3f && apc > mech && mech > foot;
            Line("score", $"mech@3000 {mech:0.000}, apc@800 {apc:0.000}, rifle@200 {foot:0.000}",
                 ok, ref bad);
        }

        e.validate();
        Console.WriteLine(bad == 0
            ? "gaze: the head answers direct and datalink bearings, and one bearing is one slot"
            : $"gaze: {bad} gap(s) - a head that looks where nothing is, or a wheel that forgets");
        return bad;
    }

    private static void Line(string name, string detail, bool ok, ref int bad)
    {
        Console.WriteLine($"{name,-12}{detail}  {(ok ? "ok" : "GAP")}");
        if (!ok) bad++;
    }

    private static float Gaze(World world, int crew) => world.ecs.fv("gaze_yaw")[crew];

    private static byte State(World world, int crew) => world.ecs.uv("ai_gaze")[crew];

    /// <summary>Column 0 of a wheel slot: what is left of the report.</summary>
    private static float Left(World world, int crew, string comp, int slot) =>
        world.ecs.fv(comp).get(crew, slot * 2);

    /// <summary>Column 1 of the laser wheel: what is left of the alarm (column 0 is
    /// this tick's charge, which the writer resets every tick).</summary>
    private static float LaserLeft(World world, int crew, int slot) =>
        world.ecs.fv("laser").get(crew, slot * 2 + 1);

    /// <summary>Hold the crew still and facing +x: `Front` would otherwise march it at
    /// the enemy base and the sweep's "front half" would move with its legs.</summary>
    private static void Pin(World world, int crew)
    {
        var e = world.ecs;
        e.fv("leg_yaw")[crew] = 0f;
        e.fv("yaw")[crew] = 0f;
        e.fv("vel_cmd").set3(crew, 0f, 0f, 0f);
    }

    private static int Crew(World world, float x, float z)
    {
        // unarmed on purpose: these rows are about looking, and a designator this crew
        // started shooting at is a beam that keeps getting interrupted
        int eid = world.spawn_unit(Defs.ETYPE_MECH, Defs.FACTION_RED, x, z, -1,
                                   weapons: new[] { Defs.WID_NONE, Defs.WID_NONE },
                                   extra: new[] { AiTags.ENGAGE }, crew: true);
        Check.aim_east(world, eid);
        return eid;
    }

    /// <summary>A round that goes past the crew at arm's length from `bearing`: it is a
    /// near miss, 12 m off the hull and level with its shoulder, so `Impact` never
    /// destroys it before this row reads the leg.  Its reverse velocity is `bearing`,
    /// which is the direction the crew has to turn to.</summary>
    private static void Flare(World world, int crew, float bearing)
    {
        var e = world.ecs;
        var p = e.fv("pos").get3(crew);
        float cx = MathF.Cos(bearing), cz = MathF.Sin(bearing);
        world.spawn_round(-1, Defs.FACTION_BLUE, Defs.WID_AC30, 0,
                          new Vec3(p.x + cx * 60f - cz * 12f, p.y + 3f,
                                   p.z + cz * 60f + cx * 12f),
                          new Vec3(-cx * 900f, 0f, -cz * 900f), 2f, -1);
    }

    /// <summary>A machine that paints the crew from `bearing`, 900 m out.  A mech: the
    /// type carries the laser whether or not a launcher is fitted, and the sight is the
    /// one line that always burns.</summary>
    private static int Painter(World world, int crew, float bearing)
    {
        var e = world.ecs;
        var p = e.fv("pos").get3(crew);
        int eid = world.spawn_unit(Defs.ETYPE_MECH, Defs.FACTION_BLUE,
                                   p.x + MathF.Cos(bearing) * 900f,
                                   p.z + MathF.Sin(bearing) * 900f, -1, crew: false);
        Lay(world, eid, crew);
        return eid;
    }

    /// <summary>Lay a designator's sight on the crew's body, from the eye it looks out
    /// of - the same rule `BeamCheck` drives the receiver with.  Re-laid every tick,
    /// because a mech's eye rides the hull and `Gunnery` turns that hull after it.</summary>
    private static void Lay(World world, int painter, int crew)
    {
        var e = world.ecs;
        var (eye, _, _) = Weapons.aim_of(world, painter);
        var p = e.fv("pos").get3(crew);
        float dy = p.y + Defs.UNIT_DEFS[e.uv("etype")[crew]].height * 0.5f - eye.y;
        float dx = p.x - eye.x, dz = p.z - eye.z;
        var sy = e.row_f(painter, "sight_yaw", out int r1);
        var sp = e.row_f(painter, "sight_pitch", out int r2);
        sy[r1] = MathF.Atan2(dz, dx);
        sp[r2] = MathF.Atan2(dy, MathF.Sqrt(dx * dx + dz * dz));
    }

    /// <summary>One tick of a scenario: the range repopulates its posts three seconds
    /// after a kill, so anything the row is not about is taken off the field before
    /// every tick - a stray hulk becomes a target, and a target outranks the wheel,
    /// which is exactly what these rows are measuring.</summary>
    private static void Step(World world, int crew)
    {
        world.update(DT);
        Pin(world, crew);
    }

    /// <summary>A stationary hostile APC on a bearing from the crew.</summary>
    private static int Hull(World world, int crew, float bearing, float distance)
    {
        var p = world.ecs.fv("pos").get3(crew);
        return world.spawn_unit(Defs.ETYPE_APC, Defs.FACTION_BLUE,
                                p.x + MathF.Cos(bearing) * distance,
                                p.z + MathF.Sin(bearing) * distance, -1,
                                weapons: new[] { Defs.WID_NONE, Defs.WID_NONE }, crew: false);
    }

    /// <summary>The 60-percent body point every AI visibility query asks about.</summary>
    private static Vec3 Body(World world, int target)
    {
        var e = world.ecs;
        var p = e.fv("pos").get3(target);
        float height = Defs.UNIT_DEFS[e.uv("etype")[target]].height;
        return new Vec3(p.x, p.y + height * 0.6f, p.z);
    }

    /// <summary>A live faction contact hidden from the crew by one real smoke puff.
    /// The friendly observer stands beside the hull, beyond the screen, and rebuilds
    /// `link_seen` on every tick until the row destroys it.</summary>
    private static (int target, int observer, int puff) LinkTarget(World world, int crew,
                                                                   float bearing, float distance)
    {
        var e = world.ecs;
        int target = Hull(world, crew, bearing, distance);
        var tp = e.fv("pos").get3(target);
        float cx = MathF.Cos(bearing), cz = MathF.Sin(bearing);
        int observer = world.spawn_unit(Defs.ETYPE_MECH, Defs.FACTION_RED,
                                        tp.x - cz * 100f, tp.z + cx * 100f, -1,
                                        weapons: new[] { Defs.WID_NONE, Defs.WID_NONE },
                                        crew: false);
        Lay(world, observer, target);
        var (eye, _, _) = Weapons.aim_of(world, crew);
        var at = Body(world, target);
        var mid = new Vec3((eye.x + at.x) * 0.5f, (eye.y + at.y) * 0.5f,
                           (eye.z + at.z) * 0.5f);
        int puff = Puff(world, mid, Defs.SMOKE_FLAT[0]);
        return (target, observer, puff);
    }

    /// <summary>One deployed puff, with the same component set as a real smoke charge.</summary>
    private static int Puff(World world, Vec3 at, float radius)
    {
        var e = world.ecs;
        int sid = e.create(Components.PUFF_COMPS);
        e.fv("pos").set3(sid, at);
        e.fv("smoke_r").set3(sid, radius, radius * 1.1f, radius);
        e.fv("smoke_t")[sid] = Defs.SMOKE_LIFE;
        e.fv("smoke_s")[sid] = 1f;
        return sid;
    }

    private static void Clean(World world, params int[] keep)
    {
        var e = world.ecs;
        // every entity, not `world.units()`: a designator spawned as scenery is not a
        // "unit", and a survivor of the last row keeps its sight on this one
        foreach (int id in e.query())
        {
            if (Array.IndexOf(keep, id) >= 0) continue;
            if (e.has(id, "smoke")) { e.destroy(id); continue; }
            if (!e.has(id, "etype")) continue;
            if (e.uv("etype")[id] == Defs.ETYPE_BASE) continue;
            e.destroy(id);
        }
        foreach (int id in world.projectiles()) e.destroy(id);
    }

    /// <summary>Open, flat ground with 1.6 km of clear line along +x, -x, +z and -z: a
    /// target dead ahead, a machine painting from astern, and two designators on
    /// opposite bearings, all from the same spot.</summary>
    private static (float x, float z) Spot(World world)
    {
        var t = world.terrain;
        var e = world.ecs;
        for (float x = 1700f; x < t.w * Terrain.TILE - 1700f; x += 200f)
        {
            for (float z = 700f; z < t.d * Terrain.TILE - 700f; z += 200f)
            {
                float h = t.height_at(x, z);
                bool open = true;
                // no command post within 2 km: a body between the crew and a painter
                // reads as a blocked laser (`Beams.body_hit`)
                foreach (var kv in world.base_entity)
                {
                    int id = kv.Value;
                    if (!e.is_alive(id)) continue;
                    var p = e.fv("pos").get3(id);
                    float dx = p.x - x, dz = p.z - z;
                    if (dx * dx + dz * dz < 1100f * 1100f) { open = false; break; }
                }
                // both directions along x: a target dead ahead and a machine painting
                // from astern share the one open lane the range map offers
                for (int s = -1; s <= 1 && open; s += 2)
                {
                    for (float d = 0f; d <= 1600f && open; d += 100f)
                    {
                        float px = x + d * s;
                        if (t.blocked(px, z)) { open = false; break; }
                        if (MathF.Abs(t.height_at(px, z) - h) > 6f) { open = false; break; }
                        if (!t.los(new Vec3(x, h + Defs.MECH_EYE, z),
                                   new Vec3(px, t.height_at(px, z) + 1.4f, z), 12f))
                        { open = false; break; }
                    }
                }
                if (open) return (x, z);
            }
        }
        return (1200f, 1200f);
    }
}
