// Battle mode: town capture and the reinforcements behind the front.
//
// Both sides count along their own copy of a lane, so "advanced" always means
// the largest index in that faction's list, never a mirrored position.
//
// A lane supplies from its **front town only** - the furthest one still
// connected to its own command base.  red - red - red - blue - red means the
// last red town before the cut raises the troops and everything past the cut
// (the captured town included) is silent.  A town with enemy hulls inside its
// capture ring supplies nothing either: the column is raised one town further
// back until the fight is over.
//
// Ported from `backend/server/modes/battle/waves.py`.

namespace Mech.Server.Modes.Battle;

public static class Waves
{
    public const float MECH_INTERVAL = 120.0f;    // command base: one mech down every lane
    public const float INF_INTERVAL = 60.0f;      // per supplying town: one infantry squad
    public const float APC_INTERVAL = 60.0f;      // per supplying town: one APC
    public static readonly int[] INF_SQUAD = _squad();
    public const float CAPTURE_TIME = 9.0f;
    public const float CAPTURE_RADIUS = 170.0f;

    private static int[] _squad()
    {
        var squad = new int[10];
        for (int k = 0; k < 6; k++) squad[k] = Defs.ETYPE_INF_ROCKET;
        for (int k = 6; k < 10; k++) squad[k] = Defs.ETYPE_INF_RIFLE;
        return squad;
    }

    /// <summary>Attach every town to the lane it sits on and give it its own timers.</summary>
    public static void bind(World world)
    {
        foreach (var town in world.towns)
        {
            var (lane, i) = _lane_of(world, town.x, town.z);
            town.lane = lane;
            town.lane_i = i;
            town.inf_t = INF_INTERVAL;
            town.apc_t = APC_INTERVAL;
            town.contested = false;
        }
    }

    /// <summary>Lane and red-side way point index of the point nearest to (x, z).</summary>
    public static (int lane, int i) _lane_of(World world, float x, float z)
    {
        int best_lane = 0, best_i = 0;
        float best_d = 1e18f;
        for (int lane = 0; lane < world.lanes.Count; lane++)
        {
            var pts = world.lanes[lane];
            for (int i = 0; i < pts.Count; i++)
            {
                var (px, pz) = pts[i];
                float d = (px - x) * (px - x) + (pz - z) * (pz - z);
                if (d < best_d)
                {
                    best_lane = lane;
                    best_i = i;
                    best_d = d;
                }
            }
        }
        return (best_lane, best_i);
    }

    public static void run(World world, float dt)
    {
        if (world.winner >= 0) return;
        _capture(world, dt);
        _base_waves(world, dt);
        _town_waves(world, dt);
    }

    public static void _capture(World world, float dt)
    {
        var e = world.ecs;
        var ids = world.combat_units();
        // recomputed below, never stale: a town that was fought over last tick and
        // is quiet this one supplies its lane again
        foreach (var town in world.towns) town.contested = false;
        if (ids.Length == 0) return;
        var pos = e.fv("pos");
        var fac = e.uv("faction");
        float r2 = CAPTURE_RADIUS * CAPTURE_RADIUS;
        foreach (var town in world.towns)
        {
            bool near = false, red = false, blue = false, contested = false;
            int owner = town.owner;
            for (int k = 0; k < ids.Length; k++)
            {
                int eid = ids[k];
                float dx = pos[eid] - town.x;
                float dz = pos.get(eid, 2) - town.z;
                if (dx * dx + dz * dz >= r2) continue;
                near = true;
                int f = fac[eid];
                // someone the town does not belong to is standing in the ring: the
                // place is being fought over, and no column is raised out of a
                // firefight
                if (f != owner) contested = true;
                if (f == Defs.FACTION_RED) red = true;
                else if (f == Defs.FACTION_BLUE) blue = true;
            }
            if (!near)
            {
                if (town.owner >= 0) town.progress = MathF.Max(0f, town.progress - dt * 0.5f);
                continue;
            }
            town.contested = contested;
            if (red == blue)
            {
                town.progress = MathF.Max(0f, town.progress - dt * 0.5f);
                continue;
            }
            int holder = red ? Defs.FACTION_RED : Defs.FACTION_BLUE;
            if (town.owner == holder)
            {
                town.progress = 0f;
                continue;
            }
            town.progress += dt;
            if (town.progress >= CAPTURE_TIME)
            {
                town.owner = holder;
                town.progress = 0f;
                world.emit("capture", ("x", town.x), ("z", town.z), ("faction", holder));
            }
        }
    }

    /// <summary>The towns on the lane still connected to the faction's base, listed
    /// the way that faction walks them: base end first, the front town last.</summary>
    public static List<Town> _chain(World world, int faction, int lane)
    {
        var chain = new List<Town>();
        foreach (var t in world.towns)
            if (t.lane == lane) chain.Add(t);
        // red's base first; `OrderBy` is a stable sort, like Python's `sorted`
        chain = chain.OrderBy(t => t.lane_i).ToList();
        if (faction == Defs.FACTION_BLUE) chain.Reverse();   // blue walks the other way
        var outv = new List<Town>();
        foreach (var town in chain)
        {
            if (town.owner != faction) break;                // cut off from the base
            outv.Add(town);
        }
        return outv;
    }

