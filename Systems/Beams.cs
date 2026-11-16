// The laser designator: what a beam rider actually flies.
//
// A SACLOS round is not steered by a seeker of its own - it has a head that looks
// at the laser its launcher is shining and turns to stay in it, which is why the
// guidance lives here and not on the round.  This system is the launcher's
// fire-control computer: it holds up to `Defs.BEAM_CHANNELS` channels, keeps a
// *track* on each of them, and publishes this tick's beam direction out of the
// sight head.  `Systems/Flights.cs::Saclos` reads the channel its round is encoded
// on and steers for a point along that line.
//
// Three things make the beam more than the sight line it used to be:
//
//   * a channel is laid on a **target**, so the computer aims it where that hull
//     is going - it leads the shot, the pilot does not;
//   * the beam is limited to a **40 deg cone** about the sight line
//     (`Defs.BEAM_CONE`, fixed whatever the optics are doing), so a hull that
//     leaves that cone is the one thing the lock cannot follow - the picture is
//     wider than the cone at 1x and narrower at 20x, and the cone does not care;
//   * when the sight is cut - a stand of trees, a house, a screen of smoke - the
//     channel keeps flying on what it measured: the point walks at the target's
//     last velocity and pull for `Defs.BEAM_MEMORY` seconds and the round keeps
//     arriving.  That is the difference between a beam that dies with the line of
//     sight and one that was aimed a second ahead;
//   * past the memory the beam goes dark but the **lock is not given up**: the hull
//     is still the machine's, so a hull that reappears inside the cone takes its
//     line back and the round is corrected again.
//
// What this system does *not* own: acquiring a lock (`Systems/Locking.cs`), and
// whether the round can still see the laser (asked from the round's end, in
// `Flights.Saclos` - a ridge between the launcher and the missile cuts the beam as
// surely as one behind the target).

namespace Mech.Server.Systems;

public static class Beams
{
    // ---- the channel table -------------------------------------------------

    /// <summary>Lay a channel on `tid`, or answer the one already pointed at it.
    /// A fifth target displaces the oldest channel: the beam counts to four, and
    /// a bear picks up one cob and drops another.</summary>
    public static int lay(World world, int eid, int tid)
    {
        var e = world.ecs;
        // a channel is laid on a hull, and only on a hull: the id a wreck left
        // behind can be a blast for a tick, and a blast has no velocity to track
        if (tid < 0 || !e.has(eid, "beam_tgt")) return -1;
        if (!e.is_alive(tid) || !e.has(tid, "etype")) return -1;
        var tgt = e.iv("beam_tgt");
        int free = -1, oldest = -1;
        float oldest_age = -1f;
        for (int ch = 0; ch < Defs.BEAM_CHANNELS; ch++)
        {
            int have = tgt.get(eid, ch);
            if (have == tid) return ch;
            if (have < 0) { if (free < 0) free = ch; }
            else if (e.fv("beam_age").get(eid, ch) > oldest_age)
            {
                oldest_age = e.fv("beam_age").get(eid, ch);
                oldest = ch;
            }
        }
        int pick = free >= 0 ? free : oldest;
        if (pick < 0) return -1;
        tgt.set(eid, pick, tid);
        e.fv("beam_age").set(eid, pick, 0f);
        // a fresh channel has no beam until this tick's pass has tracked it
        e.uv("beam_ok").set(eid, pick, 0);
        // a channel laid afresh starts its memory off the hull's own body, so a
        // round fired on the same tick as the lock has a line to ride
        _sample(world, eid, pick, tid, 0f);
        return pick;
    }

    /// <summary>Give one channel up.</summary>
    public static void free(World world, int eid, int ch)
    {
        var e = world.ecs;
        if (!e.has(eid, "beam_tgt") || ch < 0 || ch >= Defs.BEAM_CHANNELS) return;
        e.iv("beam_tgt").set(eid, ch, -1);
        e.fv("beam_seen").set(eid, ch, 0f);
        e.fv("beam_age").set(eid, ch, 0f);
        e.uv("beam_ok").set(eid, ch, 0);
    }

    /// <summary>Give every channel up - the hull carries no launcher that could use
    /// one any more.</summary>
    public static void free_all(World world, int eid)
    {
        for (int ch = 0; ch < Defs.BEAM_CHANNELS; ch++) free(world, eid, ch);
    }

    // ---- the tick ----------------------------------------------------------

