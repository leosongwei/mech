// Battle mode: the 16 km three-lane front, bases, towns and waves.
//
// The rule set the default server runs: the map is generated from the seed
// (`Terrain.generate`), both sides start from a command base at the end of the
// three lanes, and the war is fought over the towns strung along them.  Two
// systems belong to this mode - `Raids` and `Waves` - and they sit on either side
// of the shared pipeline:
//
//     pre  : raids.run, ai.run     the parties steer themselves, then every crew
//                                  (theirs included) acquires and fires
//     post : waves.run             after everyone has moved: capture, columns
//
// `pre_systems` is where the AI has always been - it wants to run before
// lock-on / weapons / movement so its velocity and trigger land in the same tick.
// The raiding parties steer first and shoot through `ai.run` after, which is the
// one system that owns every crew's guns (`ai_state`, `raider`).  `waves` runs
// after the damage chain so a town is counted after the hulls have moved and
// after `mortality` has taken the dead off the field.
//
// Ported from `backend/server/modes/battle/__init__.py`.

namespace Mech.Server.Modes.Battle;

public sealed class BattleMode : Mode
{
    public BattleMode()
    {
        name = "battle";
        start_money = 600;
        restart_respawn = 3.0f;
    }

    public override Terrain terrain(int seed) => Terrain.generate(seed);

    public override void setup(World world)
    {
        world.wave = 0;
        world.wave_timer = 8f;
        world.base_entity = new Dictionary<int, int>();
        foreach (var town in world.towns)
        {
            town.owner = -1;
            town.progress = 0f;
        }
        Waves.bind(world);
        Raids.bind(world);
        _spawn_bases(world);
        _spawn_civilians(world);
        _assign_home_towns(world);
    }

    public override List<Action<World, float>> pre_systems()
    {
        // the battlefield AI drives every non-player hull: it wants to run before
        // lock-on/weapons/movement so its velocity and trigger land in the same
        // tick.  The raiding parties steer themselves first and shoot through
        // `ai.run` after - that system owns every crew's guns, including the
        // parties' (`ai_state`, `raider`).
        return new List<Action<World, float>> { Raids.run, Systems.Ai.run };
    }

    public override List<Action<World, float>> post_systems()
    {
        return new List<Action<World, float>> { Waves.run };
    }

    // ------------------------------------------------------------------
    // players
    // ------------------------------------------------------------------
    /// <summary>Where a pilot's machine appears.
    ///
    /// After a death: one town back down the lane the hull died on, the base if
    /// there is none behind it.  On the first deploy: the most advanced town still
    /// connected to the base, so a fresh pilot steps into the fight rather than
    /// walking 5 km out of the gate.</summary>
    public override (float x, float z, float yaw)? mech_spawn(World world, Player player)
    {
        Town? town;
        if (player.death_pos is null)
            town = _lead_town(world, player.faction);
        else
            town = Waves.respawn_town(world, player.faction,
                                      player.death_pos.Value.x, player.death_pos.Value.z);
        float x, z;
        if (town is null)
        {
            (x, z) = world.base_spawn(player.faction);
        }
        else
        {
            // a ring around the town, never its centre: the middle of a town is
            // exactly where the buildings and the plaza crowd are
            (x, z) = Waves.clear_near(world, town.x, town.z, 40f, 70f);
        }
        var (ex, ez) = world.terrain.bases[1 - player.faction];
        return (x, z, MathF.Atan2(ez - z, ex - x));
    }

    /// <summary>The front town nearest the enemy base on any lane, or null when
    /// every one of them is a stone's throw from their guns (900 m).</summary>
    public Town? _lead_town(World world, int faction)
    {
        var (ex, ez) = world.terrain.bases[1 - faction];
        Town? best = null;
        float best_d = 1e18f;
        for (int lane = 0; lane < world.lanes.Count; lane++)
        {
            var town = Waves.front_town(world, faction, lane);
            if (town is null) continue;
            float dx = town.x - ex, dz = town.z - ez;
            float d = MathF.Sqrt(dx * dx + dz * dz);
            if (d < best_d)
            {
                best = town;
                best_d = d;
            }
        }
        return best is not null && best_d > 900f ? best : null;
    }

    /// <summary>The wait is the walk back: long at home, short on the enemy's step.
    ///
    /// Interpolated on how far from the enemy command base the hull died, so a
    /// death on their doorstep is a 3 s turn-round and one at your own base is 40 s
    /// without a machine.</summary>
    public override float respawn_delay(World world, Player player, float x, float z)
    {
        var (hx, hz) = world.terrain.bases[player.faction];
        var (fx, fz) = world.terrain.bases[1 - player.faction];
        float span = MathF.Max(1f, _d(hx, hz, fx, fz));
        float k = MathF.Min(1f, _d(x, z, fx, fz) / span);
        return Defs.RESPAWN_AT_FRONT + (Defs.RESPAWN_AT_HOME - Defs.RESPAWN_AT_FRONT) * k;
    }

    // ------------------------------------------------------------------
    // battlefield
    // ------------------------------------------------------------------
    public void _spawn_bases(World world)
    {
        foreach (int faction in _factions)
        {
            var (bx, bz) = world.terrain.bases[faction];
            int eid = world.spawn_unit(Defs.ETYPE_BASE, faction, bx, bz);
            world.base_entity[faction] = eid;
            // decorative guard turret mechs stay at home
            for (int k = 0; k < 2; k++)
            {
                float ang = world.rng.random() * MathUtil.TAU;
                world.spawn_unit(Defs.ETYPE_MECH, faction,
                                 bx + MathF.Cos(ang) * 55f, bz + MathF.Sin(ang) * 55f,
                                 hp_scale: 1f, lane: 0);
            }
        }
    }

    public void _spawn_civilians(World world)
    {
        foreach (var town in world.towns)
        {
            int n = world.rng.randint(1, 3);
            for (int k = 0; k < n; k++)
            {
                float ang = world.rng.random() * MathUtil.TAU;
                float r = world.rng.uniform(15f, 70f);
                world.spawn_unit(Defs.ETYPE_CIV, Defs.FACTION_CIV,
                                 town.x + MathF.Cos(ang) * r, town.z + MathF.Sin(ang) * r);
            }
        }
    }

    /// <summary>Both sides start holding their closest towns: staging areas for
    /// reinforcements and forward respawn points.</summary>
    public void _assign_home_towns(World world)
    {
        foreach (int faction in _factions)
        {
            var (bx, bz) = world.terrain.bases[faction];
            // stable sort, like Python's `sorted`
            var nearest = world.towns.OrderBy(t => _d(t.x, t.z, bx, bz));
            int k = 0;
            foreach (var town in nearest)
            {
                if (k++ >= 3) break;
                town.owner = faction;
                town.progress = 0f;
            }
        }
    }

    // the two sides, in the order the round has always visited them
    private static readonly int[] _factions = { Defs.FACTION_RED, Defs.FACTION_BLUE };

    private static float _d(float ax, float az, float bx, float bz)
    {
        float dx = ax - bx, dz = az - bz;
        return MathF.Sqrt(dx * dx + dz * dz);
    }
}
