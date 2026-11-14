// Static game data: factions, entity types, unit statistics and weapons.
//
// Everything here is also sent to the browser, so client prediction uses exactly
// the same ballistics numbers as the server.  Two consequences for this file:
//
//   * a key that is *absent* in the Python tables must be absent from the JSON
//     as well, never a zero - the client branches on `wd.reach !== undefined` and
//     on `wd.rack ? ... : ...` - so those fields are nullable and carry
//     `[JsonIgnore(WhenWritingNull)]`;
//   * every numeric field is `float`.  The tables are compared against by the
//     client, by `ballistics`, by `flight` and by the wire, all of which are
//     float32 - see `doc_backend_dotnet/01_porting_rules.md`.
//
// A JSON key keeps the Python dict key as the C# field name (`ammo_cost`,
// `box_len`, `hit_r`), so no `[JsonPropertyName]` is needed anywhere: the field
// name *is* the wire name.  One caveat that matters: the tables are public
// *fields*, and `System.Text.Json` skips fields unless it is told not to, so
// anything that serialises them has to go through `Defs.WIRE_JSON` (below) or set
// `IncludeFields` itself - otherwise the client gets `{}` for every weapon and
// every unit, which is the one failure it cannot survive.
//
// This is a line-for-line port of `backend/server/defs.py`; the comments that
// explain a rule or a measured number are the ones from there.

namespace Mech.Server;

using System.Text.Json.Serialization;

// ---------------------------------------------------------------- factions
public static partial class Defs
{
    // The JSON options these tables need: everything here is a public *field*, and
    // `System.Text.Json` only looks at properties unless `IncludeFields` is set.  The
    // key names are the C# field names (`ammo_cost`, `muzzle`...), so no naming policy
    // may be applied on top - the client reads them verbatim.
    public static readonly System.Text.Json.JsonSerializerOptions WIRE_JSON = new()
    {
        IncludeFields = true,
    };

    public const int FACTION_RED = 0;
    public const int FACTION_BLUE = 1;
    public const int FACTION_CIV = 2;
    public const int FACTION_NATURE = 3;

    public static readonly Dictionary<int, float[]> FACTION_COLORS = new()
    {
        [FACTION_RED] = new[] { 0.85f, 0.24f, 0.18f },
        [FACTION_BLUE] = new[] { 0.22f, 0.44f, 0.92f },
        [FACTION_CIV] = new[] { 0.62f, 0.36f, 0.85f },
        [FACTION_NATURE] = new[] { 0.90f, 0.90f, 0.88f },
    };

    public static readonly Dictionary<int, string> FACTION_NAMES = new()
    {
        [FACTION_RED] = "Red",
        [FACTION_BLUE] = "Blue",
        [FACTION_CIV] = "Civilians",
        [FACTION_NATURE] = "Nature",
    };

    // ------------------------------------------------------------ entity types
    public const int ETYPE_NONE = 0;
    public const int ETYPE_INF_RIFLE = 1;
    public const int ETYPE_INF_ROCKET = 2;
    public const int ETYPE_APC = 3;
    public const int ETYPE_MECH = 4;
    public const int ETYPE_BASE = 5;
    public const int ETYPE_CIV = 6;
    public const int ETYPE_DEBRIS = 7;      // inert wreck (vehicle / mech hulk), purely visual
    public const int ETYPE_CORPSE = 8;      // fallen infantry, purely visual
    public const int ETYPE_BUNKER = 9;      // indestructible building, a zeroing reference on the range

    // mech module indices (hit geometry and health array slots).  A hit on either
    // body sphere - the chassis or the torso on top of it - is charged to the one
    // hull pool at MOD_CHASSIS; MOD_TORSO only exists so the geometry can name the
    // upper sphere, and its health slot is unused.
    public const int MOD_CHASSIS = 0;
    public const int MOD_TORSO = 1;
    public const int MOD_LEFT = 2;
    public const int MOD_RIGHT = 3;
    public static readonly string[] MODULE_NAMES = { "HULL", "TORSO", "L.ARM", "R.ARM" };

    // ----------------------------------------------------------------- weapons
    public const int WID_NONE = 0;
    public const int WID_AC30 = 1;
    public const int WID_MG127 = 2;
    public const int WID_ROCKET = 3;
    public const int WID_SACLOS = 4;
    public const int WID_TOPATTACK = 5;
    public const int WID_RIFLE556 = 6;
    public const int WID_RPG = 7;
    public const int WID_G105 = 8;
    // the APC's own defence missile: the same round as the mech's SACLOS, fired
    // out of its turret one round every 5 s (see the entry in WEAPONS)
    public const int WID_APC_ATGM = 9;
}

// A launcher that carries its rounds in tubes is described once, here, and both
// halves read that one description: the server fires the n-th round out of the
// tube the lattice puts it in (`rack_tube`), the client draws the same lattice
// and the body around it from the same numbers (`scene.js armModel`), and the
// mouth of the tubes *is* the weapon's reach, so a round leaves the mouth it was
// drawn on.  A new racked launcher is therefore a data change, not code.
//
// The Python spec is a plain dict (`rack(...)`), so this is a plain class with
// public fields named exactly like its keys: it is what `rack_tube`,
// `Systems/Weapons.cs` and `scene.js` (`spec.cols`, `spec.pitch`, ...) all read,
// and it serialises back to the same JSON object.
public sealed class RackSpec
{
    public int cols;            // cells across
    public int rows;            // cells high
    public int count;           // rounds actually carried (<= cols * rows)
    public float bore;          // tube outside diameter, m
    public float length;        // tube length, m
    public float pitch;         // centre spacing, m (bore + wall unless given)
    public float centre;        // how far along the bore the launcher sits, m
    public float mouth;         // centre + length / 2: where a round leaves from
    public float elev;          // the launcher's own elevation, rad
    public bool bolted;         // the arm's slewing does not move the tubes at all
    public float box_len;       // the body drawn round the lattice
    public float box_w;
    public float box_h;
}

public static partial class Defs
{
    // ------------------------------------------------------------- tube racks
    /// <summary>Describe a tube launcher: the lattice, the tubes and the body round them.</summary>
    /// <remarks>
    /// <para>cols / rows  the lattice - `count` rounds are carried (default: every cell)</para>
    /// <para>bore         tube outside diameter, m</para>
    /// <para>length       tube length, m</para>
    /// <para>wall         gap between two tube walls, m  ->  pitch = bore + wall</para>
    /// <para>pitch        centre spacing, when the tubes are drawn overlapping (the
    /// rocket nest packs its cells 0.155 m apart on a 0.135 m bore)</para>
    /// <para>centre       how far the launcher sits along the bore, m</para>
    /// <para>end          how far the tubes poke out of each end face, m</para>
    /// <para>frame        how much wider the body is than the lattice, per side, m</para>
    /// <para>elev         the launcher's own elevation, rad: what it adds to the angle
    /// of the arm it is hung on (0 = mounted flat, the tubes follow the arm exactly -
    /// depression included)</para>
    /// <para>bolted       the tubes are fixed at `elev` and the arm's slewing does not
    /// move them at all: the top attack's 42 deg loft is a property of the launcher,
    /// not of where the pilot is looking</para>
    /// <para>box          explicit (length, width, height) when the drawn body is not
    /// the one the lattice derives</para>
    /// <para>The body is a box around the cells and nothing more: the lattice plus
    /// `frame`, `end` shorter than the tubes at each end - so it reads as a launcher
    /// with mouths rather than as a solid block with dots on it.</para>
    /// </remarks>
    public static RackSpec rack(int cols, int rows, int? count = null, float bore = 0.19f,
                                float length = 1.30f, float centre = 0.75f, float wall = 0.04f,
                                float? pitch = null, float end = 0.10f, float frame = 0.10f,
                                float elev = 0.0f, bool bolted = false, float[]? box = null)
    {
        int cells = cols * rows;
        int n = count ?? cells;
        if (!(0 < n && n <= cells))
            throw new ArgumentException($"rack: {n} rounds do not fit {cols}x{rows} cells");
        float p = pitch ?? bore + wall;
        float lat_w = (cols - 1) * p + bore;
        float lat_h = (rows - 1) * p + bore;
        float bl = length - 2.0f * end, bw = lat_w + 2.0f * frame, bh = lat_h + 2.0f * frame;
        if (box is not null)
        {
            bl = box[0];
            bw = box[1];
            bh = box[2];
        }
        return new RackSpec
        {
            cols = cols, rows = rows, count = n, bore = bore,
            length = length, pitch = p, centre = centre,
            mouth = centre + length * 0.5f, elev = elev,
            bolted = bolted, box_len = bl, box_w = bw, box_h = bh,
        };
    }

    /// <summary>(right, up) of the tube the `fired`-th round of a rack leaves from.</summary>
    /// <remarks>
    /// Bottom row first, left to right - so a forty-cell nest with 32 rockets in it
    /// has its lowest eight cells empty and a four-cell rack empties its lower pair
    /// first.  `count` may be smaller than the lattice (`rack` refuses anything that
    /// does not fit); the walk then simply stops before the top row.
    /// </remarks>
    public static (float right, float up) rack_tube(int fired, RackSpec spec)
    {
        int cols = spec.cols, count = spec.count;
        float pitch = spec.pitch;
        int i = fired % count;
        int col = i % cols, row = i / cols;
        return ((col - (cols - 1) / 2.0f) * pitch,
                (row - (spec.rows - 1) / 2.0f) * pitch);
    }

