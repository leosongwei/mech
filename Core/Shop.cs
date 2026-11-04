// The rearm shop: one quote, one deal.
//
// Ported from `backend/server/shop.py`.  A player at a base or an owned town
// opens the panel (B) and edits a draft loadout; nothing happens until it is
// confirmed, at which point the whole draft is priced and either bought or
// refused as a unit.  Both sides of a trade are priced from the same tables in
// `Defs`:
//
//   hardware  `WeaponDef.cost`
//   rounds    `WeaponDef.ammo_cost` per round, a full load is `mag + reserve`
//   selling back pays `SELL_REFUND` of the hardware, scaled by the fraction of
//             that arm still standing (an arm shot down to half is worth half
//             as much), plus half of the rounds still in it
//   welding hp back on costs `REPAIR_COST_PER_HP` per point, over all modules
//
// The client runs the same arithmetic for the live preview (`frontend/src/
// shop.js`), so the numbers here have to stay in step with it.
//
// A deal is not applied on the spot: the lines are booked on the pilot as a
// work order (`Player.work`) and `Systems/Rearm.cs` does the job over the
// seconds the draft cost, with the mech held still and its triggers dead.
// Money is charged when the job is booked.

namespace Mech.Server;

public static class Shop
{
    public static readonly string[] ARM_NAMES = { "L-ARM", "R-ARM" };

    /// <summary>Round money the way the browser's `Math.round` does, so a
    /// preview and the bill never differ by a dollar.</summary>
    public static int _r(float v) => (int)MathF.Floor(v + 0.5f);

    /// <summary>Seconds of welding for `need` points of missing armour.</summary>
    public static float repair_time(float need) =>
        MathF.Max(0f, need) * Defs.REARM_REPAIR_PER_100HP_S / 100f;

    /// <summary>What the progress bar calls the job: the operations it is made of.</summary>
    public static string work_label(List<Dictionary<string, object?>> lines)
    {
        var kinds = new HashSet<string>();
        foreach (var line in lines)
            if (line.TryGetValue("kind", out var k) && k is string s) kinds.Add(s);
        var parts = new List<string>(4);
        foreach (var (kind, name) in new (string, string)[]
                 {
                     ("swap", "WEAPON CHANGE"), ("rearm", "REARM"),
                     ("repair", "REPAIR"), ("smoke", "SMOKE"),
                 })
            if (kinds.Contains(kind)) parts.Add(name);
        return parts.Count > 0 ? string.Join(" + ", parts) : "REARM";
    }

    public static Dictionary<string, object?> empty_request() => new()
    {
        ["arms"] = new List<object?> { -1, -1 },
        ["rearm"] = new List<object?> { false, false },
        ["repair"] = false,
        ["smoke"] = false,
    };

    /// <summary>Take the client's draft and keep only what it is allowed to ask for.</summary>
    public static Dictionary<string, object?> parse_request(Dictionary<string, object?> msg)
    {
        var req = empty_request();
        var arms = req["arms"] as List<object?>;
        if (msg.TryGetValue("arms", out var raw_arms) && raw_arms is List<object?> given)
            for (int arm = 0; arm < 2 && arm < given.Count; arm++)
            {
                int wid = _to_int(given[arm], int.MinValue);
                if (wid == int.MinValue) continue;      // `except (TypeError, ValueError)`
                if (Array.IndexOf(Defs.BUYABLE, wid) >= 0) arms![arm] = wid;
            }
        var rearm = req["rearm"] as List<object?>;
        if (msg.TryGetValue("rearm", out var raw_rearm) && raw_rearm is List<object?> flags)
            for (int arm = 0; arm < 2; arm++)
                rearm![arm] = arm < flags.Count && _truthy(flags[arm]);
        req["repair"] = _truthy(msg.GetValueOrDefault("repair"));
        req["smoke"] = _truthy(msg.GetValueOrDefault("smoke"));
        return req;
    }

