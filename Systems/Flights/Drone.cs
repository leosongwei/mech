// The loitering munition: one round that searches for its own target, and the
// governed motor that keeps it in the air while it does.
//
// Two stages, because they answer two questions that do not touch.  `Loiter` is the
// guidance: what this round wants to fly at, and where it looks.  `Thrust.Commanded`
// is the motor: how far open it is.  Everything else in the flight block is the
// airframe the other three winged rounds already fly - airflow, angle of attack,
// lift, drag, gravity and the one integration - and none of it changes for this
// kind: what a munition is, is the columns it owns (`orbit`) and the tag it carries
// (`drone`).
//
// The law has no phase variable.  Four behaviours are read off the state every tick:
//
//   * no lock      the search.  The optical axis is `DRONE_LOOK_DOWN` below the
//                  horizon and swings `DRONE_SCAN` either side of the *current*
//                  heading on a triangle wave of the round's own age, so the ground
//                  it sweeps keeps up with the ground it has covered
//                  (`docs/plans/drone.md` §3.3) and the phase is one number.
//   * no lock      fly to `orbit`, or round it once inside `DRONE_ORBIT_R`: the
//                  distance to that point is the whole criterion, not a stored mode.
//   * lock, far    pure pursuit of the point the seeker believes in, holding the
//                  height band.
//   * lock, close  true proportional navigation inside `pn_r`, three-quarters of a
//                  kilometre above the ground and falling: no terrain following, the
//                  dive is the whole point of being here.
//
// What every one of those writes is the same pair the airframe reads: `a_dir` and
// `a_want`, a lift the wing is asked for.  Where the round is, how fast it is going
// and how high it is are all consequences of that, three stages later.

namespace Mech.Server.Systems;

public static partial class Flights
{
    // The guidance's gains, in the place `Guidance.Top` keeps its own (`K_H`,
    // `K_P`): what belongs to the law lives on the law, and what belongs to the
    // airframe lives in `Defs`.
    private const float K_TURN = 3.0f;    // 1/s, heading error -> demand
    private const float K_AGL = 0.15f;    // 1/s^2, height error -> acceleration
    private const float K_VY = 0.70f;     // 1/s, climb rate -> damping (2*sqrt(K_AGL))
    private const float U_TRIM = 0.66f;   // throttle that holds the cruise
    private const float K_U = 0.15f;      // throttle per m/s of speed deficit

    /// <summary>A loitering round's columns: the round's own, plus the point it
    /// circles, the hull the seeker has taken, and the demand the law writes.
    /// `OnDrone` is the query, so the other four kinds pay nothing for this
    /// stage.</summary>
    public sealed class DroneColumns : RoundColumns
    {
        public float[] orbit = null!;
        public int[] tgt = null!;
        public float[] adir = null!, awant = null!, tvel = null!;

        private static readonly string[] drone_names =
            Components.Cat(names, "orbit", "target", "tgt_vel", "a_dir", "a_want");
        public static readonly string[] OnDrone = Components.Cat(drone_names, "drone");

        public DroneColumns(Group g) : base(g) { }

        public override string[] Names => drone_names;

        protected override void bind(Group g)
        {
            base.bind(g);
            orbit = g.f("orbit");
            tgt = g.i("target"); tvel = g.f("tgt_vel");
            adir = g.f("a_dir"); awant = g.f("a_want");
        }

        public Vec3 orbit_at(int i) => new(orbit[i * 3], orbit[i * 3 + 1], orbit[i * 3 + 2]);

        /// <summary>The velocity the stare point walks at, row `i`.</summary>
        public Vec3 tvel_at(int i) => new(tvel[i * 3], tvel[i * 3 + 1], tvel[i * 3 + 2]);
    }

