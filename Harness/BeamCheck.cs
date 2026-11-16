// `--check beam`: what a laser-guided round is worth, and what takes it away.
//
// The range, not the battle, for the reason every other round is measured there:
// two dozen crews firing at once turn a hit rate into a measurement of where the
// pilot's hull happened to be.  Here there is one launcher, one target crossing
// at a known speed, and one row per thing that can cut the beam:
//
//   clear    the baseline - a hull crossing at 30 m/s, no cover anywhere
//   blind    a stand of trees short of the *target*: the launcher has lost sight
//            of it and the round has not, so the beam flies the channel's memory
//            and the round should still arrive
//   laser    trees between the launcher and the *round*: a beam is only light, so
//            the round coasts on the heading it had
//   kill     the launcher is destroyed mid-flight: the round coasts
//   window   the sight is turned 70 deg off the target: a laser cannot be pointed
//            outside the sight's window, so the channel goes and the round coasts
//   lock     the pilot's own path, through `PlayerInput` and `Locking`: the
//            trigger does nothing without a lock, and a locked round arrives
//   channels four targets, four rounds, four lines at once - and a fifth target
//            displacing the oldest
//
// Every row prints the closest approach of its rounds to the target's centre and
// how many of them were riding a live beam when the row did its damage, so a row
// that hits because nothing was ever cut reads differently from one that hits
// through cover.
//
//   ./dotnet.sh run -- --check beam

using Mech.Server;
using Mech.Server.Systems;

namespace Mech.Server.Harness;

public static class BeamCheck
{
    private const float DT = 1f / 30f;
    private const int ROUNDS = 4;            // launches per row
    private const float FIRST_SHOT = 0.5f;   // s into the row
    private const float GAP = 3.0f;          // s between launches
    private const float CUT_AT = 1.0f;       // s after a launch that the row does its damage
    private const float HIT_R = 2.0f;        // m: a warhead this close to the hull's centre is a hit

    // the range's open plain, a kilometre south of the missile-defence post so the
    // post's gatling stays out of the measurement
    private static readonly (float x, float z) LAUNCHER = (2000f, 1200f);

    private enum Cut { None, Blind, Laser, Launcher, Window }

    /// <summary>Once a row has turned the sight away, the puppet launcher stops
    /// aiming: an operator who has looked off the target is not still tracking it.</summary>
    private static bool _freeze;

    private sealed class Row_
    {
        public int fired, hits, guided;
        public float miss = -1f;
        public string note = "";
    }

    public static int run()
    {
        Console.WriteLine("row        dist  fired  hits  guided  miss m  note");
        Print("clear", 800f, One(800f, Cut.None));
        Print("clear", 1600f, One(1600f, Cut.None));
        Print("blind", 800f, One(800f, Cut.Blind));
        Print("laser", 800f, One(800f, Cut.Laser));
        Print("kill", 800f, One(800f, Cut.Launcher));
        Print("window", 800f, One(800f, Cut.Window));
        Pilot();
        Channels();
        return 0;
    }

    private static void Print(string name, float dist, Row_ r) =>
        Console.WriteLine($"{name,-8} {dist,6:0}  {r.fired,5}  {r.hits,4}  {r.guided,6}  "
                          + $"{r.miss,6:0.0}  {r.note}");

    // ---- one launcher, one crossing target ---------------------------------

