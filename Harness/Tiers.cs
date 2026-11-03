// A tiered interest sphere, priced and — more to the point — audited: what a
// server-side row filter costs per minute, and where the rule breaks.
//
//   ./dotnet.sh run -- --check tiers --minutes 30 --from 8
//
// The scheme under test (radius from the pilot's own eye):
//
//   6 km  APC + mech
//   4 km  the wreck an explosion leaves (ETYPE_DEBRIS)
//   3 km  guided rounds (rocket / SACLOS / top attack), infantry + civilians
//   2 km  shells, smoke, the command base, corpses, bunkers
//
// Nothing here changes the simulation: the same battle runs, the same rows are
// written, and this only prices the frame *as if* `snapshot_bytes` had skipped
// them (14 B header + 80 B a unit row + 32 B a round or a puff), which is what
// filtering removes.  The client needs no change for that — `net.js` deletes
// every entity the newest frame does not mention — so the audit below is about
// the four client constants that decide whether a missing row is invisible or a
// lie, and about the rows the tier table does not have a line for.

using Mech.Server;

namespace Mech.Server.Harness;

public static class Tiers
{
    // ---- the tier table under test ----
    private const float R_VEH = 6000f;      // APC, mech
    private const float R_WRECK = 4000f;    // the hulk an explosion leaves
    private const float R_ROUND = 3000f;    // guided rounds
    private const float R_SHELL = 4000f;    // shells
    private const float R_SMOKE = 6000f;    // smoke, canisters and puffs (with the hulls)
    private const float R_INF = 3000f;      // infantry, civilians
    private const float R_OTHER = 2000f;    // base, corpse, bunker

    private static float radius_unit(int etype) => etype switch
    {
        Defs.ETYPE_APC or Defs.ETYPE_MECH => R_VEH,
        Defs.ETYPE_DEBRIS => R_WRECK,
        Defs.ETYPE_INF_RIFLE or Defs.ETYPE_INF_ROCKET or Defs.ETYPE_CIV => R_INF,
        _ => R_OTHER,
    };

    private static float radius_round(int kind) => kind == 0 ? R_SHELL : R_ROUND;

    private static bool infantry(int etype) =>
        etype is Defs.ETYPE_INF_RIFLE or Defs.ETYPE_INF_ROCKET or Defs.ETYPE_CIV;

