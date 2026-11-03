// Uniform spatial grid over the units: a CSR of square cells.
//
// The battlefield is cut into 128 m cells and every unit that moves *or* can be
// fought is filed under the one it stands in - the command base never moves, but
// it is a target.  `start` says where a cell's block of `items` begins, so
// "which units are near here" is two array lookups instead of a pass over the
// whole field.
//
//     cell 0    cell 1         cell 2   cell 3
//     ┌──┐   ┌───────────────┐   ┌──┐   ┌──┐
//     │  │   │ 17  42  103   │   │8 │   │  │
//     └──┘   └───────────────┘   └──┘   └──┘
//              ↑ start[1]        ↑ start[2]
//
//     items = [17, 42, 103, 8, ...]
//
// A grid, not a tree or a hash: units move every tick, so *rebuilding* is the
// cost that matters and the layout is close to uniform.  The build walks the
// archetypes (whole buffers, no id indirection) and counting-sorts the cell keys
// - which is numpy's stable argsort here, done in two passes and with no
// comparison function - and it buys every query a handful of candidate ids
// instead of a scan of the whole battle.
//
// The grid is a **snapshot of one tick**: it keeps its own copy of the positions
// alongside the ids, so a query needs no ECS access at all.  Rebuild it after
// anything has moved (`World.update` does, at the top of the tick) - a stale copy
// would aim the AI at where a hull used to be.

namespace Mech.Server;

public sealed class Grid
{
    public const float CELL = 128f;      // metres; ~the spacing of a battle on 12 km

    public float cell;
    public float inv;
    public int cw, cd;
    public int[] start;                  // cw*cd + 1
    public int[] items = Array.Empty<int>();       // ids, grouped by cell
    public float[] pos = Array.Empty<float>();     // their positions (x,y,z), same order
    public int n;

    private int[] _count = Array.Empty<int>();
    private int[] _cursor = Array.Empty<int>();

    public Grid(float width_m, float depth_m, float cell = CELL)
    {
        this.cell = cell;
        inv = 1f / this.cell;
        cw = Math.Max(1, (int)(width_m * inv) + 1);
        cd = Math.Max(1, (int)(depth_m * inv) + 1);
        start = new int[cw * cd + 1];
        _count = new int[cw * cd];
        _cursor = new int[cw * cd];
    }

    // -- building ---------------------------------------------------------
    /// <summary>Refile every entity carrying one of `tags` under the cell it
    /// stands in.
    ///
    /// Two sets, not one: the movers and the things that can be shot.  The
    /// command base is the one `combat` hull that never moves, and a grid of
    /// `moves` alone left it out - so the AI drove at the thing its lane ends at
    /// and never saw it as a target.  A hull carrying both tags is filed once
    /// (the archetype is the same object under either query).</summary>
    public void build(ECS ecs, params string[] tags)
    {
        var groups = new List<Group>();
        var seen = new HashSet<Archetype>();
        foreach (var tag in tags)
        {
            foreach (var g in ecs.groups(tag, "pos"))
            {
                if (seen.Add(g.arch)) groups.Add(g);
            }
        }
        int total = 0;
        foreach (var g in groups) total += g.n;
        n = total;
        if (items.Length < n)
        {
            items = new int[Math.Max(n, Math.Max(64, items.Length * 2))];
            pos = new float[items.Length * 3];
        }
        if (n == 0)
        {
            Array.Clear(start);
            return;
        }
        int w = 0;
        foreach (var g in groups)
        {
            var p = g.f("pos");
            var ids = g.ids;
            for (int r = 0; r < g.n; r++)
            {
                items[w] = ids[r];
                pos[w * 3] = p[r * 3];
                pos[w * 3 + 1] = p[r * 3 + 1];
                pos[w * 3 + 2] = p[r * 3 + 2];
                w++;
            }
        }

        int ncells = cw * cd;
        Array.Clear(_count);
        if (_keysScratch.Length < n) _keysScratch = new int[Math.Max(n, 64)];
        var keys = _keysScratch;
        for (int k = 0; k < n; k++)
        {
            int ci = Math.Clamp((int)(pos[k * 3] * inv), 0, cw - 1);
            int cj = Math.Clamp((int)(pos[k * 3 + 2] * inv), 0, cd - 1);
            int key = cj * cw + ci;
            keys[k] = key;
            _count[key]++;
        }
        int acc = 0;
        for (int c = 0; c < ncells; c++)
        {
            start[c] = acc;
            _cursor[c] = acc;
            acc += _count[c];
        }
        start[ncells] = acc;

        // place ids/pos in input order inside their cell: a stable sort by key
        if (_idsScratch.Length < n)
        {
            _idsScratch = new int[Math.Max(n, 64)];
            _posScratch = new float[_idsScratch.Length * 3];
        }
        for (int k = 0; k < n; k++)
        {
            int at = _cursor[keys[k]]++;
            _idsScratch[at] = items[k];
            _posScratch[at * 3] = pos[k * 3];
            _posScratch[at * 3 + 1] = pos[k * 3 + 1];
            _posScratch[at * 3 + 2] = pos[k * 3 + 2];
        }
        Array.Copy(_idsScratch, items, n);
        Array.Copy(_posScratch, pos, n * 3);
    }

    private int[] _keysScratch = Array.Empty<int>();
    private int[] _idsScratch = Array.Empty<int>();
    private float[] _posScratch = Array.Empty<float>();

    // -- queries ----------------------------------------------------------
    public (int ci, int cj) cell_of(float x, float z) =>
        (Math.Clamp((int)(x * inv), 0, cw - 1), Math.Clamp((int)(z * inv), 0, cd - 1));

    /// <summary>Ids and positions of every unit in the (2r+1)² cell box around
    /// `(ci, cj)`, clipped to the map.
    ///
    /// The box is gathered in one pass: a cell's block of `items` is contiguous,
    /// so the blocks are addressed through `start` and copied end to end.  The
    /// returned `pos` is `(count*3)` floats, `ids` is `count` ids, in the grid's
    /// own order.</summary>
    public (int[] ids, float[] pos) box(int ci, int cj, int r)
    {
        int x0 = Math.Max(ci - r, 0);
        int x1 = Math.Min(ci + r, cw - 1);
        int j0 = Math.Max(cj - r, 0);
        int j1 = Math.Min(cj + r, cd - 1);
        int total = 0;
        for (int j = j0; j <= j1; j++)
            for (int i = x0; i <= x1; i++)
            {
                int c = j * cw + i;
                total += start[c + 1] - start[c];
            }
        if (total == 0) return (Array.Empty<int>(), Array.Empty<float>());
        var ids = new int[total];
        var p = new float[total * 3];
        int w = 0;
        for (int j = j0; j <= j1; j++)
            for (int i = x0; i <= x1; i++)
            {
                int c = j * cw + i;
                int s = start[c], cnt = start[c + 1] - s;
                Array.Copy(items, s, ids, w, cnt);
                Array.Copy(pos, s * 3, p, w * 3, cnt * 3);
                w += cnt;
            }
        return (ids, p);
    }
}
