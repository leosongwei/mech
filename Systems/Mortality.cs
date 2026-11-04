// Death: the one place a hull stops being a hull.
//
// `Combat.apply` takes hit points off and decides nothing; this system asks the
// only question there is - is the pool empty - once per tick, for every unit on
// the field, and then follows the type's own `death` recipe:
//
//     ecs.destroy            the hull is gone (an entity that no longer exists is
//                            the whole of "dead" - there is no state flag)
//     _leave_wreck           what is left: a corpse, a smoking hulk, or nothing
//     Blasts.spawn           a wreck goes up, and the burst is judged next tick
//     _award / _plan_respawn three match rules: kill money, the walk back, victory
//
// Keeping the judgement here rather than inside `apply` is what makes the damage
// path re-entrant-free: no system destroys an entity while walking its own list.
// The price is that a hull at 0 hp still exists for the rest of the tick, which
// is why this runs after every damage source (`Blasts`) and before the mode's own
// post-systems (`Waves` counts hulls to take a town).
//
// The second channel is the same question asked of the *rounds in the air*: a
// missile carries its own pool of health (`Components.MISSILE_COMPS`), so a
// 30 mm burst that fused on it takes it off the field a tick later -
// `_kill_round`, which is deliberately not `_kill`: no warhead, no wreck, no
// credit.
//
// Ported from `backend/server/systems/mortality.py`.

using Microsoft.Extensions.Logging;

namespace Mech.Server.Systems;

public static class Mortality
{
    // a round shot down is a per-round event: DEBUG, never INFO
    private static readonly ILogger log = MechLog.get("mech.intercept");

    public static void run(World world, float dt)
    {
        var e = world.ecs;
        var ids = world.units();
        if (ids.Length > 0)
        {
            var hp = e.fv("hp");
            foreach (int eid in ids)
            {
                if (hp[eid] <= 0f) _kill(world, eid);
            }
        }
        // second channel: a round in the air carries health too.  Same question,
        // same one-writer rule - `Combat.apply` took the points off, this decides
        // that the pool is empty - but a very different death: a shot-down missile
        // does not go up as a warhead.
        var mis = world.interceptable();
        if (mis.Length > 0)
        {
            var hp = e.fv("hp");
            foreach (int eid in mis)
            {
                if (hp[eid] <= 0f) _kill_round(world, eid);
            }
        }
    }

    public static void _kill(World world, int eid)
    {
        var e = world.ecs;
        if (!e.is_alive(eid)) return;
        int etype = e.uv("etype")[eid];
        int faction = e.uv("faction")[eid];
        var pos = e.fv("pos").get3(eid);
        Defs.UNIT_DEFS.TryGetValue(etype, out var d);
        var dd = d?.death;
        var blast = dd?.blast;

        world.emit("boom", ("x", pos.x), ("y", pos.y), ("z", pos.z),
                   ("r", d?.splat ?? 2f),
                   ("big", (blast is not null || dd?.loud == "big") ? 1 : 0),
                   ("blast", blast is not null ? 1 : 0),   // the client draws a fireball for this one
                   ("faction", faction));

        int credit = e.has(eid, "last_hit") ? e.iv("last_hit")[eid] : -1;
        bool feed = _feed(world, eid, d, credit);
        _award(world, eid, credit, faction, d, feed);
        _plan_respawn(world, eid, pos);
        if (dd is not null && dd.ends_match)
        {
            int winner = faction == Defs.FACTION_RED || faction == Defs.FACTION_BLUE
                ? 1 - faction : -1;
            world.winner = winner;
            world.end_timer = 40f;
            world.emit("gameover", ("winner", winner));
        }

        _leave_wreck(world, dd?.wreck, faction, pos, e.fv("yaw")[eid]);
        e.destroy(eid);
        if (blast is not null)
        {
            // no sides spared: what stands within thirty metres of a hull that just
            // came apart is in the wrong place whatever colour it is painted
            Blasts.spawn(world, pos, (blast[0], blast[1]), friendly: true, pid: credit,
                         faction: faction);
        }
    }

