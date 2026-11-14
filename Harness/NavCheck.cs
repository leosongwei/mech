// `--check nav`: what the facet navmesh costs and what it decides.
//
//   ./dotnet.sh run -- --check nav
//
// Five columns, each one a question the layer has to answer:
//
//   region   - what a window costs to build (bytes and milliseconds), because it
//              is built per request and nothing caches it;
//   profile  - how much of the ground each chassis can reach from one point: the
//              APC has to be shut out of the steep bits and the rifleman not, or
//              the slope limits are decoration;
//   ridge    - a plan across the steepest ground the map has: does it go round, how
//              far is the detour, how many facets does A* expand;
//   budget   - six requests in one tick, so the per-tick expansion budget is visible
//              instead of the 15 ms spike the raider's synchronous plan used to be;
//   lane     - three crew APCs driven down the three lanes by the real pipeline for
//              four minutes: metres covered, seconds spent pushing at something, and
//              how many detours the navmesh handed out.

using Mech.Server;
using Mech.Server.Systems.AI;

namespace Mech.Server.Harness;

public static class NavCheck
{
    public static int run()
    {
        var world = new World(11, "battle");
        var t = world.terrain;
        Console.WriteLine($"world: {t.w}x{t.d} tiles, {t.lanes.Count} lanes");

        region(t);
        profile(t);
        ridge(t);
        budget(t);
        raiders(world);
        drive();
        walk();
        lane(world);
        return 0;
    }

    /// <summary>The strategic grid the raiders plan on: 128 m nodes, and the two
    /// questions each of them answers for a chassis - is there a wall in it, and can
    /// the machine climb what is left.  The second one is new, so what this row has
    /// to show is that it closes the faces and cuts nothing off: how many nodes go,
    /// and whether every town still has a route from both bases.</summary>
    private static void raiders(World world)
    {
        var t = world.terrain;
        var (_, force) = Raid._nav(world, Defs.FACTION_RED);
        var profile = NavProfile.of(Defs.ETYPE_MECH);
        Console.WriteLine($"\nraider grid: {Raid.NAV_CELL:0} m nodes for a raiding party of mechs "
                          + $"(max_grad {profile.max_grad:0.00}), a node closes when "
                          + $"{Raid.FieldNav.STEEP_SHARE:0.00} of its facets are over that limit");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var nav = new Raid.FieldNav(t, force, profile);
        double ms = sw.Elapsed.TotalMilliseconds;
        int nodes = nav.cw * nav.cd;
        var (bx, bz) = world.terrain.bases[Defs.FACTION_RED];
        var reach = _flood(nav, nav.cell(bx, bz));
        int towns = 0, ok = 0;
        foreach (var town in world.towns)
        {
            towns++;
            var c = nav.cell(town.x, town.z);
            if (reach[c.j * nav.cw + c.i]) ok++;
        }
        Console.WriteLine($"  {nav.closed_wall} nodes walled, {nav.closed_steep} too steep "
                          + $"of {nodes}; the steepest node on the map is "
                          + $"{nav.worst_steep * 100f:0} % of its facets over 35 degrees; "
                          + $"{ok}/{towns} towns reachable from the red base; "
                          + $"{ms:0.0} ms to build");
    }

    /// <summary>Every node reachable from one over open ground, 8-connected.</summary>
    private static bool[] _flood(Raid.FieldNav nav, (int i, int j) from)
    {
        var seen = new bool[nav.cw * nav.cd];
        var q = new Queue<(int i, int j)>();
        var start = Raid._drivable(nav, from);
        if (start is null) return seen;
        seen[start.Value.j * nav.cw + start.Value.i] = true;
        q.Enqueue(start.Value);
        while (q.Count > 0)
        {
            var (i, j) = q.Dequeue();
            for (int dj = -1; dj <= 1; dj++)
            {
                for (int di = -1; di <= 1; di++)
                {
                    if (di == 0 && dj == 0) continue;
                    int i2 = i + di, j2 = j + dj;
                    if (i2 < 0 || j2 < 0 || i2 >= nav.cw || j2 >= nav.cd) continue;
                    int n = j2 * nav.cw + i2;
                    if (seen[n] || !nav.open[n]) continue;
                    seen[n] = true;
                    q.Enqueue((i2, j2));
                }
            }
        }
        return seen;
    }

