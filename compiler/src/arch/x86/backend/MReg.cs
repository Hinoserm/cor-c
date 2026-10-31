#nullable enable
using Corsac.Lang.Ir;

namespace Corsac.Lang.X86;

using Block = Corsac.Lang.Ir.Block;

/// <summary>
/// A register: a general one by its hardware number (0 to 7), an XMM one
/// as XmmBase plus its number (8 to 15, scalar floats where --cpu has SSE:
/// Selector's XMM class), or a virtual one, FirstVirtual and up. A pair for
/// an I64 is two of these, not adjacent.
/// </summary>
public sealed class MReg : MOperand
{
    public const int XmmBase = 8;
    public const int FirstVirtual = 32;

    public int Id { get; }

    public MReg(int id) => Id = id;

    /// <summary>
    /// A general machine register. Before allocation every register a pass
    /// meets is this or virtual -- no selection names an XMM register then --
    /// so `!IsPhys` there means virtual; after it, test IsVirtual.
    /// </summary>
    public bool IsPhys => Id < 8;
    /// <summary>A register the allocator has still to place.</summary>
    public bool IsVirtual => Id >= FirstVirtual;
    /// <summary>An XMM machine register.</summary>
    public bool IsXmm => Id is >= XmmBase and < XmmBase + 8;
    public Gpr Phys => (Gpr)Id;

    /// <summary>An XMM register's hardware number.</summary>
    public int XmmNumber => Id - XmmBase;
    public static MReg Xmm(int n) => Of(XmmBase + n);

    // ONE OF EACH MACHINE REGISTER, shared: nothing changes a register
    // operand once made (an allocated one is a new operand, not this one
    // renumbered), and every implicit use of EAX or EDX a call or a divide
    // has made one of these, two million a large unit, for the collector.
    private static readonly MReg[] Physical =
    {
        new(0), new(1), new(2), new(3), new(4), new(5), new(6), new(7),
        new(8), new(9), new(10), new(11), new(12), new(13), new(14), new(15),
    };

    /// <summary>
    /// EBP HOLDING A VALUE, in a function without a frame pointer
    /// (MFunction.FramePointerFree), from allocation until FramePointerFree.Run
    /// makes it EBP: every pass between takes an operand based on EBP for the
    /// frame, and an id no machine register has for one it knows nothing of.
    /// No virtual register is left by then; the first virtual number, which
    /// no register is given (MFunction.NextVReg starts past it).
    /// </summary>
    public const int EbpDataId = FirstVirtual;
    public static readonly MReg EbpData = new(EbpDataId);

    public static MReg Of(Gpr r) => (uint)r < 8u ? Physical[(int)r] : new((int)r);

    /// <summary>Register `id`: a machine register's shared operand, a virtual one's made.</summary>
    public static MReg Of(int id) => (uint)id < 16u ? Physical[id] : new(id);

    public override string ToString() => IsPhys ? Phys.ToString().ToLowerInvariant() : IsXmm ? $"xmm{XmmNumber}" : $"v{Id}";
}
