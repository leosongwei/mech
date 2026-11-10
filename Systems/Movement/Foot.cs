// A man's locomotion: his machine is his legs.
//
// The command the crew gave (`nav_speed` along its route or its point) *is* the
// velocity he makes - there is no ramp, no governor and nothing that has to catch
// up - and the collision is all that stands between him and where he was told to
// go.  His legs follow the direction of travel; his body is his gun, so `yaw`
// stays the crew's and `Gunnery` mirrors the sight off it.

using Mech.Server.Systems.AI;

namespace Mech.Server.Systems.Movement;

public static class Foot
{
    public static void run(World world, float dt)
    {
        foreach (var g in world.ecs.groups(MoveColumns.OnFoot))
        {
            var c = new MoveColumns(g);
            // a crew's man walks on the order it was given; a puppet's command is
            // whatever its own owner wrote (`crew: false` = no `ai_crew`, and the
            // archetype says so once per group).  The legs follow the command either
            // way - that is the man, not the crew.
            if (g.has(AiTags.CREW)) foreach (var r in c) Drive.legs(world, r, dt);
            Integrate.turn_legs(c, dt);
            foreach (var r in c)
            {
                var cmd = r.vel_cmd;
                Integrate.step(world, r, cmd.x, cmd.z, wheeled: false, dt);
            }
        }
    }
}
