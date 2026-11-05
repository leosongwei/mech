// The two halves of a typed query: `Columns<TRow>` is one archetype's buffers and
// `IRow<TColumns>` is one entity's place in them.
//
// A system declares the pair it needs in its own file - `Systems/Flights.cs` has
// `Flights.RoundColumns` / `Flights.RoundRow` - so the components a pass reads and
// the pass that reads them sit on one screen.  What lives here is only the
// concept: a column object answers `n`, `ids` and "row i"; a row object answers
// "which columns, which index".
//
// A row is a `readonly struct` of 16 bytes (the columns reference and the index),
// not data: its accessors read and write the buffer directly, so `r.age = x` is
// `columns.age[i] = x` and there is no write-back step.  That is why a row cannot
// be a class - one heap object per entity per pass is the one cost this must not
// have - and why the row's "abstract base" is an interface: C# structs cannot
// inherit a class.

namespace Mech.Server;

/// <summary>One archetype's columns for one pass: `n` rows, the row -> id table,
/// and the door to a single row.
///
/// A schema implements three things: `bind` (component name -> buffer, once per
/// archetype), `row` (the marker a row loop walks), and `Names` (the component
/// list the query was made from).</summary>
public abstract class Columns<TRow> where TRow : struct
{
    public readonly int n;              // rows in this archetype
    public readonly int[] ids;          // row -> entity id

    // A component name that is not registered matches no archetype, and a pass
    // that matches nothing is silently dead, so every schema's `Names` is checked
    // against the registry once.  The set is a static of the generic class, i.e.
    // per row type; a schema that extends another shares the row type, so the
    // check is keyed on the schema class itself.
    private static readonly HashSet<Type> _checked = new();

    protected Columns(Group g)
    {
        n = g.n;
        ids = g.ids;
        if (n > 0 && !_checked.Contains(GetType()))
        {
            var ecs = g.arch.ecs;
            foreach (var name in Names) _ = ecs.spec(name);   // throws on an unknown component
            _checked.Add(GetType());
        }
    }

    /// <summary>Replace every component name with this archetype's buffer.  Called
    /// once by the schema's own constructor, after `base(g)`.</summary>
    protected abstract void bind(Group g);

    /// <summary>Row `i`, for `i` in `0..n-1`: 16 bytes copied, no allocation.</summary>
    public abstract TRow row(int i);

    /// <summary>The component list this schema is queried and bound by.</summary>
    public abstract string[] Names { get; }

    /// <summary>Rows in order, so a pass reads `foreach (var r in c)`.  The
    /// enumerator is a struct (no allocation) and `Current` is the same
    /// `row(i)` a caller could ask for.</summary>
    public Enumerator GetEnumerator() => new Enumerator(this);

    /// <summary>Walks `0..n-1` of one archetype.  `Current` builds the row marker,
    /// which is what `row(i)` does - the loop costs one call per row and nothing
    /// else.</summary>
    public struct Enumerator
    {
        private readonly Columns<TRow> _c;
        private int _i;

        public Enumerator(Columns<TRow> c) { _c = c; _i = -1; }

        public TRow Current => _c.row(_i);

        public bool MoveNext() => ++_i < _c.n;
    }
}

/// <summary>One entity's place in a column object.  Every row struct implements
/// it; the interface is what says "a row knows its columns and its index", and it
/// gives a generic helper something to be constrained by.</summary>
public interface IRow<out TColumns> where TColumns : class
{
    TColumns columns { get; }
    int index { get; }
}
