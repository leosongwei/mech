// The mech's locomotion, in five passes: the intent, the thrust, the hill, the
// drag, and the ground under it.
//
// A mech is an engine on legs.  The legs push with `P(v)/v` along the slope the hull
// stands on - a power curve, held to the machine's own 1.5 g below 76 km/h and easing
// to 0.81 g at the rated speed - gravity takes its share back when the way climbs and
// hands one over when it falls away, and a quadratic drag is what gives the machine a
// top end at all (`Defs.MECH_HP_LOW` / `MECH_G_LIMIT` / `MECH_DRAG`).
//
// One law per file, in the order the tick runs them, because each one is a force
// and a force is the whole of what it owns:
//
//   `Steer`  the command (`vel_cmd`) and the legs that swing after it
//   `Thrust` the legs' push, the cruise governor, and the brake that plants them
//   `Slope`  gravity, along the way the hull is going
//   `Drag`   the square-of-speed resistance that sets the top speed
//   `Ground` the one thing that moves a hull: `Integrate.step`
//
// They talk through the components every mover already owns - `pos`, `vel`,
// `vel_cmd`, `leg_yaw`, `hp` - so the velocity buffer *is* where the forces
// compose: `Thrust` adds its push to it, `Slope` adds the hill's, `Drag` takes
// its cut out of it, and `Ground` lays the sum on the terrain.  A separate
// accumulator component would be a second copy of the same three floats.

using Mech.Server.Systems.AI;

namespace Mech.Server.Systems.Movement;

public static partial class Mech
{
    /// <summary>The command, and the legs that follow it.
    ///
    /// A crew says *where* (`nav_route` / `nav_x` / `nav_z`) and *how fast*
    /// (`nav_speed`) and this is the one translation every walker shares
    /// (`Drive.legs`); a pilot's command is already in `vel_cmd`.  Either way the
    /// legs swing after it at the machine's own rate - they are how a mech steers,
    /// and how far they have to come round is what `Thrust` reads as a hull still
    /// getting its feet under it.
    /// </summary>
    public static class Steer
    {
        public static void run(World world, float dt)
        {
            foreach (var g in world.ecs.groups(MoveColumns.OnMech))
            {
                var c = new MoveColumns(g);
                if (g.has(AiTags.CREW)) foreach (var r in c) Drive.legs(world, r, dt);
                Integrate.turn_legs(c, dt);
            }
        }
    }
}
