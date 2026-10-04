#nullable enable
namespace Corsac.Lang;

public sealed class LambdaExpr : Expr
{
    /// <summary>Set when this lambda stands for a method group: the identity of the method, so every conversion of the same method shares one closure class and compares equal.</summary>
    public string? GroupIdentity { get; set; }

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
}
