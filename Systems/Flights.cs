// One system per kind of round, and the one that ends a round's flight.
//
// A round's identity is a tag component (`Defs.KIND_TAGS`), so each kind is its
// own archetype and its own pass: the flight of a bullet is three lines, a beam
// rider's is a steering loop, and neither pays for the other's branch.  What the
// four share is the prologue and the epilogue - re-arm the record, publish the
// leg's start, integrate, publish the leg - and that is all `_begin` / `_move`
// are.
//
// `Expire` sits in front of all four on purpose: it owns the two countdowns
// (`proj_age`, `proj_life`), and a round that has run out of battery or wire stops
// where it stands *before* this tick's move.  The one semantic line a flight
// system owns is `if (first_hit_t <= 1) continue` - a condemned round holds still.
//
// Written from `Systems/Projectiles.cs` (the Python port's `systems/projectiles.py`).

namespace Mech.Server.Systems;

public static class Flights
{
    // ------------------------------------------------------------------
    // the columns a round's pass reads
    // ------------------------------------------------------------------

    /// <summary>One in-flight round's columns: the buffers every round owns
    /// (`Components.PROJ_COMPS`) plus the `first_hit_record` the flight systems
    /// re-arm and publish.  All four kinds bind this one schema; the kind itself
    /// stays a query tag (`OnBullet`, `OnSaclos`, ...), never a branch.</summary>
    public class RoundColumns : Columns<RoundRow>
    {
        // bound by `bind`, which the constructor calls: hence the `null!`
        public float[] pos = null!, vel = null!, prev = null!, hit_pos = null!;
        public float[] age = null!, life = null!, trav = null!, seed = null!;
        public float[] hit_t = null!, hit_gap = null!;
        public byte[] wid = null!, hit_kind = null!, hit_mod = null!;
        public int[] src = null!, hit_unit = null!;

        internal static readonly string[] names =
        {
            "pos", "vel", "proj_prev", "first_hit_pos",
            "proj_age", "proj_life", "proj_trav", "proj_seed", "first_hit_t", "first_hit_gap",
            "proj_wid", "first_hit_kind", "first_hit_mod",
            "proj_src", "first_hit_unit",
        };

        public static readonly string[] OnBullet = Components.Cat(names, "bullet");
        public static readonly string[] OnRocket = Components.Cat(names, "unguided_missile");
        // a beam rider owns one component more (the laser line it rides), so its
        // schema and its query are `SaclosColumns` below

        public RoundColumns(Group g) : base(g) { bind(g); }

        public override string[] Names => names;

        protected override void bind(Group g)
        {
            pos = g.f("pos"); vel = g.f("vel"); prev = g.f("proj_prev");
            hit_pos = g.f("first_hit_pos");
            age = g.f("proj_age"); life = g.f("proj_life"); trav = g.f("proj_trav");
            seed = g.f("proj_seed"); hit_t = g.f("first_hit_t"); hit_gap = g.f("first_hit_gap");
            wid = g.u8("proj_wid"); hit_kind = g.u8("first_hit_kind");
            hit_mod = g.u8("first_hit_mod");
            src = g.i("proj_src"); hit_unit = g.i("first_hit_unit");
        }

        public override RoundRow row(int i) => new RoundRow(this, i);
    }

