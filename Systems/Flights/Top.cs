// The infrared round: the seeker owns the lock and hands back the point to dive on,
// and the arc law turns it into a demand the wing answers.
//
// `Seeker.track` refreshes the stare point off the live hull while it can see it,
// keeps the memory walking at the hull's last velocity when it cannot, and takes any
// enemy hull it finds inside its cone.  What comes back is a *point*, so the law below
// cannot tell a live hull from a memory and does not need to; a round that never had a
// lock gets nothing and is a rock.
//
// The law is three phases and one aim:
//
//   * BOOST    (motor lit)  the reference arc's slope, capped by the tube's own
//                           quadrant (`loft`): a stable climb angle, all the way up
//                           until the motor burns out;
//   * GLIDE    (motor out)  the same arc, unpowered.  The far half of the arc is the
//                           dive, so the round reaches the target already pointed down
//                           `loft`-ish and never has to make a hard turn where it has
//                           neither speed nor room;
//   * TERMINAL (inside `pn_r`, and fast enough for a fin to bite)  true proportional
//                           navigation on the roof point, both planes at once.
//
// Horizontally the round is a pure pursuit in every phase: it points at where the
// target is now, and only the last `pn_r` metres solve a lead.  Firing at a hull a few
// hundred metres away is the case the arc cannot help with - the tube is still elevated
// at `loft`, so the guidance demands a *downward* lift and the round pushes its nose
// under the velocity (negative angle of attack, thrust helping it down, drag against
// the pull).

namespace Mech.Server.Systems;

public static partial class Flights
{
    public static partial class Guidance
    {
        public static class Top
        {
            private const float K_H = 0.6f;        // height-error gain of the arc tracker
            private const float K_P = 3f;          // heading-error gain of the turn (1/s)
            private const float ELEV_MAX = 1.2f;   // rad, the tracker's own clamp
            private const float MIN_REF = 60f;     // m, floor on the height-error look-ahead

            public static void run(World world, float dt)
            {
                var e = world.ecs;
                foreach (var g in e.groups(TopColumns.OnTop))
                {
                    var c = new TopColumns(g);
                    foreach (var r in c)
                    {
                        if (!flying(r.hit_t)) continue;
                        var wd = Defs.WEAPONS[r.wid];
                        var p0 = r.pos;
                        var spot = Seeker.track(world, r.eid, p0, dt);
                        Vec3? tvel = null;
                        if (spot is not null) tvel = c.tvel_at(r.index);
                        // the age is the one the last step produced, since `Expire`
                        // has already counted this tick's second
                        var (dir, mag) = _demand(p0, r.vel, r.age - dt, r.trav, spot, tvel,
                                                 wd);
                        set3(c.adir, r.index, dir);
                        c.awant[r.index] = mag;
                    }
                }
            }

