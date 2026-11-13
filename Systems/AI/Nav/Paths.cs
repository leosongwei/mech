// Where a hull that has run out of road asks for another one.
//
// The AI says *where* (`nav_route` / `nav_x`), the driver says *how*
// (`Systems/Movement/`), and between them is the case this file exists for: the
// machine has been pushing at something for two seconds and the way point it is
// aiming at is on the wrong side of a hill or a wall.  Before this, only a raiding
// party noticed that; a lane hull and a rifleman pressed into the obstacle for the
// rest of the battle, which the ground no longer hides - the relief is 4x steeper
// than it was.
//
// So: every crew hull is watched (a commanded hull whose real speed is a quarter
// of its command is pushing at something), and after `STUCK_SKIP` it asks for a
// path.  The request is a `NavQuery` over the facets of a window around it,
// advanced a slice per tick; when it finishes, the answer is spliced in front of
// the way points the hull already had and registered as its route, so the driver
// does not learn a single new thing.
//
// The strategic picture is still the raider's (`Raid.FieldNav`, 128 m) - this is
// the layer under it, and the only layer that knows what a facet's slope is.

namespace Mech.Server.Systems.AI;

public sealed class NavColumns : CrewColumns
{
    public float[] cmd = null!;
    public float[] leg_yaw = null!;
    public int[] path_i = null!;

    internal static readonly new string[] names =
        Components.Cat(CrewColumns.names, "vel_cmd", "leg_yaw", "path_i");

    public static readonly new string[] OnCrew = Components.Cat(names, AiTags.CREW);

    public NavColumns(Group g) : base(g)
    {
        cmd = g.f("vel_cmd");
        leg_yaw = g.f("leg_yaw");
        path_i = g.i("path_i");
    }

    public override string[] Names => names;
}

public static class Paths
{
    /// <summary>Facet expansions one request may make per tick.  A 1 km detour
    /// expands 2-4 k facets, so it lands two or three ticks later - and a hull is
    /// mid-swing for that long, which nobody sees.</summary>
    public const int NODES_PER_TICK = 1500;
    /// <summary>s of pushing at something before a hull asks for another way.</summary>
    public const float STUCK_SKIP = 2.0f;
    /// <summary>s before the same hull asks again, so a wall it cannot get round
    /// does not cost a plan every two seconds for the rest of the battle.</summary>
    public const float RETRY = 6.0f;
    /// <summary>s the same, for a man on foot.  Infantry are the army (150 of 200
    /// hulls) and a rifleman in a wood asks far more often than a hull does - the
    /// trigger fires at `RETRY` and it is the retry that costs, not the walk.
    /// Measured on a battle: 3.4 k detours in four minutes, 95 % of them infantry.</summary>
    public const float RETRY_FOOT = 15.0f;
    /// <summary>m; a detour longer than this is a different journey, and the
    /// strategic layer's business.</summary>
    public const float MAX_DETOUR = 700f;
    /// <summary>rad; a heading this far off the bearing to the way point is a hull
    /// that is turning, and its distance to that point says nothing yet.</summary>
    public const float TURN_SLACK = 1.0f;
    /// <summary>Fraction of the commanded speed that still counts as driving.</summary>
    public const float STUCK_RATE = 0.25f;
    /// <summary>m/s of slack: pulling away from a standstill is not a wall.</summary>
    public const float STUCK_FAST = 8f;
    /// <summary>Way points a spliced route keeps from the old one.  A lane route is
    /// 300-odd points long and the tail is the rest of the march, so this is a
    /// sanity bound and not a trim: capping it at 24 cut every detour down to the
    /// next 1.4 km of lane, and a hull that took one simply stopped there
    /// (measured: 1470 m net in four minutes on two of three lanes).</summary>
    public const int KEEP = 4096;

    private sealed class Request
    {
        public int eid;
        public int row;
        public Archetype arch = null!;
        public NavQuery query = null!;
        public float gx, gz;
    }

