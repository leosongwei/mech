// What has been thrown at a crew lately, and from where.
//
// A crew that is shot at turns its head at the noise.  That is one fact and it is
// collected here, once, because finding it is expensive and the memory of it lives on
// the crew: `Systems/AI/Watch.cs` reads the wheel a tick later and does the looking,
// this file only answers "which directions are hot".
//
// Three sources, because a hull notices three kinds of thing:
//
//   * **a round that went past** - a rocket that missed, a burst that straddled;
//   * **a burst that went off** - a rocket that landed, a shell that hit the ground;
//   * **a laser held on the hull** - a designator already aiming at *this* machine,
//     which is not a near miss at all (`Systems/Beams.cs`).
//
// All three write the same wheel, one slot per 20 degrees, through one rule
// (`Wheel.mark`: the stronger report wins the slot, the weaker one only renews it).
// Nothing here decides *whether* to turn: it puts a direction on the wheel and
// `Watch` weighs it against everything else that is on there (`Wheel.alarm_of` /
// `Wheel.laser_of` / `Wheel.tgt_of` age the slots; this file never ages them).
//
// The test for a near miss is a *swept* distance, not a point: a 1 km/s round moves
// 33 m in a tick, so a 30 m sphere tested at the tick boundaries is stepped clean over
// and the loudest thing on the battlefield would be silent.  The leg is
// `(proj_prev, pos)`, which the flight systems publish for exactly this reason
// (`Systems/Flights.cs`), and the miss is the distance from the crew's centre to that
// segment.  The bearing a round came from is its own reverse velocity - a round that
// whistles past is a bearing, not a place - with the line from the crew to the burst
// as the fallback for anything falling too steeply to have one.
//
// The laser is the one source with a *clock* of its own, and it is per slot: a line
// has to stay on the hull for `PAINT_ARM` continuously (`Wheel.paint`).  A sight
// sweeping past crosses a 10 m body in 2.5 ms at 3 km - at 150 deg/s the head covers
// 7.8 km/s of ground - so only a line that is *kept* there is read as intent.  Two
// designators alternating on the hull therefore arm nothing (each slot's clock breaks
// every time the other one looks away), while two alternating on the *same* bearing
// do: that is continuous coverage of one direction, which is what the rule means.
//
// Where it sits (`Impact` -> here -> `Blasts`): the legs are still on the field
// (`Impact` destroys the round after this), and this tick's bursts still exist
// (`Blasts` decrements and destroys them after this).  A round that hit *this* crew is
// therefore seen one tick early in its leg rather than not at all, and a burst is seen
// in the tick it went off.
//
// Cost: one pass over the machine crews (index, centres, and the laser hits), one over
// the rounds in the air, one over the bursts.  The buffers are the world's
// (`SuppressScratch`), so a tick allocates nothing.

namespace Mech.Server.Systems.AI;

/// <summary>A round's own columns, plus the side it belongs to (a round carries
/// `faction`; `Flights.RoundColumns` does not need it for the flight itself).</summary>
public sealed class WhizColumns : Flights.RoundColumns
{
    public byte[] fac = null!;

    private static readonly string[] whiz_names = Components.Cat(names, "faction");
    public static readonly string[] OnWhiz = whiz_names;

    public WhizColumns(Group g) : base(g) { }

    public override string[] Names => whiz_names;

    protected override void bind(Group g)
    {
        base.bind(g);
        fac = g.u8("faction");
    }
}

/// <summary>The crews that carry a laser warning receiver, and the three wheels it
/// writes: a near miss, a laser, a sighting.  A man has no such instrument, so a foot
/// crew is not in this archetype at all - the component set is the type
/// (`Core/World.cs` adds `Components.ALARM_COMPS`).</summary>
public sealed class AlarmColumns : CrewColumns
{
    public float[] alarm = null!, laser = null!, tgt = null!;

    private static readonly string[] alarm_names =
        Components.Cat(Components.Cat(names, "alarm"), AiTags.CREW);

