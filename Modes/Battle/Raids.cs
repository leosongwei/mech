// Battle mode: the raiders - one mech per lane, sent round the back.
//
// The lanes are a grind.  Every hull in the battle walks down one of them, so the
// fighting happens where the lanes meet and the winner is whoever feeds the front
// faster.  The answer to that is to stop feeding it: `Waves.front_town` only
// counts towns still **connected to the command base**, so a town taken out of
// the enemy's rear silences the lane beyond it - which is exactly what a raider
// is for.  Every `RAID_INTERVAL` a side sends out `RAID_SIZE` mechs, **one per
// lane, the lanes drawn at random**: a raider crosses the open field *off* the
// lanes (the lanes are where the fighting is), takes a town behind the enemy
// front on its own lane, and when it is ours the raider picks the next weak spot
// on that lane and carries on.
//
// `RAID_SIZE` raiders, `RAID_SIZE` lanes - that is the whole idea.  A single party
// walking to the one weakest town across the map is a party that can be met: two
// hulls heading for two different lanes cannot be, and whichever lane they leave
// alone is the lane the enemy got lucky with.
//
// The mission needs a plan, and the plan is made twice over, both halves off the
// same **force gradient** (`_gradient`): the enemy army spread over the ground its
// own weapons can see, weighted by what each hull is worth - a ridge of high
// ground along the front and thin ground in his rear, which is the whole map a
// raider has to read.
//
//   * **Where** - `_pick_town` takes the enemy town *on this raider's lane* with
//     the lowest gradient at it (the fewest guns that could shoot a hull standing
//     in the square), plus a mild term per kilometre of march so a raider does
//     not cross half the map to shave one defender off the score.
//   * **How** - `_astar` over a 128 m node grid whose cost is openness plus that
//     same gradient: a node that is all trees costs `WOOD_COST` extra, a node
//     that is all buildings costs `WALL_COST`, and a node the enemy has guns on
//     costs `GRADIENT_COST` per unit of force - so the route bends *round* the
//     fighting instead of through it.  The node route is then shortened through
//     open ground (`_shortcut`) into a polyline a hull can actually drive.
//
// The hulls themselves are ordinary mechs - they acquire, lead, fire and die
// through `Systems/Ai.cs` like any other crew; what makes them raiders is the
// `raider` tag, which takes them out of the lane AI's hands and puts them under
// `_drive` here.  The mission itself is a two-state machine on `ai_state`: MARCH
// along the route, then ASSAULT - stand in the capture ring and let
// `Waves._capture` do the work - and when the town turns friendly the raider plans
// again.  `Ai.run` keeps running the guns in both states, so a raider that runs
// into something fights it instead of walking past it.
//
// Nothing here is a new kind of unit: a raider is a `combat` hull, so it holds
// ground, can be locked, can be shot and its wreck goes up like anyone else's.  It
// is also the only unit in the battle that carries a plan of its own, and both
// halves of that plan (`_route`, `_pick_town`) are pure functions of the map and
// of the enemy's positions.
//
// Ported from `backend/server/modes/battle/raids.py`.  The one structural
// difference is where the round's raid state lives: Python hangs it off the world
// (`world.raid_timer`, `world.raid_paths`, `world.raid_retry`, `world.raid_serial`)
// and `World` here has no such fields, so it is one `RaidState` per world in a
// `ConditionalWeakTable` (`state_of`) - same identity, same lifetime, no edits
// outside this file.

using System.Runtime.CompilerServices;

namespace Mech.Server.Modes.Battle;

/// <summary>Everything a round's raiding parties keep between ticks, keyed by the
/// world it belongs to (Python keeps the same four fields on the world).</summary>
public sealed class RaidState
{
    /// <summary>faction -> seconds until its next party leaves.</summary>
    public readonly Dictionary<int, float> raid_timer = new();
    /// <summary>(faction, party, lane) -> the way points the raider is driving.</summary>
    public readonly Dictionary<(int faction, int party, int lane), List<(float x, float z)>> raid_paths = new();
    /// <summary>(faction, party, lane) -> seconds until the next planning attempt.</summary>
    public readonly Dictionary<(int faction, int party, int lane), float> raid_retry = new();
    /// <summary>launch counter: the party number is this modulo 256.</summary>
    public int raid_serial;
}

public static class Raids
{
    public const float RAID_INTERVAL = 120.0f;   // s between two parties, per faction
    public const float RAID_FIRST = 60.0f;       // s into the round the first one leaves
    public const float RAID_STAGGER = 10.0f;     // s between the two sides' clocks (one plan per tick)
    public const int RAID_SIZE = 2;              // raiders per party: one per lane, lanes drawn at random
    public const int RAID_MAX = 6;               // hulls on the mission before the base stops sending

    public const int STATE_MARCH = 0;            // driving the route
    public const int STATE_ASSAULT = 1;          // standing in the town ring, taking it
    public const int STATE_HOLD = 2;             // the town is ours: stay in the ring and keep it
    public const float RAID_HOLD = 90.0f;        // s a raider holds the town it took before moving on

