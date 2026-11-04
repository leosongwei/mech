// AI for everything that is not player controlled.
//
// Units acquire targets on a slow timer (0.45 s, LOS validated), engage with lead
// & ballistic compensation and otherwise push along their lane towards the enemy
// command base.
//
// Ported one for one from `backend/server/systems/ai.py`.
//
// **The sight leads, the machine follows.**  A crew lays the *sight* on the
// intercept and pulls the trigger only once the *bore* is on it
// (`Systems/Gunnery.cs` does the slewing); a hull still coming round would
// otherwise hose the dirt.
//
// Everything is indexed by row off one `ecs.slot()` lookup, the way the Python
// loop does it, because the component set of a row (has `arm_hp`? has `raider`?)
// is exactly what the branches below test.

namespace Mech.Server.Systems;

public static class Ai
{
    // weapon data as arrays, by `wid`: the radar cross-section, the collision
    // sphere and the fuse radius.  A round only ever gets a table row through
    // `world.interceptable()`, so a bullet's zeros are never read.
    public static readonly float[] RCS = Defs.WEAPONS.Select(w => w.rcs).ToArray();
    public static readonly float[] HIT_R = Defs.WEAPONS.Select(w => w.hit_r).ToArray();
    public static readonly float[] PROX = Defs.WEAPONS.Select(w => w.prox).ToArray();

    public const float ACQUIRE = 0.45f;
    public const float SPOT_MEMORY = 4.0f;   // s an AI keeps firing at a lost target's spot

    // ---- missile defence ----------------------------------------------------
    // A hull with a radar (`Defs.UNIT_DEFS[...].radar_strength`) sees a missile
    // coming long before it is a danger, and the crew's first reaction is to shoot
    // at it - but only with a weapon that can actually bring it down, i.e. one
    // whose fuse (`prox`) covers the miss distance a burst of cannon fire will
    // have.  The rule is data driven on purpose: a new fused weapon joins in with
    // no change here, and a 12.7 mm carrier (2.25 mrad of spread, no fuse) stays
    // out of it.
    public const float INTERCEPT_RANGE = 1500.0f;  // m: inside the gun's reach it is worth the ammo
    public const float CPA_HIT = 40.0f;            // m: closer than this and the round is coming for me
    // m: a guided round with no lock flies at the point it last saw its target,
    // and that point is inside this much of the hull it is still coming for
    // (`_threat`)
    public const float CPA_MEM = 100.0f;

    // the fan a walking hull tries when the step ahead of it is blocked
    private static readonly float[] FAN = { 0.5f, -0.5f, 1.0f, -1.0f, 1.6f, -1.6f, 2.2f, -2.2f };

    /// <summary>Drive every AI crew of the world.
    ///
    /// The two-argument form is what `World._build_pipeline` registers (a method
    /// group cannot carry the optional `hulls`), and it is the same call.</summary>
    public static void run(World world, float dt) => run(world, dt, null);

