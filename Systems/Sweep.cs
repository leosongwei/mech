// The tick's hit spheres, the broad phase over them, and the one
// "segment vs moving sphere" solve every collision system shares.
//
// `SphereTable` is the tick's shared copy of every hit sphere on the field.
// `prev`/`disp` are the reason it exists in this shape: a missile covers 20 m in
// a tick against a 2.5 m fuse and an 0.08 m ball, so solving a hit against a
// *standing* target would be decided by the phase of the clock.  The sweep solves
// the relative leg instead, and a unit's `pos` is the end of its tick while a
// missile's is the start - which is why the two blocks are gathered at different
// moments and each says so.
//
// `SweepScratch` is the other half: the legs the flight systems publish this tick.
// `Flights.*` records `(proj_prev, pos)` on the entity and appends the same pair
// here, and the collision systems read this list - so a round that expired before
// it flew (and is therefore not here) is still judged, by its record alone.

namespace Mech.Server.Systems;

/// <summary>Every leg a round flies this tick, in the order the flight systems
/// flew them, reused between ticks.</summary>
public sealed class SweepScratch
{
    public int[] ids = new int[64];
    public float[] p0 = new float[64 * 3];
    public float[] p1 = new float[64 * 3];
    // the ground pass's own answer, one row per leg: kept here so a tick costs
    // no allocation at all
    public bool[] hit = new bool[64];
    public float[] thr = new float[64];
    public int n;

    /// <summary>Start a tick: forget last tick's legs, keep the buffers.</summary>
    public void begin() => n = 0;

    public void add(int eid, Vec3 a, Vec3 b)
    {
        if (n == ids.Length)
        {
            int cap = ids.Length * 2;
            Array.Resize(ref ids, cap);
            Array.Resize(ref p0, cap * 3);
            Array.Resize(ref p1, cap * 3);
            Array.Resize(ref hit, cap);
            Array.Resize(ref thr, cap);
        }
        int o = n * 3;
        p0[o] = a.x; p0[o + 1] = a.y; p0[o + 2] = a.z;
        p1[o] = b.x; p1[o + 1] = b.y; p1[o + 2] = b.z;
        ids[n] = eid;
        n += 1;
    }
}

public static class Sweep
{
    // collision sphere of each weapon's rounds, by `wid`: the half-calibre of a
    // missile (`Defs.WEAPONS[...].hit_r`) and 0 for anything a bullet is - only an
    // `interceptable` round ever has a row in the table, so the zero never gets read
    public static readonly float[] HIT_R = Defs.WEAPONS.Select(w => w.hit_r).ToArray();

    /// <summary>The tick's hit-sphere table, asked for once: this system builds
    /// it where movement has already moved the hulls, and everything after it -
    /// the round sweep, the fuse, the bursts - reads the same one.</summary>
    public static class Spheres
    {
        public static void run(World world, float dt) => world.sphere_table(dt);
    }

    /// <summary>Closest approach of the leg `p0 + seg` to sphere row `s`: as a
    /// fraction of the leg, and as the distance from the sphere's own centre -
    /// the caller subtracts the radius because the two of them weigh it against
    /// different figures (a round's half-width, a fuse's trigger radius).
    ///
    /// This is the one solve the round sweep and the proximity fuse share, and it
    /// is written once because it is what makes a 20 m/tick missile judgeable
    /// against a 2.5 m fuse: both bodies are straight and constant-speed inside
    /// one 1/30 s, so the answer is the closed-form nearest point on `rel + w t`
    /// rather than a test against a standing target, which is decided by the phase
    /// of the clock (measured: 6 m off at the end of the leg, a clean miss).</summary>
    public static void Closest(SphereTable sph, int s, in Vec3 p0,
                               float segx, float segy, float segz,
                               out float t, out float dist)
    {
        float relx = sph.prev[s * 3] - p0.x;
        float rely = sph.prev[s * 3 + 1] - p0.y;
        float relz = sph.prev[s * 3 + 2] - p0.z;
        float wx = segx - sph.disp[s * 3];
        float wy = segy - sph.disp[s * 3 + 1];
        float wz = segz - sph.disp[s * 3 + 2];
        float w2 = wx * wx + wy * wy + wz * wz;
        float tt = (relx * wx + rely * wy + relz * wz) / MathF.Max(w2, 1e-9f);
        if (tt < 0f) tt = 0f; else if (tt > 1f) tt = 1f;
        float dx = relx - wx * tt, dy = rely - wy * tt, dz = relz - wz * tt;
        t = tt;
        dist = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
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

    /// <summary>The sphere rows a leg of radius `pr` can reach - the blocks its
    /// box touches, ascending, de-duplicated with the `stamp`/`tag` pair so a leg
    /// that crosses a block boundary is not tested twice against the same
    /// sphere.</summary>
    public static int LegSpheres(SphereTable sph, Vec3 p0, Vec3 p1, float pr,
                                 int[] stamp, int tag, int[] outv)
        => _leg_spheres(sph, p0, p1, pr, stamp, tag, outv);

    /// <summary>The largest displacement of any sphere this tick: a round's
    /// broad-phase box grows by it, or a missile crossing into another round's leg
    /// is never a candidate.</summary>
    public static float reach(SphereTable sph)
    {
        float r = 0f;
        for (int k = 0; k < sph.n * 3; k++)
        {
            float d = MathF.Abs(sph.disp[k]);
            if (d > r) r = d;
        }
        return r;
    }
}
