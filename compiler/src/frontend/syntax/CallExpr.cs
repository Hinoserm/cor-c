#nullable enable
namespace Corsac.Lang;

public sealed class CallExpr : Expr
{
    public required Expr Target { get; init; }
    // Source-level tuple naming survives renaming a generic call to its shared
    // machine-code specialization. Names are not part of that code's identity.
    public List<string>? ResultTupleNames { get; set; }
    public TypeRef? ResultTypeUse { get; set; }
    public Dictionary<int, TypeRef>? ArgumentTypeUses { get; set; }
    public List<Expr> Args { get; } = new();
    // Parameter indices in source evaluation order for named local calls.
    // Retained across generic rewriting after ArgNames has been consumed.
    public List<int> LocalArgumentOrder { get; } = new();

    /// <summary>
    /// The name written before each argument, or null where none was.
    ///
    /// Parallel to <see cref="Args"/> and the same length once anything has
    /// been named -- `With(nullable: true)`. The binder puts the arguments
    /// into parameter order and clears this, so nothing below it ever sees a
    /// call whose arguments are out of order.
    /// </summary>
    public List<string?> ArgNames { get; } = new();

    /// <summary>
    /// Whether the receiver has already been moved into the argument list.
    ///
    /// A member call on a type whose methods are static -- a string's, an
    /// extension method's -- is rewritten so the receiver becomes the first
    /// argument, which makes everything below an ordinary static call. That
    /// rewrite MUTATES the call, and the checker now runs more than once
    /// (generic methods are specialised between rounds), so without a mark on
    /// the call itself the receiver goes in twice and the method is told it
    /// was given one argument too many.
    /// </summary>
    public bool ReceiverAdded { get; set; }

}
