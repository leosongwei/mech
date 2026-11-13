// The triangle navmesh: the ground a chassis can cross, and the cheapest way
// across it.
//
// The terrain is two flat triangles per 16 m tile (`Terrain.height_at`), so the
// navmesh does not have to be built: the facets *are* the terrain.  What a region
// stores is the two facts the planning needs about each of them - its own slope
// and whether a hull can be there at all - and the search is A* over facets with
// three neighbours each (the other triangle of its own tile, and the two tiles
// across its straight edges).
//
// The one rule that makes this work is that *the planner and the driver agree*.
// `NavWalk.clear` is the same test `Drive`'s obstacle fan and `Integrate`'s step
// make - the same `blocked` tile types, the same trunks, the same climb limit
// out of `Defs.max_gradient` - so a facet A* crossed is a facet the machine can
// actually drive over.  Before this file the raiders planned on a grid that called
// a wood passable at a cost and a base solid, while the driver refused the first
// and allowed the second: a route through a stand of trees, and a column that
// pushed at it for the rest of the battle.
//
// A window is what keeps it small: a region covers the ground between two points
// plus `PAD`, so a 1 km plan is ~4 k tiles / 8 k facets of bytes, and nothing here
// grows with the map.

namespace Mech.Server.Systems.AI;

/// <summary>What a chassis is, for planning: how steep a face it will climb and how
/// wide it is.  The numbers come from `Defs` - the same ones movement refuses a
/// step on, which is the whole point.</summary>
public readonly struct NavProfile
{
    public readonly float max_grad;
    public readonly float radius;

    public NavProfile(float max_grad, float radius)
    {
        this.max_grad = max_grad;
        this.radius = radius;
    }

    /// <summary>A hull's own profile, off its def (`Defs.max_gradient`).</summary>
    public static NavProfile of(int etype)
    {
        var d = Defs.UNIT_DEFS[etype];
        return new NavProfile(d.max_grad, d.radius * 0.6f);
    }
}

/// <summary>The straight line a hull would drive, tested the way the driver tests
/// one step of it.</summary>
public static class NavWalk
{
    /// <summary>m; the step the segment is judged at - a third of a tile, so no
    /// facet is stepped over and a trunk is caught by the same sampling `Drive`
    /// uses.</summary>
    public const float STEP = 6f;

    /// <summary>Whether a hull with this profile can drive from `a` to `b` over the
    /// ground as it is: no wall, no trunk, and no climb past its own limit.</summary>
    public static bool clear(Terrain t, in NavProfile p, float ax, float az, float bx, float bz)
    {
        float dx = bx - ax, dz = bz - az;
        float dist = MathF.Sqrt(dx * dx + dz * dz);
        if (dist < 1e-3f) return true;
        int n = Math.Max(1, (int)MathF.Ceiling(dist / STEP));
        float px = ax, pz = az;
        float ph = t.height_at(ax, az);
        for (int k = 1; k <= n; k++)
        {
            float u = (float)k / n;
            float x = ax + dx * u, z = az + dz * u;
            if (t.blocked(x, z)) return false;
            if (t.trunk_blocked(x, z, p.radius)) return false;
            float h = t.height_at(x, z);
            // uphill only, exactly as `Integrate.too_steep` has it: a hull can
            // always come back off a face it is standing on
            float run = dist / n;
            if (h - ph > p.max_grad * run + 0.02f) return false;
            px = x;
            pz = z;
            ph = h;
        }
        return true;
    }
}

/// <summary>A window of the terrain, as facets: their slopes, and whether a hull
/// may stand in them.</summary>
public sealed class NavRegion
{
    public readonly int i0, j0, nx, nz;     // window in tiles
    public readonly float x0, z0;           // its south-west corner, metres
    private readonly byte[] _grad;          // nx*nz*2, rise over run * 100
    private readonly bool[] _open;          // nx*nz*2, no wall in the tile

    /// <summary>Tiles the window's origin and size are rounded to.  A hull asking
    /// from ten metres further along the lane gets the *same* region, which is what
    /// makes the cache in `Paths` hit: measured, building a 1 km window per request
    /// was 0.28 ms of every tick across a battle.</summary>
    public const int SNAP = 64;

