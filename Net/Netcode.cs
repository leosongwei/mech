// Wire protocol: compressed world init, binary snapshots, JSON meta.
//
// Ported from `backend/server/netcode.py`.  This file is the compatibility
// contract with `frontend/src/net.js`: the client reads fixed byte offsets out
// of the snapshot and fixed table names out of the init payload, so everything
// here is little-endian, row-major and named exactly like the Python side.
//
//   init       one JSON text frame, `static_payload` + who you are
//   snapshot   one binary frame: 16 B header, 80 B unit rows, 32 B projectile
//              rows, 32 B smoke rows, and - every `CONTACT_EVERY` ticks - the
//              minimap's 6 B contact rows.  The header is magic, tick and four
//              u16 counts (units, projectiles, smoke, contacts); the rows start
//              there.  It was 14 B (three counts) until the minimap got its own
//              block.
//   meta       one JSON text frame per pilot, 10 Hz
//
// The JSON tables are public *fields* on the C# side (`WeaponDef.wid`,
// `UnitDef.arms`, ...), which `System.Text.Json` skips unless `IncludeFields`
// is on - hence `Defs.WIRE_JSON` everywhere a payload is serialised.

using System.Buffers.Binary;
using System.IO.Compression;

namespace Mech.Server;

public static class Netcode
{
    public const uint MAGIC = 0x4D454348;      // "MECH"
    public const int HEADER = 16;              // magic, tick, four u16 counts
    public const int UNIT_STRIDE = 80;
    public const int PROJ_STRIDE = 32;
    public const int SMOKE_STRIDE = 32;
    public const int CONTACT_STRIDE = 6;       // minimap point: x, z, faction, etype
    public const int CONTACT_EVERY = 8;        // ticks: 3.75 Hz, and that is smooth
    public const float CONTACT_QUANT = 4f;     // metres per unit of the u16 (16 km -> 4000)

    // ------------------------------------------------------------------
    // init
    // ------------------------------------------------------------------
    /// <summary>`base64(zlib(bytes))` - exactly the Python `_b64`, because the
    /// client inflates it with its own JS zlib: a raw deflate stream has no
    /// header for it to sniff, so the zlib wrapper is mandatory.</summary>
    public static string _b64(byte[] raw) => Convert.ToBase64String(_zlib(raw));

    public static byte[] _zlib(byte[] raw)
    {
        // `CompressionLevel.SmallestSize` is a dynamic-Huffman deflate, which is
        // what zlib level 6 does for this kind of data (a heightfield and three
        // tile planes); the client does not care which level produced the frame
        using var ms = new MemoryStream(raw.Length / 3 + 64);
        using (var z = new ZLibStream(ms, CompressionLevel.SmallestSize, leaveOpen: true))
            z.Write(raw, 0, raw.Length);
        return ms.ToArray();
    }

