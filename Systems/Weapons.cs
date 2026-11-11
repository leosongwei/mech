// Weapon handling: magazines, reloads, cooldowns and projectile spawning.
//
// The system only *fires* stored intent (the trigger bits) - aiming lives in the
// player-input / AI systems, ballistics in `Core/Ballistics.cs` and the round's
// own flight in `Systems/Flights.cs`, and what a round runs into in
// `Systems/FirstHit.cs`.
//
// Two headings, never one (see `aim_of` / `bore_of`): the sight is where the
// crew is looking and answers instantly, the bore is where the metal points and
// has to slew after it (`Systems/Gunnery.cs`).  A round leaves along the bore.
//
// Ported from `backend/server/systems/weapons.py`.

namespace Mech.Server.Systems;

public static class Weapons
{
    // weapon kind -> the wire's `proj_kind` (what the round *flies* as)
    public static readonly Dictionary<string, int> KIND_ID = new()
    {
        ["bullet"] = 0, ["rocket"] = 1, ["saclos"] = 2, ["top"] = 3,
    };

    // the weapon table as one array, so a whole archetype of magazines can be
    // refilled in one pass instead of a table lookup per arm
    public static readonly int[] MAG_OF = Defs.WEAPONS.Select(w => w.mag).ToArray();

    public static int kind_id(string kind) => KIND_ID.TryGetValue(kind, out int k) ? k : 0;

    /// <summary>(origin, yaw, pitch) of the *sight*: where this unit's crew is looking.
    ///
    /// For a mech that is the free turret on the roof - the eye the camera sits in
    /// (`MathUtil.sight_eye`), the line the IR seeker sweeps, and the beam a SACLOS
    /// missile rides.  It answers the view instantly and is therefore *not* the
    /// barrels: those are on the hull and slew after it (`bore_of`), so ask for
    /// them at the muzzle, never here.</summary>
    public static (Vec3 origin, float yaw, float pitch) aim_of(World world, int eid)
    {
        var e = world.ecs;
        var pos = e.fv("pos").get3(eid);
        int etype = e.uv("etype")[eid];
        if (etype == Defs.ETYPE_MECH)
        {
            // the head is bolted to the hull, so the hull's yaw places the eye
            var origin = MathUtil.sight_eye(pos, e.fv("yaw")[eid]);
            return (origin, e.fv("sight_yaw")[eid], e.fv("sight_pitch")[eid]);
        }
        if (etype == Defs.ETYPE_APC)
        {
            float yaw = e.fv("turret_yaw")[eid];
            float pitch = e.fv("pitch")[eid];
            float h = Defs.UNIT_DEFS[etype].height;
            return (new Vec3(pos.x, pos.y + h * 0.85f, pos.z), yaw, pitch);
        }
        float hh = Defs.UNIT_DEFS.TryGetValue(etype, out var d) ? d.height : 1.8f;
        return (new Vec3(pos.x, pos.y + hh * 0.8f, pos.z),
                e.fv("yaw")[eid], e.fv("pitch")[eid]);
    }

    /// <summary>(yaw, pitch) the barrels of this unit are *actually* laid at.
    ///
    /// A mech's arms ride on the hull and elevate on their own pivots, so this is
    /// the hull's yaw and the arms' `pitch` - which trail the sight by up to the
    /// slew rates in `Systems/Gunnery.cs`.  A round leaves along these, not along
    /// the sight line: what the cross promises and what the gun delivers only
    /// agree once the machine has caught up.</summary>
    public static (float yaw, float pitch) bore_of(World world, int eid)
    {
        var e = world.ecs;
        if (e.uv("etype")[eid] == Defs.ETYPE_APC)
            return (e.fv("turret_yaw")[eid], e.fv("pitch")[eid]);
        return (e.fv("yaw")[eid], e.fv("pitch")[eid]);
    }

