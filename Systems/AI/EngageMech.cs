// The mech's gun: a sight on the roof, and arms that climb after it.
//
// The crew lays the *sight* on the intercept and `Systems/Gunnery.cs` swings the
// hull and the arms after it at their own rates, so the trigger here waits for the
// *bore*: a hull still coming round (or an arm still climbing) would otherwise hose
// the dirt.  That is why this file never writes `yaw` / `pitch` - the machine is
// already doing it - and why the elevation is held at the mount's stop instead of
// being driven against it for ever: a shot the mounts cannot elevate to is one the
// crew does not take.

namespace Mech.Server.Systems.AI;

public sealed class MechColumns : CrewColumns
{
    private static readonly string[] mech_names = Components.Cat(names, "arm_hp");
    public static readonly string[] OnMech = Components.Cat(mech_names, AiTags.MECH);

    public MechColumns(Group g) : base(g) { }

    public override string[] Names => mech_names;
}

public static class EngageMech
{
    public static void run(World world, float dt)
    {
        foreach (var g in world.ecs.groups(MechColumns.OnMech))
        {
            var c = new MechColumns(g);
            foreach (var r in c)
            {
                // one writer for this platform's trigger, whichever job it is
                if (r.cover != 0) { intercept(world, c, r); continue; }
                if (r.target < 0 && r.spot_t <= 0f) { r.trigger = 0; continue; }
                ground(world, c, r);
            }
        }
    }

    private static void ground(World world, MechColumns c, CrewRow r)
    {
        var aim = Engage.ground_aim(world, c, r.index);
        if (!aim.valid) { r.trigger = 0; return; }
        // the sight goes on the *master arm's* intercept point: one line, and the
        // arm that owns it is the policy's choice (`Defs.AI_MASTER_ARM`)
        lay(c, r, aim[aim.master].point);
        Engage.trigger(world, c, r.index, aim, r.yaw, r.pitch, second_arm: true);
    }

    private static void intercept(World world, MechColumns c, CrewRow r)
    {
        var aim = Engage.intercept_aim(world, r.index, c, r);
        if (!aim.valid) { r.trigger = 0; return; }
        var sol = aim[aim.master];
        lay(c, r, sol.point);
        Engage.trigger(world, c, r.index, aim, r.yaw, r.pitch, second_arm: true);
    }

    /// <summary>Put the sight on a point, held at the mount's elevation stops.
    ///
    /// The sight is what the hull and the arms chase (`Systems/Gunnery.cs`), so this
    /// is the only thing a crew writes to aim: the *bore* is the machine's, and the
    /// trigger waits for it.</summary>
    private static void lay(MechColumns c, CrewRow r, Vec3 point)
    {
        var eye = r.eye;
        var (want_yaw, want_pitch) = MathUtil.angles_to(point.x - eye.x, point.y - eye.y,
                                                       point.z - eye.z);
        want_pitch = MathF.Max(Defs.MECH_PITCH_MIN, MathF.Min(Defs.MECH_PITCH_MAX, want_pitch));
        r.sight_yaw = want_yaw;
        r.sight_pitch = want_pitch;
    }
}