    /// <summary>The window two points need, without building it: the cache in
    /// `Paths` asks for this first, so a hull that asks twice from the same
    /// kilometre never pays for the second one.</summary>
    public static void window(Terrain t, float ax, float az, float bx, float bz, float pad,
                              out int i0, out int j0, out int nx, out int nz)
    {
        float lo_x = MathF.Max(0f, MathF.Min(ax, bx) - pad);
        float lo_z = MathF.Max(0f, MathF.Min(az, bz) - pad);
        float hi_x = MathF.Min((t.w - 1) * Terrain.TILE, MathF.Max(ax, bx) + pad + Terrain.TILE);
        float hi_z = MathF.Min((t.d - 1) * Terrain.TILE, MathF.Max(az, bz) + pad + Terrain.TILE);
        i0 = Math.Max(0, (int)(lo_x / Terrain.TILE) / SNAP * SNAP);
        j0 = Math.Max(0, (int)(lo_z / Terrain.TILE) / SNAP * SNAP);
        int span_i = (int)MathF.Ceiling(hi_x / Terrain.TILE) - i0 + 1;
        int span_j = (int)MathF.Ceiling(hi_z / Terrain.TILE) - j0 + 1;
        nx = Math.Min(t.w - 1 - i0, Math.Max(SNAP, (span_i + SNAP - 1) / SNAP * SNAP));
        nz = Math.Min(t.d - 1 - j0, Math.Max(SNAP, (span_j + SNAP - 1) / SNAP * SNAP));
    }

    public NavRegion(Terrain t, float ax, float az, float bx, float bz, float pad = 320f)
    {
        window(t, ax, az, bx, bz, pad, out i0, out j0, out nx, out nz);
        x0 = i0 * Terrain.TILE;
        z0 = j0 * Terrain.TILE;
        _grad = new byte[nx * nz * 2];
        _open = new bool[nx * nz * 2];
        for (int j = 0; j < nz; j++)
        {
            int tj = Math.Min(t.d - 2, j0 + j);
            for (int i = 0; i < nx; i++)
            {
                int ti = Math.Min(t.w - 2, i0 + i);
                int k = tj * t.w + ti;
                float a = t.h[k], b = t.h[k + 1], c = t.h[k + t.w], d = t.h[k + t.w + 1];
                int n = (j * nx + i) * 2;
                _grad[n] = q(MathF.Sqrt(Sq(b - a) + Sq(c - a)));
                _grad[n + 1] = q(MathF.Sqrt(Sq(c - d) + Sq(b - d)));
                bool wall = !t.blocked((ti + 0.33f) * Terrain.TILE, (tj + 0.33f) * Terrain.TILE);
                _open[n] = wall && !t.blocked((ti + 0.66f) * Terrain.TILE, (tj + 0.66f) * Terrain.TILE);
                _open[n + 1] = _open[n];
            }
        }
    }

    private static float Sq(float v) => v * v;

    private static byte q(float grad)
    {
        float v = grad / Terrain.TILE * 100f;      // height difference -> rise over run
        return (byte)Math.Clamp((int)MathF.Round(v), 0, 255);
    }

    public int count => nx * nz * 2;

    /// <summary>Whether a hull with this profile may stand in facet `n`: open ground
    /// and a face inside its climb limit.</summary>
    public bool can(int n, in NavProfile p) => _open[n] && _grad[n] * 0.01f <= p.max_grad;

    public float grad(int n) => _grad[n] * 0.01f;

    /// <summary>The facet nearest a point, or -1 when the point is outside the
    /// window.  The point's own tile decides which of its two triangles it is in,
    /// by the same diagonal `height_at` uses.</summary>
    public int node(float x, float z)
    {
        int i = (int)(x / Terrain.TILE) - i0;
        int j = (int)(z / Terrain.TILE) - j0;
        if (i < 0 || j < 0 || i >= nx || j >= nz) return -1;
        float tx = x / Terrain.TILE - (i0 + i);
        float tz = z / Terrain.TILE - (j0 + j);
        return (j * nx + i) * 2 + (tx + tz <= 1f ? 0 : 1);
    }

    public (float x, float z) at(int n)
    {
        int c = n >> 1;
        int i = i0 + c % nx, j = j0 + c / nx;
        bool upper = (n & 1) != 0;
        // the centroid of the triangle: `a, b, c` is (1/3, 1/3) and `d, c, b` is
        // (2/3, 2/3) of the tile
        return ((i + (upper ? 0.66f : 0.33f)) * Terrain.TILE,
                (j + (upper ? 0.66f : 0.33f)) * Terrain.TILE);
    }

