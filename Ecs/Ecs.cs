// Archetype ECS: an entity's component set is its type.
//
// A component is one dense buffer over the entities that own it, not one array
// indexed by a global entity slot.  The group of entities owning exactly the
// same component set is an `Archetype`, and it is what holds those buffers, so
// an id is no longer a storage address - `_arch` / `_row` turn it into
// (archetype, row) and everything else addresses entities by id.
//
// What keeps this honest is one rule: **the component set never changes.**  A
// state transition that would add or drop a component is a *different* entity -
// `Systems/Smoke.cs` destroys a canister and creates the puff it bursts into.
//
// This is a line-for-line port of `backend/server/ecs.py` - same classes, same
// method names, same semantics - with numpy's typed buffers replaced by plain
// C# arrays of the same element type.  `float` is `np.float32`, `int` is
// `np.int32`, `ushort` is `np.uint16` and `byte` is `np.uint8`; every buffer is
// flat, with `stride` elements per row, so component `pos` of row `r` is
// `f("pos")[r * 3 + 0..2]`.
//
// The one thing that did not survive the crossing is `View`'s fancy indexing
// (`view[ids, i]` over a numpy array): in C# the batch forms write into a
// caller-supplied array (`fv.get(ids, i, dst)`), and hot systems do not use a
// View at all - they take the archetype's buffer (`Archetype.f("pos")`) and
// walk rows, which is what `ecs.groups(...)` is for.

namespace Mech.Server;

/// <summary>Element type of a component buffer.</summary>
public enum Kind { F, I, U8, U16 }

/// <summary>What the registry knows about one component kind.</summary>
public sealed class Spec
{
    public Kind kind;
    public int stride = 1;      // elements per entity (shape product)
    public double dflt;
}

/// <summary>Every entity with exactly one component set, in dense rows.</summary>
public sealed class Archetype
{
    public const int INITIAL = 16;      // first buffer size, doubled as it fills

    public int index;
    public string[] key;                // component names, sorted
    public HashSet<string> keyset;
    public Dictionary<string, Array> bufs = new();
    public int[] ids;                   // row -> entity id
    public int n;
    public int cap;
    private readonly ECS _ecs;

    public Archetype(ECS ecs, int index, string[] key, Dictionary<string, Spec> specs)
    {
        _ecs = ecs;
        this.index = index;
        this.key = key;
        keyset = new HashSet<string>(key);
        ids = new int[INITIAL];
        n = 0;
        cap = INITIAL;
        foreach (var name in key)
        {
            var s = specs[name];
            bufs[name] = NewBuf(s, INITIAL);
            Fill(bufs[name], 0, INITIAL, s);
        }
    }

    // -- typed doors ------------------------------------------------------
    // Every accessor is a cast plus an index; a system hoists the buffer out of
    // its loop and never pays the dictionary again.
    public float[] f(string name) => (float[])bufs[name];
    public int[] i(string name) => (int[])bufs[name];
    public byte[] u8(string name) => (byte[])bufs[name];
    public ushort[] u16(string name) => (ushort[])bufs[name];
    public Array buf(string name) => bufs[name];
    public bool has(string name) => keyset.Contains(name);

    public float get_f(string name, int row, int c = 0) => ((float[])bufs[name])[row * _ecs.stride(name) + c];
    public void set_f(string name, int row, float v) => ((float[])bufs[name])[row * _ecs.stride(name)] = v;
    public void set_f(string name, int row, int c, float v) => ((float[])bufs[name])[row * _ecs.stride(name) + c] = v;
    public void set_f3(string name, int row, double x, double y, double z)
    {
        var b = (float[])bufs[name];
        int o = row * 3;
        b[o] = (float)x; b[o + 1] = (float)y; b[o + 2] = (float)z;
    }
    public Vec3 get_f3(string name, int row)
    {
        var b = (float[])bufs[name];
        int o = row * 3;
        return new Vec3(b[o], b[o + 1], b[o + 2]);
    }

