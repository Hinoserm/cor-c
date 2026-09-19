using Corsac.Lang;

namespace Corsac.Tests.Metadata;

public static class SyntaxTokenCacheTests
{
    public static void Run()
    {
        SyntaxTokenCache cache = new(128 * 1024);
        const string nested = "class Box<T> {} class User { Box<Box<int>> value; int F(int x) => x >> 2; }";
        CompilationUnit first = cache.Parse(nested, "nested.cor");
        first.Types.Clear();
        CompilationUnit second = cache.Parse(nested, "nested.cor");
        Require(second.Types.Count == 2 && cache.Hits == 1 && cache.Misses == 1, "parser token splitting or AST mutation contaminated cache");
        const string conditional = "#if ENABLED\nclass Enabled {}\n#else\nclass Disabled {}\n#endif\n";
        Require(cache.Parse(conditional, "conditional.cor", new[] { "ENABLED" }).Types[0].Name == "Enabled", "enabled preprocessor branch");
        Require(cache.Parse(conditional, "conditional.cor").Types[0].Name == "Disabled", "symbols were omitted from cache identity");
        Require(cache.Parse("class Changed {}", "nested.cor").Types[0].Name == "Changed", "changed source reused stale tokens");
        const string punctuation = "<<= >>= ??= => -> == != <= >= && || ++ -- += -= *= /= %= &= |= ^= << >> ?? ?. ..";
        Tok[] expected = { Tok.ShlEq, Tok.ShrEq, Tok.QuestionQuestionEq, Tok.FatArrow, Tok.Arrow,
            Tok.Eq, Tok.NotEq, Tok.LtEq, Tok.GtEq, Tok.AndAnd, Tok.OrOr, Tok.PlusPlus, Tok.MinusMinus,
            Tok.PlusEq, Tok.MinusEq, Tok.StarEq, Tok.SlashEq, Tok.PercentEq, Tok.AmpEq, Tok.PipeEq,
            Tok.CaretEq, Tok.Shl, Tok.Shr, Tok.QuestionQuestion, Tok.QuestionDot, Tok.DotDot, Tok.End };
        Require(Lexer.Tokenize(punctuation).Select(token => token.Kind).SequenceEqual(expected), "punctuation longest-match regression");
        SyntaxTokenCache bounded = new(1024);
        for (int i = 0; i < 100; i++) bounded.Parse("class Small" + i + " {}", "bounded.cor");
        Require(bounded.ResidentBytes <= 1024, "cache budget exceeded");
        SyntaxTokenCache disabled = new(0);
        disabled.Parse(nested, "nested.cor"); disabled.Parse(nested, "nested.cor");
        Require(disabled.ResidentBytes == 0 && disabled.Hits == 0, "zero budget retained tokens");
        cache.Clear(); Require(cache.ResidentBytes == 0, "session disposal retained source tokens");
        Console.WriteLine("syntax token cache: mutation isolation, generics, symbols, source changes, punctuation and bounded eviction passed");
    }

    private static void Require(bool value, string message)
    { if (!value) throw new Exception(message); }
}