    public const float THREAT_KM = 0.15f;        // score added per km of march (two empty towns, pick the near one)
    public const float NAV_CELL = 128.0f;        // m per navigation node (8 tiles, and 16 km / 128 m is exact)
    public const float WOOD_COST = 8.0f;         // extra cost of a node that is all trees
    public const float WALL_COST = 8.0f;         // extra cost of a node that is all buildings
    // Extra cost per unit of enemy force on a node - the whole "do not cross the
    // enemy" rule, and the one number here that was measured rather than reasoned.
    // A raider sent to a town behind the blue front (seed 11, six minutes in) walks
    // a straight line of 13.8 km through an average force of 6.0: it crosses the
    // army itself.  At 0.35 that becomes 15.5 km (1.13x, 310 s at 50 m/s) through
    // 1.9 - the route slides to where the line is thinnest.  Past ~0.5 the answer
    // flips over to "go round the whole army": 25.8 km (1.87x, 516 s) through 0.3,
    // which is nine minutes of marching to avoid a crossing that costs a raider
    // nothing it cannot shoot back at.  So this stays on the near side of that
    // cliff: bend round the thick of it, use the thin spot, arrive.
    public const float GRADIENT_COST = 0.35f;    // extra cost per unit of enemy force gradient on a node
    public const float GRADIENT_CAP = 12.0f;     // gradient above this is all the same: it is the front
    // A* heuristic weight.  A node costs 1 on empty ground and up to `GRADIENT_CAP`
    // in front of the enemy, so an unscaled octile estimate is so optimistic that
    // the search expands half the map before it commits: 32 ms for the longest
    // route measured.  Weighting it by 1.6 (standard weighted A*) brings that to
    // 20 ms and found the same routes in the calibration - bend 1.22x the straight
    // line round the enemy instead of 1.25x, through a third of the fire.
    public const float ASTAR_WEIGHT = 1.6f;
    public const float ARRIVE = 25.0f;           // m: a way point this close counts as reached
    public const float HANDOVER = 400.0f;        // m from the town middle where the street grid takes over
    public const float WINDOW_PAD = 320.0f;      // m of extra window around a street plan
    public const float STREET_TREE_COST = 3.0f;  // extra cost of a street-grid node under a canopy
    public const float TOWN_STOP = 150.0f;       // m from the town middle: inside the 170 m capture ring,
                                                 // but at its edge - a raider does not drive into the
                                                 // blocks of a town it is taking (300-650 m of houses)
    public const float BASE_STOP = 900.0f;       // m off the enemy command base: close enough to shoot it
    public const float REPLAN = 12.0f;           // s before a raider whose plan failed tries again
    public const float PLAN_GAP = 0.35f;         // s between two hulls of a party getting their orders
    public const float RESCUE = 2.0f;            // s before a stuck hull may be re-routed again

    /// <summary>What one hull is worth in the force gradient (`_gradient`).  The
    /// gradient is "how much of the enemy army would see a raider standing here",
    /// so a mech that outguns and outranges a carrier counts triple a rifleman's
    /// nuisance value - and the command base, which has no guns of its own, counts
    /// for nothing (it is filtered out by its empty weapon anyway: the table only
    /// names the shooters).</summary>
    public static readonly Dictionary<int, float> FORCE_WEIGHT = new()
    {
        [Defs.ETYPE_MECH] = 3.0f,
        [Defs.ETYPE_APC] = 2.0f,
        [Defs.ETYPE_INF_ROCKET] = 1.0f,
        [Defs.ETYPE_INF_RIFLE] = 0.5f,
    };

    public const float STUCK_RATE = 0.25f;       // rolling slower than this share of the command is "pushing"
    public const float STUCK_FAST = 8.0f;        // m/s - above that a hull is going somewhere, whatever it was asked
    public const float STUCK_SKIP = 2.0f;        // s against something before the way point is abandoned
    public const float STUCK_REPLAN = 6.0f;      // s before the route itself is judged wrong

    public const int NO_TOWN = 255;              // `raid_goal`: there is nothing to take, so go for the base

    /// <summary>The components a raider carries on top of its type's bundle: the
    /// mission tag and the plan it is executing (`Ecs/Components.cs`).</summary>
    public static readonly string[] _COMPS =
        { "raider", "raid_goal", "raid_i", "raid_party", "raid_stuck", "raid_t" };

    private static readonly float[] _PROBE = { 0.8f, 2.0f, 4.0f };   // m: the steps the obstacle fan must find clear
    private static readonly (int di, int dj)[] _NEIGH =
        { (1, 0), (-1, 0), (0, 1), (0, -1), (1, 1), (1, -1), (-1, 1), (-1, -1) };
    private static readonly float _ROOT2 = MathF.Sqrt(2f);
    private static readonly int[] _factions = { Defs.FACTION_RED, Defs.FACTION_BLUE };
    private static readonly List<(float x, float z)> _no_path = new();

    private static readonly ConditionalWeakTable<World, RaidState> _states = new();

    /// <summary>The raid state of this round (`world.raid_*` in Python).</summary>
    public static RaidState state_of(World world) => _states.GetValue(world, _ => new RaidState());

    /// <summary>Give the round its raid clock and the plans, one per raider.
    ///
    /// The clock is per faction and the two are staggered by `RAID_STAGGER`: a plan
    /// is ~15 ms of A* and both sides planning in the same tick would be one late
    /// tick every two minutes for no reason, while ten seconds apart is invisible.
    ///
    /// A plan belongs to a *hull* (`(faction, raid_party, lane)`), not to the
    /// faction: the raiders of one launch walk to two different lanes and are
    /// kilometres apart when the next launch gets its orders, so a shared plan
    /// would send the old one to a route that starts behind it - which is exactly
    /// what "the raider stopped advancing" looks like from outside.</summary>
    public static void bind(World world)
    {
        var st = state_of(world);
        st.raid_timer.Clear();
        foreach (int f in _factions) st.raid_timer[f] = RAID_FIRST + f * RAID_STAGGER;
        st.raid_paths.Clear();
        st.raid_retry.Clear();
        st.raid_serial = 0;
    }

    public static void run(World world, float dt)
    {
        if (world.winner >= 0) return;
        _launch(world, dt);
        _drive(world, dt);
    }

    // ------------------------------------------------------------------
    // the clock
    // ------------------------------------------------------------------
    /// <summary>The name of this raider's plan: its side, its party and the lane it
    /// raids.</summary>
    public static (int faction, int party, int lane) key(World world, int eid)
    {
        var e = world.ecs;
        return (e.uv("faction")[eid], e.uv("raid_party")[eid], e.uv("lane")[eid]);
    }

    /// <summary>The faction's raiders, alive (a query: a wreck carries no tags at
    /// all).</summary>
    public static int[] _raiders(World world, int faction)
    {
        var ids = world.raiders();
        if (ids.Length == 0) return ids;
        var fac = world.ecs.uv("faction");
        int n = 0;
        foreach (int eid in ids) if (fac[eid] == faction) n++;
        var outv = new int[n];
        n = 0;
        foreach (int eid in ids) if (fac[eid] == faction) outv[n++] = eid;
        return outv;
    }