    // The rocket nest: forty rounds, forty cells, five across and eight high.  The
    // cells are 0.135 m hexagons packed 0.155 m apart (a little air between them) in
    // a 0.82 m body, which is what makes the face read as a roll of tubes rather
    // than as a slab with dots on it.
    public static readonly RackSpec RACK_ROCKET = rack(5, 8, 40, bore: 0.135f, pitch: 0.155f,
                                                       length: 2.15f, centre: 0.60f,
                                                       box: new[] { 1.90f, 0.82f, 1.26f });
    // The top-attack rack: four tubes as big as the round in them - 0.20 m across
    // and 1.40 m long - a hand's width between centres, and a body a wall thick
    // round the whole lattice (2 x 2 at 0.20 m needs 0.50 m, so 0.74 m is frame and
    // armour).  The body is centred on the arm's own axis (`centre` 0: the shoulder
    // is its middle, not its rear end), and the tubes are a fifth longer than the
    // body, hanging out of *both* end faces - so the mouths read from in front and
    // from behind, and a launcher on an arm you can walk round is a row of tubes,
    // not a solid block.  The launcher is bolted at its own quadrant (`elev`,
    // 42 deg): the tubes do not follow the arm, which is why the missile always
    // leaves at that angle whatever the pilot's cross is doing - and why the drawn
    // tubes have to stand at the same angle.
    //
    // WORKAROUND (2026-10-30): the machine's own model was never dimensioned
    // against the weapons hung on it, so a launcher drawn at its real 0.20 x 1.40 m
    // reads as a toy next to a 3.6 m hull, and at twice that it swamps it.  The
    // tubes sit at 1.2x real - 0.24 m across and 1.70 m long - in a lattice of 2 x 4
    // = eight rounds.  Nothing else about the machine is re-dimensioned; when the
    // hull is, these numbers come down to a real ATGM launcher (0.20 x 1.40).
    //
    // The cells are packed the way the rocket nest packs its forty - its 0.155 m
    // pitch on a 0.135 m bore is 1.15 tube widths, and that is what makes a face
    // read as a bundle of tubes instead of a grid of dots.  Same ratio here: eight
    // 0.24 m tubes on a 0.28 m pitch, two across and four high, in a body that is
    // nothing but a 0.025 m sheet-metal edge round them (0.53 x 1.09 x 1.20 m).
    // Not a wall-spaced lattice (a 0.60 pitch and a 0.24 frame on the doubled bore):
    // that came out 1.48 m wide and sat 0.17 m *inside* the hull, and it read as
    // eight dots in a big empty box.
    public static readonly RackSpec RACK_TOP = rack(2, 4, 8, bore: 0.24f, pitch: 0.28f,
                                                    length: 1.70f, centre: 0.0f, end: 0.25f,
                                                    frame: 0.025f, elev: 0.73299f, bolted: true);
    // The SACLOS rack: the same tubes and the same packing, four across and four
    // high - sixteen rounds in one flat pack.  It is not bolted: a beam rider has
    // to clear the arm on a soft launch, so the launcher sits 0.18 rad (10 deg)
    // above whatever the arm is aimed at - and the arm still depresses, the tube a
    // tenth of the way down with it.  The round leaves along the tube, so the same
    // bias applies to both, and the guidance absorbs it the moment it is out.
    public static readonly RackSpec RACK_SACLOS = rack(4, 4, 16, bore: 0.24f, pitch: 0.28f,
                                                      length: 1.70f, centre: 0.0f,
                                                      end: 0.25f, frame: 0.025f, elev: 0.18f);
}

// One entry of `WEAPONS`: everything the server, the client and the ballistics
// need to fire one round.
//
// The field comments are the weapon table's legend from `defs.py`; the ones that
// are per-weapon (a measured kill count, a rate's reason) live on the entry
// itself.
public sealed class WeaponDef
{
    public int wid;                 // the index it lives at: `WEAPONS` *is* the id space
    public string key = "";         // stable client-side name, e.g. "ac30"
    public string name = "";        // what the HUD calls it
    // kind: bullet (ballistic), rocket (unguided thrust), saclos (beam riding),
    //       top (infrared lock-on, top attack), none (an empty arm)
    public string kind = "none";
    // ap / ad: Armour Piercing power and the Armour-piercing Damage it delivers
    //          behind the plate, both measured against a target's `dp`
    // ed / er: Explosive Damage at the centre of the blast and the radius (m) it
    //          falls off to nothing over, measured against a target's `de`
    public float ap, ad, ed, er;
    public int rpm;
    // one belt of `mag`, then `reserve` more in `reload`-second chunks; a racked
    // launcher is one round per tube and has neither reserve nor reload
    public int mag, reserve;
    public float reload;
    public float vel;               // muzzle velocity, m/s
    public float spread;            // rad, one sigma
    // the mechanical elevation the launcher is sighted at, rad: the rocket pod is
    // tilted up so its slow round meets the cannon's line at 1 km
    public float bias;
    public float g;                 // gravity on the round, m/s^2 (0 = flies flat)
    public float thrust;            // m/s^2 from the motor, 0 = none
    public float maxvel;            // m/s the motor tops out at
    public float life;              // s of flight before the round cooks off
    // what happens when `life` runs out: true = the warhead goes off where the
    // round stands, false = the round is gone and leaves nothing behind
    // (`Systems/Flights.cs::Expire`; running out of *wire* always cooks off)
    public bool expire_burst;
    public int range;               // m at which the round still carries its energy
    // m the *seeker* holds a lock to, for the kinds that need one (`@lock`).  A lock
    // weapon's `range` is the crew's launch decision, and a pilot's reach is not the
    // AI's: the top attack is fired at 3 km and locked at 4.  0 = the same as `range`.
    public int lock_range;
    public int cost;                // $ for the hardware; -1 = not for sale
    public float ammo_cost;         // $ per round of a full load
    // `lock` is a C# keyword, so the field is written `@lock` - the metadata name,
    // and therefore the JSON key, is the Python one.
    public int @lock;                // 1 = needs an IR lock before it can be fired
    public float lock_time;         // s the seeker takes to lock
    public int salvo;               // rounds one pull of the trigger lets go
    public int semi;                // 1 = semi-automatic (one round per pull)
    public float[] color = new float[3];  // tracer / body colour, rgb 0..1
    // cal: bore in mm, only used to size the tracer the browser draws
    public float cal;
    public string desc = "";

    // ---- optional, and absent from the JSON when they are ----
    // prox: proximity fuse radius (m) - the round bursts when its flight path
    //       passes that close to enemy *personnel* (systems/proximity.py).
    //       0 = impact fuse only.  A hull never sets it off.
    public float prox;
    // gatling: a rotary gun, client-only: the arm's model draws six barrels round
    //          the bore and turns them while the arm is firing
    public bool gatling;
    // heavy:   a single big barrel on a boxed receiver, client-only: the 105 mm is
    //          modelled to its own numbers (scene.js `GUN105_*`) instead of the
    //          30 mm's slim tube
    public bool heavy;
    // reach:   metres from the shoulder that the bore ends at, when the weapon is
    //          longer than its kind's figure in MECH_ARM_REACH (the 105 mm's box
    //          ends at 0.90 m and its barrel runs 5 m out from there).  Both
    //          `weapons.fire` and the client's muzzle read it, so a round leaves
    //          the mouth it was drawn on.  Null = use the rack's mouth, then the
    //          kind's figure - and the client tests `!== undefined`, so an absent
    //          reach must stay absent on the wire.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public float? reach;
    // rack:    `rack(...)` above for a launcher that carries its rounds in tubes -
    //          a round leaves its own tube.  Null = the round leaves the bore.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RackSpec? rack;
    // body:    how the round itself is drawn, (across, long) metres
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public float[]? body;

    // ---- guided rounds: filled in by the finish pass (see the static ctor) ----
    // rcs:     radar cross-section, m^2, what the RWR and an AI's radar see it by
    public float rcs;
    // hit_r:   the half-calibre, i.e. the round's actual frontal size - not a
    //          playability knob; the 30 mm's 2.5 m proximity fuse is the intended
    //          way to bring one down, and a direct hit on a 0.08 m ball is a
    //          sniper's shot
    public float hit_r;
    // aim_hp / aim_dp / aim_de: the round as a 10 hp, 1/1 armour target, so
    //          `combat.hit`/`splash`/`apply` work on it with no special case
    public float aim_hp, aim_dp, aim_de;

    // ---- the airframe / the motor, per kind ----
    public float burn;              // s the motor burns
    public float drag;              // quadratic drag per metre (a SACLOS coasts on it)
    public float wob;               // beam-rider wobble, m
    public float wob_f = 1.0f;      // Hz the wobble swings at
    public float maxdist;           // m the wire / command link reaches
    public float ignite;            // s of delay before the motor lights
    public float loft;              // rad the tube is elevated to (the rack's own elev)
    public float apex;              // m the climb is planned to
    public float turn_r;            // m of turn radius the guidance asks for
    public float pn_n, pn_r, pn_v;  // proportional navigation: gain, range, speed floor
    public float mass;              // kg
    public float dia;               // m, the aero reference diameter
    public float cd0;               // profile drag coefficient
    public float k_cl;              // induced drag factor
    public float cl_alpha;          // lift slope, per rad
    public float cl_max;            // the airframe's lift ceiling
}

