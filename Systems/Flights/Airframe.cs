// The airframe as seven stages: two derivations, four forces and one integration.
//
// The stages are one law spread over the tick, so that "what acts on this round" is a
// component set rather than a branch inside a function.  Airflow and the angle of attack
// turn the tick's *starting* velocity and the guidance's demand into the values every
// force reads; lift, drag, thrust and gravity each add their own acceleration into
// `acc`; `Integrate` is the only stage that spends it.
//
// Each force's arithmetic lives in the stage that applies it, and what passes between
// stages is a column: that is what makes one force readable without the other three.  The
// only other reader of this arithmetic is the fire control's integrator, which flies an
// unguided round with no world (`Core/Flight.cs::slide`) - so what this file has to
// agree with is that one closed form, and `--check airframe` is what holds them together.
//
// What this file holds to:
//
//   1. A force stage only adds to `acc`.  The one writer of `vel` is `Integrate`
//      (a bullet has no airframe and integrates its own parabola in `Ballistic`).
//   2. `Arm` zeroes `acc`, `a_dir` and `a_want`, so the tick's force sum is complete
//      and carries nothing over.
//   3. Nothing in the flight block creates or destroys an entity, so a row keeps its
//      index across all eleven stages of the tick.
//   4. A condemned round does not fly: every stage skips it on the same
//      `flying(hit_t)` compare.
//   5. `Expire` has already counted this tick's second, so a stage that reads the
//      round's flight age reads `proj_age - dt`.

namespace Mech.Server.Systems;

public static partial class Flights
{
    // ------------------------------------------------------------------
    // the derivations: what the tick's forces are written against
    // ------------------------------------------------------------------

    /// <summary>A winged round's velocity direction and dynamic pressure.</summary>
    public sealed class AirflowColumns : Columns<int>
    {
        public float[] vel = null!, vhat = null!, q = null!, hit_t = null!;

        internal static readonly string[] names =
            { "vel", "vhat", "q", "first_hit_t" };

        public AirflowColumns(Group g) : base(g) { bind(g); }

        public override string[] Names => names;

        protected override void bind(Group g)
        {
            vel = g.f("vel"); vhat = g.f("vhat"); q = g.f("q");
            hit_t = g.f("first_hit_t");
        }

        public override int row(int i) => i;
    }

    public static class Airflow
    {
        public static void run(World world, float dt)
        {
            foreach (var g in world.ecs.groups(AirflowColumns.names))
            {
                var c = new AirflowColumns(g);
                for (int i = 0; i < c.n; i++)
                {
                    if (!flying(c.hit_t[i])) continue;
                    var vel = v3(c.vel, i);
                    float speed = vel.len;
                    set3(c.vhat, i, vel.unit);
                    c.q[i] = 0.5f * Flight.RHO * speed * speed;
                }
            }
        }
    }

    /// <summary>The lift coefficient the guidance's demand buys, and the nose that
    /// follows it.</summary>
    public sealed class AoaColumns : Columns<int>
    {
        public float[] vhat = null!, adir = null!, awant = null!, q = null!;
        public float[] cl = null!, nose = null!;
        public byte[] wid = null!;
        public float[] hit_t = null!;

        internal static readonly string[] names =
        {
            "vhat", "a_dir", "a_want", "q", "proj_wid", "cl", "nose", "first_hit_t",
        };

        public AoaColumns(Group g) : base(g) { bind(g); }

        public override string[] Names => names;

        protected override void bind(Group g)
        {
            vhat = g.f("vhat"); adir = g.f("a_dir"); awant = g.f("a_want"); q = g.f("q");
            cl = g.f("cl"); nose = g.f("nose"); wid = g.u8("proj_wid");
            hit_t = g.f("first_hit_t");
        }

        public override int row(int i) => i;
    }

