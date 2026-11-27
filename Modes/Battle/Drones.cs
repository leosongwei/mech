// The loitering munition's launch rhythm: one crew puts one in the air every twenty
// seconds, aimed at an enemy town rather than at a hull.
//
// The timer lives at the mode's level, not on the crews.  A carrier each with a
// timer of its own is one launch every four seconds once five hulls carry the round,
// and the munition is meant to be something the enemy keeps looking up for, not a
// barrage (`docs/plans/drone.md` §6.1).
//
// A crew fires its guns through `Systems/AI/Support/Engage.cs`, which engages hulls
// and knows nothing about towns.  So this system pulls the trigger it wants and
// `Weapons.pull` settles it, exactly as the trigger bits do - one magazine round, one
// cooldown, one round in the air.
//
// Which town: the enemy-held ones in turn, one per shot, so a side's fire spreads
// across its front instead of stacking on whichever town happens to be nearest.
// Which crew: the carrier nearest that town.  Both are geometric choices, so the
// order the entities happen to be walked in decides nothing.

namespace Mech.Server.Modes.Battle;

public static class Drones
{
    /// <summary>s between two launches by one faction.</summary>
    public const float INTERVAL = 20.0f;

    /// <summary>s to the next try when the timer came due and no carrier could
    /// shoot: every tube is empty or cooling, or the side holds no enemy town.</summary>
    private const float RETRY = 1.0f;

    public static void bind(World world)
    {
        for (int f = 0; f < 2; f++)
        {
            world.drone_timer[f] = INTERVAL;
            world.drone_town[f] = 0;
        }
    }

    public static void run(World world, float dt)
    {
        for (byte f = 0; f < 2; f++)
        {
            world.drone_timer[f] -= dt;
            if (world.drone_timer[f] > 0f) continue;
            world.drone_timer[f] = _launch(world, f) ? INTERVAL : RETRY;
        }
    }

    /// <summary>One faction's turn: aim at the next enemy town and let the nearest
    /// crew that can still shoot put a round in the air at it.</summary>
    private static bool _launch(World world, byte faction)
    {
        var towns = _enemy_towns(world, faction);
        if (towns.Count == 0) return false;
        int cursor = world.drone_town[faction] % towns.Count;
        var town = towns[cursor];

        var e = world.ecs;
        int best = -1, best_arm = -1, best_wid = -1;
        float best_d = float.MaxValue;
        foreach (int eid in world.units())
        {
            if (e.uv("faction")[eid] != faction) continue;
            if (!e.has(eid, "weapon")) continue;
            // a crew's rack, never a pilot's: `owner` is the pilot id and -1 is a
            // crew (`Weapons.pull` reads the same column).  A pilot fires this
            // round himself, or not at all - the mode must not spend his magazine
            // on a town he never aimed at
            if (e.iv("owner")[eid] >= 0) continue;
            for (int arm = 0; arm < 2; arm++)
            {
                int wid = e.uv("weapon").get(eid, arm);
                if (wid != Defs.WID_LOITER) continue;
                if (e.iv("mag").get(eid, arm) <= 0) continue;
                if (e.fv("cool").get(eid, arm) > 0f) continue;
                if (e.has(eid, "arm_hp") && e.fv("arm_hp").get(eid, arm) <= 0f) continue;
                var p = e.fv("pos").get3(eid);
                float d = MathUtil.dist2d(p.x, p.z, town.x, town.z);
                if (d >= best_d) continue;
                best = eid; best_arm = arm; best_wid = wid; best_d = d;
            }
        }
        if (best < 0) return false;

        var wd = Defs.WEAPONS[best_wid];
        var orbit = new Vec3(town.x, 0f, town.z);
        if (!Systems.Weapons.pull(world, best, best_arm, best_wid, wd, -1, orbit)) return false;
        world.drone_town[faction] = (cursor + 1) % towns.Count;
        // one line per launch: the rhythm and where it was aimed, which is what the
        // battle census and the range test read
        world.emit("drone", ("x", town.x), ("y", 0f), ("z", town.z),
                   ("faction", faction), ("eid", best));
        return true;
    }

    /// <summary>The towns the other side holds, in map order: a town nobody owns has
    /// nothing to be denied.</summary>
    private static List<Town> _enemy_towns(World world, byte faction)
    {
        var list = new List<Town>();
        foreach (var t in world.towns)
        {
            if (t.owner < 0 || t.owner == faction) continue;
            list.Add(t);
        }
        return list;
    }
}
