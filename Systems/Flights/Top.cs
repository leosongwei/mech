// The infrared round: the seeker owns the lock and hands back the point to dive
// on, and the airframe flies to it.
//
// `Seeker.track` refreshes the stare point off the live hull while it can see it,
// keeps the memory walking at the hull's last velocity when it cannot, and takes
// any enemy hull it finds inside its cone.  What comes back is a *point*, so the
// airframe below cannot tell a live hull from a memory and does not need to; a
// round that never had a lock gets nothing and is a rock.

namespace Mech.Server.Systems;

public static partial class Flights
{
    public static class Top
    {
        public static void run(World world, float dt)
        {
            var e = world.ecs;
            foreach (var g in e.groups(TopColumns.OnTop))
            {
                var c = new TopColumns(g);
                foreach (var r in c)
                {
                    if (!_begin(r)) continue;
                    var wd = Defs.WEAPONS[r.wid];
                    var p0 = r.pos;
                    var vel = r.vel;
                    var spot = Seeker.track(world, r.eid, p0, dt);
                    Vec3? tvel = null;
                    if (spot is not null) tvel = c.tvel_at(r.index);
                    // the airframe is stepped from the age it had *before* this
                    // tick's countdown, i.e. the state the last step produced
                    var v = Flight.step(p0, vel, r.age - dt, r.trav, spot, tvel, wd, dt);
                    _move(world, r, p0, v, dt);
                }
            }
        }
    }
}