    /// <summary>Drive every AI crew of the world - or only the ones in `hulls`.
    ///
    /// The filter is what a mode whose fixtures must stay passive uses: the range
    /// runs the ground AI for the hulls a pilot dropped there and for nothing else
    /// (`Modes/Range/TestAi.cs`), while its targets and its defence post are
    /// driven by their own systems.
    /// </summary>
    public static void run(World world, float dt, int[]? hulls)
    {
        var e = world.ecs;
        // player and AI hulls alike, civilians included
        var ids = hulls ?? world.movable();
        if (ids.Length == 0) return;

        // the two views the per-entity loop below needs by id: everything else it
        // takes from `ecs.slot()` and indexes by row
        var pos = e.fv("pos");
        var vel = e.fv("vel");
        var faction = e.uv("faction");

        // who may be shot at is a query, not a type switch: `combat` hulls only
        // (wrecks, corpses, civilians and smoke are not in here by construction).
        // One id-indexed array rather than a list per faction: `_acquire` reads it
        // for the cells around the shooter, not to filter a roster.  Built from the
        // archetypes so it costs no per-id gather.
        var combat_fac = new sbyte[e.alive.Length];
        Array.Fill(combat_fac, (sbyte)-1);
        foreach (var g in e.groups("combat", "faction"))
        {
            var gf = g.u8("faction");
            var gids = g.ids;
            for (int r = 0; r < g.n; r++) combat_fac[gids[r]] = (sbyte)gf[r];
        }

        // the rounds in the air, once: any crew with a radar looks this over
        // before it goes back to its ground fight
        var mis = _missiles(world);

        // per entity: one `slot` lookup, then every component is `a.f("x")[row]`.
        for (int k = 0; k < ids.Length; k++)
        {
            int eid = ids[k];
            var slot = e.slot(eid);
            if (!slot.ok) continue;
            var b = slot.a;
            int row = slot.row;
            if (b.has("owner") && b.i("owner")[row] >= 0) continue;   // player driven
            if (!b.has("combat"))
            {
                _civ(world, b, row, dt);
                continue;
            }

            int t = b.u8("etype")[row];
            int fac = b.u8("faction")[row];
            var d = Defs.UNIT_DEFS[t];
            float speed = d.speed;
            var bp = b.f("pos");
            int o = row * 3;
            float px = bp[o], py = bp[o + 1], pz = bp[o + 2];
            // a mech looks through its sight, on the roof, not out of its chest
            var eye = t == Defs.ETYPE_MECH
                ? MathUtil.sight_eye(new Vec3(px, py, pz), b.f("yaw")[row])
                : new Vec3(px, py + d.height * 0.75f, pz);

            var timer = b.f("ai_timer");
            var target = b.i("target");
            var spot_t = b.f("spot_t");
            timer[row] -= dt;
            int tid = target[row];
            if (timer[row] <= 0f)
            {
                timer[row] = ACQUIRE * world.rng.uniform(0.75f, 1.35f);
                tid = _acquire(world, fac, combat_fac, eye, d);
                target[row] = tid;
            }
            if (tid >= 0 && (!e.is_alive(tid) || faction[tid] == fac))
            {
                target[row] = -1;
                tid = -1;
            }

            // a round that is going to hit this hull outranks whatever it was
            // shooting at: the crew turns the gun on it and keeps driving
            bool covering = mis != null && _missile_step(world, eid, b, row, mis, dt);

            bool moving = true;
            bool seen = false;
            if (!covering && tid >= 0)
            {
                var tpos = pos.get3(tid);
                var tvel = vel.get3(tid);
                float dist = MathUtil.dist2d(eye.x, eye.z, tpos.x, tpos.z);
                if (dist < d.ai_range &&
                    _sees(world, eye, new Vec3(tpos.x, tpos.y + d.height * 0.6f, tpos.z)))
                {
                    moving = dist > d.ai_range * 0.55f;
                    seen = true;
                    // remember the spot: if the sight breaks (smoke, a crest) the
                    // crew keeps shooting there for a few seconds
                    b.f("spot_x")[row] = tpos.x;
                    b.f("spot_y")[row] = tpos.y;
                    b.f("spot_z")[row] = tpos.z;
                    spot_t[row] = SPOT_MEMORY;
                    _engage(world, eid, b, row, tid, eye, d, dist, tpos, tvel, dt);
                }
            }
            if (!covering && !seen && spot_t[row] > 0f)
            {
                // lost it: hose the spot where it was last seen
                spot_t[row] -= dt;
                float dist = MathUtil.dist2d(eye.x, eye.z,
                                             b.f("spot_x")[row], b.f("spot_z")[row]);
                if (dist < d.ai_range)
                {
                    _suppress(world, eid, b, row, eye, d, dist, dt);
                    moving = false;
                }
            }

            if (moving)
            {
                if (!covering) b.u8("trigger")[row] = 0;   // `_missile_step` owns the trigger
                if (b.has("raider"))
                {
                    // a raiding party has a route of its own, off the lanes: the
                    // mode's raid system (`Modes/Battle/Raids.cs`) writes its
                    // velocity.  Everything above - acquire, lead, fire - it shoots
                    // with the same crew as any other hull.
                }
                else if (b.has("vehicle"))
                {
                    _advance_route(world, b, row, speed);  // a car follows the lane
                }
                else
                {
                    _advance(world, eid, b, row, speed, dt);
                }
            }
            else if (b.has("vehicle"))
            {
                // close enough to shoot: a carrier brakes for that, it does not
                // roll on at the last commanded speed the way a walking hull's
                // leftover velocity does
                var cmd = b.f("vel_cmd");
                cmd[o] = 0f;
                cmd[o + 2] = 0f;
            }
        }
    }

    /// <summary>The rounds in the air that a radar can see, as flat arrays.
    ///
    /// Built once a tick: nothing here is per-shooter, and at 30 Hz a battle has a
    /// few dozen of them at most.  `rcs` and the fuse radius come off the weapon
    /// table by `wid`, so a mission that fits a new missile needs no edit here.
    /// </summary>
    private sealed class Missiles
    {
        public int[] ids = Array.Empty<int>();
        public float[] pos = Array.Empty<float>();     // n*3
        public float[] vel = Array.Empty<float>();     // n*3
        public int[] fac = Array.Empty<int>();
        public int[] kind = Array.Empty<int>();
        public int[] target = Array.Empty<int>();
        // where an unlocked round is staring: its target's last known position,
        // still walking at the target's last known velocity (`Core/Seeker.cs`)
        public float[] mem = Array.Empty<float>();     // n*3
        public float[] rcs = Array.Empty<float>();
        public float[] hit_r = Array.Empty<float>();
        public float[] prox = Array.Empty<float>();
    }

    private static Missiles? _missiles(World world)
    {
        var e = world.ecs;
        var ids = world.interceptable();
        if (ids.Length == 0) return null;
        int n = ids.Length;
        var m = new Missiles { ids = ids };
        m.pos = new float[n * 3];
        e.fv("pos").get(ids, m.pos);
        m.vel = new float[n * 3];
        e.fv("vel").get(ids, m.vel);
        m.mem = new float[n * 3];
        e.fv("tgt_mem").get(ids, m.mem);
        m.fac = _u8_by_id(e, "faction", ids);
        m.kind = _u8_by_id(e, "proj_kind", ids);
        m.target = _int_by_id(e, "target", ids);
        var wid = _u8_by_id(e, "proj_wid", ids);
        m.rcs = new float[n];
        m.hit_r = new float[n];
        m.prox = new float[n];
        for (int k = 0; k < n; k++)
        {
            int w = wid[k];
            m.rcs[k] = RCS[w];
            m.hit_r[k] = HIT_R[w];
            m.prox[k] = PROX[w];
        }
        return m;
    }

