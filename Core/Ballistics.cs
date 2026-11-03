// Fire control: find what the muzzle is pointing at, and how high to hold.
//
// The sight point is the first thing the ray from the muzzle runs into - the
// ground, a building tile, a tree trunk or a unit's hit sphere - out to
// `Defs.ZERO_RANGE`.
// The solver then returns the launch elevation that puts the round on that
// point, mirroring the integrator in `Systems/Projectiles.cs` (1/30 s
// semi-implicit Euler, same gravity and, for rockets, the same thrust curve).
//
// Only direct fire is zeroed: bullets and unguided rockets.  SACLOS and the
// infrared missile steer themselves and keep the plain aim.

namespace Mech.Server;

public static class Ballistics
{
    public const float DT = 1f / 30f;    // same step as the projectile integrator
    public const float MARCH = 8f;       // sight-ray resolution, metres (half a 16 m tile)
    public const float MIN_DIST = 25f;   // closer than this the sight point is the mech itself
    public const int ITERS = 4;

    /// <summary>Nearest hull the ray meets, solved against the tick's hit-sphere
    /// table.
    ///
    /// `world.aim_spheres()` is the table the projectile sweep already built:
    /// every hull's spheres, in id order, where they stood when this tick
    /// started - which is where they still stand for a system that runs before
    /// `movement`.  Asking for it instead of rebuilding the geometry per shot is
    /// most of what taking a shot used to cost: it was one `sphere_world` per
    /// candidate hull, plus a list of tuples built for it.
    ///
    /// Returns the hit point and its distance along the ray, or `(null,
    /// max_dist)` when nothing is in the way.</summary>
    public static (Vec3? hit, float t) _unit_hit(World world, Vec3 origin, Vec3 direction,
                                                 float max_dist, int ignore, int faction = -1)
    {
        var (sph, row_ok) = world.aim_spheres();
        int n = sph.n;
        if (n == 0) return (null, max_dist);
        var centres = sph.centres;
        var radii = sph.radii;
        var ids = sph.ids;
        var facs = sph.factions;
        float reach = max_dist + 12f;
        // one pass instead of numpy's masks: the broad phase, the row filter and
        // the quadratic are independent per row, and only the smallest `t` among
        // the rows that survive all of them can be the answer (`argmin`)
        float best = float.PositiveInfinity;
        bool found = false;
        for (int k = 0; k < n; k++)
        {
            if (row_ok is not null && !row_ok[k]) continue;   // not a hull, or not where it says
            if (ids[k] == ignore) continue;
            if (faction >= 0 && facs[k] == faction) continue; // own rounds pass through their own side
            float rx = centres[k * 3 + 0] - origin.x;
            float ry = centres[k * 3 + 1] - origin.y;
            float rz = centres[k * 3 + 2] - origin.z;
            if (rx * rx + rz * rz >= reach * reach) continue;
            float r = radii[k];
            float b = rx * direction.x + ry * direction.y + rz * direction.z;
            float disc = b * b - (rx * rx + ry * ry + rz * rz - r * r);
            if (!(disc >= 0f)) continue;
            float t = -b - MathF.Sqrt(MathF.Max(disc, 0f));
            if (!(t >= 0f) || !(t < max_dist)) continue;
            if (t < best)
            {
                best = t;
                found = true;
            }
        }
        if (!found) return (null, max_dist);
        return (origin + direction * best, best);
    }

    /// <summary>First contact of the muzzle ray, or null if it reaches `max_dist`.</summary>
    public static Vec3? sight_point(World world, Vec3 origin, Vec3 direction, float max_dist,
                                    int ignore = -1, int faction = -1)
    {
        var (unit_pt, limit) = _unit_hit(world, origin, direction, max_dist, ignore, faction);
        var terrain = world.terrain;
        float t0 = 0f;
        while (t0 < limit)
        {
            float t1 = MathF.Min(limit, t0 + MARCH);
            float x = origin.x + direction.x * t1;
            float y = origin.y + direction.y * t1;
            float z = origin.z + direction.z * t1;
            if (!terrain.inside(x, z))
            {
                // the ray left the map: off the field there is nothing to zero on,
                // so the shot just follows the aim line (the round expires in the
                // air or drops away on its own)
                return null;
            }
            // a tree trunk is a metre wide, far thinner than MARCH, so it is solved
            // over the whole step rather than probed at its end: zeroing past a
            // trunk the round is going to burst in would make every shot at a tree
            // fall short of the crosshair
            bool trunk = terrain.trunk_hit(origin + direction * t0, new Vec3(x, y, z),
                                           out float seg_t, out Vec3 seg_pt);
            float? trunk_t = trunk ? t0 + seg_t * (t1 - t0) : null;
            if (y <= terrain.height_at(x, z) || terrain.blocked(x, z))
            {
                float a = t0, b = t1;
                for (int it = 0; it < 6; it++)
                {
                    float m = 0.5f * (a + b);
                    float mx = origin.x + direction.x * m;
                    float my = origin.y + direction.y * m;
                    float mz = origin.z + direction.z * m;
                    if (my > terrain.height_at(mx, mz) && !terrain.blocked(mx, mz)) a = m;
                    else b = m;
                }
                float t = 0.5f * (a + b);
                if (trunk_t is not null && trunk_t.Value < t) return origin + direction * trunk_t.Value;
                return origin + direction * t;
            }
            if (trunk_t is not null) return seg_pt;
            t0 = t1;
        }
        return unit_pt;
    }

