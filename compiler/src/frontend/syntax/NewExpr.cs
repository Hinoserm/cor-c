#nullable enable
namespace Corsac.Lang;

public sealed class NewExpr : Expr, ICanonSlot
{
    /// <summary>The descriptor entry a shared copy reads for its type argument (ICanonSlot), or -1.</summary>
    public int CanonSlot { get; set; } = -1;
    /// <summary>The `this` that entry is read through (ICanonSlot).</summary>
    public Expr? CanonSelf { get; set; }

    public required TypeRef Type { get; init; }
    public List<Expr> Args { get; } = new();
    public List<string?> ArgNames { get; } = new();

    /// <summary>As <see cref="CallExpr.Spans"/>: -1, -1 for the receiver a constructor has not, then each argument.</summary>
    public int[]? Spans { get; set; }

    /// <summary>The text <see cref="Spans"/> index.</summary>
    public string? Source { get; set; }
    /// <summary>Parameter slots in source evaluation order after named argument binding.</summary>
    public List<int> ArgumentOrder { get; } = new();
    /// <summary>Set for <c>new int[n]</c>.</summary>
    public Expr? ArraySize { get; init; }

    /// <summary>
    /// The elements of <c>new[] { a, b, c }</c>, and null when there was no
    /// initialiser list.
    ///
    /// An implicitly-typed one leaves Type empty and takes its element from
    /// the first element written, exactly as C# does.
    /// </summary>
    public List<Expr>? Elements { get; set; }

    /// <summary>
    /// The bytes of a <c>"text"u8</c> literal, whose array this is: laid down
    /// as data rather than allocated (Lowering.Utf8Data).
    /// </summary>
    public byte[]? Utf8Bytes { get; init; }

    /// <summary>
    /// A COLLECTION EXPRESSION, <c>[a, b, ..c]</c>: a target-typed collection
    /// whose elements are its Adds (a spread is an Add marked Spread). The
    /// checker makes it an array or a collection initializer of the type that
    /// wants it.
    /// </summary>
    public bool Collection { get; init; }

    /// <summary>The `{ … }` after the constructor, if there was one.</summary>
    public InitBody Body { get; } = new();

    public List<InitAssign> Inits => Body.Inits;
    public List<InitAdd> Adds => Body.Adds;
    public List<InitIndex> Indexes => Body.Indexes;
}
