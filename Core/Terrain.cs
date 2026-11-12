// Tile terrain: continuous heightfield, tile map, lanes and towns.
//
// The heightfield stores *vertex* heights (metres, a continuous value) on a
// `w x d` grid whose vertices sit at `(i*TILE, j*TILE)`; the ground between four
// of them is the two flat triangles `height_at` solves, so what a round hits is
// the facet the client draws.  The tile map stores one byte per tile
// (`tiles[j*w + i]` covering `[i*TILE, (i+1)*TILE) square`).
//
// The map is 16000 x 16000 m made of 16 m tiles.  Both arrays are shipped to
// the browser (zlib compressed) so client side prediction - ballistic impact
// markers, reticles - matches the server exactly.
//
// This is a line-for-line port of `backend/server/terrain.py`: numpy's `(d, w)`
// arrays are flat row-major arrays indexed `j * w + i` (exactly numpy's layout),
// and every generator helper keeps its Python name and its Python rules.  The
// random draws themselves differ (see `Core/Rng.cs`), so the map a given seed
// produces is a different but equivalent one.
//
// What must stay identical is the *physics* and the *rules*: 16 m tiles with
// 1000x1000 of them, the triangle `height_at` samples, the road bit mask, the
// road corridor being flattened, `no_build` keeping houses one tile off a
// straight lane and two off a diagonal one, the town plaza, the house counts, and
// the one that other systems lean on - a tree tile blocks with its **trunk**
// (a cylinder, solved) and hides with its **canopy** (a volume, solved), never
// with a slab over the whole tile.

namespace Mech.Server;

/// <summary>One town: a depot on a lane, grown into a street network.</summary>
/// <remarks>
/// The generator writes the middle, the name and `neutral`; the battle mode later
/// writes `lane` / `lane_i` (which lane the town sits on, and where) and the two
/// reinforcement timers plus `contested` (`Modes/Battle/Waves.cs`).  `owner` is
/// `-1` while nobody holds it.
/// </remarks>
public sealed class Town
{
    public float x, z;              // middle of the town, world metres
    public int tile_x, tile_y;      // ... and the same in tile coordinates
    public int owner = -1;
    public float progress;          // capture timer, seconds
    public string name = "";
    public bool neutral;            // a spot from `TOWN_NEUTRAL`: opens unowned
    public int radius;              // tiles the streets were grown out to

    // written later, by the battle mode
    public int lane = -1;           // index into `world.lanes`, -1 = not assigned
    public int lane_i;              // position along that lane
    public float inf_t, apc_t;      // reinforcement timers
    public bool contested;
}

/// <summary>One tree trunk: the only part of a tree a round or a hull meets.</summary>
/// <remarks>
/// `r` / `h` are the cylinder, `q1` / `q3` are the hash draws the canopy is
/// built from (`_canopy`) - leaves hide, they never stop anything that flies.
/// </remarks>
public readonly struct Trunk
{
    public readonly float x, z, r, h, q1, q3;

    public Trunk(float x, float z, float r, float h, float q1, float q3)
    {
        this.x = x; this.z = z; this.r = r; this.h = h; this.q1 = q1; this.q3 = q3;
    }
}

/// <summary>The volume one tree hides behind: a ball or a cone.</summary>
/// <remarks>
/// A broadleaf (`q3 > 0.45` in the trunk's draws) is an ellipsoid ball sitting
/// on the trunk - `y0` is its centre, `y1` the vertical radius, `r` the
/// horizontal one (rx == rz).  A conifer is a cone standing on the trunk -
/// `y0` the base, `y1` the tip, `r` the skirt radius.  The client builds its
/// meshes from the same two shapes (`renderer._buildProps`).
/// </remarks>
public readonly struct Canopy
{
    public const byte ball = 0;
    public const byte cone = 1;

    public readonly byte kind;
    public readonly float x, z, y0, y1, r;

    public Canopy(byte kind, float x, float z, float y0, float y1, float r)
    {
        this.kind = kind; this.x = x; this.z = z;
        this.y0 = y0; this.y1 = y1; this.r = r;
    }
}

/// <summary>One APC post of the weapon range.</summary>
public readonly struct RangePost
{
    public readonly float x, z;
    public readonly string name;
    public readonly float patrol_a, patrol_b;   // 0, 0 = a static post

    public RangePost(float x, float z, string name, float patrol_a = 0f, float patrol_b = 0f)
    {
        this.x = x; this.z = z; this.name = name;
        this.patrol_a = patrol_a; this.patrol_b = patrol_b;
    }

    public bool patrol => patrol_a != 0f || patrol_b != 0f;
}

public sealed class Terrain
{
    // -- constants (the module-level ones of `terrain.py`) -----------------
    public const float TILE = 16f;
    public const int MAPW = 1000;
    public const int MAPH = 1000;

    public const byte TILE_GRASS = 0;
    public const byte TILE_ROAD = 1;
    public const byte TILE_BUILDING = 2;
    public const byte TILE_TREE = 3;
    public const byte TILE_ROCK = 4;
    public const byte TILE_BASE = 5;
    public const byte TILE_SOLID = 6;       // blocks movement, drawn as part of the base
    public const byte TILE_FOREST = 7;      // thick stand: several trees per tile

    public static readonly int[] BLOCKING =
        { TILE_BUILDING, TILE_TREE, TILE_ROCK, TILE_SOLID, TILE_FOREST };
    // ... except the two tree tiles, which block with their trunks only - see
    // `blocked` / `wall_at`.

    // A road tile is a set of pieces, the way OpenTTD composes one from
    // ROADBITS: each of the four cardinal bits is the half of the tile that
    // reaches that edge, so one bit is a straight, two adjacent bits an elbow,
    // three a T and four a crossroads.  OpenTTD has no diagonal road at all;
    // the two diagonal bits are ours - a 45 degree band that enters one edge in
    // the middle and leaves through the middle of the next, so a staircase of
    // them is one continuous diagonal road instead of the tile by tile stair
    // you would otherwise have to drive.
    public const int ROAD_N = 1;            // half piece out to the north edge (-z)
    public const int ROAD_E = 2;            // ... east  (+x)
    public const int ROAD_S = 4;            // ... south (+z)
    public const int ROAD_W = 8;            // ... west  (-x)
    public const int ROAD_D_WS = 16;        // west edge middle -> south edge middle (SW corner)
    public const int ROAD_D_NE = 32;        // north edge middle -> east edge middle (NE corner)
    public const int ROAD_DIAG = ROAD_D_WS | ROAD_D_NE;
    public const int ROAD_STRAIGHT = ROAD_N | ROAD_E | ROAD_S | ROAD_W;

    /// <summary>How hard a road tile outweighs its verges when the corridor is
    /// flattened: the asphalt stays level.</summary>
    public const float ROAD_WEIGHT = 100f;
    /// <summary>Wall height of a building tile the map did not size.</summary>
    public const float BUILDING_H = 8f;
    /// <summary>The tallest canopy on the map (a tree is 10..18 m tall): the
    /// bound the sight cull has to assume under a tree tile, not what actually
    /// blocks - see `_canopies`.</summary>
    public const float CANOPY_MAX = 18f;
    public const float INV_TILE = 1f / TILE;
    /// <summary>Trunk radius of a tree: the only thing a round or a hull
    /// actually meets in a tree tile (see `_trunks`).</summary>
    public const float TRUNK_R = 0.5f;
    /// <summary>One storey of a town building.</summary>
    public const float FLOOR_H = 4f;
    /// <summary>Coarse ray-cull block: 8 tiles = 128 m (see `_build_cull`).</summary>
    public const int CULL_TILES = 8;

    // command bases (tile coordinates)
    public static readonly Dictionary<int, (float x, float y)> BASE_TILES = new()
    {
        { 0, (188f, 188f) },
        { 1, (812f, 812f) },
    };

    // lane way points in tile coordinates (three classic lanes on a square
    // map).  A way point times TILE is a world position, so the half tile in
    // every coordinate keeps the traffic on the *centre* of a road tile - which
    // is where the tile pieces put the asphalt - and the diagonal lane on the
    // centre of its 45 degree band.
    public static readonly (float x, float y)[][] LANE_TILES =
    {
        new[] { (188.5f, 188.5f), (812.5f, 188.5f), (812.5f, 812.5f) },   // north lane
        new[] { (188.5f, 188.5f), (188.5f, 812.5f), (812.5f, 812.5f) },   // west lane
        new[] { (188.0f, 188.5f), (500.0f, 500.5f), (812.0f, 812.5f) },   // mid lane
    };

    // Every town sits on a lane: a depot only supplies while its lane is
    // unbroken back to its own base, so a town off the lanes would be a dead
    // capture point.  The table holds still under both mirrors the map has -
    // across the centre, `(x, y) -> (1000 - x, 1000 - y)`, which pairs a red town
    // with the blue one the same distance out of its base, and across the
    // `x = z` diagonal, which pairs the north lane with the west lane.  Both
    // sides therefore open on the same number of towns at the same ranges, and
    // every lane carries an even number of them: half is the near half of each.
    public static readonly (int x, int y)[] TOWN_SPOTS =
    {
        // north lane: the same four ranges out of each base
        (270, 188), (390, 188), (520, 188), (650, 188),
        (812, 730), (812, 610), (812, 480), (812, 350),
        // west lane: the same list read across the diagonal
        (188, 270), (188, 390), (188, 520), (188, 650),
        (730, 812), (610, 812), (480, 812), (350, 812),
        // mid lane: three a side, on mirrored ranges along the diagonal
        (260, 260), (350, 350), (440, 440),
        (560, 560), (650, 650), (740, 740),
    };

    // The depots that open neutral, on the one ground that is exactly as far from
    // either base: the far bend of the north lane, the far bend of the west lane
    // (9984 m each) and the middle of the mid lane - the map's own centre, where
    // the gap between the two mid-lane fronts would otherwise be 2.7 km of empty
    // field.  The two bends sit on the anti-diagonal, the centre is its own
    // mirror both ways, so no side starts holding one.
    public static readonly (int x, int y)[] TOWN_NEUTRAL =
    {
        (812, 188), (188, 812),
        (500, 500),
    };

    // Towns are *grown*, the way OpenTTD grows a city: a front runs a few tiles
    // of street, turns or dies and drops a branch.  Then the network is opened
    // up - side streets punched across the long blocks and every loose end
    // joined to another street - and a few dozen buildings are put up along it.
    // Each town draws its own block width, run lengths, growth budget and house
    // count, so no two read alike - and the arterial it sits on is never
    // touched: the lane is straight and it stays so.
    public static readonly (int lo, int hi) TOWN_RUNS = (85, 140);      // street runs per town
    public static readonly (int lo, int hi) TOWN_RADIUS = (10, 14);     // tiles grown out from the middle
    public static readonly (int lo, int hi) TOWN_BLOCK = (2, 4);        // tiles between parallel streets
    public static readonly (int lo, int hi) TOWN_HOUSES = (120, 200);   // buildings per town
    public const int TOWN_PLAZA = 5;    // tiles around the middle kept clear of buildings
    public const int TOWN_REACH = 9;    // how far a dead end looks for a street to join
    public const int TOWN_RUNG = 4;     // tiles between the side streets off a long street

    // the four cardinal directions, +x, +z, -x, -z, and the road bit of each
    private static readonly (int x, int y)[] _DIRS = { (1, 0), (0, 1), (-1, 0), (0, -1) };
    private static readonly int[] _BITS = { ROAD_E, ROAD_S, ROAD_W, ROAD_N };
    // where along a lane a town hangs its own streets off the arterial
    private static readonly int[] _LANE_HANGS = { -5, -1, 4, 8, -9, 12 };

    // ---------------------------------------------------------------------
    // the map itself: flat row-major arrays, `j * w + i` (numpy's layout)
    // ---------------------------------------------------------------------
    public float[] h;               // vertex heights, (d, w)
    public byte[] tiles;            // (d, w)
    public byte[] roads;            // road pieces per tile (ROAD_* bits)
    public byte[] walls;            // wall column of a building tile, metres
    public int w, d;
    public List<List<(float x, float z)>> lanes;    // world metres
    public List<Town> towns;
    public Dictionary<int, (float x, float z)> bases;   // faction -> metres

    // trunk layout of the tree tiles that get asked about, tile index ->
    // Trunk[] - built on demand, a few hundred entries
    private readonly Dictionary<int, Trunk[]> trunk_cache = new();
    // canopies reaching into a tile, tile index -> Canopy[] - a canopy is up to
    // ~8 m across, so a tree standing next door hangs over the tile and has to
    // be in it as well
    private readonly Dictionary<int, Canopy[]> canopy_cache = new();

    // coarse cull grids, one entry per CULL_TILES square block (see _build_cull)
    private float[] _cull_h = Array.Empty<float>();
    private float[] _cull_solid = Array.Empty<float>();
    private float[] _cull_sight = Array.Empty<float>();
    // the coarse ray-cull grid: `CULL_TILES` blocks of `CULL_TILES` tiles, and
    // the quantities every ray walker needs (`Systems/Sweep.cs` files its hit
    // spheres under the same blocks)
    public int _cull_d, _cull_w;
    public float _cull_inv;
    private float _wmax, _dmax;

    public Terrain(float[] heights, byte[] tiles,
                   List<List<(float x, float z)>> lanes, List<Town> towns,
                   Dictionary<int, (float x, float z)> bases,
                   byte[]? roads = null, byte[]? walls = null,
                   int w = 0, int d = 0)
    {
        h = heights;
        this.tiles = tiles;
        this.lanes = lanes;
        this.towns = towns;
        this.bases = bases;
        // both maps are square, so the shape can be read back off the buffer -
        // `w` / `d` are only needed for something that is not
        this.w = w > 0 ? w : (int)MathF.Sqrt(heights.Length);
        this.d = d > 0 ? d : heights.Length / this.w;
        // road pieces per tile and the wall column of every building tile in
        // metres, so the client draws exactly what stops a round instead of
        // guessing a height of its own
        this.roads = roads ?? new byte[this.d * this.w];
        this.walls = walls ?? new byte[this.d * this.w];
        _build_cull();
    }

