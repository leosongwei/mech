// The fire control, as one function: where the sight must be laid, and how high
// the barrel must be, for a gun zeroed to `Z` to put its round on a moving target.
//
// This is the law both hosts solve.  The client runs `frontend/src/fcs.js`, which
// is the same formula line for line, and `tests/cases/fcs.json` is the table that
// keeps the two honest (`run_tests.sh`).  It reads data only - a `Shot` (the
// muzzle, the eye, the weapon, the setting) and a `Track` (where the target is,
// what it is doing, how high to hit it) - so it is a pure function of numbers: no
// `World`, no ECS, no RNG.  That is what lets the test suite be deterministic and
// what keeps the answer the same however a host found its inputs.
//
// The shape of the law, in two sentences:
//
//   * the *sight* line is where the crew looks - it carries `yaw` / `pitch` on the
//     wire for a pilot and is what `Systems/Gunnery.cs` swings the hull after;
//   * the *elevation* is where the barrel is laid - the sight line, held off by
//     `Z` metres of zeroing - and that is the angle a round actually leaves on.
//
// So the solution walks two nested loops: an inner one on the sight elevation
// (hold the cross where the round will pass the target's range) and an outer one
// on the intercept point (lead the target by the flight time that solution
// implies).  Both converge in three or four passes, and both are iterations of
// the same integrator the rounds fly.

namespace Mech.Server;

public static class FireControl
{
    /// <summary>Passes on the sight elevation, per pass on the lead.</summary>
    public const int INNER = 4;
    /// <summary>Passes on the lead itself (a missile closing at 600 m/s needs
    /// four: each pass moves the aim point most of the way again).</summary>
    public const int OUTER = 4;
    /// <summary>m; the elevation loop stops once the round is this close to the
    /// target's height at its range - the convergence threshold of the client's
    /// `leadPoint`, which is what `tests/cases` pins.</summary>
    public const float CONVERGE = 0.15f;
    /// <summary>s; how much of the flight the target's measured pull counts for.
    /// A straight-line extrapolation of a bend is trusted for the first second and
    /// capped beyond that (`LEAD_ACCEL_S` in `fcs.js`).</summary>
    public const float ACCEL_S = 1f;
    /// <summary>m/s^2; the most pull a hull can be credited with - a mech's own
    /// figure is 9.3, so 12 is above the machine and under the noise floor.</summary>
    public const float ACCEL_MAX = 12f;

    /// <summary>One gun at the instant it fires: where the round leaves, where the
    /// crew looks from, what it is firing, and the distance it is zeroed to.</summary>
    public readonly struct Shot
    {
        public readonly Vec3 muzzle;      // the mouth of the tube, this tick
        public readonly Vec3 eye;         // the pivot the sight line is drawn from
        public readonly WeaponDef wd;
        public readonly float zero;       // Z: the distance the barrel is zeroed to, m
        public readonly float max_elev;   // rad; the mount's ceiling on the solution

        public Shot(Vec3 muzzle, Vec3 eye, WeaponDef wd, float zero,
                    float max_elev = Defs.ZERO_MAX_ELEV)
        {
            this.muzzle = muzzle;
            this.eye = eye;
            this.wd = wd;
            this.zero = zero;
            this.max_elev = max_elev;
        }
    }

    /// <summary>One target, as the shooter knows it: the muzzle is one input (a
    /// gun's own offset is in the answer) and this is the other.  A crew hands in
    /// what it believes (`ai_tgt`), a pilot what the wire drew him.</summary>
    public readonly struct Track
    {
        public readonly Vec3 pos;         // the hull's origin (its feet)
        public readonly Vec3 vel;         // what it is really doing, m/s
        public readonly Vec3 acc;         // its measured pull, already age-checked
        public readonly float aim;        // m above `pos` the round should meet

        public Track(Vec3 pos, Vec3 vel, Vec3 acc, float aim)
        {
            this.pos = pos;
            this.vel = vel;
            this.acc = acc;
            this.aim = aim;
        }
    }

