// The batch every round's pass is written against: re-arm the tick's record, and
// publish the leg a move covered.
//
// The flight systems themselves live one per file beside this one, and each is a
// nested class of `Flights`: what a round *is* is a tag component
// (`Defs.KIND_TAGS`), so each kind is its own archetype and its own pass.
//
// Written from `Systems/Projectiles.cs` (the Python port's `systems/projectiles.py`).

namespace Mech.Server.Systems;

public static partial class Flights
{
    /// <summary>Re-arm the record and publish where this tick's leg starts.
    ///
    /// False means the round was condemned before it flew (`Expire` wrote a
    /// contact at t=0 this tick) and holds still where it stands - the same place
    /// the old integrator checked its battery, before moving.</summary>
    private static bool _begin(RoundRow r)
    {
        if (r.hit_t <= 1f) return false;                // condemned: do not fly
        r.hit_t = Defs.FIRST_HIT_ARMED;                 // this tick's record is fresh
        r.prev = r.pos;
        return true;
    }

    /// <summary>Integrate one round over the tick and publish the leg it covered
    /// for the `FirstHit` systems.</summary>
    private static void _move(World world, RoundRow r, Vec3 p0, Vec3 v, float dt)
    {
        r.vel = v;
        var p1 = new Vec3(p0.x + v.x * dt, p0.y + v.y * dt, p0.z + v.z * dt);
        r.pos = p1;
        r.trav += MathF.Sqrt(v.x * v.x + v.y * v.y + v.z * v.z) * dt;
        world.sweep.add(r.eid, p0, p1);
    }

    private static (Vec3 dir, float len) _norm(Vec3 v)
    {
        float n = MathF.Sqrt(v.x * v.x + v.y * v.y + v.z * v.z);
        if (n < 1e-6f) return (new Vec3(1f, 0f, 0f), 0f);
        return (new Vec3(v.x / n, v.y / n, v.z / n), n);
    }
}
