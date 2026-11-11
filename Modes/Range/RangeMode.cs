// Range mode: a 4 km test map, a fixed cross, passive APC targets, one
// missile-defence post that shoots down what is fired at it.
//
// Ported from `backend/server/modes/range/__init__.py`.

namespace Mech.Server.Modes.Range;

public sealed class RangeMode : Mode
{
    public RangeMode()
    {
        name = "range";
        // The side is the lobby's choice, not the mode's: two browsers on one range
        // is how the whole *receiving* end gets tested - one pilot fires a missile,
        // the other watches his own radar light up and shoots it down with the
        // 30 mm.  AUTO still balances the sides, so a second pilot lands opposite
        // the first by itself.  The targets and the defence post are BLUE, so a
        // blue pilot finds them friendly and red is the side that shoots at them.
        fixed_faction = null;
        start_money = 99999;
        restart_respawn = 1.0f;
        rearm_anywhere = true;
        // a hull from the console is the one thing here that fights: the tag is
        // what puts such a hull into the AI block, and the mode's own targets
        // and post carry neither tag, so they stay as passive as they were
        console_spawn_extra = new[] { "range_drone", "front_mission", "crew_engage" };
        // no smoke_cooldown override: the battle cooldown is short enough to
        // practise a pull here
    }

    public override Terrain terrain(int seed) => Terrain.generate_range(seed);

    public override void setup(World world)
    {
        world.base_entity = new Dictionary<int, int>();
        world.targets = new Targets(world);
        // the bunkers are `invulnerable` hulls that look after themselves; Python
        // parks their ids on `world.bunkers` and nothing ever reads it back, so the
        // list is dropped here rather than given a world slot of its own
        Bunkers.place(world);
        // one AI hull that does nothing but shoot missiles down: the range's place
        // to measure the radar and the fuse against a real approach
        world.interceptor = new Interceptor(world);
        // a trace of every guided round and the hulls it chases, when asked for
        // (`MECH_RECORD`); a restart keeps writing to the same file
        Record.attach(world);
    }

    public override List<Action<World, float>> pre_systems()
    {
        var into = new List<Action<World, float>> { Targets.run, Interceptor.run };
        // the crews a pilot dropped here fight like lane hulls; the mode's own
        // fixtures carry no `ai_crew` at all and are out of every pass by
        // construction (`Systems/AI/Support/Tags.cs`)
        into.AddRange(Systems.AI.Pipeline.range());
        return into;
    }

    // last: the rounds have flown and the dead have been judged, so a line carries
    // the end state of the tick
    public override List<Action<World, float>> post_systems() => new() { Record.run };

    public override (float x, float z, float yaw)? mech_spawn(World world, Player player)
    {
        var (x0, z0) = Terrain.RANGE_SPAWN;
        var (hx, hz, _) = Terrain.RANGE_HILL;
        // one berth per pilot round the firing line, so two browsers do not
        // materialise inside each other on the one spawn point
        float a = 0.9f * (player.pid % 6);
        float x = x0 + MathF.Cos(a) * 18f;
        float z = z0 + MathF.Sin(a) * 18f;
        return (x, z, MathF.Atan2(hz - z, hx - x));
    }
}
