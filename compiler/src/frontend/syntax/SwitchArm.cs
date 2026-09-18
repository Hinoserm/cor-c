#nullable enable
namespace Corsac.Lang;

/// One arm of a switch EXPRESSION: a pattern, an optional guard, and a value.
///
/// Deliberately not the same node as SwitchCase. A case holds STATEMENTS and
/// falls out through break; an arm holds one expression and is the value of the
/// whole switch. Sharing a node would mean every consumer asking which kind it
/// was really looking at.
public sealed class SwitchArm : Node
{
    /// <summary>
    /// A constant to compare against, or null for a type pattern or for `_`.
    ///
    /// Settable because a bare name is ambiguous until something knows what
    /// names mean: `RegZero => "zero"` reads as a type pattern and IS a
    /// constant pattern, and only the checker can tell. See Binder.
    /// </summary>
    public Expr? Value { get; set; }

    /// The type in `Foo f => ...`, or null.
    public TypeRef? Type { get; set; }

    /// The name it binds, or null.
    public string? Binding { get; init; }

    /// `when` guard, evaluated only if the pattern matched.
    public Expr? When { get; init; }

    /// True for `_`, which matches anything and binds nothing.
    public bool Discard { get; init; }

    public required Expr Result { get; init; }
}
