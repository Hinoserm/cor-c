#nullable enable
using Corsac.Lang.Ir;

namespace Corsac.Lang.Opt;

using Block = Corsac.Lang.Ir.Block;

/// <summary>
/// Every instruction that writes each register, in function order, as one
/// flat array grouped by register number. A dictionary of lists, one list per
/// register, was what the lifetime rules and the inliner built over and over
/// for every function they looked at -- nearly all of it lists of one.
/// </summary>
public sealed class RegisterWrites
{
    private readonly int[] _start;
    private readonly Instr[] _all;

    public RegisterWrites(Function f)
    {
        int registers = f.RegCount;
        _start = new int[registers + 1];
        int total = 0;
        foreach (Block b in f.Blocks)
            foreach (Instr i in b.Instrs)
                if (i.Dest is { } d && d.Id < registers)
                {
                    _start[d.Id + 1]++;
                    total++;
                }
        for (int r = 0; r < registers; r++) _start[r + 1] += _start[r];
        _all = total == 0 ? Array.Empty<Instr>() : new Instr[total];
        // FILLED FROM THE END, each register's run downwards from where the
        // next one's starts, which leaves every entry at its own run's start:
        // the same order with no array of cursors beside the table.
        for (int bi = f.Blocks.Count - 1; bi >= 0; bi--)
        {
            List<Instr> instrs = f.Blocks[bi].Instrs;
            for (int k = instrs.Count - 1; k >= 0; k--)
                if (instrs[k].Dest is { } d && d.Id < registers)
                    _all[--_start[d.Id + 1]] = instrs[k];
        }
        // Each end stepped down to its own run's start, so _start[r + 1] holds
        // where r begins: moved back one place, and the last end put back.
        for (int r = 0; r < registers; r++) _start[r] = _start[r + 1];
        _start[registers] = total;
    }

    /// <summary>The writes of `r`; false, as a missing key was, when there are none or `r` is newer than the index.</summary>
    public bool TryGetValue(VReg r, out WriteList writes)
    {
        if ((uint)r.Id >= (uint)(_start.Length - 1) || _start[r.Id] == _start[r.Id + 1])
        {
            writes = default;
            return false;
        }
        writes = new WriteList(_all, _start[r.Id], _start[r.Id + 1] - _start[r.Id]);
        return true;
    }
}

/// <summary>One register's writes: a window onto RegisterWrites' array.</summary>
public readonly struct WriteList
{
    private readonly Instr[] _all;
    private readonly int _start;
    public int Count { get; }

    public WriteList(Instr[] all, int start, int count)
    {
        _all = all;
        _start = start;
        Count = count;
    }

    public Instr this[int k] => (uint)k < (uint)Count ? _all[_start + k] : throw new ArgumentOutOfRangeException(nameof(k));

    public bool All(Func<Instr, bool> test)
    {
        for (int k = 0; k < Count; k++) if (!test(_all[_start + k])) return false;
        return true;
    }

    public Enumerator GetEnumerator() => new(_all, _start, Count);

    public struct Enumerator
    {
        private readonly Instr[] _all;
        private readonly int _end;
        private int _at;
        public Enumerator(Instr[] all, int start, int count) { _all = all; _at = start - 1; _end = start + count; }
        public Instr Current => _all[_at];
        public bool MoveNext() => ++_at < _end;
    }
}
