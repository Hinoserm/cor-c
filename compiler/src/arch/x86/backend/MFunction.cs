#nullable enable
using Corsac.Lang.Ir;

namespace Corsac.Lang.X86;

using Block = Corsac.Lang.Ir.Block;

/// <summary>A function after selection: blocks, frame, and what the allocator decided.</summary>
public sealed class MFunction
{
    /// <summary>
    /// Machine registers holding half of a 64-bit integer or a pair the
    /// selector made for one. Never references, whatever the interim rule
    /// says about every other word.
    /// </summary>
    public HashSet<int> WideHalves { get; } = new();

    /// <summary>
    /// The live-reference description of each call in this function, keyed
    /// by the instruction the encoder will emit -- which is the instruction
    /// the ALLOCATOR produced, since it rebuilds every one it rewrites.
    /// </summary>
    public Dictionary<MInstr, Safepoint> Safepoints { get; } = new(ReferenceEqualityComparer.Instance);

    public Function Source { get; }
    public List<MBlock> Blocks { get; } = new();
    public Frame Frame { get; }
    public int NextVReg { get; set; } = 8;

    /// <summary>Virtual registers that must land in a register with an 8-bit form (EAX..EBX).</summary>
    public HashSet<int> ByteRegs { get; } = new();

    /// <summary>Callee-saved registers the allocator handed out; the prologue saves exactly these.</summary>
    public List<Gpr> SavedRegs { get; } = new();

    public MFunction(Function source)
    {
        Source = source;
        Frame = new Frame();
    }

    public MReg NewReg() => new(NextVReg++);

    /// <summary>The blocks control can reach from the one at <paramref name="index"/>: jump targets, then the next block unless it never falls through.</summary>
    public IEnumerable<MBlock> Successors(int index)
    {
        MBlock b = Blocks[index];
        foreach (MInstr i in b.Instrs)
        {
            switch (i.Op)
            {
                case MOp.Jmp:
                case MOp.Jcc:
                    yield return ((MLabel)i.Operands[0]).Target;
                    break;
                case MOp.JmpTable:
                    foreach (MBlock t in i.Table!)
                    {
                        yield return t;
                    }
                    break;
            }
        }
        if (!b.EndsUnconditionally && index + 1 < Blocks.Count)
        {
            yield return Blocks[index + 1];
        }
    }

    public MBlock NewBlock(string name, MBlock? after = null, Block? source = null)
    {
        MBlock b = new(name) { Source = source };
        if (after is null)
        {
            Blocks.Add(b);
        }
        else
        {
            Blocks.Insert(Blocks.IndexOf(after) + 1, b);
        }
        return b;
    }
}