public static partial class Defs
{
    // `WEAPONS` is indexed by `wid` (`MAG_OF`, the client's `defs.weapons[wid]`), so
    // the order *is* the id space.  The APC's ATGM is last on purpose.
    // ranges/muzzle velocities follow real world hardware; range is the distance at
    // which the round still carries its nominal energy, while life is a hand-written
    // estimate of 10 km of flight: 10000 m / the round's speed, rounded.  Nothing is
    // meant to expire in the air inside the map.
    public static readonly WeaponDef[] WEAPONS =
    {
        new WeaponDef
        {
            wid = WID_NONE, key = "none", name = "EMPTY", kind = "none",
            ap = 0f, ad = 0f, ed = 0f, er = 0f,
            rpm = 1, mag = 0, reserve = 0, reload = 1f, vel = 1f, spread = 0f, bias = 0.0f,
            g = 0f, thrust = 0f, maxvel = 0f, life = 1f, range = 0, cost = 0, ammo_cost = 0f,
            @lock = 0, lock_time = 0f, salvo = 0, semi = 0,
            color = new[] { 0.8f, 0.8f, 0.8f }, cal = 0f, desc = "No weapon fitted",
        },
        // one belt of 400, no reload: the magazine boundary is gone, so a pull of
        // the trigger runs the whole load downrange and the gun only goes quiet when
        // the arm is dry.  400 rounds is 80 s of held trigger at the 300 rpm below,
        // and a full load costs $300 + 400 x $0.5 = $500.
        // 300 rpm is six ticks a round: the 30 Hz clock divides it outright, where
        // the old 190 (9.47 ticks) fired an uneven 9/10 tick pulse.  It is also the
        // real 2A72's cadence, and it is what the 10.6 a round direct hit is *for* -
        // a 120 hull dies 12 rounds in (AD 7 through the plate plus the 3.6 the burst
        // keeps off a 20-de armour), i.e. 3.2 s to a kill at 70 % hits.  The rate was
        // raised and the behind-armour effect lowered to keep that number: 300 rpm at
        // the old AD 10 was 2.1 s, which read as "the autocannon is the best
        // anti-armour weapon in the game".
        // The round carries an airburst fuse: 2.5 m off an enemy rifleman it goes off
        // instead of planting itself in the dirt behind him, so a burst of cannon
        // fire sweeps infantry up without needing a direct hit on a 0.65 m sphere.
        // Only people set it off - an APC at the same distance is ignored and waits
        // for the impact fuse.  The trigger is set *inside* the burst (2.5 m against
        // a 3.11 m one-round kill on a man, ED 18 / ER 8): a fuse that fired in a
        // band where the fragments cannot kill would be spending its own trigger.
        new WeaponDef
        {
            wid = WID_AC30, key = "ac30", name = "30mm AUTOCANNON", kind = "bullet",
            ap = 15f, ad = 7f, ed = 18f, er = 8.0f,
            rpm = 300, mag = 400, reserve = 0, reload = 0.0f, vel = 1080f, spread = 0.0011f,
            bias = 0.0f, g = 9.81f, thrust = 0f, maxvel = 0f, life = 9.3f, range = 3400,
            cost = 300, ammo_cost = 0.5f, @lock = 0, lock_time = 0f, salvo = 0, semi = 0,
            color = new[] { 1.0f, 0.82f, 0.25f }, cal = 30f, prox = 2.5f,
            desc = "High velocity, 3.4 km effective",
        },
        // one belt of 1500, no reload: the gun runs dry and that is it.  1800 rpm is
        // exactly one round per tick, the fastest the 30 Hz clock can fire - twice the
        // cadence of the old 680 rpm (which quantised to one round every 3 ticks).
        // It is a rotary gun (`gatling`, a client-only flag): six barrels on a ring,
        // so the client draws it turning while this arm is firing and puts the muzzle
        // flash on whichever barrel has come up to the top of the ring.
        new WeaponDef
        {
            wid = WID_MG127, key = "mg127", name = "12.7mm MACHINE GUN", kind = "bullet",
            ap = 8f, ad = 10f, ed = 0f, er = 0f,
            rpm = 1800, mag = 1500, reserve = 0, reload = 0.0f, vel = 890f, spread = 0.00225f,
            bias = 0.0f, g = 9.81f, thrust = 0f, maxvel = 0f, life = 11.2f, range = 1000,
            cost = 180, ammo_cost = 0.1f, @lock = 0, lock_time = 0f, salvo = 0, semi = 0,
            color = new[] { 1.0f, 0.68f, 0.20f }, cal = 12.7f, gatling = true,
            desc = "1.0 km suppressive fire",
        },
        // S-8 class: a launch charge throws the 80 mm rocket out at ~55 m/s, the motor
        // burns for a second and it leaves at 610 m/s.  A small shaped charge plus a
        // lot of frag: it is the pod's *burst*, not the single round, that kills -
        // three rockets on the hull, two on an arm.
        // AD 20 -> 25 is what keeps that "three on the hull" true against a 120 hp
        // mech: 25 through the plate plus the 15 the burst keeps off a 20-de armour is
        // 40 a hit, i.e. exactly 3 x 40 = 120.  Anything less (AD 24 is 39) makes it
        // four, and the pod's 40 rounds would carry ten hulls instead of thirteen.
        // Only the direct hit moved: the burst, its 10 m, the infantry sweep and the
        // 267 rockets the command base costs are untouched.
        new WeaponDef
        {
            wid = WID_ROCKET, key = "rocket", name = "UNGUIDED ROCKET POD", kind = "rocket",
            ap = 40f, ad = 25f, ed = 35f, er = 10.0f,
            rpm = 600, mag = 40, reserve = 0, reload = 0.0f, vel = 55f, spread = 0.003f,
            bias = 0.0429f, g = 9.81f, thrust = 560f, maxvel = 610f, life = 16.4f,
            range = 1000, cost = 350, ammo_cost = 4.0f, @lock = 0, lock_time = 0f, salvo = 1,
            semi = 0, color = new[] { 1.0f, 0.45f, 0.15f }, cal = 80f, rcs = 0.005f,
            rack = RACK_ROCKET, desc = "40 rounds, hold to fire, sighted to 1 km",
        },
        // TOW class: 40 m/s soft launch, then 100 m/s^2 of boost for 5.6 s takes it to
        // its 600 m/s cruise - and that is where the motor burns out, 1.8 km down
        // range.  After that it coasts on the airframe alone, and a missile is a blunt
        // thing: quadratic drag (`drag` per metre) bleeds it back down, ~385 m/s by the
        // far end of the wire.  The wire itself is 4 km (`maxdist`): the round cooks
        // off where it runs out, so the reach is the wire and not the motor.  Fast
        // out, lazy in, and a long shot is a commitment rather than a sniper rifle.
        // A tandem HEAT warhead: AD 120 is past every hull pool in the game (mech 120,
        // APC 100, arm 50), so one hit takes a machine apart without doing the base's
        // 4000 hp much of a favour - the price is that the operator has to hold the
        // sight on it.
        //
        // Sixteen rounds in a flat 4 x 4 pack, the same tubes as the top attack and
        // the same mechanism: one round per tube, no magazine, no reload, and 0.3 s
        // between launches (`rpm` 200) - a beam rider's burst is a magazine's worth of
        // commitments fired a sight-correction apart.
        new WeaponDef
        {
            wid = WID_SACLOS, key = "saclos", name = "SACLOS ATGM", kind = "saclos",
            ap = 800f, ad = 120f, ed = 80f, er = 6.0f,
            rpm = 200, mag = 16, reserve = 0, reload = 0.0f, vel = 40f, spread = 0.003f,
            bias = 0.0f, g = 0.0f, thrust = 100f, burn = 5.6f, drag = 0.0002f, maxvel = 600f,
            life = 36.0f, wob = 30.0f, wob_f = 1.5f, maxdist = 4000.0f,
            range = 3400, cost = 500, ammo_cost = 30.0f, @lock = 0, lock_time = 0f, salvo = 0,
            semi = 0, color = new[] { 0.35f, 1.0f, 0.55f }, cal = 152f, rcs = 0.02f,
            rack = RACK_SACLOS, desc = "Beam riding, keep the sight on target",
        },
        // Javelin class, flown as an aeroplane rather than as a curve: the tube is
        // elevated to `loft`, a 20 m/s soft launch, then 3.4 s of motor (11.2 g) that
        // burns out at 0.9 M - `flight.py` has the whole model.  From there it is an
        // unpowered glide that trades the climb for range, arriving down a 35-42 deg
        // attack line, with the last 300 m on proportional navigation.
        // `mass`/`dia`/`cd0`/`k_cl`/`cl_alpha`/`cl_max` are the airframe, not tuning: a
        // hard pull really costs the speed it costs (an induced CD of 3 against a
        // profile drag of 0.75 at the 17.9 deg stop).
        //
        // The range ceiling is the battery (`life`, 30 s), not the motor: a parked hull
        // dies at 4.3 km, one running away at 3.5-4 km, and past that the warhead cooks
        // off in the air (projectiles' battery branch).  Lock range is the seeker's own
        // `range` - 4 km, which is what the shot can actually reach.  Fire and forget,
        // AD 120 takes any hull in one hit; the price is the lock, the 5-27 s flight
        // and the climb the target can hide behind - measured envelope in
        // doc/top_attack_plan.md, tool tools/top_attack_trace.py.
        //
        // Eight rounds in a 2 x 4 rack and nothing behind them: no magazine, no reload,
        // 0.3 s between launches (`rpm` 200) - fast enough that a crew can put the whole
        // rack at one hull inside a second, which is why the AI has its own
        // `AI_LAUNCH_GAP` floor and a pilot does not.  The rack is the rocket pod's
        // mechanism, not a second one - one round per tube, out of the tube it was
        // drawn in - and a fresh rack is bought at the base like a fresh belt.
        // `body` is how the round itself is drawn, (across, long): a 1.3 m missile in a
        // 1.4 m tube, so what leaves the launcher is the size of the mouth it leaves
        // from.  (`dia` is not that - it is the aero reference area, 127 mm, and moving
        // it would move the measured envelope.)
        new WeaponDef
        {
            wid = WID_TOPATTACK, key = "topattack", name = "TOP-ATTACK IR ATGM", kind = "top",
            ap = 900f, ad = 120f, ed = 60f, er = 7.0f,
            rpm = 200, mag = 8, reserve = 0, reload = 0.0f, vel = 20f, spread = 0.002f,
            bias = 0.0f, g = 9.81f, thrust = 110.0f, maxvel = 306.0f, life = 30.0f,
            expire_burst = true, range = 3000, lock_range = 4000, cost = 700, ammo_cost = 45.0f,
            mass = 20.0f, dia = 0.127f, cd0 = 0.75f, k_cl = 0.12f, cl_alpha = 16.0f,
            cl_max = 5.0f, burn = 3.4f, ignite = 0.15f, loft = RACK_TOP.elev, apex = 950.0f,
            turn_r = 700.0f, pn_n = 4.0f, pn_r = 300.0f, pn_v = 120.0f,
            rack = RACK_TOP, body = new[] { 0.18f, 1.30f },
            @lock = 1, lock_time = 0.3f, salvo = 0, semi = 0,
            color = new[] { 1.0f, 0.35f, 0.9f }, cal = 127f, rcs = 0.02f,
            desc = "4 km lock-on, fire and forget",
        },
        new WeaponDef
        {
            wid = WID_RIFLE556, key = "rifle556", name = "5.56mm RIFLE", kind = "bullet",
            ap = 5f, ad = 4f, ed = 0f, er = 0f,
            rpm = 430, mag = 30, reserve = 900, reload = 2.2f, vel = 900f, spread = 0.007f,
            bias = 0.0f, g = 9.81f, thrust = 0f, maxvel = 0f, life = 11.1f, range = 800,
            cost = -1, ammo_cost = 0.05f, @lock = 0, lock_time = 0f, salvo = 0, semi = 0,
            color = new[] { 1.0f, 0.9f, 0.5f }, cal = 5.56f, desc = "800 m rifle",
        },
        new WeaponDef
        {
            wid = WID_RPG, key = "rpg", name = "RPG LAUNCHER", kind = "rocket",
            ap = 100f, ad = 40f, ed = 20f, er = 3.0f,
            rpm = 25, mag = 1, reserve = 900, reload = 6.5f, vel = 50f, spread = 0.008f,
            bias = 0.0f, g = 9.81f, thrust = 380f, maxvel = 300f, life = 33.3f, range = 800,
            cost = -1, ammo_cost = 8.0f, @lock = 0, lock_time = 0f, salvo = 0, semi = 0,
            color = new[] { 1.0f, 0.5f, 0.2f }, cal = 85f, rcs = 0.002f,
            desc = "800 m anti-tank rocket",
        },
        // 105 mm smoothbore: a tank gun hung on a mech arm, not an autocannon.  One
        // round in the breech, three seconds of autoloader between shots, twelve
        // rounds in the rack - so the whole arm is twelve trigger pulls a battle, and
        // the magazine is 1 on purpose (`mag` 1 / `reserve` 11 is what makes the reload
        // the cycle: a shot, then the countdown, then another).  1800 m/s of APFSDS:
        // 24 m of drop at 4 km is the fire control's problem, not the pilot's, and
        // 0.3 mrad of spread is a coin across 3 km.  AD 200 is past every hull pool in
        // the game and AP 120 past the base's 80 of plate, so it is the one gun that
        // takes a mech, an APC or a chunk of the enemy base apart with a single round -
        // which is what a $800 barrel and $600 of ammunition buys.
        new WeaponDef
        {
            wid = WID_G105, key = "g105", name = "105mm SMOOTHBORE", kind = "bullet",
            ap = 120f, ad = 200f, ed = 15f, er = 3.0f,
            rpm = 20, mag = 1, reserve = 11, reload = 3.0f, vel = 1800f, spread = 0.0003f,
            bias = 0.0f, g = 9.81f, thrust = 0f, maxvel = 0f, life = 5.6f, range = 4000,
            cost = 800, ammo_cost = 60.0f, @lock = 0, lock_time = 0f, salvo = 0, semi = 0,
            color = new[] { 1.0f, 0.90f, 0.55f }, cal = 105f, heavy = true, reach = 6.45f,
            desc = "APFSDS, 3 s autoloader",
        },
        // The APC's own anti-tank missile: the same airframe, warhead and beam as the
        // mech's SACLOS, forked so the armoured car is not handed a mech's launcher.
        // Everything is that round; the one difference is the cycle - 12 rpm, one round
        // every 5 s, because a two-tube turret reloads by hand and an APC that could put
        // sixteen beam riders into the air a sight-correction apart out-trades the
        // machine that is supposed to kill it.  The `rack` is only here for `elev`: it
        // holds the tube at the 10 deg a soft launch needs.
        //
        // Last in the list on purpose: `WEAPONS` is indexed by `wid`.
        new WeaponDef
        {
            wid = WID_APC_ATGM, key = "apc_atgm", name = "APC ATGM", kind = "saclos",
            ap = 800f, ad = 120f, ed = 80f, er = 6.0f,
            rpm = 12, mag = 16, reserve = 0, reload = 0.0f, vel = 40f, spread = 0.003f,
            bias = 0.0f, g = 0.0f, thrust = 100f, burn = 5.6f, drag = 0.0002f, maxvel = 600f,
            life = 36.0f, wob = 30.0f, wob_f = 1.5f, maxdist = 4000.0f, range = 3000,
            expire_burst = true,
            cost = -1, ammo_cost = 0.0f, @lock = 0, lock_time = 0f, salvo = 0, semi = 0,
            color = new[] { 0.35f, 1.0f, 0.55f }, cal = 152f, rcs = 0.02f,
            rack = RACK_SACLOS, desc = "Beam riding, one round every five seconds",
        },
    };