    /// <summary>One round: which columns, which row.  Every accessor is the buffer
    /// element itself, so a write lands in the archetype and nothing is copied
    /// back.</summary>
    public readonly struct RoundRow : IRow<RoundColumns>
    {
        private readonly RoundColumns _c;
        public readonly int i;

        public RoundRow(RoundColumns c, int i) { _c = c; this.i = i; }

        public RoundColumns columns => _c;
        public int index => i;
        public int eid => _c.ids[i];

        public Vec3 pos
        {
            get => new(_c.pos[i * 3], _c.pos[i * 3 + 1], _c.pos[i * 3 + 2]);
            set { _c.pos[i * 3] = value.x; _c.pos[i * 3 + 1] = value.y; _c.pos[i * 3 + 2] = value.z; }
        }
        public Vec3 vel
        {
            get => new(_c.vel[i * 3], _c.vel[i * 3 + 1], _c.vel[i * 3 + 2]);
            set { _c.vel[i * 3] = value.x; _c.vel[i * 3 + 1] = value.y; _c.vel[i * 3 + 2] = value.z; }
        }
        public Vec3 prev
        {
            get => new(_c.prev[i * 3], _c.prev[i * 3 + 1], _c.prev[i * 3 + 2]);
            set { _c.prev[i * 3] = value.x; _c.prev[i * 3 + 1] = value.y; _c.prev[i * 3 + 2] = value.z; }
        }
        public Vec3 hit_pos
        {
            get => new(_c.hit_pos[i * 3], _c.hit_pos[i * 3 + 1], _c.hit_pos[i * 3 + 2]);
            set { _c.hit_pos[i * 3] = value.x; _c.hit_pos[i * 3 + 1] = value.y; _c.hit_pos[i * 3 + 2] = value.z; }
        }

        public ref float age => ref _c.age[i];
        public ref float life => ref _c.life[i];
        public ref float trav => ref _c.trav[i];
        public ref float hit_t => ref _c.hit_t[i];
        public ref float hit_gap => ref _c.hit_gap[i];
        public ref byte hit_kind => ref _c.hit_kind[i];
        public ref byte hit_mod => ref _c.hit_mod[i];
        public ref int hit_unit => ref _c.hit_unit[i];
        public float seed => _c.seed[i];
        public byte wid => _c.wid[i];
        public int src => _c.src[i];
    }

    /// <summary>A beam rider's columns: the round's, plus the line of the laser it
    /// was encoded on (`Systems/Beams.cs` holds the channels on the launcher).</summary>
    public sealed class SaclosColumns : RoundColumns
    {
        public byte[] ch = null!;

        private static readonly string[] beam_names = Components.Cat(names, "beam_ch");
        public static readonly string[] OnSaclos = Components.Cat(beam_names, "saclos");

        public SaclosColumns(Group g) : base(g) { }

        public override string[] Names => beam_names;

        protected override void bind(Group g)
        {
            base.bind(g);
            ch = g.u8("beam_ch");
        }
    }

    /// <summary>The top attack's columns: the round's, plus the velocity its stare
    /// point walks at.  `Seeker` owns both halves of that memory; the airframe
    /// reads only this one, and only to hand `Flight.step` a lead.
    ///
    /// `bind` is the only virtual call made from a constructor in the schema, and
    /// it does nothing but assign buffers, so the dispatch before the derived
    /// instance is set up is safe.</summary>
    public sealed class TopColumns : RoundColumns
    {
        public float[] tvel = null!;

        private static readonly string[] top_names = Components.Cat(names, "tgt_vel");
        public static readonly string[] OnTop = Components.Cat(top_names, "top_attack");

        public TopColumns(Group g) : base(g) { }

        public override string[] Names => top_names;

        protected override void bind(Group g)
        {
            base.bind(g);
            tvel = g.f("tgt_vel");
        }

        /// <summary>The velocity the stare point walks at, row `i`.</summary>
        public Vec3 tvel_at(int i) => new(tvel[i * 3], tvel[i * 3 + 1], tvel[i * 3 + 2]);
    }

    /// <summary>Battery and wire: the round's `life` and its maximum flight
    /// distance, both settled before anything moves, plus the one place
    /// `proj_age` is counted.
    ///
    /// A round that runs out either cooks off where it stands (`expire_burst`: the
    /// top attack's battery, a beam rider's wire) or is simply gone (a bullet and a
    /// rocket, which leave nothing behind).  Both are written into the
    /// `first_hit_record` as a contact at t=0, so `Impact` treats them like any
    /// other ending instead of a third special case.</summary>
    /// <summary>Battery and wire: the round's `life` and its maximum flight
    /// distance, both settled before anything moves, plus the one place
    /// `proj_age` is counted.
    ///
    /// A round that runs out either cooks off where it stands (`expire_burst`: the
    /// top attack's battery, a beam rider's wire) or is simply gone (a bullet and a
    /// rocket, which leave nothing behind).  Both are written into the
    /// `first_hit_record` as a contact at t=0, so `Impact` treats them like any
    /// other ending instead of a third special case.</summary>
    public static class Expire
    {
        public static void run(World world, float dt)
        {
            var e = world.ecs;
            world.sweep.begin();                 // this tick's leg list starts here
            // every round, whatever it flies as: the schema is the `PROJ_COMPS`
            // half, and `groups` walks the four kinds' archetypes in the same
            // order `world.projectiles()` hands their ids out in
            foreach (var g in e.groups(RoundColumns.names))
            {
                var c = new RoundColumns(g);
                foreach (var r in c)
                {
                    var wd = Defs.WEAPONS[r.wid];
                    r.age += dt;
                    r.life -= dt;
                    bool out_of_life = r.life <= 0f;
                    bool out_of_wire = wd.maxdist > 0f && r.trav >= wd.maxdist;
                    if (!out_of_life && !out_of_wire) continue;
                    // the wire always cooks off; running out of battery depends on
                    // the round
                    bool burst = out_of_wire || wd.expire_burst;
                    r.hit_t = 0f;
                    r.hit_kind = burst ? Defs.HIT_EXPIRE : Defs.HIT_VANISH;
                    r.hit_unit = -1;
                    r.hit_mod = 0;
                    r.hit_gap = 0f;
                    r.hit_pos = r.pos;
                }
            }
        }
    }

