#nullable enable
using Corsac.Lang.Ir;

namespace Corsac.Lang.X86;

using Block = Corsac.Lang.Ir.Block;

/// <summary>
/// A 32-bit register. Virtual registers have Id &gt;= 8; physical ones use
/// their hardware number. A pair for an I64 is two of these, not adjacent.
/// </summary>
public sealed class MReg : MOperand
{
    public int Id { get; }

    public MReg(int id) => Id = id;

    public bool IsPhys => Id < 8;
    public Gpr Phys => (Gpr)Id;

    // ONE OF EACH MACHINE REGISTER, shared: nothing changes a register
    // operand once made (an allocated one is a new operand, not this one
    // renumbered), and every implicit use of EAX or EDX a call or a divide
    // has made one of these, two million a large unit, for the collector.
    private static readonly MReg[] Physical = { new(0), new(1), new(2), new(3), new(4), new(5), new(6), new(7) };

    public static MReg Of(Gpr r) => (uint)r < 8u ? Physical[(int)r] : new((int)r);

    /// <summary>Register `id`: a machine register's shared operand, a virtual one's made.</summary>
    public static MReg Of(int id) => (uint)id < 8u ? Physical[id] : new(id);

    public override string ToString() => IsPhys ? Phys.ToString().ToLowerInvariant() : $"v{Id}";
}