    /// <summary>What one arm holds right now: hardware, rounds and how much of it is left.</summary>
    public static Dictionary<string, object?> arm_state(World world, Player pl, int arm)
    {
        var e = world.ecs;
        int wid = pl.loadout[arm];
        var wd = wid >= 0 && wid < Defs.WEAPONS.Length
            ? Defs.WEAPONS[wid] : Defs.WEAPONS[Defs.WID_NONE];
        int mid = pl.mech;
        float hp, hpmax;
        int mag, reserve;
        if (e.is_alive(mid))
        {
            // the arm's own pool (`arm_hp`, left 0 / right 1) - a hull without
            // arms has no such component and its arms never fall off
            if (e.has(mid, "arm_hp"))
            {
                hp = e.fv("arm_hp").get(mid, arm);
                hpmax = MathF.Max(1f, e.fv("arm_hpmax").get(mid, arm));
            }
            else hp = hpmax = 1f;
            mag = e.iv("mag").get(mid, arm);
            reserve = e.iv("ammo").get(mid, arm);
        }
        else
        {
            hp = hpmax = 1f;
            mag = reserve = 0;
        }
        return new Dictionary<string, object?>
        {
            ["arm"] = arm, ["wid"] = wid, ["wd"] = wd, ["hp"] = hp, ["hpmax"] = hpmax,
            ["mag"] = mag, ["reserve"] = reserve, ["rounds"] = mag + reserve,
        };
    }