    /// <summary>A bullet: gravity and nothing else, so the air half is a parabola
    /// and `Core/Ballistics.cs` can solve the zero in closed form.</summary>
    /// <summary>A bullet: gravity and nothing else, so the air half is a parabola
    /// and `Core/Ballistics.cs` can solve the zero in closed form.</summary>
    public static class Ballistic
    {
        public static void run(World world, float dt)
        {
            var e = world.ecs;
            foreach (var g in e.groups(RoundColumns.OnBullet))
            {
                var c = new RoundColumns(g);
                foreach (var r in c)
                {
                    if (!_begin(r)) continue;
                    var wd = Defs.WEAPONS[r.wid];
                    var p0 = r.pos;
                    var vel = r.vel;
                    var v = new Vec3(vel.x, vel.y - wd.g * dt, vel.z);
                    _move(world, r, p0, v, dt);
                }
            }
        }
    }

    /// <summary>An unguided rocket: thrust along the velocity it already has, up
    /// to `maxvel`, then gravity.  It never turns its nose, so it has no way to
    /// point anywhere but where it is already going.</summary>
    /// <summary>An unguided rocket: thrust along the velocity it already has, up
    /// to `maxvel`, then gravity.  It never turns its nose, so it has no way to
    /// point anywhere but where it is already going.</summary>
    public static class Rocket
    {
        public static void run(World world, float dt)
        {
            var e = world.ecs;
            foreach (var g in e.groups(RoundColumns.OnRocket))
            {
                var c = new RoundColumns(g);
                foreach (var r in c)
                {
                    if (!_begin(r)) continue;
                    var wd = Defs.WEAPONS[r.wid];
                    var p0 = r.pos;
                    var vel = r.vel;
                    float vx = vel.x, vy = vel.y, vz = vel.z;
                    var (sp, _) = _norm(new Vec3(vx, vy, vz));
                    float speed = MathF.Min(wd.maxvel,
                                            MathF.Sqrt(vx * vx + vy * vy + vz * vz)
                                            + wd.thrust * dt);
                    var v = new Vec3(sp.x * speed, sp.y * speed - wd.g * dt, sp.z * speed);
                    _move(world, r, p0, v, dt);
                }
            }
        }
    }

