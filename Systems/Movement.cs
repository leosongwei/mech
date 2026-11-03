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
        foreach (var g in world.ecs.groups("moves", "vel_cmd", "leg_yaw", "etype"))
        {
            // a wheeled hull is the exception: its `leg_yaw` is not a decoration
            // that follows the velocity, it *is* the steering state (`_vehicle`)
            if (g.has("vehicle")) continue;
            int n = g.n;
            var vel = g.f("vel_cmd");
            var leg = g.f("leg_yaw");
            var etype = g.u8("etype");
            for (int row = 0; row < n; row++)
            {
                float vx = vel[row * 3 + 0];
                float vz = vel[row * 3 + 2];
                if (MathF.Abs(vx) < 0.05f && MathF.Abs(vz) < 0.05f) continue;
                float want = MathF.Atan2(vz, vx);
                float rate = etype[row] == Defs.ETYPE_MECH ? rate_mech : rate_other;
                leg[row] = MathUtil.turn_toward(leg[row], want, rate);
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
    static (float vx, float vz) _vehicle(float[] cmd, float[] wheel, float[] backout,
                                         float[] blocked, float[] leg, float[] yaw_buf,
                                         int row, float dt, float top)
    {
        float cx = cmd[row * 3 + 0], cz = cmd[row * 3 + 2];
        float u = wheel[row];
        float yaw = leg[row];
        float mag = MathF.Sqrt(cx * cx + cz * cz);
        float err = 0f;
        float target = 0f;
        if (backout[row] > 0f)
        {
            // the nose is against something: back off the way we came, wheels
            // held over so the hull is pointing somewhere else when it comes
            // back out
            backout[row] = backout[row] - dt;
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
            float r = u / Defs.APC_V_INF;
            acc = Defs.APC_THRUST * MathF.Max(0f, 1f - r * r);
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
        wheel[row] = u;
        leg[row] = yaw;
        yaw_buf[row] = yaw;
        return (MathF.Cos(yaw) * u, MathF.Sin(yaw) * u);
    }

    /// <summary>Integrate every hull one tick: read the intent, write the velocity.</summary>
    public static void run(World world, float dt)
    {
        var e = world.ecs;
        var groups = e.groups("moves", "pos", "vel", "vel_cmd", "wheel_speed",
                              "backout_t", "blocked_t", "leg_yaw", "hp", "etype");
        if (groups.Count == 0) return;
        _turn_legs(world, dt);

        var terrain = world.terrain;
        float maxx = terrain.w * TILE - 2f * TILE;
        float maxz = terrain.d * TILE - 2f * TILE;
        float inv_dt = 1f / dt;
        // `moves` is the query; every group below owns the whole unit bundle, so
        // the buffers are taken once per group and the loop only walks rows
        foreach (var g in groups)
        {
            int n = g.n;
            var pos = g.f("pos");
            var vel = g.f("vel");
            var cmd = g.f("vel_cmd");
            var wheel = g.f("wheel_speed");
            var backout = g.f("backout_t");
            var blocked = g.f("blocked_t");
            var leg = g.f("leg_yaw");
            var yaw = g.f("yaw");
            var hp = g.f("hp");
            var etype = g.u8("etype");
            bool wheeled = g.has("vehicle");
            for (int row = 0; row < n; row++)
            {
                int et = etype[row];
                float x = pos[row * 3 + 0];
                float y0 = pos[row * 3 + 1];
                float z = pos[row * 3 + 2];
                float cx = cmd[row * 3 + 0];
                float cz = cmd[row * 3 + 2];
                float vx, vz;
                if (wheeled)
                {
                    // an APC drives itself along its own hull: forward, back and
                    // turning, never sideways
                    (vx, vz) = _vehicle(cmd, wheel, backout, blocked, leg, yaw, row,
                                        dt, Defs.UNIT_DEFS[et].speed);
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
                        float align = MathF.Cos(MathUtil.wrap_pi(MathF.Atan2(cz, cx) - leg[row]));
                        f = 0.45f + 0.55f * MathF.Max(0f, align);
                    }
                    else
                    {
                        f = 1f;
                    }
                    if (hp[row] <= 0f) f *= 0.2f;
                    float ax = vel[row * 3 + 0];
                    float az = vel[row * 3 + 2];
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
                        if (!stuck || backout[row] > 0f)
                        {
                            blocked[row] = 0f;                // rolling again
                        }
                        else
                        {
                            blocked[row] = blocked[row] + dt;
                            if (blocked[row] >= Defs.APC_BLOCKED)
                            {
                                backout[row] = Defs.APC_BACKOUT;
                                blocked[row] = 0f;
                                wheel[row] = 0f;
                            }
                        }
                    }
                    pos[row * 3 + 0] = nx;
                    pos[row * 3 + 2] = nz;
                }
                // the velocity that actually came out of this tick, and the slope
                // it drove over - a hull on the ground has a vertical velocity too
                float ny = terrain.height_at(nx, nz);
                vel[row * 3 + 0] = (nx - x) * inv_dt;
                vel[row * 3 + 1] = (ny - y0) * inv_dt;
                vel[row * 3 + 2] = (nz - z) * inv_dt;
                pos[row * 3 + 1] = ny;
            }
        }
    }
}
