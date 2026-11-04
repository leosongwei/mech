// Projectile integration, guidance, collision and damage application.
//
// `run` is three passes over the rounds because the expensive half can be done in
// bulk:
//
//   1. fly every round, remembering the leg (`proj_prev` -> `pos`) it covered;
//   2. ask the terrain once for every leg (`Terrain.ground_hits_many`) - a
//      hundred short legs make one walk instead of a hundred;
//   3. resolve what each leg ran into, one round at a time, because that is
//      the pass with the side effects (a burst per impact, damage, a wreck).
//
// `sphere_table` is the tick's shared copy of every hit sphere on the field.
// `prev`/`disp` are the reason it exists in this shape: a missile covers 20 m in
// a tick against a 2.5 m fuse and an 0.08 m ball, so solving a hit against a
// *standing* target would be decided by the phase of the clock.  The sweep solves
// the relative leg instead, and a unit's `pos` is the end of its tick while a
// missile's is the start - which is why the two blocks are gathered at different
// moments and each says so.
//
// Ported from `backend/server/systems/projectiles.py`.

namespace Mech.Server.Systems;

public static class Projectiles
{
    // collision sphere of each weapon's rounds, by `wid`: the half-calibre of a
    // missile (`Defs.WEAPONS[...].hit_r`) and 0 for anything a bullet is - only an
    // `interceptable` round ever has a row in the table, so the zero never gets read
    public static readonly float[] HIT_R = Defs.WEAPONS.Select(w => w.hit_r).ToArray();

    private static (Vec3 dir, float len) _norm(Vec3 v)
    {
        float n = MathF.Sqrt(v.x * v.x + v.y * v.y + v.z * v.z);
        if (n < 1e-6f) return (new Vec3(1f, 0f, 0f), 0f);
        return (new Vec3(v.x / n, v.y / n, v.z / n), n);
    }

    /// <summary>Turn a velocity towards `desired` at no more than `turn_rate`,
    /// keeping `speed`.</summary>
    private static Vec3 _steer(Vec3 v, Vec3 desired, float speed, float turn_rate, float dt)
    {
        var (cur, _) = _norm(v);
        var (dn, want) = _norm(desired);
        if (want < 1e-3f)                       // already on the sight line
            return cur * speed;
        float dot = MathF.Max(-1f, MathF.Min(1f, cur.dot(dn)));
        float ang = MathF.Acos(dot);
        Vec3 nd;
        if (ang < 1e-4f)
        {
            nd = dn;
        }
        else
        {
            float f = MathF.Min(1f, turn_rate * dt / ang);
            (nd, _) = _norm(cur + (dn - cur) * f);
        }
        return nd * speed;
    }

