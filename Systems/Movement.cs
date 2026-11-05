// Ground movement: integrates velocity, follows the terrain, drives the legs.
//
// Ported from `backend/server/systems/movement.py`.
//
// This is the only writer of a hull's `vel` (`Ecs/Components.cs`), and the rule
// it keeps is what makes the component worth having: the velocity it writes is
// the one the hull *actually moved at* - the ramp the mech made, the arc the car
// drove, what the collision let through - so the sweep table, the seeker's lead
// and the wire may all read it without asking what kind of thing it is.
//
// The intent arrives in `vel_cmd` and the gap between the two *is* the machine:
// a mech rolls up to its ordered speed at `MECH_ACCEL`, an APC can only honour
// the part of the order that lies along its own hull, and a man is his legs.
//
// Nothing in here reads another entity and nothing draws a random number, so
// every pass is archetype-major (`ecs.groups`): the buffers are hoisted once per
// group and the loop only walks rows.

namespace Mech.Server.Systems;

public static class Movement
{
    const float TILE = Terrain.TILE;

    // ------------------------------------------------------------------
    // the columns a locomotion pass needs
    // ------------------------------------------------------------------

    /// <summary>What a leg follows: the command, the leg's own state, and the
    /// type that decides how fast it may swing.  A wheeled hull is skipped whole -
    /// its `leg_yaw` is its steering state, not a decoration that follows the
    /// velocity.</summary>
    public sealed class SteerColumns : Columns<SteerRow>
    {
        // bound by `bind`, which the constructor calls: hence the `null!`
        public float[] vel_cmd = null!, leg_yaw = null!;
        public byte[] etype = null!;
        public bool has_vehicle;

        internal static readonly string[] names = { "moves", "vel_cmd", "leg_yaw", "etype" };

        public SteerColumns(Group g) : base(g) { bind(g); }

        public override string[] Names => names;

        protected override void bind(Group g)
        {
            vel_cmd = g.f("vel_cmd"); leg_yaw = g.f("leg_yaw");
            etype = g.u8("etype");
            has_vehicle = g.has("vehicle");
        }

        public override SteerRow row(int i) => new SteerRow(this, i);
    }

    /// <summary>One hull's legs: what it was told, where its legs point.</summary>
    public readonly struct SteerRow : IRow<SteerColumns>
    {
        private readonly SteerColumns _c;
        public readonly int i;

        public SteerRow(SteerColumns c, int i) { _c = c; this.i = i; }

        public SteerColumns columns => _c;
        public int index => i;
        public int eid => _c.ids[i];
        public bool has_vehicle => _c.has_vehicle;
        public byte etype => _c.etype[i];

        public Vec3 vel_cmd
        {
            get => new(_c.vel_cmd[i * 3], _c.vel_cmd[i * 3 + 1], _c.vel_cmd[i * 3 + 2]);
            set { _c.vel_cmd[i * 3] = value.x; _c.vel_cmd[i * 3 + 1] = value.y; _c.vel_cmd[i * 3 + 2] = value.z; }
        }
        public ref float leg_yaw => ref _c.leg_yaw[i];
    }

    /// <summary>Everything one tick of locomotion touches: the place, the truth,
    /// the command, and the machine's own state (`wheel_speed`, `backout_t`,
    /// `blocked_t`).</summary>
    public sealed class MoverColumns : Columns<MoverRow>
    {
        // bound by `bind`, which the constructor calls: hence the `null!`
        public float[] pos = null!, vel = null!, vel_cmd = null!;
        public float[] wheel_speed = null!, backout_t = null!, blocked_t = null!;
        public float[] leg_yaw = null!, yaw = null!, hp = null!;
        public byte[] etype = null!;
        public bool has_vehicle;

        internal static readonly string[] names =
        {
            "moves", "pos", "vel", "vel_cmd", "wheel_speed", "backout_t", "blocked_t",
            "leg_yaw", "yaw", "hp", "etype",
        };

        public MoverColumns(Group g) : base(g) { bind(g); }

        public override string[] Names => names;

