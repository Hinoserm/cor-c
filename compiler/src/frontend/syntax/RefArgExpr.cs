#nullable enable
namespace Corsac.Lang;

/// An argument passed BY REFERENCE: `f(out x)`, `f(ref x)`, `f(out int x)`.
///
/// A node of its own rather than a flag on the call, because what it produces
/// is not the same KIND of thing an ordinary argument produces. Every other
/// argument evaluates to a value; this one evaluates to the ADDRESS of somewhere
/// a value can be put. Making it an expression means the whole argument
/// machinery -- spilling across a suspension, passing on the stack once the
/// registers run out -- goes on working without knowing about it.
public sealed class RefArgExpr : Expr
{
    /// What the address is taken of. For a declaration form this is the
    /// NameExpr the parser synthesised for the new variable.
    public required Expr Target { get; init; }

    /// `out` rather than `ref`. The difference is entirely about definite
    /// assignment -- who has to have written to it before the call -- which is
    /// a check this compiler does not make yet.
    public bool IsOut { get; init; }

    /// The type written in a declaration form, or null for `out var x` and for
    /// an argument that names something already declared.
    public TypeRef? Declare { get; init; }

    /// The name being declared, or null when nothing is.
    public string? Name { get; init; }
}
