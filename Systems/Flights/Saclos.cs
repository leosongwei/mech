// A beam rider: it rides one line of its launcher's laser, which `Systems/Beams.cs`
// has laid on a locked target and leads for it.  The round knows only the line, so
// four of them can be in the air on four lines of the same beam.
//
// `Guidance` is the stage between the beam and the airframe, and it is where one round
// differs from another: it turns what the round is told into the two values the wing
// answers (`a_dir`, `a_want`) and, for a beam rider, the crosswind the motor pushes
// across the line.  Everything downstream - lift, drag, thrust, gravity, integration -
// is the same seven passes whatever kind of round it is.
//
// The law is a command-to-line-of-sight ride and it has no arc in it: the round is told
// where the laser *is* - the ray from the launcher's aperture to the point
// `Beams.intercept` leads the target to - and its whole job is to sit on that ray.
//
// Two things take the line away, and both leave the round flying on its heading - the
// motor and the drag go on, the guidance is what stops: the launcher dying (nothing is
// designating), and the laser not reaching the round any more (a ridge or a house
// between the launcher and the missile cut a beam that is only light).  A round with no
// line has no lift demand either, so it keeps its heading and falls like anything else
// without a wing.

namespace Mech.Server.Systems;

public static partial class Flights
{
    public static partial class Guidance
    {
        public static class Saclos
        {
            private const float CLOS_K = 16f;        // offset gain, in `t_go` units
                                                     // (the law's damping is sqrt(K)/2)
            private const float CLOS_T_MIN = 0.15f;  // s, floor on the time left (it divides)

            public static void run(World world, float dt)
            {
                var e = world.ecs;
                foreach (var g in e.groups(SaclosColumns.OnSaclos))
                {
                    var c = new SaclosColumns(g);
                    foreach (var r in c)
                    {
                        if (!flying(r.hit_t)) continue;
                        var wd = Defs.WEAPONS[r.wid];
                        var p0 = r.pos;
                        var vel = r.vel;
                        float speed = vel.len;

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
                                // against, and what the crosswind pushes square to
                                line = _beam_line(aperture, aim);
                            }
                        }
                        var (dir, mag) = _demand(p0, vel, aperture, aim, wd);
                        set3(c.adir, r.index, dir);
                        c.awant[r.index] = mag;
                        // thrust misalignment / crosswind: a push square to the sight
                        // line, seeded per round, that the guidance has to chase down.
                        // Bounded because the beam always wins.
                        add3(c.acc, r.index, _wobble_accel(wd, line, r.age - dt, r.seed));
                    }
                }
            }

            /// <summary>The direct law's demand: the pull back onto the laser line, plus
            /// the weight the wing is carrying.
            ///
            /// The offset from the line, and the rate it is growing at, are nulled over
            /// the time left before the target is reached
            /// (`a = K(d + ḋ·t_go)/t_go²`).  A round that only chases the *point* lags
            /// the beam for its whole flight - measured, 18 m off at 3 km with the round
            /// arriving with no time left.
            ///
            /// A missile is not a rock: it carries itself, so `g` in the plane square to
            /// the flight path is part of the demand, and the angle of attack that buys
            /// it is real angle of attack - induced drag included.  That is the whole of
            /// "the round sags when it is slow": under about 100 m/s the wing cannot
            /// make 1 g any more at all, and it drops out of the beam with no authority
            /// to get back into it.</summary>
            private static (Vec3 dir, float mag) _demand(Vec3 p, Vec3 v, Vec3 origin,
                                                         Vec3? aim, WeaponDef wd)
            {
                if (aim is null) return (Vec3.zero, 0f);
                var (vhat, q) = Flight.airflow(v);
                float speed = v.len;
                float lift_max = Flight.lift_accel(wd, q, wd.cl_max);
                // the beam: where the round is along it, how far off it, and how fast
                // that gap is opening
                var to_aim = aim.Value - origin;
                float line_len = to_aim.len;
                var line = line_len > 1e-3f ? to_aim.unit : new Vec3(1f, 0f, 0f);
                var from_origin = p - origin;
                float along = from_origin.dot(line);
                var off = from_origin - line * along;
                var off_rate = v - line * v.dot(line);
                // the time this round has left to be on the line: what is left of the
                // beam, over the speed it is getting there at
                float t_go = MathF.Max((line_len - along) / MathF.Max(speed, 1f),
                                       CLOS_T_MIN);
                float k = CLOS_K / (t_go * t_go);
                var a = -(off + off_rate * t_go) * k;
                // the weight, in the plane the wing can push in: on a level round that
                // is the full 1 g straight up, on a diving one only its
                // square-to-the-path part
                float gv = Flight.GRAVITY * vhat.y;      // g, along the flight path
                a = new Vec3(a.x - vhat.x * gv, a.y + Flight.GRAVITY - vhat.y * gv,
                             a.z - vhat.z * gv);
                a = a - vhat * a.dot(vhat);
                float mag = MathF.Min(a.len, lift_max);
                return (mag > 1e-9f ? a.unit : Vec3.zero, mag);
            }

            /// <summary>The line of the beam, from the launcher's aperture to the point
            /// the laser leads to.  Zero when there is no beam, which is also what
            /// silences the crosswind below.</summary>
            private static Vec3 _beam_line(Vec3 origin, Vec3? aim)
            {
                if (aim is null) return Vec3.zero;
                return (aim.Value - origin).unit;
            }

            /// <summary>m/s² of crosswind: thrust misalignment, seeded per round, pushing
            /// square to the sight line at `wob` amplitude.  The guidance has to chase
            /// it down, and it is bounded because the beam always wins.  Zero for a round
            /// with no line to be pushed across.</summary>
            private static Vec3 _wobble_accel(WeaponDef wd, Vec3 line, float age, float seed)
            {
                float side = line.horiz;
                if (wd.wob == 0f || side <= 1e-3f) return Vec3.zero;
                float f = wd.wob_f * (0.75f + 0.5f * seed);
                float ph = seed * MathUtil.TAU;
                float am = wd.wob * (0.7f + 0.6f * ((seed * 3.7f) % 1f))
                           * (MathF.Sin(age * f * 2.1f + ph)
                              + 0.55f * MathF.Sin(age * f * 5.3f + ph * 2.3f));
                return new Vec3(-line.z / side * am, 0f, line.x / side * am);
            }
        }
    }
}
