#nullable enable
using System.Text;

namespace Corsac.Lang.Ir;

public static class IrTypes
{
    /// <summary>The type of a reference or pointer on the current target.</summary>
    public static IrType Word => Target.Current.WordSize == 8 ? IrType.I64 : IrType.I32;

    public static bool IsFloat(this IrType t) => t is IrType.F32 or IrType.F64;
    public static bool IsInt(this IrType t) => t is IrType.I32 or IrType.I64;

    public static int Bytes(this IrType t) => t switch
    {
        IrType.Void => 0,
        IrType.I32 or IrType.F32 => 4,
        _ => 8,
    };

    /// <summary>The IR type a source type is carried in.</summary>
    public static IrType Of(Type t)
    {
        // AN ENUM IS ITS UNDERLYING INTEGER, which is int unless the
        // declaration said otherwise: `enum E : long` is carried in sixty-four
        // bits, and carrying it in thirty-two would drop the top half of every
        // member that needed them.
        if (t.Symbol is { Kind: TypeKind.Enum } counted)
        {
            return counted.EnumUnderlying is Prim.I64 or Prim.U64 ? IrType.I64 : IrType.I32;
        }

        if (t.IsPointer || t.IsNullableValue || t.IsReference || t.IsArray || t.Symbol is not null)
        {
            return Word;
        }

        return t.Prim switch
        {
            Prim.Void => IrType.Void,
            Prim.I64 or Prim.U64 => IrType.I64,
            Prim.NInt or Prim.NUInt => Word,
            Prim.F32 => IrType.F32,
            Prim.F64 => IrType.F64,
            Prim.String or Prim.Type or Prim.Any or Prim.NullLiteral => Word,
            _ => IrType.I32,
        };
    }
}