    /// <summary>Everything the client needs once (map, definitions).</summary>
    public static Dictionary<string, object?> static_payload(World world)
    {
        var t = world.terrain;
        // `np.round(t.h).astype(np.int16)`: heights sit on 4 m steps, so the
        // int16 plane is the biggest thing in the init frame before zlib.
        // `MathF.Round` is round-half-to-even by default, which is what
        // `np.round` does - a vertex exactly on a .5 step lands the same way.
        var hb = new byte[t.h.Length * 2];
        for (int k = 0; k < t.h.Length; k++)
            BinaryPrimitives.WriteInt16LittleEndian(hb.AsSpan(k * 2), (short)MathF.Round(t.h[k]));

        // the towns with the Python dict's keys, not the C# `Town`'s (which
        // also carries the capture timers only the server ever reads)
        var towns = new List<object?>(t.towns.Count);
        foreach (var tn in t.towns)
            towns.Add(new Dictionary<string, object?>
            {
                ["x"] = tn.x, ["z"] = tn.z,
                ["tile_x"] = tn.tile_x, ["tile_y"] = tn.tile_y,
                ["owner"] = tn.owner, ["progress"] = tn.progress, ["name"] = tn.name,
            });

        // a base is a 2-array of metres on the wire, never an object
        var bases = new Dictionary<int, object?>();
        foreach (var (fac, b) in t.bases) bases[fac] = new[] { b.x, b.z };

        var lanes = new List<object?>(t.lanes.Count);
        foreach (var lane in t.lanes)
        {
            var pts = new List<object?>(lane.Count);
            foreach (var (x, z) in lane) pts.Add(new[] { x, z });
            lanes.Add(pts);
        }

        var colors = new Dictionary<int, object?>();
        foreach (var (fac, c) in Defs.FACTION_COLORS) colors[fac] = c;
        var names = new Dictionary<int, object?>();
        foreach (var (fac, n) in Defs.FACTION_NAMES) names[fac] = n;
        var units = new Dictionary<int, object?>();
        foreach (var (etype, def) in Defs.UNIT_DEFS) units[etype] = def;
        var muzzles = new Dictionary<int, object?>();
        foreach (var (arm, m) in Defs.MECH_ARM_MUZZLE) muzzles[arm] = m;
        var reach = new Dictionary<string, object?>();
        foreach (var (kind, r) in Defs.MECH_ARM_REACH) reach[kind] = r;

        // the largest cross-section in the game: the outer ring of the RWR dial
        float rcs = 0f;
        foreach (var w in Defs.WEAPONS) rcs = MathF.Max(rcs, w.rcs);

        return new Dictionary<string, object?>
        {
            ["map"] = new Dictionary<string, object?>
            {
                ["w"] = t.w, ["h"] = t.d, ["tile"] = Terrain.TILE,
                ["heights"] = _b64(hb),
                ["tiles"] = _b64(t.tiles),
                ["roads"] = _b64(t.roads),
                ["walls"] = _b64(t.walls),
            },
            ["weapons"] = Defs.WEAPONS,
            ["units"] = units,
            ["factions"] = new Dictionary<string, object?>
            {
                ["colors"] = colors, ["names"] = names,
            },
            ["bases"] = bases,
            ["lanes"] = lanes,
            ["towns"] = towns,
            ["mech"] = new Dictionary<string, object?>
            {
                ["hp"] = mech_hp_slots(),
                ["hitspheres"] = Defs.MECH_HITSPHERES,
                ["eye"] = Defs.MECH_EYE,
                ["sight_z"] = Defs.MECH_SIGHT_Z,
                ["muzzles"] = muzzles,
                ["reach"] = reach,
                ["modules"] = Defs.MODULE_NAMES,
            },
            ["buyable"] = Defs.BUYABLE,
            ["rearm"] = new Dictionary<string, object?>
            {
                ["smoke_cost"] = Defs.SMOKE_LOAD_COST,
                ["smoke_charges"] = Defs.SMOKE_CHARGES,
                ["smoke_load_s"] = Defs.SMOKE_LOAD_S,
                ["sell_refund"] = Defs.SELL_REFUND,
                ["repair_cost"] = Defs.REPAIR_COST_PER_HP,
                ["swap_s"] = Defs.REARM_SWAP_S,
                ["ammo_s"] = Defs.REARM_AMMO_S,
                ["repair_s"] = Defs.REARM_REPAIR_PER_100HP_S,
            },
            ["debug"] = Environment.GetEnvironmentVariable("MECH_DEBUG") == "1",
            ["mode"] = world.mode,
            // the radar equation's constants (`Defs.radar_k`): the client solves
            // the very same `d^2 <= K * v_closing` for the RWR
            ["radar"] = new Dictionary<string, object?>
            {
                ["v_ref"] = Defs.RADAR_V_REF, ["rcs"] = rcs,
            },
            ["lock"] = new Dictionary<string, object?>
            {
                ["frac"] = Defs.LOCK_FRAC, ["fov"] = Defs.BASE_FOV,
            },
            ["zero"] = new Dictionary<string, object?>
            {
                ["range"] = Defs.ZERO_RANGE, ["max_elev"] = Defs.ZERO_MAX_ELEV,
                ["steps"] = Defs.ZERO_STEPS, ["default"] = Defs.ZERO_DEFAULT,
            },
        };
    }

