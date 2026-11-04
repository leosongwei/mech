// The world: owns the ECS, the terrain, the players and the system pipeline.
//
// Ported from `backend/server/world.py` (Player lives here too, exactly as it
// does there).

namespace Mech.Server;

/// <summary>One connected pilot and everything the server knows about him.</summary>
public sealed class Player
{
    public int pid;
    public string name;
    public int faction;
    public int mech = -1;                    // entity id, -1 while dead
    public float money = 600f;
    public int kills;
    public int deaths;
    public float respawn;
    public (float x, float z)? death_pos;    // where the hull died
    public float in_yaw;
    public float in_pitch;
    public (float x, float z) in_move = (0f, 0f);
    public (bool left, bool right) fire = (false, false);
    public int lock_target = -1;
    public float lock_progress;
    public int[] loadout = (int[])Defs.DEFAULT_LOADOUT.Clone();
    public int last_seq;
    public int score;
    public float zoom = 1f;                  // optics magnification, mirrored for the IR seeker
    public float zero_range = Defs.ZERO_DEFAULT;   // sight setting, metres
    public float aspect = 16f / 9f;
    public bool smoke_q;                     // one press of Q, consumed by Systems/Smoke.cs
    public float smoke_cd;                   // seconds until the next pull is allowed
    public int smoke_charges = Defs.SMOKE_CHARGES;
    public bool god;                         // debug: ignore all damage to this pilot
    // the rearm job the pilot paid for: the priced lines, and how long the whole
    // thing took.  The countdown itself lives in the `work_remaining_time`
    // component on the mech (Systems/Rearm.cs); `work_total` is only what the
    // progress bar needs to draw a fraction against.  A `dict` in Python and a
    // plain map here: `Shop` builds the lines, the rearm panel prints them.
    public List<Dictionary<string, object?>>? work;
    public float work_total;

    public Player(int pid, string name, int faction)
    {
        this.pid = pid;
        this.name = name;
        this.faction = faction;
    }

    /// <summary>Drop the rearm job - it finished, the mech died, or a round
    /// restarted.</summary>
    public void end_work()
    {
        work = null;
        work_total = 0f;
    }
}

public sealed class World
{
    public Modes.Mode mode_mod;
    public string mode;
    public Terrain terrain;
    public ECS ecs = new();
    public Grid grid;
    public Rng rng = new();          // one stream per thread (Core/Rng.cs)
    public Dictionary<int, Player> players = new();
    public int next_pid = 1;
    public float time;
    public int tick;
    public int wave;
    public float wave_timer = 8f;
    public int winner = -1;
    public float end_timer;
    public List<Dictionary<string, object?>> events = new();
    // the names the kill feed says out loud: index 0 is "nameless" (a car, a man,
    // a base - they answer to their type), everything else is a crew drawn off
    // `Defs.AI_NAMES` or a pilot's callsign.  Deduplicated, so the table is
    // bounded by the pool however long the server runs.
    public List<string> names = new() { "" };
    private readonly Dictionary<string, int> _name_ids = new() { [""] = 0 };
    public float dt = 1f / 30f;              // last step, for a table built mid-tick
    public SphereTable? _sph;               // this tick's hit-sphere table
    public int _sph_tick = -1;
    public bool[]? _aim_ok;                 // rows of that table still aimed at
    public bool _sph_premove;               // built before movement, not for the sweep
    public List<Town> towns;
    public List<List<(float x, float z)>> lanes;
    public List<List<(float x, float z)>> lanes_rev;
    public Dictionary<int, int> base_entity = new();
    public object? targets;                 // range mode only
    public object? interceptor;             // range mode only: the missile post
    public List<Action<World, float>> systems = new();

    /// <summary>The tick's RNG draw for anything that needs a float in [0,1) -
    /// kept as a method so the draw order stays visible at the call site.</summary>
    public float rand() => rng.random();

    public World(int seed = 11, string mode = "battle")
    {
        mode_mod = Modes.Modes.get(mode);
        this.mode = mode_mod.name;
        terrain = mode_mod.terrain(seed);
        Components.register(ecs);
        grid = new Grid(terrain.w * Terrain.TILE, terrain.d * Terrain.TILE);
        towns = terrain.towns;
        lanes = terrain.lanes;
        lanes_rev = new List<List<(float x, float z)>>();
        foreach (var l in terrain.lanes)
        {
            var rev = new List<(float x, float z)>(l);
            rev.Reverse();
            lanes_rev.Add(rev);
        }
        mode_mod.setup(this);
        _build_pipeline();
    }

