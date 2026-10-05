#nullable enable
namespace Corsac.Lang;

public sealed class LambdaExpr : Expr
{
    /// <summary>Set when this lambda stands for a method group: the identity of the method, so every conversion of the same method shares one closure class and compares equal.</summary>
    public string? GroupIdentity { get; set; }

    /// <summary>
    /// Set when this lambda stands for a LOCAL FUNCTION converted to a
    /// delegate of another type: the identity of the local function, so that
    /// two conversions of it over one environment compare equal, as .NET's
    /// delegates of the same method and target do (TypeSymbol.DelegateGroup).
    /// </summary>
    public string? LocalGroup { get; set; }

    public List<Param> Params { get; } = new();
    public Expr? Body { get; init; }
    public Block? BlockBody { get; init; }
    public bool Async { get; init; }

    /// <summary>
    /// `ref int (int[] a) => ref a[0]`: the result C# 10 lets a lambda write
    /// in front of its parameters, and how it is returned -- Mods.RefReturn,
    /// with Mods.RefReadonlyReturn for `ref readonly`. Null and None for the
    /// usual lambda, whose result is what the delegate it converts to says.
    /// </summary>
    public TypeRef? Returns { get; init; }
    public Mods ReturnMods { get; init; }

    /// <summary>
    /// Every parameter's type was written, `(int x, string s) => ...`: the
    /// Params carry them, and with them C# 10 gives the lambda a natural
    /// type. Otherwise the parameters' types are left empty for the delegate
    /// it converts to to fill in.
    /// </summary>
    public bool TypesWritten { get; init; }

    /// <summary>
    /// `[A] (int x) => x`: the lambda's own attributes (C# 10), kept as
    /// written. Nothing here reads them; .NET shows them only to reflection.
    /// </summary>
    public List<AttributeRef> Attributes { get; } = new();
}
