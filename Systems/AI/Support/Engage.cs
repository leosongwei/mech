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
// `sight_yaw` / `sight_pitch`: the **master arm** (`Defs.AI_MASTER_ARM`, the round
// that hurts most).  Every arm that has a solution still gets its own, and its own
// elevation goes into `aim_elev`; the arm that owns the sight is the arm that hits,
// and one that is loading, empty or waiting out a launch gap hands the sight over
// after `MASTER_HANDOFF` - the other muzzle is 2.86 m away, so it fires its own
// solution or nothing.  An arm that does not own the sight fires only while its own
// solution sits inside its own trigger gate, which is what keeps a slow round from
// being aimed by a fast one and the other way round.
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

    /// <summary>s; how long an arm must be unable to fire before it hands the sight
    /// to the other one.  A burst's own cycle (0.1 s) stays inside it, so a gun keeps
    /// the sight while it is firing; a 105 mm's reload (3 s) and a launch gap (15 s)
    /// do not, so the other arm takes its turn.  Without the hand-off one arm owns the
    /// sight for the whole battle: the muzzle that is not laid misses by 2.86 m.</summary>
    public const float MASTER_HANDOFF = 0.5f;

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

    /// <summary>The intercept point of a ground target, solved per arm.
    ///
    /// It solves against what the crew *knows*: `ai_tgt` is the belief
    /// `Systems/AI/Sense.cs` refreshes while the sight is clear and walks once it is
    /// cut (`CrewRow.belief_*`).  That is what keeps a crew honest - a screen of smoke
    /// costs it the target exactly as it costs a pilot his ring, and it keeps firing
    /// at the point it thinks the hull is at for `Crew.MEMORY` seconds.</summary>
    public static Aim ground_aim(World world, CrewColumns c, int i)
    {
        var aim = new Aim { master = -1 };
        var r = new CrewRow(c, i);
        if (!r.has_belief) return aim;                       // nothing to work
        var eye = r.eye;
        var own = Defs.UNIT_DEFS[c.etype[i]];
        var tdef = Defs.UNIT_DEFS[r.belief_type];            // the *target's* hull

        var tpos = r.belief_pos;                             // where it is, believed
        var tvel = r.belief_vel;                             // ... and what it is doing
        // a pull is a measurement, so it only exists for a hull the crew can see
        // (`Crew.measure_accel` stops sampling a lost one and the value ages out)
        var tacc = c.accel_of(i, c.target[i]);
        // the geometric centre of the hull: `height / 2` on both sides of the
        // client/server split, and the only aim height there is (`AGENTS.md`)
        aim.at = new Vec3(tpos.x, tpos.y + tdef.height * 0.5f, tpos.z);
        aim.dist = MathUtil.dist(eye, aim.at);
        aim.radius = MathUtil.max_radius(r.belief_type);
        if (aim.dist > own.ai_range) return aim;

        var track = new FireControl.Track(tpos, tvel, tacc, tdef.height * 0.5f);
        // each arm answers for itself; which of the two owns the sight is decided
        // after the loop, because it depends on more than the solutions
        bool[] has = new bool[2];
        bool[] can_fire = new bool[2];
        float[] score = { float.NegativeInfinity, float.NegativeInfinity };
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
            has[arm] = true;
            can_fire[arm] = _can_fire(world, c.ids[i], arm, wd);
            score[arm] = Defs.AI_MASTER_ARM switch
            {
                Defs.MasterArm.Fastest => -sol.tof,        // the round that arrives first
                Defs.MasterArm.Punch => wd.ad + wd.ed,     // the round that hurts most
                _ => sol.tof,                              // the round that takes longest
            };
            aim.valid = true;
        }
        // the sight goes on an arm that can fire *now*, and the two muzzles 2.86 m
        // apart mean that arm is the one that fires - the other one's round leaves on
        // the same bearing and misses (`atan(2.86 / dist)` against the target's own
        // angle).  So an arm that is reloading, waiting out `AI_LAUNCH_GAP` or empty
        // hands the sight over instead of blocking the other weapon for the battle.
        aim.master = _master(has, can_fire, score);
        if (aim.master < 0) aim.valid = false;
        return aim;
    }

    /// <summary>Can this arm put a round in the air in the next
    /// `MASTER_HANDOFF` seconds: a magazine, a reserve, no reload and no cooldown.
    /// A beam rider answers yes whatever its clock - the sight *is* its guidance, so
    /// handing the sight over would throw the round it is riding away.</summary>
    private static bool _can_fire(World world, int eid, int arm, WeaponDef wd)
    {
        var e = world.ecs;
        int left = e.iv("mag").get(eid, arm) + e.iv("ammo").get(eid, arm);
        if (left <= 0) return false;
        if (wd.kind == "saclos") return true;
        float busy = MathF.Max(e.fv("cool").get(eid, arm), e.fv("reload_t").get(eid, arm));
        return busy <= MASTER_HANDOFF;
    }

    /// <summary>The arm the sight goes on: the policy's pick among the ones that can
    /// fire now, and the pick among all of them when neither can (the machine stays
    /// pointed at something sensible while it waits).</summary>
    private static int _master(bool[] has, bool[] can_fire, float[] score)
    {
        int best = -1;
        for (int pass = 0; pass < 2 && best < 0; pass++)
        {
            for (int arm = 0; arm < 2; arm++)
            {
                if (!has[arm] || (pass == 0 && !can_fire[arm])) continue;
                if (best < 0 || score[arm] > score[best]) best = arm;
            }
        }
        return best;
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
        // the same reach test a ground shot gets: a round that cannot get there is
        // not a shot, and `trigger` would refuse it anyway
        if (aim.dist > wd.range) return aim;
        // the angle the burst covers, not the round's body: a proximity fuse goes off
        // `prox` metres from what it passes, so that is the size a missile has to a
        // crew aiming at it (`trigger`: atan((radius + hit_r) / dist), 2.65 m at 1 km)
        aim.radius = MathF.Max(wd.prox, wd.hit_r);
        // a round in the air is a point to lead: nothing to pull, and its own body
        // is all there is to aim at
        var track = new FireControl.Track(tpos, tvel, default, 0f);
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
        // door the gun itself fires through) - laid on the *bore*'s angles, which are
        // the ones `Weapons.fire` will use: a mech's hull, a carrier's turret, a
        // man's body (`Weapons.bore_of`)
        var (byaw, bpitch) = Weapons.bore_of(world, c.ids[i]);
        var muzzle = Weapons.launch_geometry(world, c.ids[i], arm, wd, byaw, bpitch,
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
        var own = Defs.UNIT_DEFS[c.etype[i]];
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
                float reach = wd.kind is "rocket" or "saclos" ? RACK_REACH : own.ai_range;
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