    public static void run(World world, float dt)
    {
        var e = world.ecs;
        var ids = world.units();
        if (ids.Length == 0) return;

        // ---- countdowns, a whole archetype at a time -----------------------
        // Every arm counts down on its own: no row looks at another, so this is one
        // pass per group instead of two Reads per (unit, arm).  A reload that lands
        // on this tick refills the magazine here and keeps the arm from firing this
        // tick, which is what the old per-entity `continue` meant.
        var just_reloaded = new HashSet<int>();
        foreach (var g in e.groups("cool", "reload_t", "mag", "ammo", "weapon"))
        {
            int n = g.n;
            var cool = g.f("cool");
            var reload_t = g.f("reload_t");
            var mag = g.i("mag");
            var ammo = g.i("ammo");
            var weapon = g.u8("weapon");
            for (int row = 0; row < n; row++)
            {
                for (int arm = 0; arm < 2; arm++)
                {
                    int k = row * 2 + arm;
                    if (cool[k] > 0f) cool[k] = MathF.Max(0f, cool[k] - dt);
                    if (reload_t[k] <= 0f) continue;
                    reload_t[k] -= dt;
                    if (reload_t[k] > 0f) continue;
                    int take = Math.Min(MAG_OF[weapon[k]], ammo[k]);
                    mag[k] = take;
                    ammo[k] = ammo[k] - take;
                    reload_t[k] = 0f;
                    just_reloaded.Add(g.ids[row] * 2 + arm);
                }
            }
        }

        // ---- firing -------------------------------------------------------
        var trigger = e.uv("trigger");
        var trigger_prev = e.uv("trigger_prev");
        var owner = e.iv("owner");
        // the AI's own target (`Systems/AI/Sense.cs` writes it): what a lock weapon on a
        // hull nobody is driving takes its lock from
        var ai_target = e.iv("target");
        e.locate(ids, out int[] rows, out int[] archs);

        for (int k = 0; k < ids.Length; k++)
        {
            int eid = ids[k];
            int ai = archs[k];
            int row = rows[k];
            if (ai < 0) continue;
            var a = e.archetypes[ai];
            int trig = trigger[eid];
            if (trig == 0)
            {
                // nothing is being asked for: still drop the edge state (a released
                // trigger is what re-arms a semi automatic arm) and move on
                if (trigger_prev[eid] != 0) trigger_prev[eid] = 0;
                continue;
            }
            for (int arm = 0; arm < 2; arm++)
            {
                int c = row * 2 + arm;
                if (a.f("reload_t")[c] > 0f) continue;     // still reloading
                if (just_reloaded.Count > 0 && just_reloaded.Contains(eid * 2 + arm))
                    continue;                              // finished this very tick
                int wid = a.u8("weapon")[c];
                if (wid == Defs.WID_NONE) continue;
                if (((trig >> arm) & 1) == 0) continue;
                if (a.has("arm_hp") && a.f("arm_hp")[c] <= 0f) continue;  // shot off
                var wd = Defs.WEAPONS[wid];
                // semi automatic arms fire once per pull: the trigger has to be
                // released and pressed again, holding it does nothing
                if (wd.semi != 0 && ((trigger_prev[eid] >> arm) & 1) != 0) continue;
                if (a.i("mag")[c] <= 0)
                {
                    if (a.i("ammo")[c] > 0) a.f("reload_t")[c] = wd.reload;
                    continue;
                }
                // a hair of float dust left over from the tick subtraction must not
                // cost a whole tick: 600 rpm on a 30 Hz clock is exactly 3 ticks,
                // and 0.1 - 3 * dt rounds to a positive 1e-9
                if (a.f("cool")[c] > 1e-4f) continue;
                int target = -1;
                if (wd.@lock != 0)
                {
                    if (world.players.TryGetValue(owner[eid], out var pl))
                    {
                        if (pl.lock_target < 0 || pl.lock_progress < 1f) continue;
                        if (!e.is_alive(pl.lock_target)) continue;
                        target = pl.lock_target;
                    }
                    else
                    {
                        // an AI crew has no seeker display to watch: its sight is
                        // already on the hull it is engaging (the trigger waits for
                        // the bore), so the round leaves the rack holding that lock
                        int tid = ai_target[eid];
                        if (tid < 0 || !e.is_alive(tid)) continue;
                        target = tid;
                    }
                }
                fire(world, eid, arm, wid, wd, target);
                // the round's own archetype may have grown inside `fire`, so the
                // buffers are read back off the hull's archetype rather than off a
                // reference taken before the shot
                a.i("mag")[c] -= 1;
                float cycle = 60f / MathF.Max(wd.rpm, 1f);
                if (wd.@lock != 0 && owner[eid] < 0)
                {
                    // an AI crew does not empty a rack of fire-and-forget rounds at
                    // the rack's own cycle: it launches one, watches it arrive, and
                    // only then thinks about the next.  Eight tubes at the rack's
                    // own cycle is the whole nest gone in two and a half seconds; at
                    // `AI_LAUNCH_GAP` a top attack is something that keeps coming
                    // back instead
                    cycle = MathF.Max(cycle, Defs.AI_LAUNCH_GAP);
                }
                a.f("cool")[c] = cycle;
            }
            trigger_prev[eid] = (byte)trig;
        }
    }

