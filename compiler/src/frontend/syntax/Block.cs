#nullable enable
namespace Corsac.Lang;

public sealed class Block : Stmt
{
    /// <summary>0 inherits the lexical context, 1 is checked, 2 is unchecked.</summary>
    public byte ArithmeticContext { get; set; }
    public List<Stmt> Statements { get; } = new();
}
