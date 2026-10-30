#nullable enable
using System.Collections;

namespace Corsac.Lang.Ir;

/// <summary>
/// A list held in chunks of a fixed number of items rather than one array,
/// for the lists a unit's compile grows to megabytes: a section's
/// relocations (a call or an address in every few bytes of .text) and the
/// backend's call sites. The reason is ChunkedBytes's: the collector does not
/// move objects, so a doubling List needs one free run of twice its size,
/// with the old array still held, and a capped heap with plenty free in
/// smaller holes can fail it. Growth here asks for one more chunk at a time.
///
/// The first chunk starts small and doubles up to the chunk size, so the
/// many short lists (most sections have a handful of relocations or none)
/// cost what a List would.
/// </summary>
public sealed class ChunkedList<T> : IReadOnlyList<T>
{
    private const int ChunkShift = 11, ChunkSize = 1 << ChunkShift, ChunkMask = ChunkSize - 1;
    private const int FirstChunk = 4;
    private readonly List<T[]> _chunks = new();
    private int _count, _capacity;

    public int Count => _count;

    // EXACT, where the list is told what it will hold (EnsureCapacity): the
    // last chunk only as long as that, grown whole before anything past it.
    // A link reads every section's relocations so, and a whole last chunk a
    // section was a third of their arrays on the compiler's own link.
    private void Reserve(int needed, bool exact = false)
    {
        if (needed <= _capacity) return;
        if (needed <= ChunkSize)
        {
            int size = _chunks.Count == 0 ? FirstChunk : _chunks[0].Length * 2;
            while (size < needed) size *= 2;
            if (exact) size = needed;
            T[] grown = new T[Math.Min(size, ChunkSize)];
            if (_chunks.Count == 0) _chunks.Add(grown);
            else
            {
                Array.Copy(_chunks[0], grown, _count);
                _chunks[0] = grown;
            }
            _capacity = grown.Length;
            return;
        }
        if (_chunks.Count == 0) _chunks.Add(new T[ChunkSize]);
        else if (_chunks[0].Length < ChunkSize)
        {
            T[] whole = new T[ChunkSize];
            Array.Copy(_chunks[0], whole, _count);
            _chunks[0] = whole;
        }
        // A last chunk made short, made whole before the list goes past it.
        if (_chunks.Count > 1 && _chunks[^1].Length < ChunkSize)
        {
            T[] whole = new T[ChunkSize];
            Array.Copy(_chunks[^1], whole, _chunks[^1].Length);
            _chunks[^1] = whole;
        }
        while ((long)_chunks.Count << ChunkShift < needed)
        {
            int rest = needed - (_chunks.Count << ChunkShift);
            _chunks.Add(new T[exact && rest < ChunkSize ? rest : ChunkSize]);
        }
        _capacity = exact ? needed : _chunks.Count << ChunkShift;
    }

    public T this[int index]
    {
        get
        {
            if ((uint)index >= (uint)_count) throw new ArgumentOutOfRangeException(nameof(index));
            return _chunks[index >> ChunkShift][index & ChunkMask];
        }
        set
        {
            if ((uint)index >= (uint)_count) throw new ArgumentOutOfRangeException(nameof(index));
            _chunks[index >> ChunkShift][index & ChunkMask] = value;
        }
    }

    public void Add(T item)
    {
        if (_count == _capacity) Reserve(checked(_count + 1));
        _chunks[_count >> ChunkShift][_count & ChunkMask] = item;
        _count++;
    }

    public void AddRange(IEnumerable<T> items)
    {
        if (items is IReadOnlyCollection<T> sized) Reserve(checked(_count + sized.Count));
        foreach (T item in items) Add(item);
    }

    /// <summary>Room for `capacity` items made now (List.EnsureCapacity), the last chunk no longer than that; only the first and last chunks are ever copied.</summary>
    public void EnsureCapacity(int capacity) => Reserve(capacity, exact: true);

    /// <summary>Empties the list and lets every chunk go.</summary>
    public void Clear()
    {
        _chunks.Clear();
        _chunks.TrimExcess();
        _count = 0;
        _capacity = 0;
    }

    public int FindIndex(Predicate<T> match)
    {
        for (int i = 0; i < _count; i++)
            if (match(_chunks[i >> ChunkShift][i & ChunkMask])) return i;
        return -1;
    }

    /// <summary>The first item equal to `item` taken out, the rest moved down (List.Remove).</summary>
    public bool Remove(T item)
    {
        EqualityComparer<T> equal = EqualityComparer<T>.Default;
        int found = -1;
        for (int i = 0; i < _count; i++)
            if (equal.Equals(_chunks[i >> ChunkShift][i & ChunkMask], item)) { found = i; break; }
        if (found < 0) return false;
        for (int i = found; i < _count - 1; i++)
            _chunks[i >> ChunkShift][i & ChunkMask] = _chunks[(i + 1) >> ChunkShift][(i + 1) & ChunkMask];
        _count--;
        _chunks[_count >> ChunkShift][_count & ChunkMask] = default!;
        return true;
    }

    /// <summary>The items as one array: a contiguous copy, for callers that sort or search one.</summary>
    public T[] ToArray()
    {
        T[] made = new T[_count];
        for (int from = 0; from < _count; from += ChunkSize)
            Array.Copy(_chunks[from >> ChunkShift], 0, made, from, Math.Min(ChunkSize, _count - from));
        return made;
    }

    /// <summary>A struct, as List's is, so a foreach over the list allocates nothing.</summary>
    public struct Enumerator : IEnumerator<T>
    {
        private readonly ChunkedList<T> _list;
        private int _at;
        internal Enumerator(ChunkedList<T> list) { _list = list; _at = -1; }
        public T Current => _list._chunks[_at >> ChunkShift][_at & ChunkMask];
        object? IEnumerator.Current => Current;
        public bool MoveNext() => ++_at < _list._count;
        public void Reset() { _at = -1; }
        public void Dispose() { }
    }

    public Enumerator GetEnumerator() => new(this);
    IEnumerator<T> IEnumerable<T>.GetEnumerator() => new Enumerator(this);
    IEnumerator IEnumerable.GetEnumerator() => new Enumerator(this);
}
