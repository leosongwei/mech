// Player intent -> ECS components (sight, velocity, trigger).
//
// Ported from `backend/server/systems/player_input.py`.
//
// The pilot only ever asks for two things: where to *look* and where to drive.
// Looking writes the sight (`sight_yaw` / `sight_pitch`); the hull and the arms
// slew after it at the machine's own rates in `Systems/Gunnery.cs` and are the
// only thing a weapon or a camera reads.  Driving writes `vel_cmd` - the
// order, never `vel`, which `Systems/Movement.cs` makes of it.

namespace Mech.Server.Systems;

public static class PlayerInput
{
    public static void run(World world, float dt)
    {
        var e = world.ecs;
        var cmd = e.fv("vel_cmd");
        var sight_yaw = e.fv("sight_yaw");
        var sight_pitch = e.fv("sight_pitch");
        var trigger = e.uv("trigger");
        var hp = e.fv("hp");
        var work = e.fv("work_remaining_time");
        float speed = Defs.UNIT_DEFS[Defs.ETYPE_MECH].speed;

        foreach (var pl in world.players.Values)
        {
            int eid = pl.mech;
            if (eid < 0 || !e.is_alive(eid)) continue;
            if (e.uv("etype")[eid] != Defs.ETYPE_MECH) continue;

            // the sight answers the mouse instantly; the hull and the arms are
            // what has to catch up with it (Systems/Gunnery.cs), and the arms
            // stop at their own elevation limits on the way
            sight_yaw[eid] = pl.in_yaw;
            sight_pitch[eid] = pl.in_pitch;

            // a rearm job (Systems/Rearm.cs) holds the machine where it stands
            // and kills the triggers: the pilot may still look around, nothing
<<<<<<< HEAD
            // else (a Q pressed during the job is dropped in `Smoke._launch`)
=======
            // else
>>>>>>> 43dd4b6 (Replace the Python backend with the C# one, move the repo to a single root layout)
            bool busy = work[eid] > 0f;

            float mx = pl.in_move.x, mz = pl.in_move.z;
            float mag = busy ? 0f : MathF.Sqrt(mx * mx + mz * mz);
            if (mag > 0.001f && hp[eid] > 0f)
            {
                float k = speed / MathF.Max(mag, 1f);
                cmd.set(eid, 0, mx * k);
                cmd.set(eid, 2, mz * k);
            }
            else
            {
                cmd.set(eid, 0, 0f);
                cmd.set(eid, 2, 0f);
            }

            int t = 0;
            if (!busy && pl.fire.left && Combat.arm_alive(world, eid, 0)) t |= 1;
            if (!busy && pl.fire.right && Combat.arm_alive(world, eid, 1)) t |= 2;
            trigger[eid] = (byte)t;
        }
    }
}
