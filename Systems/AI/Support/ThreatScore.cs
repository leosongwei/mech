// What a crew cares about: which of the hulls it can see is worth working first.
//
// One opinion, in one place, so the two systems that need it agree: `Sense` ranks
// the candidates it is about to look at by it (`Systems/AI/Sense.cs`) and the
// mission policies can ask the same question without a second table.  The answer
// is a *product of terms* - a platform weight and a range falloff - so a future
// term (being shot at, closing rate, what is left of its hull, what it is carrying)
// is one more factor and nothing else in the file moves.
//
// The weights are the design's platform order: a mech is worth two carriers and ten
// riflemen.  The falloff is normalised to the *watching* crew's own sight radius,
// so the same table serves a rifleman at 800 m and a mech at 3500 m - an APC and a
// mech see the same relative geometry, not the same metres.
//
// The chosen weights survive the falloff inside the whole sight radius: a 1 : 0.5
// weight gap needs more than twice the distance to be overturned, and 1 : 0.1 needs
// ten times, which a `1/(1+(x/0.7)^2)` that never falls below 0.33 cannot produce.
// So "mech > APC > infantry" is a stable rule and distance only breaks ties inside
// one type.  That is a chosen consequence, not an accident: to let a near rifleman
// outbid a far mech, shrink the weight gap or raise `HALF_AT`.

namespace Mech.Server.Systems.AI;

public static class ThreatScore
{
    /// <summary>Normalised distance at which the range term is 0.5: 0.7 is "70 % of
    /// the sight radius".  1 at the muzzle, 0.33 at the edge of the radius.</summary>
    public const float HALF_AT = 0.7f;

    /// <summary>How much better a new candidate must be before a crew drops the
    /// hull it is already working: without it two hulls of equal score swap the
    /// gaze back and forth every scan.</summary>
    public const float SWITCH = 1.25f;

    /// <summary>Platform weight by `etype`, 0 = not worth working.  Every type a
    /// crew may fight is registered here: a new one that is missing reads 0 and is
    /// never picked, which is exactly the bug this table exists to make visible.</summary>
    public static readonly float[] W = _weights();

    private const int MAX_ETYPE = 16;

    private static float[] _weights()
    {
        var w = new float[MAX_ETYPE];
        w[Defs.ETYPE_MECH] = 1.0f;
        w[Defs.ETYPE_APC] = 0.5f;
        w[Defs.ETYPE_INF_ROCKET] = 0.25f;    // a rocket can open armour
        w[Defs.ETYPE_INF_RIFLE] = 0.1f;
        // the enemy command post is the win condition: a crew that can see it
        // shoots it, which is what the nearest-visible rule did before this table
        w[Defs.ETYPE_BASE] = 1.0f;
        return w;
    }

    /// <summary>How much this hull at this range is worth, from 0 to the platform
    /// weight.  `dist` is eye to hull centre and `sight` the watching crew's own
    /// pick-out radius - the same two quantities `Sense` gates on, so the ranking
    /// and the gate answer the same question.</summary>
    public static float of(int etype, float dist, float sight)
    {
        if ((uint)etype >= (uint)W.Length) return 0f;
        float weight = W[etype];
        if (weight <= 0f) return 0f;
        float x = dist / MathF.Max(sight, 1f);
        float k = x / HALF_AT;
        return weight / (1f + k * k);
    }
}