    public static void run(World world, float dt)
    {
        var e = world.ecs;
        var pids = world.projectiles();
        if (pids.Length == 0) return;
        var terrain = world.terrain;

        // ---- hit spheres of every unit and missile, gathered once a tick ----
        var sph = world.sphere_table(dt);
        // a target that moves during the tick can walk into a leg it was never in
        // at either end of: the broad-phase box of every round grows by the fastest
        // thing on the field (a missile covers 20 m of ground a tick)
        float reach = 0f;
        for (int k = 0; k < sph.n * 3; k++)
        {
            float d = MathF.Abs(sph.disp[k]);
            if (d > reach) reach = d;
        }

        // ---- pass one: fly every round, remember the leg it covered -------
        int n_round = pids.Length;
        var legs0 = new float[n_round * 3];
        var legs1 = new float[n_round * 3];
        var flying = new bool[n_round];
        // the legs that actually flew, packed for the one bulk terrain call
        var fly_i = new int[n_round];
        var fly0 = new float[n_round * 3];
        var fly1 = new float[n_round * 3];
        int n_fly = 0;

        for (int i = 0; i < n_round; i++)
        {
            int pid = pids[i];
            if (!e.is_alive(pid)) continue;
            var slot = e.slot(pid);
            if (!slot.ok) continue;
            var a = slot.a;
            int row = slot.row;
            var wd = Defs.WEAPONS[a.u8("proj_wid")[row]];
            int k = a.u8("proj_kind")[row];
            var age = a.f("proj_age");
            var life = a.f("proj_life");
            age[row] += dt;
            life[row] -= dt;
            if (life[row] <= 0f)
            {
                if (k == 3)
                {
                    // a top-attack round's flight ends with its battery, and it ends
                    // it the way a warhead ends: the charge goes off where the round
                    // happens to be, no direct hit (a SACLOS running out of wire
                    // cooks off the same way, further down)
                    _impact(world, a.get_f3("pos", row), wd, pid);
                }
                e.destroy(pid);
                continue;
            }

            var posb = a.f("pos");
            var velb = a.f("vel");
            var prevb = a.f("proj_prev");
            var travb = a.f("proj_trav");
            float p0x = posb[row * 3], p0y = posb[row * 3 + 1], p0z = posb[row * 3 + 2];
            // the leg the prox fuse solves
            prevb[row * 3] = p0x;
            prevb[row * 3 + 1] = p0y;
            prevb[row * 3 + 2] = p0z;
            float vx = velb[row * 3], vy = velb[row * 3 + 1], vz = velb[row * 3 + 2];
            var (sp, _) = _norm(new Vec3(vx, vy, vz));
            float speed = MathF.Sqrt(vx * vx + vy * vy + vz * vz);

            if (k == 0)                                 // ballistic bullet
            {
                vy -= wd.g * dt;
            }
            else if (k == 1)                            // unguided rocket
            {
                speed = MathF.Min(wd.maxvel, speed + wd.thrust * dt);
                vx = sp.x * speed; vy = sp.y * speed; vz = sp.z * speed;
                vy -= wd.g * dt;
            }
            else if (k == 2)                            // SACLOS, rides the sight line
            {
                int shooter = a.i("proj_src")[row];
                bool has_aim = false;
                var d = Vec3.zero;
                float ax = 0f, ay = 0f, az = 0f;
                if (shooter >= 0 && e.is_alive(shooter)
                    && e.uv("etype")[shooter] != Defs.ETYPE_BASE)
                {
                    var (origin, sy, spitch) = Weapons.aim_of(world, shooter);
                    d = MathUtil.dir_from(sy, spitch);
                    float relx = p0x - origin.x, rely = p0y - origin.y, relz = p0z - origin.z;
                    float along = relx * d.x + rely * d.y + relz * d.z;
                    // steer for a point well *ahead* on the sight line: the foot of
                    // the perpendicular sits 90 deg off the round's heading, so
                    // aiming there commands a full-rate turn for any lateral error
                    // and the round slithers across the beam instead of settling.
                    float lead = MathF.Max(0f, along) + MathF.Max(90f, speed * 1.2f);
                    ax = origin.x + d.x * lead;
                    ay = origin.y + d.y * lead;
                    az = origin.z + d.z * lead;
                    has_aim = true;
                }
                // the motor has a burn time, then it is out and the airframe coasts:
                // quadratic drag bleeds the cruise speed back down for the rest of
                // the flight, so a long shot arrives slow (`burn` / `drag`)
                if (wd.burn <= 0f || age[row] < wd.burn)
                    speed = MathF.Min(wd.maxvel, speed + wd.thrust * dt);
                else
                    speed = MathF.Max(0f, speed - wd.drag * speed * speed * dt);
                if (has_aim)
                {
                    var v = _steer(new Vec3(vx, vy, vz),
                                   new Vec3(ax - p0x, ay - p0y, az - p0z), speed, 1.35f, dt);
                    vx = v.x; vy = v.y; vz = v.z;
                    // thrust misalignment / crosswind: a slow push square to the
                    // sight line, seeded per round, that the guidance has to chase
                    // down.  The round wobbles on the way out instead of cutting a
                    // perfect line; the amplitude stays bounded because the beam
                    // always wins - spread alone would just be noise
                    float wob = wd.wob;
                    float side = MathF.Sqrt(d.x * d.x + d.z * d.z);
                    if (wob != 0f && side > 1e-3f)
                    {
                        float s = a.f("proj_seed")[row];            // random per round
                        float f = wd.wob_f * (0.75f + 0.5f * s);
                        float ph = s * MathUtil.TAU;
                        float am = wob * (0.7f + 0.6f * ((s * 3.7f) % 1f))
                                   * (MathF.Sin(age[row] * f * 2.1f + ph)
                                      + 0.55f * MathF.Sin(age[row] * f * 5.3f + ph * 2.3f));
                        vx -= d.z / side * am * dt;
                        vz += d.x / side * am * dt;
                    }
                }
                else
                {
                    vx = sp.x * speed; vy = sp.y * speed; vz = sp.z * speed;
                }
            }
            else if (k == 3)                            // top attack IR missile
            {
                // the seeker (`Core/Seeker.cs`) owns the lock: it refreshes the
                // stare point off the live hull while it can see it, keeps the
                // memory walking at the hull's last velocity when it cannot, and
                // takes any enemy hull it finds inside its cone.  What comes back is
                // the point to dive on - the airframe below cannot tell a live hull
                // from a memory, and does not need to.
                var spot = Seeker.track(world, pid,
                                        new Vec3(p0x, p0y, p0z), dt);
                // the point is guided on even when the hull it came from is gone;
                // a round that never had a lock gets nothing and is a rock
                Vec3? tvel = null;
                if (spot is not null) tvel = e.fv("tgt_vel").get3(pid);
                var v = Flight.step(new Vec3(p0x, p0y, p0z), new Vec3(vx, vy, vz),
                                    age[row] - dt, travb[row], spot, tvel, wd, dt);
                vx = v.x; vy = v.y; vz = v.z;
            }

            velb[row * 3] = vx;
            velb[row * 3 + 1] = vy;
            velb[row * 3 + 2] = vz;
            float p1x = p0x + vx * dt, p1y = p0y + vy * dt, p1z = p0z + vz * dt;
            posb[row * 3] = p1x;
            posb[row * 3 + 1] = p1y;
            posb[row * 3 + 2] = p1z;
            travb[row] += MathF.Sqrt(vx * vx + vy * vy + vz * vz) * dt;

            legs0[i * 3] = p0x; legs0[i * 3 + 1] = p0y; legs0[i * 3 + 2] = p0z;
            legs1[i * 3] = p1x; legs1[i * 3 + 1] = p1y; legs1[i * 3 + 2] = p1z;
            fly0[n_fly * 3] = p0x; fly0[n_fly * 3 + 1] = p0y; fly0[n_fly * 3 + 2] = p0z;
            fly1[n_fly * 3] = p1x; fly1[n_fly * 3 + 1] = p1y; fly1[n_fly * 3 + 2] = p1z;
            fly_i[n_fly] = i;
            n_fly += 1;
            flying[i] = true;                           // this leg has to be judged
        }

        // ---- the terrain of every leg at once ----------------------------
        var hit = new bool[n_round];
        var hit_t = new float[n_round];
        var walk = new bool[n_round];
        if (n_fly > 0)
        {
            var fhit = new bool[n_fly];
            var ft = new float[n_fly];
            var fwalk = new bool[n_fly];
            terrain.ground_hits_many(fly0, fly1, n_fly, 9f, fhit, ft, fwalk);
            for (int j = 0; j < n_fly; j++)
            {
                int i = fly_i[j];
                hit[i] = fhit[j];
                hit_t[i] = ft[j];
                walk[i] = fwalk[j];
            }
        }

        // ---- pass two: what each leg ran into ----------------------------
        // the pass with the side effects: a burst entity per impact, the damage,
        // the dead round.  One round at a time, because none of it can be
        // batched.
        var stamp = new int[sph.n];             // candidate de-duplication, per leg
        var cand = new int[sph.n > 0 ? sph.n : 1];
        int tag = 1;
        for (int i = 0; i < n_round; i++)
        {
            if (!flying[i]) continue;
            int pid = pids[i];
            var slot = e.slot(pid);
            if (!slot.ok) continue;
            var a = slot.a;
            int row = slot.row;
            var wd = Defs.WEAPONS[a.u8("proj_wid")[row]];
            int k = a.u8("proj_kind")[row];
            float p0x = legs0[i * 3], p0y = legs0[i * 3 + 1], p0z = legs0[i * 3 + 2];
            float p1x = legs1[i * 3], p1y = legs1[i * 3 + 1], p1z = legs1[i * 3 + 2];
            bool gh;
            float gt;
            if (walk[i])
            {
                // a leg through a tree tile: a trunk is solved, not sampled
                gh = terrain.ground_hit(new Vec3(p0x, p0y, p0z), new Vec3(p1x, p1y, p1z), 9f,
                                        out gt, out _);
            }
            else
            {
                gh = hit[i];
                gt = hit_t[i];
            }
            float best_t = gh ? gt : 2f;
            int best_unit = -1;
            int best_mod = 0;
            // ---- unit collision ------------------------------------------
            if (sph.n > 0)
            {
                float segx = p1x - p0x, segy = p1y - p0y, segz = p1z - p0z;
                float l2 = segx * segx + segy * segy + segz * segz;
                float pr = k == 0 ? 0.35f : 0.6f;
                tag += 1;
                int nc = _leg_spheres(sph, new Vec3(p0x, p0y, p0z), new Vec3(p1x, p1y, p1z),
                                      pr + reach, stamp, tag, cand);
                if (l2 > 1e-6f && nc > 0)
                {
                    float best_tt = 2f;
                    int best_i = -1;
                    int my_faction = a.u8("faction")[row];
                    float my_age = a.f("proj_age")[row];
                    int my_src = a.i("proj_src")[row];
                    // relative motion, first order: both the round and the target
                    // are straight and constant-speed inside one 1/30 s tick, so the
                    // closest approach is a closed-form nearest point on the line
                    // `rel + w t` rather than a solve against a standing target.  A
                    // missile crosses 20 m a tick and the leg of a 30 mm round is 36
                    // m: measured against a *stationary* hull the answer is off by
                    // whole fuse radii, i.e. decided by the phase of the clock
                    for (int j = 0; j < nc; j++)
                    {
                        int s = cand[j];
                        float relx = sph.prev[s * 3] - p0x;
                        float rely = sph.prev[s * 3 + 1] - p0y;
                        float relz = sph.prev[s * 3 + 2] - p0z;
                        float wx = segx - sph.disp[s * 3];
                        float wy = segy - sph.disp[s * 3 + 1];
                        float wz = segz - sph.disp[s * 3 + 2];
                        float w2 = wx * wx + wy * wy + wz * wz;
                        float tt = (relx * wx + rely * wy + relz * wz) / MathF.Max(w2, 1e-9f);
                        if (tt < 0f) tt = 0f; else if (tt > 1f) tt = 1f;
                        float dx = relx - wx * tt, dy = rely - wy * tt, dz = relz - wz * tt;
                        float dd = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
                        if (!(dd < sph.radii[s] + pr)) continue;
                        if (sph.factions[s] == my_faction) continue;
                        // a round does not hit the hull that just fired it
                        if (my_age < 0.12f && sph.ids[s] == my_src) continue;
                        if (tt < best_tt)
                        {
                            best_tt = tt;
                            best_i = s;
                        }
                    }
                    if (best_i >= 0 && best_tt < best_t)
                    {
                        best_t = best_tt;
                        best_unit = sph.ids[best_i];
                        best_mod = sph.modules[best_i];
                    }
                }
            }
            if (best_t <= 1f)
            {
                var hitp = new Vec3(p0x + (p1x - p0x) * best_t,
                                    p0y + (p1y - p0y) * best_t,
                                    p0z + (p1z - p0z) * best_t);
                _impact(world, hitp, wd, pid, best_unit, best_mod);
                e.destroy(pid);
                continue;
            }
            // ---- out of flight -------------------------------------------
            // a wire-guided round has a maximum flight distance and cooks off where
            // it runs out: no direct hit, but the warhead still goes off there, so
            // anything standing at the end of the wire catches the blast
            if (wd.maxdist > 0f && a.f("proj_trav")[row] >= wd.maxdist)
            {
                _impact(world, new Vec3(p1x, p1y, p1z), wd, pid);
                e.destroy(pid);
            }
        }
    }