    // the nav's own clocks: seconds a hull has spent pushing, and seconds before it
    // may ask again.  Private to this system - they are not on the wire and no
    // other system reads them, which is what separates them from `blocked_t` (the
    // machine's) and `raid_stuck` (a mission's).
    private static readonly Dictionary<int, float> _stuck = new();     // best distance so far
    private static readonly Dictionary<int, float> _clock = new();     // seconds without progress
    private static readonly Dictionary<int, (float x, float z)> _goal_mem = new();
    private static readonly Dictionary<int, float> _retry = new();
    private static readonly List<Request> _live = new();

    /// <summary>How many detours have been asked for, and how many came back with
    /// a route: the two numbers `--check nav` reads to say whether the layer is
    /// doing anything at all.</summary>
    public static int asked, delivered;
    /// <summary>Who asked, by type name: the one number that says whether the trigger
    /// is watching a hull that is driving or one that is manoeuvring badly.</summary>
    public static readonly Dictionary<string, int> asked_by = new();

    /// <summary>Requests waiting for a free slot this tick.</summary>
    private static readonly Queue<Request> _queue = new();

    public static void run(World world, float dt)
    {
        _age(dt);
        _watch(world, dt);
        _advance(world);
    }

    private static void _age(float dt)
    {
        if (_retry.Count == 0) return;
        var keys = new List<int>(_retry.Keys);
        foreach (int k in keys)
        {
            float v = _retry[k] - dt;
            if (v <= 0f) _retry.Remove(k);
            else _retry[k] = v;
        }
    }

    /// <summary>Watch every crew hull for one that is not getting anywhere, and
    /// queue a detour when it has had enough.
    ///
    /// The measure is *progress toward the way point*, not speed: a wheeled hull
    /// pulls away at 2 m/s^2 and a mech turning in place is slow for a moment too,
    /// so "going slower than commanded" blames a machine for its own gearbox -
    /// measured, it fired a detour at every APC leaving a standstill.  A hull that
    /// has not closed a metre and a half on its way point in `STUCK_SKIP` seconds
    /// while it is commanded is pushing at something.
    /// </summary>
    private static void _watch(World world, float dt)
    {
        foreach (var g in world.ecs.groups(NavColumns.OnCrew))
        {
            var c = new NavColumns(g);
            for (int i = 0; i < c.pos.Length / 3; i++)
            {
                int eid = c.ids[i];
                if (c.nav_speed[i] < 1f)
                {
                    _stuck.Remove(eid);
                    _clock.Remove(eid);
                    _goal_mem.Remove(eid);
                    continue;
                }
                float px = c.pos[i * 3], pz = c.pos[i * 3 + 2];
                (float gx, float gz) = _goal(world, c, i, px, pz);
                // a *different* way point restarts the clock: a lane drops one every
                // 55 m, and "the distance to this one stopped shrinking" says nothing
                // when it was just replaced by the next (measured: 3714 detours in
                // four minutes for three hulls before this was remembered)
                if (!_goal_mem.TryGetValue(eid, out var mem) ||
                    MathF.Abs(mem.x - gx) > 5f || MathF.Abs(mem.z - gz) > 5f)
                {
                    _goal_mem[eid] = (gx, gz);
                    _stuck[eid] = MathF.Sqrt((gx - px) * (gx - px) + (gz - pz) * (gz - pz));
                    _clock.Remove(eid);
                    continue;
                }
                float d = MathF.Sqrt((gx - px) * (gx - px) + (gz - pz) * (gz - pz));
                // a hull coming round onto a new bearing is manoeuvring, not pushing:
                // its way point is behind it for as long as the turn lasts, and a
                // wheeled hull needs 11 m of radius to take it (measured: without
                // this test every APC leaving a standstill asked for a detour)
                float bearing = MathF.Atan2(gz - pz, gx - px);
                if (MathF.Abs(MathUtil.wrap_pi(bearing - c.leg_yaw[i])) > TURN_SLACK)
                {
                    _clock.Remove(eid);
                    _stuck[eid] = d;
                    continue;
                }
                if (!_stuck.TryGetValue(eid, out float best)) best = d;
                if (d < best - 1.5f)
                {
                    _stuck[eid] = d;
                    continue;
                }
                // the clock is the time since the distance last improved
                float t = _clock.TryGetValue(eid, out float c0) ? c0 + dt : dt;
                _clock[eid] = t;
                if (t < STUCK_SKIP || _retry.ContainsKey(eid)) continue;
                _clock.Remove(eid);
                _retry[eid] = c.etype[i] == Defs.ETYPE_MECH || c.etype[i] == Defs.ETYPE_APC
                    ? RETRY : RETRY_FOOT;
                _ask(world, g.arch, c, i, eid);
            }
        }
    }

