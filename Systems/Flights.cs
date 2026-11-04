// One system per kind of round, and the one that ends a round's flight.
//
// A round's identity is a tag component (`Defs.KIND_TAGS`), so each kind is its
// own archetype and its own pass: the flight of a bullet is three lines, a beam
// rider's is a steering loop, and neither pays for the other's branch.  What the
// four share is the prologue and the epilogue - re-arm the record, publish the
// leg's start, integrate, publish the leg - and that is all `_begin` / `_move`
// are.
//
// `Expire` sits in front of all four on purpose: it owns the two countdowns
// (`proj_age`, `proj_life`), and a round that has run out of battery or wire stops
// where it stands *before* this tick's move.  The one semantic line a flight
// system owns is `if (first_hit_t <= 1) continue` - a condemned round holds still.
//
// Written from `Systems/Projectiles.cs` (the Python port's `systems/projectiles.py`).

namespace Mech.Server.Systems;

public static class Flights
{
    /// <summary>Battery and wire: the round's `life` and its maximum flight
    /// distance, both settled before anything moves, plus the one place
    /// `proj_age` is counted.
    ///
    /// A round that runs out either cooks off where it stands (`expire_burst`: the
    /// top attack's battery, a beam rider's wire) or is simply gone (a bullet and a
    /// rocket, which leave nothing behind).  Both are written into the
    /// `first_hit_record` as a contact at t=0, so `Impact` treats them like any
    /// other ending instead of a third special case.</summary>
    public static class Expire
    {
        public static void run(World world, float dt)
        {
            var e = world.ecs;
            world.sweep.begin();                 // this tick's leg list starts here
            var ids = world.projectiles();
            if (ids.Length == 0) return;
            for (int i = 0; i < ids.Length; i++)
            {
                int pid = ids[i];
                var slot = e.slot(pid);
                if (!slot.ok) continue;
                var a = slot.a;
                int row = slot.row;
                var wd = Defs.WEAPONS[a.u8("proj_wid")[row]];
                var age = a.f("proj_age");
                var life = a.f("proj_life");
                age[row] += dt;
                life[row] -= dt;
                bool out_of_life = life[row] <= 0f;
                bool out_of_wire = wd.maxdist > 0f && a.f("proj_trav")[row] >= wd.maxdist;
                if (!out_of_life && !out_of_wire) continue;
                // the wire always cooks off; running out of battery depends on the
                // round
                bool burst = out_of_wire || wd.expire_burst;
                a.f("first_hit_t")[row] = 0f;
                a.u8("first_hit_kind")[row] = burst ? Defs.HIT_EXPIRE : Defs.HIT_VANISH;
                a.i("first_hit_unit")[row] = -1;
                a.u8("first_hit_mod")[row] = 0;
                a.f("first_hit_gap")[row] = 0f;
                var p = a.get_f3("pos", row);
                a.f("first_hit_pos")[row * 3 + 0] = p.x;
                a.f("first_hit_pos")[row * 3 + 1] = p.y;
                a.f("first_hit_pos")[row * 3 + 2] = p.z;
            }
        }
    }

    /// <summary>A bullet: gravity and nothing else, so the air half is a parabola
    /// and `Core/Ballistics.cs` can solve the zero in closed form.</summary>
    public static class Ballistic
    {
        public static void run(World world, float dt)
        {
            var e = world.ecs;
            foreach (var g in e.groups("pos", "bullet"))
            {
                var posb = g.f("pos");
                var velb = g.f("vel");
                var wdv = g.u8("proj_wid");
                for (int row = 0; row < g.n; row++)
                {
                    if (!_begin(g, row)) continue;
                    var wd = Defs.WEAPONS[wdv[row]];
                    var p0 = new Vec3(posb[row * 3], posb[row * 3 + 1], posb[row * 3 + 2]);
                    var v = new Vec3(velb[row * 3], velb[row * 3 + 1] - wd.g * dt,
                                     velb[row * 3 + 2]);
                    _move(world, g, row, p0, v, dt);
                }
            }
        }
    }

