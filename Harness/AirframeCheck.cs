// `--check airframe`: do the seven airframe stages add up to the airframe the fire
// control integrates?
//
// One round of each winged kind is fired into the range's open plain and flown by the
// world.  On every tick the harness reads the state the round had *before* the tick
// (position, velocity, age) - and, for a guided round, the demand this tick's guidance
// wrote into the round's own `a_dir` / `a_want` - and flies the same tick with the terms
// called back to back (`Flight.airframe`, `Flight.slide`).  What it prints is how far
// the two readings of that one tick ended up apart.
//
// This is not a baseline comparison: the two sides are one law written twice - the seven
// passes that file their intermediate values in components, and the six terms called in
// the order `Core/Flight.cs` gives them - so the reading answers "are they still the
// same law" and nothing else.  The fire control's integrator *is* that second side, so
// what this row really guards is "the round lands where the barrel was aimed".
//
// The tolerance is 1 mm for a whole tick, three orders of magnitude below the 30 mm
// round's own dispersion and below any hit sphere in the game.
//
// The two unguided rows fly *straight up* and with nothing asked of the wing: a 600 m/s
// round leaves the range's 4 km in a few seconds, and an unguided round is the airframe
// with `a_dir` zero.  The top attack's row is the one that carries a lift demand - its
// guidance's output is read back from the round - so between them the rows cover the
// four forces with and without a pull.  The crosswind a beam rider carries is written
// into the accumulator rather than into the demand, so it is not part of either side of
// this comparison: `--check beam` is what measures it.
//
//   ./dotnet.sh run -- --check airframe

using Mech.Server;
using Mech.Server.Systems;

namespace Mech.Server.Harness;

public static class AirframeCheck
{
    private const float DT = 1f / 30f;
    private const float DURATION = 30f;         // s of flight compared per row
    private const float TOL_POS = 1e-3f;        // m
    private const float TOL_VEL = 1e-3f;        // m/s
    private const float UP = MathF.PI * 0.5f;   // a launch with no guidance climbs

    /// <summary>What the tick is compared against: `Flight.slide` for a round the wing
    /// is not steering, `Flight.airframe` with the demand read back off the round for
    /// one that is.</summary>
    private enum Law { Slide, Wing }

    private sealed class Row
    {
        public float dpos, dvel;                // worst deviation over the flight
        public float seconds;                   // s the round was both flying and compared
        public string note = "";
    }

    public static int run()
    {
        Console.WriteLine("round        reference  seconds   max |dpos| m   max |dvel| m/s  note");
        int bad = 0;
        // the range's open plain, a kilometre clear of the missile-defence post (whose
        // pass is off anyway) - a fired round is measured here, not in a battle
        var launcher = new Vec3(2000f, 0f, 800f);
        Report("rocket", Law.Slide, Defs.WID_ROCKET, Defs.PROJ_ROCKET,
               launcher, dist: 0f, climb: UP, ref bad);
        Report("saclos", Law.Slide, Defs.WID_SACLOS, Defs.PROJ_SACLOS,
               launcher, dist: 0f, climb: UP, ref bad);
        Report("topattack", Law.Wing, Defs.WID_TOPATTACK, Defs.PROJ_TOP,
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

        var row = new Row();
        for (int t = 0; (float)t * DT < DURATION; t++)
        {
            var (p0, v0, age0) = State(e, round, out bool alive);
            if (!alive) break;
            world.update(DT);
            var (p1, v1, _) = State(e, round, out alive);
            // a round that has run into something, run out of battery or been shot at
            // stops where it stands, and the tick it stopped in is not a leg to compare
            if (!alive || !Flights.flying(Hit_t(e, round)))
            {
                row.note = $"stopped at {t * DT:0.0} s";
                break;
            }
            var want = Vec3.zero;
            if (law == Law.Wing)
            {
                var dir = Demand(e, round, out float mag);
                if (mag == 0f)
                {
                    row.note = "no demand to compare against";
                    break;
                }
                want = Flight.airframe(v0, age0, wd, DT, dir, mag);
            }
            else
            {
                want = Flight.slide(v0, age0, wd, DT);
            }
            var wp = new Vec3(p0.x + want.x * DT, p0.y + want.y * DT, p0.z + want.z * DT);
            row.dpos = MathF.Max(row.dpos, (p1 - wp).len);
            row.dvel = MathF.Max(row.dvel, (v1 - want).len);
            row.seconds += DT;
        }
        bool ok = row.seconds > 1f && row.dpos <= TOL_POS && row.dvel <= TOL_VEL;
        if (!ok) bad++;
        Console.WriteLine($"{label,-12} {law,-9} {row.seconds,7:0.0}   {row.dpos,12:E2}   "
                          + $"{row.dvel,14:E2}  {row.note}"
                          + (ok ? "" : "  <-- out of tolerance"));
    }

    /// <summary>The demand this tick's guidance wrote into the round: the direction the
    /// wing is asked to pull square to the flight path, and how hard.</summary>
    private static Vec3 Demand(ECS e, int round, out float mag)
    {
        int row = e.row_of(round);
        var a = e.arch_of(round)!;
        mag = a.f("a_want")[row];
        return new Vec3(a.f("a_dir")[row * 3], a.f("a_dir")[row * 3 + 1],
                        a.f("a_dir")[row * 3 + 2]);
    }

    /// <summary>The round's state as the flight stages see it: position, velocity, and
    /// the age it had before this tick's countdown, plus the record (a contact at t=0
    /// means this tick's leg ended in something).</summary>
    private static (Vec3 pos, Vec3 vel, float age) State(ECS e, int round, out bool alive)
    {
        alive = e.is_alive(round);
        if (!alive) return (Vec3.zero, Vec3.zero, 0f);
        int row = e.row_of(round);
        var a = e.arch_of(round)!;
        var pos = new Vec3(a.f("pos")[row * 3], a.f("pos")[row * 3 + 1],
                           a.f("pos")[row * 3 + 2]);
        var vel = new Vec3(a.f("vel")[row * 3], a.f("vel")[row * 3 + 1],
                           a.f("vel")[row * 3 + 2]);
        return (pos, vel, a.f("proj_age")[row]);
    }

    /// <summary>The record, as `Flights.flying` reads it.</summary>
    private static float Hit_t(ECS e, int round)
    {
        int row = e.row_of(round);
        return e.arch_of(round)!.f("first_hit_t")[row];
    }
}
