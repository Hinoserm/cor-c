#nullable enable
namespace Corsac.Lang;

public sealed class Block : Stmt
{
    public List<Stmt> Statements { get; } = new();
}