    /// <summary>A beam rider: it rides one line of its launcher's laser, which
    /// `Systems/Beams.cs` has laid on a locked target and leads for it.  The round
    /// knows only the line, so four of them can be in the air on four lines of the
    /// same beam.
    ///
    /// It is flown, not slid (`Flight.direct`): the same airframe as the top
    /// attack, with a law that has no arc in it - the motor puts 600 m/s on a 45 kg
    /// round in two seconds, the wing carries the weight at an angle of attack the
    /// rest of the way, and the pull available to correct anything falls with the
    /// square of the speed.  So a shot has to be *aimed*: the laser leads the
    /// target, the tube points where the pilot's arm is laid, and a round that
    /// arrives with no energy left sags out of the beam instead of turning.
    ///
    /// Two things take the line away, and both leave the round flying on its
    /// heading - the motor and the drag go on, the guidance is what stops:
    /// the launcher dying (nothing is designating), and the laser not reaching the
    /// round any more (a ridge or a house between the launcher and the missile cut
    /// a beam that is only light).  A round with no line has no lift demand either,
    /// so it keeps its heading and falls like anything else without a wing.</summary>
    public static class Saclos
    {
        public static void run(World world, float dt)
        {
            var e = world.ecs;
            foreach (var g in e.groups(SaclosColumns.OnSaclos))
            {
                var c = new SaclosColumns(g);
                foreach (var r in c)
                {
                    if (!_begin(r)) continue;
                    var wd = Defs.WEAPONS[r.wid];
                    var p0 = r.pos;
                    var vel = r.vel;
                    var (sp, _) = _norm(vel);
                    float speed = MathF.Sqrt(vel.x * vel.x + vel.y * vel.y
                                             + vel.z * vel.z);

                    // the beam: which channel this round was encoded on, whether
                    // the launcher is still lighting it, and whether the light
                    // still reaches the round.  What comes back is the channel's
                    // *track* - a point and the velocity it is moving at - and the
                    // round leads itself off it (`Systems/Beams.cs::intercept`)
                    Vec3? aim = null;
                    var aperture = Vec3.zero;
                    var line = Vec3.zero;
                    int shooter = r.src;
                    int ch = c.ch[r.index];
                    if (shooter >= 0 && ch < Defs.BEAM_CHANNELS && e.is_alive(shooter)
                        && e.has(shooter, "beam_tgt")
                        && e.uv("beam_ok").get(shooter, ch) != 0)
                    {
                        var (origin, _, _) = Weapons.aim_of(world, shooter);
                        if (Sight.clear(world, origin, p0, Sight.LOS_STEP))
                        {
                            var mem = e.fv("beam_mem");
                            var bvel = e.fv("beam_vel");
                            var bacc = e.fv("beam_acc");
                            var at = new Vec3(mem.get(shooter, ch * 3),
                                              mem.get(shooter, ch * 3 + 1),
                                              mem.get(shooter, ch * 3 + 2));
                            var tv = new Vec3(bvel.get(shooter, ch * 3),
                                              bvel.get(shooter, ch * 3 + 1),
                                              bvel.get(shooter, ch * 3 + 2));
                            var ta = new Vec3(bacc.get(shooter, ch * 2), 0f,
                                              bacc.get(shooter, ch * 2 + 1));
                            // the lead is solved with the speed this round will
                            // *average* to the target, not the 40 m/s it left the
                            // tube with: a lead computed off the launch speed asks
                            // for a 30 s flight and throws the aim point a kilometre
                            // down the target's track, and it would then sweep
                            // backwards through the sky as the motor lit
                            float avg = r.age < wd.ignite + wd.burn
                                ? 0.5f * (speed + wd.maxvel) : speed;
                            aim = Beams.intercept(at, tv, ta, p0, avg);
                            aperture = origin;
                            // the line the laser is on: what the round is steered
                            // against, and what the wobble pushes square to
                            var a = aim.Value;
                            line = _norm(new Vec3(a.x - origin.x, a.y - origin.y,
                                                  a.z - origin.z)).dir;
                        }
                    }
                    // one tick of the airframe: the motor, the wing, the drag and
                    // gravity.  The age is the one the last step produced, since
                    // `Expire` has already counted this tick's second
                    var v = Flight.direct(p0, vel, r.age - dt, aperture, aim, wd, dt);
                    // thrust misalignment / crosswind: a slow push square to the
                    // sight line, seeded per round, that the guidance has to chase
                    // down.  Bounded because the beam always wins.
                    float wob = wd.wob;
                    float side = MathF.Sqrt(line.x * line.x + line.z * line.z);
                    if (aim is not null && wob != 0f && side > 1e-3f)
                    {
                        float sd = r.seed;                      // random per round
                        float f = wd.wob_f * (0.75f + 0.5f * sd);
                        float ph = sd * MathUtil.TAU;
                        float am = wob * (0.7f + 0.6f * ((sd * 3.7f) % 1f))
                                   * (MathF.Sin(r.age * f * 2.1f + ph)
                                      + 0.55f * MathF.Sin(r.age * f * 5.3f
                                                          + ph * 2.3f));
                        v = new Vec3(v.x - line.z / side * am * dt, v.y,
                                     v.z + line.x / side * am * dt);
                    }
                    _move(world, r, p0, v, dt);
                }
            }
        }
    }