    public static void _launch(World world, float dt)
    {
        var e = world.ecs;
        var st = state_of(world);
        foreach (var k in st.raid_retry.Keys)
            st.raid_retry[k] = MathF.Max(0f, st.raid_retry[k] - dt);
        foreach (int faction in _factions)
        {
            st.raid_timer[faction] -= dt;
            if (st.raid_timer[faction] > 0f) continue;
            _prune(world);                 // a party that is gone leaves no plan behind
            st.raid_timer[faction] = RAID_INTERVAL;
            if (_raiders(world, faction).Length >= RAID_MAX)
                continue;                  // the ones already out are still working
            var lanes = world.rng.sample(_lane_ids(world), Math.Min(RAID_SIZE, world.lanes.Count));
            int party = st.raid_serial % 256;
            st.raid_serial += 1;
            var (bx, bz) = world.terrain.bases[faction];
            // one nav grid per launch: the two raiders of a party read the same
            // enemy picture, so building it twice would be 6 ms for nothing
            var (nav, force) = _nav(world, faction);
            for (int n = 0; n < lanes.Count; n++)
            {
                int lane = lanes[n];
                var (x, z) = _spawn_near(world, bx, bz, 90f, 200f);
                int eid = world.spawn_unit(Defs.ETYPE_MECH, faction, x, z, lane: lane,
                                           extra: _COMPS);
                e.uv("raider")[eid] = 1;
                e.uv("raid_party")[eid] = (byte)party;
                e.uv("raid_goal")[eid] = NO_TOWN;
                e.iv("raid_i")[eid] = 0;
                e.fv("raid_stuck")[eid] = 0f;
                e.fv("raid_t")[eid] = RAID_HOLD;
                e.uv("ai_state")[eid] = STATE_MARCH;
                if (n == 0)
                {
                    _replan(world, eid, nav, force);
                }
                else
                {
                    // one plan per tick: two A* runs and two nav grids in the same
                    // tick is 45 ms of a 33 ms frame, and a third of a second
                    // between two hulls getting their orders is invisible
                    // (`_drive` plans it as soon as the timer runs out, because it
                    // has no route yet)
                    st.raid_retry[key(world, eid)] = PLAN_GAP * n;
                }
            }
        }
    }

    /// <summary>Forget the plans of parties with no hull left alive.
    ///
    /// A raider dying is the normal end of a mission - it is a `combat` hull in the
    /// enemy's rear and the reinforcements it just cut off come for it - so the
    /// plans of dead parties have to be dropped, or every launch of a fifteen
    /// minute round would leave two route lists behind for the rest of it.</summary>
    public static void _prune(World world)
    {
        var st = state_of(world);
        var live = new HashSet<(int, int, int)>();
        foreach (int eid in world.raiders()) live.Add(key(world, eid));
        if (st.raid_paths.Count > 0)
        {
            var dead = new List<(int, int, int)>();
            foreach (var k in st.raid_paths.Keys) if (!live.Contains(k)) dead.Add(k);
            foreach (var k in dead) st.raid_paths.Remove(k);
        }
        if (st.raid_retry.Count > 0)
        {
            var dead = new List<(int, int, int)>();
            foreach (var k in st.raid_retry.Keys) if (!live.Contains(k)) dead.Add(k);
            foreach (var k in dead) st.raid_retry.Remove(k);
        }
    }

    /// <summary>A free spot in the ring around (x, z); the same rule the waves use.</summary>
    public static (float x, float z) _spawn_near(World world, float x, float z, float r0, float r1)
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

    // ------------------------------------------------------------------
    // the mission
    // ------------------------------------------------------------------
    /// <summary>Pick this raider's next weak spot on its lane and plot a route to it.
    ///
    /// The route is planned in two halves: the strategic grid carries it across the
    /// open field, and the last `HANDOVER` metres - the part that runs into a town -
    /// are planned on the 16 m street grid, because at 128 m a town is a smear of
    /// half walls and a plan drawn on it strands a hull against a house.</summary>
    public static void _replan(World world, int eid, GridNav? nav = null, float[]? force = null)
    {
        var e = world.ecs;
        var st = state_of(world);
        var k = key(world, eid);
        if (nav is null || force is null) (nav, force) = _nav(world, k.faction);
        st.raid_retry[k] = REPLAN;
        int goal = _pick_town(world, k.faction, k.lane, nav, force);
        var p = e.fv("pos").get3(eid);
        var start = (x: p.x, z: p.z);
        (float x, float z) end;
        if (goal == NO_TOWN)
        {
            end = world.terrain.bases[1 - k.faction];
        }
        else
        {
            var town = world.towns[goal];
            end = (town.x, town.z);
        }
        st.raid_paths[k] = _route(world.terrain, nav, start, end,
                                  goal == NO_TOWN ? null : _handover(start, end));
        e.uv("raid_goal")[eid] = (byte)goal;
        e.iv("raid_i")[eid] = 0;
        e.fv("raid_stuck")[eid] = 0f;
        e.fv("raid_t")[eid] = RAID_HOLD;
        e.uv("ai_state")[eid] = STATE_MARCH;
    }

    /// <summary>Plan a way out of whatever this hull has driven into, on the street
    /// grid.
    ///
    /// A raider that has pushed at something for `STUCK_REPLAN` seconds is not on
    /// the line it was given - it is behind a house, in a court, or on the wrong
    /// side of a wall the strategic grid did not resolve.  So the way out is
    /// planned where the walls are: the 16 m grid of the ground it is standing on,
    /// to the next way point it still has to reach.  Cheap (a small window, a few
    /// hundred nodes), so it can be run the moment a hull needs it instead of
    /// waiting out the `REPLAN` timer on a full re-plan.</summary>
    public static void _rescue(World world, int eid, List<(float x, float z)> path)
    {
        var e = world.ecs;
        var st = state_of(world);
        var k = key(world, eid);
        st.raid_retry[k] = RESCUE;
        var slot = e.slot(eid);
        if (!slot.ok) return;
        var b = slot.a;
        int row = slot.row;
        var pos = b.f("pos");
        float px = pos[row * 3], pz = pos[row * 3 + 2];
        int goal = b.u8("raid_goal")[row];
        var town = _town(world, goal);
        (float x, float z) end;
        if (town is not null) end = (town.x, town.z);
        else end = world.terrain.bases[1 - b.u8("faction")[row]];
        int i = b.i("raid_i")[row];
        var target = i < path.Count ? path[i] : end;
        var grid = new StreetNav(world.terrain, (px, pz), target);
        var legs = _legs(world.terrain, grid, (px, pz), target);
        if (legs.Count >= 2)
        {
            // `path[i + 1:]` in Python: an index already past the end of the route
            // (`_watch_stuck` may have walked it off) simply contributes nothing
            var np = new List<(float x, float z)>(legs.Count + Math.Max(0, path.Count - i));
            np.AddRange(legs);
            for (int q = i + 1; q < path.Count; q++) np.Add(path[q]);
            st.raid_paths[k] = np;
            b.i("raid_i")[row] = 0;
        }
        b.f("raid_stuck")[row] = 0f;
    }