    /// <summary>Every hit sphere on the field this tick, in the order the sweep
    /// expects it.
    ///
    /// The unit blocks come first (ascending unit id, sphere order within the hull)
    /// and then one sphere per missile in the air, at its half-calibre radius.
    /// Built once per tick and shared: the sweep below and every burst
    /// (`Systems/Blasts.cs`) read the same table, so a rocket into a crowd does not
    /// rebuild the battlefield per impact.  The geometry comes off a per-type cache
    /// (`MathUtil.sphere_geom`).
    ///
    /// `prev`/`disp` are where each sphere *started* its own tick and how far it
    /// moved during it.  The two blocks are gathered at different moments and say
    /// so: a *unit* has already been moved by `movement` (it runs before this), so
    /// its `pos` is the end of the tick and `prev = pos - vel * dt`; a *missile* has
    /// not moved yet - this system moves it - so its `pos` is the start of the tick
    /// and its displacement is predicted from its velocity.</summary>
    public static SphereTable sphere_table(World world, float dt)
    {
        var e = world.ecs;
        var units = world.units();
        var mis = world.interceptable();
        var t = world.terrain;
        var sph = new SphereTable();
        // The block grid `Terrain._build_cull` laid down; those three fields are
        // private over there, so the same three numbers are derived here from the
        // public shape and the same constants.  Both halves of the pair
        // (`_block_index` and `_leg_spheres`) use this copy, so a drift here would
        // only cost candidates, never correctness - but it must not drift.
        int nb = Terrain.CULL_TILES;
        int cw = (t.w - nb) / nb + ((t.w - nb) % nb != 0 ? 1 : 0);
        int cd = (t.d - nb) / nb + ((t.d - nb) % nb != 0 ? 1 : 0);
        sph.cull = (inv: 1f / (Terrain.TILE * nb), cw, cd);
        if (units.Length == 0 && mis.Length == 0)
        {
            sph.blocks = new BlockIndex { start = new int[1], items = Array.Empty<int>() };
            sph.n = 0;
            return sph;
        }

        // how many spheres the hulls contribute, and the geometry of each
        var geoms = new MathUtil.SphereGeom[units.Length];
        int nu = 0;
        int maxk = 1;
        for (int k = 0; k < units.Length; k++)
        {
            var g = MathUtil.sphere_geom(e.uv("etype")[units[k]]);
            geoms[k] = g;
            nu += g.radius.Length;
            if (g.radius.Length > maxk) maxk = g.radius.Length;
        }
        int n = nu + mis.Length;
        var centres = new float[n * 3];
        var radii = new float[n];
        var ids = new int[n];
        var modules = new int[n];
        var factions = new int[n];
        var prev = new float[n * 3];
        var disp = new float[n * 3];

        var posv = e.fv("pos");
        var yawv = e.fv("yaw");
        var velv = e.fv("vel");
        var tmpA = new float[maxk * 3];
        var tmpB = new float[maxk * 3];
        int rowi = 0;
        // ascending unit id, sphere order within the hull - which is exactly what
        // the Python backend's per-type pass plus its stable `argsort` produced
        for (int k = 0; k < units.Length; k++)
        {
            int uid = units[k];
            var geom = geoms[k];
            int m = geom.radius.Length;
            var p = posv.get3(uid);
            var v = velv.get3(uid);
            float yaw = yawv[uid];
            MathUtil.sphere_world(geom, p, yaw, tmpA);
            MathUtil.sphere_world(geom, new Vec3(p.x + v.x * dt, p.y + v.y * dt, p.z + v.z * dt),
                                  yaw, tmpB);
            for (int s = 0; s < m; s++)
            {
                int o = rowi * 3;
                centres[o] = tmpA[s * 3];
                centres[o + 1] = tmpA[s * 3 + 1];
                centres[o + 2] = tmpA[s * 3 + 2];
                // `sphere_world` takes *positions*, so the sphere's displacement is
                // the end position minus the start one - passing the offset alone
                // would build the sphere at (move + R*local), i.e. a metre from the
                // origin, and the displacement would come out as the distance to the
                // map origin (up to 16 km).  That is not a rounding detail: `prev`
                // is the sweep's start point and the broad phase's `reach`.
                disp[o] = tmpB[s * 3] - tmpA[s * 3];
                disp[o + 1] = tmpB[s * 3 + 1] - tmpA[s * 3 + 1];
                disp[o + 2] = tmpB[s * 3 + 2] - tmpA[s * 3 + 2];
                prev[o] = tmpA[s * 3] - disp[o];
                prev[o + 1] = tmpA[s * 3 + 1] - disp[o + 1];
                prev[o + 2] = tmpA[s * 3 + 2] - disp[o + 2];
                radii[rowi] = geom.radius[s];
                ids[rowi] = uid;
                modules[rowi] = geom.module[s];
                factions[rowi] = e.uv("faction")[uid];
                rowi += 1;
            }
        }
        sph.n_units = rowi;

        for (int k = 0; k < mis.Length; k++)
        {
            int pid = mis[k];
            var p = posv.get3(pid);
            var v = velv.get3(pid);
            int o = rowi * 3;
            centres[o] = p.x; centres[o + 1] = p.y; centres[o + 2] = p.z;
            // a missile's `pos` was gathered before this system moved it: it starts
            // the tick where it is and ends it one velocity-step further on
            prev[o] = p.x; prev[o + 1] = p.y; prev[o + 2] = p.z;
            disp[o] = v.x * dt; disp[o + 1] = v.y * dt; disp[o + 2] = v.z * dt;
            radii[rowi] = HIT_R[e.uv("proj_wid")[pid]];
            ids[rowi] = pid;
            modules[rowi] = 0;
            factions[rowi] = e.uv("faction")[pid];
            rowi += 1;
        }

        sph.centres = centres;
        sph.radii = radii;
        sph.ids = ids;
        sph.modules = modules;
        sph.factions = factions;
        sph.prev = prev;
        sph.disp = disp;
        sph.n = n;
        sph.blocks = _block_index(sph, n);
        return sph;
    }