    /// <summary>Max (ground + obstacle) per `CULL_TILES` square block.</summary>
    /// <remarks>
    /// `_march` samples every 8 m and each sample costs a bilinear height read
    /// plus a wall lookup.  A point that is higher than *everything* the block
    /// can hold cannot be stopped by it, so the sample is skipped - which is
    /// exact, never a rejection of a real hit: the grid is the maximum of the
    /// block, so a false "clear" is impossible and ballistics are unchanged.
    /// Two grids, because a round is stopped by trunks and walls while a line of
    /// sight is also cut by the canopies over a tree tile.
    /// </remarks>
    private void _build_cull()
    {
        int nb = CULL_TILES;
        // the vertex grid and the tile grid are the same shape, so one block
        // reduction serves both
        var solid = new float[d * w];
        var sight = new float[d * w];
        for (int j = 0; j < d; j++)
        {
            for (int i = 0; i < w; i++)
            {
                int k = j * w + i;
                byte t = tiles[k];
                if (t == TILE_BUILDING)
                {
                    float wv = walls[k];
                    float wall = wv > BUILDING_H ? wv : BUILDING_H;
                    solid[k] = wall;
                    sight[k] = wall;
                }
                else if (t == TILE_TREE || t == TILE_FOREST)
                {
                    solid[k] = 0f;              // a trunk is solved, not bounded
                    sight[k] = CANOPY_MAX;      // a canopy is 10..18 m up
                }
            }
        }

        // one block per `nb` windows, the sliding window's own remainder dropped
        // exactly the way a `[::nb]` slice drops it
        int cull_d = (d - nb) / nb + ((d - nb) % nb != 0 ? 1 : 0);
        int cull_w = (w - nb) / nb + ((w - nb) % nb != 0 ? 1 : 0);
        _cull_d = cull_d;
        _cull_w = cull_w;
        _cull_h = new float[cull_d * cull_w];
        _cull_solid = new float[cull_d * cull_w];
        _cull_sight = new float[cull_d * cull_w];
        for (int bj = 0; bj < cull_d; bj++)
        {
            for (int bi = 0; bi < cull_w; bi++)
            {
                // max over the block *and* the one row/column of vertices on
                // its far edge (a block of nb tiles spans nb+1 vertices, so a
                // 9x9 window).  Exact, not grown by a whole block: a dilated
                // grid would make the cull useless in hilly country, which is
                // where rays are longest.
                float mh = float.NegativeInfinity, ms = 0f, mg = 0f;
                int j0 = bj * nb, i0 = bi * nb;
                for (int j = j0; j <= j0 + nb; j++)
                {
                    for (int i = i0; i <= i0 + nb; i++)
                    {
                        int k = j * w + i;
                        if (h[k] > mh) mh = h[k];
                        if (solid[k] > ms) ms = solid[k];
                        if (sight[k] > mg) mg = sight[k];
                    }
                }
                int bk = bj * cull_w + bi;
                _cull_h[bk] = mh;
                _cull_solid[bk] = mh + ms;
                _cull_sight[bk] = mh + mg;
            }
        }
        _cull_inv = 1f / (TILE * nb);
        _wmax = w - 1.001f;
        _dmax = d - 1.001f;
    }

    /// <summary>Grow a stand of trees `half` tiles either side of (x, z).</summary>
    /// <remarks>
    /// The debug knob: a tree hides what stands behind it, so a tester has to
    /// be able to put one where he wants it, between his sights and a target.
    /// Only open ground turns into a stand - a building, a road tile's kerb or
    /// a base is left as it is.  The cull grid holds the canopy height of every
    /// block, so both grids are rebuilt with the map.
    ///
    /// Returns `(i0, j0, rows, cols, data)` - the rectangle it painted, so the
    /// client can paint the same tiles: the trunks it draws and the sight rule
    /// it applies come from this one bitmap.
    /// </remarks>
    public (int i0, int j0, int rows, int cols, byte[] data) plant(
        float x, float z, int half = 2, bool forest = true)
    {
        int i0 = Math.Max(0, (int)(x * INV_TILE) - half);
        int i1 = Math.Min(w - 1, (int)(x * INV_TILE) + half);
        int j0 = Math.Max(0, (int)(z * INV_TILE) - half);
        int j1 = Math.Min(d - 1, (int)(z * INV_TILE) + half);
        int rows = j1 - j0 + 1;
        int cols = i1 - i0 + 1;
        var patch = new byte[rows * cols];
        for (int j = 0; j < rows; j++)
        {
            for (int i = 0; i < cols; i++)
            {
                int k = (j0 + j) * w + (i0 + i);
                byte t = tiles[k];
                if (t != TILE_BUILDING && t != TILE_BASE && t != TILE_SOLID && t != TILE_ROCK)
                    t = forest ? TILE_FOREST : TILE_TREE;
                tiles[k] = t;
                patch[j * cols + i] = t;
            }
        }
        trunk_cache.Clear();
        canopy_cache.Clear();
        _build_cull();
        return (i0, j0, rows, cols, patch);
    }

    // `BLOCKING` as a lookup: `blocked` is asked a handful of times per hull
    // per tick, and a five entry scan there shows up
    private static readonly bool[] _BLOCKING_LUT = _blocking_lut();

    private static bool[] _blocking_lut()
    {
        var t = new bool[8];
        foreach (int v in BLOCKING) t[v] = true;
        return t;
    }

    // -- sampling ---------------------------------------------------------
    /// <summary>Bilinear height.</summary>
    /// <remarks>
    /// This is the hottest scalar in the server (every march sample, every hull,
    /// every sight line), so it reads the four corners straight off the buffer
    /// and solves the one triangle the point falls in - no helper call per corner.
    ///
    /// The rule is the one the client draws: the tile is split along the
    /// north-east to south-west diagonal, so `tx + tz <= 1` is the triangle
    /// {a=(i,j), b=(i+1,j), c=(i,j+1)} and the rest is {d=(i+1,j+1), c, b}.
    /// `renderer._buildTilePatch`'s index buffer and `_buildRoads`' `facet` are
    /// the same split, which is why a road vertex and a hull's footprint land on
    /// the ground that is drawn under them.
    /// </remarks>
    public float height_at(float x, float z)
    {
        float fx = Math.Clamp(x * INV_TILE, 0f, _wmax);
        float fz = Math.Clamp(z * INV_TILE, 0f, _dmax);
        int i = (int)fx;
        int j = (int)fz;
        float tx = fx - i;
        float tz = fz - j;
        int k = j * w + i;
        float a = h[k], b = h[k + 1], c = h[k + w], d = h[k + w + 1];
        if (tx + tz <= 1f) return a + (b - a) * tx + (c - a) * tz;
        return d + (c - d) * (1f - tx) + (b - d) * (1f - tz);
    }

    private Trunk[] _trunks(int i, int j)
    {
        int k = j * w + i;
        if (trunk_cache.TryGetValue(k, out var tr)) return tr;
        tr = _trunks(i, j, tiles[k]);
        trunk_cache[k] = tr;
        return tr;
    }

    /// <summary>Every canopy that reaches into tile `(i, j)`.</summary>
    /// <remarks>
    /// A tree hangs over its neighbours - the biggest is a ball some 8 m across
    /// around a trunk that stands up to 5 m off its tile's middle - so the
    /// leaves of the next tile over are part of what this one hides.  The 3x3
    /// block around the tile is exactly wide enough to hold every tree that can
    /// reach in, and a tile with no tree in that block holds nothing.
    /// </remarks>
    private Canopy[] _canopies(int i, int j)
    {
        int k = j * w + i;
        if (canopy_cache.TryGetValue(k, out var c)) return c;
        var outv = new List<Canopy>();
        bool near = false;
        for (int jj = j - 1; jj <= j + 1; jj++)
        {
            if (jj < 0 || jj >= d) continue;
            for (int ii = i - 1; ii <= i + 1; ii++)
            {
                if (ii < 0 || ii >= w) continue;
                byte t = tiles[jj * w + ii];
                if (t == TILE_TREE || t == TILE_FOREST) near = true;
            }
        }
        if (near)
        {
            float x0 = i * TILE;
            float z0 = j * TILE;
            for (int jj = j - 1; jj <= j + 1; jj++)
            {
                if (jj < 0 || jj >= d) continue;
                for (int ii = i - 1; ii <= i + 1; ii++)
                {
                    if (ii < 0 || ii >= w) continue;
                    foreach (var tr in _trunks(ii, jj))
                    {
                        var cx = _canopy(tr, height_at(tr.x, tr.z));
                        float rad = cx.r;
                        // the square of this tile against the canopy's footprint
                        float dx = cx.x < x0 ? x0 - cx.x
                                 : cx.x > x0 + TILE ? cx.x - x0 - TILE : 0f;
                        float dz = cx.z < z0 ? z0 - cx.z
                                 : cx.z > z0 + TILE ? cx.z - z0 - TILE : 0f;
                        if (dx * dx + dz * dz <= rad * rad) outv.Add(cx);
                    }
                }
            }
        }
        c = outv.ToArray();
        canopy_cache[k] = c;
        return c;
    }

    /// <summary>Where inside tile `(i, j)` the segment enters the leaves, or null.</summary>
    private float? _canopy_hit(Vec3 p0, float dx, float dy, float dz,
                               int i, int j, float ta, float tb)
    {
        byte t = tiles[j * w + i];
        if (t != TILE_TREE && t != TILE_FOREST) return null;
        float? best = null;
        foreach (var c in _canopies(i, j))
        {
            var v = _canopy_hit(c, p0, dx, dy, dz, ta, tb);
            if (v.HasValue && (!best.HasValue || v.Value < best.Value)) best = v;
        }
        return best;
    }

    /// <summary>Height of the obstacle standing on the ground at (x, z).</summary>
    /// <remarks>
    /// A building is a solid column up to its roof, a tree is nothing but its
    /// trunk - the canopy overhead stops no round; everything else is bare
    /// ground.  Off the field nothing flies on, which the caller reads as an
    /// immediate hit.
    /// </remarks>
    public float wall_at(float x, float z)
    {
        int i = x >= 0f ? (int)(x * INV_TILE) : -1;
        int j = z >= 0f ? (int)(z * INV_TILE) : -1;
        if (i < 0 || j < 0 || i >= w || j >= d) return 1e6f;
        byte t = tiles[j * w + i];
        if (t == TILE_BUILDING)
        {
            byte wv = walls[j * w + i];
            return wv != 0 ? wv : BUILDING_H;
        }
        if (t == TILE_TREE || t == TILE_FOREST)
        {
            foreach (var tr in _trunks(i, j))
                if ((x - tr.x) * (x - tr.x) + (z - tr.z) * (z - tr.z) <= tr.r * tr.r)
                    return tr.h;
        }
        return 0f;
    }

    /// <summary>The height a line of sight is cut at by the *tile* it stands on:
    /// a building up to its roof.</summary>
    /// <remarks>
    /// A tree is not in here: its leaves are a volume of their own
    /// (`_canopies`), a few metres across and 10-18 m up, and treating the whole
    /// 16 m tile as a 10 m slab hid hulls standing in the open beside a stand.
    /// `los` solves the canopies separately.
    /// </remarks>
    public float sight_wall(float x, float z)
    {
        int i = x >= 0f ? (int)(x * INV_TILE) : -1;
        int j = z >= 0f ? (int)(z * INV_TILE) : -1;
        if (i < 0 || j < 0 || i >= w || j >= d) return 1e6f;
        if (tiles[j * w + i] == TILE_BUILDING)
        {
            byte wv = walls[j * w + i];
            return wv != 0 ? wv : BUILDING_H;
        }
        return 0f;
    }

    /// <summary>The height a line of sight is cut at.</summary>
    public float sight_at(float x, float z) => height_at(x, z) + sight_wall(x, z);

    public int tile_type(float x, float z)
    {
        int i = x >= 0f ? (int)(x * INV_TILE) : -1;
        int j = z >= 0f ? (int)(z * INV_TILE) : -1;
        if (i < 0 || j < 0 || i >= w || j >= d) return TILE_ROCK;
        return tiles[j * w + i];
    }

    /// <summary>Whether a hull cannot stand at (x, z).  A tree blocks with its
    /// trunk only, so a hull drives under the canopy of a tree or a whole
    /// forest.</summary>
    public bool blocked(float x, float z)
    {
        int t = tile_type(x, z);
        if (t == TILE_TREE || t == TILE_FOREST)
        {
            int i = (int)(x * INV_TILE);
            int j = (int)(z * INV_TILE);
            foreach (var tr in _trunks(i, j))
                if ((x - tr.x) * (x - tr.x) + (z - tr.z) * (z - tr.z) <= tr.r * tr.r)
                    return true;
            return false;
        }
        return _BLOCKING_LUT[t];
    }

    /// <summary>Whether a hull of radius `rad` at (x, z) runs into a trunk.</summary>
    /// <remarks>
    /// A trunk is far thinner than the handful of points movement samples, so
    /// the tiles the hull overlaps are distance tested instead - otherwise a
    /// mech drives straight through the trees it should be weaving between.
    /// </remarks>
    public bool trunk_blocked(float x, float z, float rad)
    {
        int i0 = Math.Max((int)((x - rad) / TILE), 0);
        int i1 = Math.Min((int)((x + rad) / TILE), w - 1);
        int j0 = Math.Max((int)((z - rad) / TILE), 0);
        int j1 = Math.Min((int)((z + rad) / TILE), d - 1);
        for (int j = j0; j <= j1; j++)
        {
            for (int i = i0; i <= i1; i++)
            {
                byte t = tiles[j * w + i];
                if (t != TILE_TREE && t != TILE_FOREST) continue;
                foreach (var tr in _trunks(i, j))
                {
                    float rr = rad + tr.r;
                    if ((x - tr.x) * (x - tr.x) + (z - tr.z) * (z - tr.z) <= rr * rr)
                        return true;
                }
            }
        }
        return false;
    }

    /// <summary>False once a point leaves the map: off the field there is no
    /// ground to hit, only the edge height `height_at` keeps returning.</summary>
    public bool inside(float x, float z) =>
        0f <= x && x <= (w - 1) * TILE && 0f <= z && z <= (d - 1) * TILE;

    // -- ray casts --------------------------------------------------------
    /// <summary>Yield `(t_enter, t_exit, i, j)` for every tile the segment crosses.</summary>
    /// <remarks>
    /// The ground is sampled inside each of them, so the walk cannot step over a
    /// tile: a 12.7 mm round covers 26 m in one 30 Hz tick, more than a whole
    /// 16 m tile, and a fixed sampling spacing would let it fly straight through
    /// a building and burst behind it.
    ///
    /// `_march` inlines this same walk on purpose (it is the hot loop); this
    /// iterator is the readable copy, for the calls that are not hot.
    /// </remarks>
    private IEnumerable<(float ta, float tb, int i, int j)> _spans(Vec3 p0, float dx, float dz)
    {
        float x = p0.x, z = p0.z;
        int i = x >= 0f ? (int)(x * INV_TILE) : -1;
        int j = z >= 0f ? (int)(z * INV_TILE) : -1;
        int sx = dx > 0f ? 1 : -1;
        int sz = dz > 0f ? 1 : -1;
        float tx = dx != 0f ? ((i + (dx > 0f ? 1 : 0)) * TILE - x) / dx : 2f;
        float tz = dz != 0f ? ((j + (dz > 0f ? 1 : 0)) * TILE - z) / dz : 2f;
        float t = 0f;
        while (true)
        {
            float tn = MathF.Min(MathF.Min(tx, tz), 1f);
            yield return (t, tn, i, j);
            if (tn >= 1f) yield break;
            t = tn;
            if (tx <= tz)
            {
                i += sx;
                tx = ((i + (dx > 0f ? 1 : 0)) * TILE - x) / dx;
            }
            else
            {
                j += sz;
                tz = ((j + (dz > 0f ? 1 : 0)) * TILE - z) / dz;
            }
        }
    }

    /// <summary>Where inside tile `(i, j)` the segment enters a trunk, or null.</summary>
    /// <remarks>
    /// A trunk is about a metre wide, far thinner than the 8 m the ground walk
    /// samples at, so it is solved as a cylinder instead of sampled - which is
    /// what makes a round fired at a tree actually stop in it.
    /// </remarks>
    private float? _trunk_hit(Vec3 p0, float dx, float dy, float dz,
                              int i, int j, float ta, float tb)
    {
        byte t = tiles[j * w + i];
        if (t != TILE_TREE && t != TILE_FOREST) return null;
        float a = dx * dx + dz * dz;
        if (a <= 1e-9f) return null;
        float? best = null;
        foreach (var tr in _trunks(i, j))
        {
            float ex = p0.x - tr.x;
            float ez = p0.z - tr.z;
            float b = 2f * (ex * dx + ez * dz);
            float c = ex * ex + ez * ez - tr.r * tr.r;
            float disc = b * b - 4f * a * c;
            if (disc < 0f) continue;
            float sq = MathF.Sqrt(disc);
            for (int ri = 0; ri < 2; ri++)
            {
                float root = ri == 0 ? (-b - sq) / (2f * a) : (-b + sq) / (2f * a);
                if (ta <= root && root <= tb && 0f <= root && root <= 1f)
                {
                    if (p0.y + dy * root <= height_at(tr.x, tr.z) + tr.h)
                        if (!best.HasValue || root < best.Value) best = root;
                    break;
                }
            }
        }
        return best;
    }