    public static Town? _town(World world, int goal) =>
        goal >= 0 && goal < world.towns.Count ? world.towns[goal] : null;

    /// <summary>True when this raider has to be briefed again.
    ///
    /// Four cases: it has no route at all (a party's second hull is briefed a beat
    /// after the first); its target is not an enemy town any more and it is still
    /// marching at it (the front moved while it walked); its hold on the town it
    /// took has run out; or the enemy holds no towns left, so the base is the
    /// target.</summary>
    public static bool _needs_plan(World world, int eid)
    {
        var e = world.ecs;
        var st = state_of(world);
        if (!st.raid_paths.ContainsKey(key(world, eid))) return true;
        int state = e.uv("ai_state")[eid];
        if (state == STATE_HOLD) return e.fv("raid_t")[eid] <= 0f;
        int goal = e.uv("raid_goal")[eid];
        if (goal == NO_TOWN) return false;
        var town = _town(world, goal);
        if (town is null) return true;
        return state == STATE_MARCH && town.owner == e.uv("faction")[eid];
    }

    public static void _drive(World world, float dt)
    {
        var e = world.ecs;
        var st = state_of(world);
        var stuck = e.fv("raid_stuck");
        foreach (int eid in world.raiders())
        {
            var k = key(world, eid);
            bool ready = !st.raid_retry.TryGetValue(k, out float left) || left <= 0f;
            if (ready && stuck[eid] > STUCK_REPLAN)
            {
                // it is against something the plan did not see: find the way out
                // where the walls are, before re-planning the whole march
                _rescue(world, eid, st.raid_paths.TryGetValue(k, out var cur) ? cur : _no_path);
            }
            else if (ready && _needs_plan(world, eid))
            {
                _replan(world, eid);
            }
            _one(world, eid, st.raid_paths.TryGetValue(k, out var path) ? path : _no_path, dt);
        }
    }

    /// <summary>One tick of the mission: march the route, then hold the town.
    ///
    /// The velocity is the only thing this writes - the guns are `Ai.run`'s, which
    /// is why a raider that meets something shoots back with a full crew instead
    /// of driving past it.  Standing still in the ring is what takes a town
    /// (`Waves._capture`), so ASSAULT is "get inside the ring and stop".</summary>
    public static void _one(World world, int eid, List<(float x, float z)> path, float dt)
    {
        var e = world.ecs;
        var slot = e.slot(eid);
        if (!slot.ok) return;
        var b = slot.a;
        int row = slot.row;
        var pos = b.f("pos");
        // what the hull is *told* to do; `Movement` turns it into `vel`
        var vel = b.f("vel_cmd");
        float px = pos[row * 3], pz = pos[row * 3 + 2];
        int goal = b.u8("raid_goal")[row];
        var town = _town(world, goal);
        int faction = b.u8("faction")[row];
        float tx, tz, stop;
        if (town is not null)
        {
            tx = town.x; tz = town.z; stop = TOWN_STOP;
        }
        else
        {
            var (bx, bz) = world.terrain.bases[1 - faction];
            tx = bx; tz = bz; stop = BASE_STOP;
        }
        float speed = Defs.UNIT_DEFS[Defs.ETYPE_MECH].speed;

        _watch_stuck(b, row, dt);

        int state = b.u8("ai_state")[row];
        float ax, az;
        if (state == STATE_MARCH)
        {
            int i = b.i("raid_i")[row];
            while (i < path.Count && _behind(px, pz, path, i)) i++;
            b.i("raid_i")[row] = i;
            if (i < path.Count)
            {
                ax = path[i].x;
                az = path[i].z;
            }
            else
            {
                ax = tx; az = tz;
                if (_d(tx, tz, px, pz) <= stop)
                {
                    // the route is done and the ring is underfoot: take the town
                    b.u8("ai_state")[row] = STATE_ASSAULT;
                    vel[row * 3] = 0f;
                    vel[row * 3 + 2] = 0f;
                    return;
                }
            }
        }
        else
        {
            // ASSAULT and HOLD are the same move - stay in the ring - and differ
            // only in what ends them: the capture (a town that is ours) starts the
            // hold, and the hold runs out into the next target.
            if (state == STATE_ASSAULT && town is not null && town.owner == faction)
            {
                b.u8("ai_state")[row] = STATE_HOLD;
                b.f("raid_t")[row] = RAID_HOLD;
            }
            else if (state == STATE_HOLD)
            {
                b.f("raid_t")[row] -= dt;
            }
            if (_d(tx, tz, px, pz) <= stop)
            {
                vel[row * 3] = 0f;
                vel[row * 3 + 2] = 0f;
                return;
            }
            ax = tx;
            az = tz;
        }

        float dx = ax - px, dz = az - pz;
        float d = MathF.Sqrt(dx * dx + dz * dz);
        if (d < 1e-3f)
        {
            vel[row * 3] = 0f;
            vel[row * 3 + 2] = 0f;
            return;
        }
        var (sx, sz) = _steer(world.terrain, px, pz, dx / d, dz / d, pos[row * 3 + 1]);
        vel[row * 3] = sx * speed;
        vel[row * 3 + 2] = sz * speed;
    }

    /// <summary>Count the seconds a hull spends pushing at something it cannot pass.
    ///
    /// A mech asks for a velocity, `Movement` integrates what the ground allows,
    /// and the two are in `vel_cmd` and `vel` - so a commanded hull whose real
    /// speed is a quarter of the command is against a wall, a trunk or a house.
    /// After `STUCK_SKIP` seconds of that the way point it is heading for is
    /// abandoned (the grid put it somewhere a 1.9 m mech does not fit), and
    /// `_needs_plan` replans the whole route after `STUCK_REPLAN`, which is the
    /// honest fix for a hull that has driven into a court with one way out.</summary>
    public static void _watch_stuck(Archetype b, int row, float dt)
    {
        var cmd = b.f("vel_cmd");
        var vel = b.f("vel");
        float want = MathF.Sqrt(cmd[row * 3] * cmd[row * 3] + cmd[row * 3 + 2] * cmd[row * 3 + 2]);
        float real = MathF.Sqrt(vel[row * 3] * vel[row * 3] + vel[row * 3 + 2] * vel[row * 3 + 2]);
        var stuck = b.f("raid_stuck");
        // `STUCK_FAST` keeps the launch out of it: a mech off the mark is under a
        // quarter of the speed it asked for for the first second or so, and pulling
        // away from a standstill is not pushing at a wall
        if (want < 1f || real >= MathF.Min(STUCK_RATE * want, STUCK_FAST))
        {
            stuck[row] = 0f;
            return;
        }
        stuck[row] += dt;
        if (stuck[row] >= STUCK_SKIP)
        {
            stuck[row] -= STUCK_SKIP;
            b.i("raid_i")[row] += 1;
        }
    }

