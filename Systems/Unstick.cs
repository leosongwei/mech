// A hull is a footprint on the ground, and the ground under it can be a building.
//
// `Integrate.step` refuses a step whose destination circle overlaps a structure, a
// rock, a base pad or a tree trunk - and a hull that was *put down* overlapping one
// has no step to take: every destination within reach still overlaps what it is
// already standing in, the velocity it makes is refused, and it stands at full
// throttle for the rest of the match.  Nothing else can free it, either: `Drive`'s
// fan probes the ground ahead of the hull and never its own footprint, so it reports
// the way clear, and a detour changes a route the hull cannot begin to follow.  The
// one measured case is a spawn (`World.base_spawn` / `spawn_unit`) that lands a mech
// 55-140 m off its own base, inside a base structure; `--check stuck` finds 0-1 of
// them per 12 minutes of battle.
//
// So this pass is the way out: a hull whose own footprint overlaps something is put
// back on the nearest ground it can stand on, and its velocity is zeroed - `pos` and
// `vel` are what a teleport writes, and the order it was under (`vel_cmd`) is left
// alone, so it drives off from where it lands.  The look is cheap and the fault is
// permanent, so it runs on one tick in ten rather than every tick.

using Microsoft.Extensions.Logging;
using Mech.Server.Systems.Movement;

namespace Mech.Server.Systems;

public static class Unstick
{
    private static readonly ILogger log = MechLog.get("mech.unstick");

    /// <summary>Ticks between looks: the fault never heals itself, so a third of a
    /// second is invisible, and this keeps the five-sample test off 29 ticks in 30.</summary>
    public const int EVERY = 10;
    /// <summary>Metres the search will go for a clear spot.  A building is a whole
    /// tile (16 m), so a hull centred in one is out inside 12 m; the rest is slack
    /// for a base pad or a cluster of houses.</summary>
    public const float REACH = 40f;
    /// <summary>Rings of the search, in metres.</summary>
    public const float STEP = 2f;
    /// <summary>Bearings tried on each ring.</summary>
    public const int BEARINGS = 16;

    public static void run(World world, float dt)
    {
        if (world.tick % EVERY != 0) return;
        foreach (var g in world.ecs.groups(MoveColumns.OnMech)) sweep(world, g);
        foreach (var g in world.ecs.groups(MoveColumns.OnVehicle)) sweep(world, g);
    }

    /// <summary>One chassis' worth of hulls: whoever is standing in something.</summary>
    private static void sweep(World world, Group g)
    {
        var terrain = world.terrain;
        var c = new MoveColumns(g);
        foreach (var r in c)
        {
            var p = r.pos;
            float rad = Defs.UNIT_DEFS[r.etype].radius * 0.6f;
            if (!Integrate.blocked(terrain, p.x, p.z, rad)) continue;
            if (!nearest(terrain, p.x, p.z, rad, out float nx, out float nz))
            {
                log.LogWarning("mech.unstick: hull {eid} (etype {et}) is inside a structure at "
                               + "({x:0}, {z:0}) and there is no clear ground within {reach} m",
                               r.eid, r.etype, p.x, p.z, REACH);
                continue;
            }
            r.pos = new Vec3(nx, terrain.height_at(nx, nz), nz);
            r.vel = new Vec3(0f, 0f, 0f);
            log.LogDebug("mech.unstick: hull {eid} (etype {et}) stood inside a structure at "
                         + "({x:0}, {z:0}); put back at ({nx:0}, {nz:0}), {d:0} m away",
                         r.eid, r.etype, p.x, p.z, nx, nz,
                         MathUtil.dist2d(p.x, p.z, nx, nz));
        }
    }

    /// <summary>The nearest spot a hull of this size can stand on, ring by ring out
    /// from where it is: the first ring that has one wins, and the nearest bearing on
    /// it.  `false` when the whole reach is blocked - which is a wall of houses, and
    /// the warning above is the record of it.</summary>
    private static bool nearest(Terrain terrain, float x, float z, float rad,
                                out float nx, out float nz)
    {
        for (float ring = STEP; ring <= REACH; ring += STEP)
        {
            float turn = MathF.Tau / BEARINGS;
            for (int b = 0; b < BEARINGS; b++)
            {
                float a = b * turn;
                float cx = x + MathF.Cos(a) * ring, cz = z + MathF.Sin(a) * ring;
                if (Integrate.blocked(terrain, cx, cz, rad)) continue;
                nx = cx;
                nz = cz;
                return true;
            }
        }
        nx = x;
        nz = z;
        return false;
    }
}
