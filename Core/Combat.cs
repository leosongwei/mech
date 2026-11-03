// Damage resolution: the one place `hp` is written.
//
// Not a system - a library the systems call (`Ballistics`, `Shop`, `Grid` are
// the same kind of module).  A damage is not a thing that happens once a tick to
// the whole field, it is something a round does the moment it lands, so it lands
// there: forward, splash and the fuse all go through `apply` in their own loop.
//
//     apply   <- hit    <- Projectiles._impact   (a round landing on a hull)
//             <- splash <- Systems/Blasts.cs     (a hull inside a burst)
//             <- debug_hurt                      (a raw number, for testing)
//
// Two rules hold this together:
//
// * **`hp` is the health and `hp <= 0` is the death.**  Nothing here decides that
//   a unit died - `Systems/Mortality.cs` does, one stage later, so this module
//   never destroys anything and no loop ever has an entity vanish underneath it.
// * **A hull with modules carries the extra pools itself.**  Only the archetypes
//   that declare the `arms` tag own `arm_hp`; everyone else has one number and
//   one `else` branch.  That is the whole of the "mech is special" story, and it
//   is why `_mech_hit` below is the only code in the game that knows a hit can
//   land on something other than the hull.

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Mech.Server;

/// <summary>
/// The named loggers (`mech.prox`, `mech.smoke`, `mech.intercept`, ...).
///
/// Python's `logging.getLogger("mech.intercept")` has no direct C# equivalent
/// without a host to build the factory, and two of the ported libraries in this
/// folder need one (`Combat`, `Seeker`).  It lives here until the host lands
/// (`Net/Server.cs`, which owns `MECH_LOG_LEVEL`): it installs its factory into
/// `factory` at startup and every logger resolved afterwards writes through it.
///
/// The level convention does not change with the library: a per-round, per-hit,
/// per-lock line is DEBUG, never INFO (see AGENTS.md, "Logging").
/// </summary>
internal static class MechLog
{
    /// <summary>The host's factory; nothing is written until it is installed.</summary>
    public static ILoggerFactory factory = NullLoggerFactory.Instance;

    public static ILogger get(string name) => factory.CreateLogger(name);
}

public static class Combat
{
    // the interception logger: a round in the air is worth a line of its own at
    // DEBUG, because it starts with 10 hp and the numbers say whether a burst
    // was a graze or a kill (`Tools/RangeCheck.cs`, `tools/intercept_check.py`)
    private static readonly ILogger log = MechLog.get("mech.intercept");

    /// <summary>Take `amount` off the armour `module` names.  The only writer of `hp`.
    ///
    /// `by` is the hull that landed it as `(name_id, faction, etype)`, frozen
    /// here rather than looked up when the death is judged - a shooter can be a
    /// wreck by then, and its id can already belong to the wreck it left.  `wid`
    /// is the weapon it fired, for the kill feed's "with ..."
    /// (`Systems/Mortality.cs`).</summary>
    public static void apply(World world, int eid, int module, float amount, int pid = -1,
                             string src = "", (int name_id, int fac, int etype)? by = null,
                             int wid = -1)
    {
        var e = world.ecs;
        if (!e.is_alive(eid) || amount <= 0.0f) return;
        if (e.has(eid, "invulnerable")) return;      // concrete: rounds stop, nothing breaks
        // a missile owns no pilot, so the god check has nothing to ask about; it is
        // a round in the air and always takes what it is given
        if (e.has(eid, "owner"))
        {
            int owner = e.iv("owner")[eid];
            if (owner >= 0 && world.players.TryGetValue(owner, out var pilot) && pilot.god)
                return;
        }
        var slot = e.slot(eid);
        if (!slot.ok) return;
        var bufs = slot.a;
        int row = slot.row;
        if (!bufs.has("hp"))
        {
            // `hp` is the whole of "this can be hurt": an id that no longer owns a
            // pool is not a target any more.  A caller holding ids from earlier in
            // the tick can meet one - the hit-sphere table is built once a tick,
            // and an id freed by a round that landed can already belong to the
            // burst entity that round left behind (`Systems/Blasts.cs`) - so the
            // library refuses instead of raising on the buffer it cannot find.
            return;
        }
        var hp = bufs.f("hp");
        // debug only: a round in the air is worth a line of its own - it starts
        // with 10 hp, so the numbers say whether it was a graze or a kill - but
        // interception is settled, so this is off unless a tool asks for it
        bool watch = bufs.has("interceptable");
        float before = watch ? hp[row] : 0.0f;
        if (bufs.has("arm_hp")) _mech_hit(bufs, row, module, amount);
        else hp[row] = MathF.Max(0.0f, hp[row] - amount);
        if (watch && log.IsEnabled(LogLevel.Debug))
            log.LogDebug("missile {eid} took {amount:F1} ({src}) - hp {before:F1} -> {after:F1}",
                         eid, amount, src.Length > 0 ? src : "hit", before, hp[row]);
        // who earns this: recorded for the killing blow as well, because that is
        // the one that counts - the death itself is judged a stage later
        // (`Systems/Mortality.cs::_kill`), which reads this column to award the
        // kill money and to address the shot-down cue of a missile (`_kill_round`)
        if (pid >= 0 && bufs.has("last_hit")) bufs.i("last_hit")[row] = pid;
        // ...and who to *name*, which is not the same question: an AI crew's round
        // carries no pilot (`pid` is -1 for every one of them) but the crew, or the
        // car, still has to be called something on the feed
        if (by is not null && bufs.has("last_name"))
        {
            var (name_id, fac, etype) = by.Value;
            bufs.u16("last_name")[row] = (ushort)Math.Max(0, name_id);
            bufs.u8("last_fac")[row] = (byte)(fac < 0 ? 255 : fac);
            bufs.u8("last_etype")[row] = (byte)Math.Max(0, etype);
            bufs.u8("last_wid")[row] = (byte)Math.Max(0, wid);
        }
        if (hp[row] <= 0.0f) _stop(bufs, row);
    }