    /// <summary>True when way point `i` is behind the hull and can be dropped.
    ///
    /// Inside the acceptance radius, or already past the line through the point
    /// square to the leg arriving from the previous one - the same rule the wheeled
    /// route follower uses (`Ai._passed`), so a hull that overshot in a fight does
    /// not turn back for a corner it will never need.</summary>
    public static bool _behind(float px, float pz, List<(float x, float z)> path, int i)
    {
        var (wx, wz) = path[i];
        if ((px - wx) * (px - wx) + (pz - wz) * (pz - wz) <= ARRIVE * ARRIVE) return true;
        if (i <= 0) return false;
        var prev = path[i - 1];
        float lx = wx - prev.x, lz = wz - prev.z;
        return (lx * lx + lz * lz) > 1e-9f && ((px - wx) * lx + (pz - wz) * lz) > 0f;
    }

    /// <summary>The direction to walk: the straight line, or the first clear fan
    /// offset.
    ///
    /// Clearance is tested *along the first few metres*, not at one point in front:
    /// `Movement` moves a hull one 1.6 m step at a time and refuses any step where
    /// the hull does not fit, so a fan that only looks 4 m out will happily steer
    /// through a house whose far side is open - and the hull grinds against the
    /// corner for ever.  The reverse bearings are in the fan as well, because the
    /// way out of a court is usually the way in.</summary>
    public static (float dx, float dz) _steer(Terrain terrain, float px, float pz,
                                              float dx, float dz, float y)
    {
        if (_clear_run(terrain, px, pz, dx, dz, y)) return (dx, dz);
        float base_a = MathF.Atan2(dz, dx);
        foreach (float off in _fan)
        {
            float a = base_a + off;
            float nx = MathF.Cos(a), nz = MathF.Sin(a);
            if (_clear_run(terrain, px, pz, nx, nz, y)) return (nx, nz);
        }
        return (dx, dz);
    }

    private static readonly float[] _fan =
        { 0.5f, -0.5f, 1.0f, -1.0f, 1.6f, -1.6f, 2.2f, -2.2f, MathF.PI };

    /// <summary>Whether a mech can drive at least a couple of metres this way.</summary>
    public static bool _clear_run(Terrain terrain, float x, float z, float dx, float dz, float y)
    {
        foreach (float step in _PROBE)
            if (!_clear(terrain, x + dx * step, z + dz * step, y)) return false;
        return true;
    }

    /// <summary>Whether a hull of mech size fits at (x, z) - the samples `Movement`
    /// uses.
    ///
    /// A raised spot is not an obstacle for a mech (it has legs), but a step so high
    /// that the hull would be inside the ground is, so the ground height is checked
    /// against a mech's own step as well.</summary>
    public static bool _clear(Terrain terrain, float x, float z, float y)
    {
        float rad = Defs.UNIT_DEFS[Defs.ETYPE_MECH].radius * 0.6f;
        if (terrain.blocked(x, z) || terrain.blocked(x + rad, z) ||
            terrain.blocked(x - rad, z) || terrain.blocked(x, z + rad) ||
            terrain.blocked(x, z - rad))
            return false;
        if (terrain.trunk_blocked(x, z, rad)) return false;
        return MathF.Abs(terrain.height_at(x, z) - y) < 4f;
    }

    // ------------------------------------------------------------------
    // planning: the enemy picture, the choice of town, the route
    // ------------------------------------------------------------------
    /// <summary>The node grid both planners read: what a hull may cross and what it
    /// would rather not.
    ///
    /// `cost` is the static half of the route cost - one for open ground, more for
    /// woods or buildings - and `open` is what A* is allowed to stand on at all.
    /// Python has these two attributes duplicated on `Nav` and `StreetNav`; here
    /// they are one base class, and the two subclasses differ only in how they fill
    /// them (a 128 m strategic node is a mean over 8x8 tiles, a street node is one
    /// tile).
    /// </summary>
    public abstract class GridNav
    {
        public int cw, cd;
        public float cell_m, x0, z0;
        public bool[] open = Array.Empty<bool>();
        public float[] cost = Array.Empty<float>();

        public (int i, int j) cell(float x, float z)
        {
            int i = MathUtil.clamp((int)((x - x0) / cell_m), 0, cw - 1);
            int j = MathUtil.clamp((int)((z - z0) / cell_m), 0, cd - 1);
            return (i, j);
        }

        public (float x, float z) at(int i, int j) =>
            (x0 + (i + 0.5f) * cell_m, z0 + (j + 0.5f) * cell_m);
    }

    /// <summary>A 128 m node grid over the tile map: what a raider may cross and
    /// what it would rather not.
    ///
    /// `cost` is the static half of the route cost - one for open ground, more for
    /// woods or buildings - and `open` is what A* is allowed to stand on at all.
    /// It is rebuilt per plan, not per tick: the map does not change (the debug
    /// tree knob repaints tiles, and the next plan is the earliest anything could
    /// know).</summary>
    public sealed class Nav : GridNav
    {
        public Nav(Terrain terrain, float[] force)
        {
            int k = (int)(NAV_CELL / Terrain.TILE);
            int nw = terrain.w / k, nd = terrain.d / k;
            cw = nw;
            cd = nd;
            cell_m = NAV_CELL;
            x0 = 0f;
            z0 = 0f;
            open = new bool[cd * cw];
            cost = new float[cd * cw];
            float inv = 1f / (k * k);
            for (int j = 0; j < cd; j++)
            {
                for (int i = 0; i < cw; i++)
                {
                    int solid_n = 0, wood_n = 0, build_n = 0;
                    for (int jj = 0; jj < k; jj++)
                    {
                        int rowbase = (j * k + jj) * terrain.w + i * k;
                        for (int ii = 0; ii < k; ii++)
                        {
                            byte t = terrain.tiles[rowbase + ii];
                            if (t == Terrain.TILE_BUILDING || t == Terrain.TILE_ROCK ||
                                t == Terrain.TILE_SOLID || t == Terrain.TILE_BASE) solid_n++;
                            if (t == Terrain.TILE_TREE || t == Terrain.TILE_FOREST) wood_n++;
                            if (t == Terrain.TILE_BUILDING) build_n++;
                        }
                    }
                    float solid = solid_n * inv, wood = wood_n * inv, build = build_n * inv;
                    int n = j * cw + i;
                    bool is_open = solid < 0.5f;         // drivable unless half of the node is wall
                    open[n] = is_open;
                    cost[n] = is_open
                        ? 1f + WOOD_COST * wood + WALL_COST * build
                          + GRADIENT_COST * MathF.Min(GRADIENT_CAP, force[n])
                        : float.PositiveInfinity;
                }
            }
        }
    }