    /// <summary>A missile is holed: it falls out of the sky, it does not detonate.
    ///
    /// No warhead, no wreck, no kill money, no bearing on the match.  That is
    /// deliberate: a SACLOS carries ED 80 over 6 m, so a round destroyed 300 m out
    /// that still went off would make shooting it down pointless - the point of the
    /// intercept is that the *guidance* dies with the round.  Two pilots are told
    /// the identity of the shooter and the round's owner (`pid` / `firer`) so a
    /// client can address anything it wants to them, and nothing else is owed: a
    /// battle has AI crews shooting missiles down all over the map, so the event
    /// carries no cue of its own - the round's small `boom` (emitted just below) is
    /// the whole of what anybody hears, wherever they are.</summary>
    public static void _kill_round(World world, int eid)
    {
        var e = world.ecs;
        if (!e.is_alive(eid)) return;
        int wid = e.uv("proj_wid")[eid];
        var wd = wid >= 0 && wid < Defs.WEAPONS.Length ? Defs.WEAPONS[wid] : null;
        var pos = e.fv("pos").get3(eid);
        // who killed it: `Combat.apply` credited the round that took the last point
        // off (`last_hit` is a missile's own column, -1 when an AI crew fired it)
        int pid = e.iv("last_hit")[eid];
        // who fired it: the hull behind `proj_src`.  A missile outlives the hull
        // that launched it, so a dead launcher answers -1 and its pilot is told
        // nothing - there is no ear to tell.
        int src = e.iv("proj_src")[eid];
        int firer = src >= 0 && e.is_alive(src) ? e.iv("owner")[src] : -1;
        // debug only: the small boom below is what everybody hears (the sparks the
        // client draws around it are the only other sign), and the interception
        // itself is settled - `Tools/InterceptCheck.cs` is the one reader left and
        // it raises the logger to DEBUG itself
        if (log.IsEnabled(LogLevel.Debug))
            log.LogDebug("intercept: {name} round {eid} shot down at ({x:F1}, {y:F1}, {z:F1})",
                         wd?.name ?? "?", eid, pos.x, pos.y, pos.z);
        world.emit("boom", ("x", pos.x), ("y", pos.y), ("z", pos.z), ("r", 3f),
                   ("wid", wd?.wid ?? 0), ("proj", eid), ("small", 1));
        world.emit("intercept", ("x", pos.x), ("y", pos.y), ("z", pos.z),
                   ("proj", eid), ("wid", wd?.wid ?? 0), ("pid", pid), ("firer", firer),
                   ("faction", (int)e.uv("faction")[eid]));
        e.destroy(eid);
    }

    /// <summary>Kill money, for a pilot on the other side - a hull is worth its `reward`.</summary>
    private static void _award(World world, int eid, int pid, int faction, UnitDef? d, bool feed)
    {
        if (pid < 0 || !world.players.TryGetValue(pid, out var killer)) return;
        if (killer.faction == faction
            || (faction != Defs.FACTION_RED && faction != Defs.FACTION_BLUE)) return;
        int reward = d?.reward ?? 0;
        killer.money += reward;
        // a man is not a kill - a battle kills infantry by the dozen, so counting
        // them would drown the scoreboard in the one number that is meant to be
        // about machines.  The money and the score still come in; the count does
        // not.  Being killed *by* one counts as usual, and that is `_plan_respawn`'s
        // `deaths` (a victim's own account, whoever fired the round)
        if (!world.ecs.has(eid, "person")) killer.kills += 1;
        killer.score += reward;
        int owner = world.ecs.iv("owner")[eid];
        // `feed` says whether the kill line already carries the money (`_feed`); when
        // it does not - the pilot shot an infantryman, which is not announced - the
        // client still owes him the sound of the till
        world.emit("kill", ("pid", pid), ("victim", owner >= 0 ? owner : -1),
                   ("reward", reward), ("etype", (int)world.ecs.uv("etype")[eid]),
                   ("feed", feed ? 1 : 0));
    }