    public int get_i(string name, int row) => ((int[])bufs[name])[row * _ecs.stride(name)];
    public void set_i(string name, int row, int v) => ((int[])bufs[name])[row * _ecs.stride(name)] = v;
    public byte get_u8(string name, int row) => ((byte[])bufs[name])[row * _ecs.stride(name)];
    public void set_u8(string name, int row, byte v) => ((byte[])bufs[name])[row * _ecs.stride(name)] = v;
    public ushort get_u16(string name, int row) => ((ushort[])bufs[name])[row * _ecs.stride(name)];
    public void set_u16(string name, int row, ushort v) => ((ushort[])bufs[name])[row * _ecs.stride(name)] = v;

    // -- rows -------------------------------------------------------------
    public int add(int eid, Dictionary<string, Spec> specs)
    {
        if (n == cap) _resize(cap * 2);
        int row = n;
        foreach (var name in key) ResetRow(bufs[name], row, specs[name]);
        ids[row] = eid;
        n += 1;
        return row;
    }

    /// <summary>Drop `row`, moving the last row into the hole.  Returns the id
    /// of the entity that moved, or -1 when the hole was already the last row.</summary>
    public int swap_remove(int row)
    {
        int last = n - 1;
        int moved = -1;
        if (row != last)
        {
            moved = ids[last];
            foreach (var name in key) CopyRow(bufs[name], row, last, _ecs.stride(name));
            ids[row] = moved;
        }
        n = last;
        return moved;
    }

    /// <summary>The live rows of one component: the whole buffer up to `n`.</summary>
    public Array buffer(string name) => bufs[name];

    private void _resize(int newCap)
    {
        // Growing swaps in new arrays, which invalidates every buffer list a
        // caller is holding (`ecs.buffers`, a view's cache).  Bumping the ECS
        // generation is what makes those caches rebuild - without it a loop
        // keeps writing into the old, shorter array.
        foreach (var name in key)
        {
            var old = bufs[name];
            var s = _ecs.spec(name);
            var grown = NewBuf(s, newCap);
            Array.Copy(old, grown, Math.Min(old.Length, grown.Length));
            bufs[name] = grown;
        }
        var grownIds = new int[newCap];
        Array.Copy(ids, grownIds, n);
        ids = grownIds;
        cap = newCap;
        _ecs._gen += 1;
    }

    private static Array NewBuf(Spec s, int cap)
    {
        int len = cap * s.stride;
        return s.kind switch
        {
            Kind.F => new float[len],
            Kind.I => new int[len],
            Kind.U8 => new byte[len],
            Kind.U16 => new ushort[len],
            _ => new float[len],
        };
    }

    private static void Fill(Array buf, int fromRow, int toRow, Spec s)
    {
        int a = fromRow * s.stride, b = toRow * s.stride;
        switch (s.kind)
        {
            case Kind.F:
                var fv = (float[])buf;
                for (int k = a; k < b; k++) fv[k] = (float)s.dflt;
                break;
            case Kind.I:
                var iv = (int[])buf;
                for (int k = a; k < b; k++) iv[k] = (int)s.dflt;
                break;
            case Kind.U8:
                var bv = (byte[])buf;
                for (int k = a; k < b; k++) bv[k] = (byte)s.dflt;
                break;
            case Kind.U16:
                var uv = (ushort[])buf;
                for (int k = a; k < b; k++) uv[k] = (ushort)s.dflt;
                break;
        }
    }

    private static void ResetRow(Array buf, int row, Spec s) => Fill(buf, row, row + 1, s);

    private static void CopyRow(Array buf, int dst, int src, int stride)
    {
        Array.Copy(buf, src * stride, buf, dst * stride, stride);
    }
}

/// <summary>One archetype, ready to iterate: buffers hoisted, rows are `0..n-1`.
///
/// Every live row of an archetype owns the whole component set, so a system
/// that matches a group never has to test an entity - it walks rows.</summary>
public sealed class Group
{
    public Archetype arch;
    public Dictionary<string, Array> bufs;
    public int[] ids;
    public int n;

