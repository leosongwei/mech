// Lock-on: the server owns the lock state, the client mirrors it.
//
// One interaction, two weapons.  The IR top attack and the laser beam rider ask
// the pilot for the same thing: put the cross on a hull and the seeker takes it,
// in the same tick - the circle being on the hull *is* the lock - and they differ
// in what the lock *is* once it is held:
//
//   * the seeker (`kind` top) answers in the reticle circle (`Defs.LOCK_FRAC` of
//     the view, so the cone shrinks as the optics zoom) and drops the target the
//     moment it leaves that circle - there is no hysteresis to hold a strafing
//     hull;
//   * the designator (`kind` saclos) keeps up to `Defs.BEAM_CHANNELS` lines, each
//     held while the point the laser is on stays inside a 40 deg cone about the
//     sight line (`Defs.BEAM_CONE`), because a laser is *pointed* rather than
//     stared at - and held *through* a lost sight line, on the memory
//     (`Systems/Beams.cs`), so a hull that comes back into the cone takes its line
//     back.
//
// **A line is claimed by the shot, not by the cross** (`Systems/Weapons.cs::fire`
// calls `Beams.lay` for the hull it is firing at).  What the reticle is on before
// that is a *candidate*: the hull a trigger pull would take, mirrored into
// `Player.lock_target` so the HUD can ring it.  Laying on the cross instead would
// make steering cost lines - a cross swept across four hulls on the way to the one
// being aimed at fills every channel and then evicts the oldest, so the pilot's
// held target would be dropped by a near miss of his own aim.
//
// The channels themselves live in the designator component on the hull
// (`Systems/Beams.cs`), not in `Player`: a lock belongs to a machine, dies with
// it, and is what the beam reads.  `Player` keeps one mirrored number - the hull
// the reticle rings - because that is what the client's HUD is drawn from.
//
// The seeker needs to *see* the hull before it takes it: anything that cuts a line
// of sight blocks the acquisition - the ground, a building, a tree canopy, and a
// screen of smoke, which is a temporary piece of cover like the others.  A channel
// already held does not need the line of sight: the designator flies it on what it
// has measured (`Systems/Beams.cs`), which is the whole point of the CV module.
//
// Ported from `backend/server/systems/locking.py`.

namespace Mech.Server.Systems;

public static class Locking
{
    public const float MIN_ZOOM = 1f;
    public const float MAX_ZOOM = 20f;
    public const float MIN_ASPECT = 0.4f;
    public const float MAX_ASPECT = 4f;
    public const float MIN_DIST = 20f;
    public const float LOS_STEP = 8f;   // ray march: half a tile, so a 16 m block
                                        // can never slip between two samples

    /// <summary>What the seeker looks at: the middle of the hull, not the tracks.</summary>
    private static Vec3 _sight_point(Vec3 p, int etype) =>
        new(p.x, p.y + Defs.UNIT_DEFS[etype].height * 0.5f, p.z);

    /// <summary>Angular radius of the reticle circle, in radians.</summary>
    public static float cone_of(Player pl) => _cone(pl.zoom, pl.aspect, Defs.LOCK_FRAC);

    private static float _cone(float zoom, float aspect, float frac)
    {
        float fov = Defs.BASE_FOV / Math.Clamp(zoom, MIN_ZOOM, MAX_ZOOM);
        return MathF.Atan(MathF.Tan(fov * 0.5f) * frac
                          * Math.Clamp(aspect, MIN_ASPECT, MAX_ASPECT));
    }

