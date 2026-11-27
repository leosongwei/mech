// An unguided rocket: the same airframe the two guided kinds fly, with nothing
// asked of it (`Flight.slide`).  It never turns its nose, so it has no way to
// point anywhere but where it is already going; the motor pushes along that line
// for `burn` seconds and the drag comes off it, so the round slows once the motor
// is out instead of holding its top speed to the target.

namespace Mech.Server.Systems;

public static partial class Flights
{
    public static class Rocket
    {
        public static void run(World world, float dt)
        {
            var e = world.ecs;
            foreach (var g in e.groups(RoundColumns.OnRocket))
            {
                var c = new RoundColumns(g);
                foreach (var r in c)
                {
                    if (!_begin(r)) continue;
                    var wd = Defs.WEAPONS[r.wid];
                    var p0 = r.pos;
                    // the age the round had when the last step produced this
                    // velocity, i.e. `Expire` has already counted this tick
                    var v = Flight.slide(r.vel, r.age - dt, wd, dt);
                    _move(world, r, p0, v, dt);
                }
            }
        }
    }
}
