// `--check airframe`: does the eleven-stage flight block fly the same law as the pure
// function?
//
// One round of each winged kind is fired into the range's open plain and flown by the
// world for thirty seconds.  On every tick the harness reads the state the round had
// *before* the tick (position, velocity, age, distance flown) and the demand the
// guidance wrote into the round's components, and steps the pure function
// (`Flight.step` / `Flight.direct` / `Flight.slide`) from the same state.  What it
// prints is how far the two readings of that one tick ended up apart.
//
// This is not a baseline comparison: the two sides are the same law written twice (the
// stages in `Systems/Flights/`, the closed form in `Core/Flight.cs`), so the reading
// answers "are they still the same law" and nothing else.  The tolerance is 1 mm over
// a whole flight - three orders of magnitude below the 30 mm round's own dispersion and
// below any hit sphere in the game.
//
// The beam rider's row flies *unilluminated*: a round with no line asks nothing of the
// wing, so this row measures the airframe's own terms at their hardest - the heaviest
// body, the longest motor burn - against `Flight.direct` with the same empty demand.
// The beam's demand and the crosswind it carries are what `--check beam` measures.
//
//   ./dotnet.sh run -- --check airframe

using Mech.Server;
using Mech.Server.Systems;

namespace Mech.Server.Harness;

public static class AirframeCheck
{
    private const float DT = 1f / 30f;
    private const float DURATION = 30f;         // s of flight compared per row
    private const float UP = MathF.PI * 0.5f;   // a launch with no guidance climbs
    private const float TOL_POS = 1e-3f;        // m
    private const float TOL_VEL = 1e-3f;        // m/s

    /// <summary>Which pure function the row is compared against.</summary>
    private enum Law { Slide, Arc, Beam }

    private sealed class Row
    {
        public float dpos, dvel;                // worst deviation over the flight
        public float seconds;                   // s the round was both flying and compared
        public string note = "";
    }

    public static int run()
    {
        Console.WriteLine("round        law     seconds   max |dpos| m   max |dvel| m/s  note");
        int bad = 0;
        // the range's open plain, a kilometre clear of the missile-defence post (whose
        // pass is off anyway) - a fired round is measured here, not in a battle
        var launcher = new Vec3(2000f, 0f, 800f);
        // the two rows with no guidance are fired straight up: an unguided round
        // does 600 m/s, so a level shot leaves the range's 4 km in a few seconds and
        // the comparison would stop at the edge of the map
        Report("rocket", Law.Slide, Defs.WID_ROCKET, Defs.PROJ_ROCKET,
               launcher, dist: 0f, climb: UP, ref bad);
        Report("saclos", Law.Beam, Defs.WID_SACLOS, Defs.PROJ_SACLOS,
               launcher, dist: 0f, climb: UP, ref bad);
        Report("topattack", Law.Arc, Defs.WID_TOPATTACK, Defs.PROJ_TOP,
               launcher, dist: 1800f, climb: 0f, ref bad);
        Console.WriteLine(bad == 0
            ? "\nairframe: the stages and the closed form are the same law"
            : "\nairframe: the stages and the closed form disagree");
        return bad;
    }