    public static void run(World world, float dt)
    {
        var e = world.ecs;
        var pos = e.fv("pos");
        var etype = e.uv("etype");
        var faction = e.uv("faction");
        var ids = world.lockable_units();

        foreach (var pl in world.players.Values)
        {
            int eid = pl.mech;
            // a lock belongs to the machine: a dead pilot, or one whose hull never
            // carried a launcher, holds nothing
            if (eid < 0 || !e.is_alive(eid) || !e.has(eid, "beam_tgt"))
            {
                pl.lock_target = -1;
                continue;
            }
            WeaponDef? wd = null;
            for (int arm = 0; arm < 2; arm++)
            {
                int w = e.uv("weapon").get(eid, arm);
                if (w != 0 && Defs.WEAPONS[w].@lock != 0 && Combat.arm_alive(world, eid, arm))
                    wd = Defs.WEAPONS[w];
            }
            if (wd is null)
            {
                Beams.free_all(world, eid);
                pl.lock_target = -1;
                continue;
            }

            // the seeker is in the sight head, not on the barrels: it sweeps the
            // line the pilot's cross is on and answers the view instantly, so a
            // target can be locked while the hull is still swinging round to it
            var (origin, sy, sp) = Weapons.aim_of(world, eid);
            var d = MathUtil.dir_from(sy, sp);
            float cone = cone_of(pl);
            // what a held channel has to stay inside: the seeker's circle, or the
            // designator's cone - which `Systems/Beams.cs` tests on the point the
            // beam is actually on, so a hull nobody can see keeps its line
            bool beam_rider = _carries_beam(world, eid);
            float keep = beam_rider ? Defs.BEAM_CONE : cone;
            int mine = faction[eid];
            var tgt = e.iv("beam_tgt");
            for (int ch = 0; ch < Defs.BEAM_CHANNELS; ch++)
            {
                int tid = tgt.get(eid, ch);
                if (tid < 0) continue;
                bool hold = e.is_alive(tid) && faction[tid] != mine && e.has(tid, "lockable");
                if (hold)
                {
                    // a beam rider's cone is asked of the designator (the tracked
                    // point, walked when the sight is cut); a seeker needs the hull
                    // itself inside the reticle circle
                    var sp_at = _sight_point(pos.get3(tid), etype[tid]);
                    var (ang, dist) = _angle_to(sp_at, origin, d);
                    if (dist > Defs.lock_range(wd)) hold = false;
                    else if (!beam_rider && ang > keep) hold = false;
                }
                // a channel is a lock: once it is laid on the hull the sight line
                // and the window are the only things that can give it up
                if (!hold) Beams.free(world, eid, ch);
            }

            // what the cross is on: the hull a trigger pull would take, and the only
            // thing the reticle rings.  It costs nothing and claims nothing - the
            // shot claims the channel, and the channels already held are the
            // machine's *formal* targets (the wire's `locks`, the thin diamonds).
            // The candidate *stays* named once the cross has picked it, so the
            // pilot chooses a hull and is then free to move his sight where the shot
            // needs it - another hull under the circle takes over as the candidate
            int cand = _candidate(world, eid, wd, cone, ids, origin, d);
            // a weapon that can fire without a lock lets go of the hull the moment the
            // circle leaves it: the pilot's next pull is then either "at that hull" or
            // "at nothing", and "at nothing" is a shot he means
            bool release = wd.@lock == 2;
            pl.lock_target = _hold(world, eid, pl.lock_target, cand, wd, release);
        }
    }

    /// <summary>What a trigger pull takes: the hull the reticle is on, which is the
    /// machine's *candidate* (`Player.lock_target`, found by `run` this tick).
    /// Nothing under the cross, nothing fired - and a hull is a formal target only
    /// once a shot has claimed a channel for it (`Systems/Weapons.cs::fire`).
    ///
    /// The return value is the channel that hull already rides, or -1 when the shot
    /// has to claim a fresh one.</summary>
    public static int pick(World world, int eid, WeaponDef wd, Player pl, out int target)
    {
        target = -1;
        if (pl is null) return -1;
        var e = world.ecs;
        if (!e.has(eid, "beam_tgt")) return -1;
        int tid = pl.lock_target;
        if (tid < 0 || !e.is_alive(tid) || !e.has(tid, "lockable")) return -1;
        if (e.uv("faction")[tid] == e.uv("faction")[eid]) return -1;
        var (origin, _, _) = Weapons.aim_of(world, eid);
        var pos = e.fv("pos");
        var aim = _sight_point(pos.get3(tid), e.uv("etype")[tid]);
        float dx = aim.x - origin.x, dy = aim.y - origin.y, dz = aim.z - origin.z;
        if (MathF.Sqrt(dx * dx + dy * dy + dz * dz) > Defs.lock_range(wd)) return -1;
        target = tid;
        return _channel_of(world, eid, tid);
    }