        protected override void bind(Group g)
        {
            pos = g.f("pos"); vel = g.f("vel"); vel_cmd = g.f("vel_cmd");
            wheel_speed = g.f("wheel_speed"); backout_t = g.f("backout_t");
            blocked_t = g.f("blocked_t"); leg_yaw = g.f("leg_yaw");
            yaw = g.f("yaw"); hp = g.f("hp");
            etype = g.u8("etype");
            has_vehicle = g.has("vehicle");
        }

        public override MoverRow row(int i) => new MoverRow(this, i);
    }

    /// <summary>One hull: where it stands, what it is doing, and what it was told.
    /// `pos` and `vel` are the truth `movement` is the only writer of.</summary>
    public readonly struct MoverRow : IRow<MoverColumns>
    {
        private readonly MoverColumns _c;
        public readonly int i;

        public MoverRow(MoverColumns c, int i) { _c = c; this.i = i; }

        public MoverColumns columns => _c;
        public int index => i;
        public int eid => _c.ids[i];
        public bool has_vehicle => _c.has_vehicle;
        public byte etype => _c.etype[i];

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
        public Vec3 vel_cmd
        {
            get => new(_c.vel_cmd[i * 3], _c.vel_cmd[i * 3 + 1], _c.vel_cmd[i * 3 + 2]);
            set { _c.vel_cmd[i * 3] = value.x; _c.vel_cmd[i * 3 + 1] = value.y; _c.vel_cmd[i * 3 + 2] = value.z; }
        }

        public ref float wheel_speed => ref _c.wheel_speed[i];
        public ref float backout_t => ref _c.backout_t[i];
        public ref float blocked_t => ref _c.blocked_t[i];
        public ref float leg_yaw => ref _c.leg_yaw[i];
        public ref float yaw => ref _c.yaw[i];
        public ref float hp => ref _c.hp[i];
    }

    /// <summary>Whether a hull of radius `rad` cannot stand at (x, z).
    ///
    /// The point test plus four samples on the circle: a mech's collision is a
    /// disc, and the tile under its centre says nothing about the wall its
    /// shoulder is against.  Trunks are thinner than those samples, so they are
    /// distance tested instead.</summary>
    static bool _blocked(Terrain terrain, float x, float z, float rad) =>
        terrain.blocked(x, z) || terrain.blocked(x + rad, z) ||
        terrain.blocked(x - rad, z) || terrain.blocked(x, z + rad) ||
        terrain.blocked(x, z - rad) ||
        terrain.trunk_blocked(x, z, rad);

    /// <summary>Swing every unit's legs/steering towards the direction it was
    /// told to go.
    ///
    /// Locomotion, not AI: the hull turns its own legs in every mode, which is
    /// why this lives here and not in the AI system.  Nothing here looks at
    /// another entity, so the whole archetype turns in one pass.
    ///
    /// It reads `vel_cmd`, the intent: a leg follows what the machine was asked
    /// to do, and the realised velocity is what `run` makes of it.</summary>
    static void _turn_legs(World world, float dt)
    {
        float rate_mech = 2.6f * dt;
        float rate_other = 6.0f * dt;
        foreach (var g in world.ecs.groups(SteerColumns.names))
        {
            var c = new SteerColumns(g);
            if (c.has_vehicle) continue;
            foreach (var r in c)
            {
                var cmd = r.vel_cmd;
                float vx = cmd.x;
                float vz = cmd.z;
                if (MathF.Abs(vx) < 0.05f && MathF.Abs(vz) < 0.05f) continue;
                float want = MathF.Atan2(vz, vx);
                float rate = r.etype == Defs.ETYPE_MECH ? rate_mech : rate_other;
                r.leg_yaw = MathUtil.turn_toward(r.leg_yaw, want, rate);
            }
        }
    }