    // ------------------------------------------------------------------
    // setup
    // ------------------------------------------------------------------
    public void _build_pipeline()
    {
        systems = new List<Action<World, float>>
        {
            Systems.Rearm.run, Systems.PlayerInput.run,
        };
        systems.AddRange(mode_mod.pre_systems());
        // the crews have said where they are looking (player_input, the mode's
        // own AI); the machines now slew towards it, so `yaw` / `pitch` are the
        // laid gun by the time anything fires or draws
        systems.AddRange(new Action<World, float>[]
        {
            Systems.Gunnery.run, Systems.Locking.run, Systems.Smoke.run, Systems.Weapons.run,
            Systems.Movement.run, Systems.Projectiles.run, Systems.Proximity.run,
        });
        // the damage chain: a round and a burst both *write* hp where they land
        // (Combat.apply), the bursts are judged one system after the rounds that
        // made them, and only `mortality` decides that a hull has died - so no
        // system ever destroys an entity while walking its own list
        systems.AddRange(new Action<World, float>[]
        {
            Systems.Blasts.run, Systems.Mortality.run, Systems.Economy.run, Systems.Debris.run,
        });
        systems.AddRange(mode_mod.post_systems());
    }

    // ------------------------------------------------------------------
    // entity factories
    // ------------------------------------------------------------------
    public int spawn_unit(int etype, int faction, float x, float z, int owner = -1,
                          int[]? weapons = null, float hp_scale = 1f,
                          int lane = -1, string[]? extra = null)
    {
        var e = ecs;
        var d = Defs.UNIT_DEFS[etype];
        // the component set *is* the entity's type, and it is decided once, here:
        // the shared bundle plus the tags this etype declares (`unit`, `combat`,
        // `moves`...), which is what every system queries instead of switching on
        // `etype`.  `extra` is what a *mission* adds on top of the type - the
        // `raider` tag and the plan it is executing (Modes/Battle/Raids.cs).
        var comps = new List<string>(etype == Defs.ETYPE_CIV
            ? Components.CIV_COMPS : Components.UNIT_COMPS);
        bool arms = d.arms is { Length: > 0 };
        if (arms) comps.AddRange(Components.ARM_COMPS);
        // the invariant a system may rely on (Components.cs): everything that
        // moves owns a place and a velocity, and a hull owns the intent too.
        // Asserted at the factory so a new entity type that forgets one dies at
        // its first spawn, not in a missile seeker three weeks later.
        if (!comps.Contains("pos") || !comps.Contains("vel"))
            throw new InvalidOperationException("a unit must own pos and vel");
        if (d.tags.Contains("moves") && !comps.Contains("vel_cmd"))
            throw new InvalidOperationException("a mover must own vel_cmd");
        comps.AddRange(d.tags);
        if (extra != null) comps.AddRange(extra);
        int eid = e.create(comps);
        float yaw = rng.random() * MathUtil.TAU;
        var pos = e.row_f(eid, "pos", out int row);
        float h = terrain.height_at(x, z);
        pos[row * 3] = x; pos[row * 3 + 1] = h; pos[row * 3 + 2] = z;
        e.fv("vel").set3(eid, 0f, 0f, 0f);
        e.fv("vel_cmd").set3(eid, 0f, 0f, 0f);
        e.fv("yaw")[eid] = yaw;
        e.fv("turret_yaw")[eid] = yaw;
        e.fv("leg_yaw")[eid] = yaw;
        e.fv("pitch")[eid] = 0f;
        // the sight starts on the hull's own line of sight, or a fresh mech would
        // swing its hull round to nothing on its first tick
        e.fv("sight_yaw")[eid] = yaw;
        e.fv("sight_pitch")[eid] = 0f;
        e.uv("etype")[eid] = (byte)etype;
        e.uv("faction")[eid] = (byte)faction;
        e.iv("owner")[eid] = owner;
        // one pool, and `hp <= 0` is the whole of "dead"; a hull with arms gets
        // one pool more per arm, which is the only difference there is
        float hp = d.hp * hp_scale;
        e.fv("hp")[eid] = hp;
        e.fv("hpmax")[eid] = hp;
        if (arms)
        {
            var armv = e.fv("arm_hp");
            var armmax = e.fv("arm_hpmax");
            for (int k = 0; k < 2 && k < d.arms!.Length; k++)
            {
                armv.set(eid, k, d.arms[k] * hp_scale);
                armmax.set(eid, k, d.arms[k] * hp_scale);
            }
        }
        e.fv("armor").set(eid, 0, d.dp);
        e.fv("armor").set(eid, 1, d.de);
        // what the kill feed calls it.  A hull with `arms` is a crew - a pilot's
        // machine carries his callsign, an AI crew draws one off the pool; a car
        // or a man has no name and answers to its type ("APC", "INFANTRY").
        if (arms)
        {
            if (owner >= 0 && players.ContainsKey(owner))
                e.uv16("name_id")[eid] = (ushort)name_id(players[owner].name);
            else if (owner < 0)
                e.uv16("name_id")[eid] = (ushort)name_id(Defs.ai_name(rng));
        }
        int[] ws = weapons ?? (arms ? Defs.ai_loadout(rng) : d.weapons);
        for (int arm = 0; arm < 2; arm++)
        {
            int wid = ws[arm];
            var wd = Defs.WEAPONS[wid];
            e.uv("weapon").set(eid, arm, (byte)wid);
            e.iv("mag").set(eid, arm, wd.mag);
            e.iv("ammo").set(eid, arm, wd.reserve);
        }
        e.uv("lane")[eid] = (byte)(lane >= 0 ? lane : 255);
        // a unit that was given a lane starts on the stretch it spawned nearest
        // to - a car told to follow the lane from its first way point would drive
        // back to the base before setting off
        e.iv("path_i")[eid] = lane >= 0 ? lane_index_near(lane, x, z, faction) : 0;
        e.uv("ai_state")[eid] = 0;
        if (etype == Defs.ETYPE_CIV)
        {
            e.fv("home_x")[eid] = x;
            e.fv("home_z")[eid] = z;
        }
        return eid;
    }

