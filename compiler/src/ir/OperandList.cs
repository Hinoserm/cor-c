#nullable enable
using System.Text;

namespace Corsac.Lang.Ir;

/// <summary>
/// An instruction's operands, kept in the instruction itself (Instr.Operands
/// is the instruction, seen as this): the first two in fields of their own,
/// any more in an array of exactly the rest.
///
/// WHY NOT A LIST: a large unit holds hundreds of thousands of instructions
/// through its code generation, nearly all of them with two operands or
/// fewer, and a List each -- the list, and a backing array with room to
/// spare -- was more than the instruction itself. Here an instruction of up
/// to two operands carries no object for them at all, and one of more an
/// array with no spare room. A list's interface stays: Count, the indexer,
/// Add, Insert, RemoveAt, foreach without an enumerator object, and LINQ
/// through IReadOnlyList.
/// </summary>
public abstract class OperandList : IReadOnlyList<Operand>
{
    private Operand? _o0;
    private Operand? _o1;
    // Operands from the third on, exactly as many as there are; null for two or fewer.
    private Operand[]? _rest;
    // How many are in _o0 and _o1: 0, 1 or 2 (2 whenever _rest is set).
    private byte _n;

    public int Count => _rest is null ? _n : 2 + _rest.Length;

    public Operand this[int index]
    {
        get
        {
            if ((uint)index >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(index));
            return index == 0 ? _o0! : index == 1 ? _o1! : _rest![index - 2];
        }
        set
        {
            if ((uint)index >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(index));
            if (index == 0) _o0 = value;
            else if (index == 1) _o1 = value;
            else _rest![index - 2] = value;
        }
    }

    public void Add(Operand o)
    {
        if (_n == 0) { _o0 = o; _n = 1; return; }
        if (_n == 1 && _rest is null) { _o1 = o; _n = 2; return; }
        // EXACTLY ONE MORE: the array never has room to spare, so a third
        // operand and every one after it costs a copy. Few instructions
        // have more than two, and those mostly get them in one AddRange.
        Operand[] grown = new Operand[(_rest?.Length ?? 0) + 1];
        if (_rest is not null) Array.Copy(_rest, grown, _rest.Length);
        grown[^1] = o;
        _rest = grown;
    }

    public void AddRange(IEnumerable<Operand> more)
    {
        Operand[] all = ToArray();
        Operand[] adding = more as Operand[] ?? more.ToArray();
        if (adding.Length == 0) return;
        Operand[] joined = new Operand[all.Length + adding.Length];
        Array.Copy(all, joined, all.Length);
        Array.Copy(adding, 0, joined, all.Length, adding.Length);
        SetAll(joined);
    }

    public void Insert(int index, Operand o)
    {
        int count = Count;
        if ((uint)index > (uint)count) throw new ArgumentOutOfRangeException(nameof(index));
        Operand[] all = new Operand[count + 1];
        for (int k = 0, at = 0; k <= count; k++) all[k] = k == index ? o : this[at++];
        SetAll(all);
    }

    public void RemoveAt(int index)
    {
        int count = Count;
        if ((uint)index >= (uint)count) throw new ArgumentOutOfRangeException(nameof(index));
        Operand[] all = new Operand[count - 1];
        for (int k = 0, at = 0; k < count; k++) if (k != index) all[at++] = this[k];
        SetAll(all);
    }

    public void Clear()
    {
        _o0 = null;
        _o1 = null;
        _rest = null;
        _n = 0;
    }

    public int IndexOf(Operand o)
    {
        int count = Count;
        for (int k = 0; k < count; k++)
            if (ReferenceEquals(this[k], o)) return k;
        return -1;
    }

    public bool Contains(Operand o) => IndexOf(o) >= 0;

    public int FindIndex(Predicate<Operand> match)
    {
        int count = Count;
        for (int k = 0; k < count; k++)
            if (match(this[k])) return k;
        return -1;
    }

    public Operand[] ToArray()
    {
        int count = Count;
        if (count == 0) return Array.Empty<Operand>();
        Operand[] copy = new Operand[count];
        copy[0] = _o0!;
        if (count > 1) copy[1] = _o1!;
        if (_rest is not null) Array.Copy(_rest, 0, copy, 2, _rest.Length);
        return copy;
    }

    public List<Operand> ToList() => new(ToArray());

    /// <summary>Replaces every operand with these, in order; the array is copied, never kept.</summary>
    public void SetAll(Operand[] all)
    {
        int count = all.Length;
        _o0 = count > 0 ? all[0] : null;
        _o1 = count > 1 ? all[1] : null;
        if (count > 2)
        {
            _rest = new Operand[count - 2];
            Array.Copy(all, 2, _rest, 0, count - 2);
        }
        else
        {
            _rest = null;
        }
        _n = (byte)Math.Min(count, 2);
    }

    public Enumerator GetEnumerator() => new(this);
    // Behind an interface (LINQ, a list made from them): one object over the
    // instruction, not a copy of its operands as well.
    IEnumerator<Operand> IEnumerable<Operand>.GetEnumerator() => new Boxed(this);
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => new Boxed(this);

    // THE COUNT TAKEN ONCE, AND NO BOUNDS CHECKED TWICE: as List's, the
    // operands are not added to or taken from while they are walked. Read
    // afresh at every step, Count's two ways and the indexer's check made
    // MoveNext and Current too large to inline into the compiler's own large
    // passes, and a call each per operand was the hottest loop of escape
    // analysis.
    public struct Enumerator
    {
        private readonly OperandList _list;
        private readonly int _count;
        private int _at;
        internal Enumerator(OperandList list) { _list = list; _count = list.Count; _at = -1; }
        public Operand Current => _at == 0 ? _list._o0! : _at == 1 ? _list._o1! : _list._rest![_at - 2];
        public bool MoveNext() => ++_at < _count;
    }

    private sealed class Boxed : IEnumerator<Operand>
    {
        private readonly OperandList _list;
        private int _at = -1;
        public Boxed(OperandList list) => _list = list;
        public Operand Current => _list[_at];
        object System.Collections.IEnumerator.Current => Current;
        public bool MoveNext() => ++_at < _list.Count;
        public void Reset() => _at = -1;
        public void Dispose() { }
    }
}