    /// <summary>Height of the round once it has covered `dist` metres horizontally.
    ///
    /// Null when the round expires (or never gets that far) first.</summary>
    public static float? _altitude_at(Vec3 origin, float yaw, float pitch, WeaponDef wd,
                                      float dist)
    {
        var d = MathUtil.dir_from(yaw, pitch);
        float vx = d.x * wd.vel, vy = d.y * wd.vel, vz = d.z * wd.vel;
        float x = origin.x, y = origin.y, z = origin.z;
        float g = wd.g;
        float thrust = wd.thrust;
        float maxvel = wd.maxvel;
        float life = wd.life;
        float prev_h = 0f;
        float prev_y = y;
        float t = 0f;
        while (t < life)
        {
            if (thrust > 0f)
            {
                float speed = MathF.Sqrt(vx * vx + vy * vy + vz * vz);
                float k = MathF.Min(maxvel, speed + thrust * DT) / MathF.Max(speed, 1e-6f);
                vx *= k;
                vy *= k;
                vz *= k;
            }
            vy -= g * DT;
            x += vx * DT;
            y += vy * DT;
            z += vz * DT;
            t += DT;
            float h = MathF.Sqrt((x - origin.x) * (x - origin.x) + (z - origin.z) * (z - origin.z));
            if (h >= dist)
            {
                float span = h - prev_h;
                float f = span > 1e-6f ? (dist - prev_h) / span : 1f;
                return prev_y + (y - prev_y) * f;
            }
            prev_h = h;
            prev_y = y;
        }
        return null;
    }

    /// <summary>Launch elevation that drops the round on `target`, or null.</summary>
    public static float? solve_elevation(Vec3 origin, Vec3 target, WeaponDef wd, float yaw,
                                         float pitch)
    {
        float dx = target.x - origin.x, dz = target.z - origin.z;
        float d = MathF.Sqrt(dx * dx + dz * dz);
        if (d < MIN_DIST) return null;
        float h = target.y - origin.y;
        float g = wd.g;
        float vel = wd.vel;
        if (g > 0f && vel > 0f && wd.thrust == 0f)
        {
            // direct shot: closed form, low arc; half a step of Euler error over a
            // 3 km flight is under a metre, so no refinement is needed.  Anything
            // past the round's own reach cannot be zeroed for at all
            if (d > vel * wd.life) return null;
            float v2 = vel * vel;
            float disc = v2 * v2 - g * (g * d * d + 2f * h * v2);
            if (disc < 0f) return null;
            return MathF.Atan2(v2 - MathF.Sqrt(disc), g * d);
        }
        // thrusting rocket: no closed form, and the altitude at range is a steep
        // function of the elevation, so walk it in with a secant instead of Newton
        // `_altitude_at` gives an absolute height, so the walk compares it against
        // the target's absolute height: `h` is the drop below the muzzle and only
        // serves the closed form above.  Mixing the two put the round one muzzle
        // height low, which for a slow launch is hundreds of metres short
        float t0 = pitch, t1 = pitch + 0.02f;
        float? y0 = _altitude_at(origin, yaw, t0, wd, d);
        if (y0 is null) return null;
        for (int it = 0; it < ITERS + 2; it++)
        {
            float? y1 = _altitude_at(origin, yaw, t1, wd, d);
            if (y1 is null) return null;
            float err = target.y - y1.Value;
            if (MathF.Abs(err) < 0.1f) return t1;
            float dy = y1.Value - y0.Value;
            if (MathF.Abs(dy) < 1e-6f) return null;
            float t2 = MathF.Max(-1.2f, MathF.Min(Defs.ZERO_MAX_ELEV, t1 + err * (t1 - t0) / dy));
            t0 = t1;
            y0 = y1;
            t1 = t2;
        }
        return MathF.Abs(t1) <= Defs.ZERO_MAX_ELEV ? t1 : null;
    }

    /// <summary>Elevation for a shot zeroed onto whatever the crosshair sits on.
    ///
    /// Only the AI still fires this way: solving against the sight point makes the
    /// reticle dance whenever a hull crosses a ridge, so a pilot's gun holds a
    /// fixed sight setting instead (`zero_at`).</summary>
    public static float? zero_pitch(World world, Vec3 origin, float yaw, float pitch,
                                    WeaponDef wd, int ignore = -1, int faction = -1)
    {
        var sp = sight_point(world, origin, MathUtil.dir_from(yaw, pitch), Defs.ZERO_RANGE,
                             ignore, faction);
        if (sp is null) return null;
        return solve_elevation(origin, sp.Value, wd, yaw, pitch);
    }

    /// <summary>Elevation that puts the round on the sight line `distance` metres out.
    ///
    /// The aim point is a fixed way down the crosshair rather than the first thing
    /// the ray hits, so the solution depends on the pilot's setting and the hull's
    /// attitude only - never on whether a ridge, a tree or a hull happens to be in
    /// the way.  That is what stops the reticle jumping at a crest line.</summary>
    public static float? zero_at(Vec3 origin, float yaw, float pitch, WeaponDef wd,
                                 float distance)
    {
        var d = MathUtil.dir_from(yaw, pitch);
        var target = new Vec3(origin.x + d.x * distance,
                              origin.y + d.y * distance,
                              origin.z + d.z * distance);
        return solve_elevation(origin, target, wd, yaw, pitch);
    }
}