    /// <summary>Launch `ROUNDS` beam riders at a hull crossing the line of sight,
    /// cut the beam the way `cut` says one second into each flight, and measure
    /// where each warhead went off relative to the hull it was after.</summary>
    private static Row_ One(float dist, Cut cut)
    {
        var res = new Row_();
        _freeze = false;
        var world = new World(7, "range");
        var e = world.ecs;
        var (lx, lz) = LAUNCHER;
        int shooter = world.spawn_unit(Defs.ETYPE_MECH, Defs.FACTION_RED, lx, lz,
                                       weapons: new[] { Defs.WID_AC30, Defs.WID_SACLOS },
                                       crew: false);
        // deep enough in hp that a hit does not end the row: the measurement is the
        // closest approach, not a kill
        int target = world.spawn_unit(Defs.ETYPE_MECH, Defs.FACTION_BLUE, lx + dist, lz,
                                      hp_scale: 200f, crew: false);
        var wd = Defs.WEAPONS[Defs.WID_SACLOS];
        var pos = e.fv("pos");
        float best = -1f, blindAt = -1f;
        bool blind_now = false;
        int _fired = 0, bursts = 0;
        float next = FIRST_SHOT, cut_at = float.MaxValue;
        float end = FIRST_SHOT + GAP * ROUNDS + 14f;
        // three seconds of lead-in: the crossing hull is at its cruise speed before
        // the first round goes, so a row measures the guidance and not a launch ramp
        const float PRE = 3f;

        for (int t = 0; (float)t * DT < PRE + end; t++)
        {
            float now = t * DT - PRE;
            _drive(world, shooter, target);
            if (now < 0f)
            {
                world.update(DT);
                continue;
            }

            if (now >= next && _fired < ROUNDS && e.is_alive(shooter) && e.is_alive(target))
            {
                next += GAP;
                _fired++;
                int row = e.row_of(shooter);
                var a = e.arch_of(shooter)!;
                a.i("mag")[row * 2 + 1] = Math.Max(1, a.i("mag")[row * 2 + 1]);
                Weapons.fire(world, shooter, 1, Defs.WID_SACLOS, wd, target);
                a.i("mag")[row * 2 + 1] -= 1;
                res.fired++;
                // the row's damage lands `CUT_AT` seconds into the flight; the
                // `Cut.None` row samples the same instant so its `guided` column is
                // the same measurement
                cut_at = now + CUT_AT;
            }

            world.update(DT);

            // the row's own damage, one second into the flight
            if (now >= cut_at)
            {
                cut_at = float.MaxValue;
                if (cut != Cut.None) _cut(world, cut, shooter, lx, lz, target);
                if (cut == Cut.Blind) blindAt = now + 0.1f;
                // was the round this cut belongs to still on a live beam?
                res.guided += _lit_count(world, shooter, CUT_AT + 0.2f);
            }

            // where each warhead went off, against the hull it was after.  A round
            // covers 20 m between two snapshots, so a position sample is a 20 m
            // error floor; the burst point is exact, and it is the number that says
            // whether the steering worked
            var tp = pos.get3(target);
            float tc = tp.y + Defs.UNIT_DEFS[e.uv("etype")[target]].height * 0.5f;
            foreach (var ev in world.events)
            {
                if (!ev.TryGetValue("k", out var kv) || (string?)kv != "boom") continue;
                float bx = (float)ev["x"]!, by = (float)ev["y"]!, bz = (float)ev["z"]!;
                float d = MathF.Sqrt((bx - tp.x) * (bx - tp.x) + (by - tc) * (by - tc)
                                     + (bz - tp.z) * (bz - tp.z));
                if (best < 0f || d < best) best = d;
                if (d < HIT_R) res.hits++;
                bursts++;
            }
            world.events.Clear();
            if (blindAt >= 0f && now >= blindAt)
            {
                blindAt = -1f;
                blind_now = e.fv("beam_seen").get(shooter, 0) > 0f;
            }
        }

        res.miss = best;
        res.note = $"{bursts} bursts  {_channel_state(world, shooter)}";
        if (cut == Cut.Blind)
            res.note += blind_now ? "  target hidden at the cut" : "  TARGET STILL VISIBLE";
        world.ecs.validate();
        return res;
    }

