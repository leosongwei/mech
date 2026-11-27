// Battery and wire: the round's `life` and its maximum flight distance, both
// settled before anything moves, plus the one place `proj_age` is counted.
//
// A round that runs out either cooks off where it stands (`expire_burst`: the top
// attack's battery, a beam rider's wire) or is simply gone (a bullet and a
// rocket, which leave nothing behind).  Both are written into the
// `first_hit_record` as a contact at t=0, so `Impact` treats them like any other
// ending instead of a third special case.
//
// This is where a tick's leg list starts (`sweep.begin()`), and the round that has
// run out of battery or wire stops where it stands before this tick's move.

namespace Mech.Server.Systems;

public static partial class Flights
{
    public static class Expire
    {
        public static void run(World world, float dt)
        {
            var e = world.ecs;
            world.sweep.begin();                 // this tick's leg list starts here
            // every round, whatever it flies as: the schema is the `PROJ_COMPS`
            // half, and `groups` walks the four kinds' archetypes in the same
            // order `world.projectiles()` hands their ids out in
            foreach (var g in e.groups(RoundColumns.names))
            {
                var c = new RoundColumns(g);
                foreach (var r in c)
                {
                    var wd = Defs.WEAPONS[r.wid];
                    r.age += dt;
                    r.life -= dt;
                    bool out_of_life = r.life <= 0f;
                    bool out_of_wire = wd.maxdist > 0f && r.trav >= wd.maxdist;
                    if (!out_of_life && !out_of_wire) continue;
                    // the wire always cooks off; running out of battery depends on
                    // the round
                    bool burst = out_of_wire || wd.expire_burst;
                    r.hit_t = 0f;
                    r.hit_kind = burst ? Defs.HIT_EXPIRE : Defs.HIT_VANISH;
                    r.hit_unit = -1;
                    r.hit_mod = 0;
                    r.hit_gap = 0f;
                    r.hit_pos = r.pos;
                }
            }
        }
    }
}
