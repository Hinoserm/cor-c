#nullable enable
using System.Text;

namespace Corsac.Lang.Ir;

/// <summary>
/// A virtual register as an operand: THE REGISTER ITSELF (VReg derives from
/// this). One per register was already the rule -- none is ever changed after
/// it is made, and no pass compares two by reference where two uses of one
/// register must differ (each goes by the operand's position in its
/// instruction instead) -- and as an object of its own beside the register
/// it was 24 bytes for every register: 20 MB of the class library's unit,
/// live through every late pass and code generation.
/// </summary>
public abstract class RegOperand : Operand
{
    private protected RegOperand() { }

    /// <summary>The register this operand is: itself.</summary>
    public VReg Reg => System.Runtime.CompilerServices.Unsafe.As<VReg>(this);

    /// <summary>The register's operand: the register.</summary>
    public static RegOperand Of(VReg reg) => reg;
}
