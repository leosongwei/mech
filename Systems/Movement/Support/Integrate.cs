// The columns every driver binds, and the one integration pass they all end in.
//
// A hull's place and the truth about its motion are written here and nowhere
// else: `vel_cmd` is what the machine was told to make, `vel` is what it actually
// did, and the gap between the two *is* the machine (`Systems/Movement/Vehicle.cs`
// for the wheels, `Mech.cs` for the ramp).  A driver reads the crew's intent
// (`nav_route` / `nav_x` / `nav_z` / `nav_speed`) and writes the command; this
// pass turns the command into a position, with the terrain and the collision in
// the way.
//
// The three drivers share this schema because they integrate the same hull with
// the same field; what differs between them is how a command becomes a velocity,
// and that is their own file.

namespace Mech.Server.Systems.Movement;

/// <summary>What one moving hull owns: where it is, what it is doing, what it was
/// told, and the machine's private state (`wheel_speed`, `backout_t`,
/// `blocked_t`).</summary>
public sealed class MoveColumns : Columns<MoveRow>
{
    public float[] pos = null!, vel = null!, vel_cmd = null!;
    public float[] leg_yaw = null!, yaw = null!, hp = null!;
    public float[] wheel_speed = null!, backout_t = null!, blocked_t = null!;
    public float[] nav_x = null!, nav_z = null!, nav_speed = null!;
    public int[] nav_route = null!;
    public int[] path_i = null!;
    public byte[] etype = null!;

    internal static readonly string[] names =
    {
        "moves", "pos", "vel", "vel_cmd", "leg_yaw", "yaw", "hp", "etype",
        "wheel_speed", "backout_t", "blocked_t",
        "nav_route", "nav_x", "nav_z", "nav_speed", "path_i",
    };

    // the three kinds of chassis, disjoint and complete over every mover: a hull
    // either has wheels, or arm pools (the mech), or is a man
    public static readonly string[] OnFoot = Components.Cat(names, "person");
    public static readonly string[] OnMech = Components.Cat(names, "arm_hp");
    public static readonly string[] OnVehicle = Components.Cat(names, "vehicle");

    public MoveColumns(Group g) : base(g) { bind(g); }

    public override string[] Names => names;

    protected override void bind(Group g)
    {
        pos = g.f("pos"); vel = g.f("vel"); vel_cmd = g.f("vel_cmd");
        leg_yaw = g.f("leg_yaw"); yaw = g.f("yaw"); hp = g.f("hp");
        wheel_speed = g.f("wheel_speed"); backout_t = g.f("backout_t");
        blocked_t = g.f("blocked_t");
        nav_x = g.f("nav_x"); nav_z = g.f("nav_z"); nav_speed = g.f("nav_speed");
        nav_route = g.i("nav_route");
        path_i = g.i("path_i");
        etype = g.u8("etype");
    }

    public override MoveRow row(int i) => new MoveRow(this, i);
}

/// <summary>One hull: where it stands, what it was told, what its machine is
/// doing.  Every accessor is the buffer element itself.</summary>
public readonly struct MoveRow : IRow<MoveColumns>
{
    private readonly MoveColumns _c;
    public readonly int i;

    public MoveRow(MoveColumns c, int i) { _c = c; this.i = i; }

    public MoveColumns columns => _c;
    public int index => i;
    public int eid => _c.ids[i];
    public byte etype => _c.etype[i];

    public Vec3 pos
    {
        get => new(_c.pos[i * 3], _c.pos[i * 3 + 1], _c.pos[i * 3 + 2]);
        set { _c.pos[i * 3] = value.x; _c.pos[i * 3 + 1] = value.y; _c.pos[i * 3 + 2] = value.z; }
    }
    public Vec3 vel
    {
        get => new(_c.vel[i * 3], _c.vel[i * 3 + 1], _c.vel[i * 3 + 2]);
        set { _c.vel[i * 3] = value.x; _c.vel[i * 3 + 1] = value.y; _c.vel[i * 3 + 2] = value.z; }
    }
    public Vec3 vel_cmd
    {
        get => new(_c.vel_cmd[i * 3], _c.vel_cmd[i * 3 + 1], _c.vel_cmd[i * 3 + 2]);
        set { _c.vel_cmd[i * 3] = value.x; _c.vel_cmd[i * 3 + 1] = value.y; _c.vel_cmd[i * 3 + 2] = value.z; }
    }

    public ref float hp => ref _c.hp[i];
    public ref float leg_yaw => ref _c.leg_yaw[i];
    public ref float yaw => ref _c.yaw[i];
    public ref float wheel_speed => ref _c.wheel_speed[i];
    public ref float backout_t => ref _c.backout_t[i];
    public ref float blocked_t => ref _c.blocked_t[i];

    /// <summary>How far along `nav_route` this hull has driven (the driver's own
    /// cursor, `Systems/Movement/Support/Drive.cs`).</summary>
    public ref int path_i => ref _c.path_i[i];

    // the crew's intent
    public int nav_route => _c.nav_route[i];
    public float nav_x => _c.nav_x[i];
    public float nav_z => _c.nav_z[i];
    public float nav_speed => _c.nav_speed[i];
}