    /// <summary>CSR from a block index to the spheres that can reach into it.
    ///
    /// A round flies 10-35 m a tick but used to sweep every unit on the field: 95
    /// projectiles against 400 spheres is three million distance tests a second.
    /// Each sphere is filed under every block its box (grown by a metre, the widest
    /// round) overlaps, so looking up the blocks a leg crosses is exact - no
    /// candidate that the full sweep would have hit can be missed.</summary>
    private static BlockIndex _block_index(SphereTable sph, int n)
    {
        int cw = sph.cull.cw, cd = sph.cull.cd;
        int nb = cw * cd;
        var idx = new BlockIndex();
        idx.start = new int[nb + 1];
        if (n == 0)
        {
            idx.items = Array.Empty<int>();
            return idx;
        }
        var cnt = new int[nb];
        for (int s = 0; s < n; s++)
        {
            _block_span(sph, s, out int x0, out int x1, out int z0, out int z1);
            for (int bz = z0; bz <= z1; bz++)
                for (int bx = x0; bx <= x1; bx++)
                    cnt[bz * cw + bx] += 1;
        }
        int total = 0;
        for (int b = 0; b < nb; b++)
        {
            idx.start[b] = total;
            total += cnt[b];
        }
        idx.start[nb] = total;
        var items = new int[total];
        // the cursor becomes the running start of each block, as it is filled
        for (int b = 0; b < nb; b++) cnt[b] = idx.start[b];
        for (int s = 0; s < n; s++)
        {
            _block_span(sph, s, out int x0, out int x1, out int z0, out int z1);
            for (int bz = z0; bz <= z1; bz++)
                for (int bx = x0; bx <= x1; bx++)
                {
                    int b = bz * cw + bx;
                    items[cnt[b]] = s;
                    cnt[b] += 1;
                }
        }
        idx.items = items;
        return idx;
    }

