#nullable enable
using System.Collections;

namespace Corsac.Lang.Ir;

/// <summary>
/// A section's relocations in offset order, those at one offset in the
/// section's own order (a stable sort, as OrderBy is), for finding the ones
/// within a range by binary search.
///
/// NO COPY WHEN THEY ARE IN ORDER ALREADY, which is how the code generator
/// makes them and how an object reads back: the section's own list is
/// searched. A sorted copy of every section's relocations was a second copy
/// of a whole program's (some 24 bytes each), and each one a contiguous
/// array. Only a section out of order gets a sorted copy, in chunks.
///
/// The view is taken when made: a section whose relocations change after
/// (DuplicateCutter.Splice) needs a new one, as a sorted copy would have.
/// </summary>
public sealed class SortedRelocations
{
    private readonly ChunkedList<Relocation> _sorted;

    public SortedRelocations(Section section)
    {
        ChunkedList<Relocation> relocs = section.Relocs;
        bool ordered = true;
        int previous = int.MinValue;
        foreach (Relocation r in relocs)
        {
            if (r.Offset < previous) { ordered = false; break; }
            previous = r.Offset;
        }
        if (ordered) { _sorted = relocs; return; }
        // Offsets read out once: a Relocation is a struct, and every read of
        // one through the list was a copy of it, for every comparison.
        int[] offsets = new int[relocs.Count];
        int[] order = new int[relocs.Count];
        for (int i = 0; i < order.Length; i++) { order[i] = i; offsets[i] = relocs[i].Offset; }
        Array.Sort(order, (a, b) => offsets[a] != offsets[b] ? offsets[a].CompareTo(offsets[b]) : a.CompareTo(b));
        _sorted = new ChunkedList<Relocation>();
        _sorted.EnsureCapacity(order.Length);
        foreach (int i in order) _sorted.Add(relocs[i]);
    }

    /// <summary>The relocations with offsets in [from, to).</summary>
    public Range Within(long from, long to)
    {
        int low = LowerBound(from), high = LowerBound(to);
        return new Range(_sorted, low, Math.Max(low, high) - low);
    }

    private int LowerBound(long offset)
    {
        int low = 0, high = _sorted.Count;
        while (low < high)
        {
            int middle = low + (high - low) / 2;
            if (_sorted[middle].Offset < offset) low = middle + 1;
            else high = middle;
        }
        return low;
    }

    /// <summary>A run of the sorted relocations, read in place.</summary>
    public readonly struct Range : IEnumerable<Relocation>
    {
        private readonly ChunkedList<Relocation> _list;
        private readonly int _start;
        public int Count { get; }

        internal Range(ChunkedList<Relocation> list, int start, int count) { _list = list; _start = start; Count = count; }

        public Relocation this[int index] => (uint)index < (uint)Count ? _list[_start + index] : throw new ArgumentOutOfRangeException(nameof(index));

        public Enumerator GetEnumerator() => new(this);
        IEnumerator<Relocation> IEnumerable<Relocation>.GetEnumerator() => new Enumerator(this);
        IEnumerator IEnumerable.GetEnumerator() => new Enumerator(this);

        public struct Enumerator : IEnumerator<Relocation>
        {
            private readonly Range _range;
            private int _at;
            internal Enumerator(Range range) { _range = range; _at = -1; }
            public Relocation Current => _range._list[_range._start + _at];
            object IEnumerator.Current => Current;
            public bool MoveNext() => ++_at < _range.Count;
            public void Reset() { _at = -1; }
            public void Dispose() { }
        }
    }
}
