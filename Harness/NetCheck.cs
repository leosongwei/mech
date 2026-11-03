// Wire budget: what one ordinary client actually downloads, minute by minute.
//
//   ./dotnet.sh run -- --check net --minutes 30 [--from 8]
//
// `Server.game_loop` sleeps 1/30 s between ticks, so a real socket can only ever
// be measured in real time.  This drives the same `World` flat out and prices
// every payload `Server.broadcast` would have offered one pilot, off the same
// doors (`Netcode.snapshot_bytes`, `Netcode.meta_payload`, `Netcode.hearable`),
// at the same cadence (`SNAP_EVERY` / `META_EVERY`) - so the numbers are the
// wire, not an estimate of it, and a 30 minute battle costs a minute of CPU.
//
// The pilot is `Check.drive`'s scripted pilot: a normal machine, moving, firing
// and pulling smoke.  His ear position is what decides how many fx events he is
// sent (`hearable` drops the bangs under `SOUND_FLOOR`), so a run measures *a*
// client and not *every* client: the fx half of the budget moves with the
// address, the snapshot half does not (it is not per ear at all).

using Mech.Server;

namespace Mech.Server.Harness;

public static class NetCheck
{
    /// <summary>Payload bytes + the websocket frame header they ride in: a
    /// server-to-client frame is unmasked, so the header is 2/4/10 bytes.</summary>
    private static int frame(int len) => len < 126 ? 2 : len < 65536 ? 4 : 10;

    private static Dictionary<string, object?> fx_msg(List<Dictionary<string, object?>> ev) =>
        new() { ["t"] = "fx", ["events"] = ev };