    /// <summary>The mech's four hp columns as the client indexes them (`MOD_*`).</summary>
    public static int[] mech_hp_slots()
    {
        var d = Defs.UNIT_DEFS[Defs.ETYPE_MECH];
        var slots = new int[4];
        slots[Defs.MOD_CHASSIS] = (int)d.hp;
        if (d.arms is { Length: > 0 })
            for (int a = 0; a < 2 && a < d.arms.Length; a++)
                slots[Defs.MOD_LEFT + a] = (int)d.arms[a];
        return slots;
    }

    // ------------------------------------------------------------------
    // snapshot
    // ------------------------------------------------------------------
    /// <summary>(n * 4) uint16 - the hull pool in column 0, the arm pools in 2
    /// and 3.  Column 1 (`MOD_TORSO`) is always zero: the torso is hit geometry,
    /// not a health bar.  The layout is the client's (`net.js` reads four u16
    /// per unit), so it is built here rather than stored per entity.</summary>
    public static ushort[] hp_columns(World world, int[] eids)
    {
        var e = world.ecs;
        var outv = new ushort[eids.Length * 4];
        for (int k = 0; k < eids.Length; k++)
        {
            var s = e.slot(eids[k]);
            if (!s.ok) continue;
            var a = s.a;
            int row = s.row;
            outv[k * 4 + Defs.MOD_CHASSIS] = _u16(a.get_f("hp", row));
            if (a.has("arm_hp"))
            {
                outv[k * 4 + Defs.MOD_LEFT] = _u16(a.get_f("arm_hp", row, 0));
                outv[k * 4 + Defs.MOD_RIGHT] = _u16(a.get_f("arm_hp", row, 1));
            }
        }
        return outv;
    }

    /// <summary>`np.clip(x, 0, 65535).astype(uint16)`: clamp, then truncate.</summary>
    private static ushort _u16(float v) =>
        v <= 0f ? (ushort)0 : v >= 65535f ? (ushort)65535 : (ushort)v;
    private static ushort _u16(int v) =>
        v <= 0 ? (ushort)0 : v >= 65535 ? (ushort)65535 : (ushort)v;

    // A component a wreck, a corpse or a round need not own: Python's
    // `columns()` fills the default in where the entity has no buffer, so these
    // do the same instead of throwing on the archetype that lacks it.
    private static float _f(Archetype a, int row, string name, int c = 0) =>
        a.has(name) ? a.get_f(name, row, c) : 0f;
    private static int _i(Archetype a, int row, string name) =>
        a.has(name) ? a.get_i(name, row) : 0;
    private static byte _b(Archetype a, int row, string name) =>
        a.has(name) ? a.get_u8(name, row) : (byte)0;

    private static void _w16(Span<byte> s, int o, int v) =>
        BinaryPrimitives.WriteUInt16LittleEndian(s[o..], (ushort)(v & 0xFFFF));
    private static void _w32(Span<byte> s, int o, uint v) =>
        BinaryPrimitives.WriteUInt32LittleEndian(s[o..], v);
    private static void _wf(Span<byte> s, int o, float v) =>
        BinaryPrimitives.WriteSingleLittleEndian(s[o..], v);

