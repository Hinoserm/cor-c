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

    public required string Name { get; init; }
    public List<TypeRef> Args { get; init; } = new();
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

    public override string ToString()
    {
        string s = Name;

        if (Args.Count > 0)
        {
            s += "<" + string.Join(", ", Args) + ">";
        }
        if (Nullable)
        {
            s += "?";
        }
        for (int i = 0; i < ArrayRank; i++)
        {
            s += "[]";
        }
        return s;
    }
}