    // ------------------------------------------------------- missiles as targets
    // A guided or rocket round is a *thing in the air*, not just a trajectory: it
    // carries a radar cross-section (`rcs`), a collision sphere and a small pool of
    // health, so it can be shot down.
    public static readonly string[] INTERCEPT_KINDS = { "rocket", "saclos", "top" };

    /// <summary>
    /// The Python table finishes itself in a loop right after it is declared
    /// (`for _w in WEAPONS: _w.setdefault(...)`, then `hit_r = cal / 2000`): a round
    /// of one of `INTERCEPT_KINDS` is given a 10 hp / 1-1 armour target's worth of
    /// health and a hit sphere the size of its own calibre.  Same pass, same numbers,
    /// once, before anything can read a `WeaponDef`.
    /// </summary>
    /// <remarks>
    /// `aim_hp` 10 against the 30 mm burst (ED 18 / ER 8): the fuse's own trigger,
    /// 2.5 m, is 11.4 points - a burst that fires at all is a kill.  Where it used to
    /// be a 4 m trigger against a 2.67 m lethal radius (8 points at the edge, the
    /// round flew on), the trigger now sits *inside* the pattern, and the tighter
    /// fuse is what keeps interception rate honest: the AI's bore gate is
    /// `atan((prox + hit_r)/d)`, so a shorter fuse asks for a better shot instead of
    /// a wider burst.
    /// </remarks>
    static Defs()
    {
        foreach (var w in WEAPONS)
        {
            if (Array.IndexOf(INTERCEPT_KINDS, w.kind) < 0) continue;
            w.aim_hp = 10.0f;
            w.aim_dp = 1.0f;
            w.aim_de = 1.0f;
            // `rcs` needs no default here: every interceptable round declares its own.
            w.hit_r = w.cal / 2000.0f;
        }
    }

    /// <summary>May this weapon's rounds be locked, fused and shot down?</summary>
    public static bool is_interceptable(int wid) => Array.IndexOf(INTERCEPT_KINDS, WEAPONS[wid].kind) >= 0;

    /// <summary>m a seeker can lock a hull with this weapon at: its own
    /// `lock_range`, or `range` when it sets none.</summary>
    public static int lock_range(WeaponDef wd) => wd.lock_range > 0 ? wd.lock_range : wd.range;

    // ------------------------------------------------------- first_hit_record
    // The round's `first_hit_t` while nothing has been hit this tick.  A leg is
    // a fraction in [0,1]; the record is *armed* at 2 so "the round ran into
    // nothing" needs no second flag, and a system that finds a later hit leaves
    // the record alone.
    public const float FIRST_HIT_ARMED = 2f;

    // what the record says the round ran into.  `GROUND`/`BUILDING`/`TRUNK` are
    // three shapes of the same outcome today (a round that stops and bursts);
    // they are separate because a destructible building will want its own.
    public const byte HIT_NONE = 0, HIT_UNIT = 1, HIT_GROUND = 2, HIT_BUILDING = 3,
                      HIT_TRUNK = 4, HIT_FUSE = 5, HIT_EXPIRE = 6, HIT_VANISH = 7;

    /// <summary>The `proj_kind` byte a round carries (`Components.PROJ_COMPS`):
    /// its wire value and its `Defs.WEAPONS` key.  What it *flies* as is the tag
    /// below.</summary>
    public const byte PROJ_BULLET = 0, PROJ_ROCKET = 1, PROJ_SACLOS = 2, PROJ_TOP = 3;

    /// <summary>The tag components a weapon's rounds carry, by `kind`: identity is
    /// a component, never a `proj_kind` switch (the same rule `UNIT_DEFS.tags`
    /// follows).  `interceptable` is *not* here - it is `is_interceptable(wid)`,
    /// which reads the same `kind`, and one question gets one answer.</summary>
    public static readonly Dictionary<string, string[]> KIND_TAGS = new()
    {
        ["bullet"] = new[] { "bullet" },
        ["rocket"] = new[] { "unguided_missile" },
        ["saclos"] = new[] { "saclos" },
        ["top"] = new[] { "top_attack" },
    };

    // -------------------------------------------------------------------- radar
    // What a platform sees a missile by.  `plan.md` gave the half of the equation
    // that answers "how bright is it at this range"; the other half is that a
    // doppler receiver only sees what is *moving towards it*, so the echo is scaled
    // by (closing speed / V_REF)^2 and the test is `echo >= 1`, i.e.
    //
    //     radar_strength * rcs / d^4 * (v_closing / RADAR_V_REF)^2 >= 1
    //
    // which is solved for distance as `d^2 <= K * v_closing` with
    //
    //     K = sqrt(radar_strength * rcs) / RADAR_V_REF
    //
    // so nothing on the hot path needs a square root and nothing needs a threshold
    // constant.  `RADAR_V_REF` is not a physical quantity and not a tuning knob: it
    // is the closing speed for which the discovery range is exactly
    // (strength * rcs)^(1/4) - it only makes the speed term dimensionless, so
    // changing it is the same as rescaling every `radar_strength` at once.
    // 600 m/s is the game's fastest sustained round (SACLOS cruise, S-8 peak).
    public const float RADAR_V_REF = 600.0f;

    /// <summary>The constant of `d^2 &lt;= K * v_closing` for one (platform, round) pair.</summary>
    public static float radar_k(float strength, float rcs) =>
        MathF.Sqrt(MathF.Max(strength, 0.0f) * MathF.Max(rcs, 0.0f)) / RADAR_V_REF;

    /// <summary>Discovery distance against a round closing at RADAR_V_REF, metres.</summary>
    public static float radar_range(float strength, float rcs) =>
        MathF.Pow(MathF.Max(strength, 0.0f) * MathF.Max(rcs, 0.0f), 0.25f);

    // ------------------------------------------------------------------- damage
    // A hit resolves in two independent halves and the target takes the sum:
    //
    //   penetration   the round's AP against the target's DP.  A round that beats
    //                 DP outright puts its full AD through the plate; one that only
    //                 beats half of it shaves AD/5 (12.7 mm against a mech: 2
    //                 points); anything duller does not even scratch the paint.
    //   blast         the charge's ED against the target's DE, falling off linearly
    //                 to nothing at ER.  Explosives always tear *something* off, so
    //                 the result never drops below a fifth of the charge that
    //                 actually arrived.
    public static float pen_damage(float ap, float ad, float dp)
    {
        if (ap > dp)
            return ad;
        if (ap > dp * 0.5f)
            return ad / 5.0f;
        return 0.0f;
    }