    /// <summary>The ground of a whole tick's rounds at once.</summary>
    /// <remarks>
    /// The rounds of one tick are a hundred legs of 10-36 m, and the per-leg
    /// walk was one Python function call per sample for each of them.  Here the
    /// samples of every leg go through one pass - the coarse block cull, the
    /// bilinear height and the wall lookup - and only the legs that actually
    /// burst are walked again, six bisections each.
    ///
    /// Sampling is uniform along each leg at `step` (the same 9 m a round is
    /// walked at, `n + 1` samples including both ends) rather than one sample
    /// per tile: at that spacing a leg cannot cross a 16 m tile without a sample
    /// landing in it, except at a corner it only clips, which is the one case
    /// this is looser than `_march`.
    ///
    /// A leg whose samples touch a tree tile is reported in `walk` and left to
    /// the scalar walk: a trunk is a metre wide and is *solved*, not sampled.
    /// Both ends of the leg are sampled, so a leg that *starts* in a tree tile
    /// is caught too - the reason the start is sample zero and not one step in.
    ///
    /// `p0` / `p1` are flat `x, y, z` per leg.
    /// </remarks>
    public void ground_hits_many(float[] p0, float[] p1, int n, float step,
                                 bool[] hit, float[] t_hit)
    {
        for (int k = 0; k < n; k++)
        {
            hit[k] = false;
            t_hit[k] = 1f;
        }
        if (n == 0) return;

        float xmax = w * TILE, zmax = d * TILE;
        var dxyz = new float[n * 3];
        var dist = new float[n];
        for (int k = 0; k < n; k++)
        {
            for (int c = 0; c < 3; c++) dxyz[k * 3 + c] = p1[k * 3 + c] - p0[k * 3 + c];
            dist[k] = MathF.Sqrt(dxyz[k * 3] * dxyz[k * 3] +
                                 dxyz[k * 3 + 1] * dxyz[k * 3 + 1] +
                                 dxyz[k * 3 + 2] * dxyz[k * 3 + 2]);
        }

        // a leg that starts at or below the ground bursts where it starts, the
        // same early out the scalar walk takes before it walks anything
        var start_hit = new bool[n];
        for (int k = 0; k < n; k++)
        {
            float x0 = p0[k * 3], z0 = p0[k * 3 + 2];
            start_hit[k] = x0 < 0f || z0 < 0f || x0 >= xmax || z0 >= zmax
                           || p0[k * 3 + 1] <= height_at(x0, z0) + 1f;
            if (start_hit[k])
            {
                hit[k] = true;
                t_hit[k] = 0f;
            }
        }

        var legs_n = new int[n];
        int total = 0;
        for (int k = 0; k < n; k++)
        {
            int nn = (int)MathF.Ceiling(dist[k] / step);
            if (nn < 1) nn = 1;
            legs_n[k] = nn;
            total += nn + 1;                // both ends, the start included
        }

        // every sample of every leg, laid end to end (numpy's `np.repeat`)
        var leg = new int[total];
        var kk = new int[total];
        var ts = new float[total];
        var xs = new float[total];
        var zs = new float[total];
        var ys = new float[total];
        int p = 0;
        for (int k = 0; k < n; k++)
        {
            for (int s = 0; s <= legs_n[k]; s++)
            {
                float t = (float)s / legs_n[k];
                leg[p] = k;
                kk[p] = s;
                ts[p] = t;
                xs[p] = p0[k * 3] + dxyz[k * 3] * t;
                ys[p] = p0[k * 3 + 1] + dxyz[k * 3 + 1] * t;
                zs[p] = p0[k * 3 + 2] + dxyz[k * 3 + 2] * t;
                p++;
            }
        }

        float cull_inv = _cull_inv;
        int cdi = _cull_d, cwi = _cull_w;
        var clear = new bool[total];
        for (int s = 0; s < total; s++)
        {
            int ci = (int)(xs[s] * cull_inv);
            int cj = (int)(zs[s] * cull_inv);
            bool inb = ci >= 0 && cj >= 0 && ci < cwi && cj < cdi;
            clear[s] = inb && ys[s] > _cull_solid[cj * cwi + ci];
        }
        // off the field nothing flies on: a sample outside the map is solid, so a
        // round fired from beyond the edge bursts where it starts instead of
        // coasting over clamped terrain
        var blocked = new bool[total];
        for (int s = 0; s < total; s++)
            blocked[s] = !clear[s]
                         && (xs[s] < 0f || zs[s] < 0f || xs[s] >= xmax || zs[s] >= zmax
                             || ys[s] <= height_at(xs[s], zs[s]));

        int seen_leg = -1;
        for (int s = 0; s < total; s++)
        {
            if (!blocked[s]) continue;
            int L = leg[s];
            if (L == seen_leg) continue;      // the first blocked sample of a leg
            seen_leg = L;
            if (start_hit[L]) continue;
            // the bracket is the previous sample *of this leg*: sample 0 of a
            // leg starts the bisection at the leg's own origin
            float lo = kk[s] > 0 ? ts[s - 1] : 0f;
            float hi = ts[s];
            float a = lo, b = hi;
            float lx = p0[L * 3], ly = p0[L * 3 + 1], lz = p0[L * 3 + 2];
            float dx = dxyz[L * 3], dy = dxyz[L * 3 + 1], dz = dxyz[L * 3 + 2];
            for (int it = 0; it < 6; it++)
            {
                float m = (a + b) * 0.5f;
                if (ly + dy * m > height_at(lx + dx * m, lz + dz * m)) a = m;
                else b = m;
            }
            t_hit[L] = (a + b) * 0.5f;
            hit[L] = true;
        }
    }

    /// <summary>Walk the segment and report the first point at or below `at`.</summary>
    /// <remarks>
    /// `canopies` adds the leaf volumes that hide things - they are the one
    /// shape a round flies through and an eye does not.
    ///
    /// This is the hottest loop in the server (87 % of its calls are a 36 m
    /// round's leg, the rest are 1-3 km sight lines) so the tile walk lives
    /// here instead of in an iterator, and the sample height is read in one call
    /// that keeps the tile it last looked at - half the samples of an 8 m step
    /// land in the tile the previous one did.  `sample` repeats the
    /// "ground + the wall standing on it" sum inline for the walk and the
    /// bisection, so the numbers are the same ones the callable would return.
    ///
    /// Never vectorise this and never turn the tile walk into an enumerator: the
    /// samples are cheap, the machinery around them is not.
    /// </remarks>
    private (bool hit, float t, Vec3 point) _march(
        Vec3 p0, Vec3 p1, float step, bool canopies = false, float[]? cull = null)
    {
        float dx = p1.x - p0.x;
        float dy = p1.y - p0.y;
        float dz = p1.z - p0.z;
        float dist = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
        if (dist < 1e-4f) return (false, 0f, p0);
        float x0 = p0.x;
        float y0 = p0.y;
        float z0 = p0.z;
        float at_y = sight_at(x0, z0);
        if (y0 <= at_y + 1f) return (true, 0f, p0);

        float wmax = _wmax;
        float dmax = _dmax;
        int tw = w;
        int td = d;
        // the tile the last sample read, and its four corner heights
        int c_i = -1, c_j = -1;
        float c00 = 0f, c10 = 0f, c01 = 0f, c11 = 0f;

        // the ground under a point, with the tile it read held for the next one
        float sample(float x, float z)
        {
            float fx = x * INV_TILE;
            float fz = z * INV_TILE;
            fx = fx < 0f ? 0f : (fx > wmax ? wmax : fx);
            fz = fz < 0f ? 0f : (fz > dmax ? dmax : fz);
            int i = (int)fx;
            int j = (int)fz;
            if (i != c_i || j != c_j)
            {
                c_i = i;
                c_j = j;
                int k = j * tw + i;
                c00 = h[k];
                c10 = h[k + 1];
                c01 = h[k + tw];
                c11 = h[k + tw + 1];
            }
            float tx = fx - i;
            float tz = fz - j;
            // the same two triangles `height_at` solves
            float y = tx + tz <= 1f
                ? c00 + (c10 - c00) * tx + (c01 - c00) * tz
                : c11 + (c01 - c11) * (1f - tx) + (c10 - c11) * (1f - tz);
            int ti = x >= 0f ? (int)(x * INV_TILE) : -1;
            int tj = z >= 0f ? (int)(z * INV_TILE) : -1;
            if (ti < 0 || tj < 0 || ti >= tw || tj >= td)
                return y + 1e6f;                 // off the map: nothing stands there
            if (tiles[tj * tw + ti] == TILE_BUILDING)
            {
                byte wv = walls[tj * tw + ti];
                return y + (wv != 0 ? wv : BUILDING_H);
            }
            return y;
        }

        float prev = 0f;
        // coarse cull: a sample above the maximum of its block is skipped
        float[] cull_a = cull ?? _cull_solid;
        int cdi = _cull_d, cwi = _cull_w;
        float cinv = _cull_inv;

        // ---- the tile walk (`_spans`, inlined) ----------------------------
        float xx = x0;
        float zz = z0;
        int ci_ = xx >= 0f ? (int)(xx * INV_TILE) : -1;
        int cj_ = zz >= 0f ? (int)(zz * INV_TILE) : -1;
        int sx = dx > 0f ? 1 : -1;
        int sz = dz > 0f ? 1 : -1;
        float tx_ = dx != 0f ? ((ci_ + (dx > 0f ? 1 : 0)) * TILE - xx) / dx : 2f;
        float tz_ = dz != 0f ? ((cj_ + (dz > 0f ? 1 : 0)) * TILE - zz) / dz : 2f;
        float ta = 0f;
        bool inside_ = true;
        while (true)
        {
            float tb = tx_ < tz_ ? tx_ : tz_;
            if (tb > 1f) tb = 1f;
            float? tt = null;
            if (0 <= ci_ && ci_ < tw && 0 <= cj_ && cj_ < td)
            {
                inside_ = true;
                // trees are rare tiles, so the type is read once here instead
                // of inside both solvers (which would each check it again)
                if (canopies)
                {
                    byte ty = tiles[cj_ * tw + ci_];
                    if (ty == TILE_TREE || ty == TILE_FOREST)
                        tt = _canopy_hit(p0, dx, dy, dz, ci_, cj_, ta, tb);
                }
            }
            else
            {
                inside_ = false;
            }
            // a span that sits inside one block can be dismissed as a whole:
            // both ends in the same 128 m cell means the cell bounds it, so
            // `min(y)` above the cell maximum proves the whole span clear
            if (!tt.HasValue && inside_)
            {
                int ax_ = (int)((x0 + dx * ta) * cinv);
                int az_ = (int)((z0 + dz * ta) * cinv);
                int bx_ = (int)((x0 + dx * tb) * cinv);
                int bz_ = (int)((z0 + dz * tb) * cinv);
                if (ax_ == bx_ && az_ == bz_ && 0 <= ax_ && ax_ < cwi && 0 <= az_ && az_ < cdi)
                {
                    float ya = y0 + dy * ta;
                    float yb = y0 + dy * tb;
                    if ((ya < yb ? ya : yb) > cull_a[az_ * cwi + ax_])
                    {
                        prev = tb;
                        ta = tb;
                        if (ta >= 1f) break;
                        xx = x0 + dx * ta;
                        zz = z0 + dz * ta;
                        if (tx_ <= tz_)
                        {
                            ci_ += sx;
                            tx_ = dx != 0f ? ((ci_ + (dx > 0f ? 1 : 0)) * TILE - x0) / dx : 2f;
                        }
                        else
                        {
                            cj_ += sz;
                            tz_ = dz != 0f ? ((cj_ + (dz > 0f ? 1 : 0)) * TILE - z0) / dz : 2f;
                        }
                        continue;
                    }
                }
            }
            int n = (int)((tb - ta) * dist / step + 0.5f);
            if (n < 1) n = 1;
            for (int k = 1; k <= n; k++)
            {
                float t = ta + (tb - ta) * k / n;
                float txp = x0 + dx * t;
                float tzp = z0 + dz * t;
                float typ = y0 + dy * t;
                int ci = (int)(txp * cinv);
                int cj = (int)(tzp * cinv);
                if (0 <= ci && ci < cwi && 0 <= cj && cj < cdi && typ > cull_a[cj * cwi + ci])
                {
                    prev = t;
                    continue;
                }
                if (typ > sample(txp, tzp))
                {
                    prev = t;
                    continue;
                }
                if (tt.HasValue && tt.Value < t)
                {
                    float tt_ = tt.Value;
                    return (true, tt_, new Vec3(x0 + dx * tt_, y0 + dy * tt_, z0 + dz * tt_));
                }
                float a = prev, b = t;
                for (int it = 0; it < 6; it++)
                {
                    float m = (a + b) * 0.5f;
                    if (y0 + dy * m > sample(x0 + dx * m, z0 + dz * m)) a = m;
                    else b = m;
                }
                t = (a + b) * 0.5f;
                return (true, t, new Vec3(x0 + dx * t, y0 + dy * t, z0 + dz * t));
            }
            if (tt.HasValue)
            {
                float tt_ = tt.Value;
                return (true, tt_, new Vec3(x0 + dx * tt_, y0 + dy * tt_, z0 + dz * tt_));
            }
            if (tb >= 1f) break;
            ta = tb;
            if (tx_ <= tz_)
            {
                ci_ += sx;
                tx_ = dx != 0f ? ((ci_ + (dx > 0f ? 1 : 0)) * TILE - x0) / dx : 2f;
            }
            else
            {
                cj_ += sz;
                tz_ = dz != 0f ? ((cj_ + (dz > 0f ? 1 : 0)) * TILE - z0) / dz : 2f;
            }
        }
        return (false, 1f, p1);
    }

    /// <summary>Where the segment first runs into a tree trunk.</summary>
    public bool trunk_hit(Vec3 p0, Vec3 p1, out float t, out Vec3 point)
    {
        float dx = p1.x - p0.x;
        float dy = p1.y - p0.y;
        float dz = p1.z - p0.z;
        foreach (var (ta, tb, i, j) in _spans(p0, dx, dz))
        {
            if (!(0 <= i && i < w && 0 <= j && j < d)) continue;
            var v = _trunk_hit(p0, dx, dy, dz, i, j, ta, tb);
            if (v.HasValue)
            {
                t = v.Value;
                point = new Vec3(p0.x + dx * t, p0.y + dy * t, p0.z + dz * t);
                return true;
            }
        }
        t = 1f;
        point = p1;
        return false;
    }

    // a structure walk samples within one tile at this spacing: a wall is a
    // vertical plane, so the bisection only has to bracket it, and half the
    // ground walk's 8 m keeps the bracket inside the tile the face belongs to
    private const float STRUCT_STEP = 4f;