    // a `byte` component read for a batch of ids, widened to `int`: the tables
    // above index by it and the comparisons are against plain ints
    private static int[] _u8_by_id(ECS e, string name, int[] ids)
    {
        var tmp = new byte[ids.Length];
        var dst = new int[ids.Length];
        e.uv(name).get(ids, 0, tmp);
        for (int k = 0; k < ids.Length; k++) dst[k] = tmp[k];
        return dst;
    }

    private static int[] _int_by_id(ECS e, string name, int[] ids)
    {
        var dst = new int[ids.Length];
        e.iv(name).get(ids, 0, dst);
        return dst;
    }

    /// <summary>Missile defence on its own, for a mode whose hulls do not fight.
    ///
    /// `run` calls the same step inline; this is the version a test range wants -
    /// a single AI hull that shoots down what is fired at it and never picks a
    /// ground fight.
    ///
    /// The trigger is cleared here, because in a mode without `run` nothing else
    /// owns it - and it is cleared *before* the empty-sky test, not after: a round
    /// shot down last tick leaves the gun laid on the patch of air it came apart
    /// in, and a trigger that is never dropped would go on firing at it.
    /// </summary>
    public static void intercept(World world, float dt)
    {
        var mis = _missiles(world);
        var e = world.ecs;
        foreach (var eid in world.movable())
        {
            var slot = e.slot(eid);
            if (!slot.ok) continue;
            var b = slot.a;
            int row = slot.row;
            if (!b.has("owner") || b.i("owner")[row] >= 0) continue;   // player driven
            if (!b.has("combat")) continue;
            b.u8("trigger")[row] = 0;
            if (mis != null) _missile_step(world, eid, b, row, mis, dt);
        }
    }

    /// <summary>Turn the gun on the round that is coming, if there is one.  True if
    /// the crew took the shot - the caller then leaves its ground target alone.</summary>
    private static bool _missile_step(World world, int eid, Archetype b, int row,
                                      Missiles mis, float dt)
    {
        var d = Defs.UNIT_DEFS[b.u8("etype")[row]];
        float strength = d.radar_strength;
        if (strength <= 0f) return false;
        int arm = _fused_arm(b, row);
        if (arm < 0) return false;              // nothing aboard can down it
        int etype = b.u8("etype")[row];
        var bp = b.f("pos");
        int o = row * 3;
        float px = bp[o], py = bp[o + 1], pz = bp[o + 2];
        var eye = etype == Defs.ETYPE_MECH
            ? MathUtil.sight_eye(new Vec3(px, py, pz), b.f("yaw")[row])
            : new Vec3(px, py + d.height * 0.75f, pz);
        int k = _threat(world, mis, strength, eye, eid,
                        new Vec3(b.f("vel")[o], b.f("vel")[o + 1], b.f("vel")[o + 2]),
                        b.u8("faction")[row]);
        if (k < 0) return false;
        var tpos = new Vec3(mis.pos[k * 3], mis.pos[k * 3 + 1], mis.pos[k * 3 + 2]);
        var tvel = new Vec3(mis.vel[k * 3], mis.vel[k * 3 + 1], mis.vel[k * 3 + 2]);
        float dist = MathUtil.dist(eye, tpos);
        var wd = Defs.WEAPONS[b.u8("weapon")[row * 2 + arm]];
        // lead the intercept exactly as a hull is led: the same solver, the same
        // "fire control zeroes the drop at shot time" (so no lift here)
        var aim = MathUtil.solve_lead(eye, tpos, tvel, wd.vel, 0f);
        var (want_yaw, want_pitch) = MathUtil.angles_to(aim.x - eye.x, aim.y - eye.y,
                                                        aim.z - eye.z);
        var trigger = b.u8("trigger");
        float aim_yaw;
        bool laid;
        if (etype == Defs.ETYPE_MECH)
        {
            want_pitch = MathF.Max(Defs.MECH_PITCH_MIN, MathF.Min(Defs.MECH_PITCH_MAX, want_pitch));
            b.f("sight_yaw")[row] = want_yaw;
            b.f("sight_pitch")[row] = want_pitch;
            aim_yaw = b.f("yaw")[row];
            laid = MathF.Abs(want_pitch - b.f("pitch")[row]) < 0.02f;
        }
        else if (etype == Defs.ETYPE_APC)
        {
            b.f("turret_yaw")[row] = MathUtil.turn_toward(b.f("turret_yaw")[row],
                                                           want_yaw, 2.2f * dt);
            b.f("pitch")[row] = want_pitch;
            aim_yaw = b.f("turret_yaw")[row];
            laid = true;
        }
        else
        {
            b.f("yaw")[row] = MathUtil.turn_toward(b.f("yaw")[row], want_yaw, 3.2f * dt);
            b.f("pitch")[row] = want_pitch;
            aim_yaw = b.f("yaw")[row];
            laid = true;
        }
        // a burst weapon is aimed at an *area*, not a point: the tolerance is the
        // angle the fuse covers at this range, ~0.004 rad at 1 km, not the 0.14 rad
        // a hull gets (a shell that misses a hull's 1.45 m body by a metre still
        // counts, and a round that misses a missile by a metre is a miss)
        float tol = MathF.Atan((wd.prox + mis.hit_r[k]) / MathF.Max(dist, 1f));
        float err = MathF.Abs(MathUtil.wrap_pi(want_yaw - aim_yaw));
        trigger[row] = (byte)(err < tol && laid ? 1 << arm : 0);
        return true;
    }

