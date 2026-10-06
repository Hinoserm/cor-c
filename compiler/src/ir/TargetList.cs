#nullable enable
using System.Text;

namespace Corsac.Lang.Ir;

/// <summary>
/// An instruction's targets (Instr.Targets): a view of its array of exactly
/// them, made where it is asked for and walked by foreach without an
/// enumerator object. Every change goes to the instruction, so a view kept
/// across one sees it, as a kept List did.
/// </summary>
public readonly struct TargetList : IReadOnlyList<Block>
{
    private readonly Instr _instr;

    internal TargetList(Instr instr) => _instr = instr;

    public int Count => _instr.TargetBlocks?.Length ?? 0;

    // Read only: a view is not a variable, so C# will not write through its
    // indexer; Instr.SetTarget does.
    public Block this[int index]
    {
        get
        {
            if ((uint)index >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(index));
            return _instr.TargetBlocks![index];
        }
    }

    // EXACTLY ONE MORE: no room to spare, so each Add is a copy. A branch's
    // targets are made whole (Instr.InitialTargets); only a phi's grow one
    // at a time, by a predecessor each.
    public void Add(Block b)
    {
        Block[]? had = _instr.TargetBlocks;
        Block[] grown = new Block[(had?.Length ?? 0) + 1];
        if (had is not null) Array.Copy(had, grown, had.Length);
        grown[^1] = b;
        _instr.TargetBlocks = grown;
    }

    public void AddRange(IEnumerable<Block> more)
    {
        Block[] adding = more.ToArray();
        if (adding.Length == 0) return;
        Block[]? had = _instr.TargetBlocks;
        int count = had?.Length ?? 0;
        Block[] joined = new Block[count + adding.Length];
        if (had is not null) Array.Copy(had, joined, count);
        Array.Copy(adding, 0, joined, count, adding.Length);
        _instr.TargetBlocks = joined;
    }

    public void RemoveAt(int index)
    {
        int count = Count;
        if ((uint)index >= (uint)count) throw new ArgumentOutOfRangeException(nameof(index));
        Block[] had = _instr.TargetBlocks!;
        if (count == 1)
        {
            _instr.TargetBlocks = null;
            return;
        }
        Block[] shrunk = new Block[count - 1];
        Array.Copy(had, 0, shrunk, 0, index);
        Array.Copy(had, index + 1, shrunk, index, count - index - 1);
        _instr.TargetBlocks = shrunk;
    }

    public bool Contains(Block b)
    {
        int count = Count;
        for (int k = 0; k < count; k++)
            if (ReferenceEquals(_instr.TargetBlocks![k], b)) return true;
        return false;
    }

    public List<Block> ToList() => _instr.TargetBlocks is { } all ? new List<Block>(all) : new List<Block>();

    public Enumerator GetEnumerator() => new(_instr);
    // Behind an interface (LINQ): one object over the instruction, not a copy of its targets as well.
    IEnumerator<Block> IEnumerable<Block>.GetEnumerator() => new Boxed(_instr);
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => new Boxed(_instr);

    public struct Enumerator
    {
        private readonly Instr _instr;
        private int _at;
        internal Enumerator(Instr instr) { _instr = instr; _at = -1; }
        public Block Current => _instr.TargetBlocks![_at];
        public bool MoveNext() => ++_at < (_instr.TargetBlocks?.Length ?? 0);
    }

    private sealed class Boxed : IEnumerator<Block>
    {
        private readonly Instr _instr;
        private int _at = -1;
        public Boxed(Instr instr) => _instr = instr;
        public Block Current => _instr.TargetBlocks![_at];
        object System.Collections.IEnumerator.Current => Current;
        public bool MoveNext() => ++_at < (_instr.TargetBlocks?.Length ?? 0);
        public void Reset() => _at = -1;
        public void Dispose() { }
    }
}
