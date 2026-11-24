// What has been thrown at a crew lately, and from where.
//
// A crew that is shot at turns its head at the noise.  That is one fact and it is
// collected here, once, because finding it is expensive and the memory of it lives
// on the crew: `Systems/AI/Watch.cs` reads `flinch` a tick later and does the
// looking, this file only answers "was something close, and from which bearing".
//
// Two sources, because a hull notices two kinds of near miss:
//
//   * **a round that went past** - a rocket that missed, a burst that straddled;
//   * **a burst that went off** - a rocket that landed, a shell that hit the ground.
//
// The test for the first one is a *swept* distance, not a point: a 1 km/s round
// moves 33 m in a tick, so a 30 m sphere tested at the tick boundaries is stepped
// clean over and the loudest thing on the battlefield would be silent.  The leg is
// `(proj_prev, pos)`, which the flight systems publish for exactly this reason
// (`Systems/Flights.cs`), and the miss is the distance from the crew's centre to
// that segment.  The bearing a round came from is its own reverse velocity - a
// round that whistles past is a bearing, not a place - with the line from the crew
// to the burst as the fallback for anything falling too steeply to have one.
//
// Where it sits (`Impact` -> here -> `Blasts`): the legs are still on the field
// (Impact destroys the round after this), and this tick's bursts still exist
// (`Blasts` decrements and destroys them after this).  A round that hit *this* crew
// is therefore seen one tick early in its leg rather than not at all, and a burst
// is seen in the tick it went off.
//
// The debounce is in the flush, not in the event: a crew gets one bearing per tick
// (the nearest thing that came close), and a bearing it is already looking at is
// only renewed.  Without that, a burst of 30 mm walking across a hull would swing
// its head ten times a second.

namespace Mech.Server.Systems.AI;

/// <summary>A round's own columns, plus the side it belongs to (a round carries
/// `faction`; `Flights.RoundColumns` does not need it for the flight itself).</summary>
public sealed class WhizColumns : Flights.RoundColumns
{
    public byte[] fac = null!;

    private static readonly string[] whiz_names = Components.Cat(names, "faction");
    public static readonly string[] OnWhiz = whiz_names;

    public WhizColumns(Group g) : base(g) { }

    public override string[] Names => whiz_names;

    protected override void bind(Group g)
    {
        base.bind(g);
        fac = g.u8("faction");
    }
}

public static class Suppress
{
    /// <summary>m; how close a round's leg has to pass to count as a near miss.</summary>
    public const float WHIZ = 30f;

    /// <summary>m; how close a burst has to be to be heard.</summary>
    public const float HEAR = 80f;

    /// <summary>s of alarm one near miss buys: how long the head is held on it.</summary>
    public const float MEM = 1.5f;

    /// <summary>s; inside this window a new bearing has to be a *different* bearing
    /// to be worth turning to (`ARC`), which is what makes a burst one event.</summary>
    public const float GAP = 0.5f;

    /// <summary>rad; how far the new bearing has to differ inside `GAP`.</summary>
    public const float ARC = 25f * MathF.PI / 180f;

