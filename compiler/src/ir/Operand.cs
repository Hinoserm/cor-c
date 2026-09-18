#nullable enable
using System.Text;

namespace Corsac.Lang.Ir;

/// <summary>An operand: a virtual register, a constant, or the address of a symbol.</summary>
public abstract class Operand
{
    public abstract IrType Type { get; }
}