    /// <summary>Walk every channel of every designator and publish this tick's
    /// beams.  It runs after the drivers, because a channel is tracked on the
    /// target's position at the end of the tick - the one its own movement system
    /// has already written - and before the flights, which read what it leaves.</summary>
    public static void run(World world, float dt)
    {
        var e = world.ecs;
        var ids = e.query("beam_tgt");
        if (ids.Length == 0) return;
        var tgt = e.iv("beam_tgt");
        var age = e.fv("beam_age");
        var seen = e.fv("beam_seen");
        var ok = e.uv("beam_ok");
        foreach (int eid in ids)
        {
            // most designators hold nothing most of the time: one pass over the
            // four ids is the whole cost of a hull that is not steering a round
            bool any = false;
            for (int ch = 0; ch < Defs.BEAM_CHANNELS && !any; ch++) any = tgt.get(eid, ch) >= 0;
            if (!any) continue;
            int mine = e.uv("faction")[eid];
            // the sight head is the laser's aperture, and the window it can be
            // pointed in is the pilot's own picture
            var (eye, sy, sp) = Weapons.aim_of(world, eid);
            var sight = MathUtil.dir_from(sy, sp);
            float cone = Defs.BEAM_CONE;
            for (int ch = 0; ch < Defs.BEAM_CHANNELS; ch++)
            {
                int tid = tgt.get(eid, ch);
                if (tid < 0) continue;
                age.set(eid, ch, age.get(eid, ch) + dt);
                if (!e.is_alive(tid) || e.uv("faction")[tid] == mine || !e.has(tid, "lockable"))
                {
                    free(world, eid, ch);
                    continue;
                }
                var body = e.fv("pos").get3(tid);
                float h = Defs.UNIT_DEFS[e.uv("etype")[tid]].height * 0.5f;
                var at = new Vec3(body.x, body.y + h, body.z);
                bool visible = _in_window(eye, sight, cone, at)
                               && Sight.clear(world, eye, at, Sight.LOS_STEP);
                if (visible)
                {
                    _sample(world, eid, ch, tid, dt);
                }
                else
                {
                    // the sight is cut: the channel walks on at the velocity and
                    // pull it was measured making.  Past `BEAM_MEMORY` the walk
                    // stops - a guess that old is not worth flying - but the
                    // *lock* stays: the hull is this machine's, and a hull that
                    // comes back into the cone gets its line back the tick it is
                    // seen again
                    float blind = seen.get(eid, ch) + dt;
                    seen.set(eid, ch, blind);
                    if (blind <= Defs.BEAM_MEMORY) _walk(world, eid, ch, dt);
                }
                var mem = e.fv("beam_mem");
                var point = new Vec3(mem.get(eid, ch * 3), mem.get(eid, ch * 3 + 1),
                                     mem.get(eid, ch * 3 + 2));
                // a target that has walked out of the picture takes its line with
                // it: the sight head cannot point where the glass does not see
                if (!_in_window(eye, sight, cone, point))
                {
                    free(world, eid, ch);
                    continue;
                }
                // the channel is lit while the computer has something to point at:
                // the hull itself, or a guess worth flying.  Dark after that, and
                // still *held* - which is the difference between a lock that
                // outlasts a screen and one that dies with it
                ok.set(eid, ch, seen.get(eid, ch) <= Defs.BEAM_MEMORY ? (byte)1 : (byte)0);
            }
        }
    }

    /// <summary>The point a round flying at `speed` should steer for to meet a
    /// track: the point walked forward by that round's *own* time of flight, over
    /// the range to where it lands.  Two rounds on one channel are at two different
    /// points of the same line and arrive at different times, so this is solved per
    /// round and not by the designator - the beam carries the track, and each round
    /// leads itself off it.
    ///
    /// A lead is not optional: riding a line through a hull crossing at 30 m/s from
    /// 800 m out, a round to the far end of the line overtakes the hull on the way
    /// and goes off the map past it (measured, before this solve existed).</summary>
    public static Vec3 intercept(Vec3 at, Vec3 vel, Vec3 acc, Vec3 from, float speed)
    {
        float ax = acc.x, az = acc.z;
        float amag = MathF.Sqrt(ax * ax + az * az);
        if (amag > FireControl.ACCEL_MAX)
        {
            ax *= FireControl.ACCEL_MAX / amag;
            az *= FireControl.ACCEL_MAX / amag;
        }
        float sp = MathF.Max(speed, 1f);
        float tof = MathUtil.dist(from, at) / sp;
        var p = at;
        for (int it = 0; it < 3; it++)
        {
            float ta = MathF.Min(tof, FireControl.ACCEL_S);
            p = new Vec3(at.x + vel.x * tof + 0.5f * ax * tof * ta,
                         at.y + vel.y * tof,
                         at.z + vel.z * tof + 0.5f * az * tof * ta);
            tof = MathUtil.dist(from, p) / sp;
        }
        return p;
    }

