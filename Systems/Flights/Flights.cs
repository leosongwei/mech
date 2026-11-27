// The batch every round's pass is written against: re-arm the tick's record,
// publish the leg a move covered, and the one predicate that says whether a round
// flies at all this tick.  The shared column helpers are here too, because every
// pass in the block writes vectors into buffers.
//
// The flight itself is eleven passes, each one of them a column query over its own
// components - re-arm, the bullet's parabola, the two guidance laws, then the
// airframe as seven stages (airflow, angle of attack, lift, drag, thrust, gravity,
// integrate).  What a round *is* is its component set, so no pass asks what kind of
// round it is looking at: a bullet owns no airframe columns and is out of those
// seven by construction.
//
// Written from `Systems/Projectiles.cs` (the Python port's `systems/projectiles.py`).

namespace Mech.Server.Systems;

public static partial class Flights
{
    /// <summary>Is this round still flying?  `Expire` writes a contact at t=0 into
    /// the record of a round that has run out of battery or wire, and `Arm` re-arms
    /// every other one, so one compare answers it for every stage in the block.
    /// This reads the record itself rather than a separate `live` flag: one fact,
    /// one home.</summary>
    internal static bool flying(float hit_t) => hit_t > 1f;

    /// <summary>Re-arm the record and publish where this tick's leg starts.  A round
    /// that `Expire` condemned keeps the contact it was given: its record is not this
    /// tick's to re-arm, and no stage moves it.</summary>
    internal static void _arm(RoundRow r)
    {
        if (!flying(r.hit_t)) return;
        r.hit_t = Defs.FIRST_HIT_ARMED;                 // this tick's record is fresh
        r.prev = r.pos;
    }

    /// <summary>Integrate one round over the tick and publish the leg it covered
    /// for the `FirstHit` systems.</summary>
    internal static void _move(World world, RoundRow r, Vec3 p0, Vec3 v, float dt)
    {
        r.vel = v;
        var p1 = new Vec3(p0.x + v.x * dt, p0.y + v.y * dt, p0.z + v.z * dt);
        r.pos = p1;
        r.trav += MathF.Sqrt(v.x * v.x + v.y * v.y + v.z * v.z) * dt;
        world.sweep.add(r.eid, p0, p1);
    }

    /// <summary>m/s² of lift at this coefficient, from the round's own area and mass.
    /// At `cl_max` it is the ceiling on everything the guidance may ask for - which is
    /// why a slow round stops answering the beam.</summary>
    internal static float lift_accel(WeaponDef wd, float q, float cl)
        => q * Flight.area(wd) * cl / wd.mass;

    /// <summary>Row `i` of a 3-float column as a vector.</summary>
    internal static Vec3 v3(float[] a, int i) => new(a[i * 3], a[i * 3 + 1], a[i * 3 + 2]);

    /// <summary>Write a vector into row `i` of a 3-float column.</summary>
    internal static void set3(float[] a, int i, Vec3 v)
    {
        a[i * 3] = v.x; a[i * 3 + 1] = v.y; a[i * 3 + 2] = v.z;
    }

    /// <summary>Add a vector into row `i` of a 3-float column.  The force stages
    /// only ever add: `acc` is zeroed once, by `Arm`, and read once, by
    /// `Integrate`.</summary>
    internal static void add3(float[] a, int i, Vec3 v)
    {
        a[i * 3] += v.x; a[i * 3 + 1] += v.y; a[i * 3 + 2] += v.z;
    }

    // ------------------------------------------------------------------
    // the two turns every guidance law is made of
    // ------------------------------------------------------------------

    /// <summary>Unit direction square to the velocity that turns it toward
    /// `want`.</summary>
    internal static Vec3 _lift_dir(Vec3 vhat, Vec3 want)
    {
        var axis = vhat.cross(want);
        if (axis.len < 1e-6f) return Vec3.zero;
        return axis.unit.cross(vhat).unit;
    }

    /// <summary>True proportional navigation toward `roof`: the acceleration
    /// owed.</summary>
    internal static Vec3 _pn_accel(Vec3 p, Vec3 v, Vec3 roof, Vec3 tgt_vel, float n_law)
    {
        var r = roof - p;
        float r2 = r.len2;
        if (r2 < 1e-6f) return Vec3.zero;
        var rhat = r.unit;
        var vrel = tgt_vel - v;
        var omega = r.cross(vrel) / r2;
        float vc = -rhat.dot(vrel);
        var a = omega.cross(rhat);
        float k = n_law * MathF.Max(vc, 1f);
        return a * k;
    }
}