    /// <summary>The street grid: one node per 16 m map tile, over a window around a
    /// town.
    ///
    /// The strategic grid cannot see a street.  At 128 m a node is only "open"
    /// unless half of it is wall, so a town is a smear it will happily plan a route
    /// through - and the hull that drives that route ends up against a house with
    /// nothing to do but push.  This is the same idea one level down: a node per map
    /// tile, buildings and rock impassable, the streets and the plaza open, the
    /// woods expensive (a mech may drive under a canopy, it does not want to).  It
    /// is built for the window it is needed in - a town and its approach, or the
    /// ground around a hull that has got itself stuck - so it is about the size of
    /// the strategic grid and costs about the same to search.
    ///
    /// Cost per node is `1 + STREET_TREE_COST` of wood: the streets are all equal as
    /// far as a walking hull is concerned, and a route through a park is a route
    /// through a park.</summary>
    public sealed class StreetNav : GridNav
    {
        public StreetNav(Terrain terrain, (float x, float z) a, (float x, float z) b,
                         float pad = WINDOW_PAD)
        {
            float bx0 = MathF.Max(0f, MathF.Min(a.x, b.x) - pad);
            float bz0 = MathF.Max(0f, MathF.Min(a.z, b.z) - pad);
            float bx1 = MathF.Min(terrain.w * Terrain.TILE, MathF.Max(a.x, b.x) + pad + Terrain.TILE);
            float bz1 = MathF.Min(terrain.d * Terrain.TILE, MathF.Max(a.z, b.z) + pad + Terrain.TILE);
            int i0 = (int)(bx0 / Terrain.TILE), j0 = (int)(bz0 / Terrain.TILE);
            int i1 = (int)(bx1 / Terrain.TILE), j1 = (int)(bz1 / Terrain.TILE);
            cw = Math.Max(1, i1 - i0);
            cd = Math.Max(1, j1 - j0);
            cell_m = Terrain.TILE;
            x0 = i0 * Terrain.TILE;
            z0 = j0 * Terrain.TILE;
            open = new bool[cd * cw];
            cost = new float[cd * cw];
            for (int j = 0; j < cd; j++)
            {
                int tj = Math.Min(terrain.d - 1, j0 + j);
                for (int i = 0; i < cw; i++)
                {
                    int ti = Math.Min(terrain.w - 1, i0 + i);
                    byte t = terrain.tiles[tj * terrain.w + ti];
                    bool solid = t == Terrain.TILE_BUILDING || t == Terrain.TILE_ROCK ||
                                 t == Terrain.TILE_SOLID || t == Terrain.TILE_BASE;
                    bool wood = t == Terrain.TILE_TREE || t == Terrain.TILE_FOREST;
                    int n = j * cw + i;
                    open[n] = !solid;
                    cost[n] = solid ? float.PositiveInfinity
                                    : 1f + STREET_TREE_COST * (wood ? 1f : 0f);
                }
            }
        }
    }

    public static (Nav nav, float[] force) _nav(World world, int faction)
    {
        var force = _gradient(world, faction);
        return (new Nav(world.terrain, force), force);
    }

    /// <summary>The enemy force gradient: how much of his army would see a hull
    /// here.
    ///
    /// Not a count of hulls in a box - that weights a rifleman 700 m away the same
    /// as a mech on top of you.  Each hull is spread over the ground its own weapons
    /// can see (`UnitDef.sight`) with a triangular falloff, weighted by what it is
    /// worth (`FORCE_WEIGHT`), so a node reads "defenders that could shoot me here"
    /// and the value fades with distance the way a threat does.  The sum over the
    /// map *is* the shape of the enemy's position: a ridge of high ground along the
    /// front, thin in his rear - the two things a raider has to read off it.
    ///
    /// One binned count per hull type, four box filters: the whole field costs a few
    /// milliseconds, which is why the route cost and the choice of town can both be
    /// an array index.</summary>
    public static float[] _gradient(World world, int faction)
    {
        int k = (int)(NAV_CELL / Terrain.TILE);
        var terrain = world.terrain;
        int cw = terrain.w / k, cd = terrain.d / k;
        var force = new float[cd * cw];
        var e = world.ecs;
        var all = world.combat_units();
        if (all.Length == 0) return force;
        var etype = e.uv("etype");
        var pos = e.fv("pos");
        // the enemy's hulls only
        int m = 0;
        var ids = new int[all.Length];
        foreach (int eid in all)
            if (e.uv("faction")[eid] == 1 - faction) ids[m++] = eid;
        if (m == 0) return force;
        var seen = new bool[256];
        for (int q = 0; q < m; q++) seen[etype[ids[q]]] = true;
        for (int t = 0; t < 256; t++)
        {
            if (!seen[t]) continue;
            if (!Defs.UNIT_DEFS.TryGetValue(t, out var d)) continue;
            float reach = d.sight;
            if (reach <= 0f || d.weapons[0] == Defs.WID_NONE)
                continue;                      // no guns, no threat: the command base
            var count = new float[cd * cw];
            for (int q = 0; q < m; q++)
            {
                int eid = ids[q];
                if (etype[eid] != t) continue;
                var p = pos.get3(eid);
                int i = MathUtil.clamp((int)(p.x * (1f / NAV_CELL)), 0, cw - 1);
                int j = MathUtil.clamp((int)(p.z * (1f / NAV_CELL)), 0, cd - 1);
                count[j * cw + i] += 1f;
            }
            var spread = _spread(count, reach, cd, cw);
            float w = FORCE_WEIGHT.TryGetValue(t, out float fw) ? fw : 1f;
            for (int q = 0; q < force.Length; q++) force[q] += w * spread[q];
        }
        return force;
    }

