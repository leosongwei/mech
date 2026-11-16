// The kill line: who killed whom, with what, and from how far.
//
// Not a system - a library the death judgement calls, which is why it sits in
// `Systems/Support/` beside the other libraries a system reaches for
// (`AI/Support/`, `Movement/Support/`): `Core/Combat.cs` and `Core/Shop.cs` are
// the same kind of module one level down.  "Does this death get a line" is a
// rule about a kill, not a pass over the field, so it is asked at the one moment a
// kill exists: `Systems/Mortality.cs::_kill`, before the hull is destroyed and the
// attribution frozen on it goes with it.
//
// Everything printed here comes off that hull's `last_*` columns
// (`Combat.HitAttributions.from_victim`): the round that delivered the killing
// blow was destroyed a stage earlier, and its id can already belong to the burst
// it left.  The client paints the words (`fx/dispatch.js` -> `game.killfeed`),
// and it is the one that decides how a range is written.

using Microsoft.Extensions.Logging;

namespace Mech.Server.Systems;

public static class KillFeed
{
    // a kill line is a per-kill event, so it is DEBUG (`MECH_LOG_LEVEL=DEBUG`):
    // it is the one record of what the composed line says and where the shot was
    // measured from, which a feed hunt reads instead of the browser
    private static readonly ILogger log = MechLog.get("mech.feed");

    /// <summary>Announce a kill on the wire: who killed whom, with what, at what range.
    ///
    /// A line is for what a pilot has a stake in, and nothing else.  Infantry stay
    /// off it - a battle kills them by the dozen and the line would bury everything
    /// else - and so does AI fighting AI, which is most of the war and none of a
    /// pilot's business.  All that is left is a kill with a pilot on one end of it:
    /// his own, or the one that took his hull away from him.
    ///
    /// "A pilot on one end" is read off two columns, because the two ends are asked
    /// of different things: the victim's own `owner`, and the pilot of the round
    /// that delivered the *killing blow* (`last_pid`, -1 for a crew's round).  The
    /// pilot a hull is still paid to (`last_hit`) was the last pilot to take a point
    /// off it, which may have been minutes before a crew finished the job.
    ///
    /// `dist` is the killing blow's own range - metres from the muzzle it left to
    /// the point it went off at - and is -1 when there is no muzzle to measure
    /// from (a debug knob's raw number, a burst nobody fired).</summary>
    public static void announce(World world, int eid)
    {
        var e = world.ecs;
        if (e.has(eid, "person") || !e.has(eid, "last_name")) return;
        int owner = e.has(eid, "owner") ? e.iv("owner")[eid] : -1;
        var kill = HitAttributions.from_victim(world, eid);
        if (kill.pid < 0 && owner < 0) return;          // two crews, no pilot anywhere in it
        int faction = e.uv("faction")[eid];
        // `by` is null for an uncrewed burst (nobody to name) and for a side that
        // is neither red nor blue; the second half here is an accident inside the
        // family, which is nobody's kill either
        if (kill.by is null) return;
        var (kname, kfac, ketype) = kill.by.Value;
        if (kfac == faction) return;
        int wid = kill.wid;
        var wd = wid >= 0 && wid < Defs.WEAPONS.Length ? Defs.WEAPONS[wid] : null;
        string killer = _named(world, kname, ketype, bot: kill.pid < 0);
        string victim = _named(world, e.uv16("name_id")[eid], e.uv("etype")[eid],
                               bot: owner < 0);
        // whole metres: the client writes them as m or km, and a kill line is read
        // at a glance, not measured
        int range = kill.range >= 0f ? (int)MathF.Round(kill.range) : -1;
        if (log.IsEnabled(LogLevel.Debug))
            log.LogDebug("killfeed: {killer} killed {victim} with {w} from {range} m",
                         killer, victim, wd?.name ?? "?", range);
        world.emit("killfeed", ("killer", killer), ("kfac", kfac), ("victim", victim),
                   ("vfac", faction), ("w", wd?.name ?? ""), ("dist", range));
    }

    /// <summary>How the kill feed calls a hull: its name, or its type if it has none.</summary>
    private static string _named(World world, int name_id, int etype, bool bot)
    {
        string name = world.name_of(name_id);
        if (name.Length > 0) return bot ? name + "[bot]" : name;
        return Defs.UNIT_DEFS.TryGetValue(etype, out var d) ? d.name : "?";
    }

    /// <summary>One kill as it happened, for a reader outside the HUD: the plain
    /// names (no `[bot]` - `is_player` says which end was a pilot), the weapon's
    /// own name, and the shot's range in metres (-1 when there is no muzzle to
    /// measure from).  `Net/KillStream.cs` writes it out on `GET /events/kills`.</summary>
    public readonly record struct KillRecord(string victim, bool victim_is_player,
                                             string killer, bool killer_is_player,
                                             string weapon, float distance);

    /// <summary>Where every kill is also published - the server's stream.  Assigned
    /// once at startup; null in a harness and in a run with no listener, where a
    /// kill costs one null check.</summary>
    public static Action<KillRecord>? on_kill;

    /// <summary>Hand one kill to the stream.
    ///
    /// The set is the HUD's own - **a pilot on one end of it** - because a crew
    /// finishing a crew is most of a 16 km war and none of anybody's business;
    /// `is_player` on both ends would read `false` anyway.  It is wider in exactly
    /// one place: a rifleman's death is published when a pilot caused it or
    /// suffered it, where the message stack keeps infantry off the screen because a
    /// battle kills them by the dozen.</summary>
    public static void publish(World world, int eid)
    {
        var h = on_kill;
        if (h is null) return;
        var e = world.ecs;
        bool victim_is_player = e.iv("owner")[eid] >= 0;
        var kill = HitAttributions.from_victim(world, eid);
        if (kill.pid < 0 && !victim_is_player) return;      // two crews, no pilot anywhere
        if (kill.by is null) return;
        var (kname, kfac, ketype) = kill.by.Value;
        if (kfac == e.uv("faction")[eid]) return;
        int wid = kill.wid;
        var wd = wid >= 0 && wid < Defs.WEAPONS.Length ? Defs.WEAPONS[wid] : null;
        h(new KillRecord(_named(world, e.uv16("name_id")[eid], e.uv("etype")[eid], bot: false),
                         victim_is_player,
                         _named(world, kname, ketype, bot: false),
                         kill.pid >= 0,
                         wd?.name ?? "",
                         kill.range));
    }
}