    // ------------------------------------------------------------------
    // interest: one frame per pilot, not one per tick
    // ------------------------------------------------------------------
    // A snapshot used to be the whole field for everybody, and with 300-400
    // hulls on it that was 468 kB/s per pilot (94 % of the download, measured
    // with `--check net`).  It is now a **tier sphere around the pilot's own
    // eye**: the rows he can still use, and not one more.  Every radius below
    // is a client constant read the other way round, so the table is not a
    // taste - it is the answer to "where does the HUD stop reading this row".
    //
    //   6 km  mechs, APCs, the range's bunkers   fire control reaches 4 km
    //                                            (`Defs.ZERO_RANGE`: the centre
    //                                            cross and the impact marker
    //                                            solve against these rows) and
    //                                            the laser warning 3.4 km
    //         smoke, canisters and puffs         `map.js sightBlocked` reads
    //                                            puffs out to the same 4 km
    //   4 km  wrecks, shells                     a hulk and a tracer are still
    //                                            in the picture at the range a
    //                                            hull is
    //   3 km  guided rounds (rocket/SACLOS/top)  the receiver's own gate is
    //                                            `RWR_RANGE` 2 km and it reads
    //                                            *these* rows, so 2 km is the
    //                                            floor; 3 km keeps the launch
    //                                            in sight
    //         infantry, civilians
    //   2 km  corpses, anything else
    //   always  the pilot's own hull, both command bases
    //
    // The bases are exempt because the HUD's base-integrity bars scan the
    // entity table for `type === 5` (`hud.js`): a base dropped at 2 km would
    // blink out of the top bar for most of a match.  Two rows are 2.4 kB/s.
    //
    // **Enter at R, leave at 1.15 R.**  `net.js` deletes every entity the
    // newest frame does not mention, so a hull parked on a circle would be
    // re-created (position snap, `mvx` smoothing reset) on every crossing -
    // the dead band is what turns that flicker into a single late departure.
    public const float TIER_HULL = 6000f;
    public const float TIER_WRECK = 4000f;
    public const float TIER_SHELL = 4000f;
    public const float TIER_ROUND = 3000f;
    public const float TIER_INF = 3000f;
    public const float TIER_SMOKE = 6000f;
    public const float TIER_DEFAULT = 2000f;
    public const float TIER_DEAD_BAND = 1.15f;

    /// <summary>One pilot's interest sphere: where his eye is, the hull that
    /// must never be dropped, and the ids the last frame carried (the dead
    /// band).  Owned by the connection, reused every frame.</summary>
    public sealed class SnapshotView
    {
        public float x, z;
        public int mech = -1;
        // the whole field, tiers off: the baseline a harness prices the
        // filtered frame against (nothing on the live server sets it)
        public bool all;
        public readonly HashSet<int> keep = new();

        public void at(World world, Player pl)
        {
            mech = world.ecs.is_alive(pl.mech) ? pl.mech : -1;
            if (mech >= 0)
            {
                var p = world.ecs.fv("pos").get3(mech);
                x = p.x;
                z = p.z;
                return;
            }
            // no hull: the client's camera sits in the base orbit and shows the
            // same field the base can see
            var (bx, bz) = world.terrain.bases[pl.faction];
            x = bx;
            z = bz;
        }
    }

    public static float tier_of_unit(int etype) => etype switch
    {
        Defs.ETYPE_MECH or Defs.ETYPE_APC or Defs.ETYPE_BUNKER => TIER_HULL,
        Defs.ETYPE_DEBRIS => TIER_WRECK,
        Defs.ETYPE_INF_RIFLE or Defs.ETYPE_INF_ROCKET or Defs.ETYPE_CIV => TIER_INF,
        Defs.ETYPE_BASE => float.PositiveInfinity,
        _ => TIER_DEFAULT,
    };

    public static float tier_of_round(int kind) => kind == 0 ? TIER_SHELL : TIER_ROUND;

    /// <summary>Is this row inside the pilot's sphere?  A row the last frame
    /// carried keeps its place out to `TIER_DEAD_BAND` further - enter at R,
    /// leave at 1.15 R.</summary>
    private static bool inside(Archetype a, int row, in SnapshotView v, float tier, int eid)
    {
        if (v.all || tier == float.PositiveInfinity) return true;
        if (eid == v.mech) return true;
        float dx = a.get_f("pos", row, 0) - v.x;
        float dz = a.get_f("pos", row, 2) - v.z;
        float r = v.keep.Contains(eid) ? tier * TIER_DEAD_BAND : tier;
        return dx * dx + dz * dz <= r * r;
    }