    /// <summary>The hull the reticle is on: nearest the cross, inside the reticle
    /// circle, in range, with a clear line of sight.  A hull that already rides a
    /// channel is one too - the pilot may put four more rounds into a target he is
    /// already shooting at.  -1 = nothing there, so a trigger pull has nothing to
    /// claim.</summary>
    private static int _candidate(World world, int eid, WeaponDef wd, float cone,
                                  int[] ids, Vec3 origin, Vec3 d)
    {
        var e = world.ecs;
        var pos = e.fv("pos");
        var etype = e.uv("etype");
        var faction = e.uv("faction");
        int mine = faction[eid];
        int best = -1;
        float best_ang = cone;
        foreach (int tid in ids)
        {
            if (faction[tid] == mine) continue;
            var tp = pos.get3(tid);
            float vx = tp.x - origin.x, vy = tp.y - origin.y, vz = tp.z - origin.z;
            float dist = MathF.Sqrt(vx * vx + vy * vy + vz * vz);
            if (!(dist > MIN_DIST) || !(dist < Defs.lock_range(wd))) continue;
            var aim = _sight_point(tp, etype[tid]);
            var (ang, _) = _angle_to(aim, origin, d);
            if (ang >= best_ang) continue;
            if (Visibility.source(world, eid, tid, aim) == VisibilitySource.None) continue;
            best = tid;
            best_ang = ang;
        }
        return best;
    }

    /// <summary>The candidate this tick: a hull the cross has just picked takes
    /// over, and with nothing under the cross the one already named is kept while
    /// it is a live enemy hull inside the weapon's lock range.
    ///
    /// The cross is for *choosing*.  A candidate that evaporated the moment the
    /// sight moved would make a lock weapon unusable the way a trigger that only
    /// works while the cross is on the mark is: the shot has to be aimed, and aiming
    /// means looking somewhere else.  A weapon that can *also* be fired without a
    /// lock is the other case: there the candidate is released with the circle
    /// (`release`), so a pilot aiming at a hull can still say "nothing" by taking his
    /// cross off it.</summary>
    private static int _hold(World world, int eid, int cur, int cand, WeaponDef wd,
                             bool release)
    {
        if (cand >= 0) return cand;
        if (release) return -1;
        var e = world.ecs;
        if (cur < 0 || !e.is_alive(cur) || !e.has(cur, "lockable")) return -1;
        if (e.uv("faction")[cur] == e.uv("faction")[eid]) return -1;
        var (origin, _, _) = Weapons.aim_of(world, eid);
        var aim = _sight_point(e.fv("pos").get3(cur), e.uv("etype")[cur]);
        float dx = aim.x - origin.x, dy = aim.y - origin.y, dz = aim.z - origin.z;
        float range = Defs.lock_range(wd);
        return MathF.Sqrt(dx * dx + dy * dy + dz * dz) <= range ? cur : -1;
    }

    /// <summary>The channel this launcher already has on `tid`, or -1.</summary>
    private static int _channel_of(World world, int eid, int tid)
    {
        var tgt = world.ecs.iv("beam_tgt");
        for (int ch = 0; ch < Defs.BEAM_CHANNELS; ch++)
            if (tgt.get(eid, ch) == tid) return ch;
        return -1;
    }

    /// <summary>Whether the machine carries a laser designator - a beam rider in
    /// either arm.</summary>
    private static bool _carries_beam(World world, int eid)
    {
        var e = world.ecs;
        for (int arm = 0; arm < 2; arm++)
        {
            int wid = e.uv("weapon").get(eid, arm);
            if (wid != 0 && Defs.WEAPONS[wid].kind == "saclos"
                && Combat.arm_alive(world, eid, arm)) return true;
        }
        return false;
    }

    /// <summary>Angle between the sight line and a hull, and the range to it.
    /// A hull on top of the eye answers (99, 0) - no direction to measure.</summary>
    private static (float ang, float dist) _angle_to(Vec3 sp, Vec3 origin, Vec3 d)
    {
        float vx = sp.x - origin.x, vy = sp.y - origin.y, vz = sp.z - origin.z;
        float n = MathF.Sqrt(vx * vx + vy * vy + vz * vz);
        if (n < 1e-3f) return (99f, 0f);
        float dot = (vx * d.x + vy * d.y + vz * d.z) / n;
        return (MathF.Acos(Math.Clamp(dot, -1f, 1f)), n);
    }
}