    /// <summary>One tick of wheeled locomotion: ``(vx, vz)`` along the hull.
    ///
    /// The command is the same `vel_cmd` every other hull gets - a direction and
    /// a speed - but a car can only honour the part of it that lies along its own
    /// axis, so the direction becomes a steering angle and the speed a throttle.
    /// `leg_yaw` is the hull (state, not a rendering hint) and `wheel_speed` the
    /// signed speed rolling along it; `yaw` is mirrored from it because that is
    /// the field the client draws a carrier's hull on.  `backout_t` / `blocked_t`
    /// are the crew's own timers - see the two branches below.
    ///
    /// Two things fall out of that and are worth saying out loud: the hull cannot
    /// turn at a standstill (the yaw rate is speed / turn radius), and it never
    /// drives fast in reverse - the gearbox has `APC_REVERSE` of top speed and
    /// the only thing that uses it is backing off something the nose is against.
    /// A goal behind the hull is taken by turning round for it: the brake grows
    /// with the heading error, so a U-turn comes down to the radius rather than
    /// being swung at top speed.</summary>
    static (float vx, float vz) _vehicle(MoverRow r, float dt, float top)
    {
        var cmd = r.vel_cmd;
        float cx = cmd.x, cz = cmd.z;
        float u = r.wheel_speed;
        float yaw = r.leg_yaw;
        float mag = MathF.Sqrt(cx * cx + cz * cz);
        float err = 0f;
        float target = 0f;
        if (r.backout_t > 0f)
        {
            // the nose is against something: back off the way we came, wheels
            // held over so the hull is pointing somewhere else when it comes
            // back out
            r.backout_t = r.backout_t - dt;
            target = -top * Defs.APC_REVERSE;
            err = Defs.APC_STEER_LOCK;
        }
        else if (mag > 0.05f)
        {
            err = MathUtil.wrap_pi(MathF.Atan2(cz, cx) - yaw);
            // brake into a bend, and harder the worse the heading error is - a
            // couple of degrees of correction on a straight is not a reason to
            // give away any speed at all, a half turn is
            target = mag * (1f - 0.55f * MathF.Min(1f, MathF.Max(0f, MathF.Abs(err) - 0.4f) / 1.2f));
        }
        float acc;
        if (target * u >= 0f && MathF.Abs(target) > MathF.Abs(u))
        {
            // throttle: thrust minus drag, so the hull pulls hardest off the line
            // and the last few km/h take as long as the first thirty
            float ratio = u / Defs.APC_V_INF;
            acc = Defs.APC_THRUST * MathF.Max(0f, 1f - ratio * ratio);
        }
        else
        {
            acc = Defs.APC_BRAKE;
        }
        float step = acc * dt;
        u += MathF.Max(-step, MathF.Min(step, target - u));
        // steering: the wheels saturate at APC_STEER_LOCK of heading error, and a
        // hull turns at (speed / radius) - but never harder than APC_LAT_ACCEL of
        // side load, which is what stops it swinging from lock to lock at speed
        float rate = MathF.Min(MathF.Abs(u) / Defs.APC_TURN_R,
                               Defs.APC_LAT_ACCEL / MathF.Max(MathF.Abs(u), 0.5f));
        float steer = MathF.Max(-1f, MathF.Min(1f, err / Defs.APC_STEER_LOCK));
        yaw = MathUtil.wrap_pi(yaw + rate * steer * dt);
        r.wheel_speed = u;
        r.leg_yaw = yaw;
        r.yaw = yaw;
        return (MathF.Cos(yaw) * u, MathF.Sin(yaw) * u);
    }