    /// <summary>An unguided rocket: thrust along the velocity it already has, up
    /// to `maxvel`, then gravity.  It never turns its nose, so it has no way to
    /// point anywhere but where it is already going.</summary>
    public static class Rocket
    {
        public static void run(World world, float dt)
        {
            var e = world.ecs;
            foreach (var g in e.groups("pos", "unguided_missile"))
            {
                var posb = g.f("pos");
                var velb = g.f("vel");
                var wdv = g.u8("proj_wid");
                for (int row = 0; row < g.n; row++)
                {
                    if (!_begin(g, row)) continue;
                    var wd = Defs.WEAPONS[wdv[row]];
                    var p0 = new Vec3(posb[row * 3], posb[row * 3 + 1], posb[row * 3 + 2]);
                    float vx = velb[row * 3], vy = velb[row * 3 + 1], vz = velb[row * 3 + 2];
                    var (sp, _) = _norm(new Vec3(vx, vy, vz));
                    float speed = MathF.Min(wd.maxvel,
                                            MathF.Sqrt(vx * vx + vy * vy + vz * vz)
                                            + wd.thrust * dt);
                    var v = new Vec3(sp.x * speed, sp.y * speed - wd.g * dt, sp.z * speed);
                    _move(world, g, row, p0, v, dt);
                }
            }
        }
    }

    /// <summary>A beam rider: it flies the launcher's sight line, so its aim is
    /// re-read every tick off the hull that fired it, and a hull that dies leaves
    /// the round to coast straight on.  The motor burns for `burn`, then the
    /// airframe's quadratic drag bleeds the cruise speed back down.</summary>
    public static class Saclos
    {
        public static void run(World world, float dt)
        {
            var e = world.ecs;
            foreach (var g in e.groups("pos", "saclos"))
            {
                var posb = g.f("pos");
                var velb = g.f("vel");
                var age = g.f("proj_age");
                var wdv = g.u8("proj_wid");
                var seed = g.f("proj_seed");
                var src = g.i("proj_src");
                for (int row = 0; row < g.n; row++)
                {
                    if (!_begin(g, row)) continue;
                    var wd = Defs.WEAPONS[wdv[row]];
                    var p0 = new Vec3(posb[row * 3], posb[row * 3 + 1], posb[row * 3 + 2]);
                    float vx = velb[row * 3], vy = velb[row * 3 + 1], vz = velb[row * 3 + 2];
                    var (sp, _) = _norm(new Vec3(vx, vy, vz));
                    float speed = MathF.Sqrt(vx * vx + vy * vy + vz * vz);

                    int shooter = src[row];
                    bool has_aim = false;
                    var d = Vec3.zero;
                    float ax = 0f, ay = 0f, az = 0f;
                    if (shooter >= 0 && e.is_alive(shooter)
                        && e.uv("etype")[shooter] != Defs.ETYPE_BASE)
                    {
                        var (origin, sy, spitch) = Weapons.aim_of(world, shooter);
                        d = MathUtil.dir_from(sy, spitch);
                        float relx = p0.x - origin.x, rely = p0.y - origin.y,
                              relz = p0.z - origin.z;
                        float along = relx * d.x + rely * d.y + relz * d.z;
                        // steer for a point well *ahead* on the sight line: the
                        // foot of the perpendicular sits 90 deg off the round's
                        // heading, so aiming there commands a full-rate turn for
                        // any lateral error and the round slithers across the beam
                        // instead of settling.
                        float lead = MathF.Max(0f, along) + MathF.Max(90f, speed * 1.2f);
                        ax = origin.x + d.x * lead;
                        ay = origin.y + d.y * lead;
                        az = origin.z + d.z * lead;
                        has_aim = true;
                    }
                    // the motor has a burn time, then it is out and the airframe
                    // coasts: quadratic drag bleeds the cruise speed back down for
                    // the rest of the flight, so a long shot arrives slow
                    if (wd.burn <= 0f || age[row] < wd.burn)
                        speed = MathF.Min(wd.maxvel, speed + wd.thrust * dt);
                    else
                        speed = MathF.Max(0f, speed - wd.drag * speed * speed * dt);
                    if (has_aim)
                    {
                        var v = _steer(new Vec3(vx, vy, vz),
                                       new Vec3(ax - p0.x, ay - p0.y, az - p0.z),
                                       speed, 1.35f, dt);
                        vx = v.x; vy = v.y; vz = v.z;
                        // thrust misalignment / crosswind: a slow push square to
                        // the sight line, seeded per round, that the guidance has
                        // to chase down.  Bounded because the beam always wins.
                        float wob = wd.wob;
                        float side = MathF.Sqrt(d.x * d.x + d.z * d.z);
                        if (wob != 0f && side > 1e-3f)
                        {
                            float s = seed[row];                    // random per round
                            float f = wd.wob_f * (0.75f + 0.5f * s);
                            float ph = s * MathUtil.TAU;
                            float am = wob * (0.7f + 0.6f * ((s * 3.7f) % 1f))
                                       * (MathF.Sin(age[row] * f * 2.1f + ph)
                                          + 0.55f * MathF.Sin(age[row] * f * 5.3f
                                                              + ph * 2.3f));
                            vx -= d.z / side * am * dt;
                            vz += d.x / side * am * dt;
                        }
                    }
                    else
                    {
                        vx = sp.x * speed; vy = sp.y * speed; vz = sp.z * speed;
                    }
                    _move(world, g, row, p0, new Vec3(vx, vy, vz), dt);
                }
            }
        }
    }

