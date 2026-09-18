#nullable enable
using Corsac.Lang.Ir;

namespace Corsac.Lang.X86;

using Block = Corsac.Lang.Ir.Block;

/// <summary>
/// A memory reference: [Base + Index*Scale + Disp], where Disp may carry a
/// symbol (absolute address) or a block label (a jump table entry).
/// </summary>
public sealed class MMem : MOperand
{
    /// <summary>Private allocator storage, never an address-taken source-language slot.</summary>
    public bool IsSpill { get; init; }
    public MReg? Base { get; set; }
    public MReg? Index { get; set; }
    public int Scale { get; init; } = 1;
    public int Disp { get; init; }
    public string? Symbol { get; init; }

    /// <summary>
    /// How the symbol in the displacement is relocated. Abs32 in ordinary
    /// code; GotOff in a position-independent function, where the base
    /// register is EBX and the displacement is measured from the GOT.
    /// </summary>
    public RelocKind Reloc { get; init; } = RelocKind.Abs32;

    /// <summary>
    /// A block of the enclosing function, whose address is the
    /// displacement. The encoder resolves it against the function's own
    /// symbol, since a block has no symbol of its own.
    /// </summary>
    public MBlock? Label { get; init; }

    public MMem(MReg? @base, int disp)
    {
        Base = @base;
        Disp = disp;
    }

    public static MMem Frame(int offset) => new(MReg.Of(Gpr.Ebp), offset);
    public static MMem Spill(int offset) => new(MReg.Of(Gpr.Ebp), offset) { IsSpill = true };
    public static MMem Abs(string symbol, int disp) => new(null, disp) { Symbol = symbol };

    /// <summary>The same place reached from the GOT pointer: [EBX + symbol@GOTOFF + disp].</summary>
    public static MMem GotOff(MReg got, string symbol, int disp) => new(got, disp) { Symbol = symbol, Reloc = RelocKind.GotOff };

    public override string ToString()
    {
        List<string> parts = new();
        if (Base is not null)
        {
            parts.Add(Base.ToString());
        }
        if (Index is not null)
        {
            parts.Add(Scale == 1 ? Index.ToString() : $"{Index}*{Scale}");
        }
        if (Symbol is not null)
        {
            parts.Add(Symbol);
        }
        string s = string.Join("+", parts);
        if (Disp != 0 || parts.Count == 0)
        {
            s = parts.Count == 0 ? Disp.ToString() : Disp < 0 ? $"{s}-{-Disp}" : $"{s}+{Disp}";
        }
        return $"[{s}]";
    }
}
