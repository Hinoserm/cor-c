#nullable enable
namespace Corsac.Lang;

/// <summary>
/// THE SYSTEM INTERFACES A NUMBER, A BOOL, A CHAR, AN ENUM AND A STRING
/// IMPLEMENT, as .NET declares them: every integer type, char, float and
/// double are IComparable, IComparable&lt;T&gt;, IEquatable&lt;T&gt; and
/// IFormattable; bool all but IFormattable; an enum IComparable and
/// IFormattable; a string IComparable, IComparable&lt;string&gt; and
/// IEquatable&lt;string&gt;. (IConvertible, ISpanFormattable and ICloneable are
/// .NET's too, and the library declares none of them.)
///
/// One answer for the checker, which lets `IComparable x = 5;` convert, and
/// the lowering, which fills the box's slots for these interfaces and lets
/// `is` and `as` find them (Lowering.Box, TypeTest).
/// </summary>
public static class BoxedFaces
{
    /// <summary>The ones a value of type <paramref name="value"/> implements, of the shapes: none for any other type.</summary>
    public enum Face { None, Comparable, ComparableOf, EquatableOf, Formattable }

    /// <summary>Which of them an interface is: by its plain name, arity and the argument a generic one is of; None for any other.</summary>
    public static Face Of(TypeSymbol face, Type? argument)
    {
        if (face.Kind != TypeKind.Interface || face.Decl is not TypeDecl d
            || d.Namespace is not ("" or "System"))
        {
            return Face.None;
        }
        string plain = Plain(d.Template ?? face.Name);
        int arity = d.Template is not null ? d.TemplateArgs.Count : d.TypeParams.Count;
        return (plain, arity) switch
        {
            ("IComparable", 0) => Face.Comparable,
            ("IFormattable", 0) => Face.Formattable,
            ("IComparable", 1) when argument is not null => Face.ComparableOf,
            ("IEquatable", 1) when argument is not null => Face.EquatableOf,
            _ => Face.None,
        };
    }

    /// <summary>Whether a value of this type (boxed, or a string as it is) implements the interface.</summary>
    public static bool Implements(Type value, TypeSymbol face, Type? argument)
    {
        Face which = Of(face, argument);
        if (which == Face.None) return false;
        Type v = value.AsNonNullable();
        if (v.IsArray || v.IsPointer || v.ParamName is not null) return false;
        bool generic = which is Face.ComparableOf or Face.EquatableOf;
        if (v.Prim == Prim.String)
        {
            return which != Face.Formattable && (!generic || argument!.Prim == Prim.String && !argument.IsArray);
        }
        if (v.Symbol is { Kind: TypeKind.Enum })
        {
            return which is Face.Comparable or Face.Formattable;
        }
        if (v.Symbol is not null || !IsPrimitive(v.Prim))
        {
            return false;
        }
        if (which == Face.Formattable) return v.Prim != Prim.Bool;
        return !generic || argument!.Prim == v.Prim && argument.Symbol is null && !argument.IsArray
            && !argument.IsNullableValue && !argument.IsPointer;
    }

    /// <summary>The value types that have a box of their own and these interfaces with it.</summary>
    public static bool IsPrimitive(Prim p) => p is Prim.Bool or Prim.Char or Prim.I8 or Prim.U8 or Prim.I16 or Prim.U16
        or Prim.I32 or Prim.U32 or Prim.I64 or Prim.U64 or Prim.NInt or Prim.NUInt or Prim.F32 or Prim.F64;

    /// <summary>A name with its namespace, arity and mangling cut off.</summary>
    public static string Plain(string name)
    {
        int cut = name.IndexOfAny(new[] { '`', '$', '<' });
        string bare = cut < 0 ? name : name[..cut];
        int dot = bare.LastIndexOf('.');
        return dot < 0 ? bare : bare[(dot + 1)..];
    }
}