    public static float blast_damage(float ed, float de, float er, float dist)
    {
        if (ed <= 0.0f)
            return 0.0f;
        float e;
        if (er > 0.0f)
            e = ed * MathF.Max(0.0f, 1.0f - MathF.Max(dist, 0.0f) / er);
        else
            e = dist <= 0.0f ? ed : 0.0f;
        return MathF.Max(e - de, e / 5.0f);
    }

    /// <summary>Damage one hit of `wd` does to armour `dp`/`de`, `dist` metres from the blast.</summary>
    public static float hit_damage(WeaponDef wd, float dp, float de, float dist = 0.0f) =>
        pen_damage(wd.ap, wd.ad, dp) + blast_damage(wd.ed, de, wd.er, dist);

    // A wreck goes up with its own fuel and ammunition: ER 30 m at ED 10, blast only
    // (a burst, not a round through a plate) - `Systems/Mortality.cs` spawns it from
    // the dying type's `death.blast`.  It is the one blast in the game that takes no
    // sides: what stands within thirty metres of a mech that just came apart is in
    // the wrong place whatever colour it is painted, so crews of the dead machine's
    // own faction are cooked too.  ED 10 over 30 m is a wide, thin shock - a scorch
    // on armour (2 points at contact) and a wound on men (9 at contact, 1 hp left),
    // so it finishes what was already broken instead of being a second warhead.
    // `er` doubles as the radius of the fireball the client draws, which is why the
    // two hulls carry it as their `splat`.
    public static readonly DeathBlastDef DEATH_BLAST = new() { ed = 10.0f, er = 30.0f, ad = 0.0f };

    // how long the dead thing's remains linger: a fallen man topples and is gone in
    // five seconds, a hull keeps smoking for ten.  Each type's `death.wreck` names
    // its own (see UNIT_DEFS) - `Systems/Debris.cs` counts them down.
    public const float CORPSE_TIME = 5.0f;
    public const float WRECK_TIME = 10.0f;

    // weapons a player may buy, in hotbar order
    public static readonly int[] BUYABLE =
        { WID_AC30, WID_MG127, WID_ROCKET, WID_SACLOS, WID_TOPATTACK, WID_G105 };

    // what a mech leaves the lobby with: the loadout is not a menu choice - it is
    // bought back at the wrench (shop) like any other piece of equipment - and it is
    // what a pilot comes back with after dying: the machine he was killed in is
    // gone, hardware and all
    public static readonly int[] DEFAULT_LOADOUT = { WID_AC30, WID_ROCKET };

    // ---- AI crews ------------------------------------------------------------
    // An AI crew has no economy to shop with, so a varied arsenal is bought with a
    // die roll instead: every mech hull fielded without an explicit loadout
    // (`World.spawn_unit`) draws one template off this table.  Weights are shares of
    // a single draw and have to add up to 1.
    public static readonly (float share, int[] loadout)[] AI_LOADOUTS =
    {
        (0.30f, new[] { WID_AC30, WID_ROCKET }),        // the standard pair
        (0.10f, new[] { WID_AC30, WID_AC30 }),          // twin cannon: twice the rate, no rocket
        (0.10f, new[] { WID_AC30, WID_SACLOS }),        // a beam rider in the right arm
        (0.25f, new[] { WID_AC30, WID_TOPATTACK }),     // fire and forget, over the roof
        (0.15f, new[] { WID_AC30, WID_G105 }),          // 105 mm: one-round kills at 2.6 km
        (0.10f, new[] { WID_AC30, WID_MG127 }),         // cannon left, cheap gun right
    };

    /// <summary>One template off `AI_LOADOUTS`, drawn with `rng.random()`.</summary>
    public static int[] ai_loadout(Rng rng)
    {
        float r = rng.random();
        foreach (var (share, loadout) in AI_LOADOUTS)
        {
            r -= share;
            if (r <= 0.0f)
                return loadout;
        }
        return AI_LOADOUTS[^1].loadout;
    }

    // ---- AI crews have names ----------------------------------------------
    // An AI mech is a crew, not a hull with a type, so the kill feed can say who did
    // what instead of "MECH killed MECH".  Every mech spawned without a pilot
    // (`World.spawn_unit`) draws one off this pool; a pilot's hull carries his own
    // callsign instead.  Names repeat freely - it is a roll of honour, not a roster.
    //
    // Only pilots here: the ones who actually fought in a mobile suit or armour, not
    // the officers, engineers, ship's crew and civilians around them.  Sub-lists are
    // by service so a later pass can tell what was chosen from where.
    public static readonly string[] AI_NAMES =
    {
        // Earth Federation - White Base's mobile suit pilots
        "Amuro Ray",              // RX-78-2
        "Sayla Mass",             // the same Gundam, and the Core Booster
        "Hayato Kobayashi",       // Guntank, then the Gundam
        "Kai Shiden",             // Guncannon
        "Ryu Jose",               // Guncannon
        "Bright Noa",             // a civilian machine, in the one raid he led himself
        "Marker Clan",            // Guncannon
        // Zeon - the aces, and the machines they are known for
        "Char Aznable",           // Zaku II, Gelgoog, Zeong
        "Dozle Zabi",             // Big Zam
        "Lalah Sune",             // Elmeth
        "Challia Bull",           // Braw Bro
        "Cucuruz Doan",           // Zaku II, a deserter
        // ...and the mobile suit teams
        "Ramba Ral", "Clamp", "Acous", "Cozun Graham", "Tachi", "Zeygan", "Stetch",
        "Denim", "Slender", "Gene", "Crown", "J.Q.", "Kohm",
        "Gadem", "Darota",
        "Akahana", "Ivanov", "Callahan", "Lasa", "Marcy", "Boraskyniv",
        "Asakura", "Cuaran", "Giyal", "Sol", "Ross", "Magu",
    };

    /// <summary>One crew name off `AI_NAMES`, drawn like the loadout.</summary>
    public static string ai_name(Rng rng) =>
        AI_NAMES[(int)(rng.random() * AI_NAMES.Length) % AI_NAMES.Length];

    // seconds between two launches of a lock-on round by an AI crew.  A rack of
    // eight fire-and-forget rounds at the rack's own 0.3 s cycle is two and a half
    // seconds of war and an empty arm for the rest of the battle, so the crew
    // launches one, watches it arrive and picks the next moment
    // (`Systems/Weapons.cs`).  A beam rider is not on this clock: its price is
    // holding the sight on the target, so it fires at the rack's own rate.
    public const float AI_LAUNCH_GAP = 15.0f;

    // Which arm a crew lays its *sight* on, when the two arms want different leads.
    // One sight line can only express one intercept point, and the arms differ by
    // seconds of flight time, so this is a product decision - and the one setting
    // `Systems/AI/Support/Engage.cs` reads.  Every arm still gets its own solution;
    // the ones that do not own the sight fire only while their own solution sits
    // inside their own trigger gate.
    //
    // The two muzzles of a mech are 2.86 m apart and the hull can point one way, so
    // the arm that owns the sight is the arm that fires: `Punch` is what makes the
    // armament matter (a rocket nest, a beam rider, a top attack and a 105 mm all
    // outscore the 30 mm, and the gun takes its turn while the other arm reloads or
    // waits out `AI_LAUNCH_GAP`).  `Fastest` reads "the gun, always", which is the
    // left arm only - the whole right-hand arsenal then stands down for the battle.
    public enum MasterArm
    {
        Slowest = 0,    // the round that takes longest: the old behaviour
        Fastest = 1,    // the round that arrives first - what a gun is for
        Punch = 2,      // the round that hurts most per hit
    }
    public const MasterArm AI_MASTER_ARM = MasterArm.Punch;

    // ------------------------------------------------------------------- shop
    // A weapon is bought as "hardware + a full load of ammunition": the hardware is
    // `cost`, the rounds are `ammo_cost` each, and both are listed so the price of a
    // swap can be worked out line by line.  Selling back pays `SELL_REFUND` of the
    // hardware value, scaled by how much of that arm is still there (a wrecked arm is
    // worth nothing), plus half of the rounds left in it.
    public const float SELL_REFUND = 0.5f;      // fraction of the hardware value a trade-in pays

    // fire control: guns and unguided rockets hold off for the drop so the round
    // meets the sight line at the zeroed range.  The range is a fixed setting the
    // pilot steps with CTRL+wheel - solving it against whatever the crosshair happens
    // to sit on made the reticle jump every time a hull crossed a ridge, which is
    // exactly the moment a shot has to be taken.  The elevation is capped so a wild
    // setting cannot point the barrel at the sky.
    public const float ZERO_RANGE = 4000.0f;    // metres, the muzzle ray's length (the sight's reach)
    public const float ZERO_MAX_ELEV = 1.05f;   // rad, ~60 deg
    public static readonly float[] ZERO_STEPS =
        { 500.0f, 1000.0f, 1500.0f, 2000.0f, 2500.0f, 3000.0f, 3500.0f, 4000.0f };
    public const float ZERO_DEFAULT = 1000.0f;  // metres the sight is set to at spawn
}

// `DEATH_BLAST` in Python is a dict; this is the same three keys as fields.
public sealed class DeathBlastDef
{
    public float ed;            // Explosive Damage at the centre of the blast
    public float er;            // radius it falls off to nothing over, m
    public float ad;            // armour-piercing damage (0: a blast does not punch)
}

// `UNIT_DEFS[etype]["death"]`: the recipe `Systems/Mortality.cs` follows when the
// hull goes:
//   blast=(ed, er)      it bursts on the spot, no sides spared (Systems/Blasts.cs)
//   loud="blast"/"big"  which cue the client plays and how hard the screen kicks
//   wreck=(etype, life, kind)  what is left behind, and the wire `kind` byte
//   ends_match=true     killing this one decides the round
// Anything missing means "none of that", which is why every field but
// `ends_match` is nullable and left out of the JSON when it is null.
public sealed class DeathDef
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? loud;
    // (ed, er) - the burst it leaves on the spot
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public float[]? blast;
    // (etype, seconds, wire kind) - the hulk or corpse it leaves behind
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public float[]? wreck;
    public bool ends_match;
}

