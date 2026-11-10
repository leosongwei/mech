// The guided rounds in the air, gathered once for the whole tick.
//
// Every crew with a radar asks the same question of the same table, so it is built
// here, first in the AI block, and `Systems/AI/Intercept.cs` reads it
// positionally.  One table per tick (`world.threats`, guarded by the tick it was
// built on), the same rule the tick's hit spheres follow.

namespace Mech.Server.Systems.AI;

public static class Threats
{
    public static void run(World world, float dt)
    {
        world.threats = ThreatTable.build(world);
        world._threat_tick = world.tick;
    }
}
