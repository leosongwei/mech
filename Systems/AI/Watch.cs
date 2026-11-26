// The aperture: where a crew is looking, which is not where its gun is laid.
//
// Two systems and one axis.  `run` is the only writer of the *intent* (`gaze_cmd`)
// and of the word that says why (`ai_gaze`); `turn` is the only writer of the
// *bearing itself* (`gaze_yaw`) and has one law: 150 deg/s, no platform branches.  A
// gun is a turret on a hull and a hull is a driver's problem - the sight head is
// neither, which is why it can sweep a lane while the machine walks straight and its
// turret keeps pointing at the last thing it was shooting.
//
// What the aperture is *for* is `Systems/AI/Sense.cs`: a candidate has to sit within
// `CONE` of `gaze_yaw` to be found at all.  Tracking is not gated - a hull the crew
// has once seen is carried by the belief for `Crew.MEMORY` seconds the way a missile
// carries its last lock - so nothing about the stop-and-shoot behaviour the lane
// crews already have changes (`Systems/AI/Strategies/Front.cs`).
//
// Where it looks is decided from the alarm wheel (`Systems/AI/Support/Wheel.cs`):
// eighteen 20-degree slots per crew, each carrying how much life is left of a report
// that came from that direction.  `run` ages the wheel once, scores it, and then has
// exactly five things it can say (one `AiGaze` word each, and the word is the
// diagnostic read-out of the branch that fired):
//
//   1. TARGET       a belief: the head goes on the hull it is fighting, every tick
//   2. ALARM        the wheel's best slot out-scored the current one and the head is
//                   free: turn to it
//   3. ALARM_TURN   ...it out-scored it, but the head is still on its way somewhere
//   4. ALARM_STAY   the best slot is the one the head wants already, or it is not
//                   worth the hysteresis
//   5. SWEEP        nothing on the wheel: the stalest slot of the front half
//
// Two properties come out of this and are worth saying out loud, because 1.42.0 got
// both wrong by holding one bearing and one clock:
//
//   * the search is not a pattern and has no clock to reset - it is "the slot I have
//     looked at least recently", so any interruption (a target, a near miss, a laser)
//     is resumed from where it left off;
//   * more than one direction can be hot at once.  A burst from the left and one from
//     the right both keep their slots for `Wheel.ALARM_LIFE`, and the head goes to
//     whichever scores higher - it is not the last noise that wins, and it is never
//     dragged to a bearing between the two.
//
// The one thing the wheel does not decide is *what* to shoot: that stays `ai_tgt`,
// written by `Sense`, and a sighting only leaves a bearing behind (`Wheel.tgt`).

namespace Mech.Server.Systems.AI;

public sealed class WatchColumns : CrewColumns
{
    // the search sectors of 1.42.0 hung off the *driving* heading, which for a mech
    // is its legs: its hull chases the sight (`Systems/Gunnery.cs`), so the pattern
    // moved as the gun did.  The wheel needs the same thing for a different reason -
    // "the front half" has to mean the direction the machine is going.
    public float[] leg_yaw = null!;

    // the three wheels a machine's crew owns (`Components.ALARM_COMPS`); null for a
    // man, who has no warning receiver to write to
    public float[]? alarm, laser, tgt;

    private static readonly string[] watch_names =
        Components.Cat(Components.Cat(names, "leg_yaw"), AiTags.CREW);

    public static readonly string[] OnWatch = watch_names;

    public WatchColumns(Group g) : base(g) { }

    public override string[] Names => watch_names;

    protected override void bind(Group g)
    {
        base.bind(g);
        leg_yaw = g.f("leg_yaw");
        alarm = g.has("alarm") ? g.f("alarm") : null;
        laser = g.has("laser") ? g.f("laser") : null;
        tgt = g.has("tgt") ? g.f("tgt") : null;
    }

    /// <summary>rad; the heading the sweep's front half hangs off: a walker's legs,
    /// or a carrier's own heading (its driver writes `leg_yaw` and `yaw` together,
    /// `Systems/Movement/Vehicle.cs`).</summary>
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

    /// <summary>How many slots either side of the one the head points at the window
    /// covers, and therefore how many the sweep counts as just looked at: +-40 deg
    /// over 20-deg slots.</summary>
    private const int COVER = 2;

