// The AI block, in pipeline order, as the list a mode hands to `World`.
//
// The block is a *pipeline*, not a call tree: every entry is one `run(World, float)`
// and the order between them is the only coordination there is, so it is written
// down once, here.  A mode picks the block it wants - the battle runs the lane and
// the raiding parties, the range runs the lane only (its `debug_spawn` drones) plus
// the defence post, and a mode whose hulls only shoot missiles down runs the last
// three lines of `defence()`.
//
// Driving is not in here.  A crew writes an intent; `Systems/Movement/` turns it
// into wheels, legs and a position, in the shared part of the pipeline.

namespace Mech.Server.Systems.AI;

public static class Pipeline
{
    /// <summary>Everything but the missions: the rounds in the air, the alarm, and
    /// what a crew can see.</summary>
    private static void sense(List<Action<World, float>> into)
    {
        into.Add(Threats.run);      // this tick's guided rounds, once for everybody
        into.Add(Intercept.run);    // who has one coming for it: ai_cover / ai_threat
        into.Add(Sense.run);        // ai_timer: look for a target
    }

    /// <summary>The guns, one platform each: the only writer of that platform's
    /// trigger, whether it is aimed at a hull or at a missile.</summary>
    private static void engage(List<Action<World, float>> into)
    {
        into.Add(EngageMech.run);
        into.Add(EngageVehicle.run);
        into.Add(EngageFoot.run);
    }

    /// <summary>A battle: the lane front and the raiding parties.</summary>
    public static List<Action<World, float>> battle()
    {
        var into = new List<Action<World, float>>();
        sense(into);
        into.Add(Front.run);        // the lane's state machine
        into.Add(Raid.run);         // the raiding parties: their own routes and mission
        into.Add(Paths.run);        // a hull that has run out of road: the facet navmesh
        engage(into);
        into.Add(Wander.run);       // civilians
        return into;
    }

    /// <summary>The range: no parties, but a drone a pilot drops there fights like
    /// a lane hull, and the defence post is a crew with a radar and no ground fight.
    /// </summary>
    public static List<Action<World, float>> range()
    {
        var into = new List<Action<World, float>>();
        sense(into);
        into.Add(Front.run);
        into.Add(Paths.run);
        engage(into);
        into.Add(Wander.run);
        return into;
    }

    /// <summary>Missile defence and nothing else: what a hull that never picks a
    /// ground fight runs (the range's post).</summary>
    public static List<Action<World, float>> defence()
    {
        var into = new List<Action<World, float>>();
        sense(into);
        into.Add(EngageMech.run);
        into.Add(EngageVehicle.run);
        return into;
    }
}
