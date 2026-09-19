namespace Corsac.Lang;

/// <summary>Bounded immutable lexical snapshots; every parser gets its own mutable token list.</summary>
public sealed class SyntaxTokenCache
{
    private sealed record Entry(Token[] Tokens, long Bytes, long Used);
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
        var key = (text, file, string.Join("\n", (symbols ?? Array.Empty<string>()).Distinct().Order(StringComparer.Ordinal)));
        List<Token> tokens;
        lock (gate)
        {
            if (entries.TryGetValue(key, out Entry? entry))
            {
                Hits++;
                entries[key] = entry with { Used = ++clock };
                tokens = new List<Token>(entry.Tokens);
            }
            else
            {
                Misses++;
                tokens = Lexer.Tokenize(text, file, 1, 1, symbols);
                // Conservative accounting includes source/key strings and token text.
                long size = 256L + text.Length * 2L + file.Length * 2L + key.Item3.Length * 2L
                    + tokens.Sum(token => 48L + token.Text.Length * 2L);
                if (size <= budget)
                {
                    while (bytes + size > budget && entries.Count != 0)
                    {
                        var oldest = entries.MinBy(pair => pair.Value.Used);
                        bytes -= oldest.Value.Bytes; entries.Remove(oldest.Key);
                    }
                    entries.Add(key, new Entry(tokens.ToArray(), size, ++clock)); bytes += size;
                }
            }
        }
        // Parser speculation can split >> tokens; never share that mutable list or AST.
        return new Parser(tokens, file, declarationsOnly, includeTemplateBodies).ParseUnit();
    }

    public void Clear() { lock (gate) { entries.Clear(); bytes = 0; } }
}