    /// <summary>The furthest town on the lane that can still raise a column.
    ///
    /// The depots behind the front have already handed their troops forward, so a
    /// lane raises one column at a time instead of a column from every town it
    /// owns.  A captured town cuts the chain - the front falls back to the last
    /// town before it - and a town with the enemy inside its ring supplies
    /// nothing either, so the column comes from the one behind it until the fight
    /// is over.</summary>
    public static Town? front_town(World world, int faction, int lane)
    {
        var chain = _chain(world, faction, lane);
        for (int k = chain.Count - 1; k >= 0; k--)
            if (!chain[k].contested) return chain[k];
        return null;
    }

    /// <summary>The town a pilot who died at (x, z) comes back at.
    ///
    /// Three steps: **A**, the faction's own town nearest the hull that died (any
    /// lane - it only picks the lane); **B**, the town on A's lane nearest the
    /// death that is still connected to the base *and* not being fought over (A
    /// itself when it qualifies); **C**, one town back from B towards the base.
    /// So a death at a town never comes back at that town, and a town cut off from
    /// the base or under attack is skipped.  null means the base itself.</summary>
    public static Town? respawn_town(World world, int faction, float x, float z)
    {
        var own = new List<Town>();
        foreach (var t in world.towns)
            if (t.owner == faction) own.Add(t);
        if (own.Count == 0) return null;
        // A: the nearest own town (squared distances order the same way, and this
        // is `min` over a handful)
        Town? a = null;
        float best = float.PositiveInfinity;
        foreach (var t in own)
        {
            float d = _d2(t.x, t.z, x, z);
            if (d < best) { best = d; a = t; }
        }
        var available = new List<Town>();
        foreach (var t in _chain(world, faction, a!.lane))
            if (!t.contested) available.Add(t);
        if (available.Count == 0) return null;
        // B: the nearest available town on that lane
        Town? b = null;
        best = float.PositiveInfinity;
        foreach (var t in available)
        {
            float d = _d2(t.x, t.z, x, z);
            if (d < best) { best = d; b = t; }
        }
        // C: one town back towards the base
        var chain = _chain(world, faction, a.lane);        // base end first
        int i = chain.IndexOf(b!);
        return i > 0 ? chain[i - 1] : null;
    }

    /// <summary>A free spot in the ring around (x, z); the point itself if none is.</summary>
    public static (float x, float z) clear_near(World world, float x, float z, float r0, float r1)
    {
        for (int k = 0; k < 16; k++)
        {
            float ang = world.rng.random() * MathUtil.TAU;
            float r = world.rng.uniform(r0, r1);
            float sx = x + MathF.Cos(ang) * r, sz = z + MathF.Sin(ang) * r;
            if (world._clear_spot(sx, sz)) return (sx, sz);
        }
        return (x, z);
    }

    /// <summary>Spawn one unit and point it down its lane at the enemy base.</summary>
    public static int _send(World world, int etype, int faction, int lane, float x, float z)
    {
        int eid = world.spawn_unit(etype, faction, x, z, lane: lane);
        var e = world.ecs;
        var (ex, ez) = world.terrain.bases[1 - faction];
        float yaw = MathF.Atan2(ez - z, ex - x);
        e.fv("yaw")[eid] = yaw;
        e.fv("leg_yaw")[eid] = yaw;
        // fresh out of the gate the machine looks where it is pointed: a sight left
        // on the spawn's random heading would swing the hull round for nothing
        e.fv("sight_yaw")[eid] = yaw;
        return eid;
    }

    /// <summary>One mech per lane from each command base, once a minute.</summary>
    public static void _base_waves(World world, float dt)
    {
        world.wave_timer -= dt;
        if (world.wave_timer > 0f) return;
        world.wave_timer = MECH_INTERVAL;
        world.wave += 1;
        foreach (int faction in _factions)
        {
            var (bx, bz) = world.terrain.bases[faction];
            for (int lane = 0; lane < world.lanes.Count; lane++)
            {
                var (x, z) = clear_near(world, bx, bz, 70f, 130f);
                _send(world, Defs.ETYPE_MECH, faction, lane, x, z);
            }
        }
    }

    /// <summary>The front town of every lane raises the next column.</summary>
    public static void _town_waves(World world, float dt)
    {
        foreach (int faction in _factions)
        {
            for (int lane = 0; lane < world.lanes.Count; lane++)
            {
                var town = front_town(world, faction, lane);
                if (town is null) continue;
                town.inf_t -= dt;
                town.apc_t -= dt;
                if (town.inf_t > 0f && town.apc_t > 0f) continue;
                var (x, z) = clear_near(world, town.x, town.z, 40f, 70f);
                if (town.inf_t <= 0f)
                {
                    town.inf_t = INF_INTERVAL;
                    foreach (int etype in INF_SQUAD)
                    {
                        var (sx, sz) = clear_near(world, x, z, 5f, 45f);
                        _send(world, etype, faction, lane, sx, sz);
                    }
                }
                if (town.apc_t <= 0f)
                {
                    town.apc_t = APC_INTERVAL;
                    _send(world, Defs.ETYPE_APC, faction, lane, x, z);
                }
            }
        }
    }

    // the two sides, in the order the round has always visited them
    private static readonly int[] _factions = { Defs.FACTION_RED, Defs.FACTION_BLUE };

    private static float _d2(float ax, float az, float bx, float bz)
    {
        float dx = ax - bx, dz = az - bz;
        return dx * dx + dz * dz;
    }
}
