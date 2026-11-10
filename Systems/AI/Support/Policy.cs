// What every mission policy shares: how a crew reads its situation, and the four
// ways it can say what it wants.
//
// A policy is a state machine over `ai_state` (`Systems/AI/Support/Tags.cs`).  It
// reads one `Situation` and writes one of the states plus an *intent*
// (`Systems/AI/Support/Tags.cs`'s `nav_*`): where to go and how fast.  It never
// writes a velocity, a way point index, a gun or a trigger - the driver
// (`Systems/Movement/`) and the engage systems do that, and that is the whole of
// the layering this refactor is for.
//
// `sense` is the expensive call here (one line-of-sight cast per crew per tick,
// the same one the old single loop made), so a policy calls it once and decides
// from the answer.

namespace Mech.Server.Systems.AI;

/// <summary>One crew's tick, read once: what it is, what it can see, what it has
/// been told to fight and what is coming at it.</summary>
public struct Situation
{
    public int eid;
    public byte etype, faction;
    public Vec3 eye, pos;
    public float hp_frac;        // hp / hpmax
    public int mag_left;         // rounds left across both arms
    public int target;           // the id it is working (-1 = none)
    public float target_dist;    // plan-view metres
    public bool target_seen;     // the shared sight rule (`Systems/Sight.cs`) says yes
    public bool covered;         // a round is coming for it: the gun is the intercept's
    public int route;            // the poly line it is following (-1 = a point)
}

public static class Policy
{
    public const float SPOT_MEMORY = 4.0f;   // s a crew keeps firing at a lost target's spot

    /// <summary>Where the enemy keeps its command post - the end of every route,
    /// and what a crew with no lane walks at (the range).</summary>
    public static (float x, float z) enemy_base(World world, byte faction) =>
        world.terrain.bases[1 - faction];

    /// <summary>Read the crew's situation.  `target_seen` is the one line-of-sight
    /// cast: the target's body, about 60 % of its height up, through the same door
    /// the seeker and the AI's lookout use (`Systems/Sight.cs`: terrain, canopies
    /// and smoke).</summary>
    public static Situation sense(World world, CrewColumns c, int i)
    {
        var s = new Situation
        {
            eid = c.ids[i],
            etype = c.etype[i],
            faction = c.faction[i],
            pos = new Vec3(c.pos[i * 3], c.pos[i * 3 + 1], c.pos[i * 3 + 2]),
            target = c.target[i],
            covered = c.cover[i] != 0,
            route = c.nav_route[i],
            hp_frac = c.hpmax[i] > 0f ? c.hp[i] / c.hpmax[i] : 0f,
            mag_left = c.mag[i * 2] + c.mag[i * 2 + 1],
        };
        s.eye = s.etype == Defs.ETYPE_MECH
            ? MathUtil.sight_eye(s.pos, c.yaw[i])
            : new Vec3(s.pos.x, s.pos.y + Defs.UNIT_DEFS[s.etype].height * 0.75f, s.pos.z);

        var d = Defs.UNIT_DEFS[s.etype];
        int tid = s.target;
        if (tid < 0) return s;
        if (!world.ecs.is_alive(tid))
        {
            s.target = -1;
            return s;
        }
        var tpos = world.ecs.fv("pos").get3(tid);
        if (world.ecs.uv("faction")[tid] == s.faction)
        {
            s.target = -1;
            return s;
        }
        s.target_dist = MathUtil.dist2d(s.eye.x, s.eye.z, tpos.x, tpos.z);
        if (s.target_dist < d.ai_range)
            s.target_seen = Sight.clear(world, s.eye,
                                        new Vec3(tpos.x, tpos.y + d.height * 0.6f, tpos.z));
        return s;
    }

    // ------------------------------------------------------------------
    // the intent, and nothing else
    // ------------------------------------------------------------------

    /// <summary>Follow `route` (`World.routes`, `Core/Nav.cs`) at the hull's own
    /// top speed.</summary>
    public static void route(CrewColumns c, int i, int route, float speed)
    {
        c.nav_route[i] = route;
        c.nav_speed[i] = speed;
    }

    /// <summary>Go to a point - stand in a capture ring, walk at the enemy post on
    /// a map with no lanes, wander near home.</summary>
    public static void point(CrewColumns c, int i, float x, float z, float speed)
    {
        c.nav_route[i] = -1;
        c.nav_x[i] = x;
        c.nav_z[i] = z;
        c.nav_speed[i] = speed;
    }

    /// <summary>Push on at the hull's top speed, whatever it was already following.</summary>
    public static void cruise(CrewColumns c, int i)
    {
        c.nav_speed[i] = Defs.UNIT_DEFS[c.etype[i]].speed;
    }

    /// <summary>Bring the machine to a halt (the driver brakes it).</summary>
    public static void halt(CrewColumns c, int i)
    {
        c.nav_speed[i] = 0f;
    }
}
