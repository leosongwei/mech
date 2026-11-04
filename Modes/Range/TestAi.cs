// Ground AI for the hulls a pilot drops on the range himself.
//
// The range's own fixtures - the passive APC targets and the missile-defence post
// - are driven by their own systems and never pick a ground fight; that is what
// the place is for.  A hull spawned from the console (`debug_spawn`) is a
// different thing: it is there to be *tested against*, so it fights with the
// ordinary crew any AI hull gets (`Systems/Ai.run`) - which is how an AI mech's
// missile arm gets flown without waiting for a battle.
//
// `debug_spawn` tags such a hull `range_drone` (`Mode.console_spawn_extra`), and
// that tag is the whole test: `Ai.run` takes the list of hulls to drive, so the
// mode's targets, its defence post and any hull a tool spawned for a scripted
// shot stay exactly as passive as they were.
//
// Ported from `backend/server/modes/range/test_ai.py`.

namespace Mech.Server.Modes.Range;

public static class TestAi
{
    public static void run(World world, float dt)
    {
        var e = world.ecs;
        var owner = e.iv("owner");
        var hulls = new List<int>();
        foreach (var eid in world.movable())
            if (e.has(eid, "range_drone") && owner[eid] < 0) hulls.Add(eid);
        if (hulls.Count > 0) Systems.Ai.run(world, dt, hulls.ToArray());
    }
}
