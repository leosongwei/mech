// Range targets: passive APC hulks that hold a post or drive a patrol.
//
// The system keeps the range populated: every slot is either a stationary hulk
// or a moving one, and a destroyed slot comes back a few seconds later at a new
// random spot, so the target count per kind never drops.  Nothing here shoots
// back - that is what the range is for.
//
// Ported from `backend/server/modes/range/targets.py`.  The module-level
// constants of that file are members of `Targets` here, because that is the only
// class in it.

namespace Mech.Server.Modes.Range;

/// <summary>One target post: where it stands, and - for a patrol - the leg it
/// drives back and forth along.
///
/// Python keeps these as plain dicts (`role`, `x`, `z`, `lo`, `hi`, `dir`,
/// `eid`, `t` and, once it has respawned, `axis`); a class keeps the same field
/// names without a dictionary lookup per tick.</summary>
public sealed class TargetSlot
{
    public string role = "static";      // "static" holds a post, "moving" drives a patrol
    public float x, z;
    public string axis = "x";           // which axis the patrol span runs on
    public float lo, hi;                // the ends of that span, metres
    public float dir = 1f;              // +1 / -1: which way the hull is being sent
    public int eid = -1;
    public float t;                     // s left before an empty post is stood back up
}

/// <summary>The range's twelve APC targets, one per post in `Terrain.RANGE_POSTS`.</summary>
public sealed class Targets
{
    public const int STATIC_TARGETS = 10;
    public const int MOVING_TARGETS = 2;
    public const float RESPAWN_DELAY = 3.0f;
    public const float PATROL_SPEED = 8.0f;

    public const float MARGIN = 300.0f;          // keep hulks off the map edge
    public const float MIN_PLAYER_DIST = 350.0f; // never pop one in the player's face
    public const float MIN_TARGET_DIST = 140.0f; // keep the hulks from stacking up on each other
    public const float MAX_SLOPE = 18.0f;        // metres of height change over a 25 m step
    public const float PATROL_MIN = 200.0f;
    public const float PATROL_MAX = 420.0f;

    public const float TILE = Terrain.TILE;

    public List<TargetSlot> slots = new();

    /// <summary>The mode's system: `targets.run(world, dt)`.</summary>
    public static void run(World world, float dt) =>
        ((Targets)world.targets!).update(world, dt);

    public Targets(World world)
    {
        foreach (var post in Terrain.RANGE_POSTS)
        {
            if (post.patrol)
                slots.Add(new TargetSlot
                {
                    role = "moving", x = post.x, z = post.z,
                    lo = post.patrol_a, hi = post.patrol_b, dir = 1f, eid = -1, t = 0f,
                });
            else
                slots.Add(new TargetSlot
                {
                    role = "static", x = post.x, z = post.z, eid = -1, t = 0f,
                });
        }
        if (slots.Count(s => s.role == "static") != STATIC_TARGETS ||
            slots.Count(s => s.role == "moving") != MOVING_TARGETS)
            throw new InvalidOperationException(
                "the range map must carry 10 static posts and 2 patrols");
        foreach (var slot in slots) slot.eid = _spawn(world, slot);
    }

    public void update(World world, float dt)
    {
        var e = world.ecs;
        foreach (var slot in slots)
        {
            int eid = slot.eid;
            // `alive` is not enough.  Entity ids are handed out LIFO, so the id a
            // dead target leaves behind is taken by the very next entity that
            // spawns - and for a hull going up that is its own blast (a burst
            // entity, no `vel`, no `moves`).  Driving that one raised an exception
            // once a tick in Python, and because the exception aborted the loop the
            // slot was never dropped: the whole range world froze.  Ask the tag the
            // drive code actually needs instead.
            if (eid >= 0 && e.is_alive(eid) && e.has(eid, "moves"))
            {
                _drive(world, slot, eid, dt);
                continue;
            }
            // It died since the last tick: the post stands empty until the delay
            // runs out.  (`RESPAWN_DELAY` is defined in the Python source but never
            // armed - the slot's `t` only ever counts down, so a destroyed target
            // came back on the very next tick, while `doc/product/range.md` and
            // this mode's rule promise 3 s.  The timer is armed on the death here.)
            if (eid >= 0) slot.t = RESPAWN_DELAY;
            slot.eid = -1;
            slot.t -= dt;
            if (slot.t <= 0f)
            {
                _place(world, slot);
                slot.eid = _spawn(world, slot);
            }
        }
    }

    private static int _spawn(World world, TargetSlot slot)
    {
        float x = slot.x;
        float z = slot.z;
        int eid = world.spawn_unit(Defs.ETYPE_APC, Defs.FACTION_BLUE, x, z);
        // face the player's cross so the hulls read clearly through the sight
        var (sx, sz) = world.terrain.bases[Defs.FACTION_RED];
        float yaw = slot.role == "static" ? MathF.Atan2(sz - z, sx - x) : _leg_yaw(slot);
        var e = world.ecs;
        e.fv("yaw")[eid] = yaw;
        e.fv("leg_yaw")[eid] = yaw;
        e.fv("turret_yaw")[eid] = yaw;
        return eid;
    }

    /// <summary>Heading of a patrol: along x or z, whichever the span runs on.</summary>
    private static float _leg_yaw(TargetSlot slot)
    {
        if (slot.axis == "x") return slot.dir > 0 ? 0f : MathF.PI;
        return slot.dir > 0 ? -MathF.PI / 2f : MathF.PI / 2f;
    }