    public Group(Archetype arch)
    {
        this.arch = arch;
        bufs = arch.bufs;
        ids = arch.ids;
        n = arch.n;
    }

    public float[] f(string name) => (float[])bufs[name];
    public int[] i(string name) => (int[])bufs[name];
    public byte[] u8(string name) => (byte[])bufs[name];
    public ushort[] u16(string name) => (ushort[])bufs[name];
    public bool has(string name) => arch.keyset.Contains(name);
}

/// <summary>A single-entity location: `a` + `row`, or `ok == false` when dead.</summary>
public struct Slot
{
    public Archetype a;
    public int row;
    public bool ok;
}

/// <summary>Component access by entity id, for the scattered reads.
///
/// A batch read fills a caller array, a batch write scatters, and an id that
/// does not own the component reads its registered default - the same answer
/// the Python slot array gave (a wreck has no `weapon`, so it reads 0, and no
/// `target`, so it reads -1).  Hot loops should use `ECS.slot` or
/// `ECS.groups` instead; this is the door for a system that touches a handful
/// of entities it did not just query.
/// </summary>
public abstract class View
{
    protected readonly ECS _ecs;
    protected readonly string _name;
    protected readonly int _stride;
    protected readonly double _dflt;
    protected object?[] _lut = Array.Empty<object?>();
    protected int _gen = -1;

    protected View(ECS ecs, string name)
    {
        _ecs = ecs;
        _name = name;
        _stride = ecs.stride(name);
        _dflt = ecs.spec(name).dflt;
    }

    protected void Refresh()
    {
        if (_gen != _ecs._gen)
        {
            _lut = _ecs.buffers(_name);
            _gen = _ecs._gen;
        }
    }

    /// <summary>The archetype holding this id, or null (dead / component absent).</summary>
    protected Archetype? arch_of(int eid)
    {
        if (eid < 0 || eid >= _ecs._high || !_ecs.alive[eid]) return null;
        int ai = _ecs._arch[eid];
        if (ai < 0) return null;
        Refresh();
        return _lut[ai] as Archetype;
    }
}

public sealed class FView : View
{
    public FView(ECS ecs, string name) : base(ecs, name) { }

    public float this[int eid]
    {
        get
        {
            var a = arch_of(eid);
            return a is null ? (float)_dflt : a.f(_name)[_ecs._row[eid] * _stride];
        }
        set
        {
            var a = arch_of(eid) ?? throw new KeyNotFoundException($"entity {eid} has no '{_name}'");
            a.f(_name)[_ecs._row[eid] * _stride] = value;
        }
    }

    public float get(int eid, int c)
    {
        var a = arch_of(eid);
        return a is null ? (float)_dflt : a.f(_name)[_ecs._row[eid] * _stride + c];
    }

    public Vec3 get3(int eid)
    {
        var a = arch_of(eid);
        if (a is null) return new Vec3((float)_dflt, (float)_dflt, (float)_dflt);
        return a.get_f3(_name, _ecs._row[eid]);
    }

    public void set(int eid, int c, float v)
    {
        var a = arch_of(eid) ?? throw new KeyNotFoundException($"entity {eid} has no '{_name}'");
        a.f(_name)[_ecs._row[eid] * _stride + c] = v;
    }

    public void set3(int eid, double x, double y, double z)
    {
        var a = arch_of(eid) ?? throw new KeyNotFoundException($"entity {eid} has no '{_name}'");
        a.set_f3(_name, _ecs._row[eid], x, y, z);
    }

    public void set3(int eid, Vec3 v) => set3(eid, v.x, v.y, v.z);

    /// <summary>Batch read of one lane into `dst` (default where absent).</summary>
    public void get(int[] ids, int c, float[] dst)
    {
        for (int k = 0; k < ids.Length; k++)
        {
            var a = arch_of(ids[k]);
            dst[k] = a is null ? (float)_dflt : a.f(_name)[_ecs._row[ids[k]] * _stride + c];
        }
    }

