#nullable enable
namespace Corsac.Lang;

public enum Prim : byte
{
    Void,
    Bool,
    I8, I16, I32, I64,

    /// <summary>
    /// byte, ushort, uint, ulong.
    ///
    /// Not a spelling of the signed ones: an unsigned value LOADS differently
    /// (zero-extended, not sign-extended), DIVIDES differently, and SHIFTS
    /// RIGHT differently. The instruction set has Ldu, DivU, ModU and Shr
    /// beside Ld, Div, Mod and Sar precisely because these are different
    /// operations, and a byte that sign-extends reads 200 as -56.
    /// </summary>
    U8, U16, U32, U64,

    /// <summary>
    /// nint and nuint: an integer the width of a MACHINE WORD.
    ///
    /// The runtime and the collector hold every address in a long, which on
    /// a 486 is a register pair for every add and compare of something the
    /// machine does in one register. These are C#'s name for that one
    /// register: 32 bits where the word is, 64 where it is. Not a spelling of
    /// int or long -- their width is the target's, and the conversion rules
    /// are C#'s own: implicit from int, implicit to long, explicit the other
    /// way, and never mixed with the type of the other signedness.
    /// </summary>
    NInt, NUInt,

    F32, F64,
    Char,
    String,
    /// <summary>The type of <c>null</c> before it is assigned to anything.</summary>
    NullLiteral,
    /// <summary>
    /// Accepts any type. Exists for exactly one thing: reinterpreting a value
    /// as the machine word it already is, which is what lets a generic
    /// container hash a key without the language having a root object type.
    /// </summary>
    Any,
    /// <summary>
    /// A TYPE, as a value: what typeof(T) and GetType() produce.
    ///
    /// One machine word, holding the address of a type's descriptor. Comparing
    /// two of them is comparing two addresses, which is exactly right -- there
    /// is one descriptor per type, so two values are equal when and only when
    /// they name the same type.
    ///
    /// A primitive rather than a class in the standard library because the
    /// descriptor is emitted by the code generator, and a class whose layout
    /// had to agree with the code generator by hand is a layout that disagrees
    /// eventually. See docs/image-format.md.
    /// </summary>
    Type,

    /// <summary>A type that failed to bind. Poisons quietly so one mistake yields one message.</summary>
    Error,
}

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
    public int Size => Symbol is { Kind: TypeKind.Enum } ? 4
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

/// <summary>A declared type: class, interface, struct or enum.</summary>
public sealed class TypeSymbol
{
    public required string Name { get; init; }

    /// <summary>
    /// What this type is called when it has to be told apart from every other
    /// type: `Section` at the top level, `ImageFile.Section` for one written
    /// inside ImageFile.
    ///
    /// <see cref="Name"/> stays SIMPLE because that is what C# reflection
    /// answers and what a diagnostic should read like. This is the identity --
    /// the key in the table of types, and the owner part of an emitted method
    /// symbol, so that two nested types sharing a simple name do not compile
    /// down to one set of labels.
    /// </summary>
    /// <remarks>
    /// Unset, it IS the name: everything that is not a nested type -- and that
    /// is nearly everything, including every type the prelude declares and
    /// every tuple, closure and specialisation made up along the way -- has
    /// nothing to qualify with.
    /// </remarks>
    public string Key
    {
        get => _key ?? Name;
        init => _key = value;
    }

    private readonly string? _key;

    public required TypeKind Kind { get; init; }
    public TypeDecl? Decl { get; init; }
    public TypeSymbol? Base { get; set; }
    public List<TypeSymbol> Interfaces { get; } = new();
    public List<FieldSymbol> Fields { get; } = new();
    public List<MethodSymbol> Methods { get; } = new();
    public List<string> TypeParams { get; } = new();
    /// <summary>Enum member values, when this is an enum.</summary>
    public Dictionary<string, long> EnumValues { get; } = new();

    /// <summary>
    /// Whether an enum was written `[Flags]`: a SET OF BITS rather than a
    /// list of alternatives. C# gives it one behaviour and one only, and it
    /// is the one people notice -- `(Read | Run).ToString()` says
    /// "Read, Run" where an ordinary enum would say the number.
    /// </summary>
    public bool IsFlags { get; set; }

    /// <summary>Byte size of an instance, filled in during layout.</summary>
    public int InstanceSize { get; set; }

    /// <summary>
    /// This type's bit in the ancestor mask. A test like <c>x is Foo</c> is
    /// then two loads and an AND: an object's vtable carries the mask of every
    /// type it derives from, so no hierarchy walk happens at run time.
    /// </summary>
    public int TypeBit { get; set; } = -1;

    /// <summary>
    /// How deep this class sits in the single-inheritance chain: a class with
    /// no base is 0, its subclass 1, and so on. An INTERFACE is -1, because
    /// interfaces do not form a chain and are searched rather than indexed.
    ///
    /// This is what replaces <see cref="TypeBit"/>. The difference that matters
    /// is not the number but where it comes FROM: a type bit is handed out by
    /// counting every type in the program, so it changes when an unrelated file
    /// declares a class, and a library and its consumers must agree on the
    /// count. A depth is a property of the type and its own bases, so it is the
    /// same number whoever compiles it and whatever else is compiled with it.
    /// </summary>
    public int Depth { get; set; } = -1;

