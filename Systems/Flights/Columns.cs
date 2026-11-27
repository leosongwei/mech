// The columns a round's pass reads: the buffers every round owns, and the two
// schemas that extend them.
//
// A round's pass binds `RoundColumns` and queries it by kind tag; a beam rider's
// and the top attack's add the one component their law needs.  `Names` is both
// the query and the binding list, so a misspelt name throws at the first bind
// (`Ecs/Columns.cs`).

namespace Mech.Server.Systems;

public static partial class Flights
{
    /// <summary>One in-flight round's columns: the buffers every round owns
    /// (`Components.PROJ_COMPS`) plus the `first_hit_record` the flight systems
    /// re-arm and publish.  `Arm` and `Expire` walk every round with it; a flight
    /// stage binds the one that suits it, and the kind stays a query tag
    /// (`OnBullet`, `OnSaclos`, ...), never a branch.</summary>
    public class RoundColumns : Columns<RoundRow>
    {
        // bound by `bind`, which the constructor calls: hence the `null!`
        public float[] pos = null!, vel = null!, prev = null!, hit_pos = null!;
        public float[] age = null!, life = null!, trav = null!, seed = null!;
        public float[] hit_t = null!, hit_gap = null!;
        public byte[] wid = null!, hit_kind = null!, hit_mod = null!;
        public int[] src = null!, hit_unit = null!;

        internal static readonly string[] names =
        {
            "pos", "vel", "proj_prev", "first_hit_pos",
            "proj_age", "proj_life", "proj_trav", "proj_seed", "first_hit_t", "first_hit_gap",
            "proj_wid", "first_hit_kind", "first_hit_mod",
            "proj_src", "first_hit_unit",
        };

        public static readonly string[] OnBullet = Components.Cat(names, "bullet");
        // a beam rider owns one component more (the laser line it rides) and the top
        // attack two, so their schemas and their queries are the classes below; an
        // unguided rocket is queryless, because it asks the wing for nothing

        public RoundColumns(Group g) : base(g) { bind(g); }

        public override string[] Names => names;

        protected override void bind(Group g)
        {
            pos = g.f("pos"); vel = g.f("vel"); prev = g.f("proj_prev");
            hit_pos = g.f("first_hit_pos");
            age = g.f("proj_age"); life = g.f("proj_life"); trav = g.f("proj_trav");
            seed = g.f("proj_seed"); hit_t = g.f("first_hit_t"); hit_gap = g.f("first_hit_gap");
            wid = g.u8("proj_wid"); hit_kind = g.u8("first_hit_kind");
            hit_mod = g.u8("first_hit_mod");
            src = g.i("proj_src"); hit_unit = g.i("first_hit_unit");
        }

        public override RoundRow row(int i) => new RoundRow(this, i);
    }

    /// <summary>One round: which columns, which row.  Every accessor is the buffer
    /// element itself, so a write lands in the archetype and nothing is copied
    /// back.</summary>
    public readonly struct RoundRow : IRow<RoundColumns>
    {
        private readonly RoundColumns _c;
        public readonly int i;

        public RoundRow(RoundColumns c, int i) { _c = c; this.i = i; }

        public RoundColumns columns => _c;
        public int index => i;
        public int eid => _c.ids[i];

        public Vec3 pos
        {
            get => new(_c.pos[i * 3], _c.pos[i * 3 + 1], _c.pos[i * 3 + 2]);
            set { _c.pos[i * 3] = value.x; _c.pos[i * 3 + 1] = value.y; _c.pos[i * 3 + 2] = value.z; }
        }
        public Vec3 vel
        {
            get => new(_c.vel[i * 3], _c.vel[i * 3 + 1], _c.vel[i * 3 + 2]);
            set { _c.vel[i * 3] = value.x; _c.vel[i * 3 + 1] = value.y; _c.vel[i * 3 + 2] = value.z; }
        }
        public Vec3 prev
        {
            get => new(_c.prev[i * 3], _c.prev[i * 3 + 1], _c.prev[i * 3 + 2]);
            set { _c.prev[i * 3] = value.x; _c.prev[i * 3 + 1] = value.y; _c.prev[i * 3 + 2] = value.z; }
        }
        public Vec3 hit_pos
        {
            get => new(_c.hit_pos[i * 3], _c.hit_pos[i * 3 + 1], _c.hit_pos[i * 3 + 2]);
            set { _c.hit_pos[i * 3] = value.x; _c.hit_pos[i * 3 + 1] = value.y; _c.hit_pos[i * 3 + 2] = value.z; }
        }

        public ref float age => ref _c.age[i];
        public ref float life => ref _c.life[i];
        public ref float trav => ref _c.trav[i];
        public ref float hit_t => ref _c.hit_t[i];
        public ref float hit_gap => ref _c.hit_gap[i];
        public ref byte hit_kind => ref _c.hit_kind[i];
        public ref byte hit_mod => ref _c.hit_mod[i];
        public ref int hit_unit => ref _c.hit_unit[i];
        public float seed => _c.seed[i];
        public byte wid => _c.wid[i];
        public int src => _c.src[i];
    }

    /// <summary>A beam rider's columns: the round's, plus the line of the laser it
    /// was encoded on (`Systems/Beams.cs` holds the channels on the launcher) and the
    /// three airframe buffers its guidance writes - the demand the wing answers, and
    /// the accumulator the crosswind goes into.</summary>
    public sealed class SaclosColumns : RoundColumns
    {
        public byte[] ch = null!;
        public float[] adir = null!, awant = null!, acc = null!;

        private static readonly string[] beam_names = Components.Cat(names, "beam_ch",
                                                                    "a_dir", "a_want", "acc");
        public static readonly string[] OnSaclos = Components.Cat(beam_names, "saclos");

        public SaclosColumns(Group g) : base(g) { }

        public override string[] Names => beam_names;

        protected override void bind(Group g)
        {
            base.bind(g);
            ch = g.u8("beam_ch");
            adir = g.f("a_dir"); awant = g.f("a_want"); acc = g.f("acc");
        }
    }

    /// <summary>The top attack's columns: the round's, plus the velocity its stare
    /// point walks at and the demand its guidance writes.  `Seeker` owns both halves
    /// of that memory; the flight stages read this one, and only as the lead the arc
    /// needs.
    ///
    /// `bind` is the only virtual call made from a constructor in the schema, and
    /// it does nothing but assign buffers, so the dispatch before the derived
    /// instance is set up is safe.</summary>
    public sealed class TopColumns : RoundColumns
    {
        public float[] tvel = null!;
        public float[] adir = null!, awant = null!;

        private static readonly string[] top_names = Components.Cat(names, "tgt_vel",
                                                                    "a_dir", "a_want");
        public static readonly string[] OnTop = Components.Cat(top_names, "top_attack");

        public TopColumns(Group g) : base(g) { }

        public override string[] Names => top_names;

        protected override void bind(Group g)
        {
            base.bind(g);
            tvel = g.f("tgt_vel");
            adir = g.f("a_dir"); awant = g.f("a_want");
        }

        /// <summary>The velocity the stare point walks at, row `i`.</summary>
        public Vec3 tvel_at(int i) => new(tvel[i * 3], tvel[i * 3 + 1], tvel[i * 3 + 2]);
    }
}