    /// <summary>The three neighbours of facet `n`: the other triangle of its own
    /// tile, and the one across each of its two straight edges.</summary>
    public void neighbours(int n, Span<int> into, out int count)
    {
        count = 0;
        int c = n >> 1;
        int i = c % nx, j = c / nx;
        bool upper = (n & 1) != 0;
        into[count++] = n ^ 1;                              // the other half
        if (upper)
        {
            // {d, c, b}: its straight edges are x = i+1 (east) and z = j+1 (south)
            if (i + 1 < nx) into[count++] = ((j * nx) + i + 1) * 2;
            if (j + 1 < nz) into[count++] = ((j + 1) * nx + i) * 2 + 1;
        }
        else
        {
            // {a, b, c}: x = i (west) and z = j (north)
            if (i > 0) into[count++] = ((j * nx) + i - 1) * 2 + 1;
            if (j > 0) into[count++] = ((j - 1) * nx + i) * 2;
        }
    }
}

/// <summary>One A* over a region, advanced a slice at a time.
///
/// A plan is a few hundred expansions of a few thousand facets and the AI block
/// runs inside the tick, so the search is a state machine: `NavPaths` gives each
/// live query its share of a per-tick budget, and a path is ready on a later tick
/// instead of costing 15 ms in one.</summary>
public sealed class NavQuery
{
    private readonly NavRegion _r;
    private readonly NavProfile _p;
    private readonly int _start, _goal;
    private readonly float _gx, _gz;
    private readonly float[] _gs;
    private readonly int[] _from;
    private readonly byte[] _seen;
    private readonly PriorityQueue<int, float> _open = new();

    public readonly List<(float x, float z)> path = new();
    public bool done;
    public int expansions;

    public NavQuery(NavRegion r, in NavProfile p, float sx, float sz, float gx, float gz)
    {
        _r = r;
        _p = p;
        _gx = gx;
        _gz = gz;
        _start = r.node(sx, sz);
        _goal = r.node(gx, gz);
        int n = r.count;
        _gs = new float[n];
        _from = new int[n];
        _seen = new byte[n];
        if (_start < 0 || _goal < 0) { done = true; return; }
        Array.Fill(_gs, float.PositiveInfinity);
        _gs[_start] = 0f;
        _from[_start] = -1;
        _seen[_start] = 1;
        _open.Enqueue(_start, _cost(_start));
    }

    /// <summary>m; the heuristic: the straight distance to the goal, which the
    /// facet graph's edge lengths never overestimate.</summary>
    private float _h(int n)
    {
        var (x, z) = _r.at(n);
        return MathF.Sqrt((_gx - x) * (_gx - x) + (_gz - z) * (_gz - z));
    }

    private float _cost(int n) => _gs[n] + _h(n);

    /// <summary>Advance the search by at most `budget` expansions.  Answers true when
    /// it has finished, whether or not there is a path.</summary>
    public bool step(int budget)
    {
        if (done) return true;
        Span<int> nb = stackalloc int[3];
        for (int k = 0; k < budget; k++)
        {
            if (!_open.TryDequeue(out int n, out _))
            {
                done = true;
                return true;
            }
            if (_seen[n] == 2) continue;             // a stale entry
            _seen[n] = 2;
            expansions++;
            if (n == _goal)
            {
                _trace();
                done = true;
                return true;
            }
            _r.neighbours(n, nb, out int cnt);
            for (int e = 0; e < cnt; e++)
            {
                int m = nb[e];
                if (!_r.can(m, _p) && m != _goal) continue;
                // the step is centroid to centroid, which is what makes the
                // straight-line heuristic consistent with the edge costs (a cheaper
                // arbitrary step - half the shared edge, say - leaves the heuristic
                // overestimating and the search re-expands what it has already
                // settled: measured, 2.5x the expansions per plan)
                var (mx, mz) = _r.at(m);
                var (nx2, nz2) = _r.at(n);
                float step = MathF.Sqrt((mx - nx2) * (mx - nx2) + (mz - nz2) * (mz - nz2));
                float g = _gs[n] + step * (1f + SLOPE_COST * _r.grad(m) * _r.grad(m));
                if (g >= _gs[m]) continue;
                _gs[m] = g;
                _from[m] = n;
                _seen[m] = 1;
                _open.Enqueue(m, g + _h(m));
            }
        }
        return false;
    }

    /// <summary>How much a slope is worth: a face at the limit costs `1 + 2 * g^2`
    /// of a flat one, so the search takes the flat way round when it is not much
    /// longer, and the mountain pass when it is the only way.</summary>
    public const float SLOPE_COST = 2.0f;

    private void _trace()
    {
        var rev = new List<(float x, float z)>();
        for (int n = _goal; n >= 0; n = _from[n])
        {
            rev.Add(_r.at(n));
            if (n == _start) break;
        }
        rev.Reverse();
        path.AddRange(rev);
    }
}
