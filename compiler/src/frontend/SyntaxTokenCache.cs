namespace Corsac.Lang;

/// <summary>Bounded immutable lexical snapshots; every parser gets a private copy-on-write token view.</summary>
public sealed class SyntaxTokenCache
{
    private sealed class Entry
    {
        public readonly Token[] Tokens;
        public readonly long Bytes;
        public long Used;
        public Entry(Token[] tokens, long bytes, long used) { Tokens = tokens; Bytes = bytes; Used = used; }
    }
    private readonly Dictionary<(string Text, string File, string Symbols), Entry> entries = new();
    private readonly long budget;
    private long bytes, clock;
    private readonly object gate = new();
    public long Hits { get; private set; }
    public long Misses { get; private set; }
    public long ResidentBytes { get { lock (gate) return bytes; } }
    public SyntaxTokenCache(long budgetBytes = 8 * 1024 * 1024)
    { if (budgetBytes < 0) throw new ArgumentOutOfRangeException(nameof(budgetBytes)); budget = budgetBytes; }

    public CompilationUnit Parse(string text, string file, IReadOnlyCollection<string>? symbols = null,
        bool declarationsOnly = false, bool includeTemplateBodies = false)
    {
        var key = (text, file, symbols is null || symbols.Count == 0 ? ""
            : string.Join("\n", symbols.Distinct().Order(StringComparer.Ordinal)));
        Token[]? snapshot = null;
        lock (gate)
        {
            if (entries.TryGetValue(key, out Entry? entry))
            {
                Hits++;
                entry.Used = ++clock;
                snapshot = entry.Tokens;
            }
            else
            {
                Misses++;
            }
        }
        List<Token>? tokens = null;
        if (snapshot is null)
        {
            // Lexing and parsing do not hold the cache gate. Future project
            // workers can share immutable snapshots without serializing work.
            tokens = Lexer.Tokenize(text, file, 1, 1, symbols);
            // Conservative accounting includes source/key strings and token text.
            long size = 256L + text.Length * 2L + file.Length * 2L + key.Item3.Length * 2L
                + tokens.Sum(token => 48L + token.Text.Length * 2L);
            lock (gate)
            {
                if (entries.TryGetValue(key, out Entry? raced))
                { raced.Used = ++clock; snapshot = raced.Tokens; }
                else if (size <= budget)
                {
                    while (bytes + size > budget && entries.Count != 0)
                    {
                        var oldest = entries.MinBy(pair => pair.Value.Used);
                        bytes -= oldest.Value.Bytes; entries.Remove(oldest.Key);
                    }
                    snapshot = tokens.ToArray();
                    entries.Add(key, new Entry(snapshot, size, ++clock)); bytes += size;
                }
            }
        }
        // Most parses allocate no token copy. Generic >> speculation gets a
        // private copy lazily; no mutable token list or AST crosses workers.
        return snapshot is null ? new Parser(tokens!, file, declarationsOnly, includeTemplateBodies).ParseUnit()
            : new Parser(snapshot, file, declarationsOnly, includeTemplateBodies).ParseUnit();
    }

    public void Clear() { lock (gate) { entries.Clear(); bytes = 0; } }
}
