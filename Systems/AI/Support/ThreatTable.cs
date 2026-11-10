// The guided rounds in the air, gathered once a tick.
//
// A radar question - "is something closing on me fast enough to be visible, and is
// it for me" - is asked by every crew with a receiver, so the rounds are read off
// their archetypes once (`Threats.run`) and answered positionally from flat arrays
// after that.  It is the same shape as the tick's hit-sphere table: one table per
// tick, owned by the world, rebuilt when the tick it was built on is not this one.
//
// A round that can be fought carries `interceptable`; the seeker's memory
// (`tgt_mem`) only exists on the kinds that have a seeker, so it is bound from the
// archetypes that own it and stays at the origin for the rest - which is exactly
// what a guided round with no lock is aimed by.

namespace Mech.Server.Systems.AI;

/// <summary>This tick's guided rounds, as flat arrays.  `index_of` is a linear
/// scan on purpose: a battle has a few dozen rounds in the air, and a dictionary
/// per tick would cost more than the scan.</summary>
public sealed class ThreatTable
{
    public int[] ids = Array.Empty<int>();
    public int n;
    public float[] pos = Array.Empty<float>();     // n*3
    public float[] vel = Array.Empty<float>();     // n*3
    public float[] mem = Array.Empty<float>();     // n*3, the point it stares at (0 if it has none)
    public byte[] faction = Array.Empty<byte>();
    public byte[] kind = Array.Empty<byte>();      // Defs proj kind: 0 bullet 1 rocket 2 saclos 3 top
    public byte[] wid = Array.Empty<byte>();
    public int[] target = Array.Empty<int>();
    public float[] rcs = Array.Empty<float>();
    public float[] hit_r = Array.Empty<float>();
    public float[] prox = Array.Empty<float>();

    public Vec3 pos_at(int k) => new(pos[k * 3], pos[k * 3 + 1], pos[k * 3 + 2]);
    public Vec3 vel_at(int k) => new(vel[k * 3], vel[k * 3 + 1], vel[k * 3 + 2]);
    public Vec3 mem_at(int k) => new(mem[k * 3], mem[k * 3 + 1], mem[k * 3 + 2]);

    public int index_of(int eid)
    {
        for (int k = 0; k < n; k++) if (ids[k] == eid) return k;
        return -1;
    }

    /// <summary>The columns a round's row is made of.  The seeker's memory is bound
    /// by its own schema (`SeekerColumns`) because only one kind of round owns it.
    /// </summary>
    public class ThreatColumns : Columns<ThreatRow>
    {
        public float[] pos = null!, vel = null!;
        public byte[] faction = null!, kind = null!, wid = null!;
        public int[] target = null!;

        internal static readonly string[] names =
        {
            "interceptable", "pos", "vel", "faction", "proj_kind", "proj_wid", "target",
        };

        public ThreatColumns(Group g) : base(g) { bind(g); }

        public override string[] Names => names;

        protected override void bind(Group g)
        {
            pos = g.f("pos"); vel = g.f("vel");
            faction = g.u8("faction"); kind = g.u8("proj_kind"); wid = g.u8("proj_wid");
            target = g.i("target");
        }

        public override ThreatRow row(int i) => new ThreatRow(this, i);
    }

    /// <summary>The rounds that carry a seeker (`Components.SEEKER_COMPS`): the top
    /// attack, and nothing else today.</summary>
    public sealed class SeekerColumns : ThreatColumns
    {
        public float[] mem = null!;
        private static readonly string[] seeker_names = Components.Cat(names, "tgt_mem");
        public static readonly string[] OnSeeker = Components.Cat(seeker_names, "top_attack");

        public SeekerColumns(Group g) : base(g) { }

        public override string[] Names => seeker_names;

        protected override void bind(Group g)
        {
            base.bind(g);
            mem = g.f("tgt_mem");
        }
    }

    public readonly struct ThreatRow : IRow<ThreatColumns>
    {
        private readonly ThreatColumns _c;
        public readonly int i;

        public ThreatRow(ThreatColumns c, int i) { _c = c; this.i = i; }

        public ThreatColumns columns => _c;
        public int index => i;
        public int eid => _c.ids[i];
        public Vec3 pos => new(_c.pos[i * 3], _c.pos[i * 3 + 1], _c.pos[i * 3 + 2]);
        public Vec3 vel => new(_c.vel[i * 3], _c.vel[i * 3 + 1], _c.vel[i * 3 + 2]);
        public byte faction => _c.faction[i];
        public byte kind => _c.kind[i];
        public byte wid => _c.wid[i];
        public int target => _c.target[i];
    }

    /// <summary>Read every guided round in the air into one table.</summary>
    public static ThreatTable build(World world)
    {
        var e = world.ecs;
        var t = new ThreatTable();
        var pos = new List<float>();
        var vel = new List<float>();
        var fac = new List<byte>();
        var kind = new List<byte>();
        var wid = new List<byte>();
        var target = new List<int>();
        var ids = new List<int>();
        var mem = new List<float>();
        foreach (var g in e.groups(ThreatColumns.names))
        {
            var c = new ThreatColumns(g);
            foreach (var r in c)
            {
                ids.Add(r.eid);
                pos.Add(r.pos.x); pos.Add(r.pos.y); pos.Add(r.pos.z);
                vel.Add(r.vel.x); vel.Add(r.vel.y); vel.Add(r.vel.z);
                mem.Add(0f); mem.Add(0f); mem.Add(0f);
                fac.Add(r.faction); kind.Add(r.kind); wid.Add(r.wid); target.Add(r.target);
            }
        }
        if (ids.Count == 0) return t;
        // the seeker's memory, for the kinds that keep one
        var seat = new Dictionary<int, int>(ids.Count);
        for (int k = 0; k < ids.Count; k++) seat[ids[k]] = k;
        foreach (var g in e.groups(SeekerColumns.OnSeeker))
        {
            var c = new SeekerColumns(g);
            foreach (var r in c)
            {
                if (!seat.TryGetValue(r.eid, out int k)) continue;
                mem[k * 3] = c.mem[r.index * 3];
                mem[k * 3 + 1] = c.mem[r.index * 3 + 1];
                mem[k * 3 + 2] = c.mem[r.index * 3 + 2];
            }
        }
        t.n = ids.Count;
        t.ids = ids.ToArray();
        t.pos = pos.ToArray();
        t.vel = vel.ToArray();
        t.mem = mem.ToArray();
        t.faction = fac.ToArray();
        t.kind = kind.ToArray();
        t.wid = wid.ToArray();
        t.target = target.ToArray();
        t.rcs = new float[t.n];
        t.hit_r = new float[t.n];
        t.prox = new float[t.n];
        for (int k = 0; k < t.n; k++)
        {
            var wd = Defs.WEAPONS[t.wid[k]];
            t.rcs[k] = wd.rcs;
            t.hit_r[k] = wd.hit_r;
            t.prox[k] = wd.prox;
        }
        return t;
    }
}
