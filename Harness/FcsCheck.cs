// `--check fcs`: does a crew's gun land rounds where the client's law would?
//
// The unit tests (`tests/`) prove the two hosts solve the same function.  This check
// proves the *chains* around it agree: a real `World`, a real crew (`Sense` ->
// `Engage` -> `Weapons.fire` -> the round's own flight), a real pilot's mech fed the
// same law the way the browser feeds it (`in_yaw` / `in_pitch`), and a scripted
// target crossing the range at a known speed.  What is measured is the thing that
// matters to a player: how close the rounds come to the hull they were aimed at.
//
//   ./dotnet.sh run -- --check fcs
//
// Every scenario is fired twice, once through the crew and once through the pilot,
// because the two differ in exactly two settings of one law: **Z** (a crew zeroes on
// the range of the point it is engaging, a pilot on the dial he set) and which arm
// the sight follows (`Defs.AI_MASTER_ARM`).  A scenario where the crew is an order of
// magnitude worse than the pilot is the generation gap this check exists to catch.
//
// Dispersion is *on* here on purpose: this is the integration layer, and a spread of
// 0.0011 rad at 1 km is 1.1 m of the answer.  The unit tests keep it out.

using Mech.Server.Systems;
using Mech.Server.Systems.AI;

namespace Mech.Server.Harness;

public static class FcsCheck
{
    private const float DT = 1f / 30f;
    /// <summary>s of simulated time per shot: enough for the crew to pick the target
    /// up, settle its mount and put a burst out.</summary>
    private const float WINDOW = 7f;
    /// <summary>m; the pilot fires once the bore is this close to the solution.</summary>
    private const float PILOT_SETTLED = 0.01f;
    /// <summary>The pilot's sight setting: `Defs.ZERO_DEFAULT`, and what `Check.drive`
    /// uses, so a pilot's answers here are the ones a player's are.</summary>
    private const float PILOT_ZERO = 1000f;

    private sealed record Scenario(string Name, int Weapon, int Target, float Range, float Speed);

    /// <summary>Where the round's clearance is measured: the hull's geometric centre,
    /// the same aim height the solver is given.</summary>
    private static float AimHeight(int etype) => Defs.UNIT_DEFS[etype].height * 0.5f;

    public static int run()
    {
        var t0 = DateTime.UtcNow;
        // the range: 4 km of plains, made for measuring a gun.  Every hull it spawns
        // (its targets, the defence post) is swept away each tick, so the field holds
        // exactly the two hulls a scenario is about
        var world = new World(seed: 7, mode: "range");
        Console.WriteLine($"fcs check: {world.terrain.w} x {world.terrain.d} tiles, "
                          + $"master arm {Defs.AI_MASTER_ARM}, built in "
                          + $"{(DateTime.UtcNow - t0).TotalMilliseconds:0} ms");
        // the check owns the field: the range's own fixtures are targets too, and a
        // crew that picked one of them up would be measured against the wrong hull
        foreach (int id in world.units()) world.ecs.destroy(id);
        // a flat stretch of the range, far from its own fixtures
        var (sx, sz) = FlatSpot(world);
        Console.WriteLine($"firing line at ({sx:0}, {sz:0}), ground {world.terrain.height_at(sx, sz):0.0} m\n");

        var scenarios = new List<Scenario>
        {
            new("ac30 vs APC 800 crossing 12", Defs.WID_AC30, Defs.ETYPE_APC, 800f, 12f),
            new("ac30 vs APC 1600 crossing 12", Defs.WID_AC30, Defs.ETYPE_APC, 1600f, 12f),
            new("ac30 vs MECH 1200 crossing 25", Defs.WID_AC30, Defs.ETYPE_MECH, 1200f, 25f),
            new("ac30 vs APC 1200 standing", Defs.WID_AC30, Defs.ETYPE_APC, 1200f, 0f),
            new("ac30 + pod vs APC 1200 crossing 12", Defs.WID_ROCKET, Defs.ETYPE_APC, 1200f, 12f),
            new("105 mm vs APC 1200 crossing 12", Defs.WID_G105, Defs.ETYPE_APC, 1200f, 12f),
        };

        int bad = 0;
        Console.WriteLine("scenario                                     crew              pilot");
        foreach (var s in scenarios)
        {
            var crew = Shoot(world, sx, sz, s, pilot: false);
            var pilot = Shoot(world, sx, sz, s, pilot: true);
            string verdict = CrewBehind(crew, pilot) ? "GAP" : "ok";
            if (verdict == "GAP") bad++;
            Console.WriteLine($"{s.Name,-44} {Show(crew),14} {Show(pilot),16}  {verdict}");
        }
        Console.WriteLine(bad == 0
            ? "\nfcs: the crew and the pilot agree on every scenario"
            : $"\nfcs: {bad} scenario(s) where the crew is behind the pilot by more than 2 m");
        return bad == 0 ? 0 : 1;
    }

