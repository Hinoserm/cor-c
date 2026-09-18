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
    public int Id { get; set; }

    public MReg(int id) => Id = id;

    public bool IsPhys => Id < 8;
    public Gpr Phys => (Gpr)Id;

    public static MReg Of(Gpr r) => new((int)r);

    public override string ToString() => IsPhys ? Phys.ToString().ToLowerInvariant() : $"v{Id}";
}