    public static void run(World world, float dt)
    {
        var e = world.ecs;
        var grid = world.grid;

        // ---- 1. the crews, indexed: who is where, and whose side it is --------
        // A flat table built once per tick (the `ThreatTable` shape): the two event
        // passes below are positional from here on.
        var idx = new int[e.alive.Length];
        Array.Fill(idx, -1);
        var cx = new List<float>();
        var cy = new List<float>();
        var cz = new List<float>();
        var cfac = new List<byte>();
        var n = 0;
        foreach (var g in e.groups(CrewColumns.OnCrew))
        {
            var c = new CrewColumns(g);
            var gids = c.ids;
            for (int i = 0; i < c.n; i++)
            {
                var d = Defs.UNIT_DEFS[c.etype[i]];
                idx[gids[i]] = n++;
                cx.Add(c.pos[i * 3]);
                cy.Add(c.pos[i * 3 + 1] + d.height * 0.5f);
                cz.Add(c.pos[i * 3 + 2]);
                cfac.Add(c.faction[i]);
            }
        }
        if (n == 0) return;

        var best = new float[n];                    // nearest event per crew
        var byaw = new float[n];                    // ...and the bearing it came from
        Array.Fill(best, float.MaxValue);

        // ---- 2. the rounds that went past ------------------------------------
        foreach (var g in e.groups(WhizColumns.OnWhiz))
        {
            var c = new WhizColumns(g);
            for (int i = 0; i < c.n; i++)
            {
                if (c.life[i] <= 0f) continue;                  // no battery or wire left
                byte ff = c.fac[i];
                var end = new Vec3(c.pos[i * 3], c.pos[i * 3 + 1], c.pos[i * 3 + 2]);
                var start = new Vec3(c.prev[i * 3], c.prev[i * 3 + 1], c.prev[i * 3 + 2]);
                float vx = c.vel[i * 3], vz = c.vel[i * 3 + 2];
                float flat = vx * vx + vz * vz;
                // the bearing the fire came from is the round's own reverse bearing,
                // which needs a horizontal velocity to mean anything: a top attack
                // round coming down the chimney is read off where it is instead
                float yaw = flat > 1f ? MathF.Atan2(-vz, -vx)
                                      : MathF.Atan2(start.z - end.z, start.x - end.x);
                var (ci, cj) = grid.cell_of(end.x, end.z);
                var (near, _) = grid.box(ci, cj, (int)(WHIZ * grid.inv) + 1);
                for (int k = 0; k < near.Length; k++)
                {
                    int who = idx[near[k]];
                    if (who < 0 || cfac[who] == ff) continue;
                    float d = _miss(start, end, new Vec3(cx[who], cy[who], cz[who]));
                    if (d > WHIZ || d >= best[who]) continue;
                    best[who] = d;
                    byaw[who] = flat > 1f ? yaw
                                           : MathF.Atan2(start.z - cz[who], start.x - cx[who]);
                }
            }
        }

        // ---- 3. the bursts ---------------------------------------------------
        foreach (int bid in world.blasts())
        {
            var p = e.fv("pos").get3(bid);
            byte bf = e.uv("faction")[bid];
            var (ci, cj) = grid.cell_of(p.x, p.z);
            var (near, _) = grid.box(ci, cj, (int)(HEAR * grid.inv) + 1);
            for (int k = 0; k < near.Length; k++)
            {
                int who = idx[near[k]];
                if (who < 0 || cfac[who] == bf) continue;
                float dx = p.x - cx[who], dy = p.y - cy[who], dz = p.z - cz[who];
                float d = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
                if (d > HEAR || d >= best[who]) continue;
                best[who] = d;
                byaw[who] = MathF.Atan2(dz, dx);
            }
        }

        // ---- 4. write it down, one bearing per crew, with the debounce --------
        foreach (var g in e.groups(CrewColumns.OnCrew))
        {
            var c = new CrewColumns(g);
            foreach (var r in c)
            {
                int who = idx[c.ids[r.index]];
                if (who < 0 || best[who] == float.MaxValue) continue;
                // still hot and already looking that way: renew the alarm, do not
                // move the head (a burst is one event until a second shooter speaks)
                bool hot = r.flinch_t > MEM - GAP;
                bool same = MathF.Abs(MathUtil.wrap_pi(byaw[who] - r.flinch_yaw)) < ARC;
                if (!(hot && same)) r.flinch_yaw = byaw[who];
                r.flinch_t = MEM;
            }
        }
    }

    /// <summary>m; how close the segment `a`-`b` comes to `p`.  The one geometric
    /// fact this file needs, and the reason a fast round cannot slip between two
    /// ticks unnoticed.</summary>
    private static float _miss(Vec3 a, Vec3 b, Vec3 p)
    {
        float ax = b.x - a.x, ay = b.y - a.y, az = b.z - a.z;
        float len2 = ax * ax + ay * ay + az * az;
        float t = 0f;
        if (len2 > 1e-6f)
        {
            t = ((p.x - a.x) * ax + (p.y - a.y) * ay + (p.z - a.z) * az) / len2;
            t = Math.Clamp(t, 0f, 1f);
        }
        float dx = a.x + ax * t - p.x;
        float dy = a.y + ay * t - p.y;
        float dz = a.z + az * t - p.z;
        return MathF.Sqrt(dx * dx + dy * dy + dz * dz);
    }
}
