// The rifleman's gun: his whole body.
//
// A man aims with `yaw` / `pitch` - there is no sight to lay and no mount to catch
// up with - so the crew owns his body and `Systems/Gunnery.cs` mirrors his sight off
// it for the HUD and the seeker.  His legs belong to the driver
// (`Systems/Movement/Foot.cs`), which is why he can walk one way and shoot another.
//
// The trigger is the same shared rule as the other two platforms: the arms' reach,
// the semi automatic edge and the bearing gate each arm solves for itself - the
// angle his target's own hull covers at that range.

namespace Mech.Server.Systems.AI;

public sealed class FootColumns : CrewColumns
{
    private static readonly string[] foot_names = names;
    public static readonly string[] OnFoot = Components.Cat(foot_names, AiTags.CREW, AiTags.FOOT);

    public FootColumns(Group g) : base(g) { }

    public override string[] Names => foot_names;
}

public static class EngageFoot
{
    /// <summary>rad/s a man turns onto a target.</summary>
    public const float BODY_RATE = 3.2f;

    public static void run(World world, float dt)
    {
        foreach (var g in world.ecs.groups(FootColumns.OnFoot))
        {
            var c = new FootColumns(g);
            foreach (var r in c)
            {
                if (r.cover != 0) { intercept(world, c, r, dt); continue; }
                // nothing in hand and nothing remembered: nothing to shoot at
                if (!r.has_belief) { r.trigger = 0; continue; }
                ground(world, c, r, dt);
            }
        }
    }

    private static void ground(World world, FootColumns c, CrewRow r, float dt)
    {
        var aim = Engage.ground_aim(world, c, r.index);
        if (!aim.valid) { r.trigger = 0; return; }
        lay(r, aim[aim.master].point, dt);
        // a man's body is his mount: it is both the sight and the bore
        Engage.trigger(world, c, r.index, aim, r.yaw, r.pitch, second_arm: true);
    }

    private static void intercept(World world, FootColumns c, CrewRow r, float dt)
    {
        var aim = Engage.intercept_aim(world, r.index, c, r);
        if (!aim.valid) { r.trigger = 0; return; }
        lay(r, aim[aim.master].point, dt);
        Engage.trigger(world, c, r.index, aim, r.yaw, r.pitch, second_arm: true);
    }

    /// <summary>Turn the man's body onto a point.</summary>
    private static void lay(CrewRow r, Vec3 point, float dt)
    {
        var eye = r.eye;
        var (want_yaw, want_pitch) = MathUtil.angles_to(point.x - eye.x, point.y - eye.y,
                                                       point.z - eye.z);
        r.yaw = MathUtil.turn_toward(r.yaw, want_yaw, BODY_RATE * dt);
        r.pitch = MathF.Max(-0.4f, MathF.Min(0.7f,
            r.pitch + (want_pitch - r.pitch) * MathF.Min(1f, 6f * dt)));
    }
}