    /// <summary>Which arm can bring a missile down: the first that carries a fuse
    /// and still has rounds and metal.  -1 if none can.</summary>
    private static int _fused_arm(Archetype b, int row)
    {
        var weapon = b.u8("weapon");
        var mag = b.i("mag");
        var arm_hp = b.has("arm_hp") ? b.f("arm_hp") : null;
        for (int arm = 0; arm < 2; arm++)
        {
            int wid = weapon[row * 2 + arm];
            if (wid == 0 || PROX[wid] <= 0f) continue;
            if (arm_hp != null && arm_hp[row * 2 + arm] <= 0f) continue;
            if (mag[row * 2 + arm] <= 0) continue;
            return arm;
        }
        return -1;
    }

    /// <summary>Index of the round worth shooting at in `mis`, or -1.
    ///
    /// Two questions, both of them `plan.md`'s:
    ///
    /// * **can I see it** - `d^2 <= K * v_closing` with `K = sqrt(S * rcs) / V_REF`
    ///   (`Defs.radar_k`).  A round that is opening out or crossing gives a
    ///   non-positive closing speed, so the right-hand side is <= 0 and the test
    ///   fails by itself: no separate "is it getting closer" clause, and no
    ///   `RADAR_SPEED` threshold.
    /// * **is it for me** - a top-attack round carries the id of the hull its
    ///   seeker locked, so it is asked directly; everything else is judged by
    ///   geometry, the closest point of its straight-line approach.  A `t_cpa` gate
    ///   would say nothing: the moment a round is first *seen* it is 1.1-2.6 s from
    ///   its closest approach whatever it is doing, so any threshold either always
    ///   passes or throws away the genuinely dangerous ones.  A guided round whose
    ///   lock has been cut has no id to ask, but it is not harmless - it is flying
    ///   at its memory, and that memory is a point near this hull or it is not
    ///   (`CPA_MEM`), so the crew keeps shooting at the screen it is coming through
    ///   instead of letting go the moment the smoke goes up.
    /// </summary>
    private static int _threat(World world, Missiles mis, float strength, Vec3 eye,
                               int eid, Vec3 ovel, int fac)
    {
        int bestK = -1;
        float best = 0f;
        for (int k = 0; k < mis.ids.Length; k++)
        {
            float rx = mis.pos[k * 3] - eye.x;
            float ry = mis.pos[k * 3 + 1] - eye.y;
            float rz = mis.pos[k * 3 + 2] - eye.z;
            float vrx = mis.vel[k * 3] - ovel.x;
            float vry = mis.vel[k * 3 + 1] - ovel.y;
            float vrz = mis.vel[k * 3 + 2] - ovel.z;
            float d2 = rx * rx + ry * ry + rz * rz;
            float vc = -(rx * vrx + ry * vry + rz * vrz) / MathF.Sqrt(MathF.Max(d2, 1e-9f));
            float kk = MathF.Sqrt(MathF.Max(strength * mis.rcs[k], 0f)) / Defs.RADAR_V_REF;
            if (!(d2 <= kk * vc)) continue;                   // not seen by this radar
            if (mis.fac[k] == fac) continue;                  // a friendly round
            if (!(d2 <= INTERCEPT_RANGE * INTERCEPT_RANGE)) continue;
            float w2 = MathF.Max(vrx * vrx + vry * vry + vrz * vrz, 1e-9f);
            float tcpa = -(rx * vrx + ry * vry + rz * vrz) / w2;
            if (tcpa < 0f) tcpa = 0f;
            float cx = rx + vrx * tcpa;
            float cy = ry + vry * tcpa;
            float cz = rz + vrz * tcpa;
            float cpa = MathF.Sqrt(cx * cx + cy * cy + cz * cz);
            // top attack: it climbs 150 m over its victim and dives on the roof, so
            // its straight line passes nowhere near him - the seeker's own lock is
            // the truth, and with the lock cut, the point it is still staring at is
            // the other half of the same answer
            float mx = mis.mem[k * 3] - eye.x;
            float my = mis.mem[k * 3 + 1] - eye.y;
            float mz = mis.mem[k * 3 + 2] - eye.z;
            bool mine = mis.target[k] == eid
                        || MathF.Sqrt(mx * mx + my * my + mz * mz) < CPA_MEM;
            if (!(mis.kind[k] == 3 ? mine : cpa < CPA_HIT)) continue;
            if (bestK < 0 || d2 < best)
            {
                best = d2;
                bestK = k;
            }
        }
        return bestK;
    }