    /// <summary>How the kill feed calls a hull: its name, or its type if it has none.</summary>
    private static string _named(World world, int name_id, int etype, bool bot)
    {
        string name = world.name_of(name_id);
        if (name.Length > 0) return bot ? name + "[bot]" : name;
        return Defs.UNIT_DEFS.TryGetValue(etype, out var d) ? d.name : "?";
    }

    /// <summary>Announce a kill on the wire: who killed whom, with what.
    ///
    /// A line is for what a pilot has a stake in, and nothing else.  Infantry stay
    /// off it - a battle kills them by the dozen and the line would bury everything
    /// else - and so does AI fighting AI, which is most of the war and none of a
    /// pilot's business.  All that is left is a kill with a pilot on one end of it:
    /// his own, or the one that took his hull away from him.
    ///
    /// Returns whether a line went out, which is what tells `_award` the money is
    /// already on it.</summary>
    private static bool _feed(World world, int eid, UnitDef? d, int credit)
    {
        var e = world.ecs;
        if (e.has(eid, "person") || !e.has(eid, "last_name")) return false;
        int owner = e.has(eid, "owner") ? e.iv("owner")[eid] : -1;
        if (credit < 0 && owner < 0) return false;      // two crews, no pilot anywhere in it
        int faction = e.uv("faction")[eid];
        int kfac = e.uv("last_fac")[eid];
        if ((kfac != Defs.FACTION_RED && kfac != Defs.FACTION_BLUE) || kfac == faction)
            return false;                               // uncrewed burst, or an accident inside the family
        string killer = _named(world, e.uv16("last_name")[eid], e.uv("last_etype")[eid],
                               bot: credit < 0);
        int wid = e.uv("last_wid")[eid];
        var wd = wid >= 0 && wid < Defs.WEAPONS.Length ? Defs.WEAPONS[wid] : null;
        world.emit("killfeed", ("killer", killer), ("kfac", kfac),
                   ("victim", _named(world, e.uv16("name_id")[eid], e.uv("etype")[eid],
                                     bot: owner < 0)),
                   ("vfac", faction), ("w", wd?.name ?? ""), ("pid", credit),
                   ("reward", d?.reward ?? 0));
        return true;
    }

    /// <summary>Where it died decides how long the walk back is - the mode says how long.</summary>
    private static void _plan_respawn(World world, int eid, Vec3 pos)
    {
        var e = world.ecs;
        int owner = e.iv("owner")[eid];
        if (owner < 0) return;
        if (!world.players.TryGetValue(owner, out var pl)) return;
        pl.deaths += 1;
        pl.mech = -1;
        pl.death_pos = (pos.x, pos.z);
        pl.respawn = world.mode_mod.respawn_delay(world, pl, pos.x, pos.z);
    }

    /// <summary>What the dead thing leaves behind: `(etype, seconds, wire kind)` or null.</summary>
    private static void _leave_wreck(World world, float[]? wreckspec, int faction, Vec3 pos,
                                     float yaw)
    {
        if (wreckspec is null) return;
        int dtype = (int)wreckspec[0];
        float life = wreckspec[1];
        int kind = (int)wreckspec[2];
        var e = world.ecs;
        int eid = e.create(Components.DEBRIS_COMPS);
        e.fv("pos").set3(eid, pos.x, world.terrain.height_at(pos.x, pos.z), pos.z);
        e.fv("yaw")[eid] = yaw;
        e.fv("leg_yaw")[eid] = yaw;
        e.fv("turret_yaw")[eid] = yaw;
        e.uv("etype")[eid] = (byte)dtype;
        e.uv("faction")[eid] = (byte)faction;
        e.fv("wreck_t")[eid] = life;
        e.uv("wreck_kind")[eid] = (byte)kind;
    }
}
