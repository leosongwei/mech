// `--check airframe`: do the seven airframe stages add up to the arc the fire control
// integrates?
//
// An unguided round is fired into the range's open plain and flown by the world.  On
// every tick the harness reads the state the round had *before* the tick (position,
// velocity, age) and flies the same tick with `Flight.slide` - the one world-free
// integrator left, and the one `Ballistics.flight_at` solves a barrel's angle against.
// What it prints is how far the two readings of that one tick ended up apart.
//
// This is not a baseline comparison: the two sides are one law written twice (the
// passes in `Systems/Flights/Airframe.cs`, the closed form in `Core/Flight.cs`), so the
// reading answers "are they still the same law" and nothing else - and what it really
// guards is "the round lands where the barrel was aimed".  The tolerance is 1 mm for a
// whole tick, three orders of magnitude below the 30 mm round's own dispersion and below
// any hit sphere in the game.
//
// The rows are three different airframes at different points of the parameter space -
// 11.3 kg and 610 m/s, 2.25 kg and 300 m/s (the transonic term at a different speed), and
// the 45 kg beam rider - all fired *straight up* and with nothing asked of the wing: an
// unguided round is the airframe with `a_dir` zero, and a 600 m/s round leaves the
// range's 4 km in a few seconds.  What these rows cannot cover is the pull: `a_dir` and
// `a_want` exist only inside the world, so a guided round has no world-free twin left to
// be compared against.  The guided rounds are measured where they land instead
// (`--check beam`, `--check hit`, `--check intercept`).
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

    private sealed class Row
    {
        public float dpos, dvel;                // worst deviation over the flight
        public float seconds;                   // s the round was both flying and compared
        public string note = "";
    }

    public static int run()
    {
        Console.WriteLine("round        seconds   max |dpos| m   max |dvel| m/s  note");
        int bad = 0;
        Report("rocket", Defs.WID_ROCKET, Defs.PROJ_ROCKET, ref bad);
        Report("rocket_he", Defs.WID_ROCKET_HE, Defs.PROJ_ROCKET, ref bad);
        Report("rpg", Defs.WID_RPG, Defs.PROJ_ROCKET, ref bad);
        Report("saclos", Defs.WID_SACLOS, Defs.PROJ_SACLOS, ref bad);
        Console.WriteLine(bad == 0
            ? "\nairframe: the stages and the closed form are the same law"
            : "\nairframe: the stages and the closed form disagree");
        return bad;
    }

    /// <summary>Fire one round straight up and compare every tick it flies.</summary>
    private static void Report(string label, int wid, byte kind, ref int bad)
    {
        var world = new World(7, "range");
        // nothing in the range may touch the measurement: the dummies do not need to
        // observe the round and the defence post would shoot at it
        world.systems.Remove(Modes.Range.Targets.run);
        world.systems.Remove(Modes.Range.Interceptor.run);
        var e = world.ecs;
        var wd = Defs.WEAPONS[wid];

        // the range's open plain, a kilometre clear of the missile-defence post (whose
        // pass is off anyway) - a fired round is measured here, not in a battle
        var start = new Vec3(2000f, world.terrain.height_at(2000f, 800f) + 400f, 800f);
        // it leaves the tube doing 200 m/s, the way `Weapons.fire` hands it over
        int round = world.spawn_round(-1, Defs.FACTION_BLUE, wid, kind, start,
                                      new Vec3(0f, 200f, 0f), wd.life, -1);

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
            var want = Flight.slide(v0, age0, wd, DT);
            var wp = new Vec3(p0.x + want.x * DT, p0.y + want.y * DT, p0.z + want.z * DT);
            row.dpos = MathF.Max(row.dpos, (p1 - wp).len);
            row.dvel = MathF.Max(row.dvel, (v1 - want).len);
            row.seconds += DT;
        }
        bool ok = row.seconds > 1f && row.dpos <= TOL_POS && row.dvel <= TOL_VEL;
        if (!ok) bad++;
        Console.WriteLine($"{label,-12} {row.seconds,7:0.0}   {row.dpos,12:E2}   "
                          + $"{row.dvel,14:E2}  {row.note}"
                          + (ok ? "" : "  <-- out of tolerance"));
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