    /// <summary>The minimap's own channel: **every** unit on the field as one
    /// point - x and z in `CONTACT_QUANT`-metre units, faction, etype - 6 bytes
    /// a row.  The snapshot is a tier sphere around one eye, so the strategic
    /// picture cannot come from it; this block is the same for every pilot, so
    /// `Server.broadcast` builds it once and hands the same bytes to each
    /// frame.  A 210 px map of 16 km resolves ~76 m per pixel, so 3.75 Hz is
    /// smooth: the block would cost 36 kB/s at the snapshot's own 15 Hz.
    /// Quantising to 4 m keeps a row at 6 B and nobody can see a quarter pixel.</summary>
    public static byte[] contacts_bytes(World world)
    {
        var e = world.ecs;
        int[] uids = world.all_units();
        var buf = GC.AllocateUninitializedArray<byte>(uids.Length * CONTACT_STRIDE);
        var s = buf.AsSpan();
        int off = 0;
        foreach (int eid in uids)
        {
            var slot = e.slot(eid);
            if (!slot.ok || !slot.a.has("pos")) continue;
            var a = slot.a;
            int r = slot.row;
            _w16(s, off + 0, (int)(a.get_f("pos", r, 0) / CONTACT_QUANT));
            _w16(s, off + 2, (int)(a.get_f("pos", r, 2) / CONTACT_QUANT));
            s[off + 4] = _b(a, r, "faction");
            s[off + 5] = _b(a, r, "etype");
            off += CONTACT_STRIDE;
        }
        return off == buf.Length ? buf : buf[..off];
    }