    public int spawn_mech(Player player)
    {
        player.smoke_charges = Defs.SMOKE_CHARGES;
        player.smoke_cd = 0f;
        player.smoke_q = false;
        player.end_work();
        // the machine comes back as the standard pair whatever the last one
        // carried: equipment is lost with the hull, so a bought 105 mm is a thing
        // to spend again after every death (see `Defs.DEFAULT_LOADOUT`)
        player.loadout = (int[])Defs.DEFAULT_LOADOUT.Clone();
        var spawn = mode_mod.mech_spawn(this, player);
        float x, z, yaw;
        if (spawn is null)
        {
            (x, z) = base_spawn(player.faction);
            yaw = rng.random() * MathUtil.TAU;
        }
        else
        {
            x = spawn.Value.x; z = spawn.Value.z; yaw = spawn.Value.yaw;
        }
        int eid = spawn_unit(Defs.ETYPE_MECH, player.faction, x, z, owner: player.pid,
                             weapons: player.loadout);
        ecs.fv("yaw")[eid] = yaw;
        ecs.fv("leg_yaw")[eid] = yaw;
        ecs.fv("sight_yaw")[eid] = yaw;
        player.mech = eid;
        player.respawn = 0f;
        player.death_pos = null;     // spent: the next death is its own
        return eid;
    }

    // ------------------------------------------------------------------
    // players
    // ------------------------------------------------------------------
    public Player add_player(string name)
    {
        var factions = new Dictionary<int, int> { [Defs.FACTION_RED] = 0, [Defs.FACTION_BLUE] = 0 };
        foreach (var p in players.Values)
            if (factions.ContainsKey(p.faction)) factions[p.faction]++;
        int faction = factions[Defs.FACTION_RED] <= factions[Defs.FACTION_BLUE]
            ? Defs.FACTION_RED : Defs.FACTION_BLUE;
        int pid = next_pid;
        next_pid += 1;
        var pl = new Player(pid, string.IsNullOrEmpty(name) ? $"PILOT-{pid}" : name, faction);
        if (mode_mod.fixed_faction is not null) pl.faction = mode_mod.fixed_faction.Value;
        pl.money = mode_mod.start_money;
        players[pid] = pl;
        spawn_mech(pl);
        return pl;
    }

    public void remove_player(int pid)
    {
        if (!players.TryGetValue(pid, out var pl)) return;
        players.Remove(pid);
        if (pl.mech >= 0 && ecs.alive[pl.mech]) ecs.destroy(pl.mech);
        pl.mech = -1;
    }

    // ------------------------------------------------------------------
    // queries
    // ------------------------------------------------------------------
    /// <summary>Every entity the client draws as a unit row: hulls, wrecks, corpses.</summary>
    public int[] all_units() => ecs.query("etype", "hp");

