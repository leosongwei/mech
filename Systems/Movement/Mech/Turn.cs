// The turn: the legs plant, and the machine comes round.
//
// The push and the turn are two actuators, and this is the second one.  A hull's
// *speed* is the engine's business (`Thrust`, `Slope`, `Drag`); its *direction* is
// this pass's, and nothing else writes it.  That split is what a pilot feels: with
// the direction left to the sideways share of the engine's own push, a change of
// heading was an exponential with `v/a` for a time constant - 8 or 10 seconds for a
// right angle at the top speed - so a machine driven from a cross that had already
// come round appeared to slide sideways across its own sight.  A right angle costs
// 2.8 s at 180 km/h now, and the cross and the way the hull is going agree all the
// way round it.
//
// The rate is the machine's, not the crew's, and it has two ends:
//
//   * `MECH_TURN_G` of lateral acceleration is what its feet can plant, so the
//     rate is `MECH_TURN_G * g / v` - wide at speed, which is why a heavy hull
//     turns in an arc (`v / rate` is its radius: 85 m at 180 km/h, 14 m at 72);
//   * `MECH_TURN_RATE` is the ceiling under that, for the slow end, where a hull
//     that planted its feet could otherwise pivot on the spot.
//
// How far the legs have come round scales both, because the legs *are* the steering:
// a hull whose feet are still pointing the old way comes round at the leg-alignment
// factor, which is the same number `Turn` gives the drive.

namespace Mech.Server.Systems.Movement;

public static partial class Mech
{
    /// <summary>The way the hull is going, turned at the rate its legs allow.</summary>
    public static class Turn
    {
        public static void run(World world, float dt)
        {
            foreach (var g in world.ecs.groups(MoveColumns.OnMech))
            {
                var c = new MoveColumns(g);
                foreach (var r in c)
                {
                    var v = r.vel;
                    var cmd = r.vel_cmd;
                    float cs = MathF.Sqrt(cmd.x * cmd.x + cmd.z * cmd.z);
                    if (cs < 0.05f) continue;                 // nothing asked for
                    float speed = MathF.Sqrt(v.x * v.x + v.z * v.z);
                    if (speed < 0.5f) continue;               // standing still: no heading to turn
                    float have = MathF.Atan2(v.z, v.x);
                    float want = MathF.Atan2(cmd.z, cmd.x);
                    float err = MathUtil.wrap_pi(want - have);
                    float rate = MathF.Min(Defs.MECH_TURN_RATE,
                                           Defs.MECH_TURN_G * Defs.GRAVITY / speed)
                                 * Thrust.Legs(r, cmd.x / cs, cmd.z / cs);
                    float turn = Math.Clamp(err, -rate * dt, rate * dt);
                    if (turn == 0f) continue;
                    float ang = have + turn;
                    r.vel = new Vec3(MathF.Cos(ang) * speed, v.y, MathF.Sin(ang) * speed);
                }
            }
        }
    }
}
