// The mech's locomotion: legs, and a hull that has to be swung onto them.
//
// A mech rolls up to the speed it was asked for instead of snapping to it - 0-100
// km/h takes about 3 s, so leaning on the stick is a surge rather than a teleport.
// Legs lined up with the motion carry the hull all the way; a hull that fights its
// legs (or lost the chassis) tops out lower, and the ramp aims at that reduced
// speed.  The ramp *is* the difference between the command and the truth, which is
// why it lives here and not in the crew that gave the command.

using Mech.Server.Systems.AI;

namespace Mech.Server.Systems.Movement;

public static class Mech
{
    public static void run(World world, float dt)
    {
        foreach (var g in world.ecs.groups(MoveColumns.OnMech))
        {
            var c = new MoveColumns(g);
            // a pilot's machine owns its `vel_cmd` (the stick) and a crew's is the
            // intent - but the legs swing after *whatever* was commanded, pilot or
            // not: a hull whose legs are still lying where it spawned drives at the
            // leg-alignment floor (`0.45` of top speed) and nothing else
            if (g.has(AiTags.CREW)) foreach (var r in c) Drive.legs(world, r, dt);
            Integrate.turn_legs(c, dt);
            foreach (var r in c)
            {
                var cmd = r.vel_cmd;
                float cx = cmd.x, cz = cmd.z;
                float f;
                if (MathF.Abs(cx) > 1e-3f || MathF.Abs(cz) > 1e-3f)
                {
                    float align = MathF.Cos(MathUtil.wrap_pi(MathF.Atan2(cz, cx) - r.leg_yaw));
                    f = 0.45f + 0.55f * MathF.Max(0f, align);
                }
                else
                {
                    f = 1f;
                }
                if (r.hp <= 0f) f *= 0.2f;
                var vel = r.vel;
                float ax = vel.x, az = vel.z;
                float dx = cx * f - ax;
                float dz = cz * f - az;
                float step = MathF.Sqrt(dx * dx + dz * dz);
                float limit = Defs.MECH_ACCEL * dt;
                if (step > limit)
                {
                    dx *= limit / step;
                    dz *= limit / step;
                }
                Integrate.step(world, r, ax + dx, az + dz, wheeled: false, dt);
            }
        }
    }
}
