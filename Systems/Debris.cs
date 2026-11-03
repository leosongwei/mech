// Wreck and corpse cleanup.
//
// A destroyed unit leaves a hulk behind (`Systems/Mortality.cs`): infantry topple
// and vanish after `Defs.CORPSE_TIME`, vehicles and mechs keep smoking for
// `Defs.WRECK_TIME`.  Both are inert - they are not part of `world.units()`, so
// nothing can target, hit or capture with them.  All this system owns is the
// countdown and the destroy.
//
// Ported from `backend/server/systems/debris.py`.

namespace Mech.Server.Systems;

public static class Debris
{
    public static void run(World world, float dt)
    {
        var e = world.ecs;
        var ids = e.query("wreck_t");
        if (ids.Length == 0) return;
        var t = e.fv("wreck_t");
        // the ids are a snapshot: a destroy re-rows the archetype under a live
        // row, which is why this walks entity ids and not rows
        foreach (int eid in ids)
        {
            t[eid] = t[eid] - dt;
            if (t[eid] <= 0f) e.destroy(eid);
        }
    }
}
