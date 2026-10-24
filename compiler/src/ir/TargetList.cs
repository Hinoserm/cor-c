#nullable enable
using System.Text;

namespace Corsac.Lang.Ir;

/// <summary>
/// An instruction's targets (Instr.Targets): a view of them as the
/// instruction keeps them (one Block, or an array of exactly them), made where it is asked for and walked by foreach without an
/// enumerator object. Every change goes to the instruction, so a view kept
/// across one sees it, as a kept List did.
/// </summary>
public readonly struct TargetList : IReadOnlyList<Block>
{
    private readonly Instr _instr;

    internal TargetList(Instr instr) => _instr = instr;

    public int Count => _instr.TargetCount;

    // Read only: a view is not a variable, so C# will not write through its
    // indexer; Instr.SetTarget does.
    public Block this[int index]
    {
        get
        {
            if ((uint)index >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(index));
            return _instr.TargetAt(index);
        }
    }

    // EXACTLY ONE MORE: no room to spare, so each Add is a copy. A branch's
    // targets are made whole (Instr.InitialTargets); only a phi's grow one
    // at a time, by a predecessor each.
    public void Add(Block b)
    {
        int count = Count;
        Block[] grown = new Block[count + 1];
        for (int k = 0; k < count; k++) grown[k] = _instr.TargetAt(k);
        grown[^1] = b;
        _instr.SetTargets(grown);
    }

    public void AddRange(IEnumerable<Block> more)
    {
        Block[] adding = more.ToArray();
        if (adding.Length == 0) return;
        int count = Count;
        Block[] joined = new Block[count + adding.Length];
        for (int k = 0; k < count; k++) joined[k] = _instr.TargetAt(k);
        Array.Copy(adding, 0, joined, count, adding.Length);
        _instr.SetTargets(joined);
    }

    public void RemoveAt(int index)
    {
        int count = Count;
        if ((uint)index >= (uint)count) throw new ArgumentOutOfRangeException(nameof(index));
        Block[] shrunk = new Block[count - 1];
        for (int k = 0, at = 0; k < count; k++) if (k != index) shrunk[at++] = _instr.TargetAt(k);
        _instr.SetTargets(shrunk);
    }

    public bool Contains(Block b)
    {
        int count = Count;
        for (int k = 0; k < count; k++)
            if (ReferenceEquals(_instr.TargetAt(k), b)) return true;
        return false;
    }

    public List<Block> ToList() => new List<Block>(_instr.TargetsCopy());

    public Enumerator GetEnumerator() => new(_instr);
    // Behind an interface (LINQ): one object over the instruction, not a copy of its targets as well.
    IEnumerator<Block> IEnumerable<Block>.GetEnumerator() => new Boxed(_instr);
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => new Boxed(_instr);

    public struct Enumerator
    {
        private readonly Instr _instr;
        private int _at;
        internal Enumerator(Instr instr) { _instr = instr; _at = -1; }
        public Block Current => _instr.TargetAt(_at);
        public bool MoveNext() => ++_at < _instr.TargetCount;
    }

    private sealed class Boxed : IEnumerator<Block>
    {
        private readonly Instr _instr;
        private int _at = -1;
        public Boxed(Instr instr) => _instr = instr;
        public Block Current => _instr.TargetAt(_at);
        object System.Collections.IEnumerator.Current => Current;
        public bool MoveNext() => ++_at < _instr.TargetCount;
        public void Reset() => _at = -1;
        public void Dispose() { }
    }
}