    /// <summary>Fire one round `dist` metres up-range of a parked hull (`dist` 0 = at
    /// nothing) and compare every tick it flies.</summary>
    private static void Report(string label, Law law, int wid, byte kind, Vec3 from,
                               float dist, float climb, ref int bad)
    {
        var world = new World(7, "range");
        // nothing in the range may touch the measurement: the dummies do not need to
        // observe the round and the defence post would shoot at it
        world.systems.Remove(Modes.Range.Targets.run);
        world.systems.Remove(Modes.Range.Interceptor.run);
        var e = world.ecs;
        var wd = Defs.WEAPONS[wid];

        var start = new Vec3(from.x, world.terrain.height_at(from.x, from.z) + 400f, from.z);
        int target = -1;
        if (dist > 0f)
        {
            float tx = start.x + dist;
            target = world.spawn_unit(Defs.ETYPE_APC, Defs.FACTION_RED, tx,
                                      start.z + 300f, crew: false);
        }
        float yaw = 0f;
        if (target >= 0)
        {
            var tp = e.fv("pos").get3(target);
            yaw = MathF.Atan2(tp.z - start.z, tp.x - start.x);
        }
        // the round leaves the tube doing 200 m/s along its heading, the way
        // `Weapons.fire` hands it over
        float flat = MathF.Cos(climb) * 200f;
        var vel = new Vec3(MathF.Cos(yaw) * flat, MathF.Sin(climb) * 200f,
                           MathF.Sin(yaw) * flat);
        int round = world.spawn_round(-1, Defs.FACTION_BLUE, wid, kind, start, vel,
                                      wd.life, target);
        float seed = e.fv("proj_seed")[round];

        var row = new Row();
        var last = new Vec3(0f, 0f, 0f);
        float lastAge = 0f, lastTrav = 0f;
        for (int t = 0; (float)t * DT < DURATION; t++)
        {
            var (p0, v0, age0, trav0, _) = State(e, round, out bool alive);
            if (!alive) break;
            last = p0;
            lastAge = age0;
            lastTrav = trav0;
            world.update(DT);
            var (p1, v1, _, _, hit_t) = State(e, round, out alive);
            // a round that has run into something, run out of battery or been shot at
            // stops where it stands, and the tick it stopped in is not a leg to compare
            if (!alive || !Flights.flying(hit_t))
            {
                row.note = $"stopped at {t * DT:0.0} s";
                break;
            }
            var want = Vec3.zero;
            bool comparable = true;
            switch (law)
            {
                case Law.Arc:
                    // the stare point this tick's seeker refreshed, and the velocity
                    // it walks at: what `arc_demand` was handed
                    var spot = e.fv("tgt_mem").get3(round);
                    if (spot.x == 0f && spot.y == 0f && spot.z == 0f)
                    {
                        row.note = "no lock to compare against";
                        comparable = false;
                        break;
                    }
                    want = Flight.step(p0, v0, age0, trav0, spot,
                                       e.fv("tgt_vel").get3(round), wd, DT);
                    break;
                case Law.Beam:
                    want = Flight.direct(p0, v0, age0, Vec3.zero, null, wd, DT, seed);
                    break;
                default:
                    want = Flight.slide(v0, age0, wd, DT);
                    break;
            }
            if (!comparable) break;
            var wp = new Vec3(p0.x + want.x * DT, p0.y + want.y * DT, p0.z + want.z * DT);
            row.dpos = MathF.Max(row.dpos, (p1 - wp).len);
            row.dvel = MathF.Max(row.dvel, (v1 - want).len);
            row.seconds += DT;
        }
        bool ok = row.seconds > 1f && row.dpos <= TOL_POS && row.dvel <= TOL_VEL;
        if (!ok) bad++;
        Console.WriteLine($"{label,-12} {law,-7} {row.seconds,7:0.0}   {row.dpos,12:E2}   "
                          + $"{row.dvel,14:E2}  {row.note}"
                          + (ok ? "" : "  <-- out of tolerance"));
        if (!ok && row.seconds > 1f)
            Console.WriteLine($"{"",-12} last state of the flight: {last.x:0.0}, {last.y:0.0}, "
                              + $"{last.z:0.0} at {lastAge:0.00} s / {lastTrav:0} m");
    }

    /// <summary>The round's state as the flight stages see it: position, velocity, the
    /// age it had before this tick's countdown, the distance flown, and the record (a
    /// contact at t=0 means this tick's leg ended in something).</summary>
    private static (Vec3 pos, Vec3 vel, float age, float trav, float hit_t) State(
        ECS e, int round, out bool alive)
    {
        alive = e.is_alive(round);
        if (!alive) return (Vec3.zero, Vec3.zero, 0f, 0f, 0f);
        int row = e.row_of(round);
        var a = e.arch_of(round)!;
        var pos = new Vec3(a.f("pos")[row * 3], a.f("pos")[row * 3 + 1],
                           a.f("pos")[row * 3 + 2]);
        var vel = new Vec3(a.f("vel")[row * 3], a.f("vel")[row * 3 + 1],
                           a.f("vel")[row * 3 + 2]);
        return (pos, vel, a.f("proj_age")[row], a.f("proj_trav")[row],
                a.f("first_hit_t")[row]);
    }
}