// One entry of `UNIT_DEFS`: the *data* of an entity type.  What an entity *is*
// comes from the marker components its `tags` name, never from a type switch (see
// `Ecs/Components.cs`).
//
//   unit          a hull on the battlefield (rounds stop on it, it has an hp bar)
//   combat        fights and may be fought - the only thing AI or a player may
//                 pick as a target, and the only thing that holds a town
//   lockable      an IR seeker can track it
//   moves         has locomotion, `movement.run` integrates it
//   person        a proximity fuse may go off on it (systems/proximity.py) - a man
//                 is a man whether he carries a rifle, an RPG or a shopping bag
//   invulnerable  rounds stop on it, damage does nothing
//   vehicle       wheeled: it steers instead of strafing (Systems/Movement.cs)
//
// `dp` / `de` are the two defences every hit is measured against: `dp` soaks
// penetration (see `pen_damage`), `de` soaks blast (`blast_damage`).  They are
// copied into the `armor` component at spawn, so the damage path never reads a
// definition object.
//
// `hp` is *the* health of the entity - one number, and `hp <= 0` is what kills it.
// A hull built out of separately damaged modules declares `arms` as well and gets a
// second and a third pool for them: the factory adds `components.ARM_COMPS`, so the
// archetype itself carries `arm_hp` and every system tests *that* rather than the
// unit's type.  The mech's torso has no pool at all: it is hit geometry that
// charges the hull (`MOD_TORSO`).
//
// `spheres` is the hit geometry: (module, right, up, forward, radius) in the hull's
// own frame, solved against a line, not sampled - one table per type, cached as a
// numpy array by `mathutil.sphere_geom` (rows of floats, module column included, so
// the table is one array-of-arrays on the wire too).
//
// `radar_strength` (m^4) is the platform's self-defence radar; 0 = none, so a
// rifleman, a base or a blockhouse never sees a missile coming and never shoots at
// one (see the radar block above).
public sealed class UnitDef
{
    public string name = "";
    public float hp;
    // the left / right arm pools, when the hull is built out of modules; null for
    // everything that is not a mech
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int[]? arms;
    public float dp, de;
    public float speed;         // m/s the hull's own governor allows
    /// <summary>Rise over run this chassis will climb; `Defs.max_gradient` reads it.
    /// The default is a man's - the two hulls that drive say their own.</summary>
    public float max_grad = Defs.FOOT_MAX_GRADIENT;
    public float radius;        // m, the collision circle
    public float height;        // m, ground to the top of the hull
    public float radar_strength;    // m^4, 0 = no self-defence radar
    public int[] weapons = { Defs.WID_NONE, Defs.WID_NONE };  // (left arm, right arm)
    public int ai_range;        // m an AI crew engages at (an arm still needs its own `range`)
    public int sight;           // m it can pick a target out to
    public int reward;          // $ for killing it
    public float splat;         // m, the fireball the client draws when it dies
    // the only types an AI crew of this one will pick as a target; null = anything
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int[]? targets;
    public float[][] spheres = System.Array.Empty<float[]>();
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DeathDef? death;
    public string[] tags = System.Array.Empty<string>();
}

public static partial class Defs
{
    // -------------------------------------------------------------- unit types
    // (module, x right offset, y height, z forward offset, radius)
    public static readonly float[][] MECH_HITSPHERES =
    {
        new[] { (float)MOD_CHASSIS, 0.0f, 1.45f, 0.0f, 1.45f },
        new[] { (float)MOD_TORSO, 0.0f, 4.15f, 0.0f, 1.30f },
        new[] { (float)MOD_LEFT, -1.43f, 3.55f, 0.10f, 0.85f },
        new[] { (float)MOD_RIGHT, 1.43f, 3.55f, 0.10f, 0.85f },
    };

    public static readonly Dictionary<int, UnitDef> UNIT_DEFS = new()
    {
        // 10 hp is a deliberate knife edge: one 12.7 mm round (AD 10) kills outright,
        // while a rifle (AD 4) needs exactly three - two leave him standing.  Infantry
        // is therefore no threat to armour at all, and is gone the moment anything
        // with a heavy machine gun looks at it.
        [ETYPE_INF_RIFLE] = new UnitDef
        {
            name = "RIFLEMAN", hp = 10f, dp = 1f, de = 1f,
            speed = 5.5f, radius = 0.55f, height = 1.8f,
            weapons = new[] { WID_RIFLE556, WID_NONE }, ai_range = 800, sight = 800,
            reward = 45, splat = 1.0f, targets = new[] { ETYPE_INF_RIFLE, ETYPE_INF_ROCKET },
            spheres = new[] { new[] { (float)MOD_CHASSIS, 0.0f, 0.9f, 0.0f, 0.65f } },
            death = new DeathDef
            {
                loud = "boom", wreck = new[] { (float)ETYPE_CORPSE, CORPSE_TIME, 0f },
            },
            tags = new[] { "unit", "combat", "moves", "person" },
        },
        [ETYPE_INF_ROCKET] = new UnitDef
        {
            name = "AT RIFLEMAN", hp = 10f, dp = 1f, de = 1f,
            speed = 5.0f, radius = 0.55f, height = 1.8f,
            weapons = new[] { WID_RPG, WID_NONE }, ai_range = 800, sight = 800,
            reward = 70, splat = 1.0f,
            spheres = new[] { new[] { (float)MOD_CHASSIS, 0.0f, 0.9f, 0.0f, 0.65f } },
            death = new DeathDef
            {
                loud = "boom", wreck = new[] { (float)ETYPE_CORPSE, CORPSE_TIME, 0f },
            },
            tags = new[] { "unit", "combat", "moves", "person" },
        },
        // one hull pool: the turret is hit geometry, not a second health bar.  `vehicle`
        // is what makes it steer instead of strafe (Systems/Movement.cs) and follow the
        // lane as a path instead of aiming at its goal (Systems/AI/).
        // 100 km/h on the governor: the fastest thing on the lanes by a wide margin, but
        // it takes its time getting there (see APC_THRUST)
        [ETYPE_APC] = new UnitDef
        {
            name = "APC", hp = 100f, dp = 10f, de = 20f,
            speed = 100.0f / 3.6f, radius = 2.2f, height = 2.8f,
            max_grad = APC_MAX_GRADIENT,
            // self-defence radar: half the mech's discovery range, i.e. a sixteenth of
            // its `radar_strength` (d^4), so the warning time is halved as well
            radar_strength = 3.125e12f,
            weapons = new[] { WID_MG127, WID_APC_ATGM }, ai_range = 3000, sight = 3000,
            reward = 180, splat = DEATH_BLAST.er,
            spheres = new[]
            {
                new[] { (float)MOD_CHASSIS, 0.0f, 1.1f, 0.0f, 1.7f },
                new[] { (float)MOD_CHASSIS, 0.0f, 2.4f, 0.0f, 1.2f },
            },
            death = new DeathDef
            {
                blast = new[] { DEATH_BLAST.ed, DEATH_BLAST.er }, loud = "blast",
                wreck = new[] { (float)ETYPE_DEBRIS, WRECK_TIME, 1f },
            },
            tags = new[] { "unit", "combat", "lockable", "moves", "vehicle" },
        },
        // hull pool 120 (chassis + torso), one arm 50 each; armour of a light tank.  The
        // hull was 100 until the autocannon went to 300 rpm: 120 is what keeps a
        // sustained-fire machine from being the answer to everything, and it costs the
        // cannon two extra rounds (12 hits, 2.2 s at full hits, 3.2 s at 70 %) while
        // every one-shot killer - SACLOS 180, top attack 160, 105 mm 203 - is still past
        // the whole pool in one round.  The arms are *not* scaled with it: an arm is 50,
        // so it is 42 % of the hull now rather than half
        [ETYPE_MECH] = new UnitDef
        {
            name = "MECH", hp = 120f, arms = new[] { 50, 50 }, dp = 10f, de = 20f,
            speed = 50.0f, radius = 1.9f, height = 6.5f,
            max_grad = MECH_MAX_GRADIENT,
            // A doppler receiver answers `d^2 <= K * v_closing`, so where a round is
            // picked up is set by how fast it is *closing*: 544 / 975 / 1368 / 977 m
            // against the RPG / the S-8 / the SACLOS / the top attack, each at the speed
            // it actually arrives at (`Defs.radar_range`).  The top attack is the one this
            // is tuned to: on a 2.2 km launch the round is 778 m out and closing at
            // 195 m/s exactly 4 s before the warhead goes off, so K >= 778^2 / 195 =
            // 3100, i.e. strength >= 1.73e14 (1.75e14 is the round figure).  Four seconds
            // is the window a pilot needs: at 780 m the fuse's own trigger is a 0.26 deg
            // cone, so the burst has to be laid, not sprayed
            radar_strength = 1.75e14f,
            weapons = new[] { WID_AC30, WID_ROCKET }, ai_range = 3500, sight = 3500,
            reward = 350, splat = DEATH_BLAST.er, spheres = MECH_HITSPHERES,
            death = new DeathDef
            {
                blast = new[] { DEATH_BLAST.ed, DEATH_BLAST.er }, loud = "blast",
                wreck = new[] { (float)ETYPE_DEBRIS, WRECK_TIME, 2f },
            },
            tags = new[] { "unit", "combat", "lockable", "moves" },
        },
        // the objective: a target, but it never moves and cannot be locked.  4000 hp
        // behind 80 of plate: neither 80 mm rockets (AP 40) nor 30 mm rounds (AP 15) get
        // through, so both only do their blast - which prices the base at exactly 267
        // rockets (15 each) or 1112 cannon rounds (3.6 each)
        [ETYPE_BASE] = new UnitDef
        {
            name = "COMMAND BASE", hp = 4000f, dp = 80f, de = 20f,
            speed = 0.0f, radius = 7.0f, height = 12.0f,
            weapons = new[] { WID_NONE, WID_NONE }, ai_range = 0, sight = 0, reward = 2000,
            splat = 8.0f,
            spheres = new[] { new[] { (float)MOD_CHASSIS, 0.0f, 5.0f, 0.0f, 8.5f } },
            death = new DeathDef { loud = "big", ends_match = true },
            tags = new[] { "unit", "combat" },
        },
        [ETYPE_CIV] = new UnitDef
        {
            name = "CIVILIAN", hp = 10f, dp = 1f, de = 1f,
            speed = 4.0f, radius = 0.5f, height = 1.7f,
            weapons = new[] { WID_NONE, WID_NONE }, ai_range = 0, sight = 60, reward = 0,
            splat = 1.0f,
            spheres = new[] { new[] { (float)MOD_CHASSIS, 0.0f, 0.9f, 0.0f, 0.65f } },
            death = new DeathDef
            {
                loud = "boom", wreck = new[] { (float)ETYPE_CORPSE, CORPSE_TIME, 0f },
            },
            tags = new[] { "unit", "moves", "person" },
        },
        // inert: drawn by the client, but not a unit any more
        [ETYPE_DEBRIS] = new UnitDef
        {
            name = "WRECK", hp = 0f, dp = 0f, de = 0f,
            speed = 0.0f, radius = 2.0f, height = 2.6f,
            weapons = new[] { WID_NONE, WID_NONE }, ai_range = 0, sight = 0, reward = 0,
            splat = 2.0f,
            spheres = new[] { new[] { (float)MOD_CHASSIS, 0.0f, 1.0f, 0.0f, 1.5f } },
            death = null, tags = System.Array.Empty<string>(),
        },
        [ETYPE_CORPSE] = new UnitDef
        {
            name = "CORPSE", hp = 0f, dp = 0f, de = 0f,
            speed = 0.0f, radius = 0.5f, height = 0.4f,
            weapons = new[] { WID_NONE, WID_NONE }, ai_range = 0, sight = 0, reward = 0,
            splat = 1.0f,
            spheres = new[] { new[] { (float)MOD_CHASSIS, 0.0f, 1.0f, 0.0f, 1.5f } },
            death = null, tags = System.Array.Empty<string>(),
        },
        // concrete blockhouse: rounds stop on it, damage does not
        [ETYPE_BUNKER] = new UnitDef
        {
            name = "BUNKER", hp = 999999f, dp = 1000f, de = 1000f,
            speed = 0.0f, radius = 6.0f, height = 9.0f,
            weapons = new[] { WID_NONE, WID_NONE }, ai_range = 0, sight = 0, reward = 0,
            splat = 6.0f,
            // stepped blockhouse: a squat mass and the deck on top
            spheres = new[]
            {
                new[] { (float)MOD_CHASSIS, 0.0f, 4.0f, 0.0f, 5.0f },
                new[] { (float)MOD_CHASSIS, 0.0f, 8.8f, 0.0f, 3.6f },
            },
            death = null, tags = new[] { "unit", "invulnerable" },
        },
    };