    public static byte[] snapshot_bytes(World world, SnapshotView v, byte[]? contacts = null)
    {
        var e = world.ecs;
        int[] uids = world.all_units();        // hulls, wrecks, corpses - by id
        int[] pids = world.projectiles();
        int[] sids = e.query("smoke_r");       // canisters and puffs alike
        // Pass one: which rows this pilot's sphere carries.  The counts are in
        // the header, so they have to be known before the first row is written.
        v.keep.Clear();
        var ku = new List<int>(uids.Length);
        foreach (int eid in uids)
        {
            var slot = e.slot(eid);
            if (!slot.ok || !slot.a.has("pos")) continue;
            int et = slot.a.has("etype") ? slot.a.get_u8("etype", slot.row) : 0;
            if (!inside(slot.a, slot.row, v, tier_of_unit(et), eid)) continue;
            ku.Add(eid);
        }
        var kp = new List<int>(pids.Length);
        foreach (int eid in pids)
        {
            var slot = e.slot(eid);
            if (!slot.ok || !slot.a.has("pos")) continue;
            int kind = slot.a.has("proj_kind") ? slot.a.get_u8("proj_kind", slot.row) : 0;
            if (!inside(slot.a, slot.row, v, tier_of_round(kind), eid)) continue;
            kp.Add(eid);
        }
        var ks = new List<int>(sids.Length);
        foreach (int eid in sids)
        {
            var slot = e.slot(eid);
            if (!slot.ok || !slot.a.has("pos")) continue;
            if (!inside(slot.a, slot.row, v, TIER_SMOKE, eid)) continue;
            ks.Add(eid);
        }
        foreach (int eid in ku) v.keep.Add(eid);
        foreach (int eid in kp) v.keep.Add(eid);
        foreach (int eid in ks) v.keep.Add(eid);

        // Allocated whole and never pooled: a buffer cannot be returned until
        // the client's writer task has flushed its copy out.
        int nc = contacts is null ? 0 : contacts.Length / CONTACT_STRIDE;
        var buf = GC.AllocateUninitializedArray<byte>(
            HEADER + ku.Count * UNIT_STRIDE + kp.Count * PROJ_STRIDE
                   + ks.Count * SMOKE_STRIDE + nc * CONTACT_STRIDE);
        var s = buf.AsSpan();
        _w32(s, 0, MAGIC);
        _w32(s, 4, (uint)world.tick);
        _w16(s, 8, ku.Count);
        _w16(s, 10, kp.Count);
        _w16(s, 12, ks.Count);
        _w16(s, 14, nc);

        int off = HEADER;
        var ammo = e.iv("ammo");
        var mag = e.iv("mag");
        var wpn = e.uv("weapon");
        foreach (int eid in ku)
        {
            var slot = e.slot(eid);
            if (!slot.ok) continue;
            var a = slot.a;
            int r = slot.row;
            _w16(s, off + 0, eid);
            s[off + 2] = _b(a, r, "etype");
            s[off + 3] = _b(a, r, "faction");
            // `-1` -> 255, anything else capped at 254 (255 is "nobody")
            int owner = _i(a, r, "owner");
            s[off + 4] = (byte)(owner < 0 ? 255 : Math.Min(owner, 254));
            // bit0/1 the triggers, bit2/3 a reload in progress, bit4-5 what a
            // wreck used to be (0 infantry, 1 apc, 2 mech)
            byte trig = _b(a, r, "trigger");
            byte flags = (byte)(trig & 1);
            flags |= (byte)(((trig >> 1) & 1) << 1);
            if (_f(a, r, "reload_t", 0) > 0f) flags |= 1 << 2;
            if (_f(a, r, "reload_t", 1) > 0f) flags |= 1 << 3;
            flags |= (byte)((_b(a, r, "wreck_kind") & 3) << 4);
            s[off + 5] = flags;
            int tgt = _i(a, r, "target");
            _w16(s, off + 6, tgt < 0 ? 65535 : Math.Min(tgt, 65534));
            _wf(s, off + 8, _f(a, r, "pos", 0));
            _wf(s, off + 12, _f(a, r, "pos", 1));
            _wf(s, off + 16, _f(a, r, "pos", 2));
            _wf(s, off + 20, _f(a, r, "yaw"));
            _wf(s, off + 24, _f(a, r, "pitch"));
            _wf(s, off + 28, _f(a, r, "turret_yaw"));
            _wf(s, off + 32, _f(a, r, "leg_yaw"));
            // the velocity the hull is *actually* moving at (`vel`, not
            // `vel_cmd`): a hull following a slope has a vy
            _wf(s, off + 36, _f(a, r, "vel", 0));
            _wf(s, off + 40, _f(a, r, "vel", 1));
            _wf(s, off + 44, _f(a, r, "vel", 2));
            _w16(s, off + 48, _u16(_f(a, r, "hp")));
            _w16(s, off + 50, 0);                 // MOD_TORSO: never a pool
            _w16(s, off + 52, a.has("arm_hp") ? _u16(_f(a, r, "arm_hp", 0)) : 0);
            _w16(s, off + 54, a.has("arm_hp") ? _u16(_f(a, r, "arm_hp", 1)) : 0);
            // the two-slot counters (`ammo` / `mag` are ints, `weapon` a byte,
            // two arms each) go through the views: the default they answer for
            // a hull that has no arms is exactly what Python's `columns()` fills
            _w16(s, off + 56, _u16(ammo.get(eid, 0)));
            _w16(s, off + 58, _u16(ammo.get(eid, 1)));
            _w16(s, off + 60, _u16(mag.get(eid, 0)));
            _w16(s, off + 62, _u16(mag.get(eid, 1)));
            _w16(s, off + 64, wpn.get(eid, 0));
            _w16(s, off + 66, wpn.get(eid, 1));
            // where the crew is looking; `yaw` / `pitch` above are the laid gun
            _wf(s, off + 68, _f(a, r, "sight_yaw"));
            _wf(s, off + 72, _f(a, r, "sight_pitch"));
            // 76: `pad3`, zero, keeps the row 80 B and 4-byte aligned
            off += UNIT_STRIDE;
        }

        foreach (int eid in kp)
        {
            var slot = e.slot(eid);
            if (!slot.ok) continue;
            var a = slot.a;
            int r = slot.row;
            _w16(s, off + 0, eid);
            s[off + 2] = _b(a, r, "proj_kind");
            s[off + 3] = _b(a, r, "proj_wid");
            s[off + 4] = _b(a, r, "faction");
            _wf(s, off + 8, _f(a, r, "pos", 0));
            _wf(s, off + 12, _f(a, r, "pos", 1));
            _wf(s, off + 16, _f(a, r, "pos", 2));
            _wf(s, off + 20, _f(a, r, "vel", 0));
            _wf(s, off + 24, _f(a, r, "vel", 1));
            _wf(s, off + 28, _f(a, r, "vel", 2));
            off += PROJ_STRIDE;
        }

        foreach (int eid in ks)
        {
            var slot = e.slot(eid);
            if (!slot.ok) continue;
            var a = slot.a;
            int r = slot.row;
            _w16(s, off + 0, eid);
            // bit0: still a canister on its way, i.e. not a deployed puff
            s[off + 2] = (byte)(a.has("canister") ? 1 : 0);
            _wf(s, off + 4, _f(a, r, "pos", 0));
            _wf(s, off + 8, _f(a, r, "pos", 1));
            _wf(s, off + 12, _f(a, r, "pos", 2));
            _wf(s, off + 16, _f(a, r, "smoke_r", 0));
            _wf(s, off + 20, _f(a, r, "smoke_r", 1));
            _wf(s, off + 24, _f(a, r, "smoke_r", 2));
            _wf(s, off + 28, _f(a, r, "smoke_t"));
            off += SMOKE_STRIDE;
        }
        if (nc > 0) contacts!.CopyTo(s[off..]);
        return buf;
    }