    /// <summary>Line of sight: whatever the shared sight rule says (ground,
    /// buildings, tree canopies and smoke).</summary>
    private static bool _sees(World world, Vec3 eye, Vec3 point) =>
        Sight.clear(world, eye, point);

    /// <summary>Nearest hostile unit with line of sight.
    ///
    /// A hull may declare a restricted target list in its definition (`targets`):
    /// a rifleman only picks a fight with other infantry and leaves armour alone,
    /// because 15 hp in the open is not a threat to a tank and the crew knows it.
    ///
    /// The candidates come from the grid, not from a roster: a 5000-unit battle
    /// puts a few dozen ids in the box around the shooter instead of a few thousand
    /// in the array to sort.  The box covers the whole sight radius, so the answer
    /// is the same one the full scan gave.  No position is gathered here - the grid
    /// carries its own copy, in the same order as its ids.
    /// </summary>
    private static int _acquire(World world, int fac, sbyte[] combat_fac, Vec3 eye,
                                UnitDef d)
    {
        var grid = world.grid;
        var (ci, cj) = grid.cell_of(eye.x, eye.z);
        var (ids, p) = grid.box(ci, cj, (int)(d.sight * grid.inv) + 1);
        if (ids.Length == 0) return -1;
        var etype = world.ecs.uv("etype");
        var want = d.targets;
        var cand = new Cand[ids.Length];
        int n = 0;
        for (int k = 0; k < ids.Length; k++)
        {
            int id = ids[k];
            sbyte cf = combat_fac[id];
            if (cf < 0 || cf == fac) continue;
            if (want != null && Array.IndexOf(want, (int)etype[id]) < 0) continue;
            float dx = p[k * 3] - eye.x;
            float dz = p[k * 3 + 2] - eye.z;
            float ds = MathF.Sqrt(dx * dx + dz * dz);
            if (!(ds <= d.sight)) continue;
            cand[n].id = id;
            cand[n].x = p[k * 3];
            cand[n].y = p[k * 3 + 1];
            cand[n].z = p[k * 3 + 2];
            cand[n].ds = ds;
            n += 1;
        }
        if (n == 0) return -1;
        // `np.argsort(ds)[:6]`: nearest first, and only the six nearest are worth a
        // sight cast
        Array.Sort(cand, 0, n);
        int take = Math.Min(6, n);
        for (int k = 0; k < take; k++)
        {
            var tp = cand[k];
            if (_sees(world, eye, new Vec3(tp.x, tp.y + 1.5f, tp.z))) return tp.id;
        }
        return -1;
    }

    /// <summary>One candidate for `_acquire`: the grid's own copy of the position
    /// (the same numbers its `ids` are filed with), ordered by plan-view distance.</summary>
    private struct Cand : IComparable<Cand>
    {
        public int id;
        public float x, y, z;
        public float ds;

        public int CompareTo(Cand other) => ds.CompareTo(other.ds);
    }

    private static void _engage(World world, int eid, Archetype b, int row, int tid,
                                Vec3 eye, UnitDef d, float dist, Vec3 tpos, Vec3 tvel,
                                float dt)
    {
        // aim at the intercept point of the *slowest round that can reach*.  The
        // sight is one line whatever the arms carry, and a guided round is aimed
        // with it (a beam rider rides it), so leading for a 1080 m/s cannon shell
        // while the right arm is a missile that takes seconds to arrive put the
        // missile consistently short of a moving hull.  No gravity lift here: the
        // fire control zeroes the gun for the drop at shot time, so lifting the aim
        // as well would double compensate
        var weapon = b.u8("weapon");
        float speed = 0f;
        for (int arm = 0; arm < 2; arm++)
        {
            int wid = weapon[row * 2 + arm];
            if (wid == 0) continue;
            var wd = Defs.WEAPONS[wid];
            if (dist > wd.range) continue;
            float v = MathUtil.mean_speed(wd, dist);
            speed = speed <= 0f ? v : MathF.Min(speed, v);
        }
        if (speed <= 0f)
        {
            int w0 = weapon[row * 2];
            speed = Defs.WEAPONS[w0 != 0 ? w0 : Defs.WID_AC30].vel;
        }
        var aim = MathUtil.solve_lead(eye, new Vec3(tpos.x, tpos.y + 2.0f, tpos.z),
                                      tvel, speed, 0f);
        _aim_and_fire(world, eid, b, row, eye, d, dist, aim, dt);
    }

    /// <summary>Keep firing at the spot where the target was last seen.</summary>
    private static void _suppress(World world, int eid, Archetype b, int row, Vec3 eye,
                                 UnitDef d, float dist, float dt)
    {
        var aim = new Vec3(b.f("spot_x")[row], b.f("spot_y")[row] + 1.5f,
                           b.f("spot_z")[row]);
        _aim_and_fire(world, eid, b, row, eye, d, dist, aim, dt);
    }

