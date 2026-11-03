// The sight leads, the machine follows.
//
// Ported from `backend/server/systems/gunnery.py`.
//
// A mech's aim is a free turret on the roof.  It answers the crew - a pilot's
// mouse or the AI's intercept solution - *instantly*, and everything the crew
// sees is seen through it: the eye the camera sits in (`MathUtil.sight_eye`),
// the IR seeker, and the beam a SACLOS missile rides.
//
// The hull underneath and the arms beside it are steel on pivots, so they can
// only catch up: `sight_yaw` is where the sight points, `yaw` swings after it at
// `MECH_HULL_RATE` and the arms' `pitch` climbs after it at `MECH_ARM_RATE`.
// Flick the view faster than that and the machine lags the cross - the barrels,
// and the impact markers the client draws off them, trail behind the reticle for
// a moment.
//
// Because the sight stands `Defs.MECH_SIGHT_Z` behind the hull's rotation axis,
// that catch-up is not free of side effects: a hull swinging under a sight that
// already points somewhere else carries the sight - and the pilot's eye - round
// an arc.  `MathUtil.sight_eye` is that geometry; this system is what makes it
// happen.
//
// Every other kind of unit aims with its whole mount and has nothing to catch up
// with, so its sight is simply mirrored from the mount it aims with: the hull for
// a rifleman or a civilian, the turret for a carrier.  That keeps one meaning for
// `sight_yaw` on the wire - where the crew is looking - whoever is reading it.

namespace Mech.Server.Systems;

public static class Gunnery
{
    public static void run(World world, float dt)
    {
        var e = world.ecs;
        float hull_step = Defs.MECH_HULL_RATE * dt;
        float arm_step = Defs.MECH_ARM_RATE * dt;
        foreach (var g in e.groups("sight_yaw", "sight_pitch", "yaw", "pitch",
                                   "turret_yaw", "etype"))
        {
            int n = g.n;
            var yaw = g.f("yaw");
            var pitch = g.f("pitch");
            var sy = g.f("sight_yaw");
            var sp = g.f("sight_pitch");
            if (g.has("vehicle"))
            {
                // a carrier's hull belongs to the driver - `yaw` is its steering
                // state - and the crew turns nothing but the turret, which the AI
                // turns directly: its sight *is* the turret
                var turret = g.f("turret_yaw");
                for (int row = 0; row < n; row++)
                {
                    sy[row] = turret[row];
                    sp[row] = pitch[row];
                }
                continue;
            }
            // a rifle is aimed with the man's whole body: nothing lags anything
            var etype = g.u8("etype");
            for (int row = 0; row < n; row++)
            {
                if (etype[row] != Defs.ETYPE_MECH)
                {
                    sy[row] = yaw[row];
                    sp[row] = pitch[row];
                    continue;
                }
                yaw[row] = MathUtil.turn_toward(yaw[row], sy[row], hull_step);
                // the mount's mechanical stops: the sight may look higher than
                // the arms can elevate, and an arm sits against its stop until
                // the sight comes back into the envelope
                float want = Math.Clamp(sp[row], Defs.MECH_PITCH_MIN, Defs.MECH_PITCH_MAX);
                pitch[row] = Math.Clamp(
                    MathUtil.turn_toward(pitch[row], want, arm_step),
                    Defs.MECH_PITCH_MIN, Defs.MECH_PITCH_MAX);
            }
        }
    }
}
