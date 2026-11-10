// Civilians: they wander around their home square and stay out of the war.
//
// A civilian is not a crew - it carries no `ai_crew`, no `combat` and no gun - but
// it is a mover, so its legs are `Systems/Movement/Foot.cs`'s like anybody's.  All
// this pass does is pick where it wants to be next and how fast, which is the same
// kind of note a policy writes for a mech.

namespace Mech.Server.Systems.AI;

public sealed class WanderColumns : Columns<WanderRow>
{
    public float[] pos = null!, home_x = null!, home_z = null!, yaw = null!;
    public float[] timer = null!, nav_x = null!, nav_z = null!, nav_speed = null!;

    internal static readonly string[] names =
        { "home_x", "home_z", "moves", "pos", "yaw", "ai_timer", "nav_x", "nav_z", "nav_speed" };

    public WanderColumns(Group g) : base(g) { bind(g); }

    public override string[] Names => names;

    protected override void bind(Group g)
    {
        pos = g.f("pos"); home_x = g.f("home_x"); home_z = g.f("home_z");
        yaw = g.f("yaw"); timer = g.f("ai_timer");
        nav_x = g.f("nav_x"); nav_z = g.f("nav_z"); nav_speed = g.f("nav_speed");
    }

    public override WanderRow row(int i) => new WanderRow(this, i);
}

public readonly struct WanderRow : IRow<WanderColumns>
{
    private readonly WanderColumns _c;
    public readonly int i;

    public WanderRow(WanderColumns c, int i) { _c = c; this.i = i; }

    public WanderColumns columns => _c;
    public int index => i;
    public Vec3 pos => new(_c.pos[i * 3], _c.pos[i * 3 + 1], _c.pos[i * 3 + 2]);
    public float home_x => _c.home_x[i];
    public float home_z => _c.home_z[i];
    public ref float timer => ref _c.timer[i];
    public ref float yaw => ref _c.yaw[i];
    public ref float nav_x => ref _c.nav_x[i];
    public ref float nav_z => ref _c.nav_z[i];
    public ref float nav_speed => ref _c.nav_speed[i];
}

public static class Wander
{
    /// <summary>m/s a civilian strolls at.</summary>
    public const float SPEED = 2.0f;
    /// <summary>m from home it turns back - the square is theirs, the field is not.</summary>
    public const float LEASH = 240.0f;
    /// <summary>m of leg it picks each time it changes its mind.</summary>
    public const float LEG = 40.0f;

    public static void run(World world, float dt)
    {
        foreach (var g in world.ecs.groups(WanderColumns.names))
        {
            var c = new WanderColumns(g);
            foreach (var r in c)
            {
                r.timer -= dt;
                if (r.timer > 0f) continue;
                r.timer = world.rng.uniform(1.5f, 4.5f);
                float px = r.pos.x, pz = r.pos.z;
                float dx = r.home_x - px;
                float dz = r.home_z - pz;
                float a = dx * dx + dz * dz > LEASH * LEASH
                    ? MathF.Atan2(dz, dx)                 // wandered too far: head back
                    : world.rng.random() * MathUtil.TAU;   // otherwise: any way it likes
                r.yaw = a;
                r.nav_x = px + MathF.Cos(a) * LEG;
                r.nav_z = pz + MathF.Sin(a) * LEG;
                r.nav_speed = SPEED;
            }
        }
    }
}