    /// <summary>Destroy everything but the two hulls this scenario is about.  The
    /// command bases are left alone: they are part of the mode, not of the field.</summary>
    private static void Sweep(World world, int keepA, int keepB)
    {
        var e = world.ecs;
        foreach (int id in world.units())
        {
            if (id == keepA || id == keepB) continue;
            if (e.uv("etype")[id] == Defs.ETYPE_BASE) continue;
            e.destroy(id);
        }
    }

    /// <summary>Is this id still the hull it was, and not the wreck its id was handed
    /// to?  An id is an address, not an identity - the archetype is the identity.</summary>
    private static bool IsHull(ECS e, int id)
    {
        var a = e.arch_of(id);
        return a is not null && e.is_alive(id) && a.keyset.Contains("unit");
    }

    /// <summary>A firing line that can see 3 km down +x.
    ///
    /// A crew only engages what its lookout can see, so a scenario at 2 km on a range
    /// with a ridge in the way would report "no rounds" and mean nothing.  The spot
    /// has to be open along the whole corridor the crossing target travels.</summary>
    private static (float x, float z) FlatSpot(World world)
    {
        var t = world.terrain;
        float best = float.MaxValue, bx = 3000f, bz = 5000f;
        const float reach = 1800f;                 // the longest scenario, plus the crossing
        for (float x = 1200f; x < t.w * 16f - reach - 400f; x += 300f)
        {
            for (float z = 1200f; z < t.d * 16f - 800f; z += 300f)
            {
                float h = t.height_at(x, z);
                bool open = true;
                for (float d = 0f; d <= reach && open; d += 100f)
                {
                    for (float off = -100f; off <= 400f; off += 100f)
                    {
                        float px = x + d, pz = z + off;
                        if (t.blocked(px, pz)) { open = false; break; }
                        if (MathF.Abs(t.height_at(px, pz) - h) > 6f) { open = false; break; }
                        if (!t.los(new Vec3(x, h + Defs.MECH_EYE, z),
                                   new Vec3(px, t.height_at(px, pz) + 1.4f, pz), 12f))
                        { open = false; break; }
                    }
                }
                if (!open) continue;
                float span = 0f;
                for (float d = 0f; d <= reach; d += 100f)
                    span = MathF.Max(span, MathF.Abs(t.height_at(x + d, z) - h));
                if (span < best) { best = span; bx = x; bz = z; }
            }
        }
        return (bx, bz);
    }

    /// <summary>One shot, one way: the median clearance of the rounds fired (m) and
    /// whether the hull was killed outright.</summary>
    private readonly record struct ShotResult(float Miss, bool Killed, int Rounds);

    private static string Show(in ShotResult r) => r.Rounds == 0
        ? "no rounds"
        : $"{r.Miss,6:0.00} m{(r.Killed ? " KILL" : "")}";

    private static bool CrewBehind(in ShotResult crew, in ShotResult pilot)
    {
        // a kill is 0 m of clearance: nothing can be worse than nothing fired, so a
        // scenario where one side never got a round away is a gap on its own
        if (crew.Rounds == 0 && pilot.Rounds > 0) return true;
        if (pilot.Rounds == 0) return false;
        return crew.Miss > pilot.Miss + 2f;
    }

