// A beam rider: it rides one line of its launcher's laser, which `Systems/Beams.cs`
// has laid on a locked target and leads for it.  The round knows only the line, so
// four of them can be in the air on four lines of the same beam.
//
// `Guidance` is the stage between the beam and the airframe, and it is where a
// round differs from another round: it turns what the round is told into the two
// values the wing answers (`a_dir`, `a_want`) and, for a beam rider, the crosswind
// the motor pushes across the line.  Everything downstream - lift, drag, thrust,
// gravity, integration - is the same eleven passes whatever kind of round it is.

namespace Mech.Server.Systems;

public static partial class Flights
{
    public static partial class Guidance
    {
        /// <summary>A beam rider, flown not slid: the same airframe as the top
        /// attack, with a law that has no arc in it - the motor puts 600 m/s on a
        /// 45 kg round in two seconds, the wing carries the weight at an angle of
        /// attack the rest of the way, and the pull available to correct anything
        /// falls with the square of the speed.  So a shot has to be *aimed*: the
        /// laser leads the target, the tube points where the pilot's arm is laid, and
        /// a round that arrives with no energy left sags out of the beam instead of
        /// turning.
        ///
        /// Two things take the line away, and both leave the round flying on its
        /// heading - the motor and the drag go on, the guidance is what stops: the
        /// launcher dying (nothing is designating), and the laser not reaching the
        /// round any more (a ridge or a house between the launcher and the missile cut
        /// a beam that is only light).  A round with no line has no lift demand
        /// either, so it keeps its heading and falls like anything else without a
        /// wing.</summary>
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
                                line = Flight.beam_line(aperture, aim);
                            }
                        }
                        // the demand, in the airframe's own units: the pull back onto
                        // the line plus the weight the wing is carrying, capped by
                        // what the wing can make at this speed
                        var (dir, mag) = Flight.direct_demand(p0, vel, aperture, aim, wd);
                        set3(c.adir, r.index, dir);
                        c.awant[r.index] = mag;
                        // thrust misalignment / crosswind: a slow push square to the
                        // sight line, seeded per round, that the guidance has to chase
                        // down.  Bounded because the beam always wins.
                        add3(c.acc, r.index,
                             Flight.wobble_accel(wd, line, r.age - dt, r.seed));
                    }
                }
            }
        }
    }
}