    /// <summary>Batch read of the whole value into `dst` (`ids.Length * stride`).</summary>
    public void get(int[] ids, float[] dst)
    {
        for (int k = 0; k < ids.Length; k++)
        {
            var a = arch_of(ids[k]);
            int o = k * _stride;
            if (a is null) { for (int c = 0; c < _stride; c++) dst[o + c] = (float)_dflt; }
            else Array.Copy(a.f(_name), _ecs._row[ids[k]] * _stride, dst, o, _stride);
        }
    }

    public void set(int[] ids, int c, float[] src)
    {
        for (int k = 0; k < ids.Length; k++) set(ids[k], c, src[k]);
    }
}

public sealed class IView : View
{
    public IView(ECS ecs, string name) : base(ecs, name) { }

    public int this[int eid]
    {
        get
        {
            var a = arch_of(eid);
            return a is null ? (int)_dflt : a.i(_name)[_ecs._row[eid] * _stride];
        }
        set
        {
            var a = arch_of(eid) ?? throw new KeyNotFoundException($"entity {eid} has no '{_name}'");
            a.i(_name)[_ecs._row[eid] * _stride] = value;
        }
    }

    public int get(int eid, int c)
    {
        var a = arch_of(eid);
        return a is null ? (int)_dflt : a.i(_name)[_ecs._row[eid] * _stride + c];
    }

    public void set(int eid, int c, int v)
    {
        var a = arch_of(eid) ?? throw new KeyNotFoundException($"entity {eid} has no '{_name}'");
        a.i(_name)[_ecs._row[eid] * _stride + c] = v;
    }

    public void get(int[] ids, int c, int[] dst)
    {
        for (int k = 0; k < ids.Length; k++)
        {
            var a = arch_of(ids[k]);
            dst[k] = a is null ? (int)_dflt : a.i(_name)[_ecs._row[ids[k]] * _stride + c];
        }
    }

    public void set(int[] ids, int c, int[] src)
    {
        for (int k = 0; k < ids.Length; k++) set(ids[k], c, src[k]);
    }
}

public sealed class U8View : View
{
    public U8View(ECS ecs, string name) : base(ecs, name) { }

    public byte this[int eid]
    {
        get
        {
            var a = arch_of(eid);
            return a is null ? (byte)_dflt : a.u8(_name)[_ecs._row[eid] * _stride];
        }
        set
        {
            var a = arch_of(eid) ?? throw new KeyNotFoundException($"entity {eid} has no '{_name}'");
            a.u8(_name)[_ecs._row[eid] * _stride] = value;
        }
    }

    public byte get(int eid, int c)
    {
        var a = arch_of(eid);
        return a is null ? (byte)_dflt : a.u8(_name)[_ecs._row[eid] * _stride + c];
    }

    public void set(int eid, int c, byte v)
    {
        var a = arch_of(eid) ?? throw new KeyNotFoundException($"entity {eid} has no '{_name}'");
        a.u8(_name)[_ecs._row[eid] * _stride + c] = v;
    }

    public void get(int[] ids, int c, byte[] dst)
    {
        for (int k = 0; k < ids.Length; k++)
        {
            var a = arch_of(ids[k]);
            dst[k] = a is null ? (byte)_dflt : a.u8(_name)[_ecs._row[ids[k]] * _stride + c];
        }
    }
}

public sealed class U16View : View
{
    public U16View(ECS ecs, string name) : base(ecs, name) { }

    public ushort this[int eid]
    {
        get
        {
            var a = arch_of(eid);
            return a is null ? (ushort)_dflt : a.u16(_name)[_ecs._row[eid] * _stride];
        }
        set
        {
            var a = arch_of(eid) ?? throw new KeyNotFoundException($"entity {eid} has no '{_name}'");
            a.u16(_name)[_ecs._row[eid] * _stride] = value;
        }
    }

    public ushort get(int eid, int c)
    {
        var a = arch_of(eid);
        return a is null ? (ushort)_dflt : a.u16(_name)[_ecs._row[eid] * _stride + c];
    }