    /// <summary>Where the segment first runs into a *structure*: a building's
    /// column, or a tree's trunk.
    ///
    /// The two shapes are solved, never sampled at the round's 8 m step, because
    /// neither is shaped like the ground: a building is a whole tile and is a slab
    /// from the terrain up to its roof (`walls`), a trunk is a metre-wide cylinder
    /// (`_trunk_hit`).  Walking the tiles the leg crosses and asking each one is
    /// both more exact than a point sample and cheaper - a tile that holds no
    /// structure costs one byte test.
    ///
    /// `structure` is the door the destructible map will come through: today it is
    /// always -1, and the building id takes its place without a caller changing.
    /// `tile` says which shape was hit, so the caller can record a facade and a
    /// trunk as the two different things they are.</summary>
    public bool structure_hit(Vec3 p0, Vec3 p1, out float t, out Vec3 point,
                              out byte tile, out int structure)
    {
        float dx = p1.x - p0.x, dy = p1.y - p0.y, dz = p1.z - p0.z;
        float len = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
        float prev = 0f;
        foreach (var (ta, tb, i, j) in _spans(p0, dx, dz))
        {
            if (0 <= i && i < w && 0 <= j && j < d)
            {
                int k = j * w + i;
                byte ty = tiles[k];
                if (ty == TILE_TREE || ty == TILE_FOREST)
                {
                    var v = _trunk_hit(p0, dx, dy, dz, i, j, ta, tb);
                    if (v.HasValue)
                    {
                        structure = -1;
                        return _structure_at(p0, dx, dy, dz, v.Value, ty, out t, out point,
                                             out tile);
                    }
                }
                else if (ty == TILE_BUILDING)
                {
                    // the column this tile stands as, from its terrain up to its
                    // roof - the number a point sample of the column would give
                    float wall = _wall_of(k);
                    // sample this tile's span and bisect the first sample inside
                    // the column, so the burst lands on the face nearest the
                    // round rather than on a step past it
                    int n = (int)((tb - ta) * len / STRUCT_STEP + 0.5f);
                    if (n < 1) n = 1;
                    float a = ta;
                    bool below = p0.y + dy * ta <= height_at(p0.x + dx * ta, p0.z + dz * ta) + wall;
                    for (int q = 1; q <= n && !below; q++)
                    {
                        float tt = ta + (tb - ta) * q / n;
                        float xx = p0.x + dx * tt;
                        float zz = p0.z + dz * tt;
                        a = tt;
                        if (p0.y + dy * tt <= height_at(xx, zz) + wall)
                        {
                            below = true;
                            float lo = q > 1 ? ta + (tb - ta) * (q - 1) / n : ta;
                            float hi = tt;
                            for (int it = 0; it < 6; it++)
                            {
                                float m = (lo + hi) * 0.5f;
                                if (p0.y + dy * m > height_at(p0.x + dx * m, p0.z + dz * m)
                                    + wall) lo = m;
                                else hi = m;
                            }
                            a = (lo + hi) * 0.5f;
                        }
                    }
                    if (below)
                    {
                        tile = ty;
                        structure = -1;
                        t = a;
                        point = new Vec3(p0.x + dx * a, p0.y + dy * a, p0.z + dz * a);
                        return true;
                    }
                }
            }
            prev = tb;
        }
        tile = 0;
        structure = -1;
        t = 1f;
        point = p1;
        return false;
    }

    /// <summary>The wall height of building tile `k`, in metres: a 2-6 storey
    /// column, the type's default where the generator wrote none.</summary>
    private float _wall_of(int k)
    {
        float wv = walls[k];
        return wv > 0f ? wv : BUILDING_H;
    }

    private static bool _structure_at(Vec3 p0, float dx, float dy, float dz, float t,
                                      byte ty, out float tout, out Vec3 point, out byte tile)
    {
        tile = ty;
        tout = t;
        point = new Vec3(p0.x + dx * t, p0.y + dy * t, p0.z + dz * t);
        return true;
    }

    /// <summary>Whether `a` sees `b`: the ground, the buildings and the tree
    /// canopies between them decide, not the trunks.</summary>
    /// <remarks>
    /// The canopies are the leaves the eye actually sees - one ball or one cone
    /// per tree, at the height and the width they are drawn - so a hull standing
    /// beside a stand is in plain view, and only one standing behind the trees
    /// themselves is hidden.  `Systems/Sight.cs` puts the smoke on top of this
    /// and is the door every eye uses.
    /// </remarks>
    public bool los(Vec3 a, Vec3 b, float step = 8f)
    {
        var r = _march(a, b, step, canopies: true, _cull_sight);
        return !r.hit;
    }

    // =====================================================================
    // the leaf helpers of the generators (`terrain.py` module level)
    // =====================================================================

    private static float[] _upsample(float[] g, int gw, int gh, int w, int h)
    {
        var outv = new float[w * h];
        for (int y = 0; y < h; y++)
        {
            float ys = h > 1 ? y * (gh - 1) / (float)(h - 1) : 0f;
            int y0 = (int)MathF.Floor(ys);
            if (y0 > gh - 1) y0 = gh - 1;
            int y1 = Math.Min(y0 + 1, gh - 1);
            float fy = ys - y0;
            fy = fy * fy * fy * (fy * (fy * 6f - 15f) + 10f);
            for (int x = 0; x < w; x++)
            {
                float xs = w > 1 ? x * (gw - 1) / (float)(w - 1) : 0f;
                int x0 = (int)MathF.Floor(xs);
                int x1 = Math.Min(x0 + 1, gw - 1);
                float fx = xs - x0;
                fx = fx * fx * fx * (fx * (fx * 6f - 15f) + 10f);
                float top = g[y0 * gw + x0] * (1f - fx) + g[y0 * gw + x1] * fx;
                float bot = g[y1 * gw + x0] * (1f - fx) + g[y1 * gw + x1] * fx;
                outv[y * w + x] = top * (1f - fy) + bot * fy;
            }
        }
        return outv;
    }

    /// <summary>TerraGenesis' octave amplitudes (OpenTTD's `tgp.cpp`, the
    /// "smooth" row of its table): every wavelength is half the last, and every
    /// contribution is worth a fifth to a hundredth of it.  A fractal with an
    /// even amplitude ratio puts as much slope in the metre scale as in the
    /// kilometre scale, which is a crumpled blanket; this one is a landscape.</summary>
    private static readonly float[] TGEN_AMP = { 1f, 0.45f, 0.15f, 0.05f, 0.015f, 0.005f, 0.002f };

    /// <summary>Fractal noise: octaves of upsampled value grids.  `base_res_y`
    /// coarsens the y lattice alone, which stretches the field along y - the
    /// shape of a crest line rather than of a bump.  `amps` sets the weight of
    /// each octave; without one the weight simply halves.</summary>
    private static float[] _fbm(Rng rng, int w, int h, int octaves = 6, int base_res = 2,
                                int base_res_y = 0, float[] amps = null)
    {
        int resy = base_res_y > 0 ? base_res_y : base_res;
        var outv = new float[w * h];
        float amp = 1f;
        float total = 0f;
        int res = base_res;
        for (int o = 0; o < octaves; o++)
        {
            float a = amps != null ? (o < amps.Length ? amps[o] : 0f) : amp;
            var g = new float[res * resy];
            for (int k = 0; k < g.Length; k++) g[k] = rng.random();
            var up = _upsample(g, res, resy, w, h);
            for (int k = 0; k < outv.Length; k++) outv[k] += up[k] * a;
            total += a;
            amp *= 0.5f;
            res *= 2;
            resy *= 2;
        }
        for (int k = 0; k < outv.Length; k++) outv[k] /= total;
        return outv;
    }

    /// <summary>The four land styles of TerraGenesis' curve maps, coarse to
    /// broken: a plain flattens what the fractal hands it (a fifth of the range
    /// by the top of the fractal), country rolls, hills climb, and a range keeps
    /// half its ground in the top fifth of the height.  Each is a list of
    /// (x, y) control points on a normalized height.</summary>
    private static readonly float[][] _CURVES =
    {
        new[] { 0f, 0f, 0.80f, 0.13f, 1f, 0.40f },
        new[] { 0f, 0f, 0.53f, 0.13f, 0.80f, 0.27f, 1f, 0.60f },
        new[] { 0f, 0f, 0.53f, 0.27f, 0.80f, 0.57f, 1f, 0.80f },
        new[] { 0f, 0f, 0.40f, 0.30f, 0.70f, 0.80f, 0.92f, 0.99f, 1f, 0.99f },
    };

    /// <summary>A normalized height through one style, `s` in 0..1 blending
    /// between neighbouring styles.</summary>
    private static float _curve_map(float x, float s)
    {
        float f = Math.Clamp(s, 0f, 1f) * (_CURVES.Length - 1);
        int i = Math.Min((int)f, _CURVES.Length - 2);
        float t = f - i;
        float a = _piecewise(x, _CURVES[i]);
        float b = _piecewise(x, _CURVES[i + 1]);
        return a + (b - a) * t;
    }

    /// <summary>Linear between a curve's control points.</summary>
    private static float _piecewise(float x, float[] cp)
    {
        for (int i = 0; i + 3 < cp.Length; i += 2)
        {
            if (x <= cp[i + 2] || i + 4 >= cp.Length)
            {
                float dx = cp[i + 2] - cp[i];
                return cp[i + 1] + (cp[i + 3] - cp[i + 1]) * (dx > 0f ? (x - cp[i]) / dx : 1f);
            }
        }
        return cp[^1];
    }

    /// <summary>One octave of value noise, its lattice *rotated* by the caller's
    /// angle and read with a smootherstep weight.  The rotation is what keeps the
    /// octaves from agreeing: a lattice of its own, on the same axes as its
    /// neighbours, lines its creases up with theirs and the map reads as a quilt
    /// of squares.  The lattice wraps, so the field is continuous at the edge.</summary>
    private static float[] _octave(Rng rng, int res, int w, int d, float ca, float sa)
    {
        var g = new float[res * res];
        for (int k = 0; k < g.Length; k++) g[k] = rng.random();
        var outv = new float[w * d];
        float sc = (res - 1) / (float)Math.Max(w - 1, d - 1);
        float cx = (w - 1) * 0.5f, cz = (d - 1) * 0.5f;
        for (int j = 0; j < d; j++)
        {
            float pz = j - cz;
            for (int i = 0; i < w; i++)
            {
                float px = i - cx;
                float u = (px * ca - pz * sa) * sc + (res - 1) * 0.5f;
                float v = (px * sa + pz * ca) * sc + (res - 1) * 0.5f;
                int u0 = (int)MathF.Floor(u), v0 = (int)MathF.Floor(v);
                float fu = u - u0, fv = v - v0;
                fu = fu * fu * fu * (fu * (fu * 6f - 15f) + 10f);
                fv = fv * fv * fv * (fv * (fv * 6f - 15f) + 10f);
                int ua = ((u0 % res) + res) % res, ub = (ua + 1) % res;
                int va = ((v0 % res) + res) % res, vb = (va + 1) % res;
                float top = g[va * res + ua] * (1f - fu) + g[va * res + ub] * fu;
                float bot = g[vb * res + ua] * (1f - fu) + g[vb * res + ub] * fu;
                outv[j * w + i] = top * (1f - fv) + bot * fv;
            }
        }
        return outv;
    }

    /// <summary>Separable 1-2-1 blur, one tile wide.</summary>
    private static float[] _blur3(float[] a, int w, int d)
    {
        int pw = w + 2;
        var p = new float[(d + 2) * pw];
        for (int j = 0; j < d + 2; j++)
        {
            int sj = j - 1;
            if (sj < 0) sj = 0;
            if (sj > d - 1) sj = d - 1;
            for (int i = 0; i < pw; i++)
            {
                int si = i - 1;
                if (si < 0) si = 0;
                if (si > w - 1) si = w - 1;
                p[j * pw + i] = a[sj * w + si];
            }
        }
        var x = new float[(d + 2) * w];
        for (int j = 0; j < d + 2; j++)
            for (int i = 0; i < w; i++)
                x[j * w + i] = (p[j * pw + i] + 2f * p[j * pw + i + 1] + p[j * pw + i + 2]) * 0.25f;
        var o = new float[d * w];
        for (int j = 0; j < d; j++)
            for (int i = 0; i < w; i++)
                o[j * w + i] = (x[j * w + i] + 2f * x[(j + 1) * w + i] + x[(j + 2) * w + i]) * 0.25f;
        return o;
    }

    /// <summary>Draw a rasterised line of `value` into the tile map.</summary>
    private static void _raster_line(byte[] tiles, int w, int d,
                                     (float x, float y) a, (float x, float y) b,
                                     byte value, float step = 0.5f)
    {
        float ax = a.x, ay = a.y, bx = b.x, by = b.y;
        float span = MathF.Max(MathF.Abs(bx - ax), MathF.Abs(by - ay));
        int n = (int)(span / step) + 2;
        for (int k = 0; k < n; k++)
        {
            float t = n > 1 ? k / (float)(n - 1) : 0f;      // np.linspace(0, 1, n)
            int i = (int)(ax + (bx - ax) * t);
            int j = (int)(ay + (by - ay) * t);
            if (i >= 0 && i < w && j >= 0 && j < d) tiles[j * w + i] = value;
        }
    }

    /// <summary>Lower (or raise) every vertex into the band its four neighbours
    /// allow, so no face is steeper than `max_grad` rise over run.</summary>
    /// <remarks>
    /// The continuous field can be shaped as steeply as the noise likes, and a
    /// short octave easily builds a face no hull can climb - 40 m of relief over a
    /// 125 m wavelength is above 45 degrees on its flank.  This is a Lipschitz
    /// clamp: one pass pulls each vertex to within `max_grad * TILE` of each of
    /// its four neighbours, and a handful of passes carries the excess out into
    /// the run instead of leaving a blade.  Four-neighbour only: a diagonal
    /// neighbour is `TILE * sqrt 2` away and its own limit is looser, so the pass
    /// errs towards gentle.
    /// </remarks>
    private static void _limit_slope(float[] hh, int w, int d, float max_grad, int passes = 4)
    {
        float reach = max_grad * TILE;
        // A diagonal neighbour is `TILE * sqrt 2` away, and the bound here is set
        // a little inside it: that diagonal is the shared edge of a tile's two
        // triangles, and `_limit_facets` trims a facet against it, so it has to
        // come out strictly under `sqrt 2` of the reach or the trim has no root to
        // move to (measured: a tile right on the boundary was left at 1.05).
        float diag = reach * 1.30f;
        for (int p = 0; p < passes; p++)
        {
            for (int j = 0; j < d; j++)
            {
                for (int i = 0; i < w; i++)
                {
                    int k = j * w + i;
                    float v = hh[k];
                    if (i > 0) v = Math.Clamp(v, hh[k - 1] - reach, hh[k - 1] + reach);
                    if (i < w - 1) v = Math.Clamp(v, hh[k + 1] - reach, hh[k + 1] + reach);
                    if (j > 0) v = Math.Clamp(v, hh[k - w] - reach, hh[k - w] + reach);
                    if (j < d - 1) v = Math.Clamp(v, hh[k + w] - reach, hh[k + w] + reach);
                    // the two diagonals: they are the shared edge of a tile's two
                    // triangles, and `_limit_facets` needs that edge to be inside
                    // the limit before a facet has a height it can be trimmed to
                    if (i > 0 && j > 0) v = Math.Clamp(v, hh[k - w - 1] - diag, hh[k - w - 1] + diag);
                    if (i < w - 1 && j < d - 1) v = Math.Clamp(v, hh[k + w + 1] - diag, hh[k + w + 1] + diag);
                    if (i < w - 1 && j > 0) v = Math.Clamp(v, hh[k - w + 1] - diag, hh[k - w + 1] + diag);
                    if (i > 0 && j < d - 1) v = Math.Clamp(v, hh[k + w - 1] - diag, hh[k + w - 1] + diag);
                    hh[k] = v;
                }
            }
        }
    }