            /// <summary>The arc law's demand: the reference parabola while the round is
            /// far out, proportional navigation inside `pn_r`.
            ///
            /// `tgt_pos` is the *point* the seeker is staring at - the deck of the hull,
            /// or where that deck should be now that the hull is not visible - and
            /// `tgt_vel` the velocity it is drifting at.  Both are null only for a round
            /// that never had a lock: guidance stops and it is a rock with a warhead.
            /// A stale stare point is a point like any other, which is what keeps the
            /// guidance (and the dive) the same through a lost lock as through a held
            /// one.</summary>
            private static (Vec3 dir, float mag) _demand(Vec3 p, Vec3 v, float age,
                                                         float trav, Vec3? tgt_pos,
                                                         Vec3? tgt_vel, WeaponDef wd)
            {
                var (vhat, q) = Flight.airflow(v);
                float speed = v.len;
                float lift_max = Flight.lift_accel(wd, q, wd.cl_max);
                var a_dir = Vec3.zero;
                float a_want = 0f;
                if (tgt_pos is null) return (a_dir, a_want);
                var tp = tgt_pos.Value;
                float dx = tp.x - p.x;
                float dz = tp.z - p.z;
                float r = MathF.Sqrt(dx * dx + dz * dz);
                float above = p.y - tp.y;
                float r0 = MathF.Max(trav + r, 1f);
                if (r <= wd.pn_r && speed >= wd.pn_v)
                {
                    var a = _pn_accel(p, v, tp, tgt_vel ?? Vec3.zero, wd.pn_n);
                    // the fins can only pull square to the flight path
                    a = a - vhat * a.dot(vhat);
                    a_want = a.len;
                    a_dir = a_want > 1e-9f ? a.unit : Vec3.zero;
                }
                else
                {
                    float elev = _arc(r, above, r0, wd);
                    if (age <= wd.ignite + wd.burn)
                    {
                        // the motor is still lit: the tube's quadrant is a ceiling on
                        // the climb, so a close target pulls the nose down here
                        elev = MathF.Min(elev, wd.loft);
                    }
                    float hx, hz;
                    if (r > 1e-3f)
                    {
                        hx = dx / r;
                        hz = dz / r;
                    }
                    else
                    {
                        hx = 1f;
                        hz = 0f;
                    }
                    float ce = MathF.Cos(elev), se = MathF.Sin(elev);
                    var want = new Vec3(hx * ce, se, hz * ce);
                    a_dir = _lift_dir(vhat, want);
                    // the pull owed is proportional to the heading error and capped by
                    // what the airframe can make at this speed: the round crabs onto
                    // the arc rather than snapping onto it
                    float beta = MathF.Asin(MathF.Min(1f, vhat.cross(want).len));
                    a_want = K_P * speed * beta;
                }
                return (a_dir, MathF.Min(a_want, lift_max));
            }

            /// <summary>Commanded flight-path angle of the reference arc, radians.
            ///
            /// The reference is a parabola over the launch range: it climbs at
            /// `atan(4H/R0)` off the rail and comes down at the same angle onto the
            /// target, where `R0` is the range to go plus the distance already flown
            /// (so it equals the range at the shot and breathes with a moving target).
            /// One number therefore sets both the climb and the attack angle, and
            /// `loft` is that number: `H` is capped so the arc's climb equals the
            /// tube's own quadrant.</summary>
            private static float _arc(float r, float above, float r0, WeaponDef wd)
            {
                float u = MathF.Min(1f, MathF.Max(0f, r / r0));
                // apex: what the shot asks for, what the airframe can turn out of, and
                // enough height that the motor can lift it off the ground at all
                // (thrust/weight 11.2 -> below ~6 deg of climb the round sinks)
                float h = MathF.Min(wd.apex, 0.25f * MathF.Tan(wd.loft) * r0);
                h = MathF.Min(h, r0 * r0 / (8f * wd.turn_r));
                h = MathF.Max(0.045f * r0, h);
                float slope = 4f * h * (1f - 2f * u) / r0;  // dh/dr, flight direction +
                // the tracker: the arc's local slope, corrected toward the reference
                // height so the round converges onto the arc instead of drifting
                // parallel to it
                float want = MathF.Atan(-slope)
                             + K_H * MathF.Atan2(4f * h * u * (1f - u) - above,
                                                 MathF.Max(r, MIN_REF));
                return MathF.Max(-ELEV_MAX, MathF.Min(ELEV_MAX, want));
            }

            /// <summary>Unit direction square to the velocity that turns it toward
            /// `want`.</summary>
            private static Vec3 _lift_dir(Vec3 vhat, Vec3 want)
            {
                var axis = vhat.cross(want);
                if (axis.len < 1e-6f) return Vec3.zero;
                return axis.unit.cross(vhat).unit;
            }

            /// <summary>True proportional navigation toward `roof`: the acceleration
            /// owed.</summary>
            private static Vec3 _pn_accel(Vec3 p, Vec3 v, Vec3 roof, Vec3 tgt_vel,
                                          float n_law)
            {
                var r = roof - p;
                float r2 = r.len2;
                if (r2 < 1e-6f) return Vec3.zero;
                var rhat = r.unit;
                var vrel = tgt_vel - v;
                var omega = r.cross(vrel) / r2;
                float vc = -rhat.dot(vrel);
                var a = omega.cross(rhat);
                float k = n_law * MathF.Max(vc, 1f);
                return a * k;
            }
        }
    }
}