    public void set(int eid, int c, ushort v)
    {
        var a = arch_of(eid) ?? throw new KeyNotFoundException($"entity {eid} has no '{_name}'");
        a.u16(_name)[_ecs._row[eid] * _stride + c] = v;
    }

    public void get(int[] ids, int c, ushort[] dst)
    {
        for (int k = 0; k < ids.Length; k++)
        {
            var a = arch_of(ids[k]);
            dst[k] = a is null ? (ushort)_dflt : a.u16(_name)[_ecs._row[ids[k]] * _stride + c];
        }
    }
}

public sealed class ECS
{
    public const int GROW = 256;        // first size of the id -> location tables

    private readonly Dictionary<string, Spec> _specs = new();
    public readonly List<Archetype> archetypes = new();
    private readonly Dictionary<string, Archetype> _by_key = new();
    private readonly Dictionary<string, View> _views = new();
    private readonly Stack<int> _free = new();

    public bool[] alive = Array.Empty<bool>();
    public int[] _arch = Array.Empty<int>();
    public int[] _row = Array.Empty<int>();
    public int _high;
    public int _gen;
    /// <summary>Ids destroyed since the last `clear_destroyed` (the id a corpse
    /// just left may be reused at once - this is how a system sees the list).</summary>
    public readonly List<int> destroyed = new();

    private int[] _qbuf = new int[64];
    private int[] _stamp = Array.Empty<int>();
    private int _stamp_gen;

    public ECS(int capacity = GROW)
    {
        alive = new bool[capacity];
        _arch = new int[capacity];
        _row = new int[capacity];
        for (int k = 0; k < capacity; k++) _arch[k] = -1;
    }

    // -- registry ---------------------------------------------------------
    public void register(string name, Kind kind, int shape = 1, double dflt = 0.0)
    {
        _specs[name] = new Spec { kind = kind, stride = Math.Max(1, shape), dflt = dflt };
    }

    public Spec spec(string name) => _specs[name];
    public int stride(string name) => _specs[name].stride;
    public bool known(string name) => _specs.ContainsKey(name);
    public IEnumerable<string> spec_names => _specs.Keys;

    /// <summary>Per-archetype buffers for one component (`null` where an
    /// archetype does not own it); the View cache, one entry per archetype.</summary>
    public object?[] buffers(string name)
    {
        var outv = new object?[archetypes.Count];
        for (int k = 0; k < archetypes.Count; k++)
            outv[k] = archetypes[k].keyset.Contains(name) ? archetypes[k] : null;
        return outv;
    }

    public FView fv(string name) => (FView)view(name, Kind.F);
    public IView iv(string name) => (IView)view(name, Kind.I);
    public U8View uv(string name) => (U8View)view(name, Kind.U8);
    public U16View uv16(string name) => (U16View)view(name, Kind.U16);

    private View view(string name, Kind kind)
    {
        if (_views.TryGetValue(name, out var v)) return v;
        if (!_specs.TryGetValue(name, out var s))
            throw new KeyNotFoundException($"unknown component '{name}'");
        v = kind switch
        {
            Kind.F => new FView(this, name),
            Kind.I => new IView(this, name),
            Kind.U8 => new U8View(this, name),
            _ => new U16View(this, name),
        };
        _views[name] = v;
        return v;
    }

    // -- entity lifecycle -------------------------------------------------
    /// <summary>Spawn an entity owning exactly `comps`.  This is the only place a
    /// component set is decided.</summary>
    public int create(IEnumerable<string> comps)
    {
        var key = comps.Distinct().OrderBy(x => x, StringComparer.Ordinal).ToArray();
        if (!_by_key.TryGetValue(KeyOf(key), out var arch))
        {
            foreach (var name in key)
                if (!_specs.ContainsKey(name))
                    throw new KeyNotFoundException($"unknown component '{name}'");
            arch = new Archetype(this, archetypes.Count, key, _specs);
            archetypes.Add(arch);
            _by_key[KeyOf(key)] = arch;
            _gen += 1;
        }
        int eid = _alloc();
        _arch[eid] = arch.index;
        _row[eid] = arch.add(eid, _specs);
        alive[eid] = true;
        return eid;
    }

