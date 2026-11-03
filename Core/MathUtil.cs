// Geometry / ballistics helpers shared by the weapon systems.
//
// Ported one for one from `backend/server/mathutil.py`.  The numpy array forms
// there (`wrap_pi_arr`, `sphere_world` over many hulls) are loops here; the
// batched hitsphere build keeps the same "one hull's spheres come out
// contiguously, in the order of `local`" contract, because the collision sweep
// keeps its rows aligned with its unit ids.

namespace Mech.Server;

public static class MathUtil
{
    public static readonly float TAU = MathF.PI * 2f;

    /// <summary>Yaw / pitch -> a unit direction.  yaw 0 is +x, a positive yaw
    /// turns towards +z, a positive pitch climbs.</summary>
    public static Vec3 dir_from(float yaw, float pitch)
    {
        float cp = MathF.Cos(pitch);
        return new Vec3(cp * MathF.Cos(yaw), MathF.Sin(pitch), cp * MathF.Sin(yaw));
    }

    public static float yaw_of(float dx, float dz) => MathF.Atan2(dz, dx);

    /// <summary>Yaw / pitch that point along the given vector.</summary>
    public static (float yaw, float pitch) angles_to(float dx, float dy, float dz)
    {
        float horiz = MathF.Sqrt(dx * dx + dz * dz);
        return (MathF.Atan2(dz, dx), MathF.Atan2(dy, horiz));
    }

    public static float wrap_pi(float a)
    {
        while (a > MathF.PI) a -= TAU;
        while (a < -MathF.PI) a += TAU;
        return a;
    }

    public static float turn_toward(float cur, float target, float max_step)
    {
        float d = wrap_pi(target - cur);
        if (MathF.Abs(d) <= max_step) return target;
        return wrap_pi(cur + MathF.CopySign(max_step, d));
    }

    /// <summary>World position of the commander's sight head: the pilot's eye.
    ///
    /// The sight is bolted to the *hull*, `Defs.MECH_SIGHT_Z` behind the hull's
    /// own rotation axis and `Defs.MECH_EYE` up, so the eye is not a point in
    /// space - it is carried round by the hull.  Swinging the hull underneath a
    /// sight already pointing somewhere else walks the eye along an arc, and the
    /// head's own rotation does not move it: it turns about the pillar it stands
    /// on, and the eye is that pillar.</summary>
    public static Vec3 sight_eye(Vec3 pos, float hull_yaw)
    {
        float c = MathF.Cos(hull_yaw), s = MathF.Sin(hull_yaw);
        float oz = Defs.MECH_SIGHT_Z;
        return new Vec3(pos.x + c * oz, pos.y + Defs.MECH_EYE, pos.z + s * oz);
    }

    /// <summary>World position of the barrel tip of the given mech arm.
    ///
    /// The tip sits `reach` metres out along the barrel (default: the arm's
    /// front face), and the barrel swings with `arm_pitch`, exactly like the
    /// client builds the models - so the round leaves the mouth it was drawn on.
    /// `right` and `up` move the mouth off the arm's centre line in the arm's
    /// own frame: a rocket pod is a lattice of tubes and each round leaves its
    /// own, so the offset is taken along the *pitched* arm.</summary>
    public static Vec3 muzzle_point(Vec3 pos, float yaw, int module,
                                    float arm_pitch = 0f, float? reach = null,
                                    float right = 0f, float up = 0f)
    {
        // [right, up, forward] of the arm's mouth; the default is the mech's
        var off = Defs.MECH_ARM_MUZZLE.TryGetValue(module, out var o) ? o : null;
        float rx = off is null ? 0f : off[0];
        float ry = off is null ? 4.3f : off[1];
        float rz = off is null ? 0.9f : off[2];
        float fwd = reach ?? rz;
        float c = MathF.Cos(yaw), s = MathF.Sin(yaw);
        // local +z forward, +x right  -> world
        float fx = c, fz = s;
        float rxw = -s, rzw = c;
        float cp = MathF.Cos(arm_pitch), sp = MathF.Sin(arm_pitch);
        float outd = fwd * cp - up * sp;      // how far out along the ground track
        float high = fwd * sp + up * cp;      // how far up the bore line has climbed
        return new Vec3(pos.x + rxw * (rx + right) + fx * outd,
                        pos.y + ry + high,
                        pos.z + rzw * (rx + right) + fz * outd);
    }

    /// <summary>One unit type's hit geometry, built once: local offsets in the
    /// hull's own frame (`right, up, forward`), the radii, and which module each
    /// sphere is.  The projectile sweep asks for it once a tick.</summary>
    public sealed class SphereGeom
    {
        public Vec3[] local = Array.Empty<Vec3>();
        public float[] radius = Array.Empty<float>();
        public int[] module = Array.Empty<int>();
    }

    private static readonly Dictionary<int, SphereGeom> _GEOM_CACHE = new();

    private static readonly Vec3[] _DEFAULT_SPHERES = { new(0f, 1f, 0f) };
    private static readonly float[] _DEFAULT_RADIUS = { 1.5f };
    private static readonly int[] _DEFAULT_MODULE = { 0 };