    public static int bandwidth(int minutes, float from = 8f, int seed = 11)
    {
        var world = new World(seed, "battle");
        var pl = world.add_player("PILOT");
        Console.WriteLine($"battle world: {world.terrain.w}x{world.terrain.d} tiles, "
                          + $"{world.towns.Count} towns, {world.lanes.Count} lanes, pilot {pl.name}");
        var init = Netcode.static_payload(world);
        Console.WriteLine($"one-off init frame: {Server.json(init).Length / 1024} kB "
                          + $"(zlib {Netcode._b64(Server.json(init)).Length / 1024} kB on the wire)");
        Console.WriteLine();
        Console.WriteLine("min |   snap    fx  meta   total  Mbit/s | frame rows: all veh  inf dead  rnd  smk | field | fx/s");
        Console.WriteLine("----+------------------------------------------+-------+-------------------");

        const float dt = 1f / 30f;
        int ticks = (int)(minutes * 60f / dt);
        int nb = minutes + 1;
        var snapB = new double[nb];
        var conB = new double[nb];
        var fxB = new double[nb];
        var metaB = new double[nb];
        var headB = new double[nb];
        var snapN = new int[nb];
        var unitSum = new long[nb];
        var projSum = new long[nb];
        var smokeSum = new long[nb];
        var nearSum = new long[nb];     // unit rows a 2 km interest sphere would keep
        var fxEv = new int[nb];
        var fxKeep = new int[nb];
        var metaN = new int[nb];
        double cpuFx = 0, cpuSnap = 0, cpuMeta = 0;
        var view = new Netcode.SnapshotView();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int last_min = -1;
        var e = world.ecs;
        for (int t = 0; t < ticks; t++)
        {
            Check.drive(world, pl, t);
            world.update(dt);
            int tick = world.tick;
            // the server takes the events and clears them every tick, whatever
            // the tick's cadence is - a headless run has to do the same or the
            // buffer grows into a 400 event flood
            List<Dictionary<string, object?>>? fx = null;
            if (world.events.Count > 0)
            {
                fx = new List<Dictionary<string, object?>>(world.events);
                world.events.Clear();
            }
            if (world.time < from * 60f) continue;

            (float x, float y, float z)? ear = null;
            if (pl.mech >= 0 && e.is_alive(pl.mech))
            {
                var p = e.fv("pos").get3(pl.mech);
                ear = (p.x, p.y, p.z);
            }
            int min = Math.Min((int)(world.time / 60f), nb - 1);
            if (min != last_min)
            {
                if (last_min >= 0) report(world, view, last_min, minutes, snapB, fxB, metaB, headB,
                                          snapN, unitSum, projSum, smokeSum, fxEv, fxKeep);
                last_min = min;
            }

            if (tick % Server.SNAP_EVERY == 0)
            {
                long q0 = System.Diagnostics.Stopwatch.GetTimestamp();
                view.at(world, pl);
                // the minimap block is built once and shared, exactly as
                // `Server.broadcast` does - it is part of the frame, so it is
                // part of the download
                byte[]? contacts = tick % Netcode.CONTACT_EVERY == 0
                    ? Netcode.contacts_bytes(world) : null;
                var snap = Netcode.snapshot_bytes(world, view, contacts);
                cpuSnap += System.Diagnostics.Stopwatch.GetElapsedTime(q0).TotalMilliseconds;
                snapB[min] += snap.Length;
                conB[min] += contacts is null ? 0 : contacts.Length;
                headB[min] += frame(snap.Length);
                snapN[min]++;
                // the frame's own rows, off its header (five class counts, then
                // rounds, smoke and the minimap block) - the field is only the
                // pond it was picked from now that the wire is a tier sphere
                for (int c = 0; c < Netcode.ROWS; c++)
                    unitSum[min] += BitConverter.ToUInt16(snap, 8 + c * 2);
                projSum[min] += BitConverter.ToUInt16(snap, 18);
                smokeSum[min] += BitConverter.ToUInt16(snap, 20);
                nearSum[min] += (pl.mech >= 0 && e.is_alive(pl.mech)) ? near(world, pl.mech, 2000f) : 0;
            }
            if (fx is not null)
            {
                long q0 = System.Diagnostics.Stopwatch.GetTimestamp();
                var mine = Netcode.hearable(fx, ear);
                // exactly `Server.broadcast`: rounds as binary rows, words as JSON
                int payload = 0;
                if (mine.Count > 0)
                {
                    byte[]? bin = Netcode.fx_bytes(mine, out var words);
                    if (bin is not null)
                    {
                        payload += bin.Length;
                        headB[min] += frame(bin.Length);
                    }
                    if (words.Count > 0)
                    {
                        var w = Server.json(fx_msg(words));
                        payload += w.Length;
                        headB[min] += frame(w.Length);
                    }
                }
                cpuFx += System.Diagnostics.Stopwatch.GetElapsedTime(q0).TotalMilliseconds;
                fxB[min] += payload;
                fxEv[min] += fx.Count;
                fxKeep[min] += mine.Count;
            }
            if (tick % Server.META_EVERY == 0)
            {
                long q0 = System.Diagnostics.Stopwatch.GetTimestamp();
                var m = Server.json(Netcode.meta_payload(world, pl));
                cpuMeta += System.Diagnostics.Stopwatch.GetElapsedTime(q0).TotalMilliseconds;
                metaB[min] += m.Length;
                headB[min] += frame(m.Length);
                metaN[min]++;
            }
        }
        if (last_min >= 0)
            report(world, view, last_min, minutes, snapB, fxB, metaB, headB,
                   snapN, unitSum, projSum, smokeSum, fxEv, fxKeep);
        sw.Stop();

        // ---- summary over the whole measured window ----
        double sSnap = 0, sFx = 0, sMeta = 0, sHead = 0, sCon = 0;
        long sUnits = 0, sProj = 0, sSmoke = 0, sNear = 0;
        int sSnaps = 0, sMetas = 0, peak = -1;
        for (int k = (int)from; k <= minutes; k++)
        {
            sSnap += snapB[k]; sFx += fxB[k]; sMeta += metaB[k]; sHead += headB[k];
            sCon += conB[k];
            sUnits += unitSum[k]; sProj += projSum[k]; sSmoke += smokeSum[k];
            sNear += nearSum[k];
            sSnaps += snapN[k]; sMetas += metaN[k];
            if (peak < 0 || snapB[k] > snapB[peak]) peak = k;
        }
        double secs = Math.Max(1, minutes - (int)from) * 60.0;
        double total = sSnap + sFx + sMeta + sHead;
        Console.WriteLine();
        Console.WriteLine($"-- minutes {(int)from}..{minutes}: mean "
                          + $"{total / secs / 1024:0.0} kB/s ({total / secs * 8 / 1e6:0.00} Mbit/s), "
                          + $"{total / 1e6:0.0} MB downloaded");
        Console.WriteLine($"   snapshot {sSnap / secs / 1024,7:0.0} kB/s  {100 * sSnap / total,4:0}%    "
                          + $"fx {sFx / secs / 1024,7:0.0} kB/s  {100 * sFx / total,3:0}%    "
                          + $"meta {sMeta / secs / 1024,5:0.0} kB/s  {100 * sMeta / total,3:0}%    "
                          + $"frames {sHead / secs / 1024,4:0.0} kB/s  {100 * sHead / total,3:0}%");
        Console.WriteLine($"   of it the minimap block {sCon / secs / 1024,6:0.0} kB/s ({100 * sCon / Math.Max(sSnap, 1):0}% of the snapshots, "
                          + $"{sCon / Math.Max(1, (int)(secs * 30.0 / Netcode.CONTACT_EVERY)):0} B x {30.0 / Netcode.CONTACT_EVERY:0.00} Hz)");
        // one snapshot, broken into the rows it is made of: this is where the
        // download goes, so the split is the whole diagnosis
        double per_snap = sSnaps > 0 ? (double)sSnaps : 1;
        double uRow = (double)sUnits / per_snap, pRow = (double)sProj / per_snap;
        double mRow = (double)sSmoke / per_snap;
        double snapBytes = sSnap / per_snap;
        double uBytes = snapBytes - Netcode.HEADER - pRow * Netcode.PROJ_STRIDE
                        - mRow * Netcode.SMOKE_STRIDE
                        - sCon / per_snap;      // the minimap block rides every 8th tick
        double hz = Server.SNAP_EVERY > 0 ? 30.0 / Server.SNAP_EVERY : 30.0;
        Console.WriteLine($"   one frame: {snapBytes / 1024:0.00} kB x {hz:0} Hz  =  "
                          + $"units {uBytes:0} B x {uRow:0} rows ({uBytes / uRow:0} B each, {100 * uBytes / snapBytes:0}%),  "
                          + $"rounds {pRow * Netcode.PROJ_STRIDE:0} B x {pRow:0} rows ({100 * pRow * Netcode.PROJ_STRIDE / snapBytes:0}%),  "
                          + $"smoke {mRow * Netcode.SMOKE_STRIDE:0} B x {mRow:0.0}");
        Console.WriteLine($"   one meta: {sMeta / Math.Max(sMetas, 1):0} B x {30.0 / Server.META_EVERY:0} Hz "
                          + $"({100 * sMeta / total:0}% of the wire)");
        double nearUnits = (double)sNear / Math.Max(sSnaps, 1), nearBytes = Netcode.HEADER
                          + nearUnits * (uBytes / Math.Max(uRow, 1)) + pRow * Netcode.PROJ_STRIDE
                          + mRow * Netcode.SMOKE_STRIDE;
        Console.WriteLine($"   the frame carries {uRow:0} unit rows; a 2 km sphere alone would carry "
                          + $"{nearUnits:0} ({rate((long)nearBytes, hz, 1024):0} kB/s of the "
                          + $"{rate((long)snapBytes, hz, 1024):0})");
        Console.WriteLine($"   heaviest minute: {peak} ({snapB[peak] / 60 / 1024:0.0} kB/s snapshots)");
        whatif(world, pl);
        Console.WriteLine($"   wire CPU over the window: snapshot {cpuSnap / 1000:0.00} s, "
                          + $"fx {cpuFx / 1000:0.00} s, meta {cpuMeta / 1000:0.00} s "
                          + $"({(cpuSnap + cpuFx + cpuMeta) / (ticks * dt) / 1000:0.000} ms/simulated second)");
        Console.WriteLine($"-- {minutes} min simulated in {sw.Elapsed.TotalSeconds:0.0} s "
                          + $"({ticks / sw.Elapsed.TotalSeconds:0} ticks/s)");
        world.ecs.validate();
        Console.WriteLine("ecs.validate() ok");
        return 0;
    }

