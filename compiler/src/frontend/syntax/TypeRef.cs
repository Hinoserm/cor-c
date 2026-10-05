#nullable enable
namespace Corsac.Lang;

// ---- types ------------------------------------------------------------

/// <summary>
/// A type as WRITTEN, not as resolved. Binding happens later; the parser's job
/// is to record exactly what the author typed so a diagnostic can quote it.
/// </summary>
public sealed class TypeRef : Node
{
    /// <summary>
    /// What a tuple type is called before the checker writes the class for it.
    ///
    /// C# has a library type of this name and this compiler does not: a tuple
    /// becomes a class the checker synthesises, one per shape, exactly as it
    /// does for an array being used as a sequence. The SPELLING in source is
    /// C#'s -- `(int At, string Label)` -- and that is the part that matters.
    /// </summary>
    public const string Tuple = "ValueTuple";

    /// <summary>
    /// Stands for THE SUBJECT'S OWN TYPE, made non-null.
    ///
    /// `x is { } y` tests that x is not null and names it. There is no type
    /// written, and the parser cannot invent one -- only the checker knows what
    /// x is. So the parser writes this and the binder resolves it against the
    /// operand, which is the one place that knows.
    /// </summary>
    public const string Same = "__same";

    /// <summary>
    /// Stands for THE SUBJECT'S OWN TYPE, exactly as it is.
    ///
    /// The `var` of a var pattern: `p is { Index: var i }` asks nothing at all
    /// and names what it found. It differs from <see cref="Same"/> in the one
    /// way C# says it does -- a var pattern matches null too, so the name keeps
    /// the nullability the subject had.
    /// </summary>
    public const string Anything = "__anything";

    public required string Name { get; set; }

    /// <summary>
    /// The name a function pointer, `delegate* [unmanaged]<A, B, R>`, is
    /// written under: its types are the Args, the parameters and then the
    /// result, so every pass that copies a type reference carries them as it
    /// carries a generic's arguments.
    /// </summary>
    public const string UnmanagedFunction = "__fnptr$unmanaged";
    public const string ManagedFunction = "__fnptr$managed";

    public bool IsFunctionPointer => Name is UnmanagedFunction or ManagedFunction;
    // A GENERIC'S ARGUMENTS, made only for a type that has some: a list for
    // every type reference -- the arguments of `int`, `string`, every name --
    // was three and a half million empty lists in a self-hosted compile.
    private static readonly List<TypeRef> NoArguments = new();
    private List<TypeRef>? _arguments;

    /// <summary>The arguments, to read: one shared empty list for a type with none, never written through.</summary>
    public List<TypeRef> Args => _arguments ?? NoArguments;

    /// <summary>The arguments, to write: made on first use; given the shared empty list, it keeps none.</summary>
    public List<TypeRef> Arguments
    {
        get => _arguments ??= new();
        // An empty list given is none: every copy of a type was made from
        // its original's arguments (`Args.ToList()`, a Select of them), and
        // half a million empty lists sat in a compile's live heap.
        init => _arguments = value is null || value.Count == 0 ? null : value;
    }
    // Use-site arguments retained after Args is folded into a specialization
    // name. These annotations do not request another runtime specialization.
    public List<TypeRef>? UseArgs { get; init; }
    /// <summary>Array rank, 0 when not an array.</summary>
    public int ArrayRank { get; init; }
    public bool Nullable { get; init; }

    /// <summary>
    /// Whether the ELEMENT is nullable, when this is an array.
    ///
    /// `string?[]` and `string[]?` are different types -- an array of things
    /// that may be null, and a reference to an array that may itself be null --
    /// and one flag could only say one of them. Written source never needed the
    /// distinction because the spelling puts the '?' where it belongs;
    /// SUBSTITUTION does, because `T[]` with T bound to `string?` is an array of
    /// nullable strings, and the monomorphiser had nowhere to record that so it
    /// put the '?' on the array instead. List&lt;string?&gt; then declared its
    /// backing store as a nullable array, and every use of it in std.cor was
    /// reported as a possible null dereference -- 29 errors in a library file
    /// nobody had edited, blocking every compiler source that holds strings in
    /// a list.
    ///
    /// The BOUND type has always modelled this correctly: Type carries a nested
    /// Element with its own Nullable. Only the syntactic side was flat.
    /// </summary>
    public bool ElementNullable { get; init; }

    /// <summary>
    /// The '?' marks between the brackets of an array of arrays: bit k-1 set
    /// means the type after k pairs of brackets is nullable, for k strictly
    /// inside the rank. `byte[]?[]?` has rank 2, Nullable, and bit 0 here:
    /// an array that may be null, of arrays that may be null. The outermost
    /// mark is Nullable and the innermost ElementNullable, as before.
    /// </summary>
    public int InnerNullable { get; init; }

    /// <summary>How many stars follow the name: <c>byte*</c> is one.</summary>
    public int PointerDepth { get; init; }

    /// <summary>
    /// What each element of a TUPLE type was called, or null for every other
    /// type.
    ///
    /// `(int At, string Label)` is a type whose elements have names, and the
    /// names are part of it: `f.At` has to mean the first one. Parallel to
    /// Args, with an empty string where an element was not named.
    /// </summary>
    public List<string>? TupleNames { get; set; }

    /// <summary>
    /// WHICH SHARED TYPE ARGUMENT THIS MACHINE WORD STANDS FOR (Type.CanonParam):
    /// -1 for none; k for a shared class copy's parameter k, written `__canon`
    /// (Monomorphiser.Canonicalise); -2 - k for a shared method copy's type
    /// parameter k, written `object` (Monomorphiser.CopyName). It resolves to
    /// the same object it always did, carrying where it came from, so that a
    /// generic method called with it knows where its caller finds the type
    /// argument at run time. Not part of the spelling: two references
    /// differing only here name one type.
    /// </summary>
    public int CanonIndex { get; set; } = -1;

    public override string ToString()
    {
        string s = Name;

        if (Args.Count > 0)
        {
            s += "<" + string.Join(", ", Args) + ">";
        }
        // AN ARRAY'S `?` IS WHERE C# WRITES IT: `int?[]` holds nullable ints
        // (ElementNullable), `int[]?` may itself be null (Nullable). Written
        // before the brackets either way, a record's synthesised equality --
        // source text built from its parameters' types -- asked for an
        // EqualityComparer of the wrong one, and refused its own argument.
        if (ArrayRank == 0)
        {
            if (Nullable) s += "?";
            return s;
        }
        if (ElementNullable) s += "?";
        for (int i = 0; i < ArrayRank; i++)
        {
            s += "[]";
        }
        if (Nullable) s += "?";
        return s;
    }
}
