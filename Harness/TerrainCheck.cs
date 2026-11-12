// `--check terrain`: what the ground is, and what it costs to ask.
//
// Two questions, both about the field itself rather than about a battle:
//
//   * **can anything go there?**  The *facet* slope - the gradient of the triangle
//     a hull actually stands on, which is the only slope the simulation ever
//     reads - its histogram against each chassis' own limit (`Defs.max_gradient`),
//     and the same question asked along the three lanes every 6 m: a lane that
//     crosses a face an APC cannot climb is a column that stops for ever, and that
//     is not a thing a battle census would attribute to the ground.
//   * **what does a sample cost?**  `height_at` (the hottest scalar in the server)
//     and `los` (the `_march` walk: 87.6 % of its calls are ground legs of 50 m or
//     less), measured here, in ns per call.
//
//   ./dotnet.sh run -- --check terrain
//
// `MECH_TERRAIN_MAP=/tmp/t.ppm` writes the field as a hypsometric map - low ground
// green, the middle yellow to brown, the summits white, the sun's shading
// multiplied in - for `MECH_TERRAIN_SEEDS` seeds (default 4) side by side, because
// a histogram says how steep the ground is and only the picture says whether the
// steep ground is a range or a crumpled blanket.
//
// A central difference over 16 m is *not* a facet slope: it straddles the crease
// between two facets, and where one faces along x and the next along z it reads up
// to `sqrt 2` times the steepest facet on the map (measured: 0.86 for a field
// whose steepest facet is 0.85).  So the numbers here come from the triangles.
//
// The figures are read for their magnitude and for the shape of the histogram -
// did the relief actually get steeper, did a lane get blocked - never as a
// baseline to diff.

using Mech.Server;

namespace Mech.Server.Harness;

