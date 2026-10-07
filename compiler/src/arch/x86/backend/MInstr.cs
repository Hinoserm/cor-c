#nullable enable
using Corsac.Lang.Ir;

namespace Corsac.Lang.X86;

using Block = Corsac.Lang.Ir.Block;

public sealed class MInstr : MOperandList
{
    public MOp Op { get; init; }
    /// <summary>Its operands, held in the instruction itself (MOperandList).</summary>
    public MOperandList Operands => this;

    /// <summary>Operand width in bytes: 1, 2, 4, or 8 for an x87 qword. Registers are always 32-bit except at width 1 and 2.</summary>
    public int Width { get; init; } = 4;

    /// <summary>
    /// The source line this came from, or 0 where there is none.
    ///
    /// Carried this far so the encoder can write down which byte of the image
    /// each line begins at, which is the whole of what a stack trace needs
    /// beyond the function names: `at Ns.Type.Method(args) in file.cor:line N`
    /// is a lookup of the return address in that table. Settable rather than
    /// init-only because the selector stamps it in one place, on its way out,
    /// rather than at every one of the hundred sites that build an MInstr.
    /// </summary>
    public int Line { get; set; }

    /// <summary>For Jcc and Setcc.</summary>
    public Cond Cond { get; init; }

    /// <summary>
    /// For a call: into C (Ir.NativeCall). Carried through the passes after
    /// selection so a call that is rewritten stays one; the collector needs
    /// nothing of it, since the thread's callee-saved registers were copied
    /// into its block (Tls.Spill) when it counted itself stopped, and C
    /// preserves them for the length of the call.
    /// </summary>
    public bool Native { get; init; }

    /// <summary>Whether a LOCK prefix goes in front (Xadd, Cmpxchg).</summary>
    public bool Lock { get; init; }

    /// <summary>For JmpTable: the table's cases, in index order.</summary>
    public List<MBlock>? Table { get; init; }

    /// <summary>For a Call to a symbol: how the reference is made. Rel32 today; Plt32 in a PIC function later.</summary>
    public RelocKind CallReloc { get; init; } = RelocKind.Rel32;

    public MInstr(MOp op, params MOperand[] operands)
    {
        Op = op;
        Operands.AddRange(operands);
    }

    // ONE, TWO AND THREE OPERANDS WRITTEN OUT: nearly every instruction has
    // one of those counts, and through `params` each was an array made only
    // to be copied into Operands and dropped.
    public MInstr(MOp op)
    {
        Op = op;
    }

    public MInstr(MOp op, MOperand a)
    {
        Op = op;
        Operands.Add(a);
    }

    public MInstr(MOp op, MOperand a, MOperand b)
    {
        Op = op;
        Operands.Add(a);
        Operands.Add(b);
    }

    public MInstr(MOp op, MOperand a, MOperand b, MOperand c)
    {
        Op = op;
        Operands.Add(a);
        Operands.Add(b);
        Operands.Add(c);
    }

    public MReg Reg(int i) => (MReg)Operands[i];
}
