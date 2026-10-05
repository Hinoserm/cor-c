namespace Corsac.Lang;

/// <summary>
/// Where declarations and sources are parsed: lexed to an immutable snapshot
/// that the parser reads copy-on-write.
///
/// IT KEEPS NOTHING. It held each text's snapshot for a second asking that
/// never came: hits=0 over a whole self-build and over every declaration
/// pass of one. Kept, every token the lexer made was reachable from a
/// long-lived table, so the collector had to find all of them and region
/// inference called every one global. Hits and Misses still count the
/// parses for the build's report.
/// </summary>
public sealed class SyntaxTokenCache
{
    private long misses;
    public long Hits => 0;
    public long Misses => Interlocked.Read(ref misses);
    public long ResidentBytes => 0;

    public SyntaxTokenCache(long budgetBytes = 0)
    { if (budgetBytes < 0) throw new ArgumentOutOfRangeException(nameof(budgetBytes)); }

    public CompilationUnit Parse(string text, string file, IReadOnlyCollection<string>? symbols = null,
        bool declarationsOnly = false, bool includeTemplateBodies = false)
    {
        Interlocked.Increment(ref misses);
        Token[] snapshot = Lexer.Tokenize(text, file, 1, 1, symbols).ToArray();
        return new Parser(snapshot, file, declarationsOnly, includeTemplateBodies) { Source = text }.ParseUnit();
    }

    public void Clear() { }
}
