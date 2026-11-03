// The simulation's random numbers.
//
// The Python backend uses two generators: `random.Random(2024)` on the world
// (`world.rng`, the battle's draws: spawn jitter, AI loadouts, name pools,
// spread) and `np.random.default_rng(seed)` inside terrain generation.  This is
// one class with the union of the method names they expose, wrapped around
// `System.Random` and handing back `float` - the simulation is float32
// throughout (see `Vec3.cs`).
//
// **The numbers are not the same ones Python draws, and they do not have to
// be.**  A different seed stream produces a different but equally valid battle
// - the rules, the timings and the probabilities are what has to match, and
// they do: the *order* of the draws inside a loop is part of the simulation and
// every system must keep it (see `doc_backend_dotnet/01_porting_rules.md`).

namespace Mech.Server;

public sealed class Rng
{
    private readonly Random _r;

    public Rng(int seed) => _r = new Random(seed);

    /// <summary>`random.random()`: [0, 1).</summary>
    public float random() => (float)_r.NextDouble();

    /// <summary>`rng.uniform(a, b)` (and numpy's `uniform`).</summary>
    public float uniform(float a, float b) => a + (b - a) * (float)_r.NextDouble();

    /// <summary>`rng.integers(a, b)`: [a, b) - numpy's half-open interval.</summary>
    public int integers(int a, int b) => b <= a ? a : _r.Next(a, b);

    /// <summary>`rng.randint(a, b)`: [a, b], both ends included.</summary>
    public int randint(int a, int b) => b < a ? a : _r.Next(a, b + 1);

    /// <summary>`rng.random()` gaussian, mean 0 sigma 1 (`rng.standard_normal`).</summary>
    public float gauss()
    {
        // Box-Muller: numpy's ziggurat draws different numbers anyway.
        double u1 = 1.0 - _r.NextDouble();          // (0, 1]
        double u2 = _r.NextDouble();
        return (float)(Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2));
    }

    public float gauss(float mu, float sigma) => mu + sigma * gauss();

    /// <summary>`rng.choice(seq)` / `rng.choice_i(n)`.</summary>
    public T choice<T>(IReadOnlyList<T> seq) => seq[_r.Next(seq.Count)];

    public int choice_i(int n) => _r.Next(n);

    /// <summary>In-place Fisher-Yates, the same contract as `random.shuffle`.</summary>
    public void shuffle<T>(IList<T> seq)
    {
        for (int k = seq.Count - 1; k > 0; k--)
        {
            int j = _r.Next(k + 1);
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
            int j = _r.Next(pool.Count);
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