    public static SphereGeom sphere_geom(int etype)
    {
        if (_GEOM_CACHE.TryGetValue(etype, out var g)) return g;
        var d = Defs.UNIT_DEFS.TryGetValue(etype, out var u) ? u : null;
        var rows = d?.spheres;
        if (rows == null || rows.Length == 0)
        {
            g = new SphereGeom
            {
                local = _DEFAULT_SPHERES,
                radius = _DEFAULT_RADIUS,
                module = _DEFAULT_MODULE,
            };
        }
        else
        {
            int n = rows.Length;
            g = new SphereGeom
            {
                local = new Vec3[n],
                radius = new float[n],
                module = new int[n],
            };
            for (int k = 0; k < n; k++)
            {
                var row = rows[k];          // module, right, up, forward, radius
                g.module[k] = (int)row[0];
                g.local[k] = new Vec3(row[1], row[2], row[3]);
                g.radius[k] = row[4];
            }
        }
        _GEOM_CACHE[etype] = g;
        return g;
    }

    /// <summary>One hull's local sphere offsets -> world centres, in the order of
    /// `local`, written to `out` at `count` triples starting at element 0.
    ///
    /// The rotation is the hull's own: +x right, +z forward, exactly what the
    /// client's `compose` builds a model with.</summary>
    public static void sphere_world(SphereGeom geom, Vec3 pos, float yaw, float[] outv)
    {
        float c = MathF.Cos(yaw), s = MathF.Sin(yaw);
        var local = geom.local;
        for (int k = 0; k < local.Length; k++)
        {
            float right = local[k].x, up = local[k].y, forward = local[k].z;
            outv[k * 3] = pos.x - s * right + c * forward;
            outv[k * 3 + 1] = pos.y + up;
            outv[k * 3 + 2] = pos.z + c * right + s * forward;
        }
    }

    /// <summary>The centre of one sphere of one hull (the scalar door: an aim
    /// cast asks about one hull, the sweep asks about all of them).</summary>
    public static Vec3 sphere_world(SphereGeom geom, int k, Vec3 pos, float yaw)
    {
        float c = MathF.Cos(yaw), s = MathF.Sin(yaw);
        var l = geom.local[k];
        return new Vec3(pos.x - s * l.x + c * l.z,
                        pos.y + l.y,
                        pos.z + c * l.x + s * l.z);
    }

    /// <summary>Iteratively solve the intercept point for a ballistic projectile.
    ///
    /// All three components of the target's velocity are honoured: a hull drives
    /// on the flat (its `vel[1]` is 0, so nothing changes for one), a missile in
    /// the air climbs or dives and *that* is the component a gunner has to lead.
    /// `gravity` is the drop of *our* round over the flight time, not the
    /// target's - the fire control zeroes the barrel for it at shot time.</summary>
    public static Vec3 solve_lead(Vec3 origin, Vec3 tpos, Vec3? tvel,
                                  float speed, float gravity, int iters = 3)
    {
        float ox = origin.x, oy = origin.y, oz = origin.z;
        float tx = tpos.x, ty = tpos.y, tz = tpos.z;
        float vx = 0f, vy = 0f, vz = 0f;
        if (tvel.HasValue)
        {
            vx = tvel.Value.x; vy = tvel.Value.y; vz = tvel.Value.z;
        }
        float sp = MathF.Max(speed, 1f);
        float t = dist(origin, tpos) / sp;
        float ax = tx, ay = ty, az = tz;
        for (int i = 0; i < iters; i++)
        {
            ax = tx + vx * t;
            az = tz + vz * t;
            ay = ty + vy * t + 0.5f * gravity * t * t;
            float d = MathF.Sqrt((ox - ax) * (ox - ax) + (oy - ay) * (oy - ay)
                                 + (oz - az) * (oz - az));
            t = d / sp;
        }
        return new Vec3(ax, ay, az);
    }

    /// <summary>The speed a boost-and-coast round averages over `dist` metres.
    ///
    /// A gun leaves at its muzzle speed and keeps it; a missile leaves at 40 m/s,
    /// accelerates at `thrust` up to `maxvel` for the length of its `burn` and
    /// then coasts.  Leading one with the launch speed puts the aim point
    /// seconds in front of a running hull, and leading it with the cruise speed
    /// puts it behind, so the gunnery solves against what the round will
    /// actually average (a beam rider at 1 km: ~340 m/s, not the 40 it left the
    /// tube at).</summary>
    public static float mean_speed(WeaponDef wd, float dist)
    {
        float v0 = wd.vel;
        float a = wd.thrust;
        float vmax = wd.maxvel != 0f ? wd.maxvel : v0;
        float burn = wd.burn;
        if (v0 <= 0f || a <= 0f || burn <= 0f || vmax <= v0) return v0;
        float tb = MathF.Min(burn, (vmax - v0) / a);
        float db = v0 * tb + 0.5f * a * tb * tb;      // metres covered under power
        if (dist <= db)
        {
            float t = (-v0 + MathF.Sqrt(MathF.Max(v0 * v0 + 2f * a * dist, 0f))) / a;
            return dist / MathF.Max(t, 1e-3f);
        }
        return dist / MathF.Max(tb + (dist - db) / vmax, 1e-3f);
    }

    public static float ballistic_drop(float gravity, float flight) =>
        0.5f * gravity * flight * flight;

    public static float weapon_range(int wid) => Defs.WEAPONS[wid].range;

    // -- small shared helpers (the numpy code called these inline) ---------
    public static float dist(Vec3 a, Vec3 b)
    {
        float dx = a.x - b.x, dy = a.y - b.y, dz = a.z - b.z;
        return MathF.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    public static float dist2d(float ax, float az, float bx, float bz)
    {
        float dx = ax - bx, dz = az - bz;
        return MathF.Sqrt(dx * dx + dz * dz);
    }

    public static float clamp(float v, float lo, float hi) => v < lo ? lo : v > hi ? hi : v;
    public static int clamp(int v, int lo, int hi) => v < lo ? lo : v > hi ? hi : v;
}
