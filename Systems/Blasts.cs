// Area damage, and the lifetime of the flash that does it.
//
// An explosion is an entity.  Whoever wants one - a round landing, a proximity
// fuse, a wreck of either hull - only creates it (`spawn`); the single
// implementation of "everything inside `er` takes blast" reads it here.
//
// That buys four things the old in-line loop could not:
//
//   * one place that solves area damage, so a mine or a fuel dump is a `spawn`
//     call;
//   * a chain reaction is a tick apart *by construction*.  A hull that dies this
//     tick leaves a burst that goes off on the next one, so it cannot damage the
//     hulk that hull has already become - no "solve every distance first, then
//     apply" discipline needed;
//   * the credit is frozen at creation (`blast_pid`), so a burst whose shooter has
//     since died still pays out to the pilot who fired it;
//   * `blast_t` is a fuse.  A one-tick burst is `0`; anything longer is a burning
//     area that goes on taking pieces off whatever stands in it.
//
// Costs nothing when nothing is exploding, and nothing else owns these buffers.
//
// Ported from `backend/server/systems/blasts.py`.

namespace Mech.Server.Systems;

public static class Blasts
{
    /// <summary>Create a burst at `pos`.  `power` is `(ed, er)`; `skip` is spared.
    ///
    /// `friendly` false means "enemies only" (a weapon burst), true means "no
    /// sides" (a wreck going up).  `life` is how many seconds it keeps burning - 0
    /// is a single tick, which is every explosion in the game today.
    ///
    /// `by` / `wid` are who the kill feed names and with what: frozen here for the
    /// same reason the credit is (`blast_pid`) - the burst is judged a stage later,
    /// and the hull that left it may be gone by then.</summary>
    public static int spawn(World world, Vec3 pos, (float ed, float er) power,
                            bool friendly = false, int skip = -1, int pid = -1,
                            int faction = -1, float life = 0f,
                            (int name_id, int fac, int etype)? by = null, int wid = -1)
    {
        float ed = power.ed;
        if (ed <= 0f) return -1;
        var e = world.ecs;
        int bid = e.create(Components.BLAST_COMPS);
        e.fv("pos").set3(bid, pos);
        // 255 is "no side" and it is deliberately not 0: a burst with no owner
        // spares nobody, and faction 0 is a faction
        e.uv("faction")[bid] = (byte)(faction < 0 ? 255 : faction);
        var pw = e.fv("blast_power");
        pw.set(bid, 0, ed);
        pw.set(bid, 1, power.er);
        e.fv("blast_t")[bid] = life;
        e.uv("blast_friendly")[bid] = (byte)(friendly ? 1 : 0);
        e.iv("blast_skip")[bid] = skip;
        e.iv("blast_pid")[bid] = pid;
        if (by is not null)
        {
            e.uv16("blast_name")[bid] = (ushort)Math.Max(0, by.Value.name_id);
            e.uv("blast_etype")[bid] = (byte)Math.Max(0, by.Value.etype);
        }
        e.uv("blast_wid")[bid] = (byte)Math.Max(0, wid);
        return bid;
    }

    public static void run(World world, float dt)
    {
        var e = world.ecs;
        var ids = world.blasts();
        if (ids.Length == 0) return;
        var sph = world.sphere_table(dt);
        var t = e.fv("blast_t");
        foreach (int bid in ids)
        {
            _burst(world, bid, sph);
            t[bid] = t[bid] - dt;
            if (t[bid] <= 0f) e.destroy(bid);
        }
    }

    /// <summary>One burst against the whole battlefield: one reading per hull.</summary>
    private static void _burst(World world, int bid, SphereTable sph)
    {
        var e = world.ecs;
        var pw = e.fv("blast_power");
        float ed = pw.get(bid, 0);
        float er = pw.get(bid, 1);
        if (ed <= 0f) return;
        if (sph.n == 0) return;
        var p = e.fv("pos").get3(bid);
        int name_id = e.uv16("blast_name")[bid];
        // the burst as a weapon: area damage only, so `ad` is 0 and nothing here
        // can punch a plate
        var wd = new WeaponDef { ed = ed, er = er, ad = 0f, wid = e.uv("blast_wid")[bid] };
        int skip = e.iv("blast_skip")[bid];
        int fac = e.uv("faction")[bid];
        bool friendly = e.uv("blast_friendly")[bid] != 0;

        // which spheres are inside the pattern
        int m = 0;
        var h_id = new int[sph.n];      // the unit the sphere belongs to
        var h_row = new int[sph.n];     // the sphere's row in the table
        var h_d = new float[sph.n];     // its distance from the centre
        for (int s = 0; s < sph.n; s++)
        {
            float dx = sph.centres[s * 3] - p.x;
            float dy = sph.centres[s * 3 + 1] - p.y;
            float dz = sph.centres[s * 3 + 2] - p.z;
            float d = MathF.Sqrt(dx * dx + dy * dy + dz * dz) - sph.radii[s];
            if (!(d <= er)) continue;
            if (skip >= 0 && sph.ids[s] == skip) continue;
            if (!friendly && sph.factions[s] == fac) continue;
            h_id[m] = sph.ids[s];
            h_row[m] = s;
            h_d[m] = d;
            m += 1;
        }
        if (m == 0) return;

        // one splash per unit, off its closest sphere - the same rule (and the same
        // order) the impact burst always used: by unit id, then by distance, then
        // by the sphere's place in the table
        var order = new int[m];
        for (int k = 0; k < m; k++) order[k] = k;
        Array.Sort(order, (x, y) =>
        {
            int c = h_id[x].CompareTo(h_id[y]);
            if (c != 0) return c;
            c = h_d[x].CompareTo(h_d[y]);
            return c != 0 ? c : x.CompareTo(y);
        });

        int pid = e.iv("blast_pid")[bid];
        // who is named on it: the shooter, frozen at creation.  An unnamed, unowned
        // burst (a wreck going up) names nobody - it is not a kill anyone made
        (int name_id, int fac, int etype)? by = null;
        if (name_id > 0 && (fac == Defs.FACTION_RED || fac == Defs.FACTION_BLUE))
            by = (name_id, fac, (int)e.uv("blast_etype")[bid]);
        // the table was built earlier in this tick: an id it holds can have been
        // freed by a round landing since and handed out again (LIFO) to something
        // else - a burst, a puff.  `ecs.destroyed` is what this tick reaped, so it
        // is exactly the stale set, and a burst must not splash whatever now wears
        // the id.
        int last = -1;
        for (int k = 0; k < m; k++)
        {
            int h = order[k];
            int uid = h_id[h];
            if (uid == last) continue;               // already settled this hull
            last = uid;
            if (e.destroyed.Contains(uid)) continue;
            float d = MathF.Max(h_d[h], 0f);
            Combat.splash(world, uid, sph.modules[h_row[h]], wd, d, pid,
                          $"blast {d:F1} m", by);
        }
    }
}
