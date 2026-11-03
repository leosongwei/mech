// Income and player respawns - the two economy rules every mode shares.
//
// Ported from `backend/server/systems/economy.py`.
//
// Kill money is *not* here: a kill is paid where it is judged
// (`Systems/Mortality.cs`), because that is where the victim's value and the
// killer's identity are frozen.  What this system owns is the clock everything
// runs on: a steady income and, for a pilot with no mech, the wait until the mode
// hands him a new one.  Where that spawn is - the town the death plan picked, or
// the base ring - is the mode's answer (`Mode.mech_spawn`), asked inside
// `World.spawn_mech`.

namespace Mech.Server.Systems;

public static class Economy
{
    public const float INCOME = 4f;        // $/s

    public static void run(World world, float dt)
    {
        if (world.winner >= 0) return;
        foreach (var pl in world.players.Values)
        {
            pl.money += INCOME * dt;
            if (pl.mech >= 0 && world.ecs.is_alive(pl.mech)) continue;
            pl.respawn -= dt;
            if (pl.respawn <= 0f)
            {
                world.spawn_mech(pl);
                world.emit("respawn", ("pid", pl.pid));
            }
        }
    }
}
