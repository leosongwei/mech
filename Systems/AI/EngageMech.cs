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
    public float[] arm_hp = null!;

    private static readonly string[] mech_names = Components.Cat(names, "arm_hp");
    public static readonly string[] OnMech = Components.Cat(mech_names, AiTags.MECH);

    public MechColumns(Group g) : base(g) { arm_hp = g.f("arm_hp"); }

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
        var (want_yaw, want_pitch) = lay(c, r, aim.point);
        bool laid = MathF.Abs(want_pitch - r.pitch) < Engage.PITCH_TOL;
        Engage.trigger(world, c, r.index, want_yaw, r.yaw, aim.dist, laid,
                       c.arm_hp[r.index * 2 + 1] > 0f);
    }

    private static void intercept(World world, MechColumns c, CrewRow r)
    {
        var aim = Engage.intercept_aim(world, r.index, c, r);
        int arm = Crew.fused_arm(r);
        if (!aim.valid || arm < 0) { r.trigger = 0; return; }
        var (want_yaw, want_pitch) = lay(c, r, aim.point);
        bool laid = MathF.Abs(want_pitch - r.pitch) < Engage.PITCH_TOL;
        Engage.trigger_intercept(world, r.index, c, want_yaw, r.yaw, laid, arm, aim.dist,
                                 c.weapon[r.index * 2 + arm]);
    }

    /// <summary>Put the sight on a point, held at the mount's elevation stops, and
    /// answer the angles it was laid to.</summary>
    private static (float yaw, float pitch) lay(MechColumns c, CrewRow r, Vec3 point)
    {
        var eye = r.eye;
        var (want_yaw, want_pitch) = MathUtil.angles_to(point.x - eye.x, point.y - eye.y,
                                                       point.z - eye.z);
        want_pitch = MathF.Max(Defs.MECH_PITCH_MIN, MathF.Min(Defs.MECH_PITCH_MAX, want_pitch));
        r.sight_yaw = want_yaw;
        r.sight_pitch = want_pitch;
        return (want_yaw, want_pitch);
    }
}