    public static readonly string[] OnAlarm = alarm_names;

    public AlarmColumns(Group g) : base(g) { }

    public override string[] Names => alarm_names;

    protected override void bind(Group g)
    {
        base.bind(g);
        alarm = g.f("alarm");
        laser = g.f("laser");
        tgt = g.f("tgt");
    }
}

/// <summary>One tick's tables, owned by the world and reused (`world.suppress`).
/// Every buffer is grown on demand and never shrunk, so after the first busy minute
/// this pass allocates nothing at all.</summary>
public sealed class SuppressScratch
{
    // entity id -> crew index, plus what a crew index needs to write its own slots
    public int[] idx = Array.Empty<int>();
    public Archetype[] arch = Array.Empty<Archetype>();
    public int[] row = Array.Empty<int>();
    public float[] px = Array.Empty<float>(), py = Array.Empty<float>(), pz = Array.Empty<float>();
    public byte[] fac = Array.Empty<byte>();

    // the grid box of one round or one burst, reused for every one of them
    public int[] box_ids = Array.Empty<int>();
    public float[] box_pos = Array.Empty<float>();

    // this tick's designators: entity id, eye, unit direction, side
    public int[] desig = Array.Empty<int>();
    public float[] deye = Array.Empty<float>(), ddir = Array.Empty<float>();
    public byte[] dfac = Array.Empty<byte>();
    public int dn;

    // every hull a beam can land on, for `Beams.body_hit`'s occlusion test
    public int[] hulls = Array.Empty<int>();
    public int hull_n;

    public int[] blasts = Array.Empty<int>();
    public bool dbg2;
}

public static class Suppress
{
    /// <summary>m; how close a round's leg has to pass to count as a near miss.</summary>
    public const float WHIZ = 30f;

    /// <summary>m; how close a burst has to be to be heard.</summary>
    public const float HEAR = 80f;

    /// <summary>s of continuous coverage before a laser is read as intent rather than
    /// as a sight sweeping past.  The debounce is the geometry: a head turning at
    /// `Watch.RATE` covers 7.8 km/s of ground at 3 km, so a 10 m body
    /// (`Defs.LASER_SPHERE`) is under a moving beam for 2.5 ms.  A line still on the
    /// hull half a second later was put there and kept there.</summary>
    public const float PAINT_ARM = 0.5f;

    private static readonly string[] BEAM_TGT = { "beam_tgt" };
    private static readonly string[] LOCKABLE = { "lockable" };
    private static readonly string[] BLAST = { "blast" };

