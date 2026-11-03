// Indestructible bunkers on the range ridge lines: fire-control references.
//
// Each one sits on a local high point, so from the firing line its silhouette
// stands against the sky.  A round that lands on it proves the zeroing solution;
// one that falls short or sails over is read against the slope behind it.  The
// concrete takes no damage - the range is for measuring, not for scoring.
//
// Ported from `backend/server/modes/range/bunkers.py`.

namespace Mech.Server.Modes.Range;

public static class Bunkers
{
    // search boxes (x, z) for the local high point, metres on the map
    public static readonly (float x, float z)[] BUNKER_ANCHORS =
    {
        (1000f, 980f),       // main summit
        (1500f, 1250f),      // south-east ridge, the near flank facing the cross
        (600f, 1750f),       // west ridge
        (700f, 2000f),       // southern spur
        (1400f, 400f),       // north shoulder
    };

    public const float SEARCH = 160f;   // half width of the search box around an anchor
    public const float GRID = 10f;      // resolution of that search

    /// <summary>Spawn one bunker per anchor and return their entity ids.</summary>
    public static List<int> place(World world)
    {
        var e = world.ecs;
        var terrain = world.terrain;
        var (sx, sz) = terrain.bases[Defs.FACTION_RED];
        var ids = new List<int>();
        foreach (var (ax, az) in BUNKER_ANCHORS)
        {
            var (x, z) = _high_point(terrain, ax, az);
            int eid = world.spawn_unit(Defs.ETYPE_BUNKER, Defs.FACTION_NATURE, x, z);
            float yaw = MathF.Atan2(sz - z, sx - x);      // face the firing line
            e.fv("yaw")[eid] = yaw;
            e.fv("turret_yaw")[eid] = yaw;
            e.fv("leg_yaw")[eid] = yaw;
            ids.Add(eid);
        }
        return ids;
    }

    /// <summary>Highest tile in the anchor's search box: the crest it should sit on.</summary>
    private static (float x, float z) _high_point(Terrain terrain, float ax, float az)
    {
        float best_h = -1f;
        var best = (x: ax, z: az);
        int n = (int)(SEARCH / GRID);
        for (int i = -n; i <= n; i++)
        {
            float x = ax + i * GRID;
            for (int j = -n; j <= n; j++)
            {
                float z = az + j * GRID;
                float h = terrain.height_at(x, z);
                if (h > best_h)
                {
                    best_h = h;
                    best = (x, z);
                }
            }
        }
        return best;
    }
}
