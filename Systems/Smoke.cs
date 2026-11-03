// Hot smoke: a fan of canisters thrown out of the hull, then a wall of smoke.
//
// A pull (`Q`) throws `Defs.SMOKE_FAN` canisters out of the hull in a fan that
// closes the front hemisphere.  Each one flies roughly `SMOKE_RANGE` metres and
// pops into one big puff that stays up for `SMOKE_LIFE` seconds.  No two of them
// are identical: the tube velocity jitters the flight time by
// `SMOKE_TIME_JITTER` and every charge is filled `SMOKE_SIZE_JITTER` off the
// nominal puff, so the wall is ragged instead of a perfect lattice.
//
// The smoke is a set of ordinary entities - `pos` + `smoke` + `smoke_r` +
// `smoke_t` - and *only* that: no `unit`, no `faction`, no `etype`.  Every
// system that cares about units asks for the `unit` / `combat` tag, so a puff is
// invisible to weapons, AI targeting, town capture and projectile collision
// without a single exclusion rule.  What it does affect is anything that *sees*,
// which asks `blocks` through `Systems/Sight.cs` - the IR seeker's lock, the
// AI's lookout and a missile's seeker all lose a hull behind a screen exactly as
// they lose one behind a stand of trees.
//
// Ported from `backend/server/systems/smoke.py`.

namespace Mech.Server.Systems;

public static class Smoke
{
    public static void run(World world, float dt)
    {
        _launch(world, dt);
        _fly(world, dt);
        _fade(world, dt);
    }

    /// <summary>Turn a pilot's Q press into a fan of canisters.</summary>
    private static void _launch(World world, float dt)
    {
        var e = world.ecs;
        foreach (var pl in world.players.Values)
        {
            pl.smoke_cd = MathF.Max(0f, pl.smoke_cd - dt);
            bool fired = pl.smoke_q;
            pl.smoke_q = false;
            int eid = pl.mech;
            if (!fired || eid < 0 || !e.is_alive(eid)) continue;
            if (pl.smoke_charges <= 0 || pl.smoke_cd > 0f) continue;
            if (!e.has(eid, "unit")) continue;
            pl.smoke_charges -= 1;
            float? cd = world.mode_mod.smoke_cooldown;
            pl.smoke_cd = cd is null ? Defs.SMOKE_COOLDOWN : cd.Value;
            @throw(world, eid);
            world.emit("smoke", ("eid", eid), ("charges", pl.smoke_charges));
        }
    }

    /// <summary>Spawn the canister fan of the hull `eid`.
    ///
    /// `throw` is a C# keyword, so the method is written `@throw` - the Python
    /// name, the way `WeaponDef.@lock` keeps its own.</summary>
    public static void @throw(World world, int eid)
    {
        var e = world.ecs;
        var p = e.fv("pos").get3(eid);
        float yaw = e.fv("yaw")[eid];
        float launcher_h = Defs.UNIT_DEFS[e.uv("etype")[eid]].height * 0.55f;
        var pos = e.fv("pos");
        var vel = e.fv("vel");
        var smoke_t = e.fv("smoke_t");
        var smoke_r = e.fv("smoke_r");
        var smoke_s = e.fv("smoke_s");
        foreach (var (az, el) in Defs.SMOKE_FAN)
        {
            float a = yaw + az * MathF.PI / 180f;
            float pt = el * MathF.PI / 180f;
            float cp = MathF.Cos(pt);
            float dx = cp * MathF.Cos(a), dy = MathF.Sin(pt), dz = cp * MathF.Sin(a);
            // No two canisters of a pull are identical: the tube velocity jitters
            // (+-SMOKE_TIME_JITTER of the flight time, so the pop distance follows)
            // and every charge is filled slightly differently (+-SMOKE_SIZE_JITTER).
            float speed = Defs.SMOKE_SPEED * world.rng.uniform(1f - Defs.SMOKE_TIME_JITTER,
                                                              1f + Defs.SMOKE_TIME_JITTER);
            float scale = world.rng.uniform(1f - Defs.SMOKE_SIZE_JITTER,
                                            1f + Defs.SMOKE_SIZE_JITTER);
            int cid = e.create(Components.CANISTER_COMPS);
            pos.set3(cid, p.x, p.y + launcher_h, p.z);
            vel.set3(cid, dx * speed, dy * speed, dz * speed);
            smoke_t[cid] = Defs.SMOKE_RANGE / speed;     // time to pop
            smoke_r.set3(cid, _radii(0f, scale));
            smoke_s[cid] = scale;
        }
    }

