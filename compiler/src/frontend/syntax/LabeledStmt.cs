#nullable enable
namespace Corsac.Lang;

/// <summary>`name: statement`, a target for `goto name;` (GotoStmt).</summary>
public sealed class LabeledStmt : Stmt
{
    public string Label { get; init; } = "";
    public Stmt Body { get; init; } = null!;
}