    private static void _aim_and_fire(World world, int eid, Archetype b, int row,
                                      Vec3 eye, UnitDef d, float dist, Vec3 aim,
                                      float dt)
    {
        var yawb = b.f("yaw");
        var pitchb = b.f("pitch");
        var turret = b.f("turret_yaw");
        var trigger = b.u8("trigger");
        int etype = b.u8("etype")[row];
        var hp = b.f("hp");
        var arm_hp = b.has("arm_hp") ? b.f("arm_hp") : null;
        var weapon = b.u8("weapon");

        var (want_yaw, want_pitch) = MathUtil.angles_to(aim.x - eye.x, aim.y - eye.y,
                                                       aim.z - eye.z);

        if (etype == Defs.ETYPE_MECH)
        {
            // the crew lays the sight on the intercept; the hull and the arms slew
            // after it at their own rates (`Systems/Gunnery.cs`), which is why the
            // trigger below waits for the *bore*, not for the sight.  A shot the
            // mounts cannot elevate to is one they never take: hold the sight at the
            // stop instead of driving the arms against it for ever
            want_pitch = MathF.Max(Defs.MECH_PITCH_MIN, MathF.Min(Defs.MECH_PITCH_MAX, want_pitch));
            b.f("sight_yaw")[row] = want_yaw;
            b.f("sight_pitch")[row] = want_pitch;
        }
        else if (etype == Defs.ETYPE_APC)
        {
            // the hull of a wheeled carrier belongs to the driver: movement points it
            // where it rolls, and the crew turns nothing but the turret
            turret[row] = MathUtil.turn_toward(turret[row], want_yaw, 2.2f * dt);
            pitchb[row] = MathF.Max(-0.4f, MathF.Min(0.7f, pitchb[row] +
                (want_pitch - pitchb[row]) * MathF.Min(1f, 6f * dt)));
        }
        else
        {
            // a rifleman turns his whole body onto the target: no sight to lead
            yawb[row] = MathUtil.turn_toward(yawb[row], want_yaw, 3.2f * dt);
            pitchb[row] = MathF.Max(-0.4f, MathF.Min(0.7f, pitchb[row] +
                (want_pitch - pitchb[row]) * MathF.Min(1f, 6f * dt)));
        }

        float aim_yaw = etype == Defs.ETYPE_APC ? turret[row] : yawb[row];
        float err = MathF.Abs(MathUtil.wrap_pi(want_yaw - aim_yaw));
        // a mech's barrels are laid by the gunnery slew, not by this loop: the gun
        // opens up when the bore is actually on the intercept, so a hull still
        // coming round (or an arm still climbing) does not hose the dirt
        bool laid = etype != Defs.ETYPE_MECH
                    || MathF.Abs(want_pitch - pitchb[row]) < 0.02f;
        int t = 0;
        if (err < 0.14f && laid)
        {
            if (hp[row] > 0f) t |= 1;
            // the second arm exists only on a hull that carries arm pools, and only
            // fires while that arm is still there (`Combat.arm_alive`, but the
            // buffers are already in hand here, so no entity lookup)
            bool has_arm = arm_hp != null && arm_hp[row * 2 + 1] > 0f;
            if (has_arm && dist < d.ai_range)
            {
                // the right arm fires whatever it carries, as far as that round
                // reaches: 2.2 km was the rocket nest's hand-off, but a racked
                // launcher or a 105 mm barrel is no less ready at the crew's own
                // engagement range (`ai_range`), so only the two old arms keep it
                int w1 = weapon[row * 2 + 1];
                if (w1 != 0)
                {
                    var wd1 = Defs.WEAPONS[w1];
                    float reach = wd1.kind == "rocket" || wd1.kind == "saclos"
                        ? 2200f : d.ai_range;
                    if (dist < MathF.Min(reach, wd1.range)) t |= 2;
                }
            }
            if (etype == Defs.ETYPE_APC && dist < 3500f) t |= 2;
        }
        // a semi automatic arm only fires on the rising edge of the trigger, so the
        // AI has to let go of it every other tick
        if ((t & 2) != 0 && (world.tick & 1) != 0
            && Defs.WEAPONS[weapon[row * 2 + 1]].semi != 0)
        {
            t &= ~2;
        }
        trigger[row] = (byte)t;
    }

