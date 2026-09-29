#nullable enable
namespace Corsac.Lang;

public enum BinOp : byte
{
    Add, Sub, Mul, Div, Rem,
    And, Or, Xor, Shl, Shr,
    Eq, Ne, Lt, Gt, Le, Ge,
    AndAlso, OrElse,
    Coalesce,
    /// <summary>`>>>`: the bits move right and zeros come in, whatever the sign.</summary>
    UShr,
}