    public static class AoA
    {
        public static void run(World world, float dt)
        {
            foreach (var g in world.ecs.groups(AoaColumns.names))
            {
                var c = new AoaColumns(g);
                for (int i = 0; i < c.n; i++)
                {
                    if (!flying(c.hit_t[i])) continue;
                    var wd = Defs.WEAPONS[c.wid[i]];
                    var vhat = v3(c.vhat, i);
                    float want = c.awant[i];
                    float s = Flight.area(wd);
                    // nothing asked for (or no air to make it in) is a nose on the
                    // flight path and no lift: the unguided round's own case
                    if (want <= 1e-9f || c.q[i] * s <= 1e-9f)
                    {
                        c.cl[i] = 0f;
                        set3(c.nose, i, vhat);
                        continue;
                    }
                    float cl = MathF.Min(wd.cl_max, wd.mass * want / (c.q[i] * s));
                    // the needed angle of attack: the nose leads the velocity by it, so
                    // a pull lifts the nose above the path and a pushover puts it under
                    float alpha = cl / wd.cl_alpha;
                    float ca = MathF.Cos(alpha), sa = MathF.Sin(alpha);
                    var adir = v3(c.adir, i);
                    c.cl[i] = cl;
                    set3(c.nose, i, new Vec3(vhat.x * ca + adir.x * sa,
                                             vhat.y * ca + adir.y * sa,
                                             vhat.z * ca + adir.z * sa));
                }
            }
        }
    }

    // ------------------------------------------------------------------
    // the four forces: each one adds its own acceleration
    // ------------------------------------------------------------------

    /// <summary>Lift, square to the flight path, in the direction the guidance
    /// asked for.</summary>
    public sealed class LiftColumns : Columns<int>
    {
        public float[] acc = null!, adir = null!, cl = null!, q = null!;
        public byte[] wid = null!;
        public float[] hit_t = null!;

        internal static readonly string[] names =
            { "acc", "a_dir", "cl", "q", "proj_wid", "first_hit_t" };

        public LiftColumns(Group g) : base(g) { bind(g); }

        public override string[] Names => names;

        protected override void bind(Group g)
        {
            acc = g.f("acc"); adir = g.f("a_dir"); cl = g.f("cl"); q = g.f("q");
            wid = g.u8("proj_wid"); hit_t = g.f("first_hit_t");
        }

        public override int row(int i) => i;
    }

    public static class Lift
    {
        public static void run(World world, float dt)
        {
            foreach (var g in world.ecs.groups(LiftColumns.names))
            {
                var c = new LiftColumns(g);
                for (int i = 0; i < c.n; i++)
                {
                    if (!flying(c.hit_t[i])) continue;
                    float lift = lift_accel(Defs.WEAPONS[c.wid[i]], c.q[i], c.cl[i]);
                    add3(c.acc, i, v3(c.adir, i) * lift);
                }
            }
        }
    }

    /// <summary>Drag, against the flight path - and so the reason a hard pull costs
    /// speed.</summary>
    public sealed class DragColumns : Columns<int>
    {
        public float[] acc = null!, vel = null!, vhat = null!, cl = null!, q = null!;
        public byte[] wid = null!;
        public float[] hit_t = null!;

        internal static readonly string[] names =
            { "acc", "vel", "vhat", "cl", "q", "proj_wid", "first_hit_t" };

        public DragColumns(Group g) : base(g) { bind(g); }

        public override string[] Names => names;

        protected override void bind(Group g)
        {
            acc = g.f("acc"); vel = g.f("vel"); vhat = g.f("vhat");
            cl = g.f("cl"); q = g.f("q");
            wid = g.u8("proj_wid"); hit_t = g.f("first_hit_t");
        }

        public override int row(int i) => i;
    }

    public static class Drag
    {
        public static void run(World world, float dt)
        {
            foreach (var g in world.ecs.groups(DragColumns.names))
            {
                var c = new DragColumns(g);
                for (int i = 0; i < c.n; i++)
                {
                    if (!flying(c.hit_t[i])) continue;
                    var wd = Defs.WEAPONS[c.wid[i]];
                    float speed = v3(c.vel, i).len;
                    // the polar: profile drag, the induced price of the lift just
                    // commanded (squared, so a pushover costs what a pull-up costs) and
                    // the transonic rise
                    float over = MathF.Max(0f, speed / Flight.MACH - 0.80f);
                    float cd = wd.cd0 + wd.k_cl * c.cl[i] * c.cl[i] + 0.4f * over * over;
                    float drag = c.q[i] * Flight.area(wd) * cd / wd.mass;
                    add3(c.acc, i, -v3(c.vhat, i) * drag);
                }
            }
        }
    }