public static class TerrainCheck
{
    public static int run()
    {
        var t0 = DateTime.UtcNow;
        var world = new World(11, "battle");
        var t = world.terrain;
        Console.WriteLine($"world built in {(DateTime.UtcNow - t0).TotalMilliseconds:0} ms: "
                          + $"{t.w}x{t.d} tiles, {t.towns.Count} towns, {t.lanes.Count} lanes");

        // ---- the facet slopes ----------------------------------------------
        // one sample per triangle (two per tile), area weighted by construction
        var buckets = new int[17];              // 0.05 each, the last is "over 0.80"
        float max = 0f, max_x = 0f, max_z = 0f;
        double sum = 0.0;
        long n = 0;
        var steep = new Dictionary<string, long>();
        foreach (var (etype, def) in Defs.UNIT_DEFS)
            if (def.speed > 0f) steep[def.name] = 0;
        var heights = new List<float>(66000);
        for (int j = 0; j + 1 < t.d; j++)
        {
            for (int i = 0; i + 1 < t.w; i++)
            {
                int k = j * t.w + i;
                float a = t.h[k], b = t.h[k + 1], c = t.h[k + t.w], d = t.h[k + t.w + 1];
                // triangle {a, b, c} and triangle {d, c, b}: the two the ground is
                float ga = MathF.Sqrt(Sq(b - a) + Sq(c - a)) / Terrain.TILE;
                float gb = MathF.Sqrt(Sq(c - d) + Sq(b - d)) / Terrain.TILE;
                for (int half = 0; half < 2; half++)
                {
                    float g = half == 0 ? ga : gb;
                    n++;
                    sum += g;
                    buckets[Math.Min(buckets.Length - 1, (int)(g / 0.05f))]++;
                    foreach (var (etype, def) in Defs.UNIT_DEFS)
                        if (def.speed > 0f && g > def.max_grad) steep[def.name]++;
                    if (g > max)
                    {
                        max = g;
                        max_x = (i + (half == 0 ? 0.33f : 0.66f)) * Terrain.TILE;
                        max_z = (j + (half == 0 ? 0.33f : 0.66f)) * Terrain.TILE;
                    }
                }
                if ((i % 60) == 8 && (j % 60) == 8) heights.Add(a);
            }
        }
        heights.Sort();
        Console.WriteLine($"\nslope (per facet): max {max:0.000} at ({max_x:0},{max_z:0}) m, "
                          + $"mean {sum / n:0.000}   [0.58 = 30 deg, 0.70 = 35, 0.80 = 39]");
        for (int b2 = 0; b2 < buckets.Length; b2++)
        {
            if (buckets[b2] == 0) continue;
            Console.Write($"  {b2 * 0.05f:0.00}-{(b2 + 1) * 0.05f:0.00}: {100.0 * buckets[b2] / n,5:0.0}%");
            if (b2 % 4 == 3) Console.WriteLine();
        }
        Console.WriteLine();
        float P(double q) => heights[Math.Clamp((int)(q * (heights.Count - 1)), 0, heights.Count - 1)];
        Console.WriteLine($"height: p5 {P(0.05):0} m, p50 {P(0.5):0} m, p95 {P(0.95):0} m, max {heights[^1]:0} m");
        // the ceiling is a relaxation, not a projection: it settles within a few
        // percent, which the chassis limits do not notice (0.58 / 0.70 / 0.80)
        Console.WriteLine($"ceiling {Defs.MAX_TERRAIN_GRADIENT:0.000}, measured max {max:0.000} "
                          + $"({(max / Defs.MAX_TERRAIN_GRADIENT - 1f) * 100f:+0;-0}%)");
        foreach (var (etype, def) in Defs.UNIT_DEFS)
        {
            if (def.speed <= 0f) continue;
            Console.WriteLine($"  {def.name,-14} max_grad {def.max_grad:0.00}: "
                              + $"{100.0 * steep[def.name] / n,5:0.0}% of the map is too steep to climb");
        }

        // ---- the lanes ------------------------------------------------------
        // every 6 m along each lane (a third of a tile, so no facet is stepped
        // over): the rise a hull has to climb, and whether the ground is blocked
        Console.WriteLine("\nlane walk (every 6 m): rise over run against the APC's limit, and blocked ground");
        for (int li = 0; li < t.lanes.Count; li++)
        {
            var lane = t.lanes[li];
            int tooSteep = 0, wall = 0, samples = 0;
            float worst = 0f, worst_at = 0f;
            for (int k = 0; k + 1 < lane.Count; k++)
            {
                var (ax, az) = lane[k];
                var (bx, bz) = lane[k + 1];
                float seg = MathF.Sqrt((bx - ax) * (bx - ax) + (bz - az) * (bz - az));
                int steps = Math.Max(1, (int)MathF.Ceiling(seg / 6f));
                float prevh = t.height_at(ax, az);
                for (int s = 1; s <= steps; s++)
                {
                    float u = (float)s / steps;
                    float x = ax + (bx - ax) * u, z = az + (bz - az) * u;
                    float run = seg / steps;
                    float hh = t.height_at(x, z);
                    samples++;
                    float g = (hh - prevh) / run;
                    if (g > worst) { worst = g; worst_at = MathF.Sqrt(x * x + z * z); }
                    if (g > Defs.APC_MAX_GRADIENT) tooSteep++;
                    if (t.blocked(x, z)) wall++;
                    prevh = hh;
                }
            }
            Console.WriteLine($"  lane {li}: {samples} samples, {tooSteep} too steep for an APC, "
                              + $"{wall} blocked, worst climb {worst:0.00} (at {worst_at:0} m out)");
        }

        // ---- what a sample costs -------------------------------------------
        var rng = new Rng(3);
        const int M = 400000;
        float acc = 0f;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int k = 0; k < M; k++)
            acc += t.height_at(rng.random() * t.w * Terrain.TILE, rng.random() * t.d * Terrain.TILE);
        sw.Stop();
        double per = sw.Elapsed.TotalMilliseconds * 1e6 / M;
        Console.WriteLine($"\nheight_at: {per:0.0} ns/call ({M} samples, {acc:0.0} m acc)");

        var eyes = new Vec3[256];
        var tgts = new Vec3[256];
        for (int k = 0; k < eyes.Length; k++)
        {
            float x = rng.random() * t.w * Terrain.TILE, z = rng.random() * t.d * Terrain.TILE;
            eyes[k] = new Vec3(x, t.height_at(x, z) + Defs.MECH_EYE, z);
            float tx = x + 800f, tz = z + 600f;
            tgts[k] = new Vec3(tx, t.height_at(tx, tz) + 2f, tz);
        }
        int clear = 0;
        sw.Restart();
        for (int k = 0; k < eyes.Length; k++) if (t.los(eyes[k], tgts[k])) clear++;
        sw.Stop();
        double los_ms = sw.Elapsed.TotalMilliseconds / eyes.Length;
        double dist = MathF.Sqrt(800f * 800f + 600f * 600f);
        Console.WriteLine($"los: {los_ms:0.000} ms/call at {dist:0} m ({clear}/{eyes.Length} clear), "
                          + $"{los_ms * 1e6 / (dist / 8f):0.0} ns per 8 m step");

