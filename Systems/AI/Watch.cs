// The aperture: where a crew is looking, which is not where its gun is laid.
//
// Two systems and one axis.  `run` is the only writer of the *intent*
// (`gaze_cmd`) and of the two clocks that decide it; `turn` is the only writer of
// the *bearing itself* (`gaze_yaw`) and has one law: 150 deg/s, no platform
// branches.  A gun is a turret on a hull and a hull is a driver's problem - the
// sight head is neither, which is why it can sweep a lane while the machine walks
// straight and its turret keeps pointing at the last thing it was shooting.
//
// What the aperture is *for* is `Systems/AI/Sense.cs`: a candidate has to sit
// within `CONE` of `gaze_yaw` to be found at all.  Tracking is not gated - a hull
// the crew has once seen is carried by the belief for `Crew.MEMORY` seconds the way
// a missile carries its last lock - so nothing about the stop-and-shoot behaviour
// the lane crews already have changes (`Systems/AI/Strategies/Front.cs`).
//
// Two things can hold the aperture's attention (`AiGaze.ENGAGE`):
//
//   * a target in hand - then the aperture sits on it, which keeps it inside the
//     window it was found through;
//   * fire that came too close (`Systems/AI/Suppress.cs` writes `flinch`) - then
//     the aperture turns to the bearing it came from, but only when there is no
//     target in hand.  A round going past does not make a crew drop the hull it is
//     already fighting; what changes *who* it fights is `ThreatScore`, not the
//     alarm.
//
// Off the clock for 2 s and the guns stop (that is `Crew.MEMORY`); off it for 8 s
// and the attention gives up and starts searching again.  The two clocks are
// deliberately different: "I know where it was" and "I am still looking at it" are
// different beliefs, and only the second one belongs to an eye.

namespace Mech.Server.Systems.AI;

public sealed class WatchColumns : CrewColumns
{
    // the search sectors are measured off the direction the machine is *driving*,
    // which for a mech is its legs: its hull chases the sight (`Systems/Gunnery.cs`),
    // so a pattern hung on `yaw` would chase its own tail
    public float[] leg_yaw = null!;

    private static readonly string[] watch_names =
        Components.Cat(Components.Cat(names, "leg_yaw"), AiTags.CREW);

    public static readonly string[] OnWatch = watch_names;

    public WatchColumns(Group g) : base(g) { }

    public override string[] Names => watch_names;

    protected override void bind(Group g) { base.bind(g); leg_yaw = g.f("leg_yaw"); }

    /// <summary>rad; the direction the sectors hang off: a walker's legs, or a
    /// carrier's own heading (its `yaw` belongs to its driver, and
    /// `Integrate.turn_legs` skips a wheeled hull).</summary>
    public float heading(int i, CrewRow r, bool wheeled) => wheeled ? r.yaw : leg_yaw[i];
}

public static class Watch
{
    /// <summary>rad/s the sight head turns: 150 deg/s.  One law for every platform -
    /// a mech's roof sight, a carrier's sensor and a man's eye all turn at this, and
    /// none of them is the turret.</summary>
    public const float RATE = 150f * MathF.PI / 180f;

    /// <summary>rad; the half angle of the window, 40 deg.  The same figure the
    /// laser designator can hold a line in (`Defs.BEAM_CONE`): a crew can see
    /// anywhere it could steer a beam, and nowhere else.</summary>
    public const float CONE = Defs.BEAM_CONE;

    /// <summary>s the attention stays on a hull after it was last really seen.</summary>
    public const float HOLD = 8.0f;

    /// <summary>s each search sector is given.</summary>
    public const float STEP = 3.0f;

    /// <summary>rad off the heading, in the order they are visited: right 90, the
    /// nose, left 90, the nose.  One cycle is 12 s and covers 90 deg each side - the
    /// rear is never swept, so a hull that comes up from behind is not seen until it
    /// fires (that is the shape the design asks for: a flank is possible, and the
    /// alarm is what ends it).</summary>
    public static readonly float[] PATTERN =
    {
        90f * MathF.PI / 180f, 0f, -90f * MathF.PI / 180f, 0f,
    };

    /// <summary>The attention, and the bearing it wants: one tick of the crew's
    /// state machine.  Runs before `Sense`, so the aperture it writes into this tick
    /// is the one `Sense` looks through, and reads the belief `Sense` left last tick
    /// (`belief_age == 0` means "seen in the last tick" until `Sense` walks it on).</summary>
    public static void run(World world, float dt)
    {
        foreach (var g in world.ecs.groups(WatchColumns.OnWatch))
        {
            var c = new WatchColumns(g);
            bool wheeled = g.has("vehicle");
            foreach (var r in c)
            {
                bool known = r.has_belief;
                // "seen" is `Sense`'s own word for it (`Policy.situation`): the belief
                // is the truth as of this tick's copy, and once the sight is cut the
                // age starts climbing
                bool fresh = known && r.belief_age == 0f;
                bool hit = r.flinch_t > 0f;

                // ---- 1. the clock ------------------------------------------
                if (fresh || hit) r.hold = HOLD;
                else if (r.hold > 0f) r.hold = MathF.Max(0f, r.hold - dt);
                bool engage = known || r.hold > 0f;
                r.gaze_state = engage ? AiGaze.ENGAGE : AiGaze.SEARCH;
                if (engage) r.search_clock = 0f;
                else r.search_clock += dt;

                // ---- 2. the intent -----------------------------------------
                // A target in hand owns the aperture: it is the thing worth fighting
                // and the window it was found through has to keep covering it.
                if (known)
                {
                    var p = r.belief_pos;
                    r.gaze_cmd = MathF.Atan2(p.z - r.eye.z, p.x - r.eye.x);
                }
                else if (hit)
                {
                    r.gaze_cmd = r.flinch_yaw;
                }
                else if (!engage)
                {
                    int sector = (int)(r.search_clock / STEP) % PATTERN.Length;
                    r.gaze_cmd = MathUtil.wrap_pi(c.heading(r.index, r, wheeled) + PATTERN[sector]);
                }
                // else: it was here and it is gone - the aperture holds the bearing it
                // was left on until `HOLD` runs out (no write is the intent)
            }
        }
    }

    /// <summary>Turn the head onto the intent: the whole of "how fast".  No platform
    /// has anything to say about it (see the file header).</summary>
    public static void turn(World world, float dt)
    {
        float step = RATE * dt;
        foreach (var g in world.ecs.groups(WatchColumns.OnWatch))
        {
            var c = new WatchColumns(g);
            foreach (var r in c)
                r.gaze = MathUtil.turn_toward(r.gaze, r.gaze_cmd, step);
        }
    }
}
