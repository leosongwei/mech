// Rules that differ between game modes.
//
// A mode owns the map, what a fresh round spawns, which order the shared systems
// run in and the handful of rules the shared code has to ask about (fixed side,
// starting money, rearm anywhere...).  Mechanics that all modes share -
// ballistics, movement, lock-on, damage, income - stay in `Systems/`; anything
// that only makes sense in one mode lives in that mode's folder as its own
// systems, so adding a third mode never means growing an `if` here.
//
// Ported from `backend/server/modes/base.py`.

namespace Mech.Server.Modes;

public abstract class Mode
{
    public string name = "mode";
    public int? fixed_faction = null;          // null: the lobby picks the side
    public int start_money = 600;
    public float restart_respawn = 3f;         // seconds before players come back after a restart
    public bool rearm_anywhere = false;
    public float? smoke_cooldown = null;       // null: Defs.SMOKE_COOLDOWN
    /// <summary>Components a hull spawned from the debug console carries.  A mode
    /// whose own units must stay passive uses the tag to tell "a hull the pilot
    /// dropped here on purpose" from everything else it owns
    /// (`Modes/Range/TestAi.cs`).</summary>
    public string[] console_spawn_extra = Array.Empty<string>();

    public abstract Terrain terrain(int seed);

    /// <summary>Build the battlefield for a fresh round (also used on restart).</summary>
    public virtual void setup(World world) { }

    /// <summary>Mode systems that run before lock-on / weapons / movement.</summary>
    public virtual List<Action<World, float>> pre_systems() => new();

    /// <summary>Mode systems that run after projectiles have been integrated.</summary>
    public virtual List<Action<World, float>> post_systems() => new();

    /// <summary>(x, z, yaw) for a (re)spawning player, or null for the default rule.</summary>
    public virtual (float x, float z, float yaw)? mech_spawn(World world, Player player) => null;

    /// <summary>Seconds a pilot waits after a hull died at (x, z).</summary>
    public virtual float respawn_delay(World world, Player player, float x, float z) =>
        Defs.RESPAWN_TIME;
}
