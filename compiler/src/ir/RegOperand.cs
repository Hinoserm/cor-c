#nullable enable
using System.Text;

namespace Corsac.Lang.Ir;

public sealed class RegOperand : Operand
{
    public VReg Reg { get; }
    public RegOperand(VReg reg) => Reg = reg;
    public override IrType Type => Reg.Type;
    public override string ToString() => Reg.ToString();
}
