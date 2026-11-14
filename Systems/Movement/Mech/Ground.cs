// The ground: the one thing that moves a hull.
//
// Every force above has already gone into `vel`, so this pass is the same single
// step every chassis ends in (`Integrate.step`) - terrain bounds, the collision
// slide, and the velocity that actually came out of both.  The wheeled hull's
// `blocked_t` / `backout_t` belong to `Vehicle`: a hull on legs turns its legs
// instead (`Drive.legs`), so all this pass carries is the ground.

namespace Mech.Server.Systems.Movement;

public static partial class Mech
{
    /// <summary>The velocity laid on the terrain.</summary>
    public static class Ground
    {
        public static void run(World world, float dt)
        {
            foreach (var g in world.ecs.groups(MoveColumns.OnMech))
            {
                var c = new MoveColumns(g);
                foreach (var r in c)
                {
                    var v = r.vel;
                    Integrate.step(world, r, v.x, v.z, wheeled: false, dt);
                }
            }
        }
    }
}
