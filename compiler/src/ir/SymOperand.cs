#nullable enable
using System.Text;

namespace Corsac.Lang.Ir;

/// <summary>
/// The address of a named thing: a function, a static, a string literal, a
/// descriptor. Always word-typed. The backend turns it into a relocation.
/// </summary>
public sealed class SymOperand : Operand
{
    public string Name { get; }
    public long Offset { get; }
    public SymOperand(string name, long offset = 0)
    {
        Name = name;
        Offset = offset;
    }
    public override IrType Type => IrTypes.Word;
    public override string ToString() => Offset == 0 ? $"@{Name}" : $"@{Name}+{Offset}";
}
