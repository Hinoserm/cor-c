#nullable enable
namespace Corsac.Lang;

public sealed class NewExpr : Expr
{
    public required TypeRef Type { get; init; }
    public List<Expr> Args { get; } = new();
    public List<string?> ArgNames { get; } = new();
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

    /// <summary>The `{ … }` after the constructor, if there was one.</summary>
    public InitBody Body { get; } = new();

    public List<InitAssign> Inits => Body.Inits;
    public List<InitAdd> Adds => Body.Adds;
    public List<InitIndex> Indexes => Body.Indexes;
}
