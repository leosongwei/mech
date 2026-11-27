// A bullet: gravity and nothing else, so the air half is a parabola and
// `Core/Ballistics.cs` can solve the zero in closed form.

namespace Mech.Server.Systems;

public static partial class Flights
{
    public static class Ballistic
    {
        public static void run(World world, float dt)
        {
            var e = world.ecs;
            foreach (var g in e.groups(RoundColumns.OnBullet))
            {
                var c = new RoundColumns(g);
                foreach (var r in c)
                {
                    if (!flying(r.hit_t)) continue;
                    var wd = Defs.WEAPONS[r.wid];
                    var p0 = r.pos;
                    var vel = r.vel;
                    var v = new Vec3(vel.x, vel.y - wd.g * dt, vel.z);
                    _move(world, r, p0, v, dt);
                }
            }
        }
    }
}
