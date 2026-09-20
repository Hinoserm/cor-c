#nullable enable
namespace Corsac.Lang;

/// <summary>
/// A bound type.
///
/// NULLABILITY IS PART OF THE TYPE AND ALWAYS EXPLICIT. A reference type is
/// non-nullable unless written with '?', and the checker refuses to assign null
/// into one or to dereference a nullable without a check. There is no
/// suppression operator and no "unknown" state: a language that lets you shrug
/// at null has the same bugs as one without the feature, plus the annotations.
/// </summary>
public sealed class Type : IEquatable<Type>
{
    public Prim Prim { get; init; }
    /// <summary>Set for classes, interfaces, structs and enums.</summary>
    public TypeSymbol? Symbol { get; init; }
    public bool Nullable { get; init; }
    /// <summary>Element type when this is an array.</summary>
    public Type? Element { get; init; }
    public int ArrayRank { get; init; }
    /// <summary>Type arguments of a constructed generic.</summary>
    public IReadOnlyList<Type> Args { get; init; } = Array.Empty<Type>();
    public IReadOnlyList<Type>? UseArgs { get; init; }
    /// <summary>Set when this is a type parameter rather than a concrete type.</summary>
    public string? ParamName { get; init; }

    /// <summary>
    /// What each element of a TUPLE type was called, and null for every other
    /// type.
    ///
    /// DELIBERATELY NOT PART OF EQUALITY. `(int a, int b)` and `(int x, int y)`
    /// are one type in C# and assigning one to the other is not a conversion --
    /// the names are there so `f.At` can mean something at the place it is
    /// written, and they travel with the type only so far as that.
    /// </summary>
    public IReadOnlyList<string>? Names { get; init; }

    /// <summary>
    /// How many stars: <c>byte*</c> is one, <c>byte**</c> is two.
    ///
    /// A POINTER IS AN ADDRESS AND NOTHING ELSE -- no length, no header, no
    /// check. That is the whole point of it and the whole danger, which is why
    /// C# will only let one be used inside `unsafe`.
    ///
    /// It exists here because this machine's library has to touch raw memory to
    /// be written at all, and until now it did that through Sys.Peek and
    /// Sys.Poke -- a spelling of our own for something C# already has a way of
    /// doing. Pointers are that way.
    /// </summary>
    public int PointerDepth { get; init; }

    /// <summary>What this points at, one star fewer.</summary>
    public Type? Pointee { get; init; }

    public static readonly Type Void   = new() { Prim = Prim.Void };
    public static readonly Type Bool   = new() { Prim = Prim.Bool };
    public static readonly Type I8     = new() { Prim = Prim.I8 };
    public static readonly Type I16    = new() { Prim = Prim.I16 };
    public static readonly Type I32    = new() { Prim = Prim.I32 };
    public static readonly Type I64    = new() { Prim = Prim.I64 };
    public static readonly Type U8     = new() { Prim = Prim.U8 };
    public static readonly Type U16    = new() { Prim = Prim.U16 };
    public static readonly Type U32    = new() { Prim = Prim.U32 };
    public static readonly Type U64    = new() { Prim = Prim.U64 };
    public static readonly Type NInt   = new() { Prim = Prim.NInt };
    public static readonly Type NUInt  = new() { Prim = Prim.NUInt };
    public static readonly Type F32    = new() { Prim = Prim.F32 };
    public static readonly Type F64    = new() { Prim = Prim.F64 };
    public static readonly Type Char   = new() { Prim = Prim.Char };
    public static readonly Type String = new() { Prim = Prim.String };
    public static readonly Type Null   = new() { Prim = Prim.NullLiteral, Nullable = true };
    public static readonly Type Any    = new() { Prim = Prim.Any };
    public static readonly Type Error  = new() { Prim = Prim.Error };

    /// <summary>A type, as a value. See Prim.Type.</summary>
    public static readonly Type TypeHandle = new() { Prim = Prim.Type };

    public bool IsArray => ArrayRank > 0;
    public bool IsError => Prim == Prim.Error;
    public bool IsPointer => PointerDepth > 0;

    /// <summary>
    /// A pointer to this type.
    ///
    /// The primitive is kept so that `byte*` still knows it points at bytes and
    /// a load through it is one byte wide rather than a word. Getting that
    /// wrong reads seven neighbours along with the one that was asked for.
    /// </summary>
    public Type PointerTo() => new()
    {
        Prim = Prim, Symbol = Symbol, Element = Element, ArrayRank = ArrayRank,
        Args = Args, ParamName = ParamName,
        PointerDepth = PointerDepth + 1, Pointee = this,
    };