    /// <summary>The puppets: the target cruises across at 30 m/s, the launcher keeps
    /// its bore on it as an operator would (the lead is the beam's job).</summary>
    private static void _drive(World world, int shooter, int target)
    {
        var e = world.ecs;
        if (_hull(e, target))
        {
            e.fv("vel_cmd").set3(target, 0f, 0f, 30f);
            e.fv("leg_yaw")[target] = MathF.PI * 0.5f;
        }
        if (_freeze || !_hull(e, shooter) || !_hull(e, target)) return;
        var p = e.fv("pos").get3(shooter);
        var t = e.fv("pos").get3(target);
        float dx = t.x - p.x, dz = t.z - p.z;
        float dy = t.y + Defs.UNIT_DEFS[e.uv("etype")[target]].height * 0.5f
                   - (p.y + Defs.MECH_EYE);
        float yaw = MathF.Atan2(dz, dx);
        float pitch = MathF.Atan2(dy, MathF.Sqrt(dx * dx + dz * dz));
        e.fv("yaw")[shooter] = yaw;
        e.fv("sight_yaw")[shooter] = yaw;
        e.fv("pitch")[shooter] = pitch;
        e.fv("sight_pitch")[shooter] = pitch;
    }

    /// <summary>A deployed screen: the entity `Systems/Smoke.cs` leaves when a
    /// canister bursts, so the harness can put one where it wants without waiting
    /// for a hull to throw it.</summary>
    private static void _puff(World world, Vec3 at, float radius)
    {
        var e = world.ecs;
        int sid = e.create(Components.PUFF_COMPS);
        e.fv("pos").set3(sid, at);
        e.fv("smoke_r").set3(sid, radius, radius * 1.1f, radius);
        e.fv("smoke_t")[sid] = 5f;
        e.fv("smoke_s")[sid] = 1f;
    }

    /// <summary>Whether this launcher holds at least one channel.</summary>
    private static bool _locked(World world, int eid)
    {
        var e = world.ecs;
        if (!e.has(eid, "beam_tgt")) return false;
        for (int ch = 0; ch < Defs.BEAM_CHANNELS; ch++)
            if (e.iv("beam_tgt").get(eid, ch) >= 0) return true;
        return false;
    }