    /// <summary>What one snapshot of the same field costs under the levers a wire
    /// has - compress it, stop sending what the pilot cannot use, send a leaner
    /// row.  Not a proposal, a ceiling: each line is measured on the field the
    /// run just produced, so the next decision is a number and not a guess.</summary>
    private static void whatif(World world, Player pl)
    {
        var e = world.ecs;
        var raw = Netcode.snapshot_bytes(world, new Netcode.SnapshotView { all = true });
        int nu = world.all_units().Length;
        int np = world.projectiles().Length;
        int ns = e.query("smoke_r").Length;
        double hz = 30.0 / Server.SNAP_EVERY;
        const double kb = 1024.0;
        Console.WriteLine();
        Console.WriteLine($"   what the same field could ride in (t = {world.time / 60f:0.0} min, "
                          + $"{nu} unit + {np} round + {ns} smoke rows):");
        Console.WriteLine($"     as it is now      {raw.Length / kb,7:0.00} kB x {hz:0} Hz = {rate(raw.Length, hz, kb),6:0} kB/s");
        var z = Netcode._zlib(raw);
        Console.WriteLine($"     deflated          {z.Length / kb,7:0.00} kB x {hz:0} Hz = {rate(z.Length, hz, kb),6:0} kB/s"
                          + $"   ({100.0 * z.Length / raw.Length:0}% of raw)");

        var p = pl.mech >= 0 && e.is_alive(pl.mech) ? e.fv("pos").get3(pl.mech) : default;
        foreach (float r in new[] { 800f, 1500f, 3000f })
        {
            int ku = 0, kp = 0;
            long ub = 0;
            foreach (int eid in world.all_units())
            {
                var q = e.slot(eid);
                if (!q.ok || !q.a.has("pos")) continue;
                float dx = q.a.get_f("pos", q.row, 0) - p.x, dz = q.a.get_f("pos", q.row, 2) - p.z;
                if (dx * dx + dz * dz > r * r) continue;
                ku++;
                ub += Netcode.row_bytes(e.uv("etype")[eid]);
            }
            foreach (int eid in world.projectiles())
            {
                var q = e.slot(eid);
                if (!q.ok) continue;
                float dx = q.a.get_f("pos", q.row, 0) - p.x, dz = q.a.get_f("pos", q.row, 2) - p.z;
                if (dx * dx + dz * dz <= r * r) kp++;
            }
            long bytes = Netcode.HEADER + ub
                         + (long)kp * Netcode.PROJ_STRIDE + (long)ns * Netcode.SMOKE_STRIDE;
            Console.WriteLine($"     inside {r,4:0} m     {bytes / kb,7:0.00} kB x {hz:0} Hz = {rate(bytes, hz, kb),6:0} kB/s"
                              + $"   ({ku} unit + {kp} round rows)");
        }

        // infantry are decoration past a few hundred metres, and they are most
        // of the field: what a per-type reach costs
        int inf = 0, far = 0;
        foreach (int eid in world.all_units())
        {
            int et = e.uv("etype")[eid];
            if (et is not (Defs.ETYPE_INF_RIFLE or Defs.ETYPE_INF_ROCKET or Defs.ETYPE_CIV)) continue;
            inf++;
            var q = e.slot(eid);
            float dx = q.a.get_f("pos", q.row, 0) - p.x, dz = q.a.get_f("pos", q.row, 2) - p.z;
            if (dx * dx + dz * dz > 400f * 400f) far++;
        }
        long noFar = raw.Length - (long)far * Netcode.row_bytes(Defs.ETYPE_INF_RIFLE);
        Console.WriteLine($"     infantry >400 m   {noFar / kb,7:0.00} kB x {hz:0} Hz = {rate(noFar, hz, kb),6:0} kB/s"
                          + $"   ({inf} infantry, {far} of them out of range)");

        // and the floor: quantise the pose.  `pos` / `vel` are f32 today; an
        // int16 pair (1 m, 1 m/s) carries the same battle in half the bytes for
        // everything that has one - which is what a delta scheme would do next.
        long quant = Netcode.HEADER + (long)np * 20 + (long)ns * 32;
        foreach (int eid in world.all_units())
        {
            int et = e.uv("etype")[eid];
            quant += Netcode.row_bytes(et) - (et == Defs.ETYPE_BASE ? 6 : 12);
        }
        Console.WriteLine($"     int16 pos/vel     {quant / kb,7:0.00} kB x {hz:0} Hz = {rate(quant, hz, kb),6:0} kB/s"
                          + $"   (pose as 1 m / 1 m/s shorts)");
    }