    /// <summary>What one window costs: a 1 km plan and a 2 km one.</summary>
    private static void region(Terrain t)
    {
        foreach (float span in new[] { 1000f, 2000f })
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int n = 0;
            for (int k = 0; k < 20; k++)
            {
                var r = new NavRegion(t, 4000f + k * 10f, 4000f, 4000f + span, 4000f + span * 0.6f);
                n = r.count;
            }
            sw.Stop();
            double ms = sw.Elapsed.TotalMilliseconds / 20;
            Console.WriteLine($"\nregion {span:0} m: {n / 2} tiles / {n} facets, "
                              + $"{ms:0.00} ms to build, {n * 3 + 4 * n / 2:0} B of state");
        }
    }

    /// <summary>How much of a 2 km square each chassis can reach from one corner.</summary>
    private static void profile(Terrain t)
    {
        // a hilly square, not a plains one: the middle of the map is open country
        // and every profile reaches all of it, which says nothing
        float x0 = 11500f, z0 = 12500f, span = 2000f;
        var r = new NavRegion(t, x0, z0, x0 + span, z0 + span, pad: 0f);
        Console.WriteLine("\nreachable area of a 2 km square (flood fill from its middle):");
        int[] nb = new int[3];
        foreach (var (etype, def) in Defs.UNIT_DEFS)
        {
            if (def.speed <= 0f) continue;
            var p = NavProfile.of(etype);
            var seen = new bool[r.count];
            var stack = new Stack<int>();
            int start = r.node(x0 + span * 0.5f, z0 + span * 0.5f);
            if (start < 0) { Console.WriteLine("  (window has no middle)"); return; }
            seen[start] = true;
            stack.Push(start);
            int reached = 0;
            while (stack.Count > 0)
            {
                int n = stack.Pop();
                reached++;
                r.neighbours(n, nb, out int cnt);
                for (int e = 0; e < cnt; e++)
                {
                    int m = nb[e];
                    if (seen[m] || !r.can(m, p)) continue;
                    seen[m] = true;
                    stack.Push(m);
                }
            }
            Console.WriteLine($"  {def.name,-14} max_grad {p.max_grad:0.00}: "
                              + $"{100.0 * reached / r.count,5:0.0}% reachable");
        }
    }

    /// <summary>A plan across the steepest ground on the map.</summary>
    private static void ridge(Terrain t)
    {
        // find the steepest facet, and then ask for the longest path in the game
        float best = 0f, bx = 0f, bz = 0f;
        for (int j = 40; j + 1 < t.d - 40; j += 4)
            for (int i = 40; i + 1 < t.w - 40; i += 4)
            {
                int k = j * t.w + i;
                float a = t.h[k], b = t.h[k + 1], c = t.h[k + t.w], d = t.h[k + t.w + 1];
                float ga = MathF.Sqrt((b - a) * (b - a) + (c - a) * (c - a)) / Terrain.TILE;
                float gb = MathF.Sqrt((c - d) * (c - d) + (b - d) * (b - d)) / Terrain.TILE;
                if (ga > best) { best = ga; bx = i * Terrain.TILE; bz = j * Terrain.TILE; }
                if (gb > best) { best = gb; bx = i * Terrain.TILE; bz = j * Terrain.TILE; }
            }
        // the anti-diagonal: it crosses all three lanes and every ridge on the way.
        // (The map's *other* diagonal is the mid lane itself - a 14 km straight road,
        // which the predicate correctly calls clear, and says nothing.)
        float ax = 2000f, az = 14000f, bx2 = 14000f, bz2 = 2000f;
        double straight0 = Math.Sqrt(Sq(bx2 - ax) + Sq(bz2 - az));
        Console.WriteLine($"\nridge: steepest facet {best:0.00} at ({bx:0},{bz:0}) m; "
                          + "and a cross-country plan 12.7 km across the anti-diagonal, "
                          + "which every lane and ridge on the map lies across");
        foreach (int etype in new[] { Defs.ETYPE_MECH, Defs.ETYPE_APC })
        {
            var p = NavProfile.of(etype);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var pts = Paths.plan(t, p, ax, az, bx2, bz2,
                                 out int facets, out int exp, budget: 4000000);
            sw.Stop();
            double straight = straight0;
            double len = 0.0;
            for (int k = 1; k < pts.Count; k++)
                len += Math.Sqrt(Sq(pts[k].x - pts[k - 1].x) + Sq(pts[k].z - pts[k - 1].z));
            Console.WriteLine($"  {Defs.UNIT_DEFS[etype].name,-6} max_grad {p.max_grad:0.00}: "
                              + (pts.Count == 0
                                  ? "no path"
                                  : $"{len:0} m ({len / straight:0.00}x straight), {pts.Count} way points"
                                    + $" from {facets} facets / {exp} expansions")
                              + $", {sw.Elapsed.TotalMilliseconds:0.0} ms");
            Console.WriteLine($"    the straight line itself: "
                              + $"{(NavWalk.clear(t, p, ax, az, bx2, bz2) ? "clear" : "blocked")}");
        }
    }

    /// <summary>Six plans started in one tick, and where the work lands.</summary>
    private static void budget(Terrain t)
    {
        var p = NavProfile.of(Defs.ETYPE_MECH);
        var live = new List<NavQuery>();
        for (int k = 0; k < 6; k++)
        {
            float sx = 2000f + k * 120f, sz = 2000f;
            live.Add(new NavQuery(new NavRegion(t, sx, sz, sx + 900f, sz + 700f),
                                  p, sx, sz, sx + 900f, sz + 700f));
        }
        int ticks = 0, total = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (live.Count > 0 && ticks < 600)
        {
            ticks++;
            for (int k = live.Count - 1; k >= 0; k--)
                if (live[k].step(Math.Max(200, Paths.NODES_PER_TICK / live.Count)))
                {
                    total += live[k].expansions;
                    live.RemoveAt(k);
                }
        }
        sw.Stop();
        Console.WriteLine($"\nbudget: six plans, {Paths.NODES_PER_TICK} expansions/tick across them: "
                          + $"{ticks} ticks ({ticks / 30.0:0.00} s), {total} expansions, "
                          + $"{sw.Elapsed.TotalMilliseconds * 1000.0 / Math.Max(1, total):0.00} us per expansion");
    }

    /// <summary>One crew APC on the empty range map, told to drive 2 km: the nav and
    /// the driver on their own, with nothing shooting at either of them.</summary>
    private static void drive()
    {
        var world = new World(7, "range");
        var t = world.terrain;
        // across the range's 200 m hill, so the ground is in the way
        float sx = 300f, sz = 2900f, gx = 1800f, gz = 600f;
        // a crew with no mission: `Front` drives the hulls that carry
        // `front_mission`, and this one is nobody's business but the nav's
        int eid = world.spawn_unit(Defs.ETYPE_APC, 0, sx, sz, crew: true,
                                   extra: new[] { AiTags.ENGAGE });
        world.ecs.iv("nav_route")[eid] = -1;
        world.ecs.fv("nav_x")[eid] = gx;
        world.ecs.fv("nav_z")[eid] = gz;
        world.ecs.fv("nav_speed")[eid] = Defs.UNIT_DEFS[Defs.ETYPE_APC].speed;
        int asked0 = Paths.asked, delivered0 = Paths.delivered;
        const float dt = 1f / 30f;
        double straight = Math.Sqrt(Sq(gx - sx) + Sq(gz - sz));
        double best = double.MaxValue;
        float reached_at = -1f;
        for (int tick = 0; tick < 30 * 200; tick++)
        {
            world.update(dt);
            var slot = world.ecs.slot(eid);
            if (!slot.ok) break;
            var b = slot.a;
            int row = slot.row;
            float x = b.f("pos")[row * 3], z = b.f("pos")[row * 3 + 2];
            double d = Math.Sqrt(Sq(x - gx) + Sq(z - gz));
            if (d < best) best = d;
            if (d < 25 && reached_at < 0) reached_at = tick * dt;
        }
        Console.WriteLine($"\ndrive: one crew APC on the range, {straight:0} m across the hill");
        Console.WriteLine($"  {(reached_at >= 0 ? $"arrived in {reached_at:0.0} s ({straight / reached_at:0.0} m/s average)" : "never arrived")}"
                          + $", closest {best:0} m, detours asked {Paths.asked - asked0} "
                          + $"(delivered {Paths.delivered - delivered0}), "
                          + $"top speed {Defs.UNIT_DEFS[Defs.ETYPE_APC].speed:0.0} m/s");
    }

    /// <summary>The same question for a squad on foot: twelve crew riflemen on the
    /// empty range, told to walk 600 m over the hill.</summary>
    private static void walk()
    {
        var world = new World(7, "range");
        var ids = new List<int>();
        var goals = new List<(float, float)>();
        for (int k = 0; k < 12; k++)
        {
            float sx = 300f + (k % 4) * 40f, sz = 2900f + (k / 4) * 40f;
            // open ground on the other side of the hill, not on its flank: a
            // rifleman's own limit (`FOOT_MAX_GRADIENT` 0.80) cannot climb the 0.85
            // faces, which is the ground's job and not this row's
            float gx = 1600f + (k % 4) * 40f, gz = 2600f + (k / 4) * 40f;
            int eid = world.spawn_unit(Defs.ETYPE_INF_RIFLE, 0, sx, sz, crew: true,
                                       extra: new[] { AiTags.ENGAGE });
            world.ecs.iv("nav_route")[eid] = -1;
            world.ecs.fv("nav_x")[eid] = gx;
            world.ecs.fv("nav_z")[eid] = gz;
            world.ecs.fv("nav_speed")[eid] = Defs.UNIT_DEFS[Defs.ETYPE_INF_RIFLE].speed;
            ids.Add(eid);
            goals.Add((gx, gz));
        }
        int asked0 = Paths.asked;
        int arrived = 0;
        const float dt = 1f / 30f;
        for (int tick = 0; tick < 30 * 300; tick++) world.update(dt);
        for (int k = 0; k < ids.Count; k++)
        {
            var slot = world.ecs.slot(ids[k]);
            if (!slot.ok) continue;
            var b = slot.a;
            int row = slot.row;
            float x = b.f("pos")[row * 3], z = b.f("pos")[row * 3 + 2];
            if (MathF.Sqrt(Sq(x - goals[k].Item1) + Sq(z - goals[k].Item2)) < 25f) arrived++;
        }
        Console.WriteLine($"\nwalk: twelve crew riflemen, 1.8 km across the range, 300 s");
        Console.WriteLine($"  {arrived}/12 arrived, detours asked {Paths.asked - asked0}");
        for (int k = 0; k < Math.Min(3, ids.Count); k++)
        {
            var slot = world.ecs.slot(ids[k]);
            if (!slot.ok) { Console.WriteLine($"  walker {k}: dead"); continue; }
            var b = slot.a;
            int row = slot.row;
            float x = b.f("pos")[row * 3], z = b.f("pos")[row * 3 + 2];
            int route = b.i("nav_route")[row];
            var rt = route >= 0 ? world.routes[route] : null;
            float nx = b.f("nav_x")[row], nz = b.f("nav_z")[row];
            Console.WriteLine($"  walker {k}: at ({x:0},{z:0}) goal ({goals[k].Item1:0},{goals[k].Item2:0}) "
                              + $"d={MathF.Sqrt(Sq(x - goals[k].Item1) + Sq(z - goals[k].Item2)):0} "
                              + $"route {route} path_i {b.i("path_i")[row]}/{rt?.pts.Count ?? -1} "
                              + $"nav_speed {b.f("nav_speed")[row]:0.0} pt ({nx:0},{nz:0}) "
                              + $"vel {MathF.Sqrt(Sq(b.f("vel")[row * 3]) + Sq(b.f("vel")[row * 3 + 2])):0.0}");
        }
    }

    /// <summary>Three crew APCs down the three lanes, driven by the real pipeline.</summary>
    private static void lane(World world)
    {
        var t = world.terrain;
        var ids = new List<int>();
        for (int lane = 0; lane < t.lanes.Count; lane++)
        {
            var (x, z) = world.terrain.bases[0];
            int route = world.lane_route(0, lane);
            var pts = world.routes[route].pts;
            // a hull on the lane, with the lane's own route in hand
            int eid = world.spawn_unit(Defs.ETYPE_APC, 0, pts[Math.Min(2, pts.Count - 1)].x,
                                       pts[Math.Min(2, pts.Count - 1)].z,
                                       lane: lane, crew: true);
            world.ecs.iv("nav_route")[eid] = route;
            world.ecs.iv("path_i")[eid] = 2;
            world.ecs.fv("nav_speed")[eid] = Defs.UNIT_DEFS[Defs.ETYPE_APC].speed;
            ids.Add(eid);
        }
        int asked0 = Paths.asked, delivered0 = Paths.delivered;
        var start = new (float x, float z)[ids.Count];
        var still = new float[ids.Count];
        var driven = new float[ids.Count];
        var travelled = new double[ids.Count];
        var last = new (float x, float z)[ids.Count];
        for (int k = 0; k < ids.Count; k++)
        {
            var slot = world.ecs.slot(ids[k]);
            start[k] = (slot.a.f("pos")[slot.row * 3], slot.a.f("pos")[slot.row * 3 + 2]);
            last[k] = start[k];
        }
        const float dt = 1f / 30f;
        int ticks = (int)(240f / dt);
        for (int tick = 0; tick < ticks; tick++)
        {
            world.update(dt);
            for (int k = 0; k < ids.Count; k++)
            {
                var slot = world.ecs.slot(ids[k]);
                if (!slot.ok || !slot.a.keyset.Contains("nav_speed")) continue;
                var b = slot.a;
                int row = slot.row;
                float cx = b.f("pos")[row * 3], cz = b.f("pos")[row * 3 + 2];
                travelled[k] += Math.Sqrt(Sq(cx - last[k].x) + Sq(cz - last[k].z));
                last[k] = (cx, cz);
                float want = b.f("nav_speed")[row];
                float vx = b.f("vel")[row * 3], vz = b.f("vel")[row * 3 + 2];
                if (want > 1f)
                {
                    driven[k] += dt;
                    if (MathF.Sqrt(vx * vx + vz * vz) < Defs.UNIT_DEFS[Defs.ETYPE_APC].speed * 0.1f)
                        still[k] += dt;
                }
            }
        }
        Console.WriteLine("\nlane: three crew APCs, 240 s of the real pipeline (the whole battle is running, "
                          + "so they halt to fight: the number to read is the seconds below 10 % of top speed)");
        for (int k = 0; k < ids.Count; k++)
        {
            var slot = world.ecs.slot(ids[k]);
            if (!slot.ok || !slot.a.keyset.Contains("nav_speed"))
            { Console.WriteLine($"  lane {k}: destroyed"); continue; }
            var b = slot.a;
            int row = slot.row;
            float x = b.f("pos")[row * 3], z = b.f("pos")[row * 3 + 2];
            float moved = MathF.Sqrt(Sq(x - start[k].x) + Sq(z - start[k].z));
            int route = b.i("nav_route")[row];
            int pi = b.i("path_i")[row];
            var rt = route >= 0 ? world.routes[route] : null;
            string where = rt is null
                ? $"point ({b.f("nav_x")[row]:0},{b.f("nav_z")[row]:0})"
                : $"route {route} [{pi}/{rt.pts.Count}] next ({rt.pts[Math.Min(pi, rt.pts.Count - 1)].x:0},"
                  + $"{rt.pts[Math.Min(pi, rt.pts.Count - 1)].z:0}) d="
                  + $"{Math.Sqrt(Sq(rt.pts[Math.Min(pi, rt.pts.Count - 1)].x - x) + Sq(rt.pts[Math.Min(pi, rt.pts.Count - 1)].z - z)):0}";
            Console.WriteLine($"  lane {k}: {where}");
            Console.WriteLine($"  lane {k}: {moved,5:0} m net / {travelled[k],6:0} m driven, "
                              + $"{driven[k],5:0} s of 240 under orders, "
                              + $"{still[k],5:0.0} s of it below 10 % of top speed");
        }
        Console.WriteLine($"  detours asked {Paths.asked - asked0}, delivered {Paths.delivered - delivered0}");
        Console.WriteLine("  by type: " + string.Join(", ",
            Paths.asked_by.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} {kv.Value}")));
    }

    private static float Sq(float v) => v * v;
}