    /// <summary>A binned count spread over the ground of `reach` metres around it.
    ///
    /// A triangle, made by convolving a box with itself, because that is what an
    /// integral image can do in one pass: a hull sitting on a node reads 1.0 there
    /// and 0 at `reach`, so the gradient has a slope instead of a cliff at the edge
    /// of a window.</summary>
    public static float[] _spread(float[] count, float reach, int cd, int cw)
    {
        int h = Math.Max(1, (int)MathF.Round(reach / (2f * NAV_CELL)));
        int w = 2 * h + 1;
        var a = _box(count, cd, cw, h);
        var b = _box(a, cd, cw, h);
        float inv = 1f / (w * w);
        for (int q = 0; q < b.Length; q++) b[q] *= inv;
        return b;
    }

    /// <summary>Sum of `a` over a (2r+1) square window, through one integral image.
    /// (`cd` / `cw` are what numpy read off `a.shape`.)</summary>
    public static float[] _box(float[] a, int cd, int cw, int r)
    {
        int pw = cw + 1;
        var p = new float[(cd + 1) * pw];
        for (int j = 0; j < cd; j++)
        {
            float run = 0f;
            for (int i = 0; i < cw; i++)
            {
                run += a[j * cw + i];
                p[(j + 1) * pw + (i + 1)] = p[j * pw + (i + 1)] + run;
            }
        }
        var outv = new float[cd * cw];
        for (int j = 0; j < cd; j++)
        {
            int j0 = MathUtil.clamp(j - r, 0, cd);
            int j1 = MathUtil.clamp(j + r + 1, 0, cd);
            for (int i = 0; i < cw; i++)
            {
                int i0 = MathUtil.clamp(i - r, 0, cw);
                int i1 = MathUtil.clamp(i + r + 1, 0, cw);
                outv[j * cw + i] = p[j1 * pw + i1] - p[j0 * pw + i1]
                                 - p[j1 * pw + i0] + p[j0 * pw + i0];
            }
        }
        return outv;
    }

    /// <summary>The enemy town on this raider's lane with the least of his army
    /// about it.
    ///
    /// The rear is where he is thin - the front is where his hulls are - so the
    /// weakest town is the one behind his line, and taking it cuts the chain that
    /// feeds the front (`Waves.front_town`).  The score is the force gradient *at
    /// the town* (`_gradient`: defenders that could shoot a hull standing there),
    /// plus `THREAT_KM` per kilometre of march so two towns that are equally empty
    /// do not send a raider across the map for nothing.
    ///
    /// A lane with nothing left to cut on it hands the raider the weakest town of
    /// *any* lane instead - a hull sent out with no target is a hull wasted - and
    /// only when the enemy holds no towns at all does it go for the command
    /// base.</summary>
    public static int _pick_town(World world, int faction, int lane, GridNav nav, float[] force)
    {
        var (bx, bz) = world.terrain.bases[faction];
        int best = NO_TOWN, other = NO_TOWN;
        float best_score = float.PositiveInfinity;    // on the raider's own lane
        float other_score = float.PositiveInfinity;   // anywhere
        for (int k = 0; k < world.towns.Count; k++)
        {
            var town = world.towns[k];
            if (town.owner != 1 - faction) continue;
            var (i, j) = nav.cell(town.x, town.z);
            float score = force[j * nav.cw + i]
                        + THREAT_KM * _d(town.x, town.z, bx, bz) / 1000f;
            if (score < other_score)
            {
                other = k;
                other_score = score;
            }
            if (town.lane == lane && score < best_score)
            {
                best = k;
                best_score = score;
            }
        }
        return best != NO_TOWN ? best : other;
    }

    /// <summary>Way points from `start` to `end`, planned at two scales.
    ///
    /// The strategic grid carries the raider across the open field to `street`
    /// (`_handover`), and from there the street grid takes it the last few hundred
    /// metres into the town - one plan, one list of way points, and the driver does
    /// not need to know which half it is on.</summary>
    public static List<(float x, float z)> _route(Terrain terrain, GridNav nav,
                                                  (float x, float z) start, (float x, float z) end,
                                                  (float x, float z)? street = null)
    {
        if (street is null) return _legs(terrain, nav, start, end);
        var pts = _legs(terrain, nav, start, street.Value);
        var town = new StreetNav(terrain, street.Value, end);
        var legs = _legs(terrain, town, street.Value, end);
        if (legs.Count == 0) return pts;
        var outv = new List<(float x, float z)>(pts.Count + legs.Count);
        for (int i = 0; i + 1 < pts.Count; i++) outv.Add(pts[i]);
        outv.AddRange(legs);
        return outv;
    }

    /// <summary>Way points for one leg: A* on `grid`, then shortened through open
    /// ground.</summary>
    public static List<(float x, float z)> _legs(Terrain terrain, GridNav grid,
                                                 (float x, float z) start, (float x, float z) end)
    {
        var a = _drivable(grid, grid.cell(start.x, start.z));
        var b = _drivable(grid, grid.cell(end.x, end.z));
        if (a is null || b is null) return new List<(float x, float z)>();
        var cells = _astar(grid, a.Value, b.Value);
        if (cells.Count == 0) return new List<(float x, float z)>();
        var pts = new List<(float x, float z)>(cells.Count);
        foreach (var (i, j) in cells) pts.Add(grid.at(i, j));
        pts[0] = start;
        pts[^1] = end;
        return _shortcut(terrain, pts);
    }

    /// <summary>Where the street plan takes over from the strategic one.
    ///
    /// `HANDOVER` metres out from the town middle, on the line the raider arrives
    /// along: far enough out that the strategic grid's last decision is made in open
    /// country, close enough that the street plan sees the town's edge and the ring
    /// it has to hold.</summary>
    public static (float x, float z) _handover((float x, float z) start, (float x, float z) end)
    {
        float d = _d(start.x, start.z, end.x, end.z);
        if (d <= HANDOVER + 1f) return start;
        float f = (d - HANDOVER) / d;
        return (start.x + (end.x - start.x) * f, start.z + (end.z - start.z) * f);
    }