    private static void _advance(World world, int eid, Archetype b, int row, float speed,
                                 float dt)
    {
        var ppos = b.f("pos");
        var cmd = b.f("vel_cmd");
        var terrain = world.terrain;
        int fac = b.u8("faction")[row];
        int lane_i = b.u8("lane")[row];
        float px = ppos[row * 3], pz = ppos[row * 3 + 2];
        var (bx, bz) = terrain.bases[1 - fac];
        float dx, dz;
        if (world.lanes.Count == 0)
        {
            // a map with no lanes at all (the range): there is nothing to follow,
            // so the hull walks straight at the enemy post
            dx = bx - px;
            dz = bz - pz;
        }
        else
        {
            var pts = world.lanes[lane_i % world.lanes.Count];
            if (fac == Defs.FACTION_BLUE) pts = world.lanes_rev[lane_i % world.lanes.Count];
            int i = b.i("path_i")[row];
            if (i >= pts.Count) i = pts.Count - 1;
            var (wx, wz) = pts[i];
            dx = wx - px;
            dz = wz - pz;
            if (dx * dx + dz * dz < 55f * 55f)
            {
                if (i < pts.Count - 1)
                {
                    i += 1;
                    b.i("path_i")[row] = i;
                    (wx, wz) = pts[i];
                    dx = wx - px;
                    dz = wz - pz;
                }
                else if (i >= pts.Count - 1)
                {
                    dx = bx - px;
                    dz = bz - pz;
                }
            }
        }
        float d = MathF.Sqrt(dx * dx + dz * dz);
        if (d < 1e-3f)
        {
            cmd[row * 3] = 0f;
            cmd[row * 3 + 2] = 0f;
            return;
        }
        dx /= d;
        dz /= d;
        // simple obstacle avoidance: fan out when the step ahead is blocked
        const float step = 14f;
        if (terrain.blocked(px + dx * step, pz + dz * step))
        {
            float bese = MathF.Atan2(dz, dx);
            for (int k = 0; k < FAN.Length; k++)
            {
                float a = bese + FAN[k];
                float nx = MathF.Cos(a), nz = MathF.Sin(a);
                if (!terrain.blocked(px + nx * step, pz + nz * step))
                {
                    dx = nx;
                    dz = nz;
                    break;
                }
            }
        }
        cmd[row * 3] = dx * speed;
        cmd[row * 3 + 2] = dz * speed;
    }

    // ---------------------------------------------------------------------------
    // wheeled route following
    // ---------------------------------------------------------------------------
    // A hull with an 11 m turning radius cannot be steered at a point: it has to
    // follow a *line of way points*, and the line the lanes hand it is dense and
    // full of sharp corners.  The rule that makes such a line drivable is the
    // acceptance radius: a way point that comes within `_arrive_radius(speed)` of
    // the hull counts as reached and is dropped, so the hull is already heading for
    // the one after it while it is still tens of metres short of the first.  That
    // is what a driver does at a right angle - aim for the far side and swing
    // across the inside - and it means the hull cheerfully leaves the road on the
    // way through, which is fine: the road is not the constraint, the hull's
    // turning radius is.
    //
    // Locomotion (`Systems/Movement.cs`) turns the aim into throttle and steering;
    // nothing here sets a velocity of its own.

    private static float _seg(List<(float x, float z)> pts, int k)
    {
        var (ax, az) = pts[k];
        var (bx, bz) = pts[k + 1];
        return MathUtil.dist2d(ax, az, bx, bz);
    }

    /// <summary>The fastest a hull can take a corner that turns by `ang` radians.
    ///
    /// How much room the legs give is irrelevant - a kink is a kink however long
    /// the straight either side of it - so the corner is defined by how far the
    /// hull is allowed to cut inside it (`Defs.APC_CUT`, metres).  A fillet of
    /// radius R misses the vertex by `R * (1 / cos(ang / 2) - 1)`, so the cut
    /// budget caps R and the hull's own turning radius floors it; the bend is then
    /// driven at `sqrt(APC_LAT_ACCEL * R)`.  A right angle comes out at ~11 m/s
    /// cutting 8 m off the inside - the turn anyone makes at a street corner - and
    /// a 20 degree kink costs nothing at all, as it should.
    /// </summary>
    private static float _corner_speed(float ang, float l1, float l2, float top)
    {
        if (ang < 1e-3f) return top;
        float over = 1f / MathF.Cos(0.5f * MathF.Min(ang, MathF.PI * 0.999f)) - 1f;
        float radius = over > 1e-6f ? Defs.APC_CUT / over : 1e9f;
        radius = MathF.Max(Defs.APC_TURN_R, radius);
        return MathF.Min(top, MathF.Sqrt(Defs.APC_LAT_ACCEL * radius));
    }

    /// <summary>How fast the hull may be rolling now and still make the corners
    /// ahead.
    ///
    /// Every corner inside `APC_CORNER_HORIZON` sets a ceiling that rises with the
    /// distance to it (`v^2 = v_corner^2 + 2 a d`), so the car brakes coming in and
    /// rolls out of the bend instead of arriving at full speed with a turning
    /// radius that cannot hold it.
    /// </summary>
    private static float _route_speed(List<(float x, float z)> pts, int i, float px,
                                      float pz, float top)
    {
        // a lane shorter than one leg has no corner to look at (the scan starts at
        // index 1, so there has to be one)
        if (pts.Count < 2) return top;
        float limit = top;
        // start at the way point *before* the one being aimed at: the acceptance
        // radius drops a corner as soon as the hull is within ~12 m of it, and the
        // braking for it has to have happened well before that
        int k = Math.Max(1, i - 1);
        float d = MathUtil.dist2d(px, pz, pts[k].x, pts[k].z);   // metres to the corner itself
        while (k + 1 < pts.Count && d < Defs.APC_CORNER_HORIZON)
        {
            var (ax, az) = pts[k - 1];
            var (bx, bz) = pts[k];
            var (cx, cz) = pts[k + 1];
            float ang = MathF.Abs(MathUtil.wrap_pi(MathF.Atan2(cz - bz, cx - bx) -
                                                   MathF.Atan2(bz - az, bx - ax)));
            float vb = _corner_speed(ang, _seg(pts, k - 1), _seg(pts, k), top);
            if (vb < top)
                limit = MathF.Min(limit, MathF.Sqrt(vb * vb + 2f * Defs.APC_BRAKE * d));
            k += 1;
            d += _seg(pts, k - 1);
        }
        return limit;
    }

