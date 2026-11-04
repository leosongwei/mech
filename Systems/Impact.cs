// The one reader of `first_hit_record`, and the only thing that ends a round.
//
// Two decisions live here and nowhere else: what a contact does (a hull takes a
// direct hit *and* the burst, a wall takes the burst, a fused missile takes its
// splash at the gap the fuse measured) and that the round is then gone.  Every
// collision system upstream only says where the round stopped, so the order they
// run in cannot change what a round does to the world.
//
// The area half is not solved here either - it becomes a `blast`, which
// `Systems/Blasts.cs` judges a stage later (and, for a round that killed
// something, is what makes a chain reaction a tick apart by construction).

namespace Mech.Server.Systems;

public static class Impact
{
    public static void run(World world, float dt)
    {
        var e = world.ecs;
        var ids = world.projectiles();
        if (ids.Length == 0) return;
        for (int i = 0; i < ids.Length; i++)
        {
            int pid = ids[i];
            var slot = e.slot(pid);
            if (!slot.ok) continue;
            var a = slot.a;
            int row = slot.row;
            if (a.f("first_hit_t")[row] > 1f) continue;      // nothing this tick
            var wd = Defs.WEAPONS[a.u8("proj_wid")[row]];
            byte kind = a.u8("first_hit_kind")[row];
            var p = a.get_f3("first_hit_pos", row);
            switch (kind)
            {
                case Defs.HIT_UNIT:
                {
                    int unit = a.i("first_hit_unit")[row];
                    int mod = a.u8("first_hit_mod")[row];
                    _detonate(world, p, wd, pid, unit, mod);
                    break;
                }
                case Defs.HIT_FUSE:
                {
                    // the round went off where it fused, so the burst, the flash
                    // and the credit all come out of that point.  A missile found
                    // there is settled here, at the gap the fuse measured: by the
                    // time `Blasts` judges the burst a 600 m/s round is twenty
                    // metres out of a four metre radius, so handing it to the burst
                    // would be asking the wrong question
                    int tid = a.i("first_hit_unit")[row];
                    float gap = a.f("first_hit_gap")[row];
                    bool human = e.has(tid, "person");
                    if (!human && e.is_alive(tid))
                    {
                        var by = ((int)e.uv16("proj_name")[pid], a.u8("faction")[row],
                                  (int)e.uv("proj_etype")[row]);
                        Combat.splash(world, tid, Defs.MOD_CHASSIS, wd, gap,
                                      e.iv("proj_pid")[pid], $"prox {gap:F1} m", by);
                    }
                    _detonate(world, p, wd, pid, skip: tid);
                    break;
                }
                case Defs.HIT_VANISH:
                    // a bullet or a rocket that ran out of flight: gone, and it
                    // leaves nothing behind
                    break;
                default:
                    // ground, a building, a trunk, a battery going flat, the end
                    // of a wire: the round bursts where it stopped
                    if (kind != Defs.HIT_NONE) _detonate(world, p, wd, pid);
                    break;
            }
            e.destroy(pid);
        }
    }

    /// <summary>A round goes off at `p`: the hull it landed on, then a burst entity.
    ///
    /// `skip` spares a unit the burst: the proximity fuse has already settled the
    /// target it fused on, at the moment it fused, and a missile that has flown on
    /// twenty metres would otherwise be judged against a burst it has left.</summary>
    public static void _detonate(World world, Vec3 p, WeaponDef wd, int proj_id,
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
