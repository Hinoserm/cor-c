#nullable enable
using Corsac.Lang.Ir;

namespace Corsac.Lang.X86;

using Block = Corsac.Lang.Ir.Block;

/// <summary>
/// A register: a general one by its hardware number (0 to 7 on i386; long
/// mode's sixteen are 0 to 15), an XMM one as XmmBase plus its number (16
/// on, scalar floats where --cpu has SSE: Selector's XMM class), or a
/// virtual one, FirstVirtual and up. A pair for an I64 is two of these, not
/// adjacent. ONE NUMBERING FOR BOTH MODES of the machine: the general
/// registers below sixteen, the vector ones from sixteen, so the passes the
/// two backends share read a register the same way.
/// </summary>
public sealed class MReg : MOperand
{
    public const int XmmBase = 16;
    public const int FirstVirtual = 32;

    public int Id { get; }

    /// <summary>
    /// A floating-point register: a vector machine register, or a virtual
    /// one made as one (long mode's selector says so on the operand; i386's
    /// keeps the set on its function, MFunction.XmmRegs).
    /// </summary>
    public bool IsFloat { get; }

    public MReg(int id) : this(id, false) { }

    public MReg(int id, bool isFloat)
    {
        Id = id;
        IsFloat = isFloat || id is >= XmmBase and < FirstVirtual;
    }

    /// <summary>A machine register of either class, general or vector: not virtual.</summary>
    public bool IsMachine => Id < FirstVirtual;

    /// <summary>The hardware number within its class: what goes in a ModRM or SIB field, its fourth bit in REX.</summary>
    public int Hw => Id & 15;

    /// <summary>
    /// A general machine register of the mode. NOT "not virtual": an XMM
    /// machine register is neither this nor virtual, and long mode's
    /// selector names those before allocation (its float arguments'). A
    /// pass that means a register still to be placed asks IsVirtual; one
    /// that means any machine register, IsMachine.
    /// </summary>
    public bool IsPhys => Id < X86Mode.Gprs;
    /// <summary>A register the allocator has still to place.</summary>
    public bool IsVirtual => Id >= FirstVirtual;
    /// <summary>An XMM machine register.</summary>
    public bool IsXmm => Id is >= XmmBase and < FirstVirtual;
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
        new(16), new(17), new(18), new(19), new(20), new(21), new(22), new(23),
        new(24), new(25), new(26), new(27), new(28), new(29), new(30), new(31),
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

    public static MReg Of(Gpr r) => (uint)r < 16u ? Physical[(int)r] : new((int)r);

    /// <summary>Long mode's name for the same thing: RBP holding a value in a leaf (X64Target).</summary>
    public const int RbpDataId = EbpDataId;
    public static MReg RbpData => EbpData;

    /// <summary>Register `id`: a machine register's shared operand, a virtual one's made.</summary>
    public static MReg Of(int id) => (uint)id < (uint)FirstVirtual ? Physical[id] : new(id);

    public override string ToString() => Id < X86Mode.Gprs ? X86Mode.GprName(Id) : IsXmm ? $"xmm{XmmNumber}" : IsFloat ? $"f{Id}" : $"v{Id}";
}
