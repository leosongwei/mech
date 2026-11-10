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
    // "static" holds a post, "moving" drives a patrol, "mech" is the walking
    // hull, "infantry" is one man of the squad
    public string role = "static";
    public int etype = Defs.ETYPE_APC;  // the hull it stands up as
    public float x, z;
    public string axis = "x";           // which axis the patrol span runs on
    public float lo, hi;                // the ends of that span, metres
    public float dir = 1f;              // +1 / -1: which way the hull is being sent
    public float speed;                 // m/s the crew is ordered; 0 = the role's own
    public int eid = -1;
    public float t;                     // s left before an empty post is stood back up
}

/// <summary>The range's targets: twelve APC hulks, one per post in
/// `Terrain.RANGE_POSTS`, one mech that drives a leg of its own, and a squad of
/// six men on a line in front of the berths.</summary>
public sealed class Targets
{
    public const int STATIC_TARGETS = 10;
    public const int MOVING_TARGETS = 2;
    public const float RESPAWN_DELAY = 3.0f;
    public const float PATROL_SPEED = 8.0f;

    /// <summary>Where the moving mech drives: 200 m of ground square to the
    /// firing line, 500 m out from the spawning berths, so a pilot standing
    /// there watches it broadside at 1x.  Short on purpose - one leg is 12.5 s
    /// at 16 m/s, so it turns round often enough to be studied, not waited for.</summary>
    public static readonly (float x, float z) MECH_PATROL = (1672f, 2605f);
    public const float MECH_PATROL_HALF = 100f;
    // 16 m/s is over half the mech's top speed, so it spends a real part of
    // every leg at the hull's own 9.3 m/s^2 - which is what an APC patrol never
    // does and what the pre-aim ring's acceleration term is for
    public const float MECH_PATROL_SPEED = 16.0f;
    public const float MECH_RESPAWN_DELAY = 5.0f;

    /// <summary>The second mech, and the one the fire control is measured on:
    /// the same walk as `MECH_PATROL`, a kilometre long and flat out.
    ///
    /// A leg has to be long for a hull to be *fast* on it - a mech takes 5.4 s
    /// and 135 m to reach its 50 m/s, so the 200 m leg never gets out of second
    /// gear.  500 m either side of the firing line leaves ~370 m at full speed
    /// in the middle of every pass, and a U-turn at each end that is the
    /// harshest deceleration the machine can make.  It stands 255 m north of the
    /// short one so a pilot has both a slow and a fast hull in the same sight,
    /// and it is a hull like any other: kill it and it comes back in place.</summary>
    public static readonly (float x, float z) MECH_FAST_PATROL = (1900f, 2350f);
    public const float MECH_FAST_HALF = 500f;
    public const float MECH_FAST_SPEED = 50f;   // m/s, the mech's own top speed