    /// <summary>One shot, one way.</summary>
    private static ShotResult Shoot(World world, float sx, float sz, in Scenario s, bool pilot)
    {
        var e = world.ecs;
        // the shooter: a crew mech, or the same machine with a pilot in it, and the
        // same pair of arms either way so the two halves differ in nothing but the
        // fire control
        int[] arms = { s.Weapon, Defs.WID_AC30 };
        int shooter = world.spawn_unit(Defs.ETYPE_MECH, Defs.FACTION_RED, sx, sz, -1,
                                       weapons: arms,
                                       extra: pilot ? null : new[] { AiTags.ENGAGE },
                                       crew: !pilot);
        // the target: a hull that is *driven* by this harness, so its path is known
        int target = world.spawn_unit(s.Target, Defs.FACTION_BLUE, sx + s.Range, sz, -1,
                                      crew: false);
        Player? pl = null;
        if (pilot)
        {
            // `add_player` builds and spawns this pilot's machine itself; it is the
            // shooter, so the crew hull above is not wanted
            e.destroy(shooter);
            pl = world.add_player("FCS");
            shooter = pl.mech;
            e.fv("pos").set3(shooter, sx, world.terrain.height_at(sx, sz), sz);
            e.fv("vel").set3(shooter, 0f, 0f, 0f);
            e.fv("yaw")[shooter] = 0f;
            e.fv("sight_yaw")[shooter] = 0f;
            pl.zero_range = PILOT_ZERO;
            // `spawn_mech` hands a fresh pilot `Defs.DEFAULT_LOADOUT`; the scenario
            // wants its own pair in the machine so the two halves fire the same gun
            e.uv("weapon").set(shooter, 0, (byte)arms[0]);
            e.uv("weapon").set(shooter, 1, (byte)arms[1]);
        }

        // `t` at the moment the round left: the target keeps moving across ticks, so
        // a measurement that starts its clock at zero would be looking at a hull that
        // has not arrived yet
        var rounds = new List<(Vec3 at, Vec3 vel, int wid, float t)>();
        var seen = new HashSet<int>();
        bool killed = false;
        float t = 0f;
        for (int tick = 0; tick < (int)(WINDOW / DT); tick++)
        {
            t += DT;
            // the field belongs to this check: the range respawns its own fixtures
            // (targets, the defence post) and a crew would acquire one of them, which
            // would be measured against the wrong hull
            Sweep(world, shooter, target);
            // a target that has been destroyed stops the scenario.  The archetype is
            // the test rather than `is_alive`: the hull's id is recycled by the wreck
            // it leaves, and a wreck is alive with no `vel` to script
            if (!IsHull(e, target)) { killed = true; break; }
            // script the target: a straight crossing at `Speed`, whatever the world does
            var tp = e.fv("pos");
            tp.set3(target, sx + s.Range, world.terrain.height_at(sx + s.Range, sz + s.Speed * t),
                    sz + s.Speed * t);
            e.fv("vel").set3(target, 0f, 0f, s.Speed);
            if (pilot) PilotAim(world, pl!, shooter, target, s, arm: 0);
            world.update(DT);
            // every round this shooter has just let go of
            foreach (int id in world.projectiles())
            {
                if (seen.Contains(id)) continue;
                if (e.iv("proj_src").get(id, 0) != shooter) continue;
                if (e.fv("proj_age")[id] > DT) continue;
                seen.Add(id);
                rounds.Add((e.fv("pos").get3(id), e.fv("vel").get3(id),
                            e.uv("proj_wid")[id], t));
            }
            if (t > WINDOW - 1f && rounds.Count > 0) break;
        }

        // measure: where each round's arc passes the target's own path
        var misses = new List<float>();
        foreach (var (at, vel, wid, t0) in rounds)
        {
            var wd = Defs.WEAPONS[wid];
            // direct fire only: a guided round is a different question (it chases the
            // sight, so its own flight is the verdict)
            if (wd.kind is not ("bullet" or "rocket")) continue;
            misses.Add(Clearance(world, wd, at, vel, sx + s.Range, sz + s.Speed * t0,
                                 s.Speed, AimHeight(s.Target)));
        }
        if (pl is not null) world.remove_player(pl.pid);
        e.destroy(shooter);
        e.destroy(target);
        if (misses.Count == 0) return new ShotResult(float.NaN, false, 0);
        misses.Sort();
        return new ShotResult(misses[misses.Count / 2], killed, misses.Count);
    }

