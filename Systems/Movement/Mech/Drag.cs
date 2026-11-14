// The drag: what the two top speeds are made of.
//
// A power curve has no speed at which it stops, so the drag is what gives a mech a
// top end: `a = MECH_DRAG v^2`, and `Defs.MECH_DRAG` is *solved* from this file's
// balance at the rated 180 km/h - the full `MECH_HP_HIGH` over the rated speed
// against `k v0^2`.  It opposes the velocity the hull has (both components scaled,
// so the direction is untouched), and that is the whole of what it knows: hills
// belong to `Slope`, commands to `Thrust`.
//
// It is also why the last 20 km/h of a mech's speed take as long as the first 100:
// an engine against a force that grows with the square is an approach to a limit,
// and the limit is what the machine is rated at.

namespace Mech.Server.Systems.Movement;

public static partial class Mech
{
    /// <summary>Square-of-speed resistance, against the way the hull is moving.</summary>
    public static class Drag
    {
        public static void run(World world, float dt)
        {
            foreach (var g in world.ecs.groups(MoveColumns.OnMech))
            {
                var c = new MoveColumns(g);
                foreach (var r in c)
                {
                    var v = r.vel;
                    float speed = MathF.Sqrt(v.x * v.x + v.z * v.z);
                    if (speed < 1e-3f) continue;
                    float shed = MathF.Min(speed, Defs.MECH_DRAG * speed * speed * dt);
                    float k = (speed - shed) / speed;
                    r.vel = new Vec3(v.x * k, v.y, v.z * k);
                }
            }
        }
    }
}
