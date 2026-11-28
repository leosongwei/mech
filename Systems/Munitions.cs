// The loitering munition's launch: every crew that carries one puts a round over
// the nearest enemy town, on its own tube's clock.
//
// A crew's munition is not aimed at a hull it can see.  The round is put in the
// air *over a town* and searches from there (`Systems/Flights/Drone.cs`), so the
// thing that pulls this trigger is a piece of ground, not a target - which is why
// the AI's own engage loop skips the arm (`AI/Support/Engage.cs`) and this system
// exists at all.
//
// Two geometric choices, both per hull and both read off the state every tick:
//
//   * which town - the enemy-held one nearest this machine, provided it is inside
//     `Defs.LOITER_TOWN_REACH` (3 km, tighter than the round's own 4 km range).  A
//     town nobody owns has nothing to be denied, and a crew whose front is further
//     off than that keeps its rounds: a munition sent past its reach arrives with
//     the battery already half spent;
//   * when - whenever the tube is ready.  There is no timer here: the arm's own
//     cooldown *is* the clock, and `Weapons.pull` sets it to `Defs.LOITER_GAP` for
//     a crew and to the rack's cycle for a pilot.  So a crew that has just fired
//     is skipped by the same number the trigger path reads, an empty or shot-off
//     arm is skipped by its own columns, and nothing has to be remembered between
//     ticks.
//
// A pilot's rack is his own: `owner >= 0` is never touched here, so the mode can
// not spend a round the pilot never aimed at.

namespace Mech.Server.Systems;

public static class Munitions
{
    public static void run(World world, float dt)
    {
        if (world.towns.Count == 0) return;
        var e = world.ecs;
        var ids = world.units();
        if (ids.Length == 0) return;
        var wd = Defs.WEAPONS[Defs.WID_LOITER];

        foreach (int eid in ids)
        {
            if (!e.has(eid, "weapon")) continue;
            if (e.iv("owner")[eid] >= 0) continue;      // a pilot's rack
            var arch = e.arch_of(eid);
            if (arch is null) continue;
            int row = e.row_of(eid);
            var pos = e.fv("pos").get3(eid);
            for (int arm = 0; arm < 2; arm++)
            {
                int c = row * 2 + arm;
                if (arch.u8("weapon")[c] != Defs.WID_LOITER) continue;
                if (arch.i("mag")[c] <= 0) continue;
                if (arch.f("cool")[c] > 1e-4f) continue;
                if (arch.has("arm_hp") && arch.f("arm_hp")[c] <= 0f) continue;
                var town = _nearest_enemy_town(world, e.uv("faction")[eid], pos,
                                               Defs.LOITER_TOWN_REACH);
                if (town is null) continue;
                var orbit = new Vec3(town.x, 0f, town.z);
                if (!Weapons.pull(world, eid, arm, Defs.WID_LOITER, wd, -1, orbit)) continue;
                // one line per launch: the rhythm and where it was aimed, which is
                // what the battle census reads
                world.emit("drone", ("x", town.x), ("y", 0f), ("z", town.z),
                           ("faction", e.uv("faction")[eid]), ("eid", eid));
            }
        }
    }

    /// <summary>The town this machine should deny, or null: the nearest one the
    /// other side holds within `reach`, in map order for the ties.  A crew behind its
    /// own lines does not spend a tube on a town the munition would reach with its
    /// battery half gone.</summary>
    private static Town? _nearest_enemy_town(World world, byte faction, Vec3 from,
                                             float reach)
    {
        Town? best = null;
        float best_d = reach;
        foreach (var t in world.towns)
        {
            if (t.owner < 0 || t.owner == faction) continue;
            float d = MathUtil.dist2d(from.x, from.z, t.x, t.z);
            if (d >= best_d) continue;
            best_d = d;
            best = t;
        }
        return best;
    }
}