    /// <summary>The answer: the point to lay the sight on, the angles the wire
    /// carries, and the elevation the round leaves at.</summary>
    public readonly struct Solution
    {
        public readonly Vec3 point;       // where the sight line must pass
        public readonly float yaw;        // the sight line's angles...
        public readonly float pitch;
        public readonly float elevation;  // ...and the bore's, for this `Z`
        public readonly float tof;        // s to `point`
        public readonly bool valid;

        public Solution(Vec3 point, float yaw, float pitch, float elevation, float tof, bool valid)
        {
            this.point = point;
            this.yaw = yaw;
            this.pitch = pitch;
            this.elevation = elevation;
            this.tof = tof;
            this.valid = valid;
        }

        public static Solution Invalid => new(default, 0f, 0f, 0f, 0f, false);
    }

    /// <summary>Solve one shot.  `passes` is the lead loop, for a test that wants
    /// to watch it converge.</summary>
    public static Solution solve(in Shot s, in Track t, int passes = OUTER)
    {
        var wd = s.wd;
        // the pull is capped and only ever a straight line: a hull's turn rotates
        // while it happens, so extrapolating a curve out to a two second flight
        // draws a circle the machine never drives
        float ax = t.acc.x, az = t.acc.z;
        float amag = MathF.Sqrt(ax * ax + az * az);
        if (amag > ACCEL_MAX)
        {
            ax *= ACCEL_MAX / amag;
            az *= ACCEL_MAX / amag;
        }
        float aim_y = t.pos.y + t.aim;
        float bx = t.pos.x, by = aim_y, bz = t.pos.z;
        float yaw = 0f, pitch = 0f, tof = 0f;
        for (int pass = 0; pass < passes; pass++)
        {
            float dx = bx - s.muzzle.x, dz = bz - s.muzzle.z;
            float d = MathF.Sqrt(dx * dx + dz * dz);
            // inside `MIN_DIST` the aim point is the shooter itself: there is no
            // elevation to solve, the mount simply points at it
            if (d < Ballistics.MIN_DIST) return Solution.Invalid;
            yaw = MathF.Atan2(dz, dx);
            pitch = MathF.Atan2(by - s.muzzle.y, d);
            (float y, float t)? fl = null;
            for (int it = 0; it < INNER; it++)
            {
                // no elevation on this mount puts the round on the point: no shot,
                // rather than a plausible-looking angle that misses
                float? e0 = Ballistics.zero_at(s.muzzle, yaw, pitch, wd, s.zero, s.max_elev);
                if (e0 is null) return Solution.Invalid;
                float elev = e0.Value;
                fl = Ballistics.flight_at(s.muzzle, yaw, elev, wd, d);
                if (fl is null) return Solution.Invalid;   // this round cannot get there
                float err = by - fl.Value.y;
                if (MathF.Abs(err) < CONVERGE) break;
                pitch += MathF.Atan2(err, d);
            }
            if (fl is null) return Solution.Invalid;
            tof = fl.Value.t;
            // where the target has moved to by the time that round lands: velocity
            // all the way, plus the measured pull for the part of the flight it is
            // worth trusting
            float ta = MathF.Min(tof, ACCEL_S);
            bx = t.pos.x + t.vel.x * tof + 0.5f * ax * tof * ta;
            by = aim_y + t.vel.y * tof;
            bz = t.pos.z + t.vel.z * tof + 0.5f * az * tof * ta;
        }
        // the ring sits on the sight line the crew lays: the bore's bearing, drawn
        // from the eye, so aiming at it puts the round on the target
        float de = MathF.Sqrt((bx - s.eye.x) * (bx - s.eye.x) + (bz - s.eye.z) * (bz - s.eye.z));
        var point = new Vec3(s.eye.x + MathF.Cos(yaw) * de,
                             s.eye.y + MathF.Tan(pitch) * de,
                             s.eye.z + MathF.Sin(yaw) * de);
        // the elevation that goes with the *final* sight line - this is the angle
        // `Systems/Weapons.cs` leaves the tube at, so the round and the sight agree
        float? elev_out = Ballistics.zero_at(s.muzzle, yaw, pitch, wd, s.zero, s.max_elev);
        if (elev_out is null) return Solution.Invalid;
        return new Solution(point, yaw, pitch, elev_out.Value, tof, tof > 0f);
    }
}