    /// <summary>Thrust, along the nose, for the motor's window: the motor burns flat out
    /// between `ignite` and `ignite + burn`, and not at all at the structure's ceiling,
    /// where the cue is already out.</summary>
    public sealed class ThrustColumns : Columns<int>
    {
        public float[] acc = null!, nose = null!, age = null!, vel = null!;
        public byte[] wid = null!;
        public float[] hit_t = null!;

        internal static readonly string[] names =
            { "acc", "nose", "proj_age", "vel", "proj_wid", "first_hit_t" };

        public ThrustColumns(Group g) : base(g) { bind(g); }

        public override string[] Names => names;

        protected override void bind(Group g)
        {
            acc = g.f("acc"); nose = g.f("nose"); age = g.f("proj_age"); vel = g.f("vel");
            wid = g.u8("proj_wid"); hit_t = g.f("first_hit_t");
        }

        public override int row(int i) => i;
    }

    public static class Thrust
    {
        public static void run(World world, float dt)
        {
            foreach (var g in world.ecs.groups(ThrustColumns.names))
            {
                var c = new ThrustColumns(g);
                for (int i = 0; i < c.n; i++)
                {
                    if (!flying(c.hit_t[i])) continue;
                    var wd = Defs.WEAPONS[c.wid[i]];
                    // the age the round had when it flew this tick: `Expire` has already
                    // counted the second, and the motor's window is `ignite` .. `+burn`
                    float age = c.age[i] - dt;
                    if (age <= wd.ignite || age > wd.ignite + wd.burn) continue;
                    if (v3(c.vel, i).len >= wd.maxvel) continue;
                    float push = wd.thrust;
                    add3(c.acc, i, v3(c.nose, i) * push);
                }
            }
        }
    }

    /// <summary>Gravity: the one force that asks the round nothing.</summary>
    public sealed class GravityColumns : Columns<int>
    {
        public float[] acc = null!;
        public float[] hit_t = null!;

        internal static readonly string[] names = { "acc", "first_hit_t" };

        public GravityColumns(Group g) : base(g) { bind(g); }

        public override string[] Names => names;

        protected override void bind(Group g)
        {
            acc = g.f("acc"); hit_t = g.f("first_hit_t");
        }

        public override int row(int i) => i;
    }

    public static class Gravity
    {
        public static void run(World world, float dt)
        {
            foreach (var g in world.ecs.groups(GravityColumns.names))
            {
                var c = new GravityColumns(g);
                for (int i = 0; i < c.n; i++)
                {
                    if (!flying(c.hit_t[i])) continue;
                    c.acc[i * 3 + 1] -= Flight.GRAVITY;
                }
            }
        }
    }

    // ------------------------------------------------------------------
    // and the integration: the one stage that spends the sum
    // ------------------------------------------------------------------

    /// <summary>The round's own columns plus the accumulator, because a step needs
    /// where it starts, where it is going and how fast.</summary>
    public sealed class IntegrateColumns : RoundColumns
    {
        public float[] acc = null!;

        internal static readonly string[] airframe_names = Components.Cat(names, "acc");

        public IntegrateColumns(Group g) : base(g) { }

        public override string[] Names => airframe_names;

        protected override void bind(Group g)
        {
            base.bind(g);
            acc = g.f("acc");
        }
    }

    public static class Integrate
    {
        public static void run(World world, float dt)
        {
            var e = world.ecs;
            foreach (var g in e.groups(IntegrateColumns.airframe_names))
            {
                var c = new IntegrateColumns(g);
                foreach (var r in c)
                {
                    if (!flying(r.hit_t)) continue;
                    var wd = Defs.WEAPONS[r.wid];
                    var p0 = r.pos;
                    var vel = r.vel;
                    var acc = v3(c.acc, r.index);
                    var v = new Vec3(vel.x + acc.x * dt, vel.y + acc.y * dt,
                                     vel.z + acc.z * dt);
                    // the structure's ceiling, not the motor's: past `maxvel` the
                    // velocity keeps its direction and loses the excess
                    float speed = v.len;
                    if (speed > wd.maxvel) v = v * (wd.maxvel / speed);
                    _move(world, r, p0, v, dt);
                }
            }
        }
    }
}
