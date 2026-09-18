#nullable enable
using System.Text;

namespace Corsac.Lang.Ir;

/// <summary>
/// A virtual register. Unlimited in number; the allocator maps them onto the
/// machine. Not SSA: one may be assigned in several places, and the
/// allocator computes liveness rather than assuming a single definition.
/// </summary>
public sealed class VReg
{
    public int Id { get; }
    public IrType Type { get; }

    /// <summary>The source name, when there is one, for the dump and for gdb later.</summary>
    public string? Name { get; init; }

    internal VReg(int id, IrType type)
    {
        Id = id;
        Type = type;
    }

    public override string ToString() => Name is null ? $"%{Id}" : $"%{Id}.{Name}";
}
