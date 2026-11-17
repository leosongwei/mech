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
//   window   the sight is turned 70 deg off the target: outside the designator's
//            40 deg cone, so the channel goes and the round coasts
//   cone     the sight is turned 30 deg off - still inside the cone - and then back
//            onto the hull: the laser is allowed to be pointed away from the cross,
//            and a hull that comes back inside the cone is corrected again
//   held     (its own row) a screen that never lifts: the beam goes dark after the
//            memory, the *lock* stays, and relighting it when the hull is visible
//            again puts the laser back on it
//   lock     the pilot's own path, through `PlayerInput` and `Locking`: the
//            trigger does nothing without a lock, and a locked round arrives
//   channels four targets, four rounds, four lines at once - and a fifth target
//            displacing the oldest
//   paint    the *target's* end: what a pilot's warning receiver is told.  Every
//            laser a machine emits is judged the same way - one down its sight,
//            four steered at the hulls it holds - by whether the beam crosses
//            that hull's body with nothing in between (`Beams.painting`)
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

    private enum Cut { None, Blind, Laser, Launcher, Window, Cone }

    /// <summary>Once a row has turned the sight away, the puppet launcher stops
    /// aiming: an operator who has looked off the target is not still tracking it.</summary>
    private static bool _freeze;

    /// <summary>When a row turns the sight *back* onto the target (`Cut.Cone`): the
    /// operator looks away, looks back, and the beam is his again.</summary>
    private static float _lookBack = -1f;

    private sealed class Row_
    {
        public int fired, hits, guided;
        public float miss = -1f;
        public string note = "";
    }

    public static int run()
    {
        Console.WriteLine("row        dist  fired  hits  guided  miss m  note");
        Print("clear", 400f, One(400f, Cut.None));
        Print("clear", 800f, One(800f, Cut.None));
        Print("clear", 1600f, One(1600f, Cut.None));
        Print("clear", 3000f, One(3000f, Cut.None));
        Print("blind", 800f, One(800f, Cut.Blind));
        Print("laser", 800f, One(800f, Cut.Laser));
        Print("kill", 800f, One(800f, Cut.Launcher));
        Print("window", 800f, One(800f, Cut.Window));
        Print("cone", 1600f, One(1600f, Cut.Cone));
        Pilot();
        Channels();
        Paint();
        Belief();
        Ghost();
        Held();
        Profile();
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
        _lookBack = -1f;
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
            if (_lookBack >= 0f && now >= _lookBack)
            {
                _lookBack = -1f;
                _freeze = false;                 // the operator looks back on it
            }
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
                if (cut != Cut.None) _cut(world, cut, shooter, lx, lz, target, now);
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

    // ---- the lock that outlives the sight ----------------------------------

    /// <summary>A screen that never lifts: what the designator does with a hull it
    /// cannot see any more.  The beam is lit on the walked point for `BEAM_MEMORY`,
    /// goes dark when the guess is too old to fly - and the **channel is still
    /// there**, because the hull is the machine's until it dies or leaves the cone.
    /// Lift the screen and the laser is back on it the tick the sight is clear.</summary>
    private static void Held()
    {
        var world = new World(7, "range");
        var e = world.ecs;
        var (lx, lz) = LAUNCHER;
        int shooter = world.spawn_unit(Defs.ETYPE_MECH, Defs.FACTION_RED, lx, lz,
                                       weapons: new[] { Defs.WID_AC30, Defs.WID_SACLOS },
                                       crew: false);
        int target = world.spawn_unit(Defs.ETYPE_MECH, Defs.FACTION_BLUE, lx + 800f, lz,
                                      hp_scale: 200f, crew: false);
        Beams.lay(world, shooter, target);          // the operator's lock
        // when the screen goes up, when the guess behind it is too old to fly, and
        // when the screen comes down again
        float screenUp = 0.8f, tooOld = Defs.BEAM_MEMORY + 1.5f, liftAt = tooOld + 0.5f;
        int puff = -1, stage = 0;
        var at_ = new[] { 0.6f, screenUp + 0.7f, tooOld, liftAt + 0.3f };
        var names = new[] { "tracking", "screened", "guess too old", "screen lifted" };
        var state = new System.Text.StringBuilder();
        for (int t = 0; t < (int)((liftAt + 1.5f) / DT); t++)
        {
            float now = t * DT;
            _drive(world, shooter, target);
            e.fv("vel_cmd").set3(target, 0f, 0f, 0f);   // a standing hull
            // a screen that never lifts: a real puff lives `SMOKE_LIFE` (5 s) and the
            // memory is 8, so it is laid again as it dies
            if (now > screenUp && now < liftAt)
            {
                // laid again before the old one dies: one clear tick in between and
                // the sight re-acquires, which is exactly what this row must not do
                bool dying = puff >= 0 && e.fv("smoke_t").get(puff, 0) < 1.2f;
                if (puff < 0 || !e.is_alive(puff) || dying)
                {
                    puff = _puff(world, new Vec3(lx + 720f,
                                                 world.terrain.height_at(lx + 720f, lz) + 6f,
                                                 lz), 45f);
                }
            }
            if (now >= liftAt && puff >= 0) { e.destroy(puff); puff = -1; }
            world.update(DT);
            while (stage < at_.Length && now >= at_[stage])
            {
                string label = names[stage];
                stage++;
                int ch = _first_channel(world, shooter);
                var mem = e.fv("beam_mem");
                var point = ch < 0 ? Vec3.zero
                    : new Vec3(mem.get(shooter, ch * 3), mem.get(shooter, ch * 3 + 1),
                               mem.get(shooter, ch * 3 + 2));
                float off = ch < 0 ? -1f : MathUtil.dist(point, e.fv("pos").get3(target));
                state.Append($"{label}: {(ch < 0 ? "DROPPED" : "held")}"
                             + $"{(ch >= 0 && e.uv("beam_ok").get(shooter, ch) != 0 ? " lit" : " dark")}"
                             + $"{(off < 0f ? "" : $" {off:0} m off the hull")}   ");
            }
        }
        Console.WriteLine($"held     {state.ToString().TrimEnd()}");
        world.ecs.validate();
    }

    // ---- the other end of the laser ---------------------------------------

    /// <summary>What the *target* is told, through the one rule every laser is judged
    /// by: a beam is on a hull when it reaches that hull's body (`Defs.LASER_SPHERE`,
    /// ten metres, around its centre) with nothing in between.  Every machine emits five
    /// of them - the four channels its computer steers at the hulls it holds, and the
    /// one down its sight, which burns whether it is guiding anything or not - and the
    /// row walks the cases where the answer is not obvious: a sight merely laid on the
    /// hull, a sight past the sphere, a lock steered 25 deg off the cross, a screen, a
    /// body in the way.</summary>
    private static void Paint()
    {
        var world = new World(7, "range");
        var e = world.ecs;
        var pl = world.add_player("PAINT");
        // the range's own fixtures are hulls with a sight line too, and this row counts
        // every mark the pilot's meta carries: nothing but this hull and the machine
        // doing the painting is left on the map
        foreach (int id in world.units()) if (id != pl.mech) e.destroy(id);
        var me = e.fv("pos").get3(pl.mech);
        int shooter = world.spawn_unit(Defs.ETYPE_MECH, 1 - pl.faction,
                                       me.x + 800f, me.z, crew: false);
        var sp = e.fv("pos").get3(shooter);
        var ud = Defs.UNIT_DEFS[e.uv("etype")[pl.mech]];
        var body = new Vec3(me.x, me.y + ud.height * 0.5f, me.z);
        float r = Defs.LASER_SPHERE;
        var sb = new System.Text.StringBuilder();

        // a sight laid on the hull and nothing else: the cross is a laser, and it is
        // burning whether or not this machine has a launcher fitted
        _aim(world, shooter, body);
        for (int t = 0; t < 3; t++) world.update(DT);
        sb.Append($"sight on it: {_marks(world, pl)}");
        // ...past the sphere the receiver answers to: the same sight, and the light
        // misses it.  Nothing about the *angle* decides this, the sphere does
        _aim(world, shooter, new Vec3(body.x, body.y, body.z + r * 2.2f));
        for (int t = 0; t < 3; t++) world.update(DT);
        sb.Append($"   sight {r * 2.2f:0.0} m off it: {_marks(world, pl)}");

        // the lock: the computer steers a line at the hull it holds, and the operator's
        // cross is 25 deg off the whole time - inside `BEAM_CONE`, which is where a
        // SACLOS lock lives
        e.fv("sight_yaw")[shooter] = MathF.Atan2(body.z - sp.z, body.x - sp.x) + 0.44f;
        e.fv("sight_pitch")[shooter] = 0f;
        int ch = Beams.lay(world, shooter, pl.mech);
        for (int t = 0; t < 3; t++) world.update(DT);
        sb.Append($"   lock, cross 25 deg off: {_marks(world, pl)}");

        // a screen between the two: the channel is still held, the beam is not on the
        // hull, and a receiver that rang through a screen would be ringing at nothing
        float mid = (sp.x + me.x) * 0.5f;
        int puff = _puff(world, new Vec3(mid, world.terrain.height_at(mid, sp.z) + 6f, sp.z), 45f);
        for (int t = 0; t < 3; t++) world.update(DT);
        sb.Append($"   screened: {_marks(world, pl)} ({_beam(world, shooter, ch)})");
        if (puff >= 0 && e.is_alive(puff)) e.destroy(puff);
        for (int t = 0; t < 3; t++) world.update(DT);
        sb.Append($"   screen lifted: {_marks(world, pl)}");

        // a hull parked on the line: the beam stops on the first body it meets, so the
        // machine behind it is in the dark with the lock still on it
        int block = world.spawn_unit(Defs.ETYPE_MECH, 1 - pl.faction,
                                     me.x + 400f, me.z, crew: false);
        for (int t = 0; t < 3; t++) world.update(DT);
        sb.Append($"   hull in the way: {_marks(world, pl)} ({_beam(world, shooter, ch)})");
        if (e.is_alive(block)) e.destroy(block);
        for (int t = 0; t < 3; t++) world.update(DT);

        // ...and a sight turned out of the cone takes the hull out of the picture: the
        // channel goes with it
        e.fv("sight_yaw")[shooter] = MathF.Atan2(body.z - sp.z, body.x - sp.x) + 1.05f;
        for (int t = 0; t < 3; t++) world.update(DT);
        sb.Append($"   looked away: {_marks(world, pl)} ({_beam(world, shooter, ch)})");
        Console.WriteLine($"paint    {sb}");
        world.ecs.validate();
    }

    /// <summary>Lay a designator's sight on a point, from the eye it looks out of.</summary>
    private static void _aim(World world, int shooter, Vec3 at)
    {
        var e = world.ecs;
        var (eye, _, _) = Weapons.aim_of(world, shooter);
        float dx = at.x - eye.x, dy = at.y - eye.y, dz = at.z - eye.z;
        e.fv("sight_yaw")[shooter] = MathF.Atan2(dz, dx);
        e.fv("sight_pitch")[shooter] = MathF.Atan2(dy, MathF.Sqrt(dx * dx + dz * dz));
    }

    /// <summary>The machines the pilot's own meta says have a laser on his hull.</summary>
    private static string _marks(World world, Player pl)
    {
        var meta = Netcode.meta_payload(world, pl);
        if (meta["lasers"] is not List<object?> list || list.Count == 0) return "no mark";
        var ids = new System.Text.StringBuilder();
        foreach (var row in list)
            if (row is Dictionary<string, object?> d && d.TryGetValue("id", out var id))
                ids.Append(ids.Length > 0 ? "," : "").Append(id);
        return $"{list.Count} mark (id {ids})";
    }

    /// <summary>What the launcher's channel is doing, read on its own: a lock that is
    /// still held with the beam off it is a different thing from a lost lock.</summary>
    private static string _beam(World world, int shooter, int ch)
    {
        var e = world.ecs;
        if (ch < 0 || e.iv("beam_tgt").get(shooter, ch) < 0) return "channel given up";
        return e.uv("beam_lit").get(shooter, ch) != 0 ? "beam on it" : "beam off it, lock held";
    }

    // ---- what a blind shot is launched on ------------------------------------

    /// <summary>A seeker round leaves the tube on what its *shooter* knows: the
    /// belief his client has been drawing (his HUD's mark) travels with his input and
    /// `Weapons.fire` seeds the round from it, so a launch made at a walked point is a
    /// launch at a walked point.  The row drives one of each - a belief handed over,
    /// and none - and reads the two numbers a round starts with.</summary>
    private static void Belief()
    {
        var world = new World(7, "range");
        var e = world.ecs;
        var pl = world.add_player("BLIND");
        // the shop is what fits an arm in the game; here the arm is written on, through
        // the views so no row index is held across a spawn
        e.uv("weapon").set(pl.mech, 1, (byte)Defs.WID_TOPATTACK);
        var me = e.fv("pos").get3(pl.mech);
        int target = world.spawn_unit(Defs.ETYPE_MECH, 1 - pl.faction,
                                      me.x + 1200f, me.z, hp_scale: 200f, crew: false);
        var truth = e.fv("pos").get3(target);
        // a screen between the two, so the shot really is a blind one
        _puff(world, new Vec3(me.x + 600f, world.terrain.height_at(me.x + 600f, me.z) + 6f,
                              me.z), 45f);
        // the candidate, and how far the pilot's own picture has walked away from the
        // hull while he has not been able to see it
        pl.lock_target = target;
        var walked = new Vec3(truth.x - 120f, truth.y + 3f, truth.z + 60f);
        var sb = new System.Text.StringBuilder();
        for (int pass = 0; pass < 2; pass++)
        {
            if (pass == 1)
            {
                pl.belief_id = target;
                pl.belief_pos = walked;
                pl.belief_vel = new Vec3(-20f, 0f, 10f);
                pl.belief_tick = world.tick;
            }
            pl.lock_target = target;
            pl.fire = (false, true);
            e.fv("cool").set(pl.mech, 1, 0f);
            e.iv("mag").set(pl.mech, 1, 8);
            // the round this pass fires, told from the one still flying from the last
            var had = new HashSet<int>(world.projectiles());
            for (int t = 0; t < 3; t++) world.update(DT);
            pl.fire = (false, false);
            world.update(DT);            // a semi-automatic arm wants the release seen
            int round = -1;
            foreach (int id in world.projectiles())
                if (e.uv("proj_kind")[id] == Defs.PROJ_TOP && !had.Contains(id)) { round = id; break; }
            if (round < 0)
            {
                sb.Append(pass == 0 ? " no round (no belief)" : " no round (belief)");
                continue;
            }
            var mem = e.fv("tgt_mem").get3(round);
            var mvel = e.fv("tgt_vel").get3(round);
            float toBelief = MathUtil.dist(mem, walked);
            float toHull = MathUtil.dist(mem, new Vec3(truth.x, truth.y + 3f, truth.z));
            sb.Append(pass == 0
                ? $" no belief: memory {toHull,4:0} m from the hull, {toBelief,4:0} m from the walk"
                : $" belief:    memory {toHull,4:0} m from the hull, {toBelief,4:0} m from the walk,"
                  + $" vel ({mvel.x:0},{mvel.z:0})");
        }
        Console.WriteLine($"belief  {sb.ToString().TrimEnd()}");
        world.ecs.validate();
    }

    // ---- what the pilot is shown while the sight is cut ---------------------

    /// <summary>How far the designator's own answer is from the truth once it stops
    /// seeing the hull.  A channel that loses the sight keeps flying a *guess* - the
    /// point walked at the target's last velocity and pull (`Beams._walk`) - and the
    /// guess is the only honest place to put a mark on that hull: the marker a client
    /// draws from the snapshot's own row is the hull itself, through a screen that
    /// hides it.  This row is that distance, per second, for a hull crossing at
    /// `GHOST_SPEED`.</summary>
    private static void Ghost()
    {
        const float GHOST_SPEED = 30f;
        var world = new World(7, "range");
        var e = world.ecs;
        var (lx, lz) = LAUNCHER;
        int shooter = world.spawn_unit(Defs.ETYPE_MECH, Defs.FACTION_RED, lx, lz,
                                       weapons: new[] { Defs.WID_AC30, Defs.WID_SACLOS },
                                       crew: false);
        int target = world.spawn_unit(Defs.ETYPE_MECH, Defs.FACTION_BLUE, lx + 800f, lz,
                                      hp_scale: 200f, crew: false);
        float pitch = 0f;
        var sb = new System.Text.StringBuilder();
        for (int t = 0; t < (int)((Defs.BEAM_MEMORY + 2.5f) / DT); t++)
        {
            float now = t * DT;
            // the launcher tracks the hull until the screen goes up, then holds the
            // last line it was given - an operator with no sight to correct on
            if (now >= 0.9f) { e.fv("vel_cmd").set3(target, 0f, 0f, GHOST_SPEED); }
            if (now < 1f)
            {
                var tp = e.fv("pos").get3(target);
                var sp = e.fv("pos").get3(shooter);
                e.fv("sight_yaw")[shooter] = MathF.Atan2(tp.z - sp.z, tp.x - sp.x);
                e.fv("pitch")[shooter] = pitch;
                Beams.lay(world, shooter, target);
            }
            // ...and a screen a third of the way out, laid again before it dies: one
            // clear tick and the sight would re-acquire, which is the thing this row
            // is measuring the absence of
            if (now >= 1f)
            {
                var ids = e.query("smoke");
                bool dying = true;
                foreach (int sid in ids) if (e.fv("smoke_t")[sid] > 1.2f) { dying = false; break; }
                if (dying)
                    _puff(world, new Vec3(lx + 260f, world.terrain.height_at(lx + 260f, lz) + 6f, lz), 45f);
            }
            world.update(DT);
            int ch = _first_channel(world, shooter);
            if (ch < 0) { sb.Append($" {now:0.0}s: DROPPED"); break; }
            var mem = e.fv("beam_mem");
            var point = new Vec3(mem.get(shooter, ch * 3), mem.get(shooter, ch * 3 + 1),
                                 mem.get(shooter, ch * 3 + 2));
            var truth = e.fv("pos").get3(target);
            float off = MathUtil.dist(point, truth);
            if (MathF.Abs(now - MathF.Round(now)) < DT * 0.5f && now >= 1f)
                sb.Append($" {now:0}s {off,4:0} m"
                          + (e.uv("beam_ok").get(shooter, ch) != 0 ? " lit" : " dark")
                          + (e.uv("beam_lit").get(shooter, ch) != 0 ? "+on" : ""));
        }
        Console.WriteLine($"ghost    crossing at {GHOST_SPEED:0} m/s, sight cut at 1 s:" + sb);
        world.ecs.validate();
    }

    // ---- the round's own flight -------------------------------------------

    /// <summary>What the motor, the wing and the drag do to one round over the whole
    /// wire: the speed at each kilometre, the seconds it took, and how far the round
    /// wandered off the straight line to a standing target.  The last row is the one
    /// that says the airframe is real - the speed only ever falls after burnout, and
    /// a slow round holds the line by flying at an angle of attack, so the sag under
    /// it grows as the square of the speed drops.</summary>
    private static void Profile()
    {
        var world = new World(7, "range");
        var e = world.ecs;
        // the long line: 3.1 km along the range's southern plain, which is the flat
        // half of the map (the 200 m hill sits north-west of it) and as far as a
        // beam rider can be locked at all (its `range`, 3400 m)
        const float lx = 300f, lz = 3400f;
        const float far = 3600f;                     // 3.3 km, inside the 3.4 km lock
        int shooter = world.spawn_unit(Defs.ETYPE_MECH, Defs.FACTION_RED, lx, lz,
                                       weapons: new[] { Defs.WID_AC30, Defs.WID_SACLOS },
                                       crew: false);
        int target = world.spawn_unit(Defs.ETYPE_MECH, Defs.FACTION_BLUE, far, far,
                                      hp_scale: 200f, crew: false);
        var wd = Defs.WEAPONS[Defs.WID_SACLOS];
        var pos = e.fv("pos");
        // straight at the target's centre: the beam's lead has nothing to do here, so
        // what is measured is the airframe and nothing else
        var fresh = new Vec3(pos.get3(shooter).x, pos.get3(shooter).y, pos.get3(shooter).z);
        var tp = pos.get3(target);
        float yaw = MathF.Atan2(tp.z - fresh.z, tp.x - fresh.x);
        float pitch = MathF.Atan2(tp.y + Defs.UNIT_DEFS[Defs.ETYPE_MECH].height * 0.5f
                                  - (fresh.y + Defs.MECH_EYE),
                                  MathF.Sqrt((tp.x - fresh.x) * (tp.x - fresh.x)
                                             + (tp.z - fresh.z) * (tp.z - fresh.z)));
        e.fv("yaw")[shooter] = yaw;
        e.fv("sight_yaw")[shooter] = yaw;
        e.fv("pitch")[shooter] = pitch;
        e.fv("sight_pitch")[shooter] = pitch;
        for (int t = 0; t < 40; t++) world.update(DT);       // let the hulls settle

        int row = e.row_of(shooter);
        e.arch_of(shooter)!.i("mag")[row * 2 + 1] = 4;
        Weapons.fire(world, shooter, 1, Defs.WID_SACLOS, wd, target);
        e.arch_of(shooter)!.i("mag")[row * 2 + 1] -= 1;

        // 200 m is inside the boost: it is where a ramped motor shows its soft end
        int[] at = { 200, 500, 1000, 2000, 3000, 3200 };
        var speed = new float[at.Length];
        var when = new float[at.Length];
        var sag = new float[at.Length];
        int found = -1;
        float live = 0f, maxSag = 0f, lineY = fresh.y + MathF.Sin(pitch) * 1f;
        // the motor's own numbers: the fastest the round ever goes and when, which
        // is what a burn time is tuned against
        float peak = 0f, peakT = 0f, peakD = 0f;
        for (int t = 0; t < 1200; t++)
        {
            world.update(DT);
            if (found < 0)
            {
                var ids = world.projectiles();
                for (int k = 0; k < ids.Length; k++)
                    if (e.uv("proj_kind")[ids[k]] == Defs.PROJ_SACLOS) found = ids[k];
                if (found < 0) continue;
            }
            if (!e.is_alive(found)) break;
            float dist = e.fv("proj_trav")[found];
            var rp = e.fv("pos").get3(found);
            var rv = e.fv("vel").get3(found);
            float sp = MathF.Sqrt(rv.x * rv.x + rv.y * rv.y + rv.z * rv.z);
            live = sp;
            if (sp > peak) { peak = sp; peakT = t * DT; peakD = dist; }
            // the line the tube is pointed down, at this range: how far the round
            // has wandered from it vertically
            float want = fresh.y + MathF.Tan(pitch) * dist;
            float off = rp.y - want;
            if (MathF.Abs(off) > maxSag) maxSag = MathF.Abs(off);
            for (int k = 0; k < at.Length; k++)
                if (speed[k] <= 0f && dist >= at[k])
                {
                    speed[k] = sp;
                    when[k] = t * DT;
                    sag[k] = off;
                }
        }
        // where the warhead went off, against the hull it was after
        float burstAt = -1f;
        for (int t = 0; t < 120; t++)
        {
            world.update(DT);
            var tp2 = pos.get3(target);
            float cy = tp2.y + Defs.UNIT_DEFS[Defs.ETYPE_MECH].height * 0.5f;
            foreach (var ev in world.events)
            {
                if (!ev.TryGetValue("k", out var kv) || (string?)kv != "boom") continue;
                float bx = (float)ev["x"]!, by = (float)ev["y"]!, bz = (float)ev["z"]!;
                float d = MathF.Sqrt((bx - tp2.x) * (bx - tp2.x) + (by - cy) * (by - cy)
                                     + (bz - tp2.z) * (bz - tp2.z));
                if (burstAt < 0f || d < burstAt) burstAt = d;
            }
            world.events.Clear();
            if (burstAt >= 0f) break;
        }
        var sb = new System.Text.StringBuilder();
        for (int k = 0; k < at.Length; k++)
            sb.Append(speed[k] > 0f
                ? $"{at[k] / 1000f:0.0} km {speed[k],5:0} m/s {when[k],5:0.0} s {sag[k],+5:0.0} m   "
                : $"{at[k] / 1000f:0.0} km -");
        Console.WriteLine($"profile  {sb.ToString().TrimEnd()}");
        Console.WriteLine($"profile  worst off the line {maxSag,4:0.0} m, "
                          + $"peak {peak,4:0} m/s at {peakD,4:0} m / {peakT,4:0.00} s "
                          + $"(motor out at {wd.ignite + wd.burn:0.00} s), "
                          + $"alive {e.is_alive(found)}, last speed {live,4:0} m/s, "
                          + $"burst {burstAt,5:0.0} m from the hull's centre");
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
    private static int _puff(World world, Vec3 at, float radius, float ttl = 5f)
    {
        var e = world.ecs;
        int sid = e.create(Components.PUFF_COMPS);
        e.fv("pos").set3(sid, at);
        e.fv("smoke_r").set3(sid, radius, radius * 1.1f, radius);
        e.fv("smoke_t")[sid] = ttl;
        e.fv("smoke_s")[sid] = 1f;
        return sid;
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

    private static void _cut(World world, Cut cut, int shooter, float lx, float lz, int target,
                             float now)
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
                // the pilot looks away: outside the designator's cone the channel goes
                _freeze = true;
                e.fv("sight_yaw")[shooter] = e.fv("sight_yaw")[shooter] + 1.2f;
                break;
            case Cut.Cone:
                // 30 deg off the hull - inside the 40 deg cone the laser may be
                // pointed in - and back on it 0.8 s later
                _freeze = true;
                e.fv("sight_yaw")[shooter] = e.fv("sight_yaw")[shooter] + 0.52f;
                _lookBack = now + 0.8f;
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
        bool _aligned = false;
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
            // the first second: the trigger, with nothing locked.  After that the
            // row waits for the hull to come round onto the target before it holds
            // the trigger - a round leaves along the *bore*, and a pilot who fires
            // mid-turn is firing where his arm points (`Systems/Weapons.cs`)
            float off = MathF.Abs(MathF.IEEERemainder(e.fv("yaw")[pl.mech] - pl.in_yaw,
                                                      MathF.PI * 2f));
            pl.fire = now < 1f ? (false, true) : (false, false);
            if (now > 1f && off < 0.02f) _aligned = true;
            if (_aligned) pl.fire = (false, true);
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
