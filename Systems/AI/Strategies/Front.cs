// The lane crew's tactics: what a hull on the front does with the tick it was given.
//
// This is the state machine the whole scaffold is for.  It reads one `Situation`
// (`Systems/AI/Support/Policy.cs`), writes one state (`ai_state`) and one intent
// (`nav_speed`, or a route when it changes what it is doing) - it never touches a
// gun, a way point index or a velocity.  A new tactic is a new state, a case here
// and one `Policy.*` call: the driving is `Systems/Movement/`'s and the shooting is
// `Engage*`'s, and neither of them learns anything.
//
// Three states are live today and they are the three the old single loop had:
// ADVANCE (walk the lane), ENGAGE (a target is in sight: work it, and close the
// distance if it is far), SUPPRESS (lost it: keep firing where it was).  The four
// at the bottom are the hooks - named in `AiState`, spelled out here as what each
// would need, implemented by nobody yet.

namespace Mech.Server.Systems.AI;

public sealed class FrontColumns : CrewColumns
{
    private static readonly string[] front_names = Components.Cat(names, AiTags.FRONT);
    public static readonly string[] OnFront = front_names;

    public FrontColumns(Group g) : base(g) { }

    public override string[] Names => front_names;
}

public static class Front
{
    public static void run(World world, float dt)
    {
        foreach (var g in world.ecs.groups(FrontColumns.OnFront))
        {
            var c = new FrontColumns(g);
            foreach (var r in c)
            {
                var s = Policy.sense(world, c, r.index);
                // a round is coming for this hull: the alarm owns the gun this tick
                // and the crew keeps driving (the machine is not stopped by what it
                // is shooting at)
                if (s.covered)
                {
                    r.state_mem = AiState.ADVANCE;
                    _route(world, c, r);
                    Policy.cruise(c, r.index);
                    continue;
                }
                decide(world, c, r, s, dt);
            }
        }
    }

    /// <summary>One tick of the tactic: state out, intent out, nothing else.</summary>
    private static void decide(World world, FrontColumns c, CrewRow r, in Situation s, float dt)
    {
        int i = r.index;
        var d = Defs.UNIT_DEFS[r.etype];
        _route(world, c, r);

        // ---- a target in sight -------------------------------------------------
        if (s.target >= 0 && s.target_seen)
        {
            r.state_mem = AiState.ENGAGE;
            if (s.target_dist > d.ai_range * 0.55f) Policy.cruise(c, i);
            else Policy.halt(c, i);              // close enough: stand and shoot it out
            return;
        }
        // ---- lost it: hose where it went --------------------------------------
        // `Sense` walked the belief out for `Crew.MEMORY` seconds and the guns are
        // already using it, so the tactic only has to stand still and keep shooting.
        if (s.target_known)
        {
            r.state_mem = AiState.SUPPRESS;
            Policy.halt(c, i);
            return;
        }
        // ---- nothing to shoot --------------------------------------------------
        // The four tactics below are the ones this scaffold exists for.  Each is a
        // state, a case here and one `Policy.*` call; the data is in `Situation`:
        //   WITHDRAW  : s.hp_frac < 0.3f            -> route back down the lane
        //   RESUPPLY  : s.mag_left == 0             -> point at the depot
        //   ATTACK_FLANK : the town ahead is contested -> a flank route
        //   CAPTURE   : an enemy town is near       -> point at its ring
        r.state_mem = AiState.ADVANCE;
        Policy.cruise(c, i);
    }

    /// <summary>Make sure the crew knows where it is going: the lane it was spawned
    /// on (`World.register_routes`), or - on a map with no lanes at all, the range -
    /// the enemy command post as a point.
    ///
    /// A future tactic replaces this with its own route (a flank, a withdrawal) or
    /// its own point (a capture ring, a depot), which is the whole of what changing
    /// where a hull walks takes.</summary>
    private static void _route(World world, FrontColumns c, CrewRow r)
    {
        if (r.route >= 0) return;
        int lane = world.lane_route(r.faction, r.lane);
        float speed = Defs.UNIT_DEFS[r.etype].speed;
        if (lane >= 0)
        {
            Policy.route(c, r.index, lane, speed);
            return;
        }
        var (bx, bz) = Policy.enemy_base(world, r.faction);
        Policy.point(c, r.index, bx, bz, speed);
    }
}