    /// <summary>The block box one sphere's (grown) bounding box covers.</summary>
    private static void _block_span(SphereTable sph, int s, out int x0, out int x1,
                                    out int z0, out int z1)
    {
        int cw = sph.cull.cw, cd = sph.cull.cd;
        float inv = sph.cull.inv;
        float pad = sph.radii[s] + 1f;
        float cx = sph.centres[s * 3], cz = sph.centres[s * 3 + 2];
        x0 = Math.Clamp((int)((cx - pad) * inv), 0, cw - 1);
        x1 = Math.Clamp((int)((cx + pad) * inv), 0, cw - 1);
        z0 = Math.Clamp((int)((cz - pad) * inv), 0, cd - 1);
        z1 = Math.Clamp((int)((cz + pad) * inv), 0, cd - 1);
    }

    /// <summary>Sphere row numbers the segment could reach - the blocks its box
    /// touches, ascending, de-duplicated with the `stamp`/`tag` pair so a leg that
    /// crosses a block boundary is not tested twice against the same sphere.</summary>
    private static int _leg_spheres(SphereTable sph, Vec3 p0, Vec3 p1, float pr,
                                    int[] stamp, int tag, int[] outv)
    {
        var items = sph.blocks.items;
        var start = sph.blocks.start;
        if (items.Length == 0) return 0;
        int cw = sph.cull.cw, cd = sph.cull.cd;
        float inv = sph.cull.inv;
        float lo_x = MathF.Min(p0.x, p1.x) - pr;
        float hi_x = MathF.Max(p0.x, p1.x) + pr;
        float lo_z = MathF.Min(p0.z, p1.z) - pr;
        float hi_z = MathF.Max(p0.z, p1.z) + pr;
        int x0 = Math.Min(Math.Max((int)(lo_x * inv), 0), cw - 1);
        int x1 = Math.Min(Math.Max((int)(hi_x * inv), 0), cw - 1);
        int z0 = Math.Min(Math.Max((int)(lo_z * inv), 0), cd - 1);
        int z1 = Math.Min(Math.Max((int)(hi_z * inv), 0), cd - 1);
        if (x0 == x1 && z0 == z1)
        {
            int b = z0 * cw + x0;
            int m0 = start[b + 1] - start[b];
            Array.Copy(items, start[b], outv, 0, m0);
            return m0;
        }
        int m = 0;
        for (int bz = z0; bz <= z1; bz++)
        {
            for (int bx = x0; bx <= x1; bx++)
            {
                int b = bz * cw + bx;
                for (int i = start[b]; i < start[b + 1]; i++)
                {
                    int s = items[i];
                    if (stamp[s] == tag) continue;
                    stamp[s] = tag;
                    outv[m] = s;
                    m += 1;
                }
            }
        }
        // ascending rows, because the sweep breaks distance ties by that order
        Array.Sort(outv, 0, m);
        return m;
    }

