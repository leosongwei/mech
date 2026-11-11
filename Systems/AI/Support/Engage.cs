// Fire control every platform shares: what the sight is laid on, how high each
// barrel is held, and which arms are allowed to open up.
//
// One law, two settings (`Core/FireControl.cs`):
//
//   * the *target* is a track - where it is, what it is really doing, and the pull
//     the crew has measured on it - and each shot is solved from the mouth of the
//     arm that would fire it, so the barrel's own offset is in the answer;
//   * **Z** is the distance that arm's gun is zeroed to.  A crew has no reticle to
//     read, so it zeroes on the range of the point it is engaging; a pilot zeroes
//     on the range he dialled in.  That one number is the whole difference between
//     the two settings of the law.
//
// The *sight* is one line - where the crew looks, and what swings the hull and the
// arms (`Systems/Gunnery.cs`) - so exactly one arm's solution is written into
// `sight_yaw` / `sight_pitch`: the **master arm** (`Defs.AI_MASTER_ARM`).  Every arm
// that has a solution still gets its own, and its own elevation goes into
// `aim_elev`; the arms that do not own the sight fire only while their own solution
// sits inside their own trigger gate, which is what keeps a slow round from being
// aimed by a fast one and the other way round.
//
// The trigger is a *geometric* gate, never a taste: an arm opens up when its bore
// is inside the angle the target's own hull covers at that range.  0.14 rad of
// bearing error is 280 m at 2 km, which is how a crew hoses the dirt at long
// range; `atan((radius + hit_r) / dist)` is the same rule the missile defence
// already uses.  The elevation side is the machine's own tolerance (`PITCH_TOL`),
// because the elevation a round leaves at is `aim_elev`, solved per arm.
//
// Two things are deliberately *not* here: randomness and `wd.spread`.  The round
// leaves on the elevation solved here and `Systems/Weapons.cs` adds the dispersion
// afterwards - that is the seam the unit tests live on.

namespace Mech.Server.Systems.AI;

public static class Engage
{
    /// <summary>m: the far hand-off a racked launcher keeps, for the two arms whose
    /// reach is not the crew's own engagement range.</summary>
    public const float RACK_REACH = 2200.0f;
    /// <summary>rad: how far the mount may still be from the sight when the arm is
    /// allowed to open up.  The elevation a round *leaves* at is `aim_elev`, solved
    /// per arm; this only says the machine has been laid.</summary>
    public const float PITCH_TOL = 0.02f;

    /// <summary>One crew's answer for one tick: the target, one solution per arm,
    /// and which arm owns the sight.</summary>
    public struct Aim
    {
        public Vec3 at;                 // the point the crew is engaging
        public float dist;              // m to it, from the eye
        public float radius;            // m, the target's biggest hit sphere
        public int master;              // 0 / 1, the arm the sight is laid on; -1 = none
        public bool valid;
        public FireControl.Solution left, right;

        public readonly FireControl.Solution this[int arm] => arm == 0 ? left : right;
    }

    /// <summary>The intercept point of a ground target, solved per arm.</summary>
    public static Aim ground_aim(World world, CrewColumns c, int i)
    {
        var e = world.ecs;
        var aim = new Aim { master = -1 };
        var r = new CrewRow(c, i);
        var eye = r.eye;
        var d = Defs.UNIT_DEFS[c.etype[i]];

        int tid = c.target[i];
        bool spotting = c.state[i] == AiState.SUPPRESS || tid < 0;
        Vec3 tpos, tvel = default, tacc = default;
        int tetype = Defs.ETYPE_APC;
        if (spotting)
        {
            // a memory rather than a hull: the spot it was last seen at, standing
            // still (whatever moved is not the spot's business)
            tpos = new Vec3(c.spot_x[i], c.spot_y[i], c.spot_z[i]);
        }
        else
        {
            tpos = e.fv("pos").get3(tid);
            tvel = e.fv("vel").get3(tid);
            tacc = c.accel_of(i, tid);
            tetype = e.uv("etype")[tid];
        }
        // the geometric centre of the hull: `height / 2` on both sides of the
        // client/server split, and the only aim height there is (`AGENTS.md`)
        aim.at = new Vec3(tpos.x, tpos.y + d.height * 0.5f, tpos.z);
        aim.dist = MathUtil.dist(eye, aim.at);
        aim.radius = MathUtil.max_radius(tetype);
        if (aim.dist > d.ai_range) return aim;

        var track = new FireControl.Track(tpos, tvel, tacc, d.height * 0.5f, aim.radius);
        float score = float.NegativeInfinity;
        for (int arm = 0; arm < 2; arm++)
        {
            int wid = c.weapon[i * 2 + arm];
            if (wid == Defs.WID_NONE) continue;
            var wd = Defs.WEAPONS[wid];
            if (aim.dist > wd.range) continue;
            if (c.arm_hp is not null && c.arm_hp[i * 2 + arm] <= 0f) continue;  // shot off
            var sol = FireControl.solve(ShotOf(world, c, i, arm, wd, eye, track), track);
            if (!sol.valid) continue;
            if (arm == 0) aim.left = sol; else aim.right = sol;
            // the gun fires at the elevation this solution asked for, and at no
            // other: `Weapons.fire` reads it and adds dispersion only
            c.aim_elev[i * 2 + arm] = sol.elevation;
            float s = Defs.AI_MASTER_ARM switch
            {
                Defs.MasterArm.Fastest => -sol.tof,        // the round that arrives first
                Defs.MasterArm.Punch => wd.ad + wd.ed,     // the round that hurts most
                _ => sol.tof,                              // the round that takes longest
            };
            if (s > score) { score = s; aim.master = arm; }
            aim.valid = true;
        }
        return aim;
    }