    /// <summary>
    /// Nothing at all, as opposed to an object.
    ///
    /// A class, interface, struct or enum is carried as Prim.Void WITH a symbol,
    /// because the primitive is not what describes it. So the primitive alone
    /// does not answer "does this method return anything" -- and code that asked
    /// it that way said no for every method that returned an object, which made
    /// returning one impossible to write. Nothing hit it while the only things
    /// with methods were the compiler's own prelude and a heap that hands back
    /// addresses.
    /// </summary>
    /// <summary>
    /// Whether this is <c>void</c> -- nothing at all, not merely something
    /// unknown.
    ///
    /// A TYPE PARAMETER IS NOT VOID. `T` is carried as Prim.Void with a name in
    /// ParamName, because until it is substituted there is no primitive to put
    /// there -- so leaving ParamName out of this made every generic method that
    /// returns its own T report "this method returns void, so 'return' cannot
    /// have a value".
    /// </summary>
    public bool IsVoid => Prim == Prim.Void && Symbol is null && !IsArray && ParamName is null;
    public bool IsNumeric => IsInteger || IsFloat;

    public bool IsInteger => Prim is Prim.I8 or Prim.I16 or Prim.I32 or Prim.I64 or Prim.Char
                                  or Prim.U8 or Prim.U16 or Prim.U32 or Prim.U64
                                  or Prim.NInt or Prim.NUInt;

    /// <summary>Whether this is nint or nuint: an integer exactly one machine word wide.</summary>
    public bool IsNative => Prim is Prim.NInt or Prim.NUInt;

    /// <summary>
    /// Whether this loads zero-extended, divides unsigned and shifts right
    /// logically. char is unsigned in C# too, and always was here.
    /// </summary>
    public bool IsUnsigned => Prim is Prim.U8 or Prim.U16 or Prim.U32 or Prim.U64 or Prim.Char or Prim.NUInt;
    public bool IsFloat => Prim is Prim.F32 or Prim.F64;

    /// <summary>
    /// Whether a value of this type is a pointer to something on the heap.
    ///
    /// Note that this is NOT the same question as "can it be null" any more --
    /// see <see cref="IsNullableValue"/>, which is null-capable without being
    /// a reference in the source.
    /// </summary>
    public bool IsReference => Prim is Prim.String or Prim.NullLiteral || Symbol is { Kind: TypeKind.Class or TypeKind.Interface } || IsArray;

    /// <summary>
    /// Whether this is <c>Nullable&lt;T&gt;</c> -- `int?`, `BinOp?` and the rest.
    ///
    /// A value type carrying "or nothing" on top of its ordinary range, which
    /// it cannot do in its own bits: `long?` needs every one of its 64 for the
    /// value. So it is HELD AS A CELL ON THE HEAP, and null is the null
    /// pointer. That costs an allocation and buys a representation that works
    /// for every value type without a reserved value in any of them.
    ///
    /// Nothing observes the sharing, because Nullable&lt;T&gt; has no mutable
    /// member -- there is no way to reach through one and change it -- so two
    /// variables holding the same cell behave exactly like two copies.
    ///
    /// The one place the cell CANNOT be taken for a plain reference is equality:
    /// two separately made cells holding 5 are equal and are not the same
    /// pointer, so `==` on these is lifted rather than compared as addresses.
    /// See Codegen.EmitNullableEquality.
    /// </summary>
    /// A MACHINE WORD IS NOT ONE, even though it is not a reference either.
    ///
    /// `object?` and a type parameter's `T?` hold null in the word itself --
    /// zero is not an address -- so there is nothing for a cell to add. Boxing
    /// them was silently wrong rather than merely wasteful: List&lt;T&gt;'s
    /// FirstOrDefault is compiled once over the canonical word, so its `T?`
    /// return was boxed on the way out and read as an object on the way in,
    /// and the caller got a cell where it expected the element.
    public bool IsNullableValue
        => Nullable && !IsReference && !IsError
        && Prim != Prim.NullLiteral && Prim != Prim.Any && ParamName is null;

    /// <summary>This type with the '?' taken off, for the value inside the cell.</summary>
    public Type Underlying => AsNonNullable();

    public Type AsNullable() => Nullable ? this : With(nullable: true);
    public Type AsNonNullable() => !Nullable ? this : With(nullable: false);

    private Type With(bool nullable) => new()
    {
        Prim = Prim, Symbol = Symbol, Nullable = nullable, Element = Element,
        ArrayRank = ArrayRank, Args = Args, ParamName = ParamName,
        Names = Names, PointerDepth = PointerDepth, Pointee = Pointee, UseArgs = UseArgs,
    };

    public Type WithNames(IReadOnlyList<string>? names) => new()
    {
        Prim = Prim, Symbol = Symbol, Nullable = Nullable, Element = Element,
        ArrayRank = ArrayRank, Args = Args, ParamName = ParamName,
        Names = names, PointerDepth = PointerDepth, Pointee = Pointee, UseArgs = UseArgs,
    };

