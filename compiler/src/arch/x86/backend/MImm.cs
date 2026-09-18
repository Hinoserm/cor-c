#nullable enable
using Corsac.Lang.Ir;

namespace Corsac.Lang.X86;

using Block = Corsac.Lang.Ir.Block;

/// <summary>
/// A constant: a plain number, or the address of a symbol plus an addend
/// (an absolute relocation), or the address of a block in this function.
/// </summary>
public sealed class MImm : MOperand
{
    public long Value { get; init; }
    public string? Symbol { get; init; }
    public MBlock? Label { get; init; }

    public MImm(long value) => Value = value;

    public static MImm Sym(string symbol, long addend) => new(addend) { Symbol = symbol };
    public static MImm Of(MBlock label) => new(0) { Label = label };

    public bool IsPlain => Symbol is null && Label is null;
    public bool FitsSbyte => IsPlain && Value is >= -128 and <= 127;

    public override string ToString()
    {
        if (Label is not null)
        {
            return Label.Name;
        }
        if (Symbol is not null)
        {
            return Value == 0 ? Symbol : $"{Symbol}+{Value}";
        }
        return Value.ToString();
    }
}