    /// <summary>
    /// For the class a TUPLE shape became: the element names, when every place
    /// that wrote this shape wrote the same ones.
    ///
    /// Names belong to the TYPE and not to the class -- `(int a, int b)` and
    /// `(int x, int y)` are one class -- and the type is where they are read
    /// from. This is the fallback for when they were lost on the way through a
    /// generic: `List&lt;(int At, string Label)&gt;` hands its element back
    /// through a substituted type parameter, and what comes out the other side
    /// is the shape without the names.
    ///
    /// Set to null the moment a second, DIFFERENT naming of the same shape is
    /// seen, so a guess is never made between two of them.
    /// </summary>
    public IReadOnlyList<string>? TupleNames { get; set; }

    /// <summary>Whether TupleNames has been settled once already.</summary>
    public bool TupleNamesSeen { get; set; }

    /// <summary>
    /// EVERY NAMING OF THIS SHAPE THAT HAS BEEN SEEN, which is what is left to
    /// answer with once two of them disagree.
    ///
    /// A name is taken from these only when every set that has it has it at the
    /// SAME position -- `(int FreeFrom, int Slot)` and `(int vreg, int instr)`
    /// are one class here and `.FreeFrom` can only mean the first element, so
    /// there is nothing to guess between. A name at two different positions is
    /// refused, as it was before there was a list at all.
    ///
    /// This is wider than C#, which keeps the names on the REFERENCE and so
    /// knows exactly which naming a value was written with; a monomorphised
    /// generic hands its element back through one shared copy and the naming
    /// does not survive the trip. What is never done is answer with the wrong
    /// element.
    /// </summary>
    public List<IReadOnlyList<string>> TupleNamings { get; } = new();

    /// <summary>The element this name can only mean, or -1 when it could mean two.</summary>
    public int TupleElement(string name)
    {
        int found = -1;

        foreach (IReadOnlyList<string> naming in TupleNamings)
        {
            for (int i = 0; i < naming.Count; i++)
            {
                if (naming[i] != name)
                {
                    continue;
                }

                if (found >= 0 && found != i)
                {
                    return -1;
                }
                found = i;
            }
        }
        return found;
    }

    public bool DerivesFrom(TypeSymbol other)
    {
        for (TypeSymbol? t = this; t != null; t = t.Base)
        {
            if (ReferenceEquals(t, other))
            {
                return true;
            }

            foreach (TypeSymbol i in t.Interfaces)
            {
                if (ReferenceEquals(i, other) || i.DerivesFrom(other))
                {
                    return true;
                }
            }
        }
        return false;
    }

    public FieldSymbol? FindField(string name)
    {
        for (TypeSymbol? t = this; t != null; t = t.Base)
        {
            FieldSymbol? f = t.Fields.Find(f => f.Name == name);

            if (f != null)
            {
                return f;
            }
        }
        return null;
    }

    public List<MethodSymbol> FindMethods(string name)
    {
        List<MethodSymbol> found = new();

        for (TypeSymbol? t = this; t != null; t = t.Base)
        {
            found.AddRange(t.Methods.Where(m => m.Name == name));
        }
        return found;
    }

    public override string ToString() => Name;
}

public sealed class FieldSymbol
{
    public required string Name { get; init; }
    public required Type Type { get; init; }
    public required TypeSymbol Owner { get; init; }
    public bool Static { get; init; }
    public bool Volatile { get; init; }

    /// <summary>
    /// This field holds the ADDRESS of a captured local's cell, not its value.
    ///
    /// A closure over a captured variable keeps a pointer to the one cell the
    /// enclosing method also uses, so both see each other's writes. Reading the
    /// field therefore takes two loads and writing takes a load and a store.
    /// Only closure fields are ever this.
    /// </summary>
    public bool Boxed { get; set; }

    /// <summary>
    /// Whoever builds this object must set this member.
    ///
    /// Checked at the construction site rather than here, which is the whole
    /// point of it: a field that must be filled in is one the type can stop
    /// checking for itself, and the check moves to the one place that knows
    /// whether it was.
    /// </summary>
    public bool Required { get; init; }

    /// <summary>Byte offset within an instance, or within static storage.</summary>
    public int Offset { get; set; }
}

public sealed class MethodSymbol
{
    public required string Name { get; init; }
    public required Type Returns { get; init; }
    public required TypeSymbol Owner { get; init; }
    public List<ParamSymbol> Params { get; } = new();
    public bool Static { get; init; }
    public bool Virtual { get; init; }
    public bool Override { get; init; }
    public bool Abstract { get; init; }
    public bool Async { get; init; }
    public bool IsCtor { get; init; }
    public MethodDecl? Decl { get; init; }
    public List<string> TypeParams { get; } = new();

    /// <summary>Slot in the owner's vtable, or -1 when dispatch is static.</summary>
    public int VtableSlot { get; set; } = -1;

    /// <summary>Code address once emitted.</summary>
    public int Address { get; set; } = -1;

    public string Signature => $"{Owner.Name}.{Name}({string.Join(", ", Params.Select(p => p.Type))})";
    public override string ToString() => Signature;
}

public sealed class ParamSymbol
{
    public required string Name { get; init; }
    public required Type Type { get; init; }
    public bool ByRef { get; init; }

    /// <summary>
    /// `in`: a ref the callee may not write, and one the CALLER does not name.
    /// C# makes the word optional at the call site precisely because nothing
    /// the caller can see changes, which is why this is kept apart from ByRef
    /// rather than folded into it.
    /// </summary>
    public bool ReadOnly { get; init; }
    public bool IsParams { get; init; }
}
