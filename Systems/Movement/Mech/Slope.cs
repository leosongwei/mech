// The hill: gravity, along the way the hull is going.
//
// Nothing about a mech's legs changes with the ground it stands on, so this is the
// other half of "uphill is slower, downhill is faster" - the ground hands back a
// pull of `g sin(slope)` along the direction of travel, negative up the face and
// positive down it.  The slope is the *terrain's* own gradient under the hull
// (`Terrain.slope_along`), so a machine that has just spawned on a hillside feels
// it from its first tick - a slope read off the velocity it made last tick would
// read zero until it was already moving.
//
// A hull that was told nothing is standing on its legs, and the legs carry the
// hill: this pass keeps to the hull that is going somewhere.  Which direction to
// measure is the velocity's while the hull is moving and the command's while it is
// pulling away from a stop (the case where the hill is felt before it is climbed).

namespace Mech.Server.Systems.Movement;

public static partial class Mech
{
    /// <summary>Gravity's share along the ground: the hill a hull is on, spent on
    /// the way the hull is going.</summary>
    public static class Slope
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
                    float dx, dz;
                    float speed = MathF.Sqrt(v.x * v.x + v.z * v.z);
                    if (speed > 0.5f)
                    {
                        dx = v.x / speed;
                        dz = v.z / speed;
                    }
                    else
                    {
                        var cmd = r.vel_cmd;
                        float cs = MathF.Sqrt(cmd.x * cmd.x + cmd.z * cmd.z);
                        if (cs < 0.05f) continue;          // parked: the legs hold it
                        dx = cmd.x / cs;
                        dz = cmd.z / cs;
                    }
                    float s = terrain.slope_along(r.pos.x, r.pos.z, dx, dz);
                    // g sin(theta) with sin(theta) = s / sqrt(1 + s^2)
                    float pull = -Defs.GRAVITY * s * dt / MathF.Sqrt(1f + s * s);
                    r.vel = new Vec3(v.x + dx * pull, v.y, v.z + dz * pull);
                }
            }
        }
    }
}