    /// <summary>Trim each facet to the same `max_grad`, measured the way a hull
    /// measures it: the gradient of one triangle, which is the length of its two
    /// leg differences over a tile.
    /// </summary>
    /// <remarks>
    /// `_limit_slope` bounds the two axes, and an axis-bound field still has a
    /// corner where both of them sit at the limit at once - that facet's own
    /// gradient is `sqrt 2` times the bound, measured at 1.18 on a map capped at
    /// 0.85.  So every triangle is asked the direct question: `(u - a, v - a)` over
    /// a tile must not be longer than the limit allows.  The apex is moved to the
    /// height that puts it exactly on the limit, `A = ((U + V) +- sqrt(2G^2 -
    /// (U-V)^2)) / 2`, taking the root nearest where it stands.  Every corner is an
    /// apex of some triangle, so a couple of passes settles it; the trim only ever
    /// takes slope out of the field.
    /// </remarks>
    private static void _limit_facets(float[] hh, int w, int d, float max_grad,
                                      int passes = 10, bool reverse = false)
    {
        float G = max_grad * TILE;
        for (int p = 0; p < passes; p++)
        {
            for (int jj = 0; jj + 1 < d; jj++)
            {
                for (int ii = 0; ii + 1 < w; ii++)
                {
                    int j = reverse ? d - 2 - jj : jj;
                    int i = reverse ? w - 2 - ii : ii;
                    int k = j * w + i;                  // a = (i,j)
                    _trim(hh, k, k + 1, k + w, G);             // triangle {a,b,c}
                    _trim(hh, k + w + 1, k + w, k + 1, G);     // triangle {d,c,b}
                }
            }
        }
    }

    private static void _trim(float[] hh, int apex, int u, int v, float G)
    {
        float U = hh[u], V = hh[v];
        // The far edge first: the diagonal is the shared edge of a tile's two
        // facets, and moving an apex can push it past what either facet can hold.
        // Measured before this step existed: one tile sat at 0.95 against a 0.85
        // ceiling purely because its shared edge was 19.32 m of difference over
        // 22.6 m, which leaves the apex no height to move to at all.
        float edge = MathF.Abs(U - V);
        float max_edge = G * 1.30f;
        if (edge > max_edge)
        {
            float mid = (U + V) * 0.5f;
            float k = max_edge / edge;
            hh[u] = mid + (U - mid) * k;
            hh[v] = mid + (V - mid) * k;
            U = hh[u];
            V = hh[v];
        }
        float A = hh[apex];
        float du = U - A, dv = V - A;
        if (du * du + dv * dv <= G * G) return;
        float inside = 2f * G * G - (U - V) * (U - V);
        if (inside <= 0f) return;
        float half = 0.5f * MathF.Sqrt(inside);
        float up = (U + V) * 0.5f + half;
        float dn = (U + V) * 0.5f - half;
        hh[apex] = MathF.Abs(up - A) <= MathF.Abs(dn - A) ? up : dn;
    }

    /// <summary>Apply the slope ceiling until it holds: one axis pass, then the
    /// facet trim, repeated, and a last pass from the far end.
    ///
    /// The two constraints feed each other - a trim moves a corner that is the
    /// shared edge of the neighbouring facet, and an axis pass pulls a diagonal
    /// back inside what the trim needs to have a root - so alternating them is
    /// what makes the ceiling hold.  Measured on the battle map: axis-only left a
    /// 1.18 face, the facet trim alone 0.95, alternating 0.89, and alternating
    /// plus one pass running backwards 0.85 - the backwards pass alone is the
    /// difference between those last two, because the facet the forwards pass
    /// moved last is the one left over.
    /// </summary>
    private static void _shape_ceiling(float[] hh, int w, int d, float max_grad, int rounds = 6)
    {
        for (int r = 0; r < rounds; r++)
        {
            _limit_slope(hh, w, d, max_grad, 1);
            _limit_facets(hh, w, d, max_grad, 2);
        }
        _limit_facets(hh, w, d, max_grad, 2, reverse: true);
        _limit_facets(hh, w, d, max_grad, 1);
    }

    /// <summary>Grow a mask by `r` tiles with the four-neighbour rule.</summary>
    private static bool[] _dilate(bool[] mask, int w, int d, int r)
    {
        var outv = (bool[])mask.Clone();
        var nxt = new bool[mask.Length];
        for (int it = 0; it < r; it++)
        {
            Array.Copy(outv, nxt, outv.Length);
            for (int j = 0; j < d; j++)
            {
                for (int i = 0; i < w; i++)
                {
                    int k = j * w + i;
                    if (!outv[k]) continue;
                    if (j + 1 < d) nxt[k + w] = true;
                    if (j > 0) nxt[k - w] = true;
                    if (i + 1 < w) nxt[k + 1] = true;
                    if (i > 0) nxt[k - 1] = true;
                }
            }
            (outv, nxt) = (nxt, outv);
        }
        return outv;
    }

    /// <summary>The 0..1 hash the client plants its trees with (`map.js`).</summary>
    /// <remarks>
    /// Both sides must pick the same trunk spots, or a round stops in the air
    /// beside a trunk the player can see.  The client's copy is JavaScript, i.e.
    /// **binary64** `Math.sin`, so this one stays double on purpose: a float32
    /// argument (90 000 radians for a tile near the far edge) would throw away
    /// more than the hash is worth and put every trunk somewhere else.  The
    /// result is a 0..1 fraction and is used as a float from here on.
    /// </remarks>
    private static float _tree_hash(double a, double b)
    {
        double s = Math.Sin(a * 12.9898 + b * 78.233) * 43758.5453;
        return (float)(s - Math.Floor(s));
    }

    /// <summary>Where the trees of a tree tile stand: `(x, z, r, h, q1, q3)` each.</summary>
    /// <remarks>
    /// One or two trees on a tree tile, three to five on a forest tile, spread
    /// over the tile but never touching its border - exactly the stand the
    /// client draws.  `r`/`h` are the trunk, the only part that stops a round or
    /// a hull; `q1`/`q3` are the hash draws the canopy is built from
    /// (`_canopy`), which hides what stands behind the tree without stopping
    /// anything that flies.
    /// </remarks>
    private static Trunk[] _trunks(int i, int j, int t)
    {
        float r1 = _tree_hash(i, j);
        int n = t == TILE_FOREST ? 3 + (int)(r1 * 3) : 1 + (int)(r1 * 2);
        float cx = (i + 0.5f) * TILE;
        float cz = (j + 0.5f) * TILE;
        var outv = new Trunk[n];
        for (int q = 0; q < n; q++)
        {
            float q1 = _tree_hash(i + q * 5.3, j + q * 1.9);
            float q2 = _tree_hash(j + q * 3.7, i + q * 7.1);
            float q3 = _tree_hash(i * 1.3 + q, j * 2.7 - q);
            outv[q] = new Trunk(
                cx + (q1 - 0.5f) * TILE * 0.66f,
                cz + (q2 - 0.5f) * TILE * 0.66f,
                TRUNK_R * (0.7f + q3 * 0.5f),
                (10f + q3 * 8f) * 0.40f,
                q1, q3);
        }
        return outv;
    }

    /// <summary>The volume one tree hides behind.</summary>
    /// <remarks>
    /// The same draws, the same numbers the client builds its meshes from
    /// (`renderer._buildProps`): a broadleaf (`q3 > 0.45`) is an ellipsoid ball
    /// sitting on the trunk and a conifer is a cone standing on it.  A whole
    /// tree tile as one 10 m slab is what the sight line used to see, and it hid
    /// hulls standing in the open beside the stand.
    /// </remarks>
    private static Canopy _canopy(Trunk tr, float ground)
    {
        float tall = 10f + tr.q3 * 8f;
        if (tr.q3 > 0.45f)
        {
            float rad = tall * 0.40f * (0.85f + tr.q1 * 0.3f);
            return new Canopy(Canopy.ball, tr.x, tr.z,
                              ground + tall * 0.60f, tall * 0.46f, rad);
        }
        float cr = tall * 0.26f * (0.85f + tr.q1 * 0.3f);
        return new Canopy(Canopy.cone, tr.x, tr.z,
                          ground + tall * 0.15f, ground + tall * 0.95f, cr);
    }

    /// <summary>Earliest `t` in `[ta, tb]` that lies inside canopy `c`, or null.</summary>
    /// <remarks>
    /// Solved, never sampled: a canopy is a few metres across, so an 8 m walk
    /// steps straight over the edge of the ball - exactly the way a trunk would
    /// be missed.
    /// </remarks>
    private static float? _canopy_hit(Canopy c, Vec3 p0, float dx, float dy, float dz,
                                      float ta, float tb)
    {
        if (c.kind == Canopy.ball)
        {
            float rx = c.r, ry = c.y1, rz = c.r;
            float cy = c.y0;
            // the segment, scaled into the unit sphere of the ball
            float ax = (p0.x - c.x) / rx;
            float ay = (p0.y - cy) / ry;
            float az = (p0.z - c.z) / rz;
            float ex = dx / rx, ey = dy / ry, ez = dz / rz;
            float qa = ex * ex + ey * ey + ez * ez;
            if (qa < 1e-12f) return null;
            float qb = 2f * (ax * ex + ay * ey + az * ez);
            float qc = ax * ax + ay * ay + az * az - 1f;
            if (!_roots(qa, qb, qc, false, out float lo, out float hi)) return null;
            lo = ta > lo ? ta : lo;
            return lo <= tb && lo <= hi ? lo : (float?)null;
        }
        // a vertical cone, apex up: inside where the horizontal distance is
        // under the radius the cone has at that height
        float u = (c.r / (c.y1 - c.y0)) * (c.r / (c.y1 - c.y0));
        float ax2 = p0.x - c.x, az2 = p0.z - c.z;
        float hh = c.y1 - p0.y;                  // height left to the apex, this point
        float qa2 = dx * dx + dz * dz - u * dy * dy;
        float qb2 = 2f * (ax2 * dx + az2 * dz) + 2f * u * hh * dy;
        float qc2 = ax2 * ax2 + az2 * az2 - u * hh * hh;
        if (!_roots(qa2, qb2, qc2, qc2 <= 0f, out float lo2, out float hi2)) return null;
        // the cone is cut off at its skirt: the segment has to be inside both
        // the cone and the band between base and apex
        lo2 = MathF.Max(lo2, ta);
        hi2 = MathF.Min(hi2, tb);
        if (dy > 1e-9f)
        {
            lo2 = MathF.Max(lo2, (c.y0 - p0.y) / dy);
            hi2 = MathF.Min(hi2, (c.y1 - p0.y) / dy);
        }
        else if (dy < -1e-9f)
        {
            lo2 = MathF.Max(lo2, (c.y1 - p0.y) / dy);
            hi2 = MathF.Min(hi2, (c.y0 - p0.y) / dy);
        }
        else if (p0.y < c.y0 || p0.y > c.y1)
        {
            return null;
        }
        if (lo2 > tb || lo2 > hi2) return null;
        return lo2;
    }

    /// <summary>Interval `[lo, hi]` where `a t^2 + b t + c <= 0`.</summary>
    /// <remarks>
    /// `inside` says the start of the segment is already within, which the
    /// caller knows from `c <= 0` having been cheap to read.
    /// </remarks>
    private static bool _roots(float a, float b, float c, bool inside,
                               out float lo, out float hi)
    {
        lo = 0f;
        hi = 0f;
        if (MathF.Abs(a) < 1e-12f)
        {
            if (MathF.Abs(b) < 1e-12f)
            {
                if (!inside) return false;
                lo = 0f;
                hi = 1f;
                return true;
            }
            float t = -c / b;
            if (b > 0f)
            {
                lo = -1e9f;
                hi = t;
            }
            else
            {
                lo = t;
                hi = 1e9f;
            }
            return true;
        }
        float disc = b * b - 4f * a * c;
        if (disc < 0f) return false;
        float sq = MathF.Sqrt(disc);
        float r0 = (-b - sq) / (2f * a);
        float r1 = (-b + sq) / (2f * a);
        if (r0 <= r1)
        {
            lo = r0;
            hi = r1;
        }
        else
        {
            lo = r1;
            hi = r0;
        }
        return true;
    }

