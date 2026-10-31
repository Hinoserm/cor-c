#nullable enable
namespace Corsac.Lang;

/// <summary>
/// `asm { ... }`: a block of inline assembly (Corsac.Asm.InlineAsm), one of
/// COR-C#'s few departures from C#. The method's locals, parameters and
/// constants are named in its instructions themselves; what goes in, what
/// comes out and what the block destroys is worked out from them (Binder:
/// BindAsm; Lowering: LowerAsm).
/// </summary>
public sealed class AsmStmt : Stmt
{
    /// <summary>The text between the braces, as written.</summary>
    public required string Body { get; init; }

    /// <summary>The line the opening brace is on, which is the body's line 1.</summary>
    public required int BodyLine { get; init; }

    /// <summary>
    /// Each name the block spells that is not a register, a keyword or a
    /// label of its own (InlineAsm.Scan), once: a local, a parameter or a
    /// constant, which the binder finds. Children of the statement, so
    /// every walk that looks for the locals a body uses -- a closure's, an
    /// iterator's or an async method's hoisting -- sees them.
    /// </summary>
    public List<NameExpr> Names { get; init; } = new();
}
