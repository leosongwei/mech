// A beam rider: it rides one line of its launcher's laser, which `Systems/Beams.cs`
// has laid on a locked target and leads for it.  The round knows only the line, so
// four of them can be in the air on four lines of the same beam.

namespace Mech.Server.Systems;

public static partial class Flights
{
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
}