    /// <summary>The intercept point of the round the crew is firing at
    /// (`ai_threat`), for the arm that can bring it down.</summary>
    public static Aim intercept_aim(World world, int i, CrewColumns c, CrewRow r)
    {
        var aim = new Aim { master = -1 };
        var threats = world.threats;
        if (threats is null) return aim;
        int k = threats.index_of(c.threat[i]);
        if (k < 0) return aim;
        int arm = Crew.fused_arm(r);
        if (arm < 0) return aim;
        int wid = c.weapon[i * 2 + arm];
        var wd = Defs.WEAPONS[wid];
        var eye = r.eye;
        var tpos = threats.pos_at(k);
        var tvel = threats.vel_at(k);
        aim.at = tpos;
        aim.dist = MathUtil.dist(eye, tpos);
        // the angle the burst covers, not the round's body: a proximity fuse goes off
        // `prox` metres from what it passes, so that is the size a missile has to a
        // crew aiming at it (`trigger`: atan((radius + hit_r) / dist), 2.65 m at 1 km)
        aim.radius = MathF.Max(wd.prox, wd.hit_r);
        // a round in the air is a point to lead: nothing to pull, and its own body
        // is all there is to aim at
        var track = new FireControl.Track(tpos, tvel, default, 0f, wd.hit_r);
        var sol = FireControl.solve(ShotOf(world, c, i, arm, wd, eye, track), track);
        if (!sol.valid) return aim;
        if (arm == 0) aim.left = sol; else aim.right = sol;
        aim.master = arm;
        aim.valid = true;
        c.aim_elev[i * 2 + arm] = sol.elevation;
        return aim;
    }

    /// <summary>One gun's geometry, ready for the solver.</summary>
    private static FireControl.Shot ShotOf(World world, CrewColumns c, int i, int arm,
                                           WeaponDef wd, Vec3 eye, in FireControl.Track track)
    {
        // the mouth it will really leave from - the rack's elevation, the barrel's
        // reach, the tube the next round is in (`Weapons.launch_geometry`, the same
        // door the gun itself fires through)
        var r = new CrewRow(c, i);
        var muzzle = Weapons.launch_geometry(world, c.ids[i], arm, wd, r.yaw, r.pitch,
                                             out _, out _, out _);
        // Z: a crew zeroes on the range of the point it is engaging, which is the
        // number a pilot reads off his dial and the one thing the two settings of
        // the law do not share
        float zero = MathF.Max(MathUtil.dist(muzzle, track.pos), Ballistics.MIN_DIST);
        return new FireControl.Shot(muzzle, eye, wd, zero);
    }

    /// <summary>An arm's trigger: the bore inside the target's own angular size, and
    /// the mount laid.
    ///
    /// Two tolerances, because two different things are being asked.  The **bearing**
    /// is what the round steers by - a launch direction is `(yaw, elevation)`, and the
    /// elevation is the fire control's own `aim_elev` - so it is held to the angle the
    /// target covers at that range plus the round's own body: a round that misses a
    /// hull by a metre still counts, a round that misses a missile by a metre is a
    /// miss.  The **elevation** of the mount is a mechanical fact about the machine
    /// having come round: no unguided round leaves along it (its tube is laid by
    /// `aim_elev`), and the two kinds that do carry their own guidance, straight down
    /// the sight line this trigger's caller laid.  So `PITCH_TOL` is a slew tolerance,
    /// not a shape: 0.02 rad is where the arms are within a degree of the sight.
    /// `aim_yaw` / `aim_pitch` are the mount the round actually leaves along (a
    /// carrier's turret, a man's body, a mech's hull and arms), which trails the
    /// sight by the machine's slew rates - so a hull still coming round opens up when
    /// it is *pointed at the hull*, not when the crew has finished deciding to.
    /// `second_arm` is the platform's own rule about whether the far hand is allowed
    /// to join in at all.</summary>
    public static void trigger(World world, CrewColumns c, int i, in Aim aim,
                               float aim_yaw, float aim_pitch, bool second_arm)
    {
        var d = Defs.UNIT_DEFS[c.etype[i]];
        int t = 0;
        if (c.hp[i] > 0f)
        {
            for (int arm = 0; arm < 2; arm++)
            {
                if (arm == 1 && !second_arm) continue;
                var sol = aim[arm];
                if (!sol.valid) continue;
                int wid = c.weapon[i * 2 + arm];
                if (wid == Defs.WID_NONE) continue;
                var wd = Defs.WEAPONS[wid];
                // the far hand-off: a racked launcher keeps its 2.2 km, everything
                // else fires as far as the round it carries reaches
                float reach = wd.kind is "rocket" or "saclos" ? RACK_REACH : d.ai_range;
                if (aim.dist > MathF.Min(reach, wd.range)) continue;
                // the gate: the angle this hull covers at this range, plus the
                // round's own body.  A round that misses a mech by a metre still
                // counts; a round that misses a missile by a metre is a miss - and
                // `radius` carries whichever of the two it is.
                float tol = MathF.Atan((aim.radius + wd.hit_r) / MathF.Max(aim.dist, 1f));
                float err_yaw = MathF.Abs(MathUtil.wrap_pi(sol.yaw - aim_yaw));
                float err_pitch = MathF.Abs(sol.pitch - aim_pitch);
                if (err_yaw < tol && err_pitch < PITCH_TOL) t |= 1 << arm;
            }
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
}