    public static int run(int minutes, float from = 8f, int seed = 11)
    {
        var world = new World(seed, "battle");
        var pl = world.add_player("PILOT");
        var e = world.ecs;
        Console.WriteLine($"battle world: {world.towns.Count} towns, {world.lanes.Count} lanes, pilot {pl.name}");
        Console.WriteLine("tiers: 6 km mech/APC + smoke | 4 km wreck + shells | 3 km guided + infantry | 2 km base, corpse, bunker");
        Console.WriteLine("the client's own constants: fire control 4000 m (Defs.ZERO_RANGE), laser warning 3400 m");
        Console.WriteLine("                            (game.LASER_RANGE), threat receiver 2000 m (game.RWR_RANGE)");
        Console.WriteLine();
        Console.WriteLine("min |   raw   tier  saved | veh  inf wre  rnd  smk |  in/s out/s | blind dangl base |  fx/s 4km/s");
        Console.WriteLine("----+----------------------+-----------------------+-------------+------------------+---------------");

        const float dt = 1f / 30f;
        int ticks = (int)(minutes * 60f / dt);
        int nb = minutes + 1;
        var rawB = new double[nb];
        var tierB = new double[nb];
        var nSnap = new int[nb];
        var cVeh = new long[nb];
        var cInf = new long[nb];
        var cWre = new long[nb];
        var cRnd = new long[nb];
        var cSmk = new long[nb];
        var cIn = new long[nb];
        var cOut = new long[nb];
        var cBlind = new long[nb];      // guided rounds in the 3-4.3 km band, i.e. off the wire
        var cDangl = new long[nb];      // kept rows whose `target` is not in the frame
        var cBase = new int[nb];        // frames carrying the enemy command base
        var cFx = new int[nb];
        var cFx4 = new int[nb];
        var prev = new HashSet<int>();
        var cur = new HashSet<int>();
        var tgts = new List<(int id, int tgt)>();
        int min_now = -1;
        int maxSmk = 0;       // most puff rows in one frame (one screen is 14)
        for (int t = 0; t < ticks; t++)
        {
            Check.drive(world, pl, t);
            // the scripted pilot's four pulls all happen before the meter starts
            // and the AI never pulls smoke at all, so a screen would never be on
            // the wire inside the window: one pull a minute, to measure the tier
            if (t % 1800 == 1700) pl.smoke_q = true;
            world.update(dt);
            int tick = world.tick;
            List<Dictionary<string, object?>>? fx = null;
            if (world.events.Count > 0)
            {
                fx = new List<Dictionary<string, object?>>(world.events);
                world.events.Clear();
            }
            if (world.time < from * 60f) continue;
            int min = Math.Min((int)(world.time / 60f), nb - 1);
            if (min != min_now)
            {
                if (min_now >= 0) report(min_now, rawB, tierB, nSnap, cVeh, cInf, cWre, cRnd, cSmk,
                                         cIn, cOut, cBlind, cDangl, cBase, cFx, cFx4);
                min_now = min;
            }

            // one ear: the pilot's hull, or - while he is dead, in the base
            // orbit the client falls back to - the base he will come back from
            bool alive = pl.mech >= 0 && e.is_alive(pl.mech);
            var (bx, bz) = world.terrain.bases[pl.faction];
            float ex = bx, ez = bz;
            if (alive)
            {
                var p = e.fv("pos").get3(pl.mech);
                ex = p.x; ez = p.z;
            }

            if (fx is not null)
            {
                foreach (var ev in fx)
                {
                    cFx[min]++;
                    if (!ev.TryGetValue("x", out var xv) || xv is not float x) continue;
                    float z = ev.GetValueOrDefault("z") is float zf ? zf : 0f;
                    float dx = x - ex, dz = z - ez;
                    if (dx * dx + dz * dz <= R_WRECK * R_WRECK) cFx4[min]++;
                }
            }
            if (tick % Server.SNAP_EVERY != 0) continue;

            cur.Clear();
            tgts.Clear();
            int nv = 0, ni = 0, nw = 0, nd = 0, sm = 0, blind = 0;
            int nu = 0, np = 0;
            var uet = e.uv("etype");
            foreach (int id in world.all_units())
            {
                var q = e.slot(id);
                if (!q.ok || !q.a.has("pos")) continue;
                int et = q.a.has("etype") ? q.a.get_u8("etype", q.row) : 0;
                float dx = q.a.get_f("pos", q.row, 0) - ex, dz = q.a.get_f("pos", q.row, 2) - ez;
                bool near = dx * dx + dz * dz <= radius_unit(et) * radius_unit(et);
                // the pilot's own hull is never dropped, whatever the table says
                if (!near && !(alive && id == pl.mech)) continue;
                cur.Add(id);
                nu++;
                tgts.Add((id, q.a.has("target") ? q.a.get_i("target", q.row) : -1));
                if (et is Defs.ETYPE_APC or Defs.ETYPE_MECH) nv++;
                else if (infantry(et)) ni++;
                else if (et == Defs.ETYPE_DEBRIS) nw++;
                else nd++;
            }
            foreach (int id in world.projectiles())
            {
                var q = e.slot(id);
                if (!q.ok || !q.a.has("pos")) continue;
                int kind = q.a.has("proj_kind") ? q.a.get_u8("proj_kind", q.row) : 0;
                float dx = q.a.get_f("pos", q.row, 0) - ex, dz = q.a.get_f("pos", q.row, 2) - ez;
                float d2 = dx * dx + dz * dz;
                if (d2 > radius_round(kind) * radius_round(kind))
                {
                    // a guided round beyond its tier: invisible, and it is the
                    // one row that flies *at* somebody
                    if (kind != 0 && d2 <= 4300f * 4300f) blind++;
                    continue;
                }
                cur.Add(id);
                np++;
            }
            foreach (int id in e.query("smoke_r"))
            {
                var q = e.slot(id);
                if (!q.ok) continue;
                float dx = q.a.get_f("pos", q.row, 0) - ex, dz = q.a.get_f("pos", q.row, 2) - ez;
                if (dx * dx + dz * dz > R_SMOKE * R_SMOKE) continue;
                cur.Add(id);
                sm++;
            }
            // rows whose `target` is a hull the frame does not carry: the client
            // only ever compares it with its own id (`game.js`), so this is a
            // count of rows that can no longer be read, not a crash
            int dangl = 0;
            foreach (var (_, tgt) in tgts)
                if (tgt >= 0 && !cur.Contains(tgt)) dangl++;

            int raw = Netcode.HEADER + world.all_units().Length * Netcode.UNIT_STRIDE
                      + world.projectiles().Length * Netcode.PROJ_STRIDE
                      + e.query("smoke_r").Length * Netcode.SMOKE_STRIDE;
            int tiered = Netcode.HEADER + nu * Netcode.UNIT_STRIDE + np * Netcode.PROJ_STRIDE
                         + sm * Netcode.SMOKE_STRIDE;

            int inn = 0, outn = 0;
            foreach (int id in cur) if (!prev.Contains(id)) inn++;
            foreach (int id in prev) if (!cur.Contains(id)) outn++;
            prev.Clear();
            prev.UnionWith(cur);

            rawB[min] += raw;
            tierB[min] += tiered;
            nSnap[min]++;
            cVeh[min] += nv;
            cInf[min] += ni;
            cWre[min] += nw;
            cRnd[min] += np;
            cSmk[min] += sm;
            if (sm > maxSmk) maxSmk = sm;
            cIn[min] += inn;
            cOut[min] += outn;
            cBlind[min] += blind;
            cDangl[min] += dangl;
            // the enemy base is a unit row like any other: `_base_kept` asks the
            // same distance question the table does
            bool baseKept = false;
            foreach (int id in world.all_units())
            {
                if (e.uv("etype")[id] != Defs.ETYPE_BASE) continue;
                if (cur.Contains(id)) { baseKept = true; break; }
            }
            if (baseKept) cBase[min]++;
        }
        if (min_now >= 0)
            report(min_now, rawB, tierB, nSnap, cVeh, cInf, cWre, cRnd, cSmk,
                   cIn, cOut, cBlind, cDangl, cBase, cFx, cFx4);

        summary(minutes, (int)from, nSnap, rawB, tierB, cVeh, cInf, cWre, cRnd, cSmk,
                cIn, cOut, cBlind, cDangl, cBase, cFx, cFx4, maxSmk);
        world.ecs.validate();
        Console.WriteLine("ecs.validate() ok");
        return 0;
    }