    /// <summary>The mouth a round leaves from, and the pitch its tubes are laid at.
    ///
    /// One decision, in one place, because three things read it: the gun itself
    /// (`fire`), a crew's fire control (`Systems/AI/Support/Engage.cs`, which solves the
    /// shot from the mouth it will really leave from), and the client's own muzzle
    /// (`scene.js` / `game._muzzle`, drawn from the same `defs.rack` spec).
    ///
    /// The rack's elevation comes first: a launcher bolted at its own quadrant
    /// stands there whatever the arm does (the top attack's 42 deg is the round's own
    /// loft), one carried on the arm adds its bias to the arm's aim, and one mounted
    /// flat (`elev` 0, the rocket nest) is simply the arm - depression included, or a
    /// nest could never bear on anything closer than its own range setting.  Never
    /// clamp this as a floor (`max(pitch, elev)`): a flat rack whose `elev` is 0
    /// would be pinned to the horizon.
    ///
    /// `right` / `up` are the tube the next round is in, in the rack's own lattice
    /// (`defs.rack_tube`): a pod fires out of the cells it still has rounds in.
    /// </summary>
    public static Vec3 launch_geometry(World world, int eid, int arm, WeaponDef wd,
                                       float yaw, float pitch,
                                       out float tube_pitch, out float right, out float up)
    {
        var e = world.ecs;
        var rack = wd.rack;
        float elev = rack is not null ? rack.elev : 0f;
        tube_pitch = rack is not null && rack.bolted ? elev : pitch + elev;
        right = 0f;
        up = 0f;
        var origin = e.fv("pos").get3(eid);
        if (e.uv("etype")[eid] != Defs.ETYPE_MECH) return origin;
        // where the bore ends, in order of who knows best: the weapon itself (the
        // 105 mm's barrel runs 5 m past its receiver), then its rack (a launcher of
        // tubes ends at the mouth of the tubes), then the kind's figure.
        float reach;
        if (wd.reach is not null) reach = wd.reach.Value;
        else if (rack is not null) reach = rack.mouth;
        else if (Defs.MECH_ARM_REACH.TryGetValue(wd.kind, out float r)) reach = r;
        else reach = 1.8f;
        if (rack is not null)
        {
            // `mag` is the magazine's size, `mag[eid, arm]` the pre-shot count here -
            // the caller takes the round off the top after this call
            (right, up) = Defs.rack_tube(wd.mag - e.iv("mag").get(eid, arm), rack);
        }
        return MathUtil.muzzle_point(origin, yaw, arm == 0 ? Defs.MOD_LEFT : Defs.MOD_RIGHT,
                                     tube_pitch, reach, right, up);
    }

    /// <summary>Spawn the projectile(s) of one trigger pull.
    ///
    /// The random draws are, per round of a salvo: one `gauss` for the yaw, one
    /// `gauss` for the pitch, then one `random()` for the round's own seed - in
    /// that order, because the battle is a replay of this sequence.</summary>
    public static void fire(World world, int eid, int arm, int wid, WeaponDef wd, int target = -1)
    {
        var e = world.ecs;
        var (yaw, pitch) = bore_of(world, eid);

        int kind = kind_id(wd.kind);
        int n = Math.Max(1, wd.salvo);
        float life = wd.life;

        // the mouth the round leaves, and the pitch its tubes are actually laid at
        // (`launch_geometry`: the rack's own elevation, the barrel's reach, the
        // tube the next round is in)
        var origin = launch_geometry(world, eid, arm, wd, yaw, pitch,
                                     out float pitch_tube, out _, out _);
        pitch = pitch_tube;

        // Direct fire is zeroed: the barrel is held off for the drop so the round
        // meets the sight line at the setting the gun carries.  Two settings, one
        // law (`Core/FireControl.cs`): a pilot's is the range he dialled in, which
        // keeps his reticle still while it crosses a crest; a crew has no reticle to
        // read, so its fire control solved the elevation for the target itself and
        // left it in `aim_elev`.  `bias` is the launcher's mechanical sighting and
        // is absorbed by the solution; guided rounds keep it.
        float? zero = null;
        if (kind == 0 || kind == 1)
        {
            if (world.players.TryGetValue(e.iv("owner")[eid], out var pl))
                zero = Ballistics.zero_at(origin, yaw, pitch, wd, pl.zero_range);
            else if (e.arch_of(eid)?.keyset.Contains("aim_elev") == true)
                zero = e.fv("aim_elev").get(eid, arm);
            else
                // an ownerless hull with no fire control is a *scripted* shooter (a
                // harness puppet, a mode's fixture): its gun holds the default sight
                // setting, exactly like a pilot who never touched the dial
                zero = Ballistics.zero_at(origin, yaw, pitch, wd, Defs.ZERO_DEFAULT);
        }

        for (int k = 0; k < n; k++)
        {
            float sp = wd.spread * (k == 0 ? 1f : 0.6f);
            float dy = yaw + world.rng.gauss(0f, sp);
            // `bias` is the mechanical elevation the launcher is sighted at: the
            // rocket pod is tilted up so its slow round meets the cannon at 1 km
            float dp = (zero is null ? pitch + wd.bias : zero.Value)
                       + world.rng.gauss(0f, sp);
            var d = MathUtil.dir_from(dy, dp);
            // the round's component set - which pool, which tag, whether it
            // carries a seeker - is decided once, in the factory
            world.spawn_round(eid, e.uv("faction")[eid], wid, kind,
                              origin, new Vec3(d.x * wd.vel, d.y * wd.vel, d.z * wd.vel),
                              life, target);
        }
        world.emit("shot", ("x", origin.x), ("y", origin.y), ("z", origin.z),
                   ("wid", wid), ("eid", eid), ("arm", arm));
    }
}
