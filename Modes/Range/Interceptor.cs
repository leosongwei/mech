// A missile-defence post on the range: one AI mech that shoots down what is
// fired at it and does nothing else.
//
// It is a crew like any other (`ai_crew` + `mech_ai`) that carries no
// `crew_engage`, so it never picks a ground target: the range's own AI block
// (`Systems/AI/Pipeline.cs::range`) lays its intercept and nothing else, and this
// file only puts the hull back up after it is shot to pieces.
//
// The range's rule is that nothing there shoots back: this hull never acquires a
// ground target, never advances along a lane and never touches the player.  What it does have is a 30 mm with its 2.5 m airburst fuse
// and a mech's radar (`Defs.UNIT_DEFS[ETYPE_MECH].radar_strength`), which is
// exactly what an incoming SACLOS, top-attack, S-8 or RPG has to get past.
//
// It is the testbed for the radar/intercept numbers: fire each round at it from
// each approach and the server log says what happened (a `prox fuse:` line for a
// burst, `missile N took ...` for the health a missile lost, `intercept: ...` for
// one that came down).
//
// Ported from `backend/server/modes/range/interceptor.py`.

namespace Mech.Server.Modes.Range;

public sealed class Interceptor
{
    // stand-off post: on the plain, ~870 m north-west of the firing line, so every
    // weapon in the game reaches it with room to be intercepted on the way in
    public static readonly (float x, float z) INTERCEPTOR_POST = (1550f, 2250f);
    public const float RESPAWN_AFTER = 5f;      // s before a shot-down post is stood back up
    // a cannon and nothing else: the hull is here to intercept, not to fight
    public static readonly int[] LOADOUT = { Defs.WID_AC30, Defs.WID_NONE };

    public int eid = -1;
    public float t;

    /// <summary>The mode's system: `interceptor.run(world, dt)`.</summary>
    public static void run(World world, float dt) =>
        ((Interceptor)world.interceptor!).update(world, dt);

    public Interceptor(World world)
    {
        spawn(world);
    }

    public int spawn(World world)
    {
        var (x, z) = INTERCEPTOR_POST;
        int eid = world.spawn_unit(Defs.ETYPE_MECH, Defs.FACTION_BLUE, x, z, weapons: LOADOUT);
        // face the firing line so the crew's sight starts where the shooting is
        var (sx, sz) = world.terrain.bases[Defs.FACTION_RED];
        float yaw = MathF.Atan2(sz - z, sx - x);
        var e = world.ecs;
        e.fv("yaw")[eid] = yaw;
        e.fv("leg_yaw")[eid] = yaw;
        e.fv("sight_yaw")[eid] = yaw;
        this.eid = eid;
        t = 0f;
        return eid;
    }

    public void update(World world, float dt)
    {
        var e = world.ecs;
        // the same id-reuse trap as the targets: a destroyed hull's id is taken by
        // the blast it left behind, and "it is alive" would then mean "a burst
        // entity stands where the post used to be" - it would never respawn
        if (eid >= 0 && !(e.is_alive(eid) && e.has(eid, "moves")))
        {
            eid = -1;                 // shot to pieces: post it again shortly
            t = RESPAWN_AFTER;
        }
        if (eid < 0)
        {
            t -= dt;
            if (t <= 0f) spawn(world);
        }
    }
}
