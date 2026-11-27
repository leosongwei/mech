// The infrared round: the seeker owns the lock and hands back the point to dive on,
// and the guidance turns it into a demand the wing answers.
//
// `Seeker.track` refreshes the stare point off the live hull while it can see it,
// keeps the memory walking at the hull's last velocity when it cannot, and takes any
// enemy hull it finds inside its cone.  What comes back is a *point*, so the arc
// below cannot tell a live hull from a memory and does not need to; a round that
// never had a lock gets nothing and is a rock.
//
// The law itself is `Flight.arc_demand`: the reference parabola while the round is
// far out, proportional navigation inside `pn_r`.  This stage only carries it across
// the tick boundary - the point, the velocity it is drifting at, and the distance
// already flown are what the arc is a function of.

namespace Mech.Server.Systems;

public static partial class Flights
{
    public static partial class Guidance
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
                        if (!flying(r.hit_t)) continue;
                        var wd = Defs.WEAPONS[r.wid];
                        var p0 = r.pos;
                        var spot = Seeker.track(world, r.eid, p0, dt);
                        Vec3? tvel = null;
                        if (spot is not null) tvel = c.tvel_at(r.index);
                        // the age is the one the last step produced, since `Expire`
                        // has already counted this tick's second
                        var (dir, mag) = Flight.arc_demand(p0, r.vel, r.age - dt, r.trav,
                                                           spot, tvel, wd);
                        set3(c.adir, r.index, dir);
                        c.awant[r.index] = mag;
                    }
                }
            }
        }
    }
}
