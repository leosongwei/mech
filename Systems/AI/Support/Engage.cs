// Fire control every platform shares: what to point at, how far away it is, and
// which arms are allowed to open up.
//
// Two aims, one rule each.  A *ground* target is led by the intercept of the
// slowest round that can reach it - the sight is one line whatever the arms carry,
// and a guided round is aimed with it (a beam rider rides it), so leading for a
// 1080 m/s cannon while the right arm is a missile that takes seconds to arrive
// put the missile consistently short.  A *missile* is led by the same solver and
// needs a far tighter trigger, because a burst weapon is aimed at an area: the
// tolerance is the angle its fuse covers at that range, ~0.004 rad at 1 km, not the
// 0.14 rad a hull gets.
//
// What the three platforms do with the answer is their own file: a mech writes
// `sight_yaw` and lets `Systems/Gunnery.cs` swing the hull and climb the arms, a
// carrier turns its turret, a man turns his body.

namespace Mech.Server.Systems.AI;

public static class Engage
{
    /// <summary>Where to point, how far it is, and the speed to lead it with.</summary>
    public struct Aim
    {
        public Vec3 point;
        public float dist;
        public float speed;
        public bool valid;
    }

    /// <summary>The shell an AI crew is willing to pull a trigger inside
    /// (radians of bearing error).</summary>
    public const float HULL_TOL = 0.14f;
    /// <summary>...and the elevation the mount has to be inside as well.</summary>
    public const float PITCH_TOL = 0.02f;
    /// <summary>m: the far hand-off a racked launcher kept, for the two arms whose
    /// reach is not the crew's own engagement range.</summary>
    public const float RACK_REACH = 2200.0f;
    /// <summary>m: inside this a carrier fires its second arm whatever it is.</summary>
    public const float APC_SECOND = 3500.0f;

    /// <summary>The intercept point of a ground target (or of the spot it was last
    /// seen at), solved for the slowest round that can reach it.</summary>
    public static Aim ground_aim(World world, CrewColumns c, int i)
    {
        var aim = new Aim { dist = 0f };
        var eye = new CrewRow(c, i).eye;
        byte etype = c.etype[i];

        int tid = c.target[i];
        bool spotting = c.state[i] == AiState.SUPPRESS || tid < 0;
        Vec3 at;
        Vec3 tvel = default;
        if (spotting)
        {
            at = new Vec3(c.spot_x[i], c.spot_y[i] + 1.5f, c.spot_z[i]);
            aim.dist = MathUtil.dist2d(eye.x, eye.z, at.x, at.z);
        }
        else
        {
            var tp = world.ecs.fv("pos").get3(tid);
            tvel = world.ecs.fv("vel").get3(tid);
            at = new Vec3(tp.x, tp.y + 2.0f, tp.z);
            aim.dist = MathUtil.dist2d(eye.x, eye.z, tp.x, tp.z);
        }
        var d = Defs.UNIT_DEFS[etype];
        // the slowest round that can reach decides how far ahead to aim
        float speed = 0f;
        for (int arm = 0; arm < 2; arm++)
        {
            int wid = c.weapon[i * 2 + arm];
            if (wid == 0) continue;
            var wd = Defs.WEAPONS[wid];
            if (aim.dist > wd.range) continue;
            float v = MathUtil.mean_speed(wd, aim.dist);
            speed = speed <= 0f ? v : MathF.Min(speed, v);
        }
        if (speed <= 0f)
        {
            int w0 = c.weapon[i * 2];
            speed = Defs.WEAPONS[w0 != 0 ? w0 : Defs.WID_AC30].vel;
        }
        // no gravity lift: the fire control zeroes the gun for the drop at shot
        // time, so lifting the aim as well would double compensate
        aim.point = MathUtil.solve_lead(eye, at, tvel, speed, 0f);
        aim.speed = speed;
        aim.valid = aim.dist < d.ai_range;
        return aim;
    }

    /// <summary>The intercept point of the round the crew is firing at
    /// (`ai_threat`), for the arm that can bring it down.</summary>
    public static Aim intercept_aim(World world, int i, CrewColumns c, CrewRow r)
    {
        var aim = new Aim();
        var threats = world.threats;
        if (threats is null) return aim;
        int k = threats.index_of(c.threat[i]);
        if (k < 0) return aim;
        int arm = Crew.fused_arm(r);
        if (arm < 0) return aim;
        var eye = r.eye;
        var tpos = threats.pos_at(k);
        var tvel = threats.vel_at(k);
        aim.dist = MathUtil.dist(eye, tpos);
        var wd = Defs.WEAPONS[c.weapon[i * 2 + arm]];
        aim.point = MathUtil.solve_lead(eye, tpos, tvel, wd.vel, 0f);
        aim.speed = wd.vel;
        aim.valid = true;
        return aim;
    }

    /// <summary>The ground trigger: both arms, gated on the bore being on the
    /// intercept.
    ///
    /// `aim_yaw` is the mount the round actually leaves from (a carrier's turret, a
    /// man's body, a mech's own hull) and `laid` says whether the elevation can
    /// deliver the shot at all - a mech's arms climb at their own rate, so a hull
    /// still coming round would otherwise hose the dirt.</summary>
    public static void trigger(World world, CrewColumns c, int i, float want_yaw,
                               float aim_yaw, float dist, bool laid, bool second_arm)
    {
        var d = Defs.UNIT_DEFS[c.etype[i]];
        float err = MathF.Abs(MathUtil.wrap_pi(want_yaw - aim_yaw));
        int t = 0;
        if (err < HULL_TOL && laid && c.hp[i] > 0f)
        {
            t = 1;
            // the right arm fires whatever it carries, as far as that round
            // reaches: 2.2 km was the rocket nest's hand-off, but a racked launcher
            // or a 105 mm barrel is no less ready at the crew's own engagement range
            int w1 = c.weapon[i * 2 + 1];
            if (second_arm && w1 != 0 && dist < d.ai_range)
            {
                var wd1 = Defs.WEAPONS[w1];
                float reach = wd1.kind is "rocket" or "saclos" ? RACK_REACH : d.ai_range;
                if (dist < MathF.Min(reach, wd1.range)) t |= 2;
            }
            if (c.etype[i] == Defs.ETYPE_APC && dist < APC_SECOND) t |= 2;
        }
        // a semi automatic arm only fires on the rising edge of the trigger, so the
        // crew has to let go of it every other tick
        if ((t & 2) != 0 && (world.tick & 1) != 0
            && Defs.WEAPONS[c.weapon[i * 2 + 1]].semi != 0)
        {
            t &= ~2;
        }
        c.trigger[i] = (byte)t;
    }

    /// <summary>The missile trigger: one arm, and a tolerance the fuse radius
    /// covers - a round that misses a hull's 1.45 m body by a metre still counts,
    /// and a round that misses a missile by a metre is a miss.</summary>
    public static void trigger_intercept(World world, int i, CrewColumns c, float want_yaw,
                                        float aim_yaw, bool laid, int arm, float dist, int wid)
    {
        var wd = Defs.WEAPONS[wid];
        float tol = MathF.Atan((wd.prox + wd.hit_r) / MathF.Max(dist, 1f));
        float err = MathF.Abs(MathUtil.wrap_pi(want_yaw - aim_yaw));
        c.trigger[i] = (byte)(err < tol && laid ? 1 << arm : 0);
    }
}