    public int create(params string[] comps) => create((IEnumerable<string>)comps);

    private static string KeyOf(string[] key) => string.Join('\u0001', key);

    public void destroy(int eid)
    {
        if (eid < 0 || eid >= _high || !alive[eid]) return;
        int ai = _arch[eid];
        if (ai >= 0)
        {
            var arch = archetypes[ai];
            int row = _row[eid];
            int moved = arch.swap_remove(row);
            if (moved >= 0) _row[moved] = row;
            _arch[eid] = -1;
        }
        alive[eid] = false;
        _row[eid] = 0;
        _free.Push(eid);
        destroyed.Add(eid);
    }

    private int _alloc()
    {
        if (_free.Count > 0) return _free.Pop();     // LIFO: the id a corpse just left
        int eid = _high;
        if (eid >= alive.Length) _grow(Math.Max(GROW, alive.Length * 2));
        _high = eid + 1;
        return eid;
    }

    private void _grow(int cap)
    {
        int old = alive.Length;
        var a2 = new bool[cap];
        var ar2 = new int[cap];
        var rw2 = new int[cap];
        Array.Fill(ar2, -1);
        Array.Copy(alive, a2, old);
        Array.Copy(_arch, ar2, old);
        Array.Copy(_row, rw2, old);
        alive = a2; _arch = ar2; _row = rw2;
    }

    public bool is_alive(int eid) => eid >= 0 && eid < _high && alive[eid];

    // -- queries ----------------------------------------------------------
    /// <summary>Ids of the entities owning all of `names`, ascending.
    ///
    /// Ascending matters: it is the order the old slot scan produced, and the
    /// order systems consume the world in - a system that draws random numbers
    /// per entity must see the same order every tick.</summary>
    public int[] query(params string[] names)
    {
        if (names.Length == 0)
        {
            var all = new int[_high];
            int m = 0;
            for (int k = 0; k < _high; k++) if (alive[k]) all[m++] = k;
            return all[..m];
        }
        int total = 0;
        for (int k = 0; k < archetypes.Count; k++)
        {
            var a = archetypes[k];
            if (a.n == 0 || !owns(a, names)) continue;
            total += a.n;
        }
        if (total == 0) return Array.Empty<int>();
        if (_qbuf.Length < total) _qbuf = new int[Math.Max(total, _qbuf.Length * 2)];
        int w = 0;
        for (int k = 0; k < archetypes.Count; k++)
        {
            var a = archetypes[k];
            if (a.n == 0 || !owns(a, names)) continue;
            Array.Copy(a.ids, 0, _qbuf, w, a.n);
            w += a.n;
        }
        if (w != total) total = w;
        var outIds = _qbuf[..total];
        Array.Sort(outIds);
        return outIds;
    }

    private static bool owns(Archetype a, string[] names)
    {
        foreach (var name in names) if (!a.keyset.Contains(name)) return false;
        return true;
    }

    public int count(params string[] names) => query(names).Length;

    public bool has(int eid, params string[] names)
    {
        if (eid < 0 || eid >= _high || !alive[eid]) return false;
        int ai = _arch[eid];
        if (ai < 0) return false;
        var keyset = archetypes[ai].keyset;
        foreach (var name in names) if (!keyset.Contains(name)) return false;
        return true;
    }

    /// <summary>Vectorised `has`.</summary>
    public bool[] has_arr(int[] ids, string name)
    {
        var outv = new bool[ids.Length];
        var lut = new bool[Math.Max(1, archetypes.Count)];
        for (int k = 0; k < archetypes.Count; k++) lut[k] = archetypes[k].keyset.Contains(name);
        for (int k = 0; k < ids.Length; k++)
        {
            int eid = ids[k];
            if (eid < 0 || eid >= _high || !alive[eid]) continue;
            int ai = _arch[eid];
            outv[k] = ai >= 0 && lut[ai];
        }
        return outv;
    }

