#nullable enable
namespace Corsac.Lang;

/// `subject switch { ... }` -- an expression, not a statement.
///
/// The compiler's own source is full of these and almost all of them are type
/// patterns over a node kind, which is the shape this exists to serve.
public sealed class SwitchExpr : Expr
{
    public required Expr Subject { get; init; }
    public List<SwitchArm> Arms { get; } = new();
}