    // ---- the pieces --------------------------------------------------------

    /// <summary>Refresh a channel's memory off the live hull: where its body is,
    /// how fast it is going, and the pull those velocity samples imply (the same
    /// measurement, on the same time constant, that a crew makes on its target).</summary>
    private static void _sample(World world, int eid, int ch, int tid, float dt)
    {
        var e = world.ecs;
        int b3 = ch * 3, b2 = ch * 2;
        var pos = e.fv("pos").get3(tid);
        float h = Defs.UNIT_DEFS[e.uv("etype")[tid]].height * 0.5f;
        var vel = e.fv("vel").get3(tid);
        // the memory is a *point* plus a velocity, exactly like a missile seeker's,
        // so an extrapolated channel and a live one are the same thing to the aim
        var mem = e.fv("beam_mem");
        mem.set(eid, b3, pos.x);
        mem.set(eid, b3 + 1, pos.y + h);
        mem.set(eid, b3 + 2, pos.z);
        var bvel = e.fv("beam_vel");
        bvel.set(eid, b3, vel.x);
        bvel.set(eid, b3 + 1, vel.y);
        bvel.set(eid, b3 + 2, vel.z);
        var pv = e.fv("beam_pv");
        float px = pv.get(eid, b2), pz = pv.get(eid, b2 + 1);
        if (dt > 0f)
        {
            float ax = (vel.x - px) / dt, az = (vel.z - pz) / dt;
            if (ax * ax + az * az < 625f)      // 25 m/s^2 is a teleport, not a pull
            {
                float k = 1f - MathF.Exp(-dt / Defs.BEAM_ACCEL_TAU);
                var acc = e.fv("beam_acc");
                float ox = acc.get(eid, b2), oz = acc.get(eid, b2 + 1);
                acc.set(eid, b2, ox + (ax - ox) * k);
                acc.set(eid, b2 + 1, oz + (az - oz) * k);
            }
        }
        pv.set(eid, b2, vel.x);
        pv.set(eid, b2 + 1, vel.z);
        e.fv("beam_seen").set(eid, ch, 0f);
    }

    /// <summary>Walk a blinded channel forward one tick: the point at the velocity,
    /// the velocity at the pull it was measured making - and only for as long as
    /// that pull is worth trusting (`FireControl.ACCEL_S`, the same second every
    /// gun in the game leads on).  A measurement of a hull nobody can see stops
    /// being a measurement; carried for ever it curves a memory that should be a
    /// straight line, which is metres of error over a two-second screen.</summary>
    private static void _walk(World world, int eid, int ch, float dt)
    {
        var e = world.ecs;
        int b3 = ch * 3, b2 = ch * 2;
        var mem = e.fv("beam_mem");
        var bvel = e.fv("beam_vel");
        var acc = e.fv("beam_acc");
        float trust = e.fv("beam_seen").get(eid, ch) <= FireControl.ACCEL_S ? 1f : 0f;
        float ax = acc.get(eid, b2) * trust, az = acc.get(eid, b2 + 1) * trust;
        mem.set(eid, b3, mem.get(eid, b3) + bvel.get(eid, b3) * dt);
        mem.set(eid, b3 + 1, mem.get(eid, b3 + 1) + bvel.get(eid, b3 + 1) * dt);
        mem.set(eid, b3 + 2, mem.get(eid, b3 + 2) + bvel.get(eid, b3 + 2) * dt);
        bvel.set(eid, b3, bvel.get(eid, b3) + ax * dt);
        bvel.set(eid, b3 + 2, bvel.get(eid, b3 + 2) + az * dt);
    }

    /// <summary>Whether a point is inside the sight head's own window about the
    /// line it is looking down.</summary>
    private static bool _in_window(Vec3 eye, Vec3 sight, float cone, Vec3 p)
    {
        float vx = p.x - eye.x, vy = p.y - eye.y, vz = p.z - eye.z;
        float n = MathF.Sqrt(vx * vx + vy * vy + vz * vz);
        if (n < 1e-3f) return false;
        float dot = (vx * sight.x + vy * sight.y + vz * sight.z) / n;
        return MathF.Acos(Math.Clamp(dot, -1f, 1f)) <= cone;
    }
}
