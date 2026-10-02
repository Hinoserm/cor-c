#nullable enable
namespace Corsac.Lang;

/// <summary>`goto name;`: to a label in this block or one enclosing it (LabeledStmt).</summary>
public sealed class GotoStmt : Stmt
{
    public string Label { get; init; } = "";
}