    /// <summary>Ask a patrolling hull for its speed - the hull drives itself.
    ///
    /// A target is a fixture, not a crew, but it is still a car: it is *told* a
    /// velocity per tick (`vel_cmd`, the same component a pilot's stick writes) and
    /// `movement` gets it there.  Writing `pos` here instead would put the hull's
    /// position out of step with its velocity, and every reader of the pair - the
    /// hit-sphere sweep, an IR seeker's lead, the wire - would be told it was
    /// standing still while it crawled past.</summary>
    private static void _drive(World world, TargetSlot slot, int eid, float dt)
    {
        var e = world.ecs;
        var cmd = e.fv("vel_cmd");
        cmd.set(eid, 0, 0f);
        cmd.set(eid, 2, 0f);
        if (slot.role != "moving") return;
        float lo = slot.lo, hi = slot.hi;
        float d = slot.dir;
        int k = slot.axis == "x" ? 0 : 2;
        float v = e.fv("pos").get(eid, k);
        if (v > hi) { v = hi; d = -1f; }
        else if (v < lo) { v = lo; d = 1f; }
        slot.dir = d;
        // the hull is told a speed down the patrol axis and drives itself there: at
        // the end of a leg that is a direction reversal, which the wheeled model
        // takes as a U-turn at its own radius (the steering is its state, not ours)
        cmd.set(eid, k, d * PATROL_SPEED);
    }

    /// <summary>Pick a fresh random spot for a destroyed slot.</summary>
    private static void _place(World world, TargetSlot slot)
    {
        if (slot.role == "moving")
        {
            var (x, z, axis, lo, hi) = _random_patrol(world);
            slot.x = x; slot.z = z; slot.axis = axis; slot.lo = lo; slot.hi = hi;
            slot.dir = 1f;
        }
        else
        {
            var (x, z) = _random_spot(world);
            slot.x = x; slot.z = z;
        }
    }

    private static (float x, float z) _random_spot(World world)
    {
        float size = world.terrain.w * TILE;
        var (px, pz) = world.terrain.bases[Defs.FACTION_RED];
        var others = _occupied(world);
        for (int k = 0; k < 60; k++)
        {
            float x = world.rng.uniform(MARGIN, size - MARGIN);
            float z = world.rng.uniform(MARGIN, size - MARGIN);
            if (MathF.Sqrt((x - px) * (x - px) + (z - pz) * (z - pz)) < MIN_PLAYER_DIST)
                continue;
            bool crowded = false;
            foreach (var (ox, oz) in others)
            {
                if (MathF.Sqrt((x - ox) * (x - ox) + (z - oz) * (z - oz)) < MIN_TARGET_DIST)
                {
                    crowded = true;
                    break;
                }
            }
            if (crowded) continue;
            if (!_flat_enough(world, x, z)) continue;
            return (x, z);
        }
        // nothing worked (a very crowded map): fall back to the plain far side
        return (size * 0.75f, size * 0.5f);
    }

    /// <summary>A patrol leg somewhere random: 400-840 m of straight, flat ground.</summary>
    private static (float x, float z, string axis, float lo, float hi) _random_patrol(World world)
    {
        float size = world.terrain.w * TILE;
        var (x, z) = _random_spot(world);
        string axis = world.rng.random() < 0.5f ? "x" : "z";
        float half = world.rng.uniform(PATROL_MIN, PATROL_MAX);
        float c = axis == "x" ? x : z;
        float lo = MathF.Max(MARGIN, c - half);
        float hi = MathF.Min(size - MARGIN, c + half);
        if (hi - lo < 2f * PATROL_MIN)      // ran into the edge: use the other axis
        {
            axis = axis == "x" ? "z" : "x";
            c = axis == "z" ? z : x;
            lo = MathF.Max(MARGIN, c - half);
            hi = MathF.Min(size - MARGIN, c + half);
        }
        return (x, z, axis, lo, hi);
    }

    /// <summary>Spots a fresh target may not reuse: other live APCs and the wrecks of
    /// the ones just destroyed (a new hull on top of its own burning hulk reads as a
    /// single glitchy vehicle).</summary>
    private static List<(float x, float z)> _occupied(World world)
    {
        var e = world.ecs;
        var ids = world.all_units();
        var kind = e.uv("etype");
        var pos = e.fv("pos");
        var outv = new List<(float x, float z)>();
        foreach (var eid in ids)
        {
            int k = kind[eid];
            if (k == Defs.ETYPE_APC || k == Defs.ETYPE_DEBRIS || k == Defs.ETYPE_CORPSE)
                outv.Add((pos.get(eid, 0), pos.get(eid, 2)));
        }
        return outv;
    }

    private static bool _flat_enough(World world, float x, float z)
    {
        float h = world.terrain.height_at(x, z);
        if (MathF.Abs(world.terrain.height_at(x + 25f, z) - h) > MAX_SLOPE) return false;
        if (MathF.Abs(world.terrain.height_at(x - 25f, z) - h) > MAX_SLOPE) return false;
        if (MathF.Abs(world.terrain.height_at(x, z + 25f) - h) > MAX_SLOPE) return false;
        if (MathF.Abs(world.terrain.height_at(x, z - 25f) - h) > MAX_SLOPE) return false;
        return true;
    }
}