    /// <summary>Hulls: everything a round can hit and everything that can be
    /// damaged.  Wrecks and corpses dropped their `unit` tag when they died, so
    /// they are not in here - no exclusion list needed.</summary>
    public int[] units() => ecs.query("unit");

    /// <summary>Fighters: the only things AI may target and the only things that
    /// hold a town.</summary>
    public int[] combat_units() => ecs.query("combat");

    public int[] lockable_units() => ecs.query("lockable");

    /// <summary>Hulls on a raiding mission: the ones the raid AI drives off the
    /// lanes.</summary>
    public int[] raiders() => ecs.query("raider");

    public int[] movable() => ecs.query("moves");

    public int[] projectiles() => ecs.query("proj_kind");

    public int[] smoke_puffs() => ecs.query("smoke");

    public int[] blasts() => ecs.query("blast");

    /// <summary>Rounds in the air that can be fought: the missiles.</summary>
    public int[] interceptable() => ecs.query("interceptable");

    /// <summary>Every hit sphere on the field, built once per tick and shared.
    ///
    /// `dt` is what turns a hull's velocity into the ground it covered this tick
    /// - the sweep solves against a moving target (see
    /// `Systems/Projectiles.cs:sphere_table`).</summary>
    public SphereTable sphere_table(float dt)
    {
        if (_sph is null || _sph_tick != tick || _sph_premove)
        {
            _sph = Systems.Projectiles.sphere_table(this, dt);
            _sph_tick = tick;
            _sph_premove = false;
            _aim_ok = null;      // the aim mask belongs to the table it was built for
        }
        return _sph;
    }

    /// <summary>The hit spheres as they stood at the *start* of this tick, and
    /// which of those rows still describe a hull standing where they say.
    ///
    /// `sphere_table` is built during `projectiles`, i.e. after `movement` has
    /// moved everything, so the table it leaves behind is the end-of-tick state -
    /// which is precisely the state the next tick's aim cast reads, because `ai`
    /// and `weapons` run before `movement`.  Nothing is rebuilt for it.
    ///
    /// A row still counts when its id is a live hull owning `unit` (not a
    /// missile, which the same table carries for the sweep) and the hull is
    /// standing within ten metres of where the row put it - an id freed by a
    /// death and handed out again to something else must not be shot at.</summary>
    public (SphereTable table, bool[]? ok) aim_spheres()
    {
        if (_sph is null || _sph_tick < tick - 1)
        {
            sphere_table(dt);
            _sph_premove = true;     // built before movement: not the sweep's
        }
        if (_aim_ok is null)
        {
            var ids = _sph!.ids;
            int n = _sph.n;
            var ok = ecs.has_arr(ids[..n], "unit");
            bool any = false;
            for (int k = 0; k < ok.Length; k++) if (ok[k]) { any = true; break; }
            if (any)
            {
                var p = ecs.fv("pos");
                for (int k = 0; k < n; k++)
                {
                    if (!ok[k]) continue;
                    var pos = p.get3(ids[k]);
                    float dx = MathF.Abs(pos.x - _sph.centres[k * 3]);
                    float dy = MathF.Abs(pos.y - _sph.centres[k * 3 + 1]);
                    float dz = MathF.Abs(pos.z - _sph.centres[k * 3 + 2]);
                    if (MathF.Max(dx, MathF.Max(dy, dz)) >= 10f) ok[k] = false;
                }
            }
            _aim_ok = ok;
        }
        return (_sph!, _aim_ok);
    }

    /// <summary>The index of `name` in `names`, adding it the first time.</summary>
    public int name_id(string name)
    {
        if (_name_ids.TryGetValue(name, out int i)) return i;
        i = names.Count;
        names.Add(name);
        _name_ids[name] = i;
        return i;
    }

    /// <summary>The string behind a `name_id` component, or "" for a nameless hull.</summary>
    public string name_of(int name_id) =>
        name_id >= 0 && name_id < names.Count ? names[name_id] : "";

    /// <summary>Queue one fx event: `{t:"fx", k:kind, ...}`.</summary>
    public void emit(string kind, params (string key, object? value)[] data)
    {
        var msg = new Dictionary<string, object?>
        {
            ["t"] = "fx",
            ["k"] = kind,
        };
        foreach (var (key, value) in data) msg[key] = value;
        events.Add(msg);
        if (events.Count > 400) events.RemoveRange(0, 100);
    }

