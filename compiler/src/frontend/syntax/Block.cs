#nullable enable
namespace Corsac.Lang;

public sealed class Block : Stmt
{
    /// <summary>0 inherits the lexical context, 1 is checked, 2 is unchecked.</summary>
    public byte ArithmeticContext { get; set; }
    /// <summary>
    /// The statements, to read. A body skipped by a declarations-only parse
    /// is an empty block, and nearly every method a unit loads from another
    /// is one: they shared one empty list instead of a list each.
    /// </summary>
    public List<Stmt> Statements => _statements ?? NoStatements;
    /// <summary>To write: made on first use.</summary>
    public List<Stmt> WritableStatements => _statements ??= new();
    private List<Stmt>? _statements;
    // Never written through: every writer goes by WritableStatements.
    private static readonly List<Stmt> NoStatements = new();

    /// <summary>
    /// The generic local functions declared in this block, by the name the
    /// block uses and the hidden generic method of the type each became. A
    /// delegate cannot be generic, so C#'s generic local function is that
    /// method, visible under its own name throughout the block.
    /// </summary>
    public List<(string Name, string Method)> GenericLocals => _genericLocals ?? NoGenericLocals;
    /// <summary>To write: made on first use; almost no block has any.</summary>
    public List<(string Name, string Method)> WritableGenericLocals => _genericLocals ??= new();
    private List<(string Name, string Method)>? _genericLocals;
    private static readonly List<(string Name, string Method)> NoGenericLocals = new();

    /// <summary>
    /// The body of an iterator: it holds a `yield`, so the method, lambda or
    /// local function it belongs to returns an enumerator that runs it a step
    /// at a time, as C# runs one -- nothing until the first MoveNext.
    /// </summary>
    public bool Iterator { get; set; }
}