    /// <summary>A hull at zero hp stops dead where it is.
    ///
    /// The kill itself is judged a stage later (`Systems/Mortality.cs`), so for
    /// the rest of this tick the entity is still in the record - and without this
    /// it would go on driving, because movement and the AI run before the damage
    /// that finished it.  Both halves are zeroed: the truth (`vel`) so nothing
    /// leads on a dead hull, and the intent (`vel_cmd`) so nothing re-drives it
    /// next tick.</summary>
    public static void _stop(Archetype bufs, int row)
    {
        // a whole row, not one element: `pos`/`vel` are `float32[3]` per entity
        if (bufs.has("vel")) Array.Clear(bufs.f("vel"), row * 3, 3);
        if (bufs.has("vel_cmd")) Array.Clear(bufs.f("vel_cmd"), row * 3, 3);
    }

    /// <summary>One round landing on a unit: penetration plus blast, both halves.
    ///
    /// `dist` is how far the target's nearest surface is from the burst - 0 for a
    /// direct hit - and the blast half falls off to nothing over the weapon's
    /// `er`.</summary>
    public static void hit(World world, int eid, int module, WeaponDef wd, float dist = 0.0f,
                           int pid = -1, string src = "",
                           (int name_id, int fac, int etype)? by = null)
    {
        if (wd.ad <= 0.0f && wd.ed <= 0.0f) return;
        var (dp, de) = _armor(world, eid);
        apply(world, eid, module, Defs.hit_damage(wd, dp, de, dist), pid, src, by, wd.wid);
    }

    /// <summary>Blast-only damage for a unit near (not under) a burst.
    ///
    /// A charge going off a few metres away cannot punch a plate, so the
    /// penetration half of `hit` does not apply here.</summary>
    public static void splash(World world, int eid, int module, WeaponDef wd, float dist,
                              int pid = -1, string src = "",
                              (int name_id, int fac, int etype)? by = null)
    {
        if (wd.ed <= 0.0f) return;
        var (_, de) = _armor(world, eid);
        apply(world, eid, module, Defs.blast_damage(wd.ed, de, wd.er, dist), pid, src, by,
              wd.wid);
    }

    /// <summary>Is the weapon in `arm` (0 left, 1 right) still there to be fired?</summary>
    public static bool arm_alive(World world, int eid, int arm)
    {
        var e = world.ecs;
        if (!e.has(eid, "arm_hp")) return true;   // a single-pool hull has no arm to lose
        return e.fv("arm_hp").get(eid, arm) > 0.0f;
    }

    public static (float dp, float de) _armor(World world, int eid)
    {
        var e = world.ecs;
        if (!e.is_alive(eid)) return (0.0f, 0.0f);
        var a = e.fv("armor");
        return (a.get(eid, 0), a.get(eid, 1));
    }

    /// <summary>A hit on a hull that has arms: the only multi-pool code in the game.</summary>
    public static void _mech_hit(Archetype bufs, int row, int module, float amount)
    {
        var hp = bufs.f("hp");
        var arm = bufs.f("arm_hp");
        if (module == Defs.MOD_LEFT || module == Defs.MOD_RIGHT)
        {
            int a = module - Defs.MOD_LEFT;      // the arm index *is* the weapon index
            if (arm[row * 2 + a] > 0.0f)
            {
                arm[row * 2 + a] = MathF.Max(0.0f, arm[row * 2 + a] - amount);
                if (arm[row * 2 + a] <= 0.0f)    // that arm just came off
                {
                    bufs.i("mag")[row * 2 + a] = 0;
                    bufs.i("ammo")[row * 2 + a] = 0;
                }
                return;
            }
            // an arm that is already gone is not a shield: the round goes inwards
        }
        hp[row] = MathF.Max(0.0f, hp[row] - amount);
    }
}