    // ------------------------------------------------------------------
    // meta
    // ------------------------------------------------------------------
    /// <summary>`round(x, n)` - banker's rounding, which is what Python's
    /// `round` does for a float, so a value that lands on a .5 down the last
    /// digit rounds the same way on both servers.</summary>
    private static float _r(float v, int digits) => MathF.Round(v, digits);
    private static int _iround(float v) => (int)MathF.Round(v);
    private static float _num(object? v) => v switch
    {
        float f => f,
        int i => i,
        long l => l,
        double d => (float)d,
        _ => 0f,
    };

    /// <summary>One arm as the rearm panel needs it: hardware, rounds, and its hp.</summary>
    public static Dictionary<string, object?> _arm_meta(World world, Player pl, int arm)
    {
        var s = Shop.arm_state(world, pl, arm);
        return new Dictionary<string, object?>
        {
            ["wid"] = s["wid"],
            ["hp"] = _iround(_num(s["hp"])),
            ["hpmax"] = _iround(_num(s["hpmax"])),
            ["mag"] = s["mag"],
            ["reserve"] = s["reserve"],
        };
    }

    public static Dictionary<string, object?> meta_payload(World world, Player pl)
    {
        var e = world.ecs;
        bool mech_ok = e.is_alive(pl.mech);
        int[] hp = { 0, 0, 0, 0 };
        if (mech_ok)
        {
            var cols = hp_columns(world, new[] { pl.mech });
            for (int c = 0; c < 4; c++) hp[c] = cols[c];
        }
        // the job at the wrench, for the progress bar at the bottom of the screen
        object? work = null;
        if (mech_ok && pl.work is not null)
        {
            work = new Dictionary<string, object?>
            {
                ["t"] = _r(MathF.Max(0f, e.fv("work_remaining_time")[pl.mech]), 2),
                ["total"] = _r(pl.work_total, 2),
                ["label"] = Shop.work_label(pl.work),
            };
        }
        var players = new List<object?>(world.players.Count);
        foreach (var p in world.players.Values)
            players.Add(new Dictionary<string, object?>
            {
                ["id"] = p.pid, ["name"] = p.name, ["faction"] = p.faction,
                ["kills"] = p.kills, ["deaths"] = p.deaths,
                ["money"] = _iround(p.money), ["alive"] = p.mech >= 0,
            });
        var towns = new List<object?>(world.towns.Count);
        foreach (var t in world.towns)
            towns.Add(new Dictionary<string, object?>
            {
                ["x"] = t.x, ["z"] = t.z, ["owner"] = t.owner,
                ["progress"] = _r(t.progress, 2), ["name"] = t.name,
            });
        return new Dictionary<string, object?>
        {
            ["t"] = "meta",
            ["pid"] = pl.pid,
            ["mech"] = mech_ok ? pl.mech : -1,
            ["faction"] = pl.faction,
            ["money"] = _iround(pl.money),
            ["kills"] = pl.kills,
            ["deaths"] = pl.deaths,
            ["score"] = pl.score,
            ["respawn"] = _r(MathF.Max(0f, pl.respawn), 2),
            ["god"] = pl.god,
            ["hp"] = hp,
            ["lock_target"] = pl.lock_progress > 0f ? pl.lock_target : -1,
            ["lock_progress"] = _r(pl.lock_progress, 3),
            ["smoke"] = pl.smoke_charges,
            ["arms"] = new object?[] { _arm_meta(world, pl, 0), _arm_meta(world, pl, 1) },
            ["work"] = work,
            ["smoke_cd"] = _r(MathF.Max(0f, pl.smoke_cd), 1),
            ["wave"] = world.wave,
            ["time"] = _r(world.time, 1),
            ["winner"] = world.winner,
            ["end_timer"] = _r(MathF.Max(0f, world.end_timer), 1),
            ["players"] = players,
            ["towns"] = towns,
        };
    }

