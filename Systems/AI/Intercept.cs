// The missile alarm: which crew has a round coming for it, this tick.
//
// One question, two answers, and both are `doc/plan.md`'s:  *can I see it* is
// `d^2 <= K * v_closing` with `K = sqrt(S * rcs) / V_REF` (`Defs.radar_k`) - a round
// that opens out or crosses gives a non-positive closing speed, so the right-hand
// side is <= 0 and the test fails by itself - and *is it for me* is the seeker's own
// lock for a diving round (its straight line passes nowhere near the hull it is
// coming for) or the closest point of approach for everything else.  A round whose
// lock has been cut is still flying at the point it remembers, so that point near a
// hull keeps the crew shooting at the screen it is coming through.
//
// This system decides; it does not aim.  `ai_cover` is what the platform's engage
// system reads to know that this tick's gun belongs to the intercept instead of the
// ground fight, which is why the trigger still has exactly one writer per crew.

namespace Mech.Server.Systems.AI;

public static class Intercept
{
    /// <summary>m: inside the gun's reach it is worth the ammo.</summary>
    public const float RANGE = 1500.0f;
    /// <summary>m: closer than this and a round is coming for me.</summary>
    public const float CPA_HIT = 40.0f;
    /// <summary>m: a guided round with no lock flies at the point it last saw its
    /// target, and that point this close to a hull means it is still coming for it.</summary>
    public const float CPA_MEM = 100.0f;

    public static void run(World world, float dt)
    {
        var threats = world.threats;
        foreach (var g in world.ecs.groups(CrewColumns.OnCrew))
        {
            var c = new CrewColumns(g);
            foreach (var r in c)
            {
                c.cover[r.index] = 0;
                c.threat[r.index] = -1;
                if (threats is null || threats.n == 0) continue;
                // a hull with no self-defence radar never sees one coming
                float strength = Defs.UNIT_DEFS[r.etype].radar_strength;
                if (strength <= 0f) continue;
                // ...and one whose guns cannot down it does not try: the crew still
                // fights on the ground, its trigger is simply not the alarm's
                if (Crew.fused_arm(r) < 0) continue;
                int k = _threat(threats, strength, r);
                if (k < 0) continue;
                c.cover[r.index] = 1;
                c.threat[r.index] = threats.ids[k];
            }
        }
    }

    /// <summary>Index of the round worth shooting at, or -1.</summary>
    private static int _threat(ThreatTable mis, float strength, CrewRow r)
    {
        var eye = r.eye;
        var ovel = r.vel;
        byte fac = r.faction;
        int eid = r.eid;
        int bestK = -1;
        float best = 0f;
        for (int k = 0; k < mis.n; k++)
        {
            float rx = mis.pos[k * 3] - eye.x;
            float ry = mis.pos[k * 3 + 1] - eye.y;
            float rz = mis.pos[k * 3 + 2] - eye.z;
            float vrx = mis.vel[k * 3] - ovel.x;
            float vry = mis.vel[k * 3 + 1] - ovel.y;
            float vrz = mis.vel[k * 3 + 2] - ovel.z;
            float d2 = rx * rx + ry * ry + rz * rz;
            float vc = -(rx * vrx + ry * vry + rz * vrz) / MathF.Sqrt(MathF.Max(d2, 1e-9f));
            float kk = MathF.Sqrt(MathF.Max(strength * mis.rcs[k], 0f)) / Defs.RADAR_V_REF;
            if (!(d2 <= kk * vc)) continue;                   // not seen by this radar
            if (mis.faction[k] == fac) continue;              // a friendly round
            if (!(d2 <= RANGE * RANGE)) continue;
            float w2 = MathF.Max(vrx * vrx + vry * vry + vrz * vrz, 1e-9f);
            float tcpa = -(rx * vrx + ry * vry + rz * vrz) / w2;
            if (tcpa < 0f) tcpa = 0f;
            float cx = rx + vrx * tcpa;
            float cy = ry + vry * tcpa;
            float cz = rz + vrz * tcpa;
            float cpa = MathF.Sqrt(cx * cx + cy * cy + cz * cz);
            // a top attack climbs over its victim and dives on the roof, so its
            // straight line passes nowhere near it - the seeker's own lock is the
            // truth, and with the lock cut, the point it still stares at
            float mx = mis.mem[k * 3] - eye.x;
            float my = mis.mem[k * 3 + 1] - eye.y;
            float mz = mis.mem[k * 3 + 2] - eye.z;
            bool mine = mis.target[k] == eid
                        || MathF.Sqrt(mx * mx + my * my + mz * mz) < CPA_MEM;
            if (!(mis.kind[k] == Defs.PROJ_TOP ? mine : cpa < CPA_HIT)) continue;
            if (bestK < 0 || d2 < best)
            {
                best = d2;
                bestK = k;
            }
        }
        return bestK;
    }
}