    public static partial class Guidance
    {
        public static class Loiter
        {
            public static void run(World world, float dt)
            {
                var e = world.ecs;
                foreach (var g in e.groups(DroneColumns.OnDrone))
                {
                    var c = new DroneColumns(g);
                    foreach (var r in c)
                    {
                        if (!flying(r.hit_t)) continue;
                        var wd = Defs.WEAPONS[r.wid];
                        var p0 = r.pos;
                        var vel = r.vel;
                        float speed = vel.len;
                        var vhat = speed > 1e-3f ? vel / speed : new Vec3(1f, 0f, 0f);
                        // `Expire` has already counted this tick's second
                        float age = r.age - dt;

                        // ---- what it is looking at, and for --------------------
                        Vec3? spot = null;
                        if (c.tgt[r.index] >= 0)
                        {
                            spot = Seeker.track(world, r.eid, p0, dt);
                        }
                        else
                        {
                            var look = MathUtil.dir_from(_heading(vhat) + _swing(age),
                                                         -Defs.DRONE_LOOK_DOWN);
                            spot = Seeker.scan(world, r.eid, p0, look, Defs.DRONE_HALF_FOV);
                        }

                        // ---- what it wants ------------------------------------
                        var want = Vec3.zero;
                        if (spot is not null && _plan(p0, spot.Value) <= wd.pn_r)
                        {
                            // the terminal dive: the same law the top attack flies
                            // inside its own `pn_r`, and no height channel at all.  The
                            // weight goes in with it, so that what is left of gravity
                            // *after the projection below* is only the part that would
                            // curve the flight path - taken out here, or the round
                            // over-dives and arrives in the dirt short of the hull
                            want = _pn_accel(p0, vel, spot.Value, c.tvel_at(r.index),
                                             wd.pn_n) + new Vec3(0f, Flight.GRAVITY, 0f);
                        }
                        else
                        {
                            want = _cruise(world, p0, vel, spot, c.tvel_at(r.index),
                                           c.orbit_at(r.index), wd);
                        }

                        // the wing answers a lift, square to the flight path, capped
                        // by what the airframe can make at this speed.  When it cannot
                        // make all of it, the height channel is paid first and the turn
                        // gets what is left: a bank that eats the lift it needs to stay
                        // up spirals into the ground, and unloading it is what a pilot
                        // does with the same choice
                        want = want - vhat * want.dot(vhat);
                        float q = 0.5f * Flight.RHO * speed * speed;
                        float lift_max = lift_accel(wd, q, wd.cl_max);
                        if (want.len > lift_max)
                        {
                            float budget = MathF.Sqrt(MathF.Max(0f, lift_max * lift_max
                                                                - want.y * want.y));
                            var flat = new Vec3(want.x, 0f, want.z);
                            float f = flat.len;
                            if (f > 1e-6f) flat = flat / f * MathF.Min(f, budget);
                            want = new Vec3(flat.x, want.y, flat.z);
                        }
                        float mag = MathF.Min(want.len, lift_max);
                        set3(c.adir, r.index, mag > 1e-6f ? want / want.len : Vec3.zero);
                        c.awant[r.index] = mag;
                    }
                }
            }

            /// <summary>The search: the axis swings `DRONE_SCAN` either side of the
            /// heading, one 80 deg pass in 2 s.  `_tri` has a period of two, so one
            /// sweep out and one back is four seconds of flight.</summary>
            private static float _swing(float age)
                => Defs.DRONE_SCAN * _tri(age / 2.0f);

            /// <summary>Triangle wave in [-1, 1], period 2: one pass from one side to
            /// the other takes 1 (i.e. 2 s of `_swing`'s argument).</summary>
            private static float _tri(float t)
            {
                float u = t - MathF.Floor(t);
                return u < 0.5f ? 4f * u - 1f : 3f - 4f * u;
            }

            /// <summary>The plan-view heading this round is flying at.</summary>
            private static float _heading(Vec3 vhat)
                => vhat.horiz > 1e-3f ? MathF.Atan2(vhat.z, vhat.x) : 0f;

            /// <summary>Plan-view distance: the height channel owns the vertical, so
            /// "how far to go" is measured on the map.</summary>
            private static float _plan(Vec3 a, Vec3 b)
                => MathF.Sqrt((a.x - b.x) * (a.x - b.x) + (a.z - b.z) * (a.z - b.z));

            /// <summary>The heading channel: the horizontal lift that closes this
            /// round's track error toward a plan-view direction, sized by
            /// `K_TURN · v · beta` - the same shape `Guidance.Top` turns its pure
            /// pursuit with.  `to_aim` is a point or a direction; only its plan-view
            /// part is read.</summary>
            private static Vec3 _turn(Vec3 vhat, float speed, Vec3 to_aim)
            {
                var flat = new Vec3(to_aim.x, 0f, to_aim.z);
                float d = flat.len;
                if (d < 1e-3f) return Vec3.zero;
                var want = flat / d;
                float beta = MathF.Asin(MathF.Min(1f, vhat.cross(want).len));
                return _lift_dir(vhat, want) * (K_TURN * speed * beta);
            }