    /// <summary>bytes per simulated second at a snapshot rate</summary>
    private static double rate(long b, double hz, double kb) => b * hz / kb;

    /// <summary>How many unit rows a sphere of radius `r` around `eid` holds: the
    /// cheapest proxy for "what this pilot could actually use" (`Grid` could
    /// answer it, but this must count rows, not candidates).</summary>
    private static int near(World world, int eid, float r)
    {
        var e = world.ecs;
        var p = e.fv("pos").get3(eid);
        int n = 0;
        foreach (int id in world.all_units())
        {
            var q = e.slot(id);
            if (!q.ok || !q.a.has("pos")) continue;
            float dx = q.a.get_f("pos", q.row, 0) - p.x, dz = q.a.get_f("pos", q.row, 2) - p.z;
            if (dx * dx + dz * dz <= r * r) n++;
        }
        return n;
    }

    private static void report(World world, Netcode.SnapshotView view, int min, int minutes,
                               double[] snapB, double[] fxB, double[] metaB, double[] headB,
                               int[] snapN, long[] unitSum, long[] projSum, long[] smokeSum,
                               int[] fxEv, int[] fxKeep)
    {
        double snap = snapB[min] / 60.0 / 1024, fx = fxB[min] / 60.0 / 1024;
        double meta = metaB[min] / 60.0 / 1024;
        double head = headB[min] / 60.0 / 1024;
        double total = snap + fx + meta + head;
        double units = snapN[min] > 0 ? (double)unitSum[min] / snapN[min] : 0;
        double proj = snapN[min] > 0 ? (double)projSum[min] / snapN[min] : 0;
        double smoke = snapN[min] > 0 ? (double)smokeSum[min] / snapN[min] : 0;
        double field = world.all_units().Length;

        // what this pilot's frame carries right now: a hull, a wreck and a
        // corpse are all one 80 B row on the wire
        int inf = 0, veh = 0, dead = 0;
        var et = world.ecs.uv("etype");
        foreach (int eid in world.all_units())
        {
            if (!view.keep.Contains(eid)) continue;
            switch (et[eid])
            {
                case Defs.ETYPE_INF_RIFLE or Defs.ETYPE_INF_ROCKET or Defs.ETYPE_CIV: inf++; break;
                case Defs.ETYPE_APC or Defs.ETYPE_MECH or Defs.ETYPE_BASE or Defs.ETYPE_BUNKER: veh++; break;
                default: dead++; break;
            }
        }
        int rnd = 0;
        foreach (int id in world.projectiles()) if (view.keep.Contains(id)) rnd++;
        int puf = 0;
        foreach (int id in world.ecs.query("smoke_r")) if (view.keep.Contains(id)) puf++;
        Console.WriteLine($"{min,3} | {snap,6:0.0} {fx,5:0.0} {meta,6:0.0} {total,7:0.0} {total * 8 / 1000,7:0.00} | "
                          + $"{units,5:0} {veh,3} {inf,4} {dead,4} {rnd,5:0} {puf,4:0} | {field,4:0} | "
                          + $"{fxEv[min] / 60.0,5:0} ({100.0 * (fxEv[min] - fxKeep[min]) / Math.Max(fxEv[min], 1),3:0}% kept)");
    }
}