    /// <summary>The infantry squad: six men on a line across the firing line,
    /// 260 m out from the berths so they are in the sight at 1x.  A man is a
    /// 0.65 m ball, so this is the fixture the 30 mm's airburst is watched on -
    /// a burst beside him sweeps him up without a direct hit, and one further
    /// than 2.5 m off the flight path leaves him standing.</summary>
    public const int INFANTRY_TARGETS = 6;
    public const float SQUAD_DIST = 260.0f;   // m north of the spawning berths
    public const float SQUAD_SPACING = 26.0f; // m between two men on the line

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
        // One moving *mech*, and it is a different test from the APC patrols: a
        // car crawling down a straight at a constant 8 m/s is the case a lead
        // ring never gets wrong, while a hull pulling at `MECH_ACCEL` in and out
        // of a turn is exactly what the second-order term is for.  It carries no
        // `ai` component, so it never fires (the range's fixtures still do not
        // shoot back) and no weapon system ever looks at it - it is only a hull
        // to practise on, and it walks the same slot machinery a patrol does, so
        // a killed one comes back in place.
        slots.Add(new TargetSlot
        {
            role = "mech", etype = Defs.ETYPE_MECH,
            x = MECH_PATROL.x, z = MECH_PATROL.z,
            axis = "x",
            lo = MECH_PATROL.x - MECH_PATROL_HALF,
            hi = MECH_PATROL.x + MECH_PATROL_HALF,
            dir = 1f, eid = -1, t = 0f,
        });
        // The fast one: a kilometre of straight at the hull's top speed.  Same
        // slot machinery, same respawn, its own `speed`.
        slots.Add(new TargetSlot
        {
            role = "mech", etype = Defs.ETYPE_MECH,
            x = MECH_FAST_PATROL.x, z = MECH_FAST_PATROL.z,
            axis = "x",
            lo = MECH_FAST_PATROL.x - MECH_FAST_HALF,
            hi = MECH_FAST_PATROL.x + MECH_FAST_HALF,
            speed = MECH_FAST_SPEED,
            dir = 1f, eid = -1, t = 0f,
        });
        // A squad of men: the one fixture on the range a *fuse* is for.  They hold
        // their posts - a killed one stands back up where it fell after the same
        // 3 s a hull gets - and they alternate a rifle for an AT rifle to say out
        // loud that the fuse reads the man, never what he carries.
        for (int k = 0; k < INFANTRY_TARGETS; k++)
            slots.Add(new TargetSlot
            {
                role = "infantry",
                etype = k % 2 == 0 ? Defs.ETYPE_INF_RIFLE : Defs.ETYPE_INF_ROCKET,
                x = Terrain.RANGE_SPAWN.x + (k - (INFANTRY_TARGETS - 1) * 0.5f) * SQUAD_SPACING,
                z = Terrain.RANGE_SPAWN.z - SQUAD_DIST,
                eid = -1, t = 0f,
            });
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
            if (eid >= 0) slot.t = slot.role == "mech" ? MECH_RESPAWN_DELAY : RESPAWN_DELAY;
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
        // a mech is drawn its standard pair: an AI crew would draw one off
        // `Defs.AI_LOADOUTS`, and this hull never fires, but a bare hull with no
        // arms would not look like the machine a pilot is practising against
        // a target is a puppet: no `ai_crew`, so no AI pass can ever reach it
        int eid = world.spawn_unit(slot.etype, Defs.FACTION_BLUE, x, z,
            weapons: slot.etype == Defs.ETYPE_MECH ? Defs.DEFAULT_LOADOUT : null,
            crew: false);
        // face the player's cross so the hulls read clearly through the sight
        var (sx, sz) = world.terrain.bases[Defs.FACTION_RED];
        float yaw = slot.role is "static" or "infantry"
                    ? MathF.Atan2(sz - z, sx - x) : _leg_yaw(slot);
        var e = world.ecs;
        e.fv("yaw")[eid] = yaw;
        e.fv("leg_yaw")[eid] = yaw;
        e.fv("turret_yaw")[eid] = yaw;
        e.fv("sight_yaw")[eid] = yaw;
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
        if (slot.role != "moving" && slot.role != "mech") return;
        float lo = slot.lo, hi = slot.hi;
        float d = slot.dir;
        int k = slot.axis == "x" ? 0 : 2;
        float v = e.fv("pos").get(eid, k);
        if (v > hi) { v = hi; d = -1f; }
        else if (v < lo) { v = lo; d = 1f; }
        slot.dir = d;
        // the hull is told a speed down the patrol axis and drives itself there: at
        // the end of a leg that is a direction reversal, which the wheeled model
        // takes as a U-turn at its own radius (the steering is its state, not ours).
        // The mech has no steering of its own to spend: `movement` pulls its
        // velocity after the command at `MECH_ACCEL`, so the same reversal is a
        // deceleration and an acceleration - the cleanest moving hull the range
        // has to aim at.
        float top = slot.speed > 0f ? slot.speed
            : slot.role == "mech" ? MECH_PATROL_SPEED : PATROL_SPEED;
        cmd.set(eid, k, d * top);
        // a car's heading is its own state; a mech's is not, so a reversing mech
        // is also *pointed* the other way - `gunnery` swings its hull after the
        // sight at its own rate, which is what a pilot sees a mech do
        if (slot.role == "mech") e.fv("sight_yaw")[eid] = _leg_yaw(slot);
    }

    /// <summary>Pick a fresh random spot for a destroyed slot.</summary>
    private static void _place(World world, TargetSlot slot)
    {
        // the mech's leg and the squad's posts are fixtures: they come back where
        // a pilot last saw them rather than walking the field to some new spot
        if (slot.role is "mech" or "infantry") { slot.dir = 1f; return; }
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