    /// <summary>Queue one detour from a hull to the way point it is aiming at.</summary>
    private static void _ask(World world, Archetype g, NavColumns c, int i, int eid)
    {
        float px = c.pos[i * 3], pz = c.pos[i * 3 + 2];
        (float gx, float gz) = _goal(world, c, i, px, pz);
        float dx = gx - px, dz = gz - pz;
        float d = MathF.Sqrt(dx * dx + dz * dz);
        if (d < 40f) return;                      // it is next to what it wants
        if (d > MAX_DETOUR)
        {
            float k = MAX_DETOUR / d;
            gx = px + dx * k;
            gz = pz + dz * k;
        }
        var profile = NavProfile.of(c.etype[i]);
        var region = _region(world.terrain, px, pz, gx, gz);
        var q = new NavQuery(region, profile, px, pz, gx, gz);
        asked++;
        string tn = Defs.UNIT_DEFS[c.etype[i]].name;
        asked_by[tn] = asked_by.TryGetValue(tn, out int nn) ? nn + 1 : 1;
        _queue.Enqueue(new Request { eid = eid, row = i, arch = g, query = q, gx = gx, gz = gz });
    }

    /// <summary>A window, out of a small cache: a hull that asks twice from the same
    /// kilometre must not pay for the second one.</summary>
    private static readonly Dictionary<(int, int, int, int), NavRegion> _regions = new();
    private static readonly Queue<(int, int, int, int)> _region_order = new();
    private const int REGIONS_KEPT = 16;

    private static NavRegion _region(Terrain t, float ax, float az, float bx, float bz)
    {
        NavRegion.window(t, ax, az, bx, bz, 320f, out int i0, out int j0, out int nx, out int nz);
        var key = (i0, j0, nx, nz);
        if (_regions.TryGetValue(key, out var hit)) return hit;
        var region = new NavRegion(t, ax, az, bx, bz);
        _regions[key] = region;
        _region_order.Enqueue(key);
        while (_region_order.Count > REGIONS_KEPT)
            _regions.Remove(_region_order.Dequeue());
        return region;
    }

    /// <summary>The point this hull is driving at: the route's next way point, or a
    /// bare point.</summary>
    private static (float, float) _goal(World world, NavColumns c, int i, float px, float pz)
    {
        var route = Nav.get(world, c.nav_route[i]);
        if (route is null) return (c.nav_x[i], c.nav_z[i]);
        var (ax, az) = Nav.at(route, c.path_i[i]);
        float dx = ax - px, dz = az - pz;
        return MathF.Sqrt(dx * dx + dz * dz) < 1e-3f ? (c.nav_x[i], c.nav_z[i]) : (ax, az);
    }

    /// <summary>Step the live requests, each with its share of the tick, and start
    /// queued ones while there is room.</summary>
    private static void _advance(World world)
    {
        int budget = NODES_PER_TICK;
        for (int k = _live.Count - 1; k >= 0; k--)
        {
            var r = _live[k];
            int share = Math.Max(200, budget / Math.Max(1, _live.Count));
            if (r.query.step(share))
            {
                budget -= Math.Min(share, r.query.expansions);
                _live.RemoveAt(k);
                if (r.query.path.Count > 0) _apply(world, r);
            }
            else
            {
                budget -= share;
            }
        }
        while (_queue.Count > 0 && _live.Count < 3)
            _live.Add(_queue.Dequeue());
    }

