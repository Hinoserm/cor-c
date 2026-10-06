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

    /// <summary>
    /// One declaration of a file, parsed where it sits in it (Lexer.TokenizeRange):
    /// positions, lines and columns are the whole file's, and so is Source,
    /// so nothing is cut out of the text. <paramref name="space"/> and
    /// <paramref name="path"/> are where the declaration was written
    /// (Parser.StartNamespace). <paramref name="hoisted"/> says whether the
    /// parse named anything by the file-wide hoisting count (Parser.Hoisted).
    /// </summary>
    public CompilationUnit ParseRange(string text, int from, int to, int line, int col, string file,
        IReadOnlyCollection<string>? symbols, string space, string path, out bool hoisted,
        bool declarationsOnly = false, bool includeTemplateBodies = false)
    {
        Interlocked.Increment(ref misses);
        Token[] snapshot = Lexer.TokenizeRange(text, from, to, file, line, col, symbols).ToArray();
        Parser parser = new(snapshot, file, declarationsOnly, includeTemplateBodies)
        { Source = text, StartNamespace = space, StartTypePath = path };
        CompilationUnit unit = parser.ParseUnit();
        hoisted = parser.Hoisted;
        return unit;
    }

    public void Clear() { }
}