    // repair: welding a mech from nothing back to full armour costs REPAIR_FULL_COST,
    // split evenly over the machine's own points (hull 120 + arms 50 + 50).  Kept as a
    // total so the bill stays sane if the hp table moves.
    public const float REPAIR_FULL_COST = 400.0f;
    public static readonly float REPAIR_COST_PER_HP = REPAIR_FULL_COST / MathF.Max(
        1.0f, UNIT_DEFS[ETYPE_MECH].hp + UNIT_DEFS[ETYPE_MECH].arms!.Sum());

    // ---- rearm work (seconds at the wrench) ---------------------------------
    // Buying is not free in *time* either (Systems/Rearm.cs): the hull stands where it
    // is with the wrench out and its triggers dead, so a repair inside an owned town no
    // longer doubles as a mid-fight reload.  Fitting a weapon and loading its racks are
    // two jobs, hence a swap costs both.
    public const float REARM_SWAP_S = 2.0f;             // fitting one weapon into an arm
    public const float REARM_AMMO_S = 4.0f;             // loading a full load of rounds into one arm
    public const float REARM_REPAIR_PER_100HP_S = 2.0f; // welding armour back on, per 100 hp

    // mech geometry (metres, relative to the ground point under the entity).
    // `MECH_HITSPHERES` itself is declared above `UNIT_DEFS`, because the mech's
    // `spheres` table is built from it.
    public const float MECH_EYE = 5.05f;
    // The commander's panoramic sight is a free turret standing on the roof, and it is
    // *not* on the hull's own rotation axis: it is bolted `MECH_SIGHT_Z` behind it,
    // which is where the model draws it and where the pilot's eye sits.  That offset is
    // the whole point of the decoupling - the sight answers the view instantly, the hull
    // and the arms slew to catch up (Systems/Gunnery.cs), and because the two axes
    // differ, a hull catching up underneath a sight that already points elsewhere walks
    // the sight - and the eye - along an arc.
    public const float MECH_SIGHT_Z = -0.62f;           // m, the sight head's pivot behind the hull centre
    public const float MECH_HULL_RATE = 80.0f * MathF.PI / 180.0f;  // rad/s the hull swings after the sight
    public const float MECH_ARM_RATE = 50.0f * MathF.PI / 180.0f;   // rad/s the arms elevate after the sight
    // the mechanical elevation limits of the arm mounts: the sight may look at the sky,
    // the barrels cannot follow it there
    public const float MECH_PITCH_MIN = -0.45f;
    public const float MECH_PITCH_MAX = 0.75f;
    // ---- the mech's power curve ---------------------------------------------
    // The legs are an *engine*, and an engine is rated in horsepower
    // (`Systems/Movement/Mech/`).  Power, not force: the push it makes is
    // `P(v) / v`, so it is strongest against a hull that has hardly started and
    // settles to a steady shove at speed.  That is the whole difference from a
    // constant thrust, and it is what a pilot feels - a hard launch off the line,
    // and the last 20 km/h of the top end taking as long as the first hundred.
    //
    //   P(v) = MECH_HP_LOW + (MECH_HP_HIGH - MECH_HP_LOW) * v / MECH_RATED_V
    //   F(v) = min( P(v) / v , MECH_G_LIMIT * GRAVITY * MECH_MASS )
    //   a(v) = F(v) / MECH_MASS            (m/s^2, times cos(slope) horizontally)
    //
    // The cap is the machine's own **g limit** - its drive, and gravity is counted
    // apart from it: 1.5 g of push is 221 kN, and the curve only drops under that
    // at about 76 km/h, so everything below that is a flat 1.5 g and everything
    // above it is the engine.  Above `MECH_RATED_V` the power is capped at
    // `MECH_HP_HIGH`.
    //
    // A power curve has no speed of its own at which it stops, so the drag is what
    // gives the machine a top end, and `MECH_DRAG` is *solved* from it: the flat
    // balance at the rated 180 km/h is `P(v0)/v0 = MECH_MASS * k * v0^2`.  What a
    // hill then does is a consequence, not a constraint: 30 deg down 210 km/h,
    // 35 deg up 114 km/h, both reported by `--check drive`
    // (`docs/plans/mech_thrust.md`).
    //
    // `MECH_MASS` is the one place a mass appears anywhere in the simulation: every
    // other system works in accelerations.  It is here because horsepower is.
    public const float GRAVITY = 9.81f;                 // m/s^2, straight down
    public const float HORSEPOWER = 745.7f;             // W, mechanical hp (the two figures below are in these)
    public const float MECH_HP_LOW = 5000f;             // hp at a standstill
    public const float MECH_HP_HIGH = 8000f;            // hp at the rated speed, and every speed above it
    public const float MECH_MASS = 15000f;              // kg, the machine's design mass (15 t)
    public const float MECH_RATED_V = 50f;              // m/s, where the curve tops out and the drag balances: 180 km/h
    // the most push the drive may make, in g.  Gravity is *not* in this budget: a
    // hull on a slope feels the hill on top of whatever the legs are doing
    public const float MECH_G_LIMIT = 1.5f;
    // solved, not tuned: the flat balance at MECH_RATED_V with the full 8000 hp
    public const float MECH_DRAG =
        MECH_HP_HIGH * HORSEPOWER / (MECH_RATED_V * MECH_MASS * MECH_RATED_V * MECH_RATED_V);
    // The push is the whole of the steering: a hull pushes where it was told to and
    // its velocity bends after the push at `a/v`, so a right angle at 180 km/h is a
    // long, wide crossing of its own track (measured by `--check drive`).  Which is
    // why a pilot's WASD is read in his own hull's frame (`input/sight.js`): the
    // machine goes where the machine points.
    public const float MECH_BRAKE = 12.0f;              // m/s^2 the legs plant with; above the steepest hill's pull
    public const float MECH_GOVERNOR_BAND = 3.0f;        // m/s of speed error that puts the legs on full push
    // a hull whose legs point across the way it was told to go pushes with the
    // smaller part of its thrust, and it turns them round at `Integrate.turn_legs`
    public const float MECH_LEG_ALIGN = 0.45f;          // thrust left at 90 deg of leg misalignment
    public const float MECH_BROKEN = 0.2f;              // what a hull with its chassis out pushes with

    // ---- slopes -------------------------------------------------------------
    // Rise over run: 1.0 is 45 degrees.  The ground is a continuous field, so a
    // hillside is a number and not a step, and these are the numbers that decide
    // where a hull can go: the generator never builds a face steeper than
    // MAX_TERRAIN_GRADIENT, and a chassis refuses to enter a face steeper than
    // its own - which is what makes a crest a wall to a wheeled hull and a detour
    // to everybody else.  Movement (`Drive`, `Integrate`) and pathing
    // (`Systems/AI/Nav.cs`) read the same figure through `max_gradient`.
    public const float MAX_TERRAIN_GRADIENT = 0.85f;   // 40 deg, the generator's ceiling
    public const float APC_MAX_GRADIENT = 0.58f;       // 30 deg, wheels
    public const float MECH_MAX_GRADIENT = 0.70f;      // 35 deg, legs up a slope
    public const float FOOT_MAX_GRADIENT = 0.80f;      // 39 deg, a man on foot
    /// <summary>The slope a chassis will not climb, by its own def.</summary>
    public static float max_gradient(int etype) => UNIT_DEFS[etype].max_grad;

