#nullable enable
namespace Corsac.Lang;

public enum BinOp : byte
{
    Add, Sub, Mul, Div, Rem,
    And, Or, Xor, Shl, Shr,
    Eq, Ne, Lt, Gt, Le, Ge,
    AndAlso, OrElse,
    Coalesce,
}