    /// <summary>`np.percentile(a, q)` with numpy's linear interpolation.</summary>
    /// <summary>Hermite 0..1 ramp between two edges - the blend a mask is made of.</summary>
    private static float Smooth(float a, float b, float v)
    {
        float t = Math.Clamp((v - a) / (b - a), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    private static float _percentile(float[] a, float q)
    {
        var c = (float[])a.Clone();
        Array.Sort(c);
        float pos = q * (c.Length - 1);
        int lo = (int)MathF.Floor(pos);
        int hi = Math.Min(lo + 1, c.Length - 1);
        float frac = pos - lo;
        return c[lo] + (c[hi] - c[lo]) * frac;
    }

    // =====================================================================
    // roads
    // =====================================================================

    /// <summary>Lay the pieces of one lane leg into the road bit map.</summary>
    /// <remarks>
    /// A leg is either along an axis - laid as half-tile pieces, so it elbows and
    /// crosses whatever it meets - or a 45 degree run, laid as the two diagonal
    /// pieces a staircase needs: one tile cuts its south-west corner, the next
    /// its north-east one, and the pair repeats.  Both ends of every piece sit
    /// in the middle of a tile edge, so consecutive pieces join full width.
    /// </remarks>
    private static void _road_leg(byte[] roads, int w, int d,
                                  (float x, float y) a, (float x, float y) b)
    {
        float ax = a.x, ay = a.y, bx = b.x, by = b.y;
        float dx = bx - ax, dy = by - ay;
        if (dx == 0f && dy == 0f) return;
        if (dx == 0f || dy == 0f)
        {
            // the leg runs down the middle of a row or a column of tiles
            int i0 = (int)MathF.Floor(MathF.Min(ax, bx));
            int i1 = (int)MathF.Floor(MathF.Max(ax, bx));
            int j0 = (int)MathF.Floor(MathF.Min(ay, by));
            int j1 = (int)MathF.Floor(MathF.Max(ay, by));
            int n = Math.Max(i1 - i0, j1 - j0);
            int sx = (dx > 0f ? 1 : 0) - (dx < 0f ? 1 : 0);
            int sy = (dy > 0f ? 1 : 0) - (dy < 0f ? 1 : 0);
            int back, fwd;
            if (sx > 0) { back = ROAD_W; fwd = ROAD_E; }
            else if (sx < 0) { back = ROAD_E; fwd = ROAD_W; }
            else if (sy > 0) { back = ROAD_N; fwd = ROAD_S; }
            else { back = ROAD_S; fwd = ROAD_N; }
            int si = sx >= 0 ? i0 : i1;
            int sj = sy >= 0 ? j0 : j1;
            for (int k = 0; k <= n; k++)
            {
                int i = si + k * sx;
                int j = sj + k * sy;
                if (k > 0) roads[j * w + i] |= (byte)back;
                if (k < n) roads[j * w + i] |= (byte)fwd;
            }
        }
        else if (MathF.Abs(dx) == MathF.Abs(dy))
        {
            int sx = (dx > 0f ? 1 : 0) - (dx < 0f ? 1 : 0);
            int sy = (dy > 0f ? 1 : 0) - (dy < 0f ? 1 : 0);
            if (sx * sy < 0)
                throw new ArgumentException($"lane leg {a}->{b}: only south-east diagonals exist");
            int n = (int)MathF.Round(MathF.Abs(dx));
            int i0 = (int)MathF.Floor(ax);
            int j0 = (int)MathF.Floor(ay);
            for (int k = 0; k < n; k++)
            {
                int i = i0 + k * sx;
                int j = j0 + k * sy;
                roads[j * w + i] |= ROAD_D_WS;
                roads[(j + sy) * w + i] |= ROAD_D_NE;
            }
        }
        else
        {
            throw new ArgumentException($"lane leg {a}->{b} is neither axis aligned nor 45 degrees");
        }
    }

    /// <summary>Lay road pieces into one tile and join every neighbour they reach.</summary>
    /// <remarks>
    /// The client draws one quad per piece and per tile, so a street that ends on
    /// another road has to give *that* tile the piece facing back - otherwise the
    /// last few metres before the junction are missing.  Joining an existing road
    /// is also how a town closes a block: the walk that runs into a street it
    /// built earlier simply connects to it.
    /// </remarks>
    private static void _pave(byte[] roads, int w, int d, int i, int j, int bits)
    {
        roads[j * w + i] |= (byte)bits;
        for (int k = 0; k < 4; k++)
        {
            int dx = _DIRS[k].x, dy = _DIRS[k].y;
            if ((bits & _BITS[k]) != 0 && roads[(j + dy) * w + (i + dx)] != 0)
                roads[(j + dy) * w + (i + dx)] |= (byte)_BITS[(k + 2) % 4];
        }
    }

    /// <summary>Lay the two pieces that join tile `a` to the neighbouring tile `b`.</summary>
    private static void _pave_to(byte[] roads, int w, int d,
                                 (int i, int j) a, (int i, int j) b)
    {
        int k = _dir_index(b.i - a.i, b.j - a.j);
        _pave(roads, w, d, a.i, a.j, _BITS[k]);
        _pave(roads, w, d, b.i, b.j, _BITS[(k + 2) % 4]);
    }

    private static int _dir_index(int dx, int dy)
    {
        for (int k = 0; k < 4; k++)
            if (_DIRS[k].x == dx && _DIRS[k].y == dy) return k;
        throw new ArgumentException($"({dx}, {dy}) is not a cardinal step");
    }

    /// <summary>The index of a single ROAD_* bit in `_BITS`.</summary>
    private static int _bit_index(int bits)
    {
        for (int k = 0; k < 4; k++)
            if (_BITS[k] == bits) return k;
        throw new ArgumentException($"0x{bits:x} is not one of the four cardinal road bits");
    }

    /// <summary>Whether the town may run a street through (i, j) heading (dx, dy).</summary>
    /// <remarks>
    /// Free grass only, and never closer than `spacing` tiles to a street that
    /// runs the same way: that minimum block width is what turns a town into
    /// blocks of houses instead of a woven mat of asphalt.
    /// </remarks>
    private static bool _street_ok(byte[] roads, byte[] tiles, int w, int d,
                                   int i, int j, int dx, int dy, int spacing)
    {
        if (tiles[j * w + i] != TILE_GRASS) return false;
        bool horiz = dy == 0;
        int par = horiz ? (ROAD_E | ROAD_W) : (ROAD_N | ROAD_S);
        int px = horiz ? 0 : 1;
        int py = horiz ? 1 : 0;
        for (int k = 1; k <= spacing; k++)
        {
            if ((roads[(j + py * k) * w + (i + px * k)] & par) != 0) return false;
            if ((roads[(j - py * k) * w + (i - px * k)] & par) != 0) return false;
        }
        return true;
    }

    // =====================================================================
    // towns: grown from the middle out, the way OpenTTD grows a city
    // =====================================================================

    /// <summary>The key one mirror orbit of town spots shares: the spot reflected
    /// across the map's centre and the one reflected across the `x = z` diagonal
    /// answer with the same key, and so draw the same size.</summary>
    private static int _orbit_key(int x, int y)
    {
        int ax = Math.Min(x, MAPW - x), ay = Math.Min(y, MAPH - y);
        return Math.Min(ax, ay) * MAPW + Math.Max(ax, ay);
    }

    /// <summary>Snap a town spot onto its lane.</summary>
    /// <remarks>
    /// A town is a town because the road goes through it: its middle sits on the
    /// arterial, so the lane runs straight past the market square and the streets
    /// grow out of it.  A spot off the lane would be a detour nobody drives
    /// through, and the depot would never be worth capturing.  Also returns the
    /// lane direction, so the streets know which side of it they are on.  The snap
    /// is exact, with no jitter of its own, because the table is what holds the
    /// two halves of the map to the same ranges out of their bases.
    /// </remarks>
    private static (int cx, int cy, float ux, float uy) _on_lane(float x, float y)
    {
        float best = 1e18f, bpx = x, bpy = y, bux = 0f, buy = 0f;
        foreach (var lane in LANE_TILES)
        {
            for (int s = 0; s + 1 < lane.Length; s++)
            {
                float ax = lane[s].x, ay = lane[s].y;
                float vx = lane[s + 1].x - ax, vy = lane[s + 1].y - ay;
                float ll = vx * vx + vy * vy;
                float u = Math.Clamp(((x - ax) * vx + (y - ay) * vy) / ll, 0f, 1f);
                float px = ax + vx * u, py = ay + vy * u;
                float dd = (px - x) * (px - x) + (py - y) * (py - y);
                if (dd < best)
                {
                    float n = MathF.Sqrt(ll);
                    best = dd;
                    bpx = px;
                    bpy = py;
                    bux = vx / n;
                    buy = vy / n;
                }
            }
        }
        return ((int)MathF.Round(bpx), (int)MathF.Round(bpy), bux, buy);
    }

    /// <summary>Grow the street network of one town out from its middle.</summary>
    /// <remarks>
    /// A street is grown the way OpenTTD grows one: a front runs a few tiles
    /// straight, then either carries on, turns a corner or dies and drops a
    /// branch off behind it - and a front that runs into a street it built
    /// earlier simply joins it, which is what closes a block.  The fronts start
    /// in the middle and are re-opened on any street that still has room to
    /// grow, so a town that ran out of fronts on one side carries on on another.
    ///
    /// Every town draws its own block width, its own run lengths and its own
    /// branching odds, so no two networks read alike; the arterial is never
    /// touched - nothing here removes a road piece, and `_street_ok` keeps the
    /// town's own streets off a parallel course right next to it.
    /// </remarks>
    private static void _town_streets(byte[] roads, byte[] tiles, int w, int d, Rng rng,
                                      int cx, int cy, float ux, float uy,
                                      int radius, int spacing, int runs)
    {
        var own = new List<(int i, int j)>();               // the town's own street tiles
        var fronts = new List<(int i, int j, int h)>();
        for (int k = 0; k < 4; k++) fronts.Add((cx, cy, k));
        // ... and the town hangs off both sides of its lane: a street leaving
        // the arterial at a few points along it is what makes the lane the
        // town's main street instead of a road that happens to pass by
        foreach (int k in _LANE_HANGS)
        {
            int si = cx + (int)MathF.Round(ux * k);
            int sj = cy + (int)MathF.Round(uy * k);
            foreach (int side in _SIDES)
            {
                int h = -1;
                foreach (int cand in rng.permutation(_FOUR))
                {
                    if (side * (ux * _DIRS[cand].y - uy * _DIRS[cand].x) > 0.2f)
                    {
                        h = cand;
                        break;
                    }
                }
                if (h < 0) continue;
                int fi = si + _DIRS[h].x;
                int fj = sj + _DIRS[h].y;
                if (!(0 <= fi && fi < w && 0 <= fj && fj < d)) continue;
                if (roads[fj * w + fi] != 0 || tiles[fj * w + fi] != TILE_GRASS) continue;
                _pave(roads, w, d, fi, fj, _BITS[h] | _BITS[(h + 2) % 4]);
                own.Add((fi, fj));
                fronts.Add((fi, fj, h));
            }
        }

        bool reopen()
        {
            // Open a front on a street tile that still has somewhere to go.
            for (int t = 0; t < 24; t++)
            {
                var (i, j) = own[rng.integers(0, own.Count)];
                foreach (int k in rng.permutation(_FOUR))
                {
                    if ((roads[j * w + i] & _BITS[k]) != 0) continue;
                    int ti = i + _DIRS[k].x;
                    int tj = j + _DIRS[k].y;
                    if (Math.Abs(ti - cx) <= radius && Math.Abs(tj - cy) <= radius)
                    {
                        fronts.Add((i, j, k));
                        return true;
                    }
                }
            }
            return false;
        }

        for (int r = 0; r < runs; r++)
        {
            if (fronts.Count == 0 && !reopen()) break;
            int idx = rng.integers(0, fronts.Count);
            var (fi, fj, head) = fronts[idx];
            int i = fi, j = fj;
            int run = rng.integers(2, 7);
            var path = new List<(int i, int j, int h)>();   // the tiles this run laid
            while (run > 0)
            {
                int dx = _DIRS[head].x, dy = _DIRS[head].y;
                int ti = i + dx, tj = j + dy;
                if (Math.Abs(ti - cx) > radius || Math.Abs(tj - cy) > radius) break;
                if (roads[tj * w + ti] != 0)
                {
                    _pave(roads, w, d, i, j, _BITS[head]);  // joins, and the front ends
                    run = -1;
                    break;
                }
                if (!_street_ok(roads, tiles, w, d, ti, tj, dx, dy, spacing)) break;
                if (roads[j * w + i] == 0) own.Add((i, j));
                _pave(roads, w, d, i, j, _BITS[head]);
                _pave(roads, w, d, ti, tj, _BITS[(head + 2) % 4]);
                i = ti;
                j = tj;
                path.Add((i, j, head));
                own.Add((i, j));
                run -= 1;
            }
            if (path.Count == 0)
            {
                // boxed in: turn the front instead of giving up, so a street
                // that runs into a wall carries on beside it rather than
                // dead-ending
                int[] opts = { (head + 1) % 4, (head + 3) % 4 };
                rng.shuffle(opts);
                bool placed = false;
                foreach (int nh in opts)
                {
                    int ti = i + _DIRS[nh].x, tj = j + _DIRS[nh].y;
                    if (Math.Abs(ti - cx) > radius || Math.Abs(tj - cy) > radius
                        || (roads[j * w + i] & _BITS[nh]) != 0)
                        continue;
                    if (roads[tj * w + ti] != 0
                        || _street_ok(roads, tiles, w, d, ti, tj,
                                      _DIRS[nh].x, _DIRS[nh].y, spacing))
                    {
                        fronts[idx] = (i, j, nh);
                        placed = true;
                        break;
                    }
                }
                if (!placed) fronts.RemoveAt(idx);          // nowhere to go from here
                continue;
            }
            float rr = rng.random();
            if (rr < 0.5f)                                  // the front carries on
                fronts[idx] = (i, j, head);
            else if (rr < 0.82f)                            // ... and turns a corner
                fronts[idx] = (i, j, (head + (rng.random() < 0.5f ? 1 : 3)) % 4);
            else                                            // the street ends here
                fronts.RemoveAt(idx);
            if (run < 0) continue;
            // a side street off the back of the run
            if (rng.random() < 0.55f)
            {
                var (bi, bj, bh) = path[rng.integers(0, path.Count)];
                int nh = (bh + (rng.random() < 0.5f ? 1 : 3)) % 4;
                if ((roads[bj * w + bi] & _BITS[nh]) == 0) fronts.Add((bi, bj, nh));
            }
        }
    }

    /// <summary>Pave the shortest free line from the dead end at (i0, j0) to a street.</summary>
    /// <remarks>
    /// A widening search over free ground - houses are walls, open plots are
    /// passable - and the line is laid along the first street it touches, so the
    /// way through is the shortest one there is and never runs through a
    /// building.
    /// </remarks>
    private static bool _town_link(byte[] roads, byte[] tiles, int w, int d,
                                   int i0, int j0, int cx, int cy, int radius, int reach)
    {
        int bits = roads[j0 * w + i0] & ROAD_STRAIGHT;
        int k = _bit_index(bits);                       // the one way out it has
        var back = (i: i0 + _DIRS[k].x, j: j0 + _DIRS[k].y);   // ... which is its own street
        var here = (i: i0, j: j0);
        var prev = new Dictionary<(int i, int j), (int i, int j)>();
        var queue = new List<(int i, int j)>();
        prev[here] = (-99999, -99999);                  // the dead end itself
        foreach (var (dx, dy) in _DIRS)
        {
            var ti = (i: i0 + dx, j: j0 + dy);
            if (ti == back) continue;                   // that road is no way out
            if (roads[ti.j * w + ti.i] != 0)
            {
                _pave_to(roads, w, d, here, ti);        // a street right next door
                return true;
            }
            if (tiles[ti.j * w + ti.i] == TILE_GRASS)
            {
                prev[ti] = here;
                queue.Add(ti);
            }
        }
        int head = 0;
        while (head < queue.Count)
        {
            var (i, j) = queue[head];
            head++;
            foreach (var (dx, dy) in _DIRS)
            {
                var ti = (i: i + dx, j: j + dy);
                if (Math.Abs(ti.i - i0) > reach || Math.Abs(ti.j - j0) > reach) continue;
                if (Math.Abs(ti.i - cx) > radius + 1 || Math.Abs(ti.j - cy) > radius + 1) continue;
                if (roads[ti.j * w + ti.i] != 0)
                {
                    // a street: pave the line back from it to the dead end
                    var line = new List<(int i, int j)> { ti };
                    var node = (i, j);
                    while (prev.TryGetValue(node, out var pn) && pn.i != -99999)
                    {
                        line.Add(node);
                        node = pn;
                    }
                    line.Add(node);                     // the dead end itself
                    for (int s = 1; s < line.Count; s++)
                        _pave_to(roads, w, d, line[s], line[s - 1]);
                    return true;
                }
                if (tiles[ti.j * w + ti.i] != TILE_GRASS || prev.ContainsKey(ti)) continue;
                prev[ti] = (i, j);
                queue.Add(ti);
            }
        }
        return false;
    }

    /// <summary>Run the dead ends of a town on until they reach another street.</summary>
    /// <remarks>
    /// Growth leaves loose ends: a street that stops in the middle of a block, a
    /// stub behind a house.  A road that leads nowhere is a trap - a mech turns
    /// into it, finds a wall of buildings and has to reverse out - so every
    /// loose end gets a way through, found by widening the search a tile at a
    /// time over free ground until it touches another street.  What stays is
    /// what a town is supposed to have: cul-de-sacs with buildings all around
    /// them, and streets that walk out of town into the open field instead of
    /// turning back.
    /// </remarks>
    private static void _town_close(byte[] roads, byte[] tiles, int w, int d, Rng rng,
                                    int cx, int cy, int radius, int reach)
    {
        for (int pass = 0; pass < 8; pass++)            // closing one frees the next
        {
            var ends = new List<(int i, int j)>();
            for (int j = Math.Max(0, cy - radius - 2); j < Math.Min(d, cy + radius + 3); j++)
            {
                for (int i = Math.Max(0, cx - radius - 2); i < Math.Min(w, cx + radius + 3); i++)
                {
                    int bits = roads[j * w + i] & ROAD_STRAIGHT;
                    if (bits != 0 && (bits & (bits - 1)) == 0)   // exactly one way out
                        ends.Add((i, j));
                }
            }
            if (ends.Count == 0) return;
            rng.shuffle(ends);
            int linked = 0;
            foreach (var (i0, j0) in ends)
            {
                int i = i0, j = j0;
                int bits = roads[j * w + i] & ROAD_STRAIGHT;
                if (bits == 0 || (bits & (bits - 1)) != 0) continue;  // closed by the one before
                if (_town_link(roads, tiles, w, d, i, j, cx, cy, radius, reach))
                {
                    linked++;
                    continue;
                }
                // nothing to join: a loose end facing open ground runs on out
                // of town instead, the way a town's roads thin out into the
                // fields
                int k = (_bit_index(bits) + 2) % 4;      // ... the way it has left
                int dx = _DIRS[k].x, dy = _DIRS[k].y;
                int ti = i + dx, tj = j + dy;
                int run = 0;
                while (run < 4 && Math.Abs(ti - cx) <= radius + 4
                       && Math.Abs(tj - cy) <= radius + 4
                       && tiles[tj * w + ti] == TILE_GRASS && roads[tj * w + ti] == 0)
                {
                    _pave(roads, w, d, i, j, _BITS[k]);
                    _pave(roads, w, d, ti, tj, _BITS[(k + 2) % 4]);
                    i = ti;
                    j = tj;
                    ti = i + dx;
                    tj = j + dy;
                    run += 1;
                }
                if (run > 0) linked++;
            }
            if (linked == 0) return;
        }
    }

    /// <summary>Pave a side street off (i, j) toward the next street over, or nothing.</summary>
    /// <remarks>
    /// The line is scanned before a single piece is laid: if the way through runs
    /// into a house or into nothing at all the block stays as it is, so this
    /// never digs a new dead end.
    /// </remarks>
    private static bool _spur(byte[] roads, byte[] tiles, int w, int d, int i, int j, int k,
                              int cx, int cy, int radius, int reach)
    {
        int dx = _DIRS[k].x, dy = _DIRS[k].y;
        var line = new List<(int i, int j)> { (i, j) };
        int ti = i + dx, tj = j + dy;
        while (roads[tj * w + ti] == 0 && line.Count <= reach)
        {
            if (tiles[tj * w + ti] != TILE_GRASS
                || Math.Abs(ti - cx) > radius + 2 || Math.Abs(tj - cy) > radius + 2)
                return false;
            line.Add((ti, tj));
            ti += dx;
            tj += dy;
        }
        if (roads[tj * w + ti] == 0) return false;
        line.Add((ti, tj));
        for (int s = 1; s < line.Count; s++)
            _pave_to(roads, w, d, line[s - 1], line[s]);
        return true;
    }

    /// <summary>Cut the long streets of a town into blocks.</summary>
    /// <remarks>
    /// A street that runs on for a dozen tiles without a way off it is a canyon:
    /// the player drives it because there is nothing else to do, and never finds
    /// the street behind the block.  So every `rung` tiles a street that carries
    /// straight through gets a side street punched across to the next one - only
    /// where a street is really there to reach, so a block gets opened up rather
    /// than a cul-de-sac added.
    /// </remarks>
    private static void _town_rungs(byte[] roads, byte[] tiles, int w, int d, Rng rng,
                                    int cx, int cy, int radius, int rung, int reach)
    {
        int off = rng.integers(0, Math.Max(1, rung));
        int i0 = Math.Max(0, cx - radius - 2), i1 = Math.Min(w, cx + radius + 3);
        int j0 = Math.Max(0, cy - radius - 2), j1 = Math.Min(d, cy + radius + 3);
        for (int j = j0; j < j1; j++)
        {
            for (int i = i0; i < i1; i++)
            {
                int bits = roads[j * w + i] & ROAD_STRAIGHT;
                if (bits == 0 || (bits & (bits - 1)) == 0)
                    continue;                            // nothing, or a dead end
                if ((bits & (ROAD_E | ROAD_W)) == (ROAD_E | ROAD_W)
                    && (bits & (ROAD_N | ROAD_S)) == 0)
                {
                    if ((i + off) % rung != 0) continue;
                    if (!_spur(roads, tiles, w, d, i, j, 3, cx, cy, radius, reach))
                        _spur(roads, tiles, w, d, i, j, 1, cx, cy, radius, reach);
                }
                else if ((bits & (ROAD_N | ROAD_S)) == (ROAD_N | ROAD_S)
                         && (bits & (ROAD_E | ROAD_W)) == 0)
                {
                    if ((j + off) % rung != 0) continue;
                    if (!_spur(roads, tiles, w, d, i, j, 0, cx, cy, radius, reach))
                        _spur(roads, tiles, w, d, i, j, 2, cx, cy, radius, reach);
                }
            }
        }
    }

    /// <summary>Put up to `count` buildings along the streets of one town.</summary>
    /// <remarks>
    /// Houses are put up in rows, the way a street gets built up: a plot is
    /// drawn, and then two to five more go up shoulder to shoulder beside it
    /// along the same frontage before the town moves on to another street.  The
    /// rows are what makes a town read as a town - a terrace here, a gap with a
    /// back garden there - and the count is what keeps it from becoming a maze
    /// of walls, which is also why the plots *not* taken stay open on both sides
    /// of the street.
    ///
    /// The plots facing the arterial are left to `no_build`: with a tile or two
    /// of verge kept clear, the lane the front drives on stays a road with a
    /// shoulder instead of a corridor between two walls.
    /// </remarks>
    private static int _town_houses(byte[] roads, byte[] tiles, byte[] walls,
                                    bool[] clear, bool[] no_build, int w, int d, Rng rng,
                                    int cx, int cy, int radius, int count)
    {
        int reach = radius + 2;
        int i0 = Math.Max(0, cx - reach), i1 = Math.Min(w, cx + reach + 1);
        int j0 = Math.Max(0, cy - reach), j1 = Math.Min(d, cy + reach + 1);
        int wh = j1 - j0;                                // window rows
        int ww = i1 - i0;                                // window columns
        if (wh <= 0 || ww <= 0) return 0;
        var st = new bool[wh * ww];
        var free = new bool[wh * ww];
        var plaza = new bool[wh * ww];
        for (int y = 0; y < wh; y++)
        {
            for (int x = 0; x < ww; x++)
            {
                int k = (j0 + y) * w + (i0 + x);
                int kw = y * ww + x;
                st[kw] = roads[k] != 0;
                // the square in the middle stays open - a disc, not a block, so
                // the town wraps its corners around it: it is where a mech comes
                // down when the town is a forward spawn point, and where the
                // civilians walk
                float dxp = i0 + x - cx;
                float dzp = j0 + y - cy;
                plaza[kw] = MathF.Sqrt(dxp * dxp + dzp * dzp) <= TOWN_PLAZA;
                free[kw] = tiles[k] == TILE_GRASS && !no_build[k] && !st[kw];
            }
        }
        // the plots a street faces: one tile of frontage, not the street itself
        var stgrow = _dilate(st, ww, wh, 1);
        var front = new bool[wh * ww];
        for (int k = 0; k < front.Length; k++)
            front[k] = stgrow[k] && !st[k] && free[k] && !plaza[k];

        bool put(int y, int x)
        {
            // Raise one building of one to four tiles on a plot that faces a
            // street, and return whether there was room for it.
            float r = rng.random();
            int bw = 1, bh = 1;
            if (r > 0.86f) { bw = 2; bh = 2; }
            else if (r > 0.58f)
            {
                if (rng.random() < 0.5f) { bw = 2; bh = 1; }
                else { bw = 1; bh = 2; }
            }
            int y0 = Math.Min(y, wh - bh);
            int x0 = Math.Min(x, ww - bw);
            for (int yy = y0; yy < y0 + bh; yy++)
                for (int xx = x0; xx < x0 + bw; xx++)
                {
                    int kw = yy * ww + xx;
                    if (!free[kw] || plaza[kw]) return false;
                    if (tiles[(j0 + yy) * w + (i0 + xx)] != TILE_GRASS) return false;
                }
            // the closer to the middle, the taller the block
            float ddx = x + i0 - cx;
            float ddz = y + j0 - cy;
            float dist = MathF.Sqrt(ddx * ddx + ddz * ddz);
            int lo, hi;
            if (dist < 9f) { lo = 4; hi = 8; }
            else if (dist < 15f) { lo = 3; hi = 6; }
            else { lo = 2; hi = 4; }
            int storeys = rng.integers(lo, hi + 1);
            for (int yy = y0; yy < y0 + bh; yy++)
                for (int xx = x0; xx < x0 + bw; xx++)
                {
                    int kw = yy * ww + xx;
                    int k = (j0 + yy) * w + (i0 + xx);
                    tiles[k] = TILE_BUILDING;
                    walls[k] = (byte)(storeys * FLOOR_H);
                    clear[k] = true;
                    free[kw] = false;
                }
            return true;
        }

        var plots = new List<(int y, int x)>();
        for (int y = 0; y < wh; y++)
            for (int x = 0; x < ww; x++)
                if (front[y * ww + x]) plots.Add((y, x));
        rng.shuffle(plots);
        int built = 0;
        foreach (var (y, x) in plots)
        {
            if (built >= count) break;
            if (!front[y * ww + x] || !free[y * ww + x])
                continue;                                // row already ran over it
            // which way does the frontage run?  the plot faces the street across
            // one side, so the row continues along the other axis
            int alongx = 1, alongy = 0;
            foreach (var (dx, dy) in _DIRS)
            {
                int ny = y + dy, nx = x + dx;
                if (0 <= ny && ny < wh && 0 <= nx && nx < ww && st[ny * ww + nx])
                {
                    alongx = dy != 0 ? 0 : 1;
                    alongy = dy != 0 ? 1 : 0;
                    break;
                }
            }
            int step = rng.random() < 0.5f ? 1 : -1;
            int run = rng.integers(2, 6);                // buildings in this row
            int py = y, px = x;
            while (run > 0)
            {
                if (built >= count) break;
                if (put(py, px))
                {
                    built += 1;
                    run -= 1;
                }
                else if (!free[py * ww + px])
                {
                    break;
                }
                else
                {
                    run -= 1;
                }
                py += alongy * step;
                px += alongx * step;
                if (!(0 <= py && py < wh && 0 <= px && px < ww)) break;
            }
        }
        for (int y = 0; y < wh; y++)
            for (int x = 0; x < ww; x++)
                if (plaza[y * ww + x]) clear[(j0 + y) * w + (i0 + x)] = true;
        return built;
    }

    private static readonly int[] _SIDES = { 1, -1 };
    private static readonly int[] _FOUR = { 0, 1, 2, 3 };

    // =====================================================================
    // the battle map
    // =====================================================================

    /// <summary>Grow the battle map: 16 km of hills, three lanes, roads
    /// and 25 grown towns.</summary>
    public static Terrain generate(int seed = 11)
    {
        var rng = new Rng(seed);
        int w = MAPW, d = MAPH;

        // ---- the landform ----------------------------------------------------
        // TerraGenesis, the way OpenTTD grows a landscape (src/tgp.cpp), with our
        // own numbers: a fractal whose octave amplitudes fall away far faster
        // than its wavelengths shorten, then a *region by region* blend of land
        // styles.  That order matters - the fractal alone is one carpet of the
        // same bump everywhere, and it is the style blend that puts a plain and
        // a range on the same map.
        //
        //   amp    OpenTTD's "very rough" row of its table: a sweep of kilometres
        //          and a texture under it worth a degree of slope
        //   mask   the first three octaves of the fractal itself, so a range does
        //          not stand on ground that was flat
        //   style  four curve maps, from a plain (which flattens what it is given
        //          to a fifth of the range) to a range (which keeps half its
        //          ground in the top fifth of the height)
        //
        // The fractal is summed octave by octave here rather than through `_fbm`
        // because the first three octaves alone are the mask - *where* the high
        // ground is - and a mask drawn from the same field is what keeps a range
        // off ground that was already flat.  Every octave draws its own lattice
        // angle: lattices on one set of axes line their creases up and the map
        // reads as a quilt of squares.
        var land = new float[w * d];
        var coarse = new float[w * d];
        float amp_total = 0f, coarse_total = 0f;
        int res = 10;
        for (int o = 0; o < TGEN_AMP.Length; o++)
        {
            float ang = (rng.random() - 0.5f) * MathF.PI;
            var up = _octave(rng, res, w, d, MathF.Cos(ang), MathF.Sin(ang));
            float a = TGEN_AMP[o];
            for (int k = 0; k < land.Length; k++)
            {
                land[k] += up[k] * a;
                if (o < 3) coarse[k] += up[k] * a;
            }
            amp_total += a;
            if (o < 3) coarse_total += a;
            res *= 2;
        }
        for (int k = 0; k < land.Length; k++)
        {
            land[k] /= amp_total;
            coarse[k] /= coarse_total;
        }
        var relief = _fbm(rng, w, d, 4, 40);

        // the lowest tenth of the fractal is the valley floor: it is held at the
        // floor level rather than scaled, which is what makes a plain a plain
        float lo = _percentile(land, 0.10f);
        float hi = _percentile(land, 0.995f);
        // and the mask is read off the coarse field, so the blend between a
        // plain and a range is a band of kilometres and never a seam
        float s_lo = _percentile(coarse, 0.18f);
        float s_hi = _percentile(coarse, 0.55f);

        const float FLOOR = 16f;        // metres: the lowest ground on the map
        const float RISE = 205f;        // metres: floor to the highest summit
        var height = new float[w * d];
        for (int k = 0; k < height.Length; k++)
        {
            float x01 = Math.Clamp((land[k] - lo) / (hi - lo), 0f, 1f);
            // the style stops short of the last curve: a curve map's top is a
            // plateau, and a 200 m summit with a table on it reads as a mesa
            float s = Smooth(s_lo, s_hi, coarse[k]) * 0.8f;
            height[k] = FLOOR + _curve_map(x01, s) * RISE + (relief[k] - 0.5f) * 11f;
        }
        height = _blur3(_blur3(height, w, d), w, d);
        _shape_ceiling(height, w, d, Defs.MAX_TERRAIN_GRADIENT);

        var tiles = new byte[d * w];

        // command base plateaus
        foreach (var (fac, bt) in BASE_TILES)
        {
            float bx = bt.x * TILE, by = bt.y * TILE;
            int btx = (int)bt.x, bty = (int)bt.y;
            float target = height[bty * w + btx];
            // the blend is over at 600 m, so only that window has to be touched
            int span = (int)(600f / TILE) + 2;
            for (int j = Math.Max(0, bty - span); j <= Math.Min(d - 1, bty + span); j++)
            {
                for (int i = Math.Max(0, btx - span); i <= Math.Min(w - 1, btx + span); i++)
                {
                    float dx = i * TILE - bx, dz = j * TILE - by;
                    float k = Math.Clamp(1f - MathF.Sqrt(dx * dx + dz * dz) / 600f, 0f, 1f);
                    if (k <= 0f) continue;
                    k = k * k * (3f - 2f * k);
                    int kk = j * w + i;
                    height[kk] = height[kk] * (1f - k) + target * k;
                }
            }
        }

        // ---- roads -----------------------------------------------------------
        // one bit per road piece: the client draws exactly these pieces, so the
        // surface it lays on the ground is the tile map, not a ribbon of its own
        var roads = new byte[d * w];
        foreach (var lane in LANE_TILES)
            for (int s = 0; s + 1 < lane.Length; s++)
                _road_leg(roads, w, d, lane[s], lane[s + 1]);
        var lane_road = new bool[d * w];
        for (int k = 0; k < lane_road.Length; k++) lane_road[k] = roads[k] != 0;

        // ---- towns -----------------------------------------------------------
        // A town is grown from its middle out, and it is grown *before* the
        // ground is cut, because its streets have to be part of the corridor:
        // the town sits on terraces like the lanes do.  The arterial is not the
        // town's to touch - nothing below ever removes a road piece and nothing
        // builds on the lane or its verge, so the front's road stays straight
        // and open however the town happens to grow around it.
        var straight_road = new bool[d * w];
        var diag_road = new bool[d * w];
        for (int k = 0; k < straight_road.Length; k++)
        {
            straight_road[k] = (roads[k] & ROAD_STRAIGHT) != 0;
            diag_road[k] = (roads[k] & ROAD_DIAG) != 0;
        }
        // buildings keep one tile off a straight road but two off a diagonal
        // one: the diagonal piece reaches into the tile corners, so a house
        // beside it would stand in the roadway and wedge a hull against its wall
        var no_build = new bool[d * w];
        {
            var grown_straight = _dilate(straight_road, w, d, 1);
            var grown_diag = _dilate(diag_road, w, d, 2);
            for (int k = 0; k < no_build.Length; k++)
                no_build[k] = grown_straight[k] || grown_diag[k];
        }
        var walls = new byte[d * w];
        var towns = new List<Town>();
        var clear = new bool[d * w];
        Array.Copy(lane_road, clear, clear.Length);
        var spots = new List<(int x, int y, bool neutral)>();
        foreach (var s in TOWN_SPOTS) spots.Add((s.x, s.y, false));
        foreach (var s in TOWN_NEUTRAL) spots.Add((s.x, s.y, true));
        foreach (var spot in spots)
        {
            var (cx, cy, ux, uy) = _on_lane(spot.x, spot.y);
            towns.Add(new Town
            {
                x = cx * TILE,
                z = cy * TILE,
                tile_x = cx,
                tile_y = cy,
                owner = -1,
                progress = 0f,
                name = $"T{towns.Count:00}",
                neutral = spot.neutral,
                radius = 0,
            });
            // how big this town is comes off a stream of its own, keyed to the
            // mirror orbit the spot belongs to, so every town a spot stands for
            // is the same size: neither side opens on a bigger prize than the
            // other.  The streets themselves still grow off the map's own
            // stream, so no two towns lay out alike.
            var size = new Rng(_orbit_key(spot.x, spot.y));
            int radius = size.integers(TOWN_RADIUS.lo, TOWN_RADIUS.hi + 1);
            _town_streets(roads, tiles, w, d, rng, cx, cy, ux, uy, radius,
                          size.integers(TOWN_BLOCK.lo, TOWN_BLOCK.hi + 1),
                          size.integers(TOWN_RUNS.lo, TOWN_RUNS.hi + 1));
            // cross streets first, then every loose end gets a way out - both
            // before anything is built, so the new streets are open on both sides
            _town_rungs(roads, tiles, w, d, rng, cx, cy, radius, TOWN_RUNG, TOWN_REACH);
            _town_close(roads, tiles, w, d, rng, cx, cy, radius, TOWN_REACH);
            _town_houses(roads, tiles, walls, clear, no_build, w, d, rng, cx, cy,
                         radius, size.integers(TOWN_HOUSES.lo, TOWN_HOUSES.hi + 1));
            towns[^1].radius = radius;
        }

        // ---- the town square -------------------------------------------------
        // A town follows its hill.  The *square* in the middle (`TOWN_PLAZA`) is
        // level, because that is where a hull comes down when the town is a
        // forward spawn, and the ground around it is pulled half way towards the
        // square's height - enough that the streets and the blocks are not a
        // staircase, little enough that the town still climbs.
        foreach (var tn in towns)
        {
            float plate = height[tn.tile_y * w + tn.tile_x];
            float plaza = TOWN_PLAZA * TILE;
            float outer = tn.radius * TILE + 120f;
            int span = (int)(outer / TILE) + 2;
            for (int j = Math.Max(0, tn.tile_y - span); j <= Math.Min(d - 1, tn.tile_y + span); j++)
            {
                for (int i = Math.Max(0, tn.tile_x - span); i <= Math.Min(w - 1, tn.tile_x + span); i++)
                {
                    float dx = i * TILE - tn.x, dz = j * TILE - tn.z;
                    float dist = MathF.Sqrt(dx * dx + dz * dz);
                    if (dist >= outer) continue;
                    // 1 on the square, 0.5 across the town, 0 past its edge
                    float k = dist <= plaza
                        ? 1f
                        : 0.5f * (1f - Math.Clamp((dist - plaza) / (outer - plaza), 0f, 1f));
                    k = k * k * (3f - 2f * k);          // smoothstep
                    int kk = j * w + i;
                    height[kk] = height[kk] * (1f - k) + plate * k;
                }
            }
        }

        var road = new bool[d * w];
        for (int k = 0; k < road.Length; k++) road[k] = roads[k] != 0;
        for (int k = 0; k < road.Length; k++) if (road[k]) tiles[k] = TILE_ROAD;

        // ---- road corridor ---------------------------------------------------
        // Cut the roads into the ground: the ground under the asphalt is
        // averaged over its own tile and the tiles beside it, so what the
        // asphalt is laid on is a gentle cross section instead of four corners
        // of four different slopes meeting under the road, and it never breaks
        // up along the tile diagonals.  A road tile outvotes the tiles around it,
        // so the road itself never bends: where it changes level, the verges do.
        var corridor = _dilate(road, w, d, 2);
        var htile = _blur3(_blur3(_blur3(height, w, d), w, d), w, d);

        // every vertex averages the four tiles that touch it, with a road tile
        // counting ROAD_WEIGHT times as much as a verge
        int cw = w + 1, cd = d + 1;
        var ra = new float[cd * cw];
        var rn = new float[cd * cw];
        var ca = new float[cd * cw];
        var cn = new float[cd * cw];
        for (int j = 0; j < d; j++)
        {
            for (int i = 0; i < w; i++)
            {
                int k = j * w + i;
                float v = htile[k];
                int b0 = j * cw + i;
                if (road[k])
                {
                    ra[b0] += v; ra[b0 + 1] += v; ra[b0 + cw] += v; ra[b0 + cw + 1] += v;
                    rn[b0] += 1f; rn[b0 + 1] += 1f; rn[b0 + cw] += 1f; rn[b0 + cw + 1] += 1f;
                }
                if (corridor[k])
                {
                    ca[b0] += v; ca[b0 + 1] += v; ca[b0 + cw] += v; ca[b0 + cw + 1] += v;
                    cn[b0] += 1f; cn[b0 + 1] += 1f; cn[b0 + cw] += 1f; cn[b0 + cw + 1] += 1f;
                }
            }
        }
        for (int j = 0; j < d; j++)
        {
            for (int i = 0; i < w; i++)
            {
                int b0 = j * cw + i;
                float cnt = rn[b0] * ROAD_WEIGHT + cn[b0];
                if (cnt <= 0f) continue;
                float acc = ra[b0] * ROAD_WEIGHT + ca[b0];
                height[j * w + i] = acc / MathF.Max(cnt, 1f);
            }
        }

        // the corridor and the town plates move the ground after the first clamp,
        // so the ceiling is applied once more over the finished field: a cut bank
        // beside a road is still a bank, and this is where it is made drivable
        _shape_ceiling(height, w, d, Defs.MAX_TERRAIN_GRADIENT);

        // ---- base pads -------------------------------------------------------
        // the pad itself is drivable; the bunker core in the middle is solid so
        // nothing can walk through the command building
        foreach (var (fac, bt) in BASE_TILES)
        {
            int i0 = (int)bt.x, j0 = (int)bt.y;
            for (int j = Math.Max(0, j0 - 7); j < Math.Min(d, j0 + 8); j++)
                for (int i = Math.Max(0, i0 - 7); i < Math.Min(w, i0 + 8); i++)
                    tiles[j * w + i] = TILE_BASE;
            for (int j = Math.Max(0, j0 - 2); j < Math.Min(d, j0 + 3); j++)
                for (int i = Math.Max(0, i0 - 2); i < Math.Min(w, i0 + 3); i++)
                    tiles[j * w + i] = TILE_SOLID;
        }

        // ---- forests ---------------------------------------------------------
        // a thick stand in the middle of every blob (several trees per tile) with
        // a ragged edge of single-tree tiles, plus lone trees scattered over the
        // plains: the countryside reads as groves instead of a painted texture
        for (int blob = 0; blob < 460; blob++)
        {
            int cx = rng.integers(6, w - 6);
            int cy = rng.integers(6, d - 6);
            int rad = rng.integers(2, 12);
            int j0 = Math.Max(1, cy - rad), j1 = Math.Min(d - 1, cy + rad + 1);
            int i0 = Math.Max(1, cx - rad), i1 = Math.Min(w - 1, cx + rad + 1);
            float core2 = rad * 0.62f * (rad * 0.62f);
            for (int j = j0; j < j1; j++)
            {
                for (int i = i0; i < i1; i++)
                {
                    float draw = rng.random();       // one draw per tile, row-major
                    int r2 = (i - cx) * (i - cx) + (j - cy) * (j - cy);
                    int k = j * w + i;
                    if (r2 <= rad * rad && draw < 0.62f && tiles[k] == TILE_GRASS)
                        tiles[k] = TILE_TREE;
                    if (r2 <= core2 && tiles[k] == TILE_TREE)
                        tiles[k] = TILE_FOREST;
                }
            }
        }

        for (int j = 0; j < d; j++)
        {
            for (int i = 0; i < w; i++)
            {
                float draw = rng.random();
                int k = j * w + i;
                if (draw < 0.0045f && tiles[k] == TILE_GRASS) tiles[k] = TILE_TREE;
            }
        }

        // ---- boulders --------------------------------------------------------
        // rock fields instead of hundreds of single marbles scattered evenly over
        // the map: a few outcrops of two to eight boulders each
        for (int blob = 0; blob < 80; blob++)
        {
            int cx = rng.integers(6, w - 6);
            int cy = rng.integers(6, d - 6);
            int n = rng.integers(2, 9);
            for (int b = 0; b < n; b++)
            {
                int rx = cx + rng.integers(-4, 5);
                int rz = cy + rng.integers(-4, 5);
                if (2 <= rx && rx < w - 2 && 2 <= rz && rz < d - 2
                    && tiles[rz * w + rx] == TILE_GRASS)
                    tiles[rz * w + rx] = TILE_ROCK;
            }
        }

        // keep the asphalt and the town squares clear of obstacles: a trunk in
        // the verge stops a hull, and the square in the middle of a town is where
        // a mech comes down when the town is a forward spawn point
        {
            var keep_a = _dilate(clear, w, d, 1);
            var keep_b = _dilate(lane_road, w, d, 3);
            for (int k = 0; k < tiles.Length; k++)
            {
                if (!(keep_a[k] || keep_b[k])) continue;
                byte t = tiles[k];
                if (t == TILE_TREE || t == TILE_FOREST || t == TILE_ROCK)
                    tiles[k] = TILE_GRASS;
            }
        }
        for (int k = 0; k < road.Length; k++) if (road[k]) tiles[k] = TILE_ROAD;
        for (int k = 0; k < tiles.Length; k++)
            if (tiles[k] != TILE_BUILDING) walls[k] = 0;

        // ---- world space lane way points -------------------------------------
        var lanes = new List<List<(float x, float z)>>();
        foreach (var lane in LANE_TILES)
        {
            var pts = new List<(float x, float z)>();
            for (int s = 0; s + 1 < lane.Length; s++)
            {
                float ax = lane[s].x, ay = lane[s].y;
                float bx = lane[s + 1].x, by = lane[s + 1].y;
                float seg = MathF.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay)) * TILE;
                int n = Math.Max(2, (int)(seg / 60f));
                for (int k = 0; k < n; k++)
                {
                    float t = (float)k / n;
                    pts.Add(((ax + (bx - ax) * t) * TILE, (ay + (by - ay) * t) * TILE));
                }
            }
            pts.Add((lane[^1].x * TILE, lane[^1].y * TILE));
            lanes.Add(pts);
        }

        var bases = new Dictionary<int, (float x, float z)>();
        foreach (var (fac, bt) in BASE_TILES) bases[fac] = (bt.x * TILE, bt.y * TILE);
        return new Terrain(height, tiles, lanes, towns, bases, roads, walls, w, d);
    }