    /// <summary>Puff semiaxes `age` seconds after the pop, `scale` times a full puff.
    ///
    /// A canister is a small charge: it bursts the smoke out fast and the puff
    /// then keeps creeping for the rest of its life, so the growth is an ease-out
    /// towards `SMOKE_FLAT` rather than a constant spread.</summary>
    public static Vec3 _radii(float age, float scale = 1f)
    {
        float k = 1f - MathF.Exp(-Defs.SMOKE_BURST * age);
        float f = (Defs.SMOKE_START + (1f - Defs.SMOKE_START) * k) * scale;
        return new Vec3(Defs.SMOKE_FLAT[0] * f, Defs.SMOKE_FLAT[1] * f, Defs.SMOKE_FLAT[0] * f);
    }

    /// <summary>Canisters: a short ballistic hop, then pop into smoke.</summary>
    private static void _fly(World world, float dt)
    {
        var e = world.ecs;
        var ids = e.query("canister");
        if (ids.Length == 0) return;
        var pos = e.fv("pos");
        var vel = e.fv("vel");
        var t = e.fv("smoke_t");
        foreach (int cid in ids)
        {
            t[cid] = t[cid] - dt;
            if (t[cid] <= 0f)
            {
                _pop(world, cid);
                continue;
            }
            vel.set(cid, 1, vel.get(cid, 1) - 9.81f * dt);
            pos.set(cid, 0, pos.get(cid, 0) + vel.get(cid, 0) * dt);
            pos.set(cid, 1, pos.get(cid, 1) + vel.get(cid, 1) * dt);
            pos.set(cid, 2, pos.get(cid, 2) + vel.get(cid, 2) * dt);
        }
    }

    /// <summary>The canister stops where it is and starts making smoke.
    ///
    /// A deployed cloud is not a canister with different tags: it is a different
    /// thing, so the charge is destroyed and the puff is spawned where it burst.
    /// The component set is the entity's type and it is chosen once, at spawn -
    /// `Components.PUFF_COMPS` for a cloud, `CANISTER_COMPS` for the charge.  This
    /// is the two-archetype transition: destroy + create, never a component add.</summary>
    private static void _pop(World world, int cid)
    {
        var e = world.ecs;
        float scale = e.fv("smoke_s")[cid];
        var cp = e.fv("pos").get3(cid);
        // the burst stays where the canister ran out of fuse, underground
        // included: what is under the terrain is hidden anyway, and the puff grows
        // into it, so the screen of a low row is a dome standing on the deck rather
        // than a ball hanging over it with a gap underneath
        e.destroy(cid);
        int pid = e.create(Components.PUFF_COMPS);
        e.fv("pos").set3(pid, cp);
        e.fv("smoke_t")[pid] = Defs.SMOKE_LIFE;
        e.fv("smoke_r").set3(pid, _radii(0f, scale));
        e.fv("smoke_s")[pid] = scale;
    }

    /// <summary>Deployed puffs burst open, creep for a while, then go away.</summary>
    private static void _fade(World world, float dt)
    {
        var e = world.ecs;
        var ids = e.query("smoke");
        if (ids.Length == 0) return;
        var t = e.fv("smoke_t");
        var rad = e.fv("smoke_r");
        var scl = e.fv("smoke_s");
        foreach (int sid in ids)
        {
            t[sid] = t[sid] - dt;
            if (t[sid] <= 0f)
            {
                e.destroy(sid);
                continue;
            }
            rad.set3(sid, _radii(Defs.SMOKE_LIFE - t[sid], scl[sid]));
        }
    }

    /// <summary>True when the segment `a`->`b` passes through any deployed puff.
    ///
    /// The puff is an ellipsoid, so the segment is scaled into the unit sphere of
    /// each puff and tested there: one dot product and a closest point.  This is
    /// the door `Sight.why` calls, so every eye in the game inherits it.</summary>
    public static bool blocks(World world, Vec3 a, Vec3 b)
    {
        var e = world.ecs;
        var ids = e.query("smoke");
        if (ids.Length == 0) return false;
        var pos = e.fv("pos");
        var rad = e.fv("smoke_r");
        foreach (int sid in ids)
        {
            var r = rad.get3(sid);
            if (r.x <= 0f || r.y <= 0f || r.z <= 0f) continue;
            var c = pos.get3(sid);
            float pax = (a.x - c.x) / r.x;
            float pay = (a.y - c.y) / r.y;
            float paz = (a.z - c.z) / r.z;
            float dx = (b.x - c.x) / r.x - pax;
            float dy = (b.y - c.y) / r.y - pay;
            float dz = (b.z - c.z) / r.z - paz;
            float L = dx * dx + dy * dy + dz * dz;
            float u = L < 1e-9f ? 0f : -(pax * dx + pay * dy + paz * dz) / L;
            u = u < 0f ? 0f : (u > 1f ? 1f : u);
            float qx = pax + dx * u;
            float qy = pay + dy * u;
            float qz = paz + dz * u;
            if (qx * qx + qy * qy + qz * qz <= 1f) return true;
        }
        return false;
    }
}
