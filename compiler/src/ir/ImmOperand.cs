#nullable enable
using System.Text;

namespace Corsac.Lang.Ir;

/// <summary>An integer constant. Floating-point constants live in data and are loaded.</summary>
public sealed class ImmOperand : Operand
{
    public long Value { get; }
    private readonly IrType _type;
    public ImmOperand(long value, IrType type)
    {
        Value = value;
        _type = type;
    }
    public override IrType Type => _type;
    public override string ToString() => Value.ToString();
}
