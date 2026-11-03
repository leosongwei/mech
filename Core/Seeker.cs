// The IR seeker's lock: what a top-attack round is flying at.
//
// A guided round does not need its target to survive the flight.  The seeker
// carries two numbers (components `tgt_mem` / `tgt_vel`): the point it stares at
// - the deck of the hull it is going to dive on - and the velocity that hull had
// the last time it was seen.  While the lock holds, both are refreshed every tick
// off the live hull; the moment something cuts the sight line (a puff, a ridge, a
// tree, or the hull dying) the stare point keeps walking at that last velocity,
// and *guidance never notices*: `Flight.step` is handed a point and a velocity, and
// a stale point is a point like any other.
//
// That is the whole of the difference between a missile and a rock, and it is
// also what makes re-acquisition one question: is there an enemy hull I can see
// inside `Defs.SEEKER_CONE` of my stare line?  If there is, it is the target now -
// whichever hull it happens to be.  A hull that drives into the shot's line while
// the smoke is up is as good a target as the one that pulled the smoke, and the
// code does not care to tell them apart: it asks for an enemy, not for *the*
// enemy.
//
// The cone is measured from the seeker's own eye, against the line eye -> stare
// point, so it re-locks onto what is in front of it, not onto what is merely near
// the estimate: a hull beside the round is not a target until the round turns
// onto it.  Losers of the sweep are not ordered beyond that - the one nearest the
// stare line goes first, and the first one with a clear sight line is taken.

using Microsoft.Extensions.Logging;

namespace Mech.Server;

public static class Seeker
{
    // the smoke module's logger: a missile losing its lock has always been logged
    // here, and the range's smoke test reads the line straight out of the log
    private static readonly ILogger log = MechLog.get("mech.smoke");

    /// <summary>The point the seeker aims at on a hull: its deck, not its tracks.</summary>
    public static Vec3 roof(World world, int tid)
    {
        var e = world.ecs;
        var p = e.fv("pos").get3(tid);
        float h = Defs.UNIT_DEFS.TryGetValue(e.uv("etype")[tid], out var d) ? d.height : 6f;
        return new Vec3(p.x, p.y + Defs.AIM_ROOF * h, p.z);
    }

    /// <summary>The point this round is staring at right now (its memory as it stands).</summary>
    public static Vec3 stare(World world, int pid) => world.ecs.fv("tgt_mem").get3(pid);

    /// <summary>One tick of the seeker: hold the lock, or find a new one.
    ///
    /// Returns the point to guide on, or null for a round that has nothing to
    /// stare at (it was fired without a lock, which a lock weapon cannot be).  The
    /// round's own components are the seeker's state, so this is idempotent per
    /// tick and needs nothing remembered anywhere else.</summary>
    public static Vec3? track(World world, int pid, Vec3 p0, float dt)
    {
        var e = world.ecs;
        var vel = e.fv("vel");
        var faction = e.uv("faction");
        var target = e.iv("target");
        var mem = e.fv("tgt_mem");
        var memv = e.fv("tgt_vel");
        int mine = faction[pid];

        // ---- the lock we had, if it is still a lock -------------------------
        int tid = target[pid];
        if (tid >= 0 && (!e.is_alive(tid) || faction[tid] == mine)) tid = -1;
        Vec3? spot = null;
        if (tid >= 0)
        {
            var sp = roof(world, tid);
            string why = Systems.Sight.why(world, p0, sp);
            if (why.Length > 0)
            {
                // the seeker is blind: log it (the smoke test on the range is read
                // straight out of the server log, at DEBUG) and fall through to the
                // memory
                if (log.IsEnabled(LogLevel.Debug))
                    log.LogDebug("seeker of missile {pid} lost the lock on hull {tid}: {why} "
                                 + "on the line of sight ({dist:F0} m out, {age:F1} s in the air)",
                                 pid, tid, why, MathUtil.dist(p0, sp), e.fv("proj_age")[pid]);
                world.emit("seeker", ("x", p0.x), ("y", p0.y), ("z", p0.z),
                           ("faction", mine), ("tid", tid), ("proj", pid), ("why", why),
                           ("dist", MathF.Round(MathUtil.dist(p0, sp), 1)));
                spot = null;
            }
            else
            {
                spot = sp;
            }
        }
        if (spot is not null)
        {
            target[pid] = tid;
            mem.set3(pid, spot.Value);
            memv.set3(pid, vel.get3(tid));
            return spot;
        }

        // ---- no lock: stare at where the hull should be ---------------------
        target[pid] = -1;
        var mem0 = mem.get3(pid);
        if (mem0.x == 0f && mem0.y == 0f && mem0.z == 0f) return null;   // nothing was ever locked
        var m = mem0 + memv.get3(pid) * dt;             // the memory keeps flying the hull
        mem.set3(pid, m);
        spot = m;

        // ---- and take whatever enemy it can see in the cone ----------------
        tid = acquire(world, p0, m, mine);
        if (tid < 0) return spot;
        var st = roof(world, tid);
        target[pid] = tid;
        mem.set3(pid, st);
        memv.set3(pid, vel.get3(tid));
        if (log.IsEnabled(LogLevel.Debug))
            log.LogDebug("seeker of missile {pid} re-locked on hull {tid} ({dist:F0} m out, "
                         + "{age:F1} s in the air)", pid, tid, MathUtil.dist(p0, st),
                         e.fv("proj_age")[pid]);
        return st;
    }

    /// <summary>First enemy hull with a clear sight line inside the cone, or -1.
    ///
    /// The candidates are judged on the deck they would be dived on (the point the
    /// seeker looks at), not on their tracks: at 1 km the two are a third of a
    /// degree apart, which is a fifth of the cone.</summary>
    public static int acquire(World world, Vec3 p0, Vec3 spot, int mine)
    {
        var e = world.ecs;
        var ids = world.lockable_units();
        if (ids.Length == 0) return -1;
        var d = spot - p0;
        float n = d.len;
        if (n < 1e-3f) return -1;
        d = d / n;
        var pos = e.fv("pos");
        var etype = e.uv("etype");
        var faction = e.uv("faction");
        var cand = new List<(float ang, int tid, Vec3 deck)>();
        for (int k = 0; k < ids.Length; k++)
        {
            int tid = ids[k];
            var c = pos.get3(tid);
            float h = Defs.UNIT_DEFS.TryGetValue(etype[tid], out var u) ? u.height : 6f;
            c = new Vec3(c.x, c.y + Defs.AIM_ROOF * h, c.z);
            var v = c - p0;
            float dist = v.len;
            if (!(dist > 1e-3f)) continue;
            if (faction[tid] == mine) continue;
            float ang = MathF.Acos(MathUtil.clamp(v.dot(d) / MathF.Max(dist, 1e-3f), -1f, 1f));
            if (!(ang <= Defs.SEEKER_CONE)) continue;
            cand.Add((ang, tid, c));
        }
        // nearest to the stare line first, but a hull in cover is never a
        // candidate: take the first one the seeker can actually see
        cand.Sort((a, b) => a.ang.CompareTo(b.ang));
        foreach (var (_, tid, deck) in cand)
        {
            if (Systems.Sight.clear(world, p0, deck)) return tid;
        }
        return -1;
    }
}
