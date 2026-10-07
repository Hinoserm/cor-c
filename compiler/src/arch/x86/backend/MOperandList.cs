#nullable enable
namespace Corsac.Lang.X86;

/// <summary>
/// A machine instruction's operands, held in the instruction itself (MInstr
/// derives from this): three in fields, the rest -- rare -- in an array of
/// exactly as many. A List and its array an instruction were two objects
/// for every instruction selection made, all left to the collector with the
/// function once it was encoded. A list's interface stays: Count, the
/// indexer, Add, AddRange, Any, All and foreach, which walks it without an
/// enumerator object.
/// </summary>
public abstract class MOperandList : IReadOnlyList<MOperand>
{
    private MOperand? _o0, _o1, _o2;
    private MOperand[]? _rest;
    private byte _n;

    public int Count => _n + (_rest?.Length ?? 0);

    public MOperand this[int index]
    {
        get
        {
            if ((uint)index >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(index));
            return index switch { 0 => _o0!, 1 => _o1!, 2 => _o2!, _ => _rest![index - 3] };
        }
        set
        {
            if ((uint)index >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(index));
            switch (index)
            {
                case 0: _o0 = value; break;
                case 1: _o1 = value; break;
                case 2: _o2 = value; break;
                default: _rest![index - 3] = value; break;
            }
        }
    }

    public void Add(MOperand o)
    {
        switch (_n)
        {
            case 0: _o0 = o; _n = 1; return;
            case 1: _o1 = o; _n = 2; return;
            case 2: _o2 = o; _n = 3; return;
        }
        // EXACTLY ONE MORE, as the IR's operands: few instructions have more
        // than three.
        MOperand[] grown = new MOperand[(_rest?.Length ?? 0) + 1];
        if (_rest is not null) Array.Copy(_rest, grown, _rest.Length);
        grown[^1] = o;
        _rest = grown;
    }

    public void AddRange(IEnumerable<MOperand> more)
    {
        if (ReferenceEquals(more, this))
        {
            int count = Count;
            for (int k = 0; k < count; k++) Add(this[k]);
            return;
        }
        foreach (MOperand o in more) Add(o);
    }

    public bool Any(Func<MOperand, bool> predicate)
    {
        int count = Count;
        for (int k = 0; k < count; k++) if (predicate(this[k])) return true;
        return false;
    }

    public bool All(Func<MOperand, bool> predicate)
    {
        int count = Count;
        for (int k = 0; k < count; k++) if (!predicate(this[k])) return false;
        return true;
    }

    public Enumerator GetEnumerator() => new(this);
    IEnumerator<MOperand> IEnumerable<MOperand>.GetEnumerator() => new Boxed(this);
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => new Boxed(this);

    public struct Enumerator
    {
        private readonly MOperandList _list;
        private readonly int _count;
        private int _at;
        internal Enumerator(MOperandList list) { _list = list; _count = list.Count; _at = -1; }
        public MOperand Current => _list[_at];
        public bool MoveNext() => ++_at < _count;
    }

    private sealed class Boxed : IEnumerator<MOperand>
    {
        private readonly MOperandList _list;
        private int _at = -1;
        public Boxed(MOperandList list) => _list = list;
        public MOperand Current => _list[_at];
        object System.Collections.IEnumerator.Current => Current;
        public bool MoveNext() => ++_at < _list.Count;
        public void Reset() => _at = -1;
        public void Dispose() { }
    }
}
