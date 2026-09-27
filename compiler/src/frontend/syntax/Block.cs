#nullable enable
namespace Corsac.Lang;

public sealed class Block : Stmt
{
    /// <summary>0 inherits the lexical context, 1 is checked, 2 is unchecked.</summary>
    public byte ArithmeticContext { get; set; }
    public List<Stmt> Statements { get; } = new();

    /// <summary>
    /// The generic local functions declared in this block, by the name the
    /// block uses and the hidden generic method of the type each became. A
    /// delegate cannot be generic, so C#'s generic local function is that
    /// method, visible under its own name throughout the block.
    /// </summary>
    public List<(string Name, string Method)> GenericLocals { get; } = new();

    /// <summary>
    /// The body of an iterator: it holds a `yield`, so the method, lambda or
    /// local function it belongs to returns an enumerator that runs it a step
    /// at a time, as C# runs one -- nothing until the first MoveNext.
    /// </summary>
    public bool Iterator { get; set; }
}
