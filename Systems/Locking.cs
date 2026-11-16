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

            // what the cross is on: a *candidate*, whose line only a shot claims.
            // Sweeping the reticle over a hull therefore costs nothing and takes
            // nothing away - a channel already held survives every near miss of
            // the pilot's aim
            int cand = _candidate(world, eid, wd, cone, ids, origin, d);

            // what the reticle reads: the line a trigger pull would ride (a channel
            // already held), and failing that the hull under the cross
            _mirror(world, eid, pl, keep, cand);
        }
    }

    /// <summary>The channel a shot from `wd` rides: the complete one nearest the
    /// sight line, inside the cone that weapon's own seeker answers in - the
    /// reticle circle for a fire-and-forget round, the whole window for a beam
    /// rider, whose laser can be pointed at all of it.
    ///
    /// `target` is the hull the shot takes, and a line already held is not the only
    /// thing that qualifies: the hull under the cross is a lock the shot can claim
    /// there and then (`Systems/Weapons.cs::fire` lays its channel), which is what
    /// makes "cross on the hull, pull the trigger" work on the tick the cross
    /// arrives.  `target` is -1 when there is neither, and the trigger is
    /// refused.</summary>
    public static int pick(World world, int eid, WeaponDef wd, Player pl, out int target)
    {
        target = -1;
        var e = world.ecs;
        if (!e.has(eid, "beam_tgt")) return -1;
        float cone;
        if (wd.kind == "saclos") cone = Defs.BEAM_CONE;
        else if (pl is not null) cone = cone_of(pl);
        else cone = _cone(1f, 16f / 9f, Defs.LOCK_FRAC);
        var (origin, sy, sp) = Weapons.aim_of(world, eid);
        var d = MathUtil.dir_from(sy, sp);
        var tgt = e.iv("beam_tgt");
        var pos = e.fv("pos");
        var etype = e.uv("etype");
        int mine = e.uv("faction")[eid];
        int best = -1;
        float best_ang = cone;
        for (int ch = 0; ch < Defs.BEAM_CHANNELS; ch++)
        {
            int tid = tgt.get(eid, ch);
            if (tid < 0) continue;
            if (!e.is_alive(tid) || e.uv("faction")[tid] == mine) continue;
            var (ang, dist) = _angle_to(_sight_point(pos.get3(tid), etype[tid]), origin, d);
            if (ang >= best_ang || dist > Defs.lock_range(wd)) continue;
            best = ch;
            best_ang = ang;
            target = tid;
        }
        if (best >= 0) return best;
        // nothing held on the line: the hull the cross is on is what the shot takes
        if (pl is not null && pl.lock_target >= 0 && e.is_alive(pl.lock_target))
            target = pl.lock_target;
        return -1;
    }

    /// <summary>The hull the reticle is on that no line is held for: nearest the
    /// cross, inside the reticle circle, in range, with a clear line of sight.  -1
    /// = nothing there, so a trigger pull has nothing to claim.</summary>
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
            if (_channel_of(world, eid, tid) >= 0) continue;       // already held
            var tp = pos.get3(tid);
            float vx = tp.x - origin.x, vy = tp.y - origin.y, vz = tp.z - origin.z;
            float dist = MathF.Sqrt(vx * vx + vy * vy + vz * vz);
            if (!(dist > MIN_DIST) || !(dist < Defs.lock_range(wd))) continue;
            var aim = _sight_point(tp, etype[tid]);
            var (ang, _) = _angle_to(aim, origin, d);
            if (ang >= best_ang) continue;
            if (!Sight.clear(world, origin, aim, LOS_STEP)) continue;
            best = tid;
            best_ang = ang;
        }
        return best;
    }

    /// <summary>The channel this launcher already has on `tid`, or -1.</summary>
    private static int _channel_of(World world, int eid, int tid)
    {
        var tgt = world.ecs.iv("beam_tgt");
        for (int ch = 0; ch < Defs.BEAM_CHANNELS; ch++)
            if (tgt.get(eid, ch) == tid) return ch;
        return -1;
    }

    /// <summary>Mirror the reticle's own lock into `Player`, which is what the wire
    /// carries and the HUD draws: the channel nearest the cross, which is also the
    /// one a trigger pull takes, and failing that the hull under the cross - a lock
    /// the shot would claim, which is what the ring and the lock tone are for.</summary>
    private static void _mirror(World world, int eid, Player pl, float cone, int cand)
    {
        var e = world.ecs;
        var (origin, sy, sp) = Weapons.aim_of(world, eid);
        var d = MathUtil.dir_from(sy, sp);
        var tgt = e.iv("beam_tgt");
        var pos = e.fv("pos");
        var etype = e.uv("etype");
        int best = -1;
        float best_ang = float.MaxValue;
        for (int ch = 0; ch < Defs.BEAM_CHANNELS; ch++)
        {
            int tid = tgt.get(eid, ch);
            if (tid < 0 || !e.is_alive(tid)) continue;
            var (ang, _) = _angle_to(_sight_point(pos.get3(tid), etype[tid]), origin, d);
            if (ang > cone || ang >= best_ang) continue;
            best = ch;
            best_ang = ang;
        }
        pl.lock_target = best < 0 ? cand : tgt.get(eid, best);
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
