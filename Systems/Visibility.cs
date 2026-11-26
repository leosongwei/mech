namespace Mech.Server.Systems;

public enum VisibilitySource
{
    None,
    Direct,
    Datalink,
}

/// <summary>Knowledge visibility.  Physical propagation keeps calling `Sight.clear`
/// directly; this gate never makes a laser, seeker or warning receiver see through cover.</summary>
public static class Visibility
{
    public static VisibilitySource source(World world, int observer, int target, Vec3 point)
    {
        var e = world.ecs;
        if (!e.is_alive(observer) || !e.is_alive(target)) return VisibilitySource.None;
        var (eye, _, _) = Weapons.aim_of(world, observer);
        if (Sight.clear(world, eye, point)) return VisibilitySource.Direct;
        int faction = e.uv("faction")[observer];
        return Datalink.known(world, faction, target) ? VisibilitySource.Datalink : VisibilitySource.None;
    }
}