    // =====================================================================
    // weapon range: a 4 km test map with a 200 m hill on the west side and open
    // plains on the east, so every weapon can be judged against both a
    // mountainside and flat ground from one vantage point
    // =====================================================================
    public const int RANGE_TILES = 250;                 // 250 x 16 m = 4 km square
    public static readonly (float x, float z) RANGE_SPAWN = (1900f, 3050f);   // south centre
    public static readonly (float x, float z, float peak) RANGE_HILL = (900f, 1100f, 200f);

    // APC posts: static ones hold a spot, patrols drive back and forth along x
    public static readonly RangePost[] RANGE_POSTS =
    {
        new(950f, 1100f, "HILL TOP"),
        new(1200f, 1450f, "HILL SLOPE"),
        new(700f, 1750f, "HILL WEST"),
        new(1500f, 800f, "HILL RIDGE"),
        new(2150f, 2750f, "NEAR"),
        new(2450f, 1500f, "PLAIN MID"),
        new(3000f, 2050f, "PLAIN EAST"),
        new(2500f, 2450f, "PLAIN SOUTH"),
        new(3400f, 1200f, "PLAIN FAR"),
        new(3350f, 2650f, "PLAIN SE"),
        new(2500f, 1500f, "PATROL MID", 2100f, 2900f),
        new(2900f, 2300f, "PATROL EAST", 2500f, 3300f),
    };

