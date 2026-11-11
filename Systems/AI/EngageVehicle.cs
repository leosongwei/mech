// The carrier's gun: a turret on a hull somebody else is driving.
//
// The hull points where it rolls (`Systems/Movement/Vehicle.cs`), so the crew turns
// nothing but the turret, and its sight simply *is* the turret (`Systems/Gunnery.cs`
// mirrors it).  Everything else - the lead, the arms' reach, the semi automatic
// edge - is the shared fire control in `Systems/AI/Support/Engage.cs`.

namespace Mech.Server.Systems.AI;

public sealed class VehicleColumns : CrewColumns
{
    private static readonly string[] veh_names = names;
    public static readonly string[] OnVehicle = Components.Cat(veh_names, AiTags.CREW, "vehicle");

    public VehicleColumns(Group g) : base(g) { }

    public override string[] Names => veh_names;
}

public static class EngageVehicle
{
    /// <summary>rad/s the turret swings (the carrier's own mount, not the hull).</summary>
    public const float TURRET_RATE = 2.2f;

    public static void run(World world, float dt)
    {
        foreach (var g in world.ecs.groups(VehicleColumns.OnVehicle))
        {
            var c = new VehicleColumns(g);
            foreach (var r in c)
            {
                if (r.cover != 0) { intercept(world, c, r, dt); continue; }
                if (r.target < 0 && r.spot_t <= 0f) { r.trigger = 0; continue; }
                ground(world, c, r, dt);
            }
        }
    }

    private static void ground(World world, VehicleColumns c, CrewRow r, float dt)
    {
        var aim = Engage.ground_aim(world, c, r.index);
        if (!aim.valid) { r.trigger = 0; return; }
        lay(r, aim[aim.master].point, dt);
        // a carrier's mount *is* its turret: `Gunnery` mirrors its sight off it, so
        // the bore and the laid line are one and the same
        Engage.trigger(world, c, r.index, aim, r.turret_yaw, r.pitch, second_arm: true);
    }

    private static void intercept(World world, VehicleColumns c, CrewRow r, float dt)
    {
        var aim = Engage.intercept_aim(world, r.index, c, r);
        if (!aim.valid) { r.trigger = 0; return; }
        lay(r, aim[aim.master].point, dt);
        Engage.trigger(world, c, r.index, aim, r.turret_yaw, r.pitch, second_arm: true);
    }

    /// <summary>Swing the turret onto a point.</summary>
    private static void lay(CrewRow r, Vec3 point, float dt)
    {
        var eye = r.eye;
        var (want_yaw, want_pitch) = MathUtil.angles_to(point.x - eye.x, point.y - eye.y,
                                                       point.z - eye.z);
        // the mount, not the sight: `Gunnery` mirrors a carrier's sight off its
        // turret and its `pitch`, so this is where the crew's aim lands
        r.turret_yaw = MathUtil.turn_toward(r.turret_yaw, want_yaw, TURRET_RATE * dt);
        r.pitch = MathF.Max(-0.4f, MathF.Min(0.7f,
            r.pitch + (want_pitch - r.pitch) * MathF.Min(1f, 6f * dt)));
    }
}
