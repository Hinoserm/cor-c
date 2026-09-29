#nullable enable
namespace Corsac.Lang;

/// <summary>
/// `yield return value;`, or `yield break;` when <see cref="Value"/> is null:
/// a place where an iterator hands its caller one element, or stops. The
/// method whose body holds one is an iterator (<see cref="Block.Iterator"/>),
/// run a step at a time by the enumerator it returns.
/// </summary>
public sealed class YieldStmt : Stmt
{
    public Expr? Value { get; init; }
}