    // ------------------------------------------------------------------
    // sound: the events whose wavefront still arrives loud enough to be heard
    // ------------------------------------------------------------------
    // The cues that are carried by the air.  Everything else on the event list
    // is a message or a light (a kill, a capture, the rearm label, new tiles)
    // and is not gated by distance.
    public static readonly string[] SOUND_KINDS = { "shot", "boom" };

    /// <summary>How loud this event is at its own muzzle, or null if it makes
    /// no sound at all.</summary>
    public static float? _source_level(Dictionary<string, object?> ev)
    {
        if (!ev.TryGetValue("k", out var kv) || kv is not string k) return null;
        if (k != "shot" && k != "boom") return null;
        if (k == "shot") return Defs.SOUND_LEVEL["shot"];
        if (_truthy(ev, "blast")) return Defs.SOUND_LEVEL["blast"];
        return _truthy(ev, "big") ? Defs.SOUND_LEVEL["big"] : Defs.SOUND_LEVEL["boom"];
    }

    /// <summary>Python's truthiness for a value that came out of an event dict
    /// (`blast=1`, `big=0`, an absent key).</summary>
    private static bool _truthy(Dictionary<string, object?> ev, string key)
    {
        if (!ev.TryGetValue(key, out var v) || v is null) return false;
        return v switch
        {
            bool b => b,
            int i => i != 0,
            long l => l != 0,
            float f => f != 0f,
            double d => d != 0d,
            string s => s.Length > 0,
            _ => true,
        };
    }

    /// <summary>The events whose wavefront still arrives loud enough to be heard.
    ///
    /// `ear` is the pilot's mech *now*, not where it will be when the wavefront
    /// gets there: at 8 km the curve is nearly flat (~3 % per km), so a hull
    /// that covered another 300 m in the meantime hears a level within a few
    /// per cent.  `null` (no mech, no ear) drops every sound but keeps every
    /// message.</summary>
    public static List<Dictionary<string, object?>> hearable(
        List<Dictionary<string, object?>> events, (float x, float y, float z)? ear)
    {
        if (events.Count == 0) return events;
        float reach = Defs.SOUND_SPEED * Defs.SOUND_TTL;    // nobody could still hear it
        var outv = new List<Dictionary<string, object?>>(events.Count);
        foreach (var ev in events)
        {
            float? level = _source_level(ev);
            if (level is null)
            {
                outv.Add(ev);
                continue;
            }
            if (ear is null) continue;
            float dx = _num(ev.GetValueOrDefault("x")) - ear.Value.x;
            float dy = _num(ev.GetValueOrDefault("y")) - ear.Value.y;
            float dz = _num(ev.GetValueOrDefault("z")) - ear.Value.z;
            float dist = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
            if (dist <= reach && Defs.sound_gain(dist, level.Value) > Defs.SOUND_FLOOR)
                outv.Add(ev);
        }
        return outv;
    }
}
