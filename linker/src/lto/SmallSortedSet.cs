#nullable enable
using System.Collections;

namespace Corsac.Lang.Lto;

/// <summary>
/// A SET KEPT IN ORDER IN ONE ARRAY, none until its first member: what a
/// lifetime condition's and a field summary's needs are (LifetimeCondition,
/// LifetimeFields), and an owned-field site's names (OwnedFieldHints), a few
/// each and none at all for most -- the conditions bounded at 32 and 64, a
/// unit's name sets as large as the unit (UnionWith fills those in bulk) --
/// and held, the compiler's link, by the hundred thousand. As
/// SortedSets they were a set object and a tree a set, empty or not, and a
/// node of 48 bytes a member: 1.5 million sets and 220 MB of the link's live
/// heap. Inserting moves the members above it, which at these sizes is
/// cheaper than a tree's node; walked in order, as a SortedSet is.
/// </summary>
public sealed class SmallSortedSet<T> : IReadOnlyCollection<T>
{
    private readonly IComparer<T> _order;
    private T[]? _items;
    private int _count;
    // Changed under a walk, the walk throws, as a SortedSet's does.
    private int _version;

    public SmallSortedSet(IComparer<T>? order = null) => _order = order ?? Comparer<T>.Default;

    public int Count => _count;

    /// <summary>Where a value is, or the bitwise complement of where it would go.</summary>
    private int Find(T value) => _items is null ? ~0 : Array.BinarySearch(_items, 0, _count, value, _order);

    public bool Contains(T value) => Find(value) >= 0;

    public bool Add(T value)
    {
        int at = Find(value);
        if (at >= 0) return false;
        at = ~at;
        if (_items is null) _items = new T[4];
        else if (_count == _items.Length) Array.Resize(ref _items, _count * 2);
        if (at < _count) Array.Copy(_items, at, _items, at + 1, _count - at);
        _items[at] = value;
        _count++;
        _version++;
        return true;
    }

    public bool Remove(T value)
    {
        int at = Find(value);
        if (at < 0) return false;
        _count--;
        if (at < _count) Array.Copy(_items!, at + 1, _items!, at, _count - at);
        _items![_count] = default!;
        _version++;
        return true;
    }

    public void Clear()
    {
        _items = null;
        _count = 0;
        _version++;
    }

    /// <summary>
    /// Every member of <paramref name="other"/> added: a few one at a time,
    /// many sorted once and merged in, so a large set filled in no order
    /// costs n log n, not an insert's move per member.
    /// </summary>
    public void UnionWith(IEnumerable<T> other)
    {
        T[] given = other is ICollection<T> sized ? new T[sized.Count] : other.ToArray();
        if (other is ICollection<T> collection) collection.CopyTo(given, 0);
        if (given.Length <= 8)
        {
            foreach (T value in given) Add(value);
            return;
        }
        Array.Sort(given, _order);
        T[] merged = new T[_count + given.Length];
        int mine = 0, theirs = 0, made = 0;
        while (mine < _count || theirs < given.Length)
        {
            int order = mine == _count ? 1 : theirs == given.Length ? -1 : _order.Compare(_items![mine], given[theirs]);
            T next = order <= 0 ? _items![mine] : given[theirs];
            if (order <= 0) mine++;
            if (order >= 0) theirs++;
            if (made == 0 || _order.Compare(merged[made - 1], next) != 0) merged[made++] = next;
        }
        if (made == _count) return;
        Array.Resize(ref merged, made);
        _items = merged;
        _count = made;
        _version++;
    }

    /// <summary>Only what <paramref name="other"/> holds kept, in order.</summary>
    public void IntersectWith(IEnumerable<T> other)
    {
        if (_count == 0) return;
        SmallSortedSet<T> keep = new(_order);
        foreach (T value in other) if (Contains(value)) keep.Add(value);
        _items = keep._items;
        _count = keep._count;
        _version++;
    }

    public bool SetEquals(IEnumerable<T> other)
    {
        if (other is SmallSortedSet<T> same)
        {
            if (same._count != _count) return false;
            for (int i = 0; i < _count; i++) if (_order.Compare(_items![i], same._items![i]) != 0) return false;
            return true;
        }
        SmallSortedSet<T> seen = new(_order);
        foreach (T value in other)
        {
            if (!Contains(value)) return false;
            seen.Add(value);
        }
        return seen._count == _count;
    }

    /// <summary>The greatest member; the set must not be empty.</summary>
    public T Max => _count > 0 ? _items![_count - 1] : throw new InvalidOperationException("an empty set has no greatest member");

    public Enumerator GetEnumerator() => new(this);
    IEnumerator<T> IEnumerable<T>.GetEnumerator() => GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>The members in order, made nothing to walk.</summary>
    public struct Enumerator : IEnumerator<T>
    {
        private readonly SmallSortedSet<T> _set;
        private readonly int _version;
        private int _at;

        internal Enumerator(SmallSortedSet<T> set) { _set = set; _version = set._version; _at = -1; }

        public readonly T Current => _set._items![_at];
        readonly object? IEnumerator.Current => Current;
        public bool MoveNext()
        {
            if (_version != _set._version) throw new InvalidOperationException("the set was changed while it was walked");
            return ++_at < _set._count;
        }
        public void Reset() => _at = -1;
        public readonly void Dispose() { }
    }
}