            /// <summary>Not diving: turn toward `aim` (or round the circle it is
            /// already on) and hold the height band.
            ///
            /// The turn is the top attack's own heading channel - the lift direction
            /// that closes the error, sized by `K_TURN · v · beta` - and the height is
            /// `K_AGL` times the error against the ground 300 m ahead, which is what
            /// filters the 16 m facets and gives the climb time to happen
            /// (`docs/plans/drone.md` §3.5).  The two are added as vectors and the
            /// result is one demand: a banking round that is also climbing is exactly
            /// the vector sum of the two, and `lift_max` is what limits it.</summary>
            private static Vec3 _cruise(World world, Vec3 p0, Vec3 vel, Vec3? spot,
                                        Vec3 tvel, Vec3 orbit, WeaponDef wd)
            {
                float speed = vel.len;
                var vhat = speed > 1e-3f ? vel / speed : new Vec3(1f, 0f, 0f);
                var a = Vec3.zero;
                bool circle = spot is null && _plan(p0, orbit) <= Defs.DRONE_ORBIT_R;
                if (spot is not null)
                {
                    // a hull it has taken, still far out: null the rotation of the line
                    // to it, the same law the dive ends in - turning onto the hull's
                    // current bearing instead would fly a curve, and at 41.7 m/s the
                    // curve is most of the motor
                    a = _pn_level(p0, vel, spot.Value, tvel, wd.pn_n);
                }
                else if (circle)
                {
                    // A circle is followed as a tangent plus a radial correction: the
                    // track turns to run round the point, and the error on the radius
                    // bends it back onto the ring.  Aiming at the centre instead would
                    // be right only for a round already on the exact radius - one that
                    // drifted inside would be steered further in, through the middle
                    // and out the other side.
                    var to_c = new Vec3(p0.x - orbit.x, 0f, p0.z - orbit.z);
                    float d = to_c.len;
                    if (d > 1e-3f)
                    {
                        var rhat = to_c / d;                        // centre -> round
                        var turn = new Vec3(-rhat.z, 0f, rhat.x);   // a quarter turn on
                        // outside the ring: pull in; inside: pull out; on it: no radial
                        // term at all, and the track is the circle's own tangent
                        float off = MathUtil.clamp((d - Defs.DRONE_ORBIT_R)
                                                   / Defs.DRONE_ORBIT_R, -1f, 1f);
                        a = _turn(vhat, speed, turn - rhat * off);
                    }
                }
                else
                {
                    // at the point it was told to circle, level: a stationary point is
                    // a straight line, and pursuit of one costs nothing
                    a = _turn(vhat, speed, orbit - p0);
                }
                // the height channel: the ground it is about to fly over, plus the band
                var hdir = new Vec3(vhat.x, 0f, vhat.z);
                float hn = hdir.len;
                hdir = hn > 1e-3f ? hdir / hn : new Vec3(1f, 0f, 0f);
                float ground = world.terrain.height_at(p0.x + hdir.x * Defs.DRONE_LOOK_AHEAD,
                                                       p0.z + hdir.z * Defs.DRONE_LOOK_AHEAD);
                float err = ground + Defs.DRONE_AGL - p0.y;
                // Level flight is the weight, and the height channel is a spring with
                // no damping of its own: a round launched from the ground would swing
                // through the band and back for as long as its battery lasted.  `K_VY`
                // takes the climb rate out, at twice the square root of `K_AGL` - the
                // gain that leaves the band in one approach instead of three.
                return new Vec3(a.x, a.y + Flight.GRAVITY + K_AGL * err - K_VY * vhat.y * speed, a.z);
            }
        }
    }

    public static partial class Thrust
    {
        /// <summary>The motor that never goes out: the governor the loitering
        /// munition flies behind, and the reason its `ignite` and `burn` are both
        /// zero - `Thrust` above asks "is the window open", and this round has no
        /// window to open.
        ///
        /// One line of control and one multiplication.  The throttle answers the
        /// *speed error*, not the climb: pulled up into a climb, gravity takes the
        /// speed first, the error opens the throttle, and the excess thrust over the
        /// level-flight requirement is what climbs.  `U_TRIM` is the 0.66 that holds
        /// 41.7 m/s and `K_U` the gain that puts the motor against the stop inside
        /// five metres a second (`docs/plans/drone.md` §3.5).</summary>
        public static class Commanded
        {
            public static void run(World world, float dt)
            {
                foreach (var g in world.ecs.groups(ThrustColumns.OnDrone))
                {
                    var c = new ThrustColumns(g);
                    for (int i = 0; i < c.n; i++)
                    {
                        if (!flying(c.hit_t[i])) continue;
                        var wd = Defs.WEAPONS[c.wid[i]];
                        float speed = v3(c.vel, i).len;
                        if (speed >= wd.maxvel) continue;   // the structure's ceiling
                        float u = U_TRIM + K_U * (Defs.DRONE_CRUISE - speed);
                        if (u <= 0f) continue;
                        if (u > 1f) u = 1f;
                        add3(c.acc, i, v3(c.nose, i) * (wd.thrust * u));
                    }
                }
            }
        }
    }
}
