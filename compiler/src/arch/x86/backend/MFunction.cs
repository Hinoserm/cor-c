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
    public int NextVReg { get; set; } = MReg.FirstVirtual + 1;

    /// <summary>Whether a card mark calls the object's stub (X86Backend.CardStub).</summary>
    public bool UsesCardStub { get; set; }

    /// <summary>Whether a barrier's slow path calls the object's stub (X86Backend.BarrierStub).</summary>
    public bool UsesBarrierStub { get; set; }

    /// <summary>
    /// Whether this function was selected with the hosted ABI's leave to use
    /// the MMX registers (X86Backend.AutomaticPacked). Recorded here because
    /// the emitted-code audit refuses a packed instruction in a function
    /// selected without it: freestanding code does not own the x87 state the
    /// MMX registers alias, except inside a packed block (MInstr.OwnsState).
    /// </summary>
    public bool AutomaticPacked { get; init; }

    /// <summary>
    /// Whether it may use the SSE registers anywhere: hosted code, whose
    /// operating system enables them. Freestanding, the instructions inside
    /// a state FXSAVE keeps say so themselves (MInstr.OwnsState 2).
    /// </summary>
    public bool SseState { get; init; }

    /// <summary>
    /// Virtual registers of the XMM class: scalar floats where --cpu has SSE
    /// (Selector's _xmm32, _xmm64), allocated among XMM0 to XMM7.
    /// </summary>
    public HashSet<int> XmmRegs { get; } = new();

    /// <summary>
    /// The bytes a spilled XMM value is moved by: 8 (MOVSD) where --cpu has
    /// SSE2, a float's as well as a double's; 4 (MOVSS) where it has SSE
    /// alone, which has floats in the XMM registers and no doubles.
    /// </summary>
    public int XmmSpillWidth { get; set; } = 8;

    /// <summary>Virtual registers that must land in a register with an 8-bit form (EAX..EBX).</summary>
    public HashSet<int> ByteRegs { get; } = new();

    /// <summary>Callee-saved registers the allocator handed out; the prologue saves exactly these.</summary>
    public List<Gpr> SavedRegs { get; } = new();

    /// <summary>
    /// EBP HOLDS A VALUE IN THIS FUNCTION (X86Target, for a leaf): its frame
    /// is addressed off ESP, which nothing moves between its prologue and its
    /// epilogue (FramePointerFree), and the prologue's `mov ebp, esp` is
    /// written 8B EC, the mark a fault reads to find the frame (Encoder).
    /// </summary>
    public bool FramePointerFree { get; set; }

    public MFunction(Function source)
    {
        Source = source;
        Frame = new Frame();
    }

    public MReg NewReg() => new(NextVReg++);

    /// <summary>
    /// Successors(index) into a list the caller keeps (emptied first): the
    /// iterator was an object for every block of every function the allocator
    /// and the peephole walked.
    /// </summary>
    public void SuccessorsInto(int index, List<MBlock> into)
    {
        into.Clear();
        MBlock b = Blocks[index];
        for (int k = 0; k < b.Instrs.Count; k++)
        {
            MInstr i = b.Instrs[k];
            if (i.Op is MOp.Jmp or MOp.Jcc) into.Add(((MLabel)i.Operands[0]).Target);
            else if (i.Op == MOp.JmpTable) into.AddRange(i.Table!);
        }
        if (!b.EndsUnconditionally && index + 1 < Blocks.Count) into.Add(Blocks[index + 1]);
    }

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