    /// <summary>
    /// An array of something -- and, for more than one pair of brackets, an
    /// array of arrays.
    ///
    /// THE RANK USED TO BE A NUMBER ON ONE TYPE, so `string[][]` was a single
    /// array carrying "two" and an element of string. Nothing downstream read
    /// that number -- code generation has never mentioned it -- so the type
    /// behaved as a flat array of string in every way that mattered: indexing it
    /// once gave a string, and `string[] row = words[0]` was an error in a
    /// program that reads correctly. It also made two spellings of the same type
    /// unequal, which produced the memorable "cannot convert 'string[][]' to
    /// 'string[][]'" -- the two differing in a field the name does not show.
    ///
    /// C# has both shapes and spells them differently: `T[,]` is rectangular and
    /// is one object with two indices, `T[][]` is jagged and is an array whose
    /// elements are arrays. This machine has only ever had the second, and the
    /// parser only ever counted `[]` pairs, so nesting is what the count meant
    /// all along.
    /// </summary>
    public static Type ArrayOf(Type element, int rank = 1)
    {
        if (rank < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(rank), "an array has at least one dimension");
        }

        Type made = new() { Prim = Prim.Void, Element = element, ArrayRank = 1 };

        for (int i = 1; i < rank; i++)
        {
            made = new() { Prim = Prim.Void, Element = made, ArrayRank = 1 };
        }
        return made;
    }

    /// <summary>Width in bytes, for object layout and for choosing an operand size.</summary>
    // A POINTER IS A WORD whatever it points at, so the pointee's width must not
    // decide these. Asking a byte* how wide it is has to answer eight, or every
    // pointer would be stored one byte at a time and lose its top fifty-six bits.
    // A Nullable<T> value is represented by a pointer to its cell, not by T's
    // own width. Treating int? as four bytes truncated the cell address on a
    // load; it only appeared to work while image layout happened to leave the
    // allocation below 4 GiB.
    public int Size => Symbol is { Kind: TypeKind.Enum } counted
        ? Target.Current.SizeOf(counted.EnumUnderlying)
        : IsPointer || IsNullableValue || IsReference || IsArray || Symbol is not null
        ? Target.Current.WordSize
        : Target.Current.SizeOf(Prim);

    public Corsac.Size Operand => IsPointer || IsNullableValue ? Corsac.Size.D : Prim switch
    {
        Prim.Bool or Prim.I8 or Prim.U8 => Corsac.Size.B,
        Prim.I16 or Prim.Char or Prim.U16 => Corsac.Size.H,
        Prim.I32 or Prim.F32 or Prim.U32 => Corsac.Size.W,
        Prim.NInt or Prim.NUInt => Target.Current.WordSize == 4 ? Corsac.Size.W : Corsac.Size.D,
        _ => Corsac.Size.D,
    };

    public bool Equals(Type? other)
    {
        if (other is null)
        {
            return false;
        }

        return Prim == other.Prim
            && ReferenceEquals(Symbol, other.Symbol)
            && Nullable == other.Nullable
            && ArrayRank == other.ArrayRank
            && PointerDepth == other.PointerDepth
            && ParamName == other.ParamName
            && Equals(Element, other.Element)
            && Args.Count == other.Args.Count
            && !Args.Where((a, i) => !a.Equals(other.Args[i])).Any();
    }

    public override bool Equals(object? o) => Equals(o as Type);
    public override int GetHashCode() => HashCode.Combine((int)Prim, Symbol, Nullable, ArrayRank, ParamName);

    public override string ToString()
    {
        string s = IsArray
            ? Element + new string('?', 0) + string.Concat(Enumerable.Repeat("[]", ArrayRank))
            : Symbol?.Name ?? ParamName ?? Prim switch
            {
                Prim.Void => "void", Prim.Bool => "bool",
                Prim.I8 => "sbyte", Prim.I16 => "short", Prim.I32 => "int", Prim.I64 => "long",
                Prim.U8 => "byte", Prim.U16 => "ushort", Prim.U32 => "uint", Prim.U64 => "ulong",
                Prim.NInt => "nint", Prim.NUInt => "nuint",
                Prim.F32 => "float", Prim.F64 => "double",
                Prim.Char => "char", Prim.String => "string",
                Prim.NullLiteral => "null", Prim.Any => "object", Prim.Type => "Type", _ => "?",
            };

        if (Args.Count > 0)
        {
            s += "<" + string.Join(", ", Args) + ">";
        }

        s += new string('*', PointerDepth);
        return Nullable ? s + "?" : s;
    }
}
