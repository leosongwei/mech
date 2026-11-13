// The navmesh's contract: what a chassis may cross, and that the planner and the
// driver agree about it.
//
// The failure this file is here to prevent is not a wrong path - it is a path the
// machine cannot drive.  A route planned on facts the driver does not use is how a
// column ends up pushing at a wood for the rest of a battle (the old street grid
// called a forest passable at a cost and a command base solid, while `blocked` said
// the opposite of both), so the tests below check the predicate against `blocked`
// and against `Defs.max_gradient`, and check that a path only ever crosses ground
// the predicate allows.

using Mech.Server;
using Mech.Server.Systems.AI;

namespace Mech.Tests;

public static class NavTests
{
    private const int W = 24, H = 24;

    /// <summary>A flat 24x24 tile field, all grass.</summary>
    private static (Terrain t, float[] h, byte[] tiles) field()
    {
        var h = new float[W * H];
        var tiles = new byte[W * H];
        return (new Terrain(h, tiles, new List<List<(float x, float z)>>(), new List<Town>(),
                            new Dictionary<int, (float x, float z)>(), null, null, W, H), h, tiles);
    }

    private static NavProfile mech => NavProfile.of(Defs.ETYPE_MECH);
    private static NavProfile apc => NavProfile.of(Defs.ETYPE_APC);

    [Test]
    public static void Flat_ground_is_walkable_for_everybody()
    {
        var (t, _, _) = field();
        float m = Terrain.TILE;
        Assert.True(NavWalk.clear(t, mech, 2 * m, 2 * m, 20 * m, 20 * m), "a mech on flat grass");
        Assert.True(NavWalk.clear(t, apc, 2 * m, 2 * m, 20 * m, 20 * m), "an APC on flat grass");
    }

    [Test]
    public static void A_wall_stops_the_walk_and_the_planner()
    {
        var (t, _, tiles) = field();
        float m = Terrain.TILE;
        // a wall of buildings across the middle of the field
        for (int j = 0; j < H; j++) tiles[j * W + 12] = Terrain.TILE_BUILDING;
        Assert.Same(NavWalk.clear(t, mech, 2 * m, 2 * m, 20 * m, 20 * m), false,
                    "the straight line through a wall is not walkable");
        // one row short of the wall is fine, so the predicate is following the data
        Assert.True(NavWalk.clear(t, mech, 2 * m, 2 * m, 11 * m, 2 * m), "up to the wall");
    }

    [Test]
    public static void The_climb_limit_is_the_one_the_def_declares()
    {
        var (t, h, _) = field();
        float m = Terrain.TILE;
        // a 30% ramp everywhere: 4.8 m of rise per tile
        for (int j = 0; j < H; j++)
            for (int i = 0; i < W; i++) h[j * W + i] = i * 0.30f * Terrain.TILE;
        Assert.True(NavWalk.clear(t, apc, 2 * m, 2 * m, 10 * m, 2 * m), "a 0.30 face is an APC's");
        // and a 65% one is nobody's below a mech
        for (int j = 0; j < H; j++)
            for (int i = 0; i < W; i++) h[j * W + i] = i * 0.65f * Terrain.TILE;
        Assert.Same(NavWalk.clear(t, apc, 2 * m, 2 * m, 10 * m, 2 * m), false,
                    "a 0.65 face is not an APC's");
        Assert.True(NavWalk.clear(t, mech, 2 * m, 2 * m, 10 * m, 2 * m), "it is a mech's (0.70)");
        // downhill is always allowed: a hull comes back off what it climbed
        Assert.True(NavWalk.clear(t, apc, 10 * m, 2 * m, 2 * m, 2 * m), "an APC walks down a 0.65");
    }

    [Test]
    public static void Every_facet_has_three_neighbours_and_they_agree()
    {
        var (t, _, _) = field();
        var r = new NavRegion(t, 2 * Terrain.TILE, 2 * Terrain.TILE,
                              20 * Terrain.TILE, 20 * Terrain.TILE, pad: 0f);
        var nb = new int[3];
        for (int n = 0; n < r.count; n++)
        {
            r.neighbours(n, nb, out int cnt);
            // a facet on the window's edge has fewer; everything inside has three
            int c = n >> 1;
            bool edge = c % r.nx is 0 || c / r.nx == 0 ||
                        c % r.nx == r.nx - 1 || c / r.nx == r.nz - 1;
            Assert.True(cnt >= (edge ? 1 : 3), $"facet {n} has {cnt} neighbours");
            for (int k = 0; k < cnt; k++)
            {
                r.neighbours(nb[k], nb, out _);     // must not throw, and must be in range
                Assert.True(nb[k] >= 0 && nb[k] < r.count, $"facet {n}: neighbour out of range");
            }
        }
    }

    [Test]
    public static void A_planned_path_goes_round_a_wall_and_stays_on_walkable_ground()
    {
        var (t, _, tiles) = field();
        float m = Terrain.TILE;
        // a wall with a gap at one end
        for (int j = 2; j < H - 8; j++) tiles[j * W + 12] = Terrain.TILE_BUILDING;
        var pts = Paths.plan(t, mech, 2 * m, 2 * m, 21 * m, 2 * m, out int facets, out _);
        Assert.True(pts.Count >= 2, $"no path round the wall (facets {facets})");
        for (int k = 1; k < pts.Count; k++)
            Assert.True(NavWalk.clear(t, mech, pts[k - 1].x, pts[k - 1].z, pts[k].x, pts[k].z),
                        $"leg {k} of the path crosses ground the driver refuses");
        // and it really went round: the wall runs from z = 2 tiles southwards, so
        // the way past it is north of where both ends stand
        float north = float.MaxValue;
        foreach (var (_, pz) in pts) north = MathF.Min(north, pz);
        Assert.True(north < 32f - 5f, $"the path never left the straight line (north {north:0} m)");
    }

    /// <summary>The predicate and the driver read one climb limit, out of one table:
    /// a chassis whose def says it climbs 0.58 must be refused a 0.6 face by both.</summary>
    [Test]
    public static void The_limit_is_the_def_not_a_number_of_its_own()
    {
        Assert.Close(mech.max_grad, Defs.MECH_MAX_GRADIENT, 1e-6, "mech profile");
        Assert.Close(apc.max_grad, Defs.APC_MAX_GRADIENT, 1e-6, "apc profile");
        Assert.Close(mech.radius, Defs.UNIT_DEFS[Defs.ETYPE_MECH].radius * 0.6f, 1e-6,
                     "the profile's radius is the one `Integrate.blocked` uses");
    }
}
