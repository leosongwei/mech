// The simulation's random numbers.
//
// Two generators, doing two different jobs:
//
//   * **a private seeded stream** (`new Rng(seed)`) - `Core/Terrain.cs` builds
//     the map with one.  The map is sent to every client to be mirrored and has
//     to be the same picture every time that seed is used.
//   * **one stream per thread** (`new Rng()`, i.e. `world.rng`) - the battle.
//     The tick loop and a websocket handler are different threads and both draw
//     (a `debug_spawn` rolls a yaw, a loadout and a name), and `System.Random`
//     is not safe to share between them.
//
// `MECH_SEED` seeds the thread streams from a fixed number.  That reproduces a
// run while one thread does all the drawing, which is what `--check` does (no
// socket, no second thread).
//
// The numbers are not the ones the Python backend drew, and they do not have to
// be - the rules, the timings and the probabilities are what has to match.

namespace Mech.Server;

public sealed class Rng
{
    // A private stream for world *construction* (the map): seeded, single
    // threaded, and never the simulation's.
    private readonly Random? _fixed;

    [ThreadStatic] private static Random? _tls;
    private static int _n;
    private static readonly int _seed = _EnvSeed();

    /// <summary>A private seeded stream, for world construction (the map).</summary>
    public Rng(int seed) => _fixed = new Random(seed);

    /// <summary>The battle's generator: one stream per thread.</summary>
    public Rng() { }

    private Random r => _fixed
        ?? (_tls ??= new Random(_seed + 7919 * Interlocked.Increment(ref _n)));

    private static int _EnvSeed()
    {
        var s = Environment.GetEnvironmentVariable("MECH_SEED");
        return s is not null && int.TryParse(s, out int v) ? v : Environment.TickCount;
    }

    /// <summary>`random.random()`: [0, 1).</summary>
    public float random() => (float)r.NextDouble();

    /// <summary>`rng.uniform(a, b)` (and numpy's `uniform`).</summary>
    public float uniform(float a, float b) => a + (b - a) * (float)r.NextDouble();

    /// <summary>`rng.integers(a, b)`: [a, b) - numpy's half-open interval.</summary>
    public int integers(int a, int b) => b <= a ? a : r.Next(a, b);

    /// <summary>`rng.randint(a, b)`: [a, b], both ends included.</summary>
    public int randint(int a, int b) => b < a ? a : r.Next(a, b + 1);

    /// <summary>`rng.random()` gaussian, mean 0 sigma 1 (`rng.standard_normal`).</summary>
    public float gauss()
    {
        // Box-Muller: numpy's ziggurat draws different numbers anyway.
        double u1 = 1.0 - r.NextDouble();          // (0, 1]
        double u2 = r.NextDouble();
        return (float)(Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2));
    }

    public float gauss(float mu, float sigma) => mu + sigma * gauss();

    /// <summary>`rng.choice(seq)` / `rng.choice_i(n)`.</summary>
    public T choice<T>(IReadOnlyList<T> seq) => seq[r.Next(seq.Count)];

    public int choice_i(int n) => r.Next(n);

    /// <summary>In-place Fisher-Yates, the same contract as `random.shuffle`.</summary>
    public void shuffle<T>(IList<T> seq)
    {
        for (int k = seq.Count - 1; k > 0; k--)
        {
            int j = r.Next(k + 1);
            (seq[k], seq[j]) = (seq[j], seq[k]);
        }
    }

    public T[] permutation<T>(IReadOnlyList<T> seq)
    {
        var a = seq.ToArray();
        shuffle(a);
        return a;
    }

    /// <summary>`random.sample(seq, k)`: k distinct elements, order drawn.</summary>
    public List<T> sample<T>(IReadOnlyList<T> seq, int k)
    {
        var pool = new List<T>(seq);
        var outv = new List<T>(Math.Min(k, pool.Count));
        for (int i = 0; i < k && pool.Count > 0; i++)
        {
            int j = r.Next(pool.Count);
            outv.Add(pool[j]);
            pool.RemoveAt(j);
        }
        return outv;
    }

    /// <summary>Weighted pick off a cumulative table (the AI loadout table, the
    /// name pool) - `Defs` keeps the weights, this walks them.</summary>
    public int weighted(float[] weights)
    {
        float total = 0f;
        foreach (var w in weights) total += w;
        float t = random() * total;
        for (int k = 0; k < weights.Length; k++)
        {
            t -= weights[k];
            if (t < 0f) return k;
        }
        return weights.Length - 1;
    }
}