    /// <summary>(rows, archetype index) for ascending ids - the order preserving
    /// door, for the systems whose iteration order must not change because it
    /// draws the random numbers.</summary>
    public void locate(int[] ids, out int[] rows, out int[] arch)
    {
        rows = new int[ids.Length];
        arch = new int[ids.Length];
        for (int k = 0; k < ids.Length; k++)
        {
            int eid = ids[k];
            if (eid >= 0 && eid < _high)
            {
                rows[k] = _row[eid];
                arch[k] = alive[eid] ? _arch[eid] : -1;
            }
            else
            {
                rows[k] = 0;
                arch[k] = -1;
            }
        }
    }

    /// <summary>The non-empty archetypes owning all of `names`, buffers hoisted.
    ///
    /// Iterating these is archetype-major: fine for anything that integrates
    /// entities independently (movement, countdowns), but it reorders the
    /// world, so a system that draws random numbers per entity must keep using
    /// `query` + `locate` instead.</summary>
    public List<Group> groups(params string[] names)
    {
        var outv = new List<Group>();
        foreach (var a in archetypes)
            if (a.n > 0 && owns(a, names)) outv.Add(new Group(a));
        return outv;
    }

    // -- single entity ----------------------------------------------------
    /// <summary>The entity's archetype, or null (dead / no archetype).</summary>
    public Archetype? arch_of(int eid)
    {
        if (eid < 0 || eid >= _high || !alive[eid]) return null;
        int ai = _arch[eid];
        return ai < 0 ? null : archetypes[ai];
    }

    public int row_of(int eid) => _row[eid];

    /// <summary>(the entity's archetype, its row in it) - one lookup, then every
    /// access is `a.f("pos")[row * 3]` instead of a View round trip.
    /// `slot.ok` false means the entity is dead.</summary>
    public Slot slot(int eid)
    {
        var a = arch_of(eid);
        return a is null ? new Slot { a = null!, row = -1, ok = false }
                         : new Slot { a = a, row = _row[eid], ok = true };
    }

    /// <summary>The fast door for a single entity: `(buffer, row)` straight into
    /// a typed array, without building a view.</summary>
    public float[] row_f(int eid, string name, out int row)
    {
        row = _row[eid];
        return ((float[])archetypes[_arch[eid]].bufs[name]);
    }

    public int[] row_i(int eid, string name, out int row)
    {
        row = _row[eid];
        return ((int[])archetypes[_arch[eid]].bufs[name]);
    }

    /// <summary>Assert the id -> (archetype, row) table and the archetypes agree.
    /// Cheap enough to run at the end of a replay: it catches bookkeeping that a
    /// value comparison would not, like a row pointing past a buffer.</summary>
    public void validate()
    {
        var aliveIds = query();
        foreach (var eid in aliveIds)
        {
            int ai = _arch[eid];
            if (ai < 0) throw new InvalidOperationException($"entity {eid} is alive but has no archetype");
            var arch = archetypes[ai];
            int row = _row[eid];
            if (row < 0 || row >= arch.n)
                throw new InvalidOperationException($"entity {eid}: row {row} outside {arch.n}");
            if (arch.ids[row] != eid)
                throw new InvalidOperationException($"row {row} of {ai} holds {arch.ids[row]}");
        }
        foreach (var arch in archetypes)
        {
            for (int row = 0; row < arch.n; row++)
            {
                int eid = arch.ids[row];
                if (!alive[eid]) throw new InvalidOperationException($"archetype row holds dead entity {eid}");
                if (_arch[eid] != arch.index || _row[eid] != row)
                    throw new InvalidOperationException($"entity {eid} is not at {arch.index}/{row}");
            }
        }
    }

    /// <summary>Forget the ids destroyed so far (called once a tick, after the
    /// systems that care have read them).</summary>
    public void clear_destroyed() => destroyed.Clear();

    // `_stamp` is scratch for systems that need a per-tick visited set.
    public int[] stamps(int size)
    {
        if (_stamp.Length < size) _stamp = new int[Math.Max(size, _stamp.Length * 2)];
        return _stamp;
    }

    public int stamp_gen() => ++_stamp_gen;
}