    /// <summary>How close a way point has to come to count as reached, for this
    /// speed.
    ///
    /// The radius has to grow with the speed: a hull at 100 km/h needs ~30 m of arc
    /// to turn at all, so aiming at a point 10 m away only makes it overshoot and
    /// come back, while a hull crawling through a tight corner can and should
    /// follow the line.  `radius = clamp(speed * time, floor, cap)` is the lookahead
    /// rule of a pure-pursuit tracker, and it keeps the same behaviour at both ends
    /// of the speed range.
    /// </summary>
    private static float _arrive_radius(float speed)
    {
        float r = MathF.Abs(speed) * Defs.APC_ARRIVE_LOOK;
        return MathF.Max(Defs.APC_ARRIVE_MIN, MathF.Min(Defs.APC_ARRIVE_MAX, r));
    }

    /// <summary>True when way point `i` is behind the hull of a given acceptance
    /// radius.
    ///
    /// Either it is inside that radius, or the hull has driven past the line through
    /// it that is square to the leg arriving from the previous point.  The first is
    /// what lets a car carry speed into a corner and cut across the inside of it;
    /// the second is what keeps a car that cut *wide* of a corner - or was shoved
    /// off the line - from turning back to collect it.
    /// </summary>
    private static bool _passed(List<(float x, float z)> pts, int i, float px, float pz,
                               float radius)
    {
        float dx = px - pts[i].x, dz = pz - pts[i].z;
        if (dx * dx + dz * dz <= radius * radius) return true;
        if (i <= 0) return false;
        float lx = pts[i].x - pts[i - 1].x, lz = pts[i].z - pts[i - 1].z;
        return (lx * lx + lz * lz) > 1e-9f && (dx * lx + dz * lz) > 0f;
    }

    /// <summary>Aim a wheeled hull at its next way point - the direction, not the
    /// throttle.</summary>
    private static void _advance_route(World world, Archetype b, int row, float speed)
    {
        var ppos = b.f("pos");
        var cmd = b.f("vel_cmd");
        var pvel = b.f("vel");
        int fac = b.u8("faction")[row];
        float px = ppos[row * 3], pz = ppos[row * 3 + 2];
        int i;
        List<(float x, float z)>? pts;
        if (world.lanes.Count > 0)
        {
            int lane = b.u8("lane")[row] % world.lanes.Count;
            pts = fac == Defs.FACTION_BLUE ? world.lanes_rev[lane] : world.lanes[lane];
            // `path_i` runs to `pts.Count`, one past the end: that is the "lane
            // finished" mark, and it has to be sticky - deciding per tick between
            // the last way point and the base made a hull orbit the circle between
            // them for ever
            i = Math.Max(0, Math.Min(b.i("path_i")[row], pts.Count));
            float radius = _arrive_radius(MathUtil.dist2d(0f, 0f,
                                                          pvel[row * 3], pvel[row * 3 + 2]));
            while (i < pts.Count - 1 && _passed(pts, i, px, pz, radius)) i += 1;
            if (i == pts.Count - 1 && _passed(pts, i, px, pz, radius)) i = pts.Count;
            b.i("path_i")[row] = i;
        }
        else
        {
            // a map with no lanes (the range): the car heads straight for the enemy
            // post, which is where a lane would have led it anyway
            i = 0;
            pts = null;
        }
        float ax, az, v;
        if (pts == null || i >= pts.Count)
        {
            // the end of the lane is the enemy side of the map: the last stretch
            // runs at the command base, which is what a lane actually leads to
            (ax, az) = world.terrain.bases[1 - fac];
            v = speed;
        }
        else
        {
            (ax, az) = pts[i];
            v = _route_speed(pts, i, px, pz, speed);
        }
        float dx = ax - px;
        float dz = az - pz;
        float d = MathF.Sqrt(dx * dx + dz * dz);
        if (d < 1e-3f)
        {
            cmd[row * 3] = 0f;
            cmd[row * 3 + 2] = 0f;
            return;
        }
        cmd[row * 3] = dx / d * v;
        cmd[row * 3 + 2] = dz / d * v;
    }

    private static void _civ(World world, Archetype b, int row, float dt)
    {
        var ppos = b.f("pos");
        var cmd = b.f("vel_cmd");
        var timer = b.f("ai_timer");
        timer[row] -= dt;
        if (timer[row] > 0f) return;
        timer[row] = world.rng.uniform(1.5f, 4.5f);
        float dx = b.f("home_x")[row] - ppos[row * 3];
        float dz = b.f("home_z")[row] - ppos[row * 3 + 2];
        float a;
        if (dx * dx + dz * dz > 240f * 240f) a = MathF.Atan2(dz, dx);
        else a = world.rng.random() * MathUtil.TAU;
        cmd[row * 3] = MathF.Cos(a) * 2f;
        cmd[row * 3 + 2] = MathF.Sin(a) * 2f;
        b.f("yaw")[row] = a;
    }
}