        Console.WriteLine($"\nchassis: APC {Defs.APC_MAX_GRADIENT:0.00}, MECH {Defs.MECH_MAX_GRADIENT:0.00}, "
                          + $"FOOT {Defs.FOOT_MAX_GRADIENT:0.00} (rise over run)");

        // MECH_TERRAIN_MAP=/tmp/mech.pgm writes the field as a hillshade, which is
        // how the shape of the country is read: a histogram says how steep the
        // ground is, only the picture says whether the steep ground is a range.
        // MECH_TERRAIN_SEEDS (default 4) writes that many fields side by side,
        // because one map is not enough to tell a shape from a lucky draw.
        var path = Environment.GetEnvironmentVariable("MECH_TERRAIN_MAP");
        if (!string.IsNullOrEmpty(path))
        {
            int count = int.TryParse(Environment.GetEnvironmentVariable("MECH_TERRAIN_SEEDS"), out int v) ? v : 4;
            for (int s = 1; s < count; s++)
                dump(new World(11 + s, "battle").terrain, $"{path}.{s}");
            dump(t, path);
        }
        return 0;
    }

    /// <summary>The heightfield as a hypsometric map (a binary PPM): the ground
    /// tinted by height the way a relief map is - low ground green, the middle
    /// yellow to brown, the summits white - with the north-west sun's shading
    /// multiplied in, because the height alone says nothing about the shape.</summary>
    private static void dump(Terrain t, string path)
    {
        // a sun from the north-west, the direction a relief map is read in
        const float lx = -0.5f, lz = -0.5f, ly = 0.7f;
        float hmin = float.MaxValue, hmax = float.MinValue;
        foreach (var v in t.h) { hmin = MathF.Min(hmin, v); hmax = MathF.Max(hmax, v); }
        var px = new byte[t.w * t.d * 3];
        for (int j = 0; j < t.d; j++)
        {
            for (int i = 0; i < t.w; i++)
            {
                int k = j * t.w + i;
                float sx = (t.h[k + (i + 1 < t.w ? 1 : 0)] - t.h[k - (i > 0 ? 1 : 0)]) / (2f * Terrain.TILE);
                float sz = (t.h[k + (j + 1 < t.d ? t.w : 0)] - t.h[k - (j > 0 ? t.w : 0)]) / (2f * Terrain.TILE);
                float nl = MathF.Sqrt(sx * sx + 1f + sz * sz);
                float lit = (-sx * lx + ly - sz * lz) / (nl * MathF.Sqrt(lx * lx + ly * ly + lz * lz));
                lit = Math.Clamp(lit, 0.25f, 1.6f);
                float ramp = (t.h[k] - hmin) / MathF.Max(1f, hmax - hmin);
                var (r, g, b) = shade(ramp);
                px[k * 3 + 0] = (byte)Math.Clamp(r * lit, 0f, 255f);
                px[k * 3 + 1] = (byte)Math.Clamp(g * lit, 0f, 255f);
                px[k * 3 + 2] = (byte)Math.Clamp(b * lit, 0f, 255f);
            }
        }
        var head = System.Text.Encoding.ASCII.GetBytes($"P6\n{t.w} {t.d}\n255\n");
        using var fs = File.Create(path);
        fs.Write(head);
        fs.Write(px);
        Console.WriteLine($"\nwrote {path} (height {hmin:0}..{hmax:0} m)");
    }

    /// <summary>Height 0..1 as a topographic tint: water green, grass, dry
    /// yellow, brown, then the white of a summit.</summary>
    private static (float r, float g, float b) shade(float v)
    {
        // (position, r, g, b)
        float[] stops =
        {
            0.00f, 120f, 170f, 130f,
            0.25f, 110f, 170f, 100f,
            0.50f, 190f, 190f, 110f,
            0.72f, 190f, 150f, 100f,
            0.88f, 200f, 190f, 180f,
            1.00f, 255f, 255f, 255f,
        };
        for (int i = 0; i + 4 < stops.Length; i += 4)
        {
            if (v <= stops[i + 4] || i + 8 >= stops.Length)
            {
                float span = stops[i + 4] - stops[i];
                float u = span > 0f ? Math.Clamp((v - stops[i]) / span, 0f, 1f) : 1f;
                return (stops[i + 1] + (stops[i + 5] - stops[i + 1]) * u,
                        stops[i + 2] + (stops[i + 6] - stops[i + 2]) * u,
                        stops[i + 3] + (stops[i + 7] - stops[i + 3]) * u);
            }
        }
        return (255f, 255f, 255f);
    }

    private static float Sq(float v) => v * v;
}