    /// <summary>Metres from a point to the leg `a` -> `b`, in three dimensions.</summary>
    private static float _seg_dist(Vec3 a, Vec3 b, Vec3 p)
    {
        float wx = b.x - a.x, wy = b.y - a.y, wz = b.z - a.z;
        float len2 = wx * wx + wy * wy + wz * wz;
        float t = 0f;
        if (len2 > 1e-9f)
            t = Math.Clamp(((p.x - a.x) * wx + (p.y - a.y) * wy + (p.z - a.z) * wz) / len2, 0f, 1f);
        float dx = a.x + wx * t - p.x, dy = a.y + wy * t - p.y, dz = a.z + wz * t - p.z;
        return MathF.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    /// <summary>An id that is still a hull: `is_alive` answers "the id exists", and
    /// the blast a dead one leaves behind holds the id for a tick.</summary>
    private static bool _hull(ECS e, int eid) => e.is_alive(eid) && e.has(eid, "unit");

    private static void _cut(World world, Cut cut, int shooter, float lx, float lz, int target)
    {
        var e = world.ecs;
        var tp = e.fv("pos").get3(target);
        switch (cut)
        {
            case Cut.None:
                break;
            case Cut.Blind:
                // a screen a little short of the target: the launcher has lost
                // sight of the hull, and the round - which by now is nearer than
                // the screen - is still on the beam.  Smoke rather than a stand of
                // trees on purpose: a screen hides and stops nothing, which is the
                // case the memory exists for
                _puff(world, new Vec3(lx + (tp.x - lx) * 0.9f, tp.y + 6f,
                                      lz + (tp.z - lz) * 0.9f), 45f);
                break;
            case Cut.Laser:
            {
                // 60 m out, between the launcher and its own round
                int ch = _first_channel(world, shooter);
                if (ch < 0) break;
                var mem = e.fv("beam_mem");
                float sx = e.fv("pos").get3(shooter).x, sz = e.fv("pos").get3(shooter).z;
                float ax = mem.get(shooter, ch * 3) - sx, az = mem.get(shooter, ch * 3 + 2) - sz;
                float n = MathF.Max(1e-3f, MathF.Sqrt(ax * ax + az * az));
                _puff(world, new Vec3(sx + ax / n * 60f, e.fv("pos").get3(shooter).y + 8f,
                                      sz + az / n * 60f), 45f);
                break;
            }
            case Cut.Launcher:
                Combat.apply(world, shooter, 0, 9999f, default);
                break;
            case Cut.Window:
                // the pilot looks away: outside the sight's window the channel goes
                _freeze = true;
                e.fv("sight_yaw")[shooter] = e.fv("sight_yaw")[shooter] + 1.2f;
                break;
        }
    }

    /// <summary>How many rounds of this launcher are riding a live beam right now,
    /// counting only the ones no older than `max_age`.</summary>
    private static int _lit_count(World world, int shooter, float max_age = 1e9f)
    {
        var e = world.ecs;
        if (!_hull(e, shooter) || !e.has(shooter, "beam_tgt")) return 0;
        int n = 0;
        foreach (int pid in world.projectiles())
        {
            if (e.iv("proj_src")[pid] != shooter || !e.has(pid, "beam_ch")) continue;
            if (e.fv("proj_age")[pid] > max_age) continue;
            int ch = e.uv("beam_ch")[pid];
            if (ch >= Defs.BEAM_CHANNELS) continue;
            if (e.iv("beam_tgt").get(shooter, ch) < 0) continue;
            if (e.uv("beam_ok").get(shooter, ch) == 0) continue;
            var (origin, _, _) = Weapons.aim_of(world, shooter);
            if (!Sight.clear(world, origin, e.fv("pos").get3(pid), Sight.LOS_STEP)) continue;
            n++;
        }
        return n;
    }

    private static string _channel_state(World world, int shooter)
    {
        var e = world.ecs;
        if (!e.has(shooter, "beam_tgt")) return "no designator";
        int n = 0, lit = 0;
        for (int ch = 0; ch < Defs.BEAM_CHANNELS; ch++)
        {
            if (e.iv("beam_tgt").get(shooter, ch) < 0) continue;
            n++;
            if (e.uv("beam_ok").get(shooter, ch) != 0) lit++;
        }
        return $"channels {n}/{lit} lit";
    }

    private static int _first_channel(World world, int shooter)
    {
        var e = world.ecs;
        if (!e.has(shooter, "beam_tgt")) return -1;
        for (int ch = 0; ch < Defs.BEAM_CHANNELS; ch++)
            if (e.iv("beam_tgt").get(shooter, ch) >= 0) return ch;
        return -1;
    }

    // ---- the pilot's own path ----------------------------------------------

    /// <summary>Everything a pilot does goes through `PlayerInput`, `Locking` and
    /// `Weapons.run`: the trigger is pulled with nothing locked for the first
    /// second, then held.  What the row prints is whether a round ever left, and
    /// whether it arrived.</summary>
    private static void Pilot()
    {
        var world = new World(7, "range");
        var e = world.ecs;
        var pl = world.add_player("BEAM");
        pl.loadout = new[] { Defs.WID_AC30, Defs.WID_SACLOS };
        // the shop is what fits an arm in the game; here the arm is written on
        int row = e.row_of(pl.mech);
        var hull = e.arch_of(pl.mech)!;
        hull.u8("weapon")[row * 2 + 1] = (byte)Defs.WID_SACLOS;
        hull.i("mag")[row * 2 + 1] = 16;
        var mech = e.fv("pos").get3(pl.mech);
        int target = world.spawn_unit(Defs.ETYPE_MECH, 1 - pl.faction,
                                      mech.x + 700f, mech.z, hp_scale: 200f, crew: false);
        var tgt = e.fv("pos");

        int leaked = 0, rounds = 0, fired_while_locked = 0;
        float closest = -1f;
        for (int t = 0; t < 60 * 16; t++)
        {
            float now = t * DT;
            if (!e.is_alive(pl.mech)) break;
            var eye = MathUtil.sight_eye(e.fv("pos").get3(pl.mech), e.fv("yaw")[pl.mech]);
            var t0 = tgt.get3(target);
            float dx = t0.x - eye.x, dz = t0.z - eye.z, dy = t0.y + 3f - eye.y;
            pl.in_yaw = MathF.Atan2(dz, dx);
            pl.in_pitch = MathF.Atan2(dy, MathF.Sqrt(dx * dx + dz * dz));
            pl.zoom = 1f;
            // the first second: the trigger, with nothing locked
            pl.fire = now < 1f ? (false, true) : (false, false);
            world.update(DT);
            // a round may only exist while a channel is complete: that is the
            // lock gate, asked of the machine rather than of the trigger
            int n = world.projectiles().Length;
            if (n > 0)
            {
                if (_locked(world, pl.mech)) fired_while_locked = Math.Max(fired_while_locked, n);
                else leaked++;
                rounds = Math.Max(rounds, n);
            }
            if (now >= 1.4f) pl.fire = (false, true);            // and hold it
            var tp = tgt.get3(target);
            float tcy = tp.y + 3f;
            foreach (var ev in world.events)
            {
                if (!ev.TryGetValue("k", out var kv) || (string?)kv != "boom") continue;
                float bx = (float)ev["x"]!, by = (float)ev["y"]!, bz = (float)ev["z"]!;
                float d = MathF.Sqrt((bx - tp.x) * (bx - tp.x) + (by - tcy) * (by - tcy)
                                     + (bz - tp.z) * (bz - tp.z));
                if (closest < 0f || d < closest) closest = d;
            }
            world.events.Clear();
        }
        Console.WriteLine($"lock        -  {rounds,5}  {(closest >= 0f && closest < HIT_R ? 1 : 0),4}"
                          + $"  {fired_while_locked,6}  {closest,6:0.0}  "
                          + (leaked == 0 ? "gate holds"
                                         : $"{leaked} round-ticks WITHOUT a lock"));
        world.ecs.validate();
    }

    // ---- four channels -----------------------------------------------------

    /// <summary>Four hulls, four rounds in the air at once, then a fifth target to
    /// displace the oldest channel.</summary>
    private static void Channels()
    {
        var world = new World(7, "range");
        var e = world.ecs;
        var (lx, lz) = LAUNCHER;
        int shooter = world.spawn_unit(Defs.ETYPE_MECH, Defs.FACTION_RED, lx, lz,
                                       weapons: new[] { Defs.WID_AC30, Defs.WID_SACLOS },
                                       crew: false);
        var wd = Defs.WEAPONS[Defs.WID_SACLOS];
        var ring = new int[5];
        for (int k = 0; k < 5; k++)
        {
            float a = -0.6f + 0.3f * k;
            ring[k] = world.spawn_unit(Defs.ETYPE_MECH, Defs.FACTION_BLUE,
                                       lx + MathF.Cos(a) * 900f, lz + MathF.Sin(a) * 900f,
                                       hp_scale: 200f, crew: false);
        }
        int laid = 0, rounds = 0, lit = 0;
        for (int k = 0; k < 4; k++)
        {
            int ch = Beams.lay(world, shooter, ring[k]);
            if (ch < 0) continue;
            laid++;
            var tp = e.fv("pos").get3(ring[k]);
            var sp = e.fv("pos").get3(shooter);
            e.fv("sight_yaw")[shooter] = MathF.Atan2(tp.z - sp.z, tp.x - sp.x);
            for (int t = 0; t < 3; t++) world.update(DT);
            if (e.uv("beam_ok").get(shooter, ch) == 0) continue;
            Weapons.fire(world, shooter, 1, Defs.WID_SACLOS, wd, ring[k]);
            rounds++;
        }
        for (int t = 0; t < 15; t++) world.update(DT);
        lit = _lit_count(world, shooter);
        // a fifth target displaces the oldest channel
        Beams.lay(world, shooter, ring[4]);
        var held = new List<int>();
        for (int ch = 0; ch < Defs.BEAM_CHANNELS; ch++)
        {
            int tid = e.iv("beam_tgt").get(shooter, ch);
            if (tid >= 0) held.Add(tid);
        }
        bool fifo = !held.Contains(ring[0]) && held.Contains(ring[4]);
        Console.WriteLine($"channels    -  {rounds,5}  {laid,4}  {lit,6}  "
                          + $"{(fifo ? "fifo ok" : "FIFO WRONG")}  "
                          + $"{held.Count}/{Defs.BEAM_CHANNELS} channels held");
        world.ecs.validate();
    }
}
