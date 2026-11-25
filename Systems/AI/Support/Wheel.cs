// The alarm wheel: eighteen 20-degree slots per crew, and the only place the
// question "where should the head look" is answered from.
//
// Design and the numbers: `docs/plans/AI_alarm_wheel.md`.  Three tables say what is
// *worth looking at*, one says what has *not been looked at*:
//
//   alarm  [left s, weight]    a round that went past (1.5 s, 1.0), a burst (1.5 s, 0.5)
//   laser  [charge s, left s]  a designator holding a line on this hull
//                              (`PAINT_ARM` 0.5 s to arm, then 3 s of alarm)
//   tgt    [left s, weight]    a hull that was really seen (8 s, 0.5 x ThreatScore)
//   eye    [since looked]      the sweep's own freshness; nothing else writes it
//
// They are separate components because they have separate writers and lifetimes, and
// they are one wheel because they are all *bearings*: the fire came from where it
// came from.  Only `eye` is also read relative to the driving heading, because that
// is what "the rear is never swept" means (`Systems/AI/Watch.cs`).
//
// Two properties keep this honest.  A slot is written only through `mark`, so a
// weaker report can never overwrite a stronger one in the same slot - an autocannon
// burst walking across a hull is one slot being renewed, and a burst landing next to
// it is a weaker, different report.  And a slot's score is its weight times how much
// of its life is left, so "worth looking at" decays by itself and needs no second
// clock (`Watch.HOLD` of 1.42.0 is gone).

namespace Mech.Server.Systems.AI;

public static class Wheel
{
    /// <summary>Slots around the horizon.  18 x 20 deg: the aperture's window is
    /// +-40 deg (`Watch.CONE`), i.e. four slots, so quantising a bearing to a slot
    /// centre costs no field of view and the sweep visits twelve slots in a turn.</summary>
    public const int SLOTS = 18;

    /// <summary>rad; one slot.</summary>
    public static readonly float STEP = MathUtil.TAU / SLOTS;

    // ---- lifetimes and weights (s, and the ceiling of the score each adds) ----
    /// <summary>s a near miss or a burst keeps its slot worth looking at.</summary>
    public const float ALARM_LIFE = 1.5f;
    public const float BULLET_W = 1.0f;
    public const float BLAST_W = 0.5f;

    /// <summary>s a designator's line stays warm once it is on the hull.</summary>
    public const float LASER_LIFE = 3.0f;
    public const float LASER_W = 2.0f;

    /// <summary>s a hull that was really seen keeps its bearing.</summary>
    public const float TGT_LIFE = 8.0f;
    public const float TGT_W = 0.5f;

    // ---- the two doors the aperture's choice needs ----
    /// <summary>rad; how much better a new slot has to score before the head leaves
    /// the one it is turning to.  The same figure `ThreatScore.SWITCH` uses.</summary>
    public const float SWITCH = 1.25f;

    /// <summary>rad; "the head has arrived": one tick of travel at `Watch.RATE`.</summary>
    public const float TURN_EPS = 0.09f;

    /// <summary>Which slot a bearing falls in, `-pi..pi` mapped to `0..17`.</summary>
    public static int slot_of(float yaw) =>
        (int)MathF.Floor((MathUtil.wrap_pi(yaw) + MathF.PI) / STEP) % SLOTS;

    /// <summary>The bearing a slot stands for: its centre, `-pi + (s + 0.5) * STEP`.</summary>
    public static float centre(int slot) => -MathF.PI + (slot + 0.5f) * STEP;

    /// <summary>Slots between two slots the short way round.</summary>
    public static int dist(int a, int b)
    {
        int d = Math.Abs(a - b);
        return d < SLOTS - d ? d : SLOTS - d;
    }

    /// <summary>Does this slot lie within the front half (`+-90 deg`) of `heading`?</summary>
    public static bool in_front(int slot, float heading) =>
        MathF.Abs(MathUtil.wrap_pi(centre(slot) - heading)) <= MathF.PI * 0.5f;

    // ---- writing one slot -------------------------------------------------
    //
    // One rule for all three tables: a stronger report replaces the weight and the
    // life, a weaker one only renews the life.  "Stronger" is the weight, so a bullet
    // (1.0) is never displaced by a burst (0.5) in the same slot, while a laser (2.0)
    // displaces anything.

    public static void mark(float[] buf, int row, int slot, float life, float weight)
    {
        int o = row * (SLOTS * 2) + slot * 2;
        if (weight >= buf[o + 1])
        {
            buf[o] = life;
            buf[o + 1] = weight;
        }
        else if (buf[o] < life) buf[o] = life;
    }

    /// <summary>A designator's line has been on this hull for one more tick: add `dt`
    /// to the slot's charge and, once it is armed, put the alarm in that slot.  The
    /// charge is continuous coverage, so it is zeroed every tick by the caller rather
    /// than aged here.</summary>
    public static void paint(float[] buf, int row, int slot, float dt, float arm)
    {
        int o = row * (SLOTS * 2) + slot * 2;
        buf[o] = MathF.Min(buf[o] + dt, arm);
        if (buf[o] >= arm) mark(buf, row, slot, LASER_LIFE, LASER_W);
    }

    // ---- ageing and scoring one slot --------------------------------------

    /// <summary>Age `alarm`'s slot and return what it is worth: weight times the
    /// fraction of `ALARM_LIFE` left.  Score 0 means "nothing here any more".</summary>
    public static float alarm_of(float[] buf, int row, int slot, float dt)
    {
        int o = row * (SLOTS * 2) + slot * 2;
        float left = buf[o] = MathF.Max(0f, buf[o] - dt);
        return buf[o + 1] * MathF.Min(1f, left / ALARM_LIFE);
    }

    /// <summary>Age the laser alarm (column 1 - column 0 is this tick's charge, which
    /// the writer resets) and return its score.</summary>
    public static float laser_of(float[] buf, int row, int slot, float dt)
    {
        int o = row * (SLOTS * 2) + slot * 2 + 1;
        float left = buf[o] = MathF.Max(0f, buf[o] - dt);
        return LASER_W * MathF.Min(1f, left / LASER_LIFE);
    }

    /// <summary>Age a sighting and return its score.</summary>
    public static float tgt_of(float[] buf, int row, int slot, float dt)
    {
        int o = row * (SLOTS * 2) + slot * 2;
        float left = buf[o] = MathF.Max(0f, buf[o] - dt);
        return buf[o + 1] * MathF.Min(1f, left / TGT_LIFE);
    }
}