    // ---- wheeled hulls ------------------------------------------------------
    // An APC is tagged `vehicle`, and a vehicle steers instead of strafing.  It is a
    // rear-axle bicycle model: the hull rolls only along its own axis and turns at
    // (speed / APC_TURN_R) per second, so it can neither pivot on the spot nor slide
    // sideways - the two things a hovercraft-like `vel` vector lets a hull do.  The
    // turning radius is why the AI follows the lane as a *path*: pointing a car at its
    // goal drives it into the first house on the way.
    public const float APC_TURN_R = 11.0f;      // m, the tightest curve the hull can hold
    // Thrust against drag, not a fixed ramp: the engine pushes with APC_THRUST m/s^2 and
    // the hull sheds APC_THRUST / APC_V_INF^2 per (m/s)^2 of speed, so the pull is
    // strongest off the line and fades out towards APC_V_INF.  The two numbers are set
    // from the two things that matter: the governor (the `speed` in UNIT_DEFS,
    // 100 km/h) and the 0-100 km/h time of 20 s, with the drag balance 20 % above the
    // governor so the last km/h is actually reachable.
    //   t(0 -> v) = (V / a) * artanh(v / V)   with V = APC_V_INF
    public const float APC_V_INF = 33.33f;      // m/s, where thrust and drag would cancel (1.2 x top)
    public const float APC_THRUST = 2.0f;       // m/s^2 at a standstill - 0-100 km/h in 20.0 s
    public const float APC_BRAKE = 8.0f;        // m/s^2, it sheds speed faster than it picks it up
    public const float APC_REVERSE = 0.4f;      // fraction of top speed the crew backs up at
    public const float APC_STEER_LOCK = 0.30f;  // rad of heading error that puts the wheels on full lock
    public const float APC_BACKOUT = 1.2f;      // s of reverse when the nose is against something
    public const float APC_BLOCKED = 0.5f;      // s of pushing against a wall before the crew gives up
    // The acceptance radius of the way point tracker: a way point this close to the hull
    // counts as reached and the follower aims at the next one.  It is what lets a car
    // take a dense line of sharp corners at speed - it cuts the inside of every one of
    // them, off the road if it likes, instead of trying to turn on the spot at a vertex.
    // It has to *grow with the speed*, or a fast hull never gets close enough to the line
    // and a slow one throws away the corners it could have taken:
    // `radius = clamp(speed * time, floor, cap)` is the pure-pursuit lookahead rule.
    public const float APC_ARRIVE_LOOK = 1.1f;  // s of travel: 10 m at 9 m/s, 30 m at 27 m/s
    public const float APC_ARRIVE_MIN = 10.0f;  // m, slow: follow the line closely
    public const float APC_ARRIVE_MAX = 30.0f;  // m, quick: 30 m of arc is all the wheels can do anyway
    // How far inside a corner the hull is allowed to cut, in metres.  It is the one knob
    // that trades corner speed against holding the line: a fillet of radius R misses the
    // vertex by R * (1 / cos(angle / 2) - 1), so 8 m of budget puts a right angle at
    // ~11 m/s and a 20 degree kink at no limit at all.
    public const float APC_CUT = 8.0f;          // m
    public const float APC_CORNER_HORIZON = 140.0f;  // m of lane the corner brake looks down
    public const float APC_LAT_ACCEL = 6.0f;    // m/s^2 of side load a bend is taken at - the speed
                                                // a curve is driven at is sqrt(lat / its curvature)
    // The shoulder joint of each arm: lateral offset, height, and the forward offset of
    // the arm's front face (the round leaves from the weapon's own mouth, MECH_ARM_REACH
    // metres further out along the barrel).
    public static readonly Dictionary<int, float[]> MECH_ARM_MUZZLE = new()
    {
        [MOD_LEFT] = new[] { -1.43f, 3.55f, 0.9f },
        [MOD_RIGHT] = new[] { 1.43f, 3.55f, 0.9f },
    };
    // How far the bore ends past the shoulder, per weapon kind - the same numbers the
    // client builds the models from (`scene.js armModel`), so a round really leaves the
    // mouth of the barrel it was drawn on.  A 4.1 m cannon and a 1.8 m nest differ by
    // more than two metres; using one figure for both put the rockets inside their own
    // tubes and the cannon shells well short of the brake.  A launcher made of tubes does
    // not need an entry: its `rack` carries the mouth (`rack.mouth`) and the round leaves
    // from there.
    public static readonly Dictionary<string, float> MECH_ARM_REACH = new()
    {
        ["bullet"] = 4.30f,     // 30 mm: just inside the muzzle brake
        ["saclos"] = 1.95f,     // guided box: the launch tube
        ["none"] = 1.80f,
    };

    // ------------------------------------------------------------------ smoke
    // hot smoke countermeasure: a short fan of canisters thrown out of the hull, each
    // popping 50 m away into one big opaque puff.  A dozen of them are enough to close
    // the whole front hemisphere, and because they are drawn opaque the seams do not
    // show and no blending pass is needed.
    public const float SMOKE_RANGE = 50.0f;         // metres of flight before a canister pops
    public const float SMOKE_SPEED = 46.0f;         // m/s out of the tube (~1.1 s of flight)
    public const float SMOKE_LIFE = 5.0f;           // seconds the smoke stays up
    public static readonly float[] SMOKE_FLAT = { 45.0f, 45.0f };  // deployed puff: semiaxes h/v, metres (a sphere)
    public const float SMOKE_START = 0.35f;         // fraction of SMOKE_FLAT the burst starts at
    public const float SMOKE_BURST = 2.2f;          // 1/s; how fast the burst eases out
    public const float SMOKE_TIME_JITTER = 0.20f;   // +-fraction of the flight time, per canister
    public const float SMOKE_SIZE_JITTER = 0.20f;   // +-fraction of the puff size, per canister
    public const int SMOKE_CHARGES = 10;            // pulls a full rack holds (a life's worth)
    public const float SMOKE_COOLDOWN = 0.5f;       // seconds between pulls
    public const int SMOKE_LOAD_COST = 10;          // $ per canister loaded at the base
    public const float SMOKE_LOAD_S = 2.0f;         // seconds at the wrench for a reload

    /// <summary>Canister lattice covering the whole front hemisphere.</summary>
    /// <remarks>
    /// Three rows of elevation plus one puff over the roof.  Every puff is a sphere of
    /// SMOKE_FLAT radius seen from 50 m, which is ~50 deg wide on its own, so a handful
    /// of them overlap into a solid wall.  The bottom row is fired flat on purpose: a
    /// canister's fuse times out where the hull's own height has taken the hop, so the
    /// row of it that has to close the gap under the screen lands on the deck instead of
    /// hanging in the air.
    /// </remarks>
    public static (float az, float el)[] _smoke_fan()
    {
        var fan = new List<(float az, float el)>();
        foreach (float el in new[] { 3.0f, 40.0f, 68.0f })
        {
            int n = Math.Max(1, (int)MathF.Round(6.0f * MathF.Cos(el * MathF.PI / 180.0f)));
            for (int i = 0; i < n; i++)
                fan.Add((-90.0f + (i + 0.5f) * 180.0f / n, el));
        }
        fan.Add((0.0f, 90.0f));
        return fan.ToArray();
    }

    public static readonly (float az, float el)[] SMOKE_FAN = _smoke_fan();
    public static readonly int SMOKE_COUNT = SMOKE_FAN.Length;

    public const float RESPAWN_TIME = 8.0f;     // a mode's own rule (Mode.respawn_delay) may override it
    // Battle: the wait is the walk back to the front, so it is short on the enemy's
    // doorstep and long at home - interpolated on where the hull died.
    public const float RESPAWN_AT_FRONT = 3.0f;  // killed at the enemy command base
    public const float RESPAWN_AT_HOME = 40.0f;  // killed at your own command base
    public const float BUILD_TIME = 5.0f;

    // optics / IR seeker
    public const float BASE_FOV = 1.05f;        // vertical field of view at 1x, radians
    public const float LOCK_FRAC = 0.05f;       // lock circle diameter as a fraction of the view width
    // Where a seeker looks at a hull - the deck it dives on, as a fraction of the type's
    // `height`.  It has to stay *inside* the hull's own hit spheres or a round that flies
    // the aim point perfectly still misses the body: 0.92 of a mech's 6.5 m is 5.98 m
    // while the top of its torso sphere is 5.45 m, so every well-guided round passed half
    // a metre over the machine and burst in the dirt behind it (measured on the range:
    // 5 dives, 2 of them that way, and the ones that killed did it by arriving a hair
    // low).  0.78 puts the aim point 0.38 m inside that sphere - still the deck, and now
    // a dive that flies it is a hit.
    public const float AIM_ROOF = 0.78f;
    // A missile's own seeker does not need its target to survive the flight.  With the
    // lock gone it stares at where the hull should be, and takes the first enemy hull it
    // can see inside this cone around that stare line (`Seeker.cs`).  3 deg is about the
    // angular width of the round's own dispersion.
    public const float SEEKER_CONE = 3.0f * MathF.PI / 180.0f;

    // ---------------------------------------------------------------- acoustics
    // Whether a bang is worth putting on the wire is decided here, not by the client:
    // a 30 mm round going off 7 km away is a packet nobody hears.  What the ear gets
    // is `sound_gain`, and `frontend/src/audio.js` mirrors these numbers line for
    // line (`attenuation`, `SOUND_LEVEL`) - the client plays at exactly the level the
    // server tested.
    //
    // Spreading is inverse square *compressed*: over a 16 km map a true 1/r is 84 dB
    // of dynamic range, which is silence.  ROLLOFF 0.6 keeps the shape of the near
    // field (a gun at 100 m really is quieter than one at 20 m) and still leaves a
    // hull 8 km out audible.  A muzzle is not a point source, so nothing falls off
    // inside REF_DIST.
    public const float SOUND_REF_DIST = 25.0f;  // m, where a source is at its full level
    public const float SOUND_ROLLOFF = 0.60f;   // 1.0 = inverse square, 0 = no falloff at all
    public const float SOUND_FLOOR = 0.02f;     // arriving gain under this is not sent at all
    public const float SOUND_SPEED = 343.0f;    // m/s in air at 20 C (mirrors audio.js)
    // How long an event may stay in the air.  The loudest cue a pilot can hear is a
    // hull going up, good to ~17 km - which takes 50 s to arrive, so a shorter TTL
    // would be the real cutoff and the level test above would be dead letters.
    public const float SOUND_TTL = 50.0f;       // s (mirrors game.js)
    // how loud each cue is at SOUND_REF_DIST (mirrors audio.js SOUND_LEVEL)
    public static readonly Dictionary<string, float> SOUND_LEVEL = new()
    {
        ["shot"] = 0.60f, ["boom"] = 0.70f, ["big"] = 0.80f, ["blast"] = 1.00f,
    };

    /// <summary>Gain of a `level` loud source `dist` metres from the ear.</summary>
    public static float sound_gain(float dist, float level = 1.0f) =>
        level * MathF.Pow(SOUND_REF_DIST / MathF.Max(SOUND_REF_DIST, dist), SOUND_ROLLOFF);
}
