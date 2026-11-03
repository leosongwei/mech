// Proximity fuse: the airburst rounds go off on *people* - and on missiles.
//
// A cannon round covers 36 m in one tick, so a distance test at the tick
// boundaries would step clean over a rifleman standing 4 m off the path.  The
// fuse therefore solves the relative motion of the round and its target across
// the leg the round actually flew this tick (`proj_prev` -> `pos`) instead of
// sampling it: both are straight and constant-speed inside one 1/30 s, so the
// closest approach is closed-form, and if it is inside the fuse radius the round
// goes off *there* - not at the end of the tick, 36 m past him, which is where an
// impact fuse would have planted it in the dirt.
//
// Two kinds of target set it off:
//
//   * `person` - a man is a man whatever he carries.  Armour is deliberately not
//     tagged: an APC 4 m from the path leaves the fuse cold and waits for the
//     impact fuse, which is what keeps the cannon a penetration weapon against
//     vehicles and an area weapon against infantry.
//   * `interceptable` - a missile in the air (`Components.MISSILE_COMPS`).  This
//     is the counter-battery half: a 30 mm burst that passes within 2.5 m of an
//     incoming ATGM lights on it, and the fragments put ten points of blast into a
//     round that has eight of health at that range.  A missile moves 20 m a tick,
//     so this is also the case the relative solve exists for - measured against a
//     *stationary* hull the fuse would fire on the phase of the clock.
//
// The target a round fused on is settled *here*, at the moment it fused: the
// burst entity that follows is judged a stage later, against positions gathered
// for the whole tick, and by then a 600 m/s missile is twenty metres out of a
// four metre radius.  So the fused hull takes `Combat.splash` at the exact gap,
// and the burst is told to spare it.
//
// Ported from `backend/server/systems/proximity.py`.

using Microsoft.Extensions.Logging;

namespace Mech.Server.Systems;

public static class Proximity
{
    private static readonly ILogger log = MechLog.get("mech.prox");

    public static void run(World world, float dt)
    {
        var e = world.ecs;
        var pids = world.projectiles();
        if (pids.Length == 0) return;
        var widv = e.uv("proj_wid");
        var fused_pid = new List<int>();
        var fused_wd = new List<WeaponDef>();
        foreach (int pid in pids)
        {
            var wd = Defs.WEAPONS[widv[pid]];
            if (wd.prox <= 0f) continue;
            fused_pid.Add(pid);
            fused_wd.Add(wd);
        }
        if (fused_pid.Count == 0) return;     // nothing in the air carries a fuse

        var people = e.query("person");
        var missiles = world.interceptable();
        if (people.Length == 0 && missiles.Length == 0) return;

        // the two target sets, as rows of the tick's sphere table: the ids are
        // global, so a projectile id never collides with a hull's
        var target_set = new HashSet<int>(people);
        foreach (int m in missiles) target_set.Add(m);
        var sph = world.sphere_table(dt);
        int nrow = 0;
        var rows = new int[sph.n];
        for (int s = 0; s < sph.n; s++)
            if (target_set.Contains(sph.ids[s])) rows[nrow++] = s;
        if (nrow == 0) return;

        for (int q = 0; q < fused_pid.Count; q++)
        {
            int pid = fused_pid[q];
            if (!e.is_alive(pid)) continue;
            var wd = fused_wd[q];
            float prox = wd.prox;
            var p0 = e.fv("proj_prev").get3(pid);
            var p1 = e.fv("pos").get3(pid);
            float segx = p1.x - p0.x, segy = p1.y - p0.y, segz = p1.z - p0.z;
            float l2 = segx * segx + segy * segy + segz * segz;
            if (l2 < 1e-9f) continue;         // barely moved: nothing to solve
            float leg = MathF.Sqrt(l2);
            int mine = e.uv("faction")[pid];

            // time of closest approach, as a fraction of this tick, clamped to the
            // leg itself: for a round that slows to a stop the closest point is an
            // end, not a projection
            float best_gap = float.PositiveInfinity;
            float best_t = 0f;
            int best_row = -1;
            for (int j = 0; j < nrow; j++)
            {
                int s = rows[j];
                // a burst never fuses on its own side
                if (sph.factions[s] == mine) continue;
                float relx = sph.prev[s * 3] - p0.x;
                float rely = sph.prev[s * 3 + 1] - p0.y;
                float relz = sph.prev[s * 3 + 2] - p0.z;
                float wx = segx - sph.disp[s * 3];
                float wy = segy - sph.disp[s * 3 + 1];
                float wz = segz - sph.disp[s * 3 + 2];
                float w2 = wx * wx + wy * wy + wz * wz;
                float t = (relx * wx + rely * wy + relz * wz) / MathF.Max(w2, 1e-9f);
                if (t < 0f) t = 0f; else if (t > 1f) t = 1f;
                float dx = relx - wx * t, dy = rely - wy * t, dz = relz - wz * t;
                float gap = MathF.Sqrt(dx * dx + dy * dy + dz * dz) - sph.radii[s];
                if (gap < best_gap)
                {
                    best_gap = gap;
                    best_t = t;
                    best_row = s;
                }
            }
            if (best_row < 0 || !(best_gap <= prox))
                continue;                     // nobody inside the fuse radius
            float f = best_t;
            var burst = new Vec3(p0.x + segx * f, p0.y + segy * f, p0.z + segz * f);
            int tid = sph.ids[best_row];
            bool human = e.has(tid, "person");
            // the round goes off where it fused, so the blast, the splash and the
            // flash all belong to that point rather than to the end of the tick
            e.fv("pos").set3(pid, burst);
            // DEBUG, like every weapon-level line: a burst is an ordinary event and
            // this geometry dump only earns its place when the fuse is being hunted
            if (log.IsEnabled(LogLevel.Debug))
            {
                log.LogDebug("prox fuse: {name} round {pid} burst at ({bx:F1}, {by:F1}, "
                             + "{bz:F1}), {gap:F2} m off {what} {tid} - {into:F1} m into a "
                             + "{leg:F1} m leg from ({x0:F1}, {y0:F1}, {z0:F1}), round "
                             + "flew on to ({x1:F1}, {y1:F1}, {z1:F1})",
                             wd.name, pid, burst.x, burst.y, burst.z, best_gap,
                             human ? "person" : "missile", tid, f * leg, leg,
                             p0.x, p0.y, p0.z, p1.x, p1.y, p1.z);
            }
            // a missile that has already left the burst by the time `blasts` runs is
            // settled here, at the gap it was at when the fuse fired; a man stands
            // still and is left to the burst like he always was
            if (!human)
            {
                // the credit is the round's own (`proj_pid`, frozen at the muzzle),
                // not a lookup on the hull that fired it: that hull may be gone
                int pl = e.iv("proj_pid")[pid];
                var by = ((int)e.uv16("proj_name")[pid], mine, (int)e.uv("proj_etype")[pid]);
                Combat.splash(world, tid, Defs.MOD_CHASSIS, wd, best_gap, pl,
                              $"prox {best_gap:F1} m", by);
            }
            // the round goes off where it fused: one entry point for "a round
            // detonates", so the flash, the burst entity and the credit all come out
            // the same way a direct hit's do
            Projectiles._impact(world, burst, wd, pid, skip: human ? -1 : tid);
            e.destroy(pid);
        }
    }
}
