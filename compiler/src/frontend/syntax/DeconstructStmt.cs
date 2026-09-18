#nullable enable
namespace Corsac.Lang;

/// <summary>
/// `(string file, int at) = _origins[i];` -- taking a value apart into several
/// names, written as a statement.
///
/// The same two rules a deconstructing foreach follows: a tuple comes apart by
/// position, and anything else through its Deconstruct.
/// </summary>
public sealed class DeconstructStmt : Stmt
{
    public List<Binding> Names { get; } = new();
    public required Expr Value { get; init; }
}