    /// <summary>The nearest node A* may stand on: a base's own tiles are not
    /// drivable, and a raider starts and ends its plan in the middle of one.</summary>
    public static (int i, int j)? _drivable(GridNav nav, (int i, int j) cell, int reach = 6)
    {
        var (i0, j0) = cell;
        for (int r = 0; r <= reach; r++)
        {
            (int i, int j)? best = null;
            float best_d = 1e18f;
            for (int j = Math.Max(0, j0 - r); j < Math.Min(nav.cd, j0 + r + 1); j++)
            {
                for (int i = Math.Max(0, i0 - r); i < Math.Min(nav.cw, i0 + r + 1); i++)
                {
                    if (r != 0 && Math.Max(Math.Abs(i - i0), Math.Abs(j - j0)) != r)
                        continue;              // only the ring of this radius
                    if (!nav.open[j * nav.cw + i]) continue;
                    float d = (i - i0) * (i - i0) + (j - j0) * (j - j0);
                    if (d < best_d)
                    {
                        best = (i, j);
                        best_d = d;
                    }
                }
            }
            if (best is not null) return best;
        }
        return null;
    }

    /// <summary>The cheapest node route between two cells, 8-connected and weighted.
    ///
    /// A node diagonal only counts when both of its shared edges are drivable, so a
    /// route can never slip through the corner between two buildings.  The cost
    /// array is read straight out of the grid (Python turned it into a plain list
    /// first: this loop runs tens of thousands of steps and a numpy scalar costs
    /// more to read than the arithmetic around it - a C# float array is already
    /// that cheap).</summary>
    public static List<(int i, int j)> _astar(GridNav nav, (int i, int j) start, (int i, int j) goal)
    {
        int cw = nav.cw, cd = nav.cd;
        var cost = nav.cost;
        int n_nodes = cw * cd;
        int s = start.j * cw + start.i, g = goal.j * cw + goal.i;
        var outv = new List<(int i, int j)>();
        if (s == g)
        {
            outv.Add(start);
            return outv;
        }
        int gx = goal.i, gy = goal.j;
        float diag = _ROOT2 - 1f;
        var gs = new float[n_nodes];
        var prev = new int[n_nodes];
        var done = new bool[n_nodes];
        Array.Fill(gs, float.PositiveInfinity);
        Array.Fill(prev, -1);
        gs[s] = 0f;

        // octile estimate, weighted (`ASTAR_WEIGHT`): static, so the search loop
        // allocates nothing
        static float h(int i, int j, int gx, int gy, float diag)
        {
            int dx = i - gx, dy = j - gy;
            dx = dx >= 0 ? dx : -dx;
            dy = dy >= 0 ? dy : -dy;
            int lo = dx > dy ? dy : dx;
            int hi = dx > dy ? dx : dy;
            return ASTAR_WEIGHT * (hi + diag * lo);
        }

        var heap = new PriorityQueue<int, (float f, int n)>();
        heap.Enqueue(s, (h(start.i, start.j, gx, gy, diag), s));
        while (heap.Count > 0)
        {
            int n = heap.Dequeue();
            if (done[n]) continue;
            done[n] = true;
            if (n == g) break;
            int ni = n % cw, nj = n / cw;
            float base_g = gs[n];
            foreach (var (di, dj) in _NEIGH)
            {
                int i2 = ni + di, j2 = nj + dj;
                if (i2 < 0 || j2 < 0 || i2 >= cw || j2 >= cd) continue;
                int m = j2 * cw + i2;
                float c = cost[m];
                if (float.IsInfinity(c) || done[m]) continue;
                if (di != 0 && dj != 0 && (float.IsInfinity(cost[nj * cw + i2])
                                           || float.IsInfinity(cost[j2 * cw + ni])))
                    continue;                  // no corner cutting between two walls
                float ng = base_g + c * (di != 0 && dj != 0 ? _ROOT2 : 1f);
                if (ng < gs[m])
                {
                    gs[m] = ng;
                    prev[m] = n;
                    heap.Enqueue(m, (ng + h(i2, j2, gx, gy, diag), m));
                }
            }
        }
        if (!done[g]) return outv;
        int node = g;
        while (node >= 0)
        {
            outv.Add((node % cw, node / cw));
            node = prev[node];
        }
        outv.Reverse();
        return outv;
    }

    /// <summary>Straighten the node route into a line a hull can drive.
    ///
    /// A* answers a staircase of 128 m nodes; driven as given, a mech would walk the
    /// staircase.  So each way point is dropped when the one `look` ahead of it is
    /// still in a straight line of open ground - and "open" here excludes woods as
    /// well as walls, because crossing the fields is the whole point of the route
    /// (`_open_line`).  The look-ahead is short on purpose: the grid route is
    /// already diagonal wherever the ground is, and every extra point costs a line
    /// test of a kilometre through the tile map.</summary>
    public static List<(float x, float z)> _shortcut(Terrain terrain, List<(float x, float z)> pts,
                                                     int look = 4)
    {
        if (pts.Count <= 2) return new List<(float x, float z)>(pts);
        var outv = new List<(float x, float z)> { pts[0] };
        int k = 0;
        while (k < pts.Count - 1)
        {
            int n = Math.Min(pts.Count - 1, k + look);
            while (n > k + 1 && !_open_line(terrain, pts[k], pts[n])) n -= 1;
            outv.Add(pts[n]);
            k = n;
        }
        return outv;
    }

    /// <summary>A straight run of ground a raider may cross: no wall, no trunk, no
    /// wood.</summary>
    public static bool _open_line(Terrain terrain, (float x, float z) a, (float x, float z) b)
    {
        float dx = b.x - a.x, dz = b.z - a.z;
        int n = Math.Max(1, (int)(_d(a.x, a.z, b.x, b.z) / Terrain.TILE));
        for (int k = 1; k < n; k++)
        {
            float x = a.x + dx * k / n, z = a.z + dz * k / n;
            if (terrain.blocked(x, z)) return false;
            int t = terrain.tile_type(x, z);
            if (t == Terrain.TILE_TREE || t == Terrain.TILE_FOREST) return false;
        }
        return true;
    }

    private static List<int> _lane_ids(World world)
    {
        var ids = new List<int>(world.lanes.Count);
        for (int i = 0; i < world.lanes.Count; i++) ids.Add(i);
        return ids;
    }

    private static float _d(float ax, float az, float bx, float bz)
    {
        float dx = ax - bx, dz = az - bz;
        return MathF.Sqrt(dx * dx + dz * dz);
    }
}