    /// <summary>The attention, the bearing it wants, and the word that says why.
    /// Runs before `Sense`, so the aperture it writes into this tick is the one
    /// `Sense` looks through, and reads the belief `Sense` left last tick
    /// (`belief_age == 0` means "seen in the last tick" until `Sense` walks it on).</summary>
    public static void run(World world, float dt)
    {
        foreach (var g in world.ecs.groups(WatchColumns.OnWatch))
        {
            var c = new WatchColumns(g);
            bool wheeled = g.has("vehicle");
            foreach (var r in c)
            {
                int row = r.index;
                float heading = c.heading(row, r, wheeled);
                int cur = Wheel.slot_of(r.gaze_cmd);
                int seen = Wheel.slot_of(r.gaze);

                // ---- 1. one pass over the wheel ------------------------------
                // Age every slot, score it, and notice two things at once: the best
                // slot anywhere, and the stalest slot of the front half.  `eye` holds
                // the *time* the slot was last looked at, so 0 means "never" - the
                // stalest there is - and only the slot the head is *pointing at* is
                // stamped: stamping the whole window would keep the four neighbours of
                // the sweep permanently fresh and the middle of the arc unvisited.
                float best = 0f, cur_score = 0f, stalest = float.MaxValue;
                int best_slot = -1, lru = -1;
                for (int s = 0; s < Wheel.SLOTS; s++)
                {
                    int o = row * Wheel.SLOTS + s;
                    if (s == seen) c.eye[o] = world.time;

                    float sc = 0f;
                    if (c.alarm != null) sc = Wheel.alarm_of(c.alarm, row, s, dt);
                    if (c.laser != null) sc = MathF.Max(sc, Wheel.laser_of(c.laser, row, s, dt));
                    if (c.tgt != null) sc = MathF.Max(sc, Wheel.tgt_of(c.tgt, row, s, dt));
                    if (sc > best) { best = sc; best_slot = s; }
                    if (s == cur) cur_score = sc;

                    // the sweep takes the stalest slot of those the window does *not*
                    // cover - a slot the head is looking through has just been seen,
                    // and counting it again would park the sweep where it is.  On a tie
                    // (a fresh crew, whose `eye` is all zero) the slot nearest the
                    // bearing already held, so it does not open on -170 deg.
                    if (!Wheel.in_front(s, heading) || Wheel.dist(s, seen) <= COVER) continue;
                    bool better = c.eye[o] < stalest - 1e-4f;
                    bool tie = MathF.Abs(c.eye[o] - stalest) <= 1e-4f
                               && (lru < 0 || Wheel.dist(s, cur) < Wheel.dist(lru, cur));
                    if (better || tie) { stalest = c.eye[o]; lru = s; }
                }

                // ---- 2. the five branches of §4 -----------------------------
                bool arrived = MathF.Abs(MathUtil.wrap_pi(r.gaze - r.gaze_cmd)) < Wheel.TURN_EPS;

                if (r.has_belief)
                {
                    // 1. A target in hand owns the aperture.  Written every tick: the
                    // hull it is fighting is moving, so a held bearing would be lag.
                    var p = r.belief_pos;
                    r.gaze_cmd = MathF.Atan2(p.z - r.eye.z, p.x - r.eye.x);
                    r.gaze_state = AiGaze.TARGET;
                }
                else if (best > 0f && best_slot >= 0)
                {
                    // 2 / 3 / 4
                    bool other = best_slot != cur;
                    bool worth = best > cur_score * Wheel.SWITCH;
                    if (!other || !worth) r.gaze_state = AiGaze.ALARM_STAY;
                    else if (!arrived) r.gaze_state = AiGaze.ALARM_TURN;
                    else
                    {
                        r.gaze_cmd = Wheel.centre(best_slot);
                        r.gaze_state = AiGaze.ALARM;
                    }
                }
                else
                {
                    // 5. Nothing anywhere: sweep the front half, stalest first.  A turn
                    // already in flight is finished before the next slot is chosen,
                    // which is what stops a firing from both sides dragging the head
                    // to a bearing between them.
                    if (lru >= 0 && lru != cur && arrived) r.gaze_cmd = Wheel.centre(lru);
                    r.gaze_state = AiGaze.SWEEP;
                }
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
