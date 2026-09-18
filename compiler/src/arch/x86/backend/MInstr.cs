#nullable enable
using Corsac.Lang.Ir;

namespace Corsac.Lang.X86;

using Block = Corsac.Lang.Ir.Block;

public sealed class MInstr
{
    public MOp Op { get; init; }
    public List<MOperand> Operands { get; } = new();

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

    public MReg Reg(int i) => (MReg)Operands[i];
}