    /// <summary>Integrate every hull one tick: read the intent, write the velocity.</summary>
    public static void run(World world, float dt)
    {
        var e = world.ecs;
        var groups = e.groups(MoverColumns.names);
        if (groups.Count == 0) return;
        _turn_legs(world, dt);

        var terrain = world.terrain;
        float maxx = terrain.w * TILE - 2f * TILE;
        float maxz = terrain.d * TILE - 2f * TILE;
        float inv_dt = 1f / dt;
        // `moves` is the query; every group below owns the whole unit bundle, so
        // the columns are bound once per group and the loop only walks rows
        foreach (var g in groups)
        {
            var c = new MoverColumns(g);
            bool wheeled = c.has_vehicle;
            foreach (var r in c)
            {
                int et = r.etype;
                var p = r.pos;
                float x = p.x;
                float y0 = p.y;
                float z = p.z;
                var cmd = r.vel_cmd;
                float cx = cmd.x;
                float cz = cmd.z;
                float vx, vz;
                if (wheeled)
                {
                    // an APC drives itself along its own hull: forward, back and
                    // turning, never sideways
                    (vx, vz) = _vehicle(r, dt, Defs.UNIT_DEFS[et].speed);
                }
                else if (et == Defs.ETYPE_MECH)
                {
                    // a mech rolls up to the speed it was asked for instead of
                    // snapping to it: 0-100 km/h takes about 3 s, so leaning on
                    // the stick is a surge rather than a teleport.  Legs lined up
                    // with the motion carry the hull all the way; a mech that
                    // fights its legs (or lost the chassis) tops out lower, and
                    // the ramp aims at that reduced speed.  It ramps `vel` itself
                    // - the ramp *is* the difference between the command and the
                    // truth.
                    float f;
                    if (MathF.Abs(cx) > 1e-3f || MathF.Abs(cz) > 1e-3f)
                    {
                        float align = MathF.Cos(MathUtil.wrap_pi(MathF.Atan2(cz, cx) - r.leg_yaw));
                        f = 0.45f + 0.55f * MathF.Max(0f, align);
                    }
                    else
                    {
                        f = 1f;
                    }
                    if (r.hp <= 0f) f *= 0.2f;
                    var vel = r.vel;
                    float ax = vel.x;
                    float az = vel.z;
                    float dx = cx * f - ax;
                    float dz = cz * f - az;
                    float step = MathF.Sqrt(dx * dx + dz * dz);
                    float limit = Defs.MECH_ACCEL * dt;
                    if (step > limit)
                    {
                        dx *= limit / step;
                        dz *= limit / step;
                    }
                    vx = ax + dx;
                    vz = az + dz;
                }
                else
                {
                    // a man is marched at the command: his machine is his legs
                    vx = cx;
                    vz = cz;
                }
                float nx = x, nz = z;
                if (MathF.Abs(vx) > 0.01f || MathF.Abs(vz) > 0.01f)
                {
                    nx = MathF.Min(MathF.Max(x + vx * dt, TILE), maxx);
                    nz = MathF.Min(MathF.Max(z + vz * dt, TILE), maxz);
                    float rad = Defs.UNIT_DEFS[et].radius * 0.6f;
                    if (_blocked(terrain, nx, nz, rad))
                    {
                        if (!_blocked(terrain, nx, z, rad)) nz = z;
                        else if (!_blocked(terrain, x, nz, rad)) nx = x;
                        else { nx = x; nz = z; }
                    }
                    // a hull that made no headway is against something, however
                    // the axis-wise slide dressed it up
                    float step = MathF.Sqrt((nx - x) * (nx - x) + (nz - z) * (nz - z));
                    if (wheeled)
                    {
                        // blocked means it asked for a stride and got a fraction
                        // of it - measuring absolute metres would call a slow
                        // crawl in the open a wall, and a heavy hull pulls away
                        // slowly
                        bool stuck = step < 0.3f * MathF.Sqrt(vx * vx + vz * vz) * dt;
                        // nose against something: count it, and once the crew has
                        // been pushing for a moment back out with the wheels held
                        // over, so the next try aims somewhere else
                        if (!stuck || r.backout_t > 0f)
                        {
                            r.blocked_t = 0f;                 // rolling again
                        }
                        else
                        {
                            r.blocked_t = r.blocked_t + dt;
                            if (r.blocked_t >= Defs.APC_BLOCKED)
                            {
                                r.backout_t = Defs.APC_BACKOUT;
                                r.blocked_t = 0f;
                                r.wheel_speed = 0f;
                            }
                        }
                    }
                    r.pos = new Vec3(nx, y0, nz);
                }
                // the velocity that actually came out of this tick, and the slope
                // it drove over - a hull on the ground has a vertical velocity too
                float ny = terrain.height_at(nx, nz);
                r.vel = new Vec3((nx - x) * inv_dt, (ny - y0) * inv_dt, (nz - z) * inv_dt);
                r.pos = new Vec3(nx, ny, nz);
            }
        }
    }
}
