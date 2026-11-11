// Routes, and the way point maths every driver shares.
//
// A route is a poly line in world space - the lane a side marches down, the path
// a raiding party planned, the way out of a town a hull got boxed into - and
// `World.routes` is the table they live in.  A crew names one by index in
// `nav_route` (`Systems/AI/Support/Tags.cs`); the driver that follows it keeps its
// own cursor in `path_i`.  Nothing here knows who is following or why: this file
// is the geometry and the table, and it sits beside `Grid.cs` and `Ballistics.cs`
// as a library the systems call.
//
// The corner figures below were the AI's, and they were never AI: a hull with an
// 11 m turning radius cannot hold 100 km/h round a right angle, so `corner_speed`
// is the machine's envelope.  It lives here so the vehicle driver applies it and
// the crew never has to know the number.

namespace Mech.Server;

/// <summary>One poly line: the points, and the slot it occupies in
/// `World.routes` (`used` false = free for the next registration).</summary>
public sealed class Route
{
    public List<(float x, float z)> pts = new();
    public bool used;
}

public static class Nav
{
    /// <summary>Register a poly line and answer its index.  A freed slot is
    /// reused first, so a battle that launches a party every two minutes does not
    /// grow the table for ever.</summary>
    public static int add(World world, IEnumerable<(float x, float z)> pts)
    {
        var list = world.routes;
        int id = -1;
        for (int k = 0; k < list.Count; k++)
        {
            if (list[k].used) continue;
            id = k;
            break;
        }
        if (id < 0)
        {
            id = list.Count;
            list.Add(new Route());
        }
        var r = list[id];
        r.pts.Clear();
        r.pts.AddRange(pts);
        r.used = true;
        return id;
    }

    public static void free(World world, int id)
    {
        if (id >= 0 && id < world.routes.Count) world.routes[id].used = false;
    }

    public static Route? get(World world, int id) =>
        id >= 0 && id < world.routes.Count && world.routes[id].used ? world.routes[id] : null;

    /// <summary>The point to drive at, clamped: a cursor past the end is the end.</summary>
    public static (float x, float z) at(Route r, int i)
    {
        if (r.pts.Count == 0) return (0f, 0f);
        int k = i < 0 ? 0 : Math.Min(i, r.pts.Count - 1);
        return r.pts[k];
    }

    /// <summary>Length of leg `k` (`k + 1` has to exist).</summary>
    public static float seg(Route r, int k)
    {
        var (ax, az) = r.pts[k];
        var (bx, bz) = r.pts[k + 1];
        return MathUtil.dist2d(ax, az, bx, bz);
    }

    /// <summary>True when way point `i` is behind the hull of a given acceptance
    /// radius: inside it, or past the line through it square to the leg arriving
    /// from the previous point.
    ///
    /// The first is what lets a car carry speed into a corner and cut across the
    /// inside of it; the second is what keeps a car that cut *wide* of a corner -
    /// or was shoved off the line - from turning back to collect it.</summary>
    public static bool passed(Route r, int i, float px, float pz, float radius)
    {
        if (r.pts.Count == 0) return true;
        int k = i < 0 ? 0 : Math.Min(i, r.pts.Count - 1);
        var (wx, wz) = r.pts[k];
        float dx = px - wx, dz = pz - wz;
        if (dx * dx + dz * dz <= radius * radius) return true;
        if (k <= 0) return false;
        var (lx0, lz0) = r.pts[k - 1];
        float lx = wx - lx0, lz = wz - lz0;
        return (lx * lx + lz * lz) > 1e-9f && (dx * lx + dz * lz) > 0f;
    }

    /// <summary>Advance the cursor past every way point the hull is done with.</summary>
    public static int advance(Route r, int i, float px, float pz, float radius)
    {
        int n = r.pts.Count;
        int k = i < 0 ? 0 : Math.Min(i, n);
        while (k < n && passed(r, k, px, pz, radius)) k += 1;
        return k;
    }

    /// <summary>How close a way point has to come to count as reached, for this
    /// speed.
    ///
    /// The radius grows with the speed: a hull at 100 km/h needs ~30 m of arc to
    /// turn at all, so aiming at a point 10 m away only makes it overshoot and
    /// come back, while a hull crawling through a tight corner can and should
    /// follow the line.  `radius = clamp(speed * time, floor, cap)` is the
    /// lookahead rule of a pure-pursuit tracker.</summary>
    public static float arrive_radius(float speed)
    {
        float r = MathF.Abs(speed) * Defs.APC_ARRIVE_LOOK;
        return MathF.Max(Defs.APC_ARRIVE_MIN, MathF.Min(Defs.APC_ARRIVE_MAX, r));
    }

    /// <summary>The fastest a hull can take a corner that turns by `ang` radians.
    ///
    /// How much room the legs give is irrelevant - a kink is a kink however long
    /// the straight either side of it - so the corner is defined by how far the
    /// hull is allowed to cut inside it (`Defs.APC_CUT`, metres).  A fillet of
    /// radius R misses the vertex by `R * (1 / cos(ang / 2) - 1)`, so the cut
    /// budget caps R and the hull's own turning radius floors it; the bend is then
    /// driven at `sqrt(APC_LAT_ACCEL * R)`.  A right angle comes out at ~11 m/s
    /// cutting 8 m off the inside, and a 20 degree kink costs nothing.</summary>
    public static float corner_speed(float ang, float top)
    {
        if (ang < 1e-3f) return top;
        float over = 1f / MathF.Cos(0.5f * MathF.Min(ang, MathF.PI * 0.999f)) - 1f;
        float radius = over > 1e-6f ? Defs.APC_CUT / over : 1e9f;
        radius = MathF.Max(Defs.APC_TURN_R, radius);
        return MathF.Min(top, MathF.Sqrt(Defs.APC_LAT_ACCEL * radius));
    }

    /// <summary>How fast the hull may be rolling now and still make the corners
    /// ahead: every corner inside `APC_CORNER_HORIZON` sets a ceiling that rises
    /// with the distance to it (`v^2 = v_corner^2 + 2 a d`).</summary>
    public static float route_speed(Route r, int i, float px, float pz, float top)
    {
        if (r.pts.Count < 2) return top;
        float limit = top;
        // start at the way point *before* the one being aimed at: the acceptance
        // radius drops a corner as soon as the hull is within ~12 m of it, and the
        // braking for it has to have happened well before that
        int k = Math.Max(1, i - 1);
        var (kx, kz) = r.pts[Math.Min(k, r.pts.Count - 1)];
        float d = MathUtil.dist2d(px, pz, kx, kz);   // metres to the corner itself
        while (k + 1 < r.pts.Count && d < Defs.APC_CORNER_HORIZON)
        {
            var (ax, az) = r.pts[k - 1];
            var (bx, bz) = r.pts[k];
            var (cx, cz) = r.pts[k + 1];
            float ang = MathF.Abs(MathUtil.wrap_pi(MathF.Atan2(cz - bz, cx - bx) -
                                                   MathF.Atan2(bz - az, bx - ax)));
            float vb = corner_speed(ang, top);
            if (vb < top)
                limit = MathF.Min(limit, MathF.Sqrt(vb * vb + 2f * Defs.APC_BRAKE * d));
            k += 1;
            d += seg(r, k - 1);
        }
        return limit;
    }
}