    /// <summary>Turn the facet path into way points: straighten it against the same
    /// walkability test the driver uses, then splice it in front of the route the
    /// hull already had.</summary>
    private static void _apply(World world, Request r)
    {
        var c = r.arch;
        int i = r.row;
        float px = c.f("pos")[i * 3], pz = c.f("pos")[i * 3 + 2];
        var profile = NavProfile.of(c.u8("etype")[i]);
        var pts = straighten(world.terrain, profile, r.query.path, px, pz, r.gx, r.gz);
        if (pts.Count < 2) return;

        // the way points that were already queued: a detour is a bend in the road,
        // and the road continues after it.  A hull that was steering at a bare point
        // (`nav_route` -1) has no list to continue, so the point goes on the end of
        // the detour - without it the hull stops at the last detour way point and
        // never arrives anywhere, which is what the range measured before this.
        var old = Nav.get(world, c.i("nav_route")[i]);
        if (old is not null)
        {
            int from = c.i("path_i")[i];
            for (int k = from; k < old.pts.Count && k < from + KEEP; k++)
                pts.Add(old.pts[k]);
        }
        else
        {
            var (lx, lz) = pts[^1];
            if (MathF.Sqrt(Sq(lx - c.f("nav_x")[i]) + Sq(lz - c.f("nav_z")[i])) > 20f)
                pts.Add((c.f("nav_x")[i], c.f("nav_z")[i]));
        }
        int route = Nav.add(world, pts);
        delivered++;
        c.i("nav_route")[i] = route;
        c.i("path_i")[i] = 0;
    }

    /// <summary>Shorten a facet path to the fewest way points that still cross the
    /// same ground: each leg is tested with `NavWalk.clear`, so a bend is kept only
    /// where the ground really forces it.</summary>
    public static List<(float x, float z)> straighten(Terrain t, in NavProfile p,
        List<(float x, float z)> raw, float sx, float sz, float gx, float gz)
    {
        var outv = new List<(float x, float z)> { (sx, sz) };
        int i = 0;
        while (i < raw.Count - 1)
        {
            int best = i + 1;
            for (int k = raw.Count - 1; k > i; k--)
            {
                var (ax, az) = outv[^1];
                if (NavWalk.clear(t, p, ax, az, raw[k].x, raw[k].z)) { best = k; break; }
            }
            outv.Add(raw[best]);
            i = best;
        }
        var (lx, lz) = outv[^1];
        if (MathF.Sqrt((gx - lx) * (gx - lx) + (gz - lz) * (gz - lz)) > 1f)
            outv.Add((gx, gz));
        // a wheeled hull cannot corner at 16 m intervals (`APC_TURN_R` is 11 m of
        // radius): way points closer together than twice that are one turn, so the
        // route is stretched where it is straight and left alone where it bends
        if (p.max_grad <= Defs.APC_MAX_GRADIENT) outv = merge(outv, Defs.APC_TURN_R * 2f);
        return outv;
    }

    /// <summary>A path planned to the end in one call: `straighten` over a search
    /// that is allowed to finish.  This is the raider's door - a party plans once
    /// every couple of minutes, off the tick, and takes the whole answer rather
    /// than a slice of one.</summary>
    public static List<(float x, float z)> plan(Terrain t, in NavProfile p,
        float sx, float sz, float gx, float gz, int budget = 40000)
        => plan(t, p, sx, sz, gx, gz, out _, out _, budget);

    public static List<(float x, float z)> plan(Terrain t, in NavProfile p,
        float sx, float sz, float gx, float gz, out int facets, out int expansions,
        int budget = 40000)
    {
        var region = new NavRegion(t, sx, sz, gx, gz);
        var q = new NavQuery(region, p, sx, sz, gx, gz);
        while (!q.step(budget)) { }
        facets = q.path.Count;
        expansions = q.expansions;
        return q.path.Count == 0
            ? new List<(float x, float z)>()
            : straighten(t, p, q.path, sx, sz, gx, gz);
    }

    private static float Sq(float v) => v * v;

    /// <summary>Drop a way point that the next one already covers - the leg through
    /// it is straight and clear, so following it changes nothing.</summary>
    private static List<(float x, float z)> merge(List<(float x, float z)> pts, float min_leg)
    {
        if (pts.Count < 3) return pts;
        var outv = new List<(float x, float z)> { pts[0] };
        for (int k = 1; k + 1 < pts.Count; k++)
        {
            var (lx, lz) = outv[^1];
            float d = MathF.Sqrt((pts[k].x - lx) * (pts[k].x - lx) + (pts[k].z - lz) * (pts[k].z - lz));
            if (d >= min_leg) outv.Add(pts[k]);
        }
        outv.Add(pts[^1]);
        return outv;
    }
}