    // a low spur south of the main hill and a gentle shoulder north of it
    private static readonly (float x, float z, float peak, float radius)[] _RANGE_SPURS =
    {
        (600f, 2100f, 45f, 520f),
        (1450f, 420f, 70f, 600f),
    };

    /// <summary>Flat firing range: one big hill NW, plains everywhere else, no
    /// cover.</summary>
    public static Terrain generate_range(int seed = 7)
    {
        var rng = new Rng(seed);
        int n = RANGE_TILES;
        var height = new float[n * n];

        float hx = RANGE_HILL.x, hz = RANGE_HILL.z, peak = RANGE_HILL.peak;
        for (int j = 0; j < n; j++)
        {
            for (int i = 0; i < n; i++)
            {
                float xm = i * TILE;
                float zm = j * TILE;
                float dx = xm - hx, dz = zm - hz;
                float r = MathF.Sqrt(dx * dx + dz * dz);
                // a wobbly radius keeps the hill from being a perfect cone, and
                // the slopes are the same continuous ground the battle map has
                float ang = MathF.Atan2(dz, dx);
                float wobble = 1f + 0.16f * MathF.Sin(3f * ang + 0.7f)
                                  + 0.09f * MathF.Sin(5f * ang - 1.3f);
                float radius = 980f * wobble;
                float k = Math.Clamp(1f - r / radius, 0f, 1f);
                k = k * k * (3f - 2f * k);              // smoothstep falloff
                height[j * n + i] = peak * k;
            }
        }

        foreach (var (sx, sz, sp, sr) in _RANGE_SPURS)
        {
            for (int j = 0; j < n; j++)
            {
                for (int i = 0; i < n; i++)
                {
                    float dx = i * TILE - sx, dz = j * TILE - sz;
                    float sk = Math.Clamp(1f - MathF.Sqrt(dx * dx + dz * dz) / sr, 0f, 1f);
                    height[j * n + i] += sp * (sk * sk * (3f - 2f * sk));
                }
            }
        }

        // plains: a couple of metres of undulation so the ground is not a mirror
        var plains = _fbm(rng, n, n, 4);
        for (int j = 0; j < n; j++)
        {
            for (int i = 0; i < n; i++)
            {
                int k = j * n + i;
                float blend = Math.Clamp((i * TILE - 1500f) / 1600f, 0f, 1f);
                height[k] += plains[k] * 9f * (0.25f + 0.75f * blend);
            }
        }
        height = _blur3(_blur3(height, n, n), n, n);
        _shape_ceiling(height, n, n, Defs.MAX_TERRAIN_GRADIENT);

        var tiles = new byte[n * n];                    // grass everywhere
        var bases = new Dictionary<int, (float x, float z)>
        {
            { 0, RANGE_SPAWN },
            { 1, RANGE_SPAWN },
        };
        return new Terrain(height, tiles, new List<List<(float x, float z)>>(),
                           new List<Town>(), bases, null, null, n, n);
    }
}
