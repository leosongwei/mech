// The legs: how hard they push, and where.
//
// The machine is an engine on legs, so what it makes is a *power* and the force is
// that power over the speed it is doing - `Defs.MECH_HP_LOW` at a standstill rising
// to `Defs.MECH_HP_HIGH` at the rated 180 km/h.  At speed that settles to a steady
// shove; at low speed it would run away, so the drive has a **g limit** of its own
// (`MECH_G_LIMIT`, 1.5 g, with gravity counted apart from it) and everything below
// the speed where the two cross is a flat 1.5 g.  That is exactly the difference
// between an engine and a constant thrust: the low end pulls at the machine's own
// limit, the top end coasts to its ceiling.
//
// One actuator, three things it can be doing.  Under a command it pushes along
// that command's bearing with whatever the curve gives at this speed; over the
// speed it was asked for it plants and pushes back; with no command at all it
// plants and stops the hull where it stands.
//
// The bearing is the whole of the steering: a pilot's command comes off his own
// hull's frame (`input/sight.js`), so W pushes along the machine and A pushes
// square across it, and the way the hull is *going* is whatever the push has bent
// it into - at 1.5 g that bend is `a/v` wide, which is why a heavy machine crosses
// its own track through a turn instead of tracking it.
//
// A command at the machine's *own top speed* is the stick pushed all the way: the
// governor stands down, the legs give everything they have, and the hill and the
// drag decide what comes out.  Anything slower is a cruise speed, which the
// governor holds - and it holds it by never taking more than the gap that is left,
// because at 1.5 g a tick's worth of thrust is a real fraction of a whole band of
// speed and a governor that overshot would rattle around its target.
//
// The push is measured along the slope it is aimed up, so the hill takes its cut
// first: the horizontal share is `cos(slope)` of it and the rest goes into the
// ground.

namespace Mech.Server.Systems.Movement;

public static partial class Mech
{
    /// <summary>The legs' push: thrust on the command's bearing, or a brake that
    /// plants them.</summary>
    public static class Thrust
    {
        public static void run(World world, float dt)
        {
            var terrain = world.terrain;
            foreach (var g in world.ecs.groups(MoveColumns.OnMech))
            {
                var c = new MoveColumns(g);
                foreach (var r in c)
                {
                    var v = r.vel;
                    var cmd = r.vel_cmd;
                    float cs = MathF.Sqrt(cmd.x * cmd.x + cmd.z * cmd.z);
                    if (cs < 0.05f)
                    {
                        // nothing asked for: the legs plant.  Drag alone takes forty
                        // seconds to shed 180 km/h, and a hull that coasts that long
                        // reads as a dead one
                        Stop(r, v, dt);
                        continue;
                    }
                    float speed = MathF.Sqrt(v.x * v.x + v.z * v.z);
                    // the push goes where the machine was told to push, and it is the
                    // push that bends the way the hull is going
                    float dx = cmd.x / cs, dz = cmd.z / cs;
                    float top = Defs.UNIT_DEFS[r.etype].speed;
                    float push = Force(speed) / Defs.MECH_MASS;     // m/s^2 the legs can make here
                    float a;
                    float err = 0f;
                    bool governed = cs < top - 0.5f;
                    if (!governed)
                    {
                        a = push * Legs(r, dx, dz);
                    }
                    else
                    {
                        // the governor: a cruise speed is held by pushing while the
                        // hull is short of it and planting when it is over.  The speed
                        // it holds is the share of it going the way it was told, which
                        // is what keeps a cruise *through* a turn (the slip is the
                        // machine's own business, not part of the order)
                        err = cs - (v.x * dx + v.z * dz);
                        float u = Math.Clamp(err / Defs.MECH_GOVERNOR_BAND, -1f, 1f);
                        a = u >= 0f ? push * Legs(r, dx, dz) * u : Defs.MECH_BRAKE * u;
                        if (a == 0f) continue;
                    }
                    float s = terrain.slope_along(r.pos.x, r.pos.z, dx, dz);
                    float step = a * dt / MathF.Sqrt(1f + s * s);
                    if (governed)
                    {
                        // the gap is the limit: the last metre per second of a cruise
                        // speed is taken, never jumped over
                        step = err >= 0f ? MathF.Min(step, err) : MathF.Max(step, err);
                    }
                    r.vel = new Vec3(v.x + dx * step, v.y, v.z + dz * step);
                }
            }
        }

        /// <summary>The force the legs can put down at `speed` (m/s), in newtons:
        ///
        /// the power curve over the speed, and never more than the machine's own
        /// **g limit** - `MECH_G_LIMIT` of push, which gravity is counted apart from.
        /// The cap is what the legs do from a standstill, and the curve takes over
        /// above the speed where the two cross (about 76 km/h).
        ///
        /// Public because it is *the* law of the mech's drive - `--check drive`
        /// solves every balance speed with it, so the check and the simulation
        /// cannot describe two different machines.</summary>
        public static float Force(float speed)
        {
            float v = MathF.Max(speed, 0.5f);
            float hp = Defs.MECH_HP_LOW
                       + (Defs.MECH_HP_HIGH - Defs.MECH_HP_LOW)
                         * MathF.Min(v / Defs.MECH_RATED_V, 1f);
            return MathF.Min(hp * Defs.HORSEPOWER / v,
                             Defs.MECH_G_LIMIT * Defs.GRAVITY * Defs.MECH_MASS);
        }

        /// <summary>How much of the machine the legs can put behind a bearing: full
        /// with them lined up under it, `MECH_LEG_ALIGN` of it at right angles, and a
        /// fifth of what is left once the chassis is out.  A hull told to push square
        /// across itself plants its feet first and then leans on them, which is the
        /// ramp this factor is (`Integrate.turn_legs`).</summary>
        private static float Legs(in MoveRow r, float dx, float dz)
        {
            float align = MathF.Cos(MathUtil.wrap_pi(MathF.Atan2(dz, dx) - r.leg_yaw));
            float f = Defs.MECH_LEG_ALIGN + (1f - Defs.MECH_LEG_ALIGN) * MathF.Max(0f, align);
            return r.hp <= 0f ? f * Defs.MECH_BROKEN : f;
        }

        /// <summary>Plant the legs: shed `MECH_BRAKE` off the hull's speed, and stop
        /// it dead rather than let a brake tick reverse it.</summary>
        private static void Stop(in MoveRow r, Vec3 v, float dt)
        {
            float speed = MathF.Sqrt(v.x * v.x + v.z * v.z);
            float brake = Defs.MECH_BRAKE * dt;
            if (speed <= brake)
            {
                r.vel = new Vec3(0f, v.y, 0f);
                return;
            }
            float k = (speed - brake) / speed;
            r.vel = new Vec3(v.x * k, v.y, v.z * k);
        }
    }
}