public static class Integrate
{
    const float TILE = Terrain.TILE;

    /// <summary>Whether a hull of radius `rad` cannot stand at (x, z): the point
    /// test plus four samples on the circle, because the tile under a hull's centre
    /// says nothing about the wall its shoulder is against.</summary>
    public static bool blocked(Terrain terrain, float x, float z, float rad) =>
        terrain.blocked(x, z) || terrain.blocked(x + rad, z) ||
        terrain.blocked(x - rad, z) || terrain.blocked(x, z + rad) ||
        terrain.blocked(x, z - rad) ||
        terrain.trunk_blocked(x, z, rad);

    /// <summary>Turn every walker's legs towards the direction it is driving.
    ///
    /// Locomotion, not aim: a hull's legs follow the machine's own motion, which
    /// is why this is here and not in the AI.  A car is skipped - its `leg_yaw`
    /// *is* its steering state and its own driver writes it.</summary>
    public static void turn_legs(MoveColumns c, float dt)
    {
        float rate_mech = 2.6f * dt;
        float rate_other = 6.0f * dt;
        foreach (var r in c)
        {
            var cmd = r.vel_cmd;
            if (MathF.Abs(cmd.x) < 0.05f && MathF.Abs(cmd.z) < 0.05f) continue;
            float want = MathF.Atan2(cmd.z, cmd.x);
            float rate = r.etype == Defs.ETYPE_MECH ? rate_mech : rate_other;
            r.leg_yaw = MathUtil.turn_toward(r.leg_yaw, want, rate);
        }
    }

    /// <summary>Move one hull by the velocity its own machine produced: terrain
    /// bounds, the collision slide, and the velocity that actually came out of it.
    ///
    /// A car that made no headway counts it (`blocked_t`) and backs out with the
    /// wheels held over once it has pushed for a moment; that is the one piece of
    /// this that is wheeled only.</summary>
    public static void step(World world, MoveRow r, float vx, float vz, bool wheeled, float dt)
    {
        var terrain = world.terrain;
        float inv_dt = 1f / dt;
        float maxx = terrain.w * TILE - 2f * TILE;
        float maxz = terrain.d * TILE - 2f * TILE;
        byte et = r.etype;

        var p = r.pos;
        float x = p.x, y0 = p.y, z = p.z;
        float nx = x, nz = z;
        if (MathF.Abs(vx) > 0.01f || MathF.Abs(vz) > 0.01f)
        {
            nx = MathF.Min(MathF.Max(x + vx * dt, TILE), maxx);
            nz = MathF.Min(MathF.Max(z + vz * dt, TILE), maxz);
            float rad = Defs.UNIT_DEFS[et].radius * 0.6f;
            if (blocked(terrain, nx, nz, rad))
            {
                if (!blocked(terrain, nx, z, rad)) nz = z;
                else if (!blocked(terrain, x, nz, rad)) nx = x;
                else { nx = x; nz = z; }
            }
            if (wheeled)
            {
                // blocked means it asked for a stride and got a fraction of it -
                // measuring absolute metres would call a slow crawl in the open a
                // wall, and a heavy hull pulls away slowly
                float step = MathF.Sqrt((nx - x) * (nx - x) + (nz - z) * (nz - z));
                bool stuck = step < 0.3f * MathF.Sqrt(vx * vx + vz * vz) * dt;
                if (!stuck || r.backout_t > 0f)
                {
                    r.blocked_t = 0f;                     // rolling again
                }
                else
                {
                    r.blocked_t = r.blocked_t + dt;
                    if (r.blocked_t >= Defs.APC_BLOCKED)
                    {
                        r.backout_t = Defs.APC_BACKOUT;
                        r.blocked_t = 0f;
                        r.wheel_speed = 0f;
                    }
                }
            }
        }
        // the velocity that actually came out of this tick, and the slope it drove
        // over - a hull on the ground has a vertical velocity too
        float ny = terrain.height_at(nx, nz);
        r.vel = new Vec3((nx - x) * inv_dt, (ny - y0) * inv_dt, (nz - z) * inv_dt);
        r.pos = new Vec3(nx, ny, nz);
    }
}