    /// <summary>The infrared round: the seeker owns the lock and hands back the
    /// point to dive on, and the airframe flies to it.
    ///
    /// `Seeker.track` refreshes the stare point off the live hull while it can see
    /// it, keeps the memory walking at the hull's last velocity when it cannot, and
    /// takes any enemy hull it finds inside its cone.  What comes back is a *point*,
    /// so the airframe below cannot tell a live hull from a memory and does not need
    /// to; a round that never had a lock gets nothing and is a rock.</summary>
    public static class Top
    {
        public static void run(World world, float dt)
        {
            var e = world.ecs;
            foreach (var g in e.groups("pos", "top_attack"))
            {
                var posb = g.f("pos");
                var velb = g.f("vel");
                var age = g.f("proj_age");
                var trav = g.f("proj_trav");
                var wdv = g.u8("proj_wid");
                for (int row = 0; row < g.n; row++)
                {
                    int pid = g.ids[row];
                    if (!_begin(g, row)) continue;
                    var wd = Defs.WEAPONS[wdv[row]];
                    var p0 = new Vec3(posb[row * 3], posb[row * 3 + 1], posb[row * 3 + 2]);
                    var vel = new Vec3(velb[row * 3], velb[row * 3 + 1], velb[row * 3 + 2]);
                    var spot = Seeker.track(world, pid, p0, dt);
                    Vec3? tvel = null;
                    if (spot is not null) tvel = e.fv("tgt_vel").get3(pid);
                    // the airframe is stepped from the age it had *before* this
                    // tick's countdown, i.e. the state the last step produced
                    var v = Flight.step(p0, vel, age[row] - dt, trav[row], spot, tvel, wd, dt);
                    _move(world, g, row, p0, v, dt);
                }
            }
        }
    }

    // ------------------------------------------------------------------
    // the shared halves
    // ------------------------------------------------------------------

    /// <summary>Re-arm the record and publish where this tick's leg starts.
    ///
    /// False means the round was condemned before it flew (`Expire` wrote a
    /// contact at t=0 this tick) and holds still where it stands - the same place
    /// the old integrator checked its battery, before moving.</summary>
    private static bool _begin(Group g, int row)
    {
        var hit = g.f("first_hit_t");
        if (hit[row] <= 1f) return false;                // condemned: do not fly
        hit[row] = Defs.FIRST_HIT_ARMED;                 // this tick's record is fresh
        var posb = g.f("pos");
        var prevb = g.f("proj_prev");
        prevb[row * 3] = posb[row * 3];
        prevb[row * 3 + 1] = posb[row * 3 + 1];
        prevb[row * 3 + 2] = posb[row * 3 + 2];
        return true;
    }

    /// <summary>Integrate one round over the tick and publish the leg it covered
    /// for the `FirstHit` systems.</summary>
    private static void _move(World world, Group g, int row, Vec3 p0, Vec3 v, float dt)
    {
        var velb = g.f("vel");
        velb[row * 3] = v.x; velb[row * 3 + 1] = v.y; velb[row * 3 + 2] = v.z;
        var p1 = new Vec3(p0.x + v.x * dt, p0.y + v.y * dt, p0.z + v.z * dt);
        var posb = g.f("pos");
        posb[row * 3] = p1.x; posb[row * 3 + 1] = p1.y; posb[row * 3 + 2] = p1.z;
        g.f("proj_trav")[row] += MathF.Sqrt(v.x * v.x + v.y * v.y + v.z * v.z) * dt;
        world.sweep.add(g.ids[row], p0, p1);
    }

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
}