    // ------------------------------------------------------------------
    // helpers used by systems
    // ------------------------------------------------------------------
    /// <summary>True when a whole unit fits at (x, z) - the point test alone would
    /// let a mech materialise with its shoulder inside a wall.</summary>
    public bool _clear_spot(float x, float z, float radius = 4f)
    {
        if (terrain.blocked(x, z)) return false;
        for (int k = 0; k < 6; k++)
        {
            float a = k * MathUtil.TAU / 6f;
            if (terrain.blocked(x + MathF.Cos(a) * radius, z + MathF.Sin(a) * radius))
                return false;
        }
        // the command bases are drawn from primitives, not from tiles, so keep a
        // margin around them as well
        foreach (var (bx, bz) in terrain.bases.Values)
            if (MathF.Sqrt((x - bx) * (x - bx) + (z - bz) * (z - bz)) < 55f) return false;
        return true;
    }

    /// <summary>A free spot in the ring around the faction's own command base.</summary>
    public (float x, float z) base_spawn(int faction)
    {
        var (bx, bz) = terrain.bases[faction];
        // stand off the command base: its bunker and pylons are solid and a mech
        // that materialises inside one never gets free
        for (int k = 0; k < 24; k++)
        {
            float ang = rng.random() * MathUtil.TAU;
            float r = rng.uniform(70f, 130f);
            float x = bx + MathF.Cos(ang) * r;
            float z = bz + MathF.Sin(ang) * r;
            if (_clear_spot(x, z)) return (x, z);
        }
        // nothing random worked: walk outwards until the ground is free
        for (int r = 70; r < 420; r += 10)
        {
            for (int k = 0; k < 12; k++)
            {
                float ang = k * MathUtil.TAU / 12f;
                float x = bx + MathF.Cos(ang) * r;
                float z = bz + MathF.Sin(ang) * r;
                if (_clear_spot(x, z)) return (x, z);
            }
        }
        return (bx, bz);
    }

    /// <summary>Index into the lane as that faction walks it (blue's copy is
    /// reversed, so its indices are measured from its own base).</summary>
    public int lane_index_near(int lane, float x, float z, int faction = Defs.FACTION_RED)
    {
        var pts = lanes[lane % lanes.Count];
        if (faction == Defs.FACTION_BLUE) pts = lanes_rev[lane % lanes.Count];
        int best_i = 0;
        float best_d = 1e18f;
        for (int i = 0; i < pts.Count; i++)
        {
            var (px, pz) = pts[i];
            float d = (px - x) * (px - x) + (pz - z) * (pz - z);
            if (d < best_d) { best_d = d; best_i = i; }
        }
        return best_i;
    }

    // ------------------------------------------------------------------
    // tick
    // ------------------------------------------------------------------
    public void update(float dt)
    {
        time += dt;
        tick += 1;
        this.dt = dt;                // `aim_spheres` builds a table of its own
        // per-tick scratch: a system that spawns the replacement of a dead slot
        // must not see last tick's deaths
        ecs.destroyed.Clear();
        // the previous tick's hit-sphere table is *not* thrown away: it holds the
        // end-of-tick state, which is exactly where every hull stands now, and
        // `aim_spheres` hands it to the aim casts that run before movement
        // (`sphere_table` stamps the tick it was built for)
        //
        // refile every hull under the cell it stands in before anyone queries:
        // the build is one counting sort, the systems below ask "who is near me"
        // hundreds of times a tick.  Movers *and* combatants: a command base never
        // moves, and the AI has to find it to shoot it
        grid.build(ecs, "moves", "combat", "unit");
        foreach (var system in systems) system(this, dt);
        if (winner >= 0)
        {
            end_timer -= dt;
            if (end_timer <= 0f) reset_match();
        }
    }

    /// <summary>Wipe the battlefield and start a fresh round on the same map.</summary>
    public void reset_match()
    {
        var e = ecs;
        _sph = null;
        _sph_tick = -1;
        _aim_ok = null;
        foreach (var eid in e.query()) e.destroy(eid);
        events.Clear();
        winner = -1;
        end_timer = 0f;
        mode_mod.setup(this);
        foreach (var pl in players.Values)
        {
            pl.mech = -1;
            // a fresh round opens with a fresh purse: what was left of the last
            // war chest is not carried into this one (`add_player` starts a pilot
            // on the same figure).  The kills, deaths and score stay - they are
            // the record of the war, not of the round
            pl.money = mode_mod.start_money;
            pl.respawn = mode_mod.restart_respawn;
            pl.death_pos = null;
            pl.lock_target = -1;
            pl.lock_progress = 0f;
            pl.end_work();
        }
        emit("newmatch");
    }
}