    /// <summary>The pilot's half: the client's law, solved every tick and written into
    /// the intent the way the browser writes it, then the trigger pulled once the
    /// machine has caught up (the client's own aim assist does exactly this, by
    /// snapping the sight to the ring the pilot picked).</summary>
    private static void PilotAim(World world, Player pl, int shooter, int target, in Scenario s,
                                 int arm)
    {
        var e = world.ecs;
        var wd = Defs.WEAPONS[e.uv("weapon").get(shooter, arm)];
        if (wd.kind == "none") return;
        var eye = Weapons.aim_of(world, shooter).origin;
        var muzzle = Weapons.launch_geometry(world, shooter, arm, wd,
                                             e.fv("yaw")[shooter], e.fv("pitch")[shooter],
                                             out _, out _, out _);
        var tp = e.fv("pos").get3(target);
        var track = new FireControl.Track(tp, e.fv("vel").get3(target), default,
                                          Defs.UNIT_DEFS[s.Target].height * 0.5f,
                                          MathUtil.max_radius(s.Target));
        var sol = FireControl.solve(new FireControl.Shot(muzzle, eye, wd, pl.zero_range), track);
        if (!sol.valid) return;
        pl.in_yaw = sol.yaw;
        pl.in_pitch = sol.pitch;
        var (byaw, bpitch) = Weapons.bore_of(world, shooter);
        bool settled = MathF.Abs(MathUtil.wrap_pi(sol.yaw - byaw)) < PILOT_SETTLED
                       && MathF.Abs(sol.pitch - bpitch) < PILOT_SETTLED;
        pl.fire = (settled, false);
    }

    /// <summary>The round's closest approach to a hull crossing at `speed`, in the
    /// ground plane's z, over its whole flight (m).</summary>
    private static float Clearance(World world, WeaponDef wd, Vec3 at, Vec3 vel,
                                   float x, float z0, float speed, float aim_h)
    {
        float vx = vel.x, vy = vel.y, vz = vel.z;
        var p = at;
        float best = float.PositiveInfinity;
        for (float t = 0f; t < wd.life; t += DT)
        {
            if (wd.thrust > 0f)
            {
                float sp = MathF.Sqrt(vx * vx + vy * vy + vz * vz);
                float k = MathF.Min(wd.maxvel, sp + wd.thrust * DT) / MathF.Max(sp, 1e-6f);
                vx *= k; vy *= k; vz *= k;
            }
            vy -= wd.g * DT;
            var n = new Vec3(p.x + vx * DT, p.y + vy * DT, p.z + vz * DT);
            // the target's own leg over the same tick
            var a = new Vec3(x, world.terrain.height_at(x, z0 + speed * t) + aim_h, z0 + speed * t);
            var b = new Vec3(x, world.terrain.height_at(x, z0 + speed * (t + DT)) + aim_h,
                             z0 + speed * (t + DT));
            float rx = p.x - a.x, ry = p.y - a.y, rz = p.z - a.z;
            float wx = (n.x - p.x) - (b.x - a.x), wy = (n.y - p.y) - (b.y - a.y);
            float wz = (n.z - p.z) - (b.z - a.z);
            float ww = wx * wx + wy * wy + wz * wz;
            float u = ww < 1e-9f ? 0f : Math.Clamp(-(rx * wx + ry * wy + rz * wz) / ww, 0f, 1f);
            float cx = rx + wx * u, cy = ry + wy * u, cz = rz + wz * u;
            best = MathF.Min(best, MathF.Sqrt(cx * cx + cy * cy + cz * cz));
            if (n.y < world.terrain.height_at(n.x, n.z) - 5f) break;
            p = n;
        }
        return best;
    }
}
