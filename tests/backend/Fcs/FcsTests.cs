// The server's fire control, against the shared contract (`tests/cases/fcs.json`).
//
// The same three layers the front end checks (`tests/frontend/fcs/run.test.mjs`), and
// the same tolerances (`Support/MiniTest.cs::Tol`):
//
//   * every case must come back with a solution whose sight line really passes
//     through the point it reports;
//   * **L1**: flying the round at the elevation the solution asked for must land it
//     on the target - measured with this host's own integrator, so the two copies of
//     the law cannot hide a typo by agreeing with each other;
//   * the edge cases must come back invalid.
//
// L2 - the two hosts agreeing - is `tests/compare.mjs`, which needs both to have run.

using Mech.Server;

namespace Mech.Tests;

public static class FcsTests
{
    /// <summary>Where the target really is `t` seconds after the shot leaves:
    /// velocity all the way, plus the measured pull for the first second of the
    /// flight (`FireControl.ACCEL_S`).</summary>
    private static Vec3 TargetAt(in Case k, float t)
    {
        float ta = MathF.Min(t, FireControl.ACCEL_S);
        var pos = k.track.pos;
        var vel = k.track.vel;
        var acc = k.track.acc;
        return new Vec3(pos.x + vel.x * t + 0.5f * acc.x * t * ta,
                        pos.y + k.track.aim + vel.y * t,
                        pos.z + vel.z * t + 0.5f * acc.z * t * ta);
    }

    /// <summary>The round's closest approach to the target's own path.
    ///
    /// A leg against a moving point, solved segment by segment: a 30 mm round covers
    /// 36 m in one tick, so sampling tick boundaries would report a clean miss on a
    /// clean hit.</summary>
    private static float Closest(in Case k, in FireControl.Solution sol)
    {
        var wd = k.shot.wd;
        var d = MathUtil.dir_from(sol.yaw, sol.elevation);
        float vx = d.x * wd.vel, vy = d.y * wd.vel, vz = d.z * wd.vel;
        var p = k.shot.muzzle;
        float best = float.PositiveInfinity;
        const float dt = Ballistics.DT;
        for (float t = 0f; t < wd.life; t += dt)
        {
            if (wd.thrust > 0f)
            {
                float sp = MathF.Sqrt(vx * vx + vy * vy + vz * vz);
                float sc = MathF.Min(wd.maxvel, sp + wd.thrust * dt) / MathF.Max(sp, 1e-6f);
                vx *= sc; vy *= sc; vz *= sc;
            }
            vy -= wd.g * dt;
            var n = new Vec3(p.x + vx * dt, p.y + vy * dt, p.z + vz * dt);
            var a = TargetAt(k, t);
            var b = TargetAt(k, t + dt);
            float rx = p.x - a.x, ry = p.y - a.y, rz = p.z - a.z;
            float wx = (n.x - p.x) - (b.x - a.x);
            float wy = (n.y - p.y) - (b.y - a.y);
            float wz = (n.z - p.z) - (b.z - a.z);
            float ww = wx * wx + wy * wy + wz * wz;
            float u = ww < 1e-9f ? 0f
                : Math.Clamp(-(rx * wx + ry * wy + rz * wz) / ww, 0f, 1f);
            float cx = rx + wx * u, cy = ry + wy * u, cz = rz + wz * u;
            best = MathF.Min(best, MathF.Sqrt(cx * cx + cy * cy + cz * cz));
            if (n.y < -200f) break;
            p = n;
        }
        return best;
    }

    [Test]
    public static void SightLinePassesThePoint()
    {
        foreach (var k in Cases.All)
        {
            var sol = FireControl.solve(k.shot, k.track);
            if (k.tag.Length != 0) continue;
            Assert.True(sol.valid, $"{k.id}: no solution");
            var e = k.shot.eye;
            float dh = MathF.Sqrt((sol.point.x - e.x) * (sol.point.x - e.x)
                                  + (sol.point.z - e.z) * (sol.point.z - e.z));
            Assert.Close(sol.yaw, MathF.Atan2(sol.point.z - e.z, sol.point.x - e.x),
                         Tol.Angle, $"{k.id}: yaw is not the sight line through the point");
            Assert.Close(sol.pitch, MathF.Atan2(sol.point.y - e.y, MathF.Max(dh, 1e-3f)),
                         Tol.Angle, $"{k.id}: pitch is not the sight line through the point");
        }
    }

    [Test]
    public static void RoundLandsOnTheTarget()
    {
        foreach (var k in Cases.All)
        {
            if (k.tag.Length != 0) continue;
            var sol = FireControl.solve(k.shot, k.track);
            Assert.True(sol.valid, $"{k.id}: no solution");
            float miss = Closest(k, sol);
            Assert.AtMost(miss, Tol.Miss(k.radius),
                          $"{k.id}: the round passes {miss:0.00} m off "
                          + $"(allowed {Tol.Miss(k.radius):0.00} m)");
        }
    }

    [Test]
    public static void EdgeCasesAreRefused()
    {
        foreach (var k in Cases.All)
        {
            if (k.tag.Length == 0 || k.tag == "stale_acc") continue;
            var sol = FireControl.solve(k.shot, k.track);
            Assert.Same(sol.valid, false, $"{k.id} ({k.tag}): expected an invalid solution");
        }
    }

    [Test]
    public static void StalePullIsNoPull()
    {
        foreach (var k in Cases.All)
        {
            if (k.tag != "stale_acc") continue;
            var pair = Cases.ById(k.pair);
            Assert.True(pair is not null, $"{k.id}: no pair case {k.pair}");
            var a = FireControl.solve(k.shot, k.track);
            var b = FireControl.solve(pair!.shot, pair.track);
            Assert.True(a.valid && b.valid, $"{k.id}: expected both valid");
            float d = MathUtil.dist(a.point, b.point);
            Assert.AtMost(d, Tol.Point(k.track.pos.x),
                          $"{k.id}: a stale pull changed the answer by {d:0.00} m");
            Assert.Close(a.tof, b.tof, Tol.Tof, $"{k.id}: a stale pull changed the flight time");
        }
    }

    /// <summary>The solutions, in the shape `tests/compare.mjs` reads (the same shape
    /// the front end writes).</summary>
    public static string SolutionsJson()
    {
        var sb = new System.Text.StringBuilder("[\n");
        bool first = true;
        foreach (var k in Cases.All)
        {
            var sol = FireControl.solve(k.shot, k.track);
            sb.Append(first ? "" : ",\n");
            first = false;
            sb.Append($"  {{\"id\": \"{k.id}\", \"valid\": {(sol.valid ? "true" : "false")}");
            if (sol.valid)
            {
                sb.Append($", \"point\": [{sol.point.x:R}, {sol.point.y:R}, {sol.point.z:R}]");
                sb.Append($", \"yaw\": {sol.yaw:R}, \"pitch\": {sol.pitch:R}");
                sb.Append($", \"elevation\": {sol.elevation:R}, \"tof\": {sol.tof:R}");
            }
            sb.Append('}');
        }
        sb.Append("\n]\n");
        return sb.ToString();
    }
}
