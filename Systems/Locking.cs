// Infrared lock-on: the server owns the lock state, the client mirrors it.
//
// The seeker only sees what sits inside the reticle circle: a target has to stay
// within 5 % of the view width (`Defs.LOCK_FRAC`) for `lock_time` seconds, and
// the lock drops the moment it leaves the circle - there is no hysteresis to
// hold a strafing hull.  Infantry is too small and too cold for the seeker: only
// vehicles and mechs can be locked.
//
// The seeker needs to *see* the hull: anything that cuts a line of sight blocks
// the acquisition and breaks a lock that is already held - the ground, a
// building, a tree canopy, and a screen of smoke, which is a temporary piece of
// cover like the others.  That question is asked of `Systems/Sight.cs` and of
// nothing else, because the pilot's lock used to ask only for the field and so
// let a hull be locked straight through a screen of smoke, leaving a lock box
// standing on a target the missile could no longer see.
//
// The circle is a fixed fraction of the screen, so the angular cone shrinks as
// the optics zoom in; the pilot sends the zoom and the viewport aspect with every
// input message and the server keeps both numbers the same as the client.
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
    private static Vec3 _sight_point(Vec3 p) => new(p.x, p.y + 1.5f, p.z);

    /// <summary>Angular radius of the reticle circle, in radians.</summary>
    public static float cone_of(Player pl)
    {
        float zoom = Math.Clamp(pl.zoom, MIN_ZOOM, MAX_ZOOM);
        float aspect = Math.Clamp(pl.aspect, MIN_ASPECT, MAX_ASPECT);
        float fov = Defs.BASE_FOV / zoom;
        return MathF.Atan(MathF.Tan(fov * 0.5f) * Defs.LOCK_FRAC * aspect);
    }

    public static void run(World world, float dt)
    {
        var e = world.ecs;
        var pos = e.fv("pos");
        var faction = e.uv("faction");
        var ids = world.lockable_units();

        foreach (var pl in world.players.Values)
        {
            int eid = pl.mech;
            if (eid < 0 || !e.is_alive(eid)) continue;
            WeaponDef? wd = null;
            for (int arm = 0; arm < 2; arm++)
            {
                int w = e.uv("weapon").get(eid, arm);
                if (w != 0 && Defs.WEAPONS[w].@lock != 0 && Combat.arm_alive(world, eid, arm))
                    wd = Defs.WEAPONS[w];
            }
            if (wd is null)
            {
                pl.lock_target = -1;
                pl.lock_progress = 0f;
                continue;
            }

            // the seeker is in the sight head, not on the barrels: it sweeps the
            // line the pilot's cross is on and answers the view instantly, so a
            // target can be locked while the hull is still swinging round to it
            var (origin, sy, sp) = Weapons.aim_of(world, eid);
            var d = MathUtil.dir_from(sy, sp);
            float cone = cone_of(pl);
            int mine = faction[eid];

            int tracked = pl.lock_target;
            if (tracked >= 0)
            {
                // the hull left the circle (or died / turned friendly, or slid
                // behind cover): drop it
                if (!e.is_alive(tracked) || faction[tracked] == mine
                    || !e.has(tracked, "lockable"))
                {
                    tracked = -1;
                }
                else
                {
                    var (ang, dist) = _angle_to(_sight_point(pos.get3(tracked)), origin, d);
                    if (ang > cone || dist > wd.range
                        || !Sight.clear(world, origin, _sight_point(pos.get3(tracked)), LOS_STEP))
                        tracked = -1;
                }
                if (tracked < 0)
                {
                    pl.lock_target = -1;
                    pl.lock_progress = 0f;
                }
            }

            if (tracked < 0)
            {
                int best = -1;
                if (ids.Length > 0)
                {
                    // candidates: in range, an enemy, and closer than the crosshair
                    // is wide.  Closest to the crosshair first, but a hull in cover
                    // is never a candidate: take the first one the seeker can see
                    int n = 0;
                    var cand = new int[ids.Length];
                    var pang = new float[ids.Length];
                    foreach (int tid in ids)
                    {
                        var tp = pos.get3(tid);
                        float vx = tp.x - origin.x, vy = tp.y - origin.y, vz = tp.z - origin.z;
                        float dist = MathF.Sqrt(vx * vx + vy * vy + vz * vz);
                        if (!(dist > MIN_DIST) || !(dist < wd.range)) continue;
                        if (faction[tid] == mine) continue;
                        var aim = _sight_point(tp);
                        float ax = aim.x - origin.x, ay = aim.y - origin.y, az = aim.z - origin.z;
                        float an = MathF.Sqrt(ax * ax + ay * ay + az * az);
                        if (an < 1e-3f) continue;
                        float dot = (ax * d.x + ay * d.y + az * d.z) / an;
                        float ang = MathF.Acos(Math.Clamp(dot, -1f, 1f));
                        if (ang >= cone) continue;
                        cand[n] = tid;
                        pang[n] = ang;
                        n++;
                    }
                    if (n > 0)
                    {
                        Array.Sort(pang, cand, 0, n);
                        for (int j = 0; j < n; j++)
                        {
                            int c = cand[j];
                            if (!e.is_alive(c)) continue;
                            if (Sight.clear(world, origin, _sight_point(pos.get3(c)), LOS_STEP))
                            {
                                best = c;
                                break;
                            }
                        }
                    }
                }
                if (best >= 0)
                {
                    pl.lock_target = best;
                    pl.lock_progress = 0f;
                }
                else
                {
                    pl.lock_progress = MathF.Max(0f, pl.lock_progress - dt * 2f);
                    continue;
                }
            }

            pl.lock_progress = MathF.Min(1f, pl.lock_progress + dt / MathF.Max(wd.lock_time, 0.05f));
        }
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
