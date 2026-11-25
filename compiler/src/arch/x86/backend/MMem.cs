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

    /// <summary>Sixteen-byte aligned (AlignedSlots): MovF moves it with MOVAPS.</summary>
    public bool Aligned { get; init; }

    /// <summary>
    /// A device's register (Instr.Device: Sys.PeekByte, PeekHalf, PeekWord):
    /// read every time the source reads it, never one read for two -- a
    /// status changes by itself, and a register may clear as it is read.
    /// Carried by every rebuild of the operand, as IsSpill is.
    /// </summary>
    public bool Device { get; init; }
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

    /// <summary>Long mode: the address is read from the symbol's GOT slot, [rip + symbol@GOTPCREL].</summary>
    public bool Got { get; init; }

    /// <summary>Long mode: a segment override prefix (0x64 FS, 0x65 GS), or 0.</summary>
    public byte Segment { get; init; }

    /// <summary>Long mode: [rip + symbol + disp] -- there is no absolute form there, every named thing is reached from the instruction.</summary>
    public static MMem Rip(string symbol, int disp) => new(null, disp) { Symbol = symbol };
    public static MMem RipGot(string symbol) => new(null, 0) { Symbol = symbol, Got = true };
    public bool IsRipRelative => Symbol is not null || Label is not null;

    /// <summary>
    /// The same access by other registers: base, displacement, index and
    /// scale as given; what the access is of -- a device's memory, a
    /// segment's -- kept. No symbol, block or spill slot: an address of
    /// registers and a number (AddressFold's Plain).
    /// </summary>
    public MMem Addressed(MReg? @base, int disp, MReg? index = null, int scale = 1)
        => new(@base, disp) { Index = index, Scale = scale, Device = Device, Segment = Segment };

    /// <summary>The same reference through other registers: everything else about it kept.</summary>
    public MMem WithRegisters(MReg? @base, MReg? index) => new(@base, Disp)
    {
        Index = index, Scale = Scale, Symbol = Symbol, Reloc = Reloc, Got = Got, Label = Label, IsSpill = IsSpill, Device = Device,
        Aligned = Aligned, Segment = Segment,
    };

    /// <summary>The same reference, its displacement moved.</summary>
    public MMem WithDisp(int delta) => new(Base, Disp + delta)
    {
        Index = Index, Scale = Scale, Symbol = Symbol, Label = Label, IsSpill = IsSpill, Segment = Segment,
        Aligned = Aligned, Device = Device, Reloc = Reloc, Got = Got,
    };

    public MMem(MReg? @base, int disp)
    {
        Base = @base;
        Disp = disp;
    }

    public static MMem Frame(int offset) => new(MReg.Of(Gpr.Ebp), offset);
    public static MMem Spill(int offset) => new(MReg.Of(Gpr.Ebp), offset) { IsSpill = true };
    public static MMem Abs(string symbol, int disp) => new(null, disp) { Symbol = symbol };

    /// <summary>
    /// A named datum of this image as the mode reaches it with no register:
    /// by its address on i386 (code that is not position-independent), from
    /// the instruction in long mode. One shape, read by each encoder its way.
    /// </summary>
    public static MMem Data(string symbol, int disp) => X86Mode.Long ? Rip(symbol, disp) : Abs(symbol, disp);

    /// <summary>The same place reached from the GOT pointer: [EBX + symbol@GOTOFF + disp].</summary>
    public static MMem GotOff(MReg got, string symbol, int disp) => new(got, disp) { Symbol = symbol, Reloc = RelocKind.GotOff };

    public override string ToString()
    {
        if (X86Mode.Long) return LongString();
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

    /// <summary>As long mode writes it: RIP-relative where it names a symbol or a block, a segment prefix said.</summary>
    private string LongString()
    {
        List<string> parts = new();
        if (Symbol is not null)
        {
            parts.Add("rip");
            parts.Add(Symbol);
        }
        if (Label is not null)
        {
            parts.Add("rip");
            parts.Add(Label.Name);
        }
        if (Base is not null)
        {
            parts.Add(Base.ToString());
        }
        if (Index is not null)
        {
            parts.Add(Scale == 1 ? Index.ToString() : $"{Index}*{Scale}");
        }
        string s = string.Join("+", parts);
        if (Disp != 0 || parts.Count == 0)
        {
            s = parts.Count == 0 ? Disp.ToString() : Disp < 0 ? $"{s}-{-Disp}" : $"{s}+{Disp}";
        }
        string segment = Segment == 0x64 ? "fs:" : Segment == 0x65 ? "gs:" : "";
        return $"{segment}[{s}]";
    }
}