    /// <summary>A round goes off at `p`: the hull it landed on, then a burst entity.
    ///
    /// The area half is not solved here - it becomes a `blast`, which
    /// `Systems/Blasts.cs` judges a stage later (and, for a round that killed
    /// something, is what makes a chain reaction a tick apart by construction).
    ///
    /// `skip` spares a unit the burst: the proximity fuse has already settled the
    /// target it fused on, at the moment it fused, and a missile that has flown on
    /// twenty metres would otherwise be judged against a burst it has left.</summary>
    public static void _impact(World world, Vec3 p, WeaponDef wd, int proj_id,
                               int direct_unit = -1, int direct_mod = 0, int skip = -1)
    {
        var e = world.ecs;
        // the credit was frozen at the muzzle (`proj_pid` / `proj_name`), because
        // the hull that fired this round can be a wreck by the time it lands - and
        // its id can already belong to the wreck it left.  A kill it lands is still
        // the pilot's, and the kill feed still knows whose name to put on it.
        int pid = e.iv("proj_pid")[proj_id];
        int faction = e.uv("faction")[proj_id];
        var by = ((int)e.uv16("proj_name")[proj_id], faction,
                  (int)e.uv("proj_etype")[proj_id]);
        float r = wd.er;
        world.emit("boom", ("x", p.x), ("y", p.y), ("z", p.z), ("r", r),
                   ("big", r >= 5f ? 1 : 0), ("wid", wd.wid), ("proj", proj_id));
        if (direct_unit >= 0)
        {
            Combat.hit(world, direct_unit, direct_mod, wd, 0f, pid,
                       $"{wd.key} direct", by);
        }
        if (r <= 0f || wd.ed <= 0f) return;
        int spared = direct_unit >= 0 ? direct_unit : skip;
        Blasts.spawn(world, p, (wd.ed, r), friendly: false, skip: spared, pid: pid,
                     faction: faction, by: by, wid: wd.wid);
    }
}