    /// <summary>The infrared round: the seeker owns the lock and hands back the
    /// point to dive on, and the airframe flies to it.
    ///
    /// `Seeker.track` refreshes the stare point off the live hull while it can see
    /// it, keeps the memory walking at the hull's last velocity when it cannot, and
    /// takes any enemy hull it finds inside its cone.  What comes back is a *point*,
    /// so the airframe below cannot tell a live hull from a memory and does not need
    /// to; a round that never had a lock gets nothing and is a rock.</summary>
    /// <summary>The infrared round: the seeker owns the lock and hands back the
    /// point to dive on, and the airframe flies to it.
    ///
    /// `Seeker.track` refreshes the stare point off the live hull while it can see
    /// it, keeps the memory walking at the hull's last velocity when it cannot, and
    /// takes any enemy hull it finds inside its cone.  What comes back is a *point*,
    /// so the airframe below cannot tell a live hull from a memory and does not need
    /// to; a round that never had a lock gets nothing and is a rock.</summary>
    public static class Top
    {
        public static void run(World world, float dt)
        {
            var e = world.ecs;
            foreach (var g in e.groups(TopColumns.OnTop))
            {
                var c = new TopColumns(g);
                foreach (var r in c)
                {
                    if (!_begin(r)) continue;
                    var wd = Defs.WEAPONS[r.wid];
                    var p0 = r.pos;
                    var vel = r.vel;
                    var spot = Seeker.track(world, r.eid, p0, dt);
                    Vec3? tvel = null;
                    if (spot is not null) tvel = c.tvel_at(r.index);
                    // the airframe is stepped from the age it had *before* this
                    // tick's countdown, i.e. the state the last step produced
                    var v = Flight.step(p0, vel, r.age - dt, r.trav, spot, tvel, wd, dt);
                    _move(world, r, p0, v, dt);
                }
            }
        }
    }

    // ------------------------------------------------------------------
    // the shared halves
    // ------------------------------------------------------------------

    /// <summary>Re-arm the record and publish where this tick's leg starts.
    ///
    /// False means the round was condemned before it flew (`Expire` wrote a
    /// contact at t=0 this tick) and holds still where it stands - the same place
    /// the old integrator checked its battery, before moving.</summary>
    /// <summary>Re-arm the record and publish where this tick's leg starts.
    ///
    /// False means the round was condemned before it flew (`Expire` wrote a
    /// contact at t=0 this tick) and holds still where it stands - the same place
    /// the old integrator checked its battery, before moving.</summary>
    private static bool _begin(RoundRow r)
    {
        if (r.hit_t <= 1f) return false;                // condemned: do not fly
        r.hit_t = Defs.FIRST_HIT_ARMED;                 // this tick's record is fresh
        r.prev = r.pos;
        return true;
    }

    /// <summary>Integrate one round over the tick and publish the leg it covered
    /// for the `FirstHit` systems.</summary>
    private static void _move(World world, RoundRow r, Vec3 p0, Vec3 v, float dt)
    {
        r.vel = v;
        var p1 = new Vec3(p0.x + v.x * dt, p0.y + v.y * dt, p0.z + v.z * dt);
        r.pos = p1;
        r.trav += MathF.Sqrt(v.x * v.x + v.y * v.y + v.z * v.z) * dt;
        world.sweep.add(r.eid, p0, p1);
    }

    private static (Vec3 dir, float len) _norm(Vec3 v)
    {
        float n = MathF.Sqrt(v.x * v.x + v.y * v.y + v.z * v.z);
        if (n < 1e-6f) return (new Vec3(1f, 0f, 0f), 0f);
        return (new Vec3(v.x / n, v.y / n, v.z / n), n);
    }

    /// <summary>Turn a velocity towards `desired` at no more than `turn_rate`,
    /// keeping `speed`.</summary>
    private static Vec3 _steer(Vec3 v, Vec3 desired, float speed, float turn_rate, float dt)
    {
        var (cur, _) = _norm(v);
        var (dn, want) = _norm(desired);
        if (want < 1e-3f)                       // already on the sight line
            return cur * speed;
        float dot = MathF.Max(-1f, MathF.Min(1f, cur.dot(dn)));
        float ang = MathF.Acos(dot);
        Vec3 nd;
        if (ang < 1e-4f)
        {
            nd = dn;
        }
        else
        {
            float f = MathF.Min(1f, turn_rate * dt / ang);
            (nd, _) = _norm(cur + (dn - cur) * f);
        }
        return nd * speed;
    }
}
