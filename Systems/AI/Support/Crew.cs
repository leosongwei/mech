// The columns every crew pass binds, and the geometry a crew shares.
//
// A schema declares exactly what its pass reads (`Systems/Flights.cs` does the same
// for a round), so a platform difference is a schema difference: `EngageMech`
// binds `arm_hp` because a mech has arms, `EngageVehicle` / `EngageFoot` do not own
// the buffer at all and cannot read it by accident.  This base carries the part
// every pass needs - where the crew is, where it looks, what it carries, what it
// remembers - and a mission appends its own names with `Components.Cat`.
//
// Nothing here moves: the crews write intent (`nav_*`), the drivers write `vel`
// (`Systems/Movement/`).  `vel` is read here because a crew leads a target by its
// own motion and a radar needs a closing speed.

namespace Mech.Server.Systems.AI;

public class CrewColumns : Columns<CrewRow>
{
    public float[] pos = null!, vel = null!, yaw = null!, pitch = null!;
    public float[] sight_yaw = null!, sight_pitch = null!, turret_yaw = null!;
    public float[] hp = null!, hpmax = null!;
    public float[] timer = null!, spot_x = null!, spot_y = null!, spot_z = null!;
    public float[] spot_t = null!;
    // the fire control's own answer per arm (`Core/FireControl.cs`), and the pull
    // measured on the target: [ax, az, prev vx, prev vz, age, target id]
    public float[] aim_elev = null!, tacc = null!;
    /// <summary>Bound where the platform owns arms; null for a man or a carrier.</summary>
    public float[]? arm_hp;
    public float[] nav_x = null!, nav_z = null!, nav_speed = null!;
    public int[] mag = null!, target = null!, threat = null!, nav_route = null!;
    public byte[] etype = null!, faction = null!, weapon = null!, trigger = null!;
    public byte[] state = null!, cover = null!, goal = null!, lane = null!;

    internal static readonly string[] names =
    {
        "pos", "vel", "yaw", "pitch", "sight_yaw", "sight_pitch", "turret_yaw",
        "hp", "hpmax", "ai_timer", "spot_x", "spot_y", "spot_z", "spot_t",
        "nav_x", "nav_z", "nav_speed", "nav_route",
        "mag", "target", "ai_threat",
        "etype", "faction", "weapon", "trigger", "ai_state", "ai_cover", "ai_goal", "lane",
    };

    /// <summary>Every crew, whatever platform or mission.</summary>
    public static readonly string[] OnCrew = Components.Cat(names, AiTags.CREW);

    public CrewColumns(Group g) : base(g) { bind(g); }

    public override string[] Names => names;

    protected override void bind(Group g)
    {
        pos = g.f("pos"); vel = g.f("vel"); yaw = g.f("yaw"); pitch = g.f("pitch");
        sight_yaw = g.f("sight_yaw"); sight_pitch = g.f("sight_pitch");
        turret_yaw = g.f("turret_yaw");
        hp = g.f("hp"); hpmax = g.f("hpmax");
        timer = g.f("ai_timer");
        spot_x = g.f("spot_x"); spot_y = g.f("spot_y"); spot_z = g.f("spot_z");
        spot_t = g.f("spot_t");
        aim_elev = g.f("aim_elev"); tacc = g.f("ai_tacc");
        arm_hp = g.has("arm_hp") ? g.f("arm_hp") : null;
        nav_x = g.f("nav_x"); nav_z = g.f("nav_z"); nav_speed = g.f("nav_speed");
        nav_route = g.i("nav_route");
        mag = g.i("mag"); target = g.i("target"); threat = g.i("ai_threat");
        etype = g.u8("etype"); faction = g.u8("faction"); weapon = g.u8("weapon");
        trigger = g.u8("trigger"); state = g.u8("ai_state"); cover = g.u8("ai_cover");
        goal = g.u8("ai_goal"); lane = g.u8("lane");
    }

    public override CrewRow row(int i) => new CrewRow(this, i);

    // ---- the target's pull, measured the way the client measures it ----------
    //
    // The wire gives a pilot two velocity samples and he differentiates them
    // (`net.js`: 0.25 s EMA, a jump over 25 m/s^2 read as a teleport, a sample older
    // than 0.5 s ignored).  A crew holds the velocity itself, so it measures the same
    // quantity from the same kind of finite difference - and stops trusting it on the
    // same terms, because a stale pull is a circle drawn in the air.

    /// <summary>s; the EMA's time constant - `net.js` uses the same figure.</summary>
    public const float ACCEL_TAU = 0.25f;
    /// <summary>s; a sample older than this says nothing about now.</summary>
    public const float ACCEL_STALE = 0.5f;

    /// <summary>Measure this tick's velocity of `tid` into `ai_tacc`.  A different
    /// target than last tick starts the average over: two hulls' velocities are not
    /// a difference.</summary>
    public void measure_accel(int i, int tid, Vec3 vel, float dt)
    {
        int b = i * 6;
        if (tid < 0 || (int)tacc[b + 5] != tid)
        {
            tacc[b + 0] = 0f; tacc[b + 1] = 0f;
            tacc[b + 2] = vel.x; tacc[b + 3] = vel.z;
            tacc[b + 4] = 0f; tacc[b + 5] = tid;
            return;
        }
        float ax = (vel.x - tacc[b + 2]) / dt;
        float az = (vel.z - tacc[b + 3]) / dt;
        tacc[b + 2] = vel.x; tacc[b + 3] = vel.z;
        // a difference of two samples is a measurement of the *average* over the
        // tick, and a teleport is not a pull: anything past 25 m/s^2 is dropped
        if (ax * ax + az * az < 625f)
        {
            float k = 1f - MathF.Exp(-dt / ACCEL_TAU);
            tacc[b + 0] += (ax - tacc[b + 0]) * k;
            tacc[b + 1] += (az - tacc[b + 1]) * k;
        }
        tacc[b + 4] += dt;
    }

