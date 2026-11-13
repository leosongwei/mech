// The ground's contract, server side: `tests/cases/height.json` is a tiny
// heightfield and a list of points, and every point has a height written into
// the file.  The client answers the same table (`tests/frontend/height/`), so a
// wrong diagonal, a bilinear blend or a terrace in either copy shows up here
// without the two hosts having to agree with each other first.
//
// This is the surface a round bursts on and a hull stands on: `height_at` is
// read by movement, by `_march`, by `structure_hit` and by the spawn path, and
// the client's `heightAt` is read by the reticle and by the ballistic
// prediction - so "the two are the same function" is the whole point of the file.

using System.Text.Json;

using Mech.Server;

namespace Mech.Tests;

public static class HeightTests
{
    private sealed class Table
    {
        public int w, h;
        public float tile;
        public double tolerance;
        public float[] heights = Array.Empty<float>();
        public List<(string id, float x, float z, float expected)> cases = new();
    }

    private static readonly Table _t = Load();

    private static Table Load()
    {
        string path = Find();
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var r = doc.RootElement;
        var t = new Table
        {
            w = r.GetProperty("w").GetInt32(),
            h = r.GetProperty("h").GetInt32(),
            tile = r.GetProperty("tile").GetSingle(),
            tolerance = r.GetProperty("tolerance").GetDouble(),
        };
        var rows = r.GetProperty("heights");
        t.heights = new float[t.w * t.h];
        int n = 0;
        foreach (var row in rows.EnumerateArray())
            foreach (var v in row.EnumerateArray())
                t.heights[n++] = v.GetSingle();
        Assert.True(n == t.w * t.h, $"height.json: {n} vertices, expected {t.w * t.h}");
        foreach (var c in r.GetProperty("cases").EnumerateArray())
            t.cases.Add((c.GetProperty("id").GetString() ?? "",
                         c.GetProperty("x").GetSingle(),
                         c.GetProperty("z").GetSingle(),
                         c.GetProperty("h").GetSingle()));
        return t;
    }

    /// <summary>The case table, found from a run anywhere in the tree (the same
    /// walk `Program.cs` does for the fire control's).</summary>
    private static string Find()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "tests", "cases", "height.json");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException("tests/cases/height.json is not above " + AppContext.BaseDirectory);
    }

    private static Terrain Ground() =>
        new(_t.heights, new byte[_t.w * _t.h],
            new List<List<(float x, float z)>>(), new List<Town>(),
            new Dictionary<int, (float x, float z)>(), null, null, _t.w, _t.h);

    [Test]
    public static void Every_point_answers_the_height_the_table_wrote()
    {
        var ground = Ground();
        foreach (var (id, x, z, expected) in _t.cases)
            Assert.Close(ground.height_at(x, z), expected, _t.tolerance, id);
    }

    /// <summary>The rule is the one the client draws: the tile is two flat
    /// triangles, so four samples inside one tile are coplanar and the diagonal
    /// is the crease.  A bilinear blend answers a different number at
    /// `tri_b_just_past_diag` - which is what makes that row worth having.</summary>
    [Test]
    public static void The_facet_is_a_plane_and_the_diagonal_is_the_crease()
    {
        var ground = Ground();
        // tile (0,0) is a = 0 (i,j), b = 10 (i+1,j), c = 20 (i,j+1), d = 40
        // a plane is affine, so the middle of two points on it is the mean
        float left = ground.height_at(2f, 4f);
        float right = ground.height_at(6f, 4f);
        Assert.Close(ground.height_at(4f, 4f), (left + right) * 0.5f, 1e-4, "triangle a is a plane");
        // and it is not the bilinear one: the mid point of the diagonal is where
        // the two blends disagree most inside triangle a
        float bilinear = (0f * 0.25f + 10f * 0.25f) * 0.5f + (20f * 0.25f + 40f * 0.25f) * 0.5f;
        Assert.True(MathF.Abs(ground.height_at(4f, 4f) - bilinear) > 1e-3f,
                    "triangle a answers the bilinear blend: the facet rule is not in place");
    }

    /// <summary>The wire rounds a vertex to `H_UNIT` steps, so a height the world
    /// stores has to come back within half a step - otherwise a hull's own
    /// ground and the client's copy of it drift apart by more than the mesh can
    /// show.</summary>
    [Test]
    public static void The_wire_keeps_a_height_within_half_a_step()
    {
        float unit = Netcode.H_UNIT;
        foreach (float v in new[] { 0f, 0.024f, 0.026f, 7.5f, 12.34f, 199.999f, 655.35f })
        {
            int q = Math.Clamp((int)MathF.Round(v / unit), 0, 65535);
            float back = q * unit;
            Assert.AtMost(MathF.Abs(back - v), unit * 0.5f + 1e-5f, $"round trip of {v} m");
        }
        // and the ceiling is a real ceiling: 65535 steps of 0.05 m is over 3 km,
        // which the map never reaches but a mountain someday might
        Assert.True(65535 * unit > 3200f, "H_UNIT * 65535 is below 3.2 km");
    }
}