    private static void report(int min, double[] rawB, double[] tierB, int[] nSnap,
                               long[] cVeh, long[] cInf, long[] cWre, long[] cRnd, long[] cSmk,
                               long[] cIn, long[] cOut, long[] cBlind, long[] cDangl, int[] cBase,
                               int[] cFx, int[] cFx4)
    {
        double n = Math.Max(nSnap[min], 1);
        double raw = rawB[min] / 60 / 1024, tier = tierB[min] / 60 / 1024;
        Console.WriteLine($"{min,3} | {raw,5:0} {tier,6:0} {100 - 100 * tier / Math.Max(raw, 1),5:0}% | "
                          + $"{cVeh[min] / n,3:0} {cInf[min] / n,4:0} {cWre[min] / n,3:0} "
                          + $"{cRnd[min] / n,4:0} {cSmk[min] / n,3:0} | "
                          + $"{cIn[min] / 60.0,6:0} {cOut[min] / 60.0,6:0} | "
                          + $"{cBlind[min] / n,5:0} {cDangl[min] / n,5:0} "
                          + $"{100.0 * cBase[min] / n,3:0}% | "
                          + $"{cFx[min] / 60.0,5:0} {cFx4[min] / 60.0,6:0}");
    }

    private static void summary(int minutes, int from, int[] nSnap, double[] rawB, double[] tierB,
                                long[] cVeh, long[] cInf, long[] cWre, long[] cRnd, long[] cSmk,
                                long[] cIn, long[] cOut, long[] cBlind, long[] cDangl, int[] cBase,
                                int[] cFx, int[] cFx4, int maxSmk)
    {
        double sRaw = 0, sTier = 0;
        long snap = 0, veh = 0, inf = 0, wre = 0, rnd = 0, smk = 0;
        long inn = 0, outn = 0, blind = 0, dangl = 0, nbase = 0, fx = 0, fx4 = 0;
        for (int k = from; k <= minutes; k++)
        {
            sRaw += rawB[k]; sTier += tierB[k];
            snap += nSnap[k]; veh += cVeh[k]; inf += cInf[k]; wre += cWre[k];
            rnd += cRnd[k]; smk += cSmk[k];
            inn += cIn[k]; outn += cOut[k]; blind += cBlind[k]; dangl += cDangl[k];
            nbase += cBase[k]; fx += cFx[k]; fx4 += cFx4[k];
        }
        double secs = Math.Max(1, minutes - from) * 60.0;
        double n = Math.Max(snap, 1);
        Console.WriteLine();
        Console.WriteLine($"-- minutes {from}..{minutes}");
        Console.WriteLine($"   snapshot {sRaw / secs / 1024:0.0} kB/s -> {sTier / secs / 1024:0.0} kB/s "
                          + $"({100 * sTier / Math.Max(sRaw, 1):0}%, saves {(sRaw - sTier) / secs / 1024:0.0} kB/s)");
        Console.WriteLine($"   kept rows per frame: {veh / n:0} mech/APC, {inf / n:0} infantry, "
                          + $"{wre / n:0} wreck, {rnd / n:0} round, {smk / n:0} smoke "
                          + $"({(veh + inf + wre + rnd + smk) / n:0} rows x 15 Hz)");
        Console.WriteLine();
        Console.WriteLine("   what the tier table does not have a line for:");
        Console.WriteLine($"   - the client needs hulls out to its fire control, not 6 km: a row is used as a");
        Console.WriteLine($"     blocker by the centre cross / impact solve out to ZERO_RANGE 4000 m and by the");
        Console.WriteLine($"     laser warning out to 3400 m, so 6 km is safe and 4 km would be too");
        Console.WriteLine($"   - the enemy command base is in {100.0 * nbase / n:0}% of the frames "
                          + $"(\"everything else\" = 2 km drops it): 2 rows x 80 B x 15 Hz = 2.4 kB/s to always send");
        Console.WriteLine($"   - smoke: {smk / n:0.0} puff rows per frame on average, {maxSmk} at peak "
                          + $"(= {maxSmk * Netcode.SMOKE_STRIDE * 15 / 1024:0.0} kB/s while a screen is up);");
        Console.WriteLine($"     map.js's sightBlocked() reads puffs out to the 4 km the fire control reaches, so the");
        Console.WriteLine($"     6 km tier covers it with 2 km to spare (and a puff is 32 B, so the spare is cheap)");
        Console.WriteLine($"   - guided rounds off the wire in the 3.0-4.3 km band: {blind / n:0.00} per frame, "
                          + $"{blind / secs:0.00}/s");
        Console.WriteLine($"     (top attack locks at 4000 m and glides 4.3 km; the receiver's own gate is 2 km,");
        Console.WriteLine($"      so no warning is lost - what is lost is the sight of the round for its first km)");
        Console.WriteLine($"   - explosions are not rows at all: 0 blast entities exist.  A boom rides the fx");
        Console.WriteLine($"     channel ({fx / secs:0.0} events/s to this ear, {fx4 / secs:0.0} of them inside 4 km),");
        Console.WriteLine($"     which today is filtered by *sound* level (cannon 7.2 km, a death blast 17 km) -");
        Console.WriteLine($"     a 4 km visual radius is a change to fx, not to the snapshot");
        Console.WriteLine();
        Console.WriteLine("   what it costs on the client side (no code change, only behaviour):");
        Console.WriteLine($"   - entities pop in and out at the tier edges: {inn / secs:0} rows enter and "
                          + $"{outn / secs:0} leave per second, and net.js deletes on absence, so a hull");
        Console.WriteLine($"     sitting on a circle is re-created (position snap, smoothing reset) every crossing");
        Console.WriteLine($"     -> a dead band on the server (take at R, drop at 1.1 R) is the whole fix");
        Console.WriteLine($"   - rows whose `target` is a dropped hull: {dangl / n:0.0} per frame - harmless today");
        Console.WriteLine($"     (the client only compares it with its own id), but it is a field that stops");
        Console.WriteLine($"     meaning anything the moment somebody prints it");
    }
}