    /// <summary>The pull to lead on: zero unless it was measured on this target and
    /// is still fresh.</summary>
    public Vec3 accel_of(int i, int tid)
    {
        int b = i * 6;
        if ((int)tacc[b + 5] != tid || tacc[b + 4] > ACCEL_STALE) return default;
        return new Vec3(tacc[b + 0], 0f, tacc[b + 1]);
    }
}

/// <summary>One crew: every accessor is the buffer element itself.</summary>
public readonly struct CrewRow : IRow<CrewColumns>
{
    private readonly CrewColumns _c;
    public readonly int i;

    public CrewRow(CrewColumns c, int i) { _c = c; this.i = i; }

    public CrewColumns columns => _c;
    public int index => i;
    public int eid => _c.ids[i];
    public byte etype => _c.etype[i];
    public byte faction => _c.faction[i];
    public byte lane => _c.lane[i];
    public int target => _c.target[i];
    public byte state => _c.state[i];
    public byte cover => _c.cover[i];
    public float spot_t => _c.spot_t[i];
    public float nav_speed => _c.nav_speed[i];
    public int route => _c.nav_route[i];

    public Vec3 pos => new(_c.pos[i * 3], _c.pos[i * 3 + 1], _c.pos[i * 3 + 2]);
    public Vec3 vel => new(_c.vel[i * 3], _c.vel[i * 3 + 1], _c.vel[i * 3 + 2]);

    /// <summary>Left arm (0) or right (1).</summary>
    public int weapon_at(int arm) => _c.weapon[i * 2 + arm];
    public int mag_at(int arm) => _c.mag[i * 2 + arm];

    public float hp
    {
        get => _c.hp[i];
        set => _c.hp[i] = value;
    }
    public float hpmax => _c.hpmax[i];

    /// <summary>The mount a round leaves from: a mech's hull (swung by `Gunnery`),
    /// a man's body, a carrier's turret - the crew writes only what it owns.</summary>
    public float yaw
    {
        get => _c.yaw[i];
        set => _c.yaw[i] = value;
    }
    public float pitch
    {
        get => _c.pitch[i];
        set => _c.pitch[i] = value;
    }

    // the crew's own state
    public ref float timer => ref _c.timer[i];
    public ref float spot_t_mem => ref _c.spot_t[i];
    public ref int target_mem => ref _c.target[i];
    public ref byte state_mem => ref _c.state[i];
    public ref byte goal_mem => ref _c.goal[i];

    // the crew's aim
    public float sight_yaw
    {
        get => _c.sight_yaw[i];
        set => _c.sight_yaw[i] = value;
    }
    public float sight_pitch
    {
        get => _c.sight_pitch[i];
        set => _c.sight_pitch[i] = value;
    }
    public float turret_yaw
    {
        get => _c.turret_yaw[i];
        set => _c.turret_yaw[i] = value;
    }
    public byte trigger
    {
        get => _c.trigger[i];
        set => _c.trigger[i] = value;
    }

    // where it last saw its target
    public Vec3 spot => new(_c.spot_x[i], _c.spot_y[i], _c.spot_z[i]);
    public void remember_spot(Vec3 p)
    {
        _c.spot_x[i] = p.x; _c.spot_y[i] = p.y; _c.spot_z[i] = p.z;
    }

    /// <summary>The eye the crew looks through: a mech's panoramic sight on its
    /// roof (`MathUtil.sight_eye`, the same point the client's camera sits in), or
    /// three quarters of the way up anything else.</summary>
    public Vec3 eye => etype == Defs.ETYPE_MECH
        ? MathUtil.sight_eye(pos, _c.yaw[i])
        : new Vec3(pos.x, pos.y + Defs.UNIT_DEFS[etype].height * 0.75f, pos.z);
}

/// <summary>The aiming platform a crew stands on: how its sight is mounted, which
/// is the one thing that separates the three engage systems.</summary>
public static class Crew
{
    /// <summary>First arm that can bring a missile down: it carries a fuse, it has
    /// rounds and the mount is still there.  -1 if none can.
    ///
    /// Data driven on purpose (`Defs.WEAPONS[...].prox`): a new fused weapon joins
    /// in with no change here, and a 12.7 mm carrier stays out because its fuse
    /// radius is zero.  A destroyed arm loses its ammunition (`Combat._mech_hit`),
    /// so the magazine test covers the metal too.</summary>
    public static int fused_arm(CrewRow r)
    {
        for (int arm = 0; arm < 2; arm++)
        {
            int wid = r.weapon_at(arm);
            if (wid == 0 || Defs.WEAPONS[wid].prox <= 0f) continue;
            if (r.mag_at(arm) <= 0) continue;
            return arm;
        }
        return -1;
    }
}
