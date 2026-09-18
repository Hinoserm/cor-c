#nullable enable
using Corsac.Lang.Ir;

namespace Corsac.Lang.X86;

using Block = Corsac.Lang.Ir.Block;

/// <summary>
/// A machine basic block. Successors are the targets of the jumps it ends
/// with plus, unless it ends unconditionally, the block laid out after it.
/// </summary>
public sealed class MBlock
{
    public string Name { get; }
    public List<MInstr> Instrs { get; } = new();

    /// <summary>The IR block this heads, if any; null for a block the selector split off.</summary>
    public Block? Source { get; init; }

    /// <summary>Byte offset from the function start, once encoded.</summary>
    public int Offset { get; set; }

    public MBlock(string name) => Name = name;

    public override string ToString() => Name;

    /// <summary>Whether control cannot fall out of the bottom of this block.</summary>
    public bool EndsUnconditionally
    {
        get
        {
            if (Instrs.Count == 0)
            {
                return false;
            }
            MOp last = Instrs[^1].Op;
            return last is MOp.Jmp or MOp.Ret or MOp.JmpTable or MOp.JmpInd or MOp.Epilogue or MOp.Int3;
        }
    }
}
