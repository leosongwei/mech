using Mech.Server.Systems;

namespace Mech.Server.Systems;

/// <summary>The shared optical picture: one current observation bit and one last-seen
/// tick per faction.  Datalink is the only writer; consumers use `Visibility`.</summary>
public static class Datalink
{
    public static void run(World world, float dt)
    {
        var e = world.ecs;
        var seen = e.query("link_seen");
        for (int k = 0; k < seen.Length; k++)
        {
            int id = seen[k];
            e.uv("link_seen").set(id, 0, 0);
            e.uv("link_seen").set(id, 1, 0);
        }
        world.linked_missiles[Defs.FACTION_RED].Clear();
        world.linked_missiles[Defs.FACTION_BLUE].Clear();

        int[] ids = Array.Empty<int>();
        float[] points = Array.Empty<float>();
        var observers = e.query("datalink");
        var targets = e.query("datalink");
        var targetSet = new HashSet<int>(targets);
        var pos = e.fv("pos");
        var faction = e.uv("faction");
        var link = e.uv("link_seen");
        var last = e.iv("link_seen_tick");
        foreach (int observer in observers)
        {
            if (!e.is_alive(observer)) continue;
            int mine = faction[observer];
            var def = Defs.UNIT_DEFS[e.uv("etype")[observer]];
            var (eye, _, _) = Weapons.aim_of(world, observer);
            var (ci, cj) = world.grid.cell_of(eye.x, eye.z);
            int n = world.grid.box_into(ci, cj, (int)(def.sight * world.grid.inv) + 1,
                                        ref ids, ref points);
            for (int k = 0; k < n; k++)
            {
                int target = ids[k];
                if (target == observer || !targetSet.Contains(target) || faction[target] == mine
                    || !e.is_alive(target) || link.get(target, mine) != 0) continue;
                var p = new Vec3(points[k * 3], points[k * 3 + 1], points[k * 3 + 2]);
                float dx = p.x - eye.x, dy = p.y - eye.y, dz = p.z - eye.z;
                if (dx * dx + dy * dy + dz * dz > def.sight * def.sight) continue;
                var td = Defs.UNIT_DEFS[e.uv("etype")[target]];
                var at = new Vec3(p.x, p.y + td.height * 0.5f, p.z);
                if (!Sight.clear(world, eye, at)) continue;
                link.set(target, mine, 1);
                last.set(target, mine, world.tick);
            }
        }

        var projectiles = world.projectiles();
        foreach (int observer in observers)
        {
            if (!e.is_alive(observer)) continue;
            int mine = faction[observer];
            var def = Defs.UNIT_DEFS[e.uv("etype")[observer]];
            var (eye, _, _) = Weapons.aim_of(world, observer);
            foreach (int pid in projectiles)
            {
                if (!e.is_alive(pid) || faction[pid] == mine) continue;
                int kind = e.uv("proj_kind")[pid];
                if (kind != 2 && kind != 3) continue;
                if (world.linked_missiles[mine].Contains(pid)) continue;
                var p = pos.get3(pid);
                float dx = p.x - eye.x, dy = p.y - eye.y, dz = p.z - eye.z;
                if (dx * dx + dy * dy + dz * dz > def.sight * def.sight) continue;
                if (Sight.clear(world, eye, p)) world.linked_missiles[mine].Add(pid);
            }
        }
    }

    public static bool known(World world, int faction, int target) =>
        world.ecs.has(target, "link_seen") && world.ecs.uv("link_seen").get(target, faction) != 0;

    public static bool missile_known(World world, int faction, int projectile) =>
        faction >= 0 && faction < world.linked_missiles.Length
        && world.linked_missiles[faction].Contains(projectile);
}
