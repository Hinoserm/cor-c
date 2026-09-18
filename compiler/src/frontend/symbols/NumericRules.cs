#nullable enable
namespace Corsac.Lang;

/// <summary>
/// The C# binary numeric-promotion table.  Binding and lowering both consume
/// this one definition so operand order cannot silently change signedness.
/// </summary>
public static class NumericRules
{
    public static Type Unary(Type value) => value.Prim switch
    {
        Prim.I8 or Prim.U8 or Prim.I16 or Prim.U16 or Prim.Char => Type.I32,
        _ => value,
    };

    public static bool TryBinary(Type left, Type right, out Type result)
    {
        result = Type.Error;
        if (!left.IsNumeric || !right.IsNumeric) return false;

        if (left.Prim == Prim.F64 || right.Prim == Prim.F64)
        {
            result = Type.F64;
            return true;
        }
        if (left.Prim == Prim.F32 || right.Prim == Prim.F32)
        {
            result = Type.F32;
            return true;
        }

        if (left.Prim == Prim.U64 || right.Prim == Prim.U64)
        {
            Type other = left.Prim == Prim.U64 ? right : left;
            if (other.Prim is Prim.I8 or Prim.I16 or Prim.I32 or Prim.I64 or Prim.NInt)
                return false;
            result = Type.U64;
            return true;
        }
        if (left.Prim == Prim.I64 || right.Prim == Prim.I64)
        {
            Type other = left.Prim == Prim.I64 ? right : left;
            if (other.Prim == Prim.NUInt)
                return false;
            result = Type.I64;
            return true;
        }

        // THE NATIVE-SIZED ROWS, as C# has them: a nuint with anything
        // unsigned no wider than a word is a nuint, and with anything signed
        // it is nothing; a nint with anything signed no wider than a word, or
        // unsigned narrower than one, is a nint, and with a uint it is a long
        // -- the one type that holds both. The two never meet each other.
        if (left.Prim == Prim.NUInt || right.Prim == Prim.NUInt)
        {
            Type other = left.Prim == Prim.NUInt ? right : left;
            if (other.Prim is Prim.NUInt or Prim.U8 or Prim.U16 or Prim.U32 or Prim.Char)
            {
                result = Type.NUInt;
                return true;
            }
            return false;
        }
        if (left.Prim == Prim.NInt || right.Prim == Prim.NInt)
        {
            Type other = left.Prim == Prim.NInt ? right : left;
            if (other.Prim == Prim.U32)
            {
                result = Type.I64;
                return true;
            }
            result = Type.NInt;
            return true;
        }
        if (left.Prim == Prim.U32 || right.Prim == Prim.U32)
        {
            Type other = left.Prim == Prim.U32 ? right : left;
            result = other.Prim is Prim.I8 or Prim.I16 or Prim.I32
                ? Type.I64 : Type.U32;
            return true;
        }

        result = Type.I32;
        return true;
    }
}