    /// <summary>Price a whole draft.  `delta` per line is what it adds to the
    /// bill, `dur` how many seconds of work it books (they add up: a weapon
    /// change and the racks it brings are two jobs, done one after the other).</summary>
    public static Dictionary<string, object?> quote(World world, Player pl,
                                                    Dictionary<string, object?> req)
    {
        var e = world.ecs;
        int mid = pl.mech;
        if (!e.is_alive(mid))
            return _quote(false, "NO MECH", new List<object?>(), 0, 0f);

        var lines = new List<object?>();
        float total = 0f;
        float dur = 0f;

        // hardware: swap one arm for another
        for (int arm = 0; arm < 2; arm++)
        {
            var cur = arm_state(world, pl, arm);
            int wid = _req_arm(req, arm);
            int cur_wid = (int)cur["wid"]!;
            if (wid < 0 || wid == cur_wid || wid >= Defs.WEAPONS.Length) continue;
            var cur_wd = (WeaponDef)cur["wd"]!;
            var nwd = Defs.WEAPONS[wid];
            float cur_hp = (float)cur["hp"]!;
            float cur_hpmax = (float)cur["hpmax"]!;
            int cur_rounds = (int)cur["rounds"]!;
            int hardware = _r(cur_wd.cost * Defs.SELL_REFUND * cur_hp / cur_hpmax);
            int rounds = _r(cur_rounds * cur_wd.ammo_cost * Defs.SELL_REFUND);
            int load = _r((nwd.mag + nwd.reserve) * nwd.ammo_cost);
            int delta = nwd.cost + load - hardware - rounds;
            // the weapon comes out, the new one goes in, and its racks are
            // loaded: a swap is a fit *and* a rearm
            float t = Defs.REARM_SWAP_S + Defs.REARM_AMMO_S;
            total += delta;
            dur += t;
            lines.Add(new Dictionary<string, object?>
            {
                ["kind"] = "swap", ["arm"] = arm, ["wid"] = wid, ["delta"] = delta,
                ["dur"] = t, ["hp"] = MathF.Round(cur_hp / cur_hpmax, 2),
                ["trade_in"] = hardware + rounds,
                ["text"] = $"{ARM_NAMES[arm]}: {cur_wd.name} ({cur_rounds}) -> {nwd.name}",
            });
        }

        // rounds: top the current arm back up to a full load
        for (int arm = 0; arm < 2; arm++)
        {
            if (_req_arm(req, arm) >= 0) continue;      // a swap already brings full racks
            if (!_req_rearm(req, arm)) continue;
            var cur = arm_state(world, pl, arm);
            var cur_wd = (WeaponDef)cur["wd"]!;
            int full = cur_wd.mag + cur_wd.reserve;
            int missing = full - (int)cur["rounds"]!;
            if (missing <= 0) continue;
            int delta = _r(missing * cur_wd.ammo_cost);
            float t = Defs.REARM_AMMO_S;
            total += delta;
            dur += t;
            lines.Add(new Dictionary<string, object?>
            {
                ["kind"] = "rearm", ["arm"] = arm, ["wid"] = cur["wid"], ["delta"] = delta,
                ["dur"] = t,
                ["text"] = $"{ARM_NAMES[arm]}: {cur_wd.name} REARM {missing} rds",
            });
        }

        // armour: weld every module back to full
        if (_req_flag(req, "repair"))
        {
            float need = MathF.Max(0f, e.fv("hpmax")[mid] - e.fv("hp")[mid]);
            if (e.has(mid, "arm_hp"))
                for (int a = 0; a < 2; a++)
                    need += MathF.Max(0f, e.fv("arm_hpmax").get(mid, a) - e.fv("arm_hp").get(mid, a));
            int cost = _r(need * Defs.REPAIR_COST_PER_HP);
            if (cost > 0)
            {
                float t = repair_time(need);
                total += cost;
                dur += t;
                lines.Add(new Dictionary<string, object?>
                {
                    ["kind"] = "repair", ["delta"] = cost, ["dur"] = t,
                    ["text"] = $"REPAIR {(int)MathF.Round(need)} hp",
                });
            }
        }

<<<<<<< HEAD
        // smoke: top the rack back up, one canister at a time
        if (_req_flag(req, "smoke"))
        {
            int missing = Defs.SMOKE_CHARGES - pl.smoke_charges;
            if (missing > 0)
            {
                int cost = missing * Defs.SMOKE_LOAD_COST;
                float t = Defs.SMOKE_LOAD_S;
                total += cost;
                dur += t;
                lines.Add(new Dictionary<string, object?>
                {
                    ["kind"] = "smoke", ["delta"] = cost, ["dur"] = t,
                    ["text"] = $"REARM SMOKE {missing}/{Defs.SMOKE_CHARGES}",
                });
            }
=======
        // smoke: a fresh rack, full and off cooldown
        if (_req_flag(req, "smoke")
            && (pl.smoke_charges < Defs.SMOKE_REFILL_CHARGES || pl.smoke_cd > 0f))
        {
            int cost = Defs.SMOKE_REFILL_COST;
            total += cost;
            lines.Add(new Dictionary<string, object?>
            {
                ["kind"] = "smoke", ["delta"] = cost, ["dur"] = 0f,
                ["text"] = $"REARM SMOKE x{Defs.SMOKE_REFILL_CHARGES}",
            });
>>>>>>> 43dd4b6 (Replace the Python backend with the C# one, move the repo to a single root layout)
        }

        if (lines.Count == 0) return _quote(false, "NOTHING TO DO", lines, 0, 0f);
        if (pl.work is not null)
            return _quote(false, "ALREADY REARMING", lines, (int)total, dur);
        if (total > _r(pl.money))
            return _quote(false, "NOT ENOUGH MONEY", lines, (int)total, dur);
        return _quote(true, "", lines, (int)total, MathF.Round(dur, 2));
    }

    /// <summary>Carry out one priced line on the mech that is standing in the bay.</summary>
    public static void apply_line(World world, Player pl, Dictionary<string, object?> line)
    {
        var e = world.ecs;
        int mid = pl.mech;
        var kind = (string)line["kind"]!;
        switch (kind)
        {
            case "swap":
            {
                int arm = (int)line["arm"]!;
                int wid = (int)line["wid"]!;
                var wd = Defs.WEAPONS[wid];
                pl.loadout[arm] = wid;
                e.uv("weapon").set(mid, arm, (byte)wid);
                e.iv("mag").set(mid, arm, wd.mag);
                e.iv("ammo").set(mid, arm, wd.reserve);
                e.fv("reload_t").set(mid, arm, 0f);
                e.fv("cool").set(mid, arm, 0f);
                break;
            }
            case "rearm":
            {
                int arm = (int)line["arm"]!;
                var wd = Defs.WEAPONS[e.uv("weapon").get(mid, arm)];
                e.iv("mag").set(mid, arm, wd.mag);
                e.iv("ammo").set(mid, arm, wd.reserve);
                e.fv("reload_t").set(mid, arm, 0f);
                break;
            }
            case "repair":
                e.fv("hp")[mid] = e.fv("hpmax")[mid];
                if (e.has(mid, "arm_hp"))
                    for (int a = 0; a < 2; a++)
                        e.fv("arm_hp").set(mid, a, e.fv("arm_hpmax").get(mid, a));
                break;
            case "smoke":
<<<<<<< HEAD
                pl.smoke_charges = Defs.SMOKE_CHARGES;
=======
                pl.smoke_charges = Defs.SMOKE_REFILL_CHARGES;
>>>>>>> 43dd4b6 (Replace the Python backend with the C# one, move the repo to a single root layout)
                pl.smoke_cd = 0f;
                break;
        }
    }

    /// <summary>Quote the draft, charge it, and book the work it costs.
    ///
<<<<<<< HEAD
    /// Every line takes seconds - a fit 2 s, a load of rounds 4 s, a smoke rack
    /// 2 s, armour 2 s per 100 hp - so nothing lands on the mech here:
    /// `Systems/Rearm.cs` applies the lines when the job has run its course.</summary>
=======
    /// The lines are applied by `Systems/Rearm.cs` when the job has run its
    /// seconds - nothing lands on the mech here, except a job with no duration
    /// at all (a smoke rack), which is done on the spot.</summary>
>>>>>>> 43dd4b6 (Replace the Python backend with the C# one, move the repo to a single root layout)
    public static (bool ok, string note, Dictionary<string, object?> q) deal(
        World world, Player pl, Dictionary<string, object?> req)
    {
        var q = quote(world, pl, req);
        if (!(bool)q["ok"]!) return (false, (string)q["error"]!, q);
        int total = (int)q["total"]!;
        pl.money -= total;
        int spent = total;
        float dur = (float)q["dur"]!;
        var lines = (List<object?>)q["lines"]!;
        if (dur <= 0f)
        {
            foreach (var line in lines) apply_line(world, pl, (Dictionary<string, object?>)line!);
            var note = spent > 0 ? $"DEAL DONE -${spent}" : $"DEAL DONE +${-spent}";
            return (true, note, q);
        }
        // the seconds go on the mech: `Systems/Rearm.cs` counts them down, holds
        // the machine still for as long as they last and applies the lines at zero
        pl.work = new List<Dictionary<string, object?>>(lines.Count);
        foreach (var line in lines) pl.work.Add((Dictionary<string, object?>)line!);
        pl.work_total = dur;
        world.ecs.fv("work_remaining_time")[pl.mech] = dur;
        return (true, $"REARM STARTED -${spent} / {dur:0.0}s", q);
    }

    // ------------------------------------------------------------------
    // helpers
    // ------------------------------------------------------------------
    private static Dictionary<string, object?> _quote(bool ok, string error,
        List<object?> lines, int total, float dur) => new()
    {
        ["ok"] = ok, ["error"] = error, ["lines"] = lines, ["total"] = total, ["dur"] = dur,
    };

    private static int _req_arm(Dictionary<string, object?> req, int arm)
    {
        if (req.TryGetValue("arms", out var a) && a is List<object?> l && arm < l.Count)
            return _to_int(l[arm], -1);
        return -1;
    }

    private static bool _req_rearm(Dictionary<string, object?> req, int arm)
    {
        if (req.TryGetValue("rearm", out var a) && a is List<object?> l && arm < l.Count)
            return _truthy(l[arm]);
        return false;
    }

    private static bool _req_flag(Dictionary<string, object?> req, string key) =>
        _truthy(req.GetValueOrDefault(key));

    /// <summary>`int(v)`: the client sends JSON numbers, i.e. a `long` for a
    /// whole one and a `float`/`double` for a fractional one here.</summary>
    public static int _to_int(object? v, int dflt = 0) => v switch
    {
        int i => i,
        long l => (int)l,
        short s => s,
        byte b => b,
        float f => (int)f,
        double d => (int)d,
        bool b => b ? 1 : 0,
        string s => int.TryParse(s, out int p) ? p : dflt,
        _ => dflt,
    };

    private static bool _truthy(object? v) => v switch
    {
        null => false,
        bool b => b,
        int i => i != 0,
        long l => l != 0,
        float f => f != 0f,
        double d => d != 0d,
        string s => s.Length > 0,
        _ => true,
    };
}
