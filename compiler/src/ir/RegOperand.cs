#nullable enable
using System.Text;

namespace Corsac.Lang.Ir;

/// <summary>
/// A virtual register as an operand: ONE PER REGISTER, made on its first use
/// and kept on it (VReg.SharedOperand), never one per use. A large unit's every
/// use had an object of its own, three hundred thousand of them live through
/// code generation, and nothing told them apart but their register: none is
/// ever changed after it is made, and no pass compares two by reference
/// where two uses of one register must differ (each goes by the operand's
/// position in its instruction instead).
/// </summary>
public sealed class RegOperand : Operand
{
    public VReg Reg { get; }
    private RegOperand(VReg reg) => Reg = reg;

    /// <summary>The register's one operand.</summary>
    public static RegOperand Of(VReg reg) => reg.SharedOperand ??= new RegOperand(reg);

    public override IrType Type => Reg.Type;
    public override string ToString() => Reg.ToString();
}
