using Mech.Server.Systems;

namespace Mech.Server.Systems;

/// <summary>The shared optical picture: vehicles report hostile armour and guided rounds
/// inside a 4 km, 80-degree forward cone.  One current bit and one last-seen tick are kept
/// per faction; Datalink is the only writer and consumers use `Visibility`.</summary>
public static class Datalink
{
    public const float RANGE = 4000f;
    public const float CONE = Defs.BEAM_CONE;

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
            float yaw = e.has(observer, "gaze_yaw") ? e.fv("gaze_yaw")[observer]
                          : e.has(observer, "vehicle") ? e.fv("turret_yaw")[observer]
                                                       : e.fv("sight_yaw")[observer];
            var (eye, _, _) = Weapons.aim_of(world, observer);
            var (ci, cj) = world.grid.cell_of(eye.x, eye.z);
            int n = world.grid.box_into(ci, cj, (int)(RANGE * world.grid.inv) + 1,
                                        ref ids, ref points);
            for (int k = 0; k < n; k++)
            {
                int target = ids[k];
                if (target == observer || !targetSet.Contains(target) || faction[target] == mine
                    || !e.is_alive(target) || link.get(target, mine) != 0) continue;
                var p = new Vec3(points[k * 3], points[k * 3 + 1], points[k * 3 + 2]);
                float dx = p.x - eye.x, dy = p.y - eye.y, dz = p.z - eye.z;
                if (dx * dx + dy * dy + dz * dz > RANGE * RANGE) continue;
                if (!in_cone(yaw, dx, dz)) continue;
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
            float yaw = e.has(observer, "gaze_yaw") ? e.fv("gaze_yaw")[observer]
                          : e.has(observer, "vehicle") ? e.fv("turret_yaw")[observer]
                                                       : e.fv("sight_yaw")[observer];
            var (eye, _, _) = Weapons.aim_of(world, observer);
            // the observer's own receiver: what its radar can *place* is what the
            // rest of the side gets to place, so the side's picture of a round is
            // never better than the best machine in it
            float strength = Defs.UNIT_DEFS[e.uv("etype")[observer]].radar_strength;
            var ovel = e.has(observer, "vel") ? e.fv("vel").get3(observer) : Vec3.zero;
            foreach (int pid in projectiles)
            {
                if (!e.is_alive(pid) || faction[pid] == mine) continue;
                int kind = e.uv("proj_kind")[pid];
                // a guided round is worth sharing with the rest of the side: the beam
                // rider and the top attack have always been, and the loitering
                // munition is the third - it is the one a crew most wants to see
                // coming, because it is the one that is not aimed at anything yet
                if (kind != Defs.PROJ_SACLOS && kind != Defs.PROJ_TOP
                    && kind != Defs.PROJ_DRONE) continue;
                if (world.linked_missiles[mine].Contains(pid)) continue;
                var p = pos.get3(pid);
                float dx = p.x - eye.x, dy = p.y - eye.y, dz = p.z - eye.z;
                if (dx * dx + dy * dy + dz * dz > RANGE * RANGE) continue;
                if (!in_cone(yaw, dx, dz)) continue;
                if (!Sight.clear(world, eye, p)) continue;
                if (_placed(e, ovel, strength, eye, p, pid)) world.linked_missiles[mine].Add(pid);
            }
        }
    }

    /// <summary>Does this observer's radar hold a solution on that round: bearing,
    /// slant range and closing speed, i.e. a position and a velocity vector.
    ///
    /// The same law the pilot's own warning receiver answers (`d^2 &lt;= K * v_closing`,
    /// `Defs.radar_k`) and the same one the crew's interceptor fires on, so "a friend
    /// has detected it" means exactly what "I have detected it" means.  Two machines
    /// seeing a round through a cone is a shadow; a solution is a target.
    /// </summary>
    private static bool _placed(ECS e, Vec3 ovel, float strength, Vec3 eye,
                                Vec3 p, int pid)
    {
        if (strength <= 0f) return false;
        var rcs = Defs.WEAPONS[e.uv("proj_wid")[pid]].rcs;
        var vel = e.fv("vel").get3(pid);
        float rx = p.x - eye.x, ry = p.y - eye.y, rz = p.z - eye.z;
        float vr = (vel.x - ovel.x) * rx + (vel.y - ovel.y) * ry + (vel.z - ovel.z) * rz;
        float d = MathF.Sqrt(rx * rx + ry * ry + rz * rz);
        float vc = -vr / MathF.Max(d, 1e-3f);
        if (vc > Defs.RADAR_V_REF) vc = Defs.RADAR_V_REF;   // the echo saturates
        return d * d <= Defs.radar_k(strength, rcs) * vc;
    }

    private static bool in_cone(float yaw, float dx, float dz) =>
        MathF.Abs(MathUtil.wrap_pi(MathF.Atan2(dz, dx) - yaw)) <= CONE;

    public static bool known(World world, int faction, int target) =>
        world.ecs.has(target, "link_seen") && world.ecs.uv("link_seen").get(target, faction) != 0;

    public static bool missile_known(World world, int faction, int projectile) =>
        faction >= 0 && faction < world.linked_missiles.Length
        && world.linked_missiles[faction].Contains(projectile);
}
