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
//
// `Arm` follows it and is the one place a tick's flight state is reset: the record
// re-armed, the leg's start published, and the accumulator the force stages add into
// zeroed.  Every round the record owns is armed here, so no later stage re-arms and
// a stage that skips a condemned round skips it for the same reason.

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

    /// <summary>Re-arm every round's record and zero the flight state it is about to
    /// accumulate into.
    ///
    /// The record half is the round's own (`_begin`), the zeroing half is the winged
    /// round's: `acc`, `a_dir` and `a_want` are this tick's demand and this tick's
    /// force sum, so they start at zero and nothing carries over.  A round that
    /// `Expire` condemned is re-armed too - `_begin` leaves its record alone, and its
    /// accumulator is never read because no stage moves it.</summary>
    public static class Arm
    {
        private static readonly string[] acc_names = { "acc", "a_dir", "a_want" };

        public static void run(World world, float dt)
        {
            var e = world.ecs;
            foreach (var g in e.groups(RoundColumns.names))
            {
                var c = new RoundColumns(g);
                foreach (var r in c) _arm(r);
            }
            foreach (var g in e.groups(acc_names))
            {
                var acc = g.f("acc");
                var adir = g.f("a_dir");
                var awant = g.f("a_want");
                for (int i = 0; i < g.n; i++)
                {
                    set3(acc, i, Vec3.zero);
                    set3(adir, i, Vec3.zero);
                    awant[i] = 0f;
                }
            }
        }
    }
}