    public static void run(World world, float dt)
    {
        var e = world.ecs;
        var grid = world.grid;
        var s = world.suppress ??= new SuppressScratch();
        var btgt = e.iv("beam_tgt");
        var lit = e.uv("beam_lit");

        // ---- 1. this tick's designators, once --------------------------------
        // Both of a designator's lasers are asked below, and they are the same test
        // the warning receiver gets (`Beams.painting`): the four channels it steers
        // (their verdict already in `beam_lit`) and the one down its sight, which
        // burns whether it holds anything or not.
        int dn = e.query_into(BEAM_TGT, ref s.desig);
        if (s.deye.Length < dn * 3)
        {
            s.deye = new float[Math.Max(3, dn * 3)];
            s.ddir = new float[Math.Max(3, dn * 3)];
        }
        if (s.dfac.Length < dn) s.dfac = new byte[Math.Max(8, dn)];
        for (int d = 0; d < dn; d++)
        {
            int did = s.desig[d];
            var (eye, sy, sp) = Weapons.aim_of(world, did);
            var dir = MathUtil.dir_from(sy, sp);
            s.deye[d * 3] = eye.x; s.deye[d * 3 + 1] = eye.y; s.deye[d * 3 + 2] = eye.z;
            s.ddir[d * 3] = dir.x; s.ddir[d * 3 + 1] = dir.y; s.ddir[d * 3 + 2] = dir.z;
            s.dfac[d] = e.uv("faction")[did];
        }
        s.dn = dn;

        // ---- 2. the machine crews: index, centres, and the laser clocks ------
        // One pass, and it does three things: the position table the two event
        // passes below look up, the reset of the laser clock (`charge` is continuous
        // coverage, so it starts at zero every tick), and the laser hits themselves.
        int n = 0;
        if (s.idx.Length < e.alive.Length)
        {
            int cap = Math.Max(e.alive.Length, s.idx.Length * 2);
            s.idx = new int[cap];
            s.arch = new Archetype[cap];
            s.row = new int[cap];
            s.px = new float[cap]; s.py = new float[cap]; s.pz = new float[cap];
            s.fac = new byte[cap];
        }
        Array.Fill(s.idx, -1, 0, e.alive.Length);
        bool hulls_ready = false;

        foreach (var g in e.groups(AlarmColumns.OnAlarm))
        {
            var c = new AlarmColumns(g);
            for (int i = 0; i < c.n; i++)
            {
                int eid = c.ids[i];
                float h = Defs.UNIT_DEFS[c.etype[i]].height;
                var centre = new Vec3(c.pos[i * 3], c.pos[i * 3 + 1] + h * 0.5f,
                                      c.pos[i * 3 + 2]);
                s.idx[eid] = n;
                s.arch[n] = g.arch;
                s.row[n] = i;
                s.px[n] = centre.x; s.py[n] = centre.y; s.pz[n] = centre.z;
                s.fac[n] = c.faction[i];
                n++;

                int o = i * Wheel.SLOTS * 2;
                uint lit_mask = 0u;              // which slots a line reached this tick

                for (int d = 0; d < dn; d++)
                {
                    if (s.dfac[d] == c.faction[i]) continue;
                    int did = s.desig[d];
                    // the channels first: the computer steers them, so `Beams` has
                    // already answered "is it on this hull" and the answer is a byte
                    bool beam = false;
                    for (int ch = 0; ch < Defs.BEAM_CHANNELS && !beam; ch++)
                        beam = btgt.get(did, ch) == eid && lit.get(did, ch) != 0;
                    if (!beam)
                    {
                        // then the sight, which is the laser nobody has to lock.  The
                        // bare ray first: only a beam that lands on this body is worth
                        // the hull-by-hull test of what stands in front of it
                        var eye = new Vec3(s.deye[d * 3], s.deye[d * 3 + 1], s.deye[d * 3 + 2]);
                        var dir = new Vec3(s.ddir[d * 3], s.ddir[d * 3 + 1], s.ddir[d * 3 + 2]);
                        if (Beams.ray_sphere(eye, dir, centre, Defs.LASER_SPHERE,
                                             Defs.LASER_RANGE) < 0f) continue;
                        if (!hulls_ready)
                        {
                            s.hull_n = e.query_into(LOCKABLE, ref s.hulls);
                            hulls_ready = true;
                        }
                        beam = Beams.body_hit(world, eye, dir, eid, did, s.hulls, s.hull_n,
                                              out var landed)
                               && Sight.clear(world, eye, landed, Sight.LOS_STEP);
                    }
                    if (!beam) continue;
                    var deye = eye_at(s, d);
                    int slot = Wheel.slot_of(MathF.Atan2(deye.z - centre.z, deye.x - centre.x));
                    Wheel.paint(c.laser, i, slot, dt, PAINT_ARM);
                    lit_mask |= 1u << slot;
                }
                // a slot no line reaches this tick has lost its coverage: the clock
                // is "how long has this bearing been lit without a break"
                for (int k = 0; k < Wheel.SLOTS; k++)
                    if ((lit_mask & (1u << k)) == 0u) c.laser[o + k * 2] = 0f;
            }
        }
        if (n == 0) return;

        // ---- 3. the rounds that went past ------------------------------------
        foreach (var g in e.groups(WhizColumns.OnWhiz))
        {
            var c = new WhizColumns(g);
            for (int i = 0; i < c.n; i++)
            {
                if (c.life[i] <= 0f) continue;              // no battery or wire left
                byte ff = c.fac[i];
                var end = new Vec3(c.pos[i * 3], c.pos[i * 3 + 1], c.pos[i * 3 + 2]);
                var start = new Vec3(c.prev[i * 3], c.prev[i * 3 + 1], c.prev[i * 3 + 2]);
                float vx = c.vel[i * 3], vz = c.vel[i * 3 + 2];
                float flat = vx * vx + vz * vz;
                // the bearing the fire came from is the round's own reverse bearing,
                // which needs a horizontal velocity to mean anything: a top attack
                // round coming down the chimney is read off where it is instead
                float yaw = flat > 1f ? MathF.Atan2(-vz, -vx)
                                      : MathF.Atan2(start.z - end.z, start.x - end.x);
                var (ci, cj) = grid.cell_of(end.x, end.z);
                int bn = grid.box_into(ci, cj, (int)(WHIZ * grid.inv) + 1,
                                       ref s.box_ids, ref s.box_pos);
                for (int k = 0; k < bn; k++)
                {
                    int id = s.box_ids[k];
                    int who = id < s.idx.Length ? s.idx[id] : -1;
                    if (who < 0 || s.fac[who] == ff) continue;
                    var hull = new Vec3(s.px[who], s.py[who], s.pz[who]);
                    if (_miss(start, end, hull) > WHIZ) continue;
                    float bearing = flat > 1f ? yaw : MathF.Atan2(start.z - hull.z, start.x - hull.x);
                    Wheel.mark(s.arch[who].f("alarm"), s.row[who],
                               Wheel.slot_of(bearing), Wheel.ALARM_LIFE, Wheel.BULLET_W);
                }
            }
        }

        // ---- 4. the bursts ---------------------------------------------------
        int bl = e.query_into(BLAST, ref s.blasts);
        for (int b = 0; b < bl; b++)
        {
            int bid = s.blasts[b];
            var p = e.fv("pos").get3(bid);
            byte bf = e.uv("faction")[bid];
            var (ci, cj) = grid.cell_of(p.x, p.z);
            int bn = grid.box_into(ci, cj, (int)(HEAR * grid.inv) + 1,
                                   ref s.box_ids, ref s.box_pos);
            for (int k = 0; k < bn; k++)
            {
                int id = s.box_ids[k];
                int who = id < s.idx.Length ? s.idx[id] : -1;
                if (who < 0 || s.fac[who] == bf) continue;
                float dx = p.x - s.px[who], dy = p.y - s.py[who], dz = p.z - s.pz[who];
                if (MathF.Sqrt(dx * dx + dy * dy + dz * dz) > HEAR) continue;
                Wheel.mark(s.arch[who].f("alarm"), s.row[who],
                           Wheel.slot_of(MathF.Atan2(dz, dx)), Wheel.ALARM_LIFE, Wheel.BLAST_W);
            }
        }
    }

    private static Vec3 eye_at(SuppressScratch s, int d) =>
        new(s.deye[d * 3], s.deye[d * 3 + 1], s.deye[d * 3 + 2]);

    /// <summary>m; how close the segment `a`-`b` comes to `p`.  The one geometric
    /// fact this file needs, and the reason a fast round cannot slip between two
    /// ticks unnoticed.</summary>
    private static float _miss(Vec3 a, Vec3 b, Vec3 p)
    {
        float ax = b.x - a.x, ay = b.y - a.y, az = b.z - a.z;
        float len2 = ax * ax + ay * ay + az * az;
        float t = 0f;
        if (len2 > 1e-6f)
        {
            t = ((p.x - a.x) * ax + (p.y - a.y) * ay + (p.z - a.z) * az) / len2;
            t = Math.Clamp(t, 0f, 1f);
        }
        float dx = a.x + ax * t - p.x;
        float dy = a.y + ay * t - p.y;
        float dz = a.z + az * t - p.z;
        return MathF.Sqrt(dx * dx + dy * dy + dz * dz);
    }
}
