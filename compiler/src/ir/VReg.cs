#nullable enable
using System.Text;

namespace Corsac.Lang.Ir;

/// <summary>
/// A virtual register. Unlimited in number; the allocator maps them onto the
/// machine. Not SSA: one may be assigned in several places, and the
/// allocator computes liveness rather than assuming a single definition.
/// </summary>
public sealed class VReg : RegOperand
{
    public int Id { get; }
    public override IrType Type { get; }

    /// <summary>
    /// A parameter of a number type -- an int, a char, a double, an enum held
    /// in thirty-two bits: what a caller hands it is never an address, whatever
    /// the IR type it shares with one (RegionSummary, Lowering.NeverAddress).
    /// Only a parameter's says anything; a copy of the function copies it.
    /// </summary>
    // Declared beside Type so the two bytes share a word.
    public bool Number { get; set; }

    /// <summary>The source name, when there is one, for the dump and for gdb later.</summary>
    public string? Name { get; init; }


    internal VReg(int id, IrType type)
    {
        Id = id;
        Type = type;
    }

    public override string ToString() => Name is null ? $"%{Id}" : $"%{Id}.{Name}";
}
