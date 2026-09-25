#nullable enable
namespace Corsac.Lang;

public enum Tok : byte
{
    End,

    // literals and names
    Ident,
    Int,
    Real,
    Str,

    /// An interpolated string, holding its RAW inner text -- braces, escapes
    /// and all. The parser splits it, because splitting means parsing the
    /// expressions between the braces and a lexer has no parser.
    InterpStr,
    Char,

    // keywords
    KwNamespace, KwUsing, KwClass, KwInterface, KwStruct, KwEnum,
    KwPublic, KwPrivate, KwProtected, KwInternal,
    KwStatic, KwAbstract, KwVirtual, KwOverride, KwSealed, KwReadonly, KwConst, KwVolatile,
    KwNew, KwThis, KwBase, KwNull, KwTrue, KwFalse,
    KwIf, KwElse, KwWhile, KwFor, KwForeach, KwIn, KwDo,
    KwSwitch, KwCase, KwDefault, KwBreak, KwContinue, KwReturn,
    KwVar, KwVoid, KwRef, KwOut, KwParams,
    KwAsync, KwAwait,
    KwTry, KwCatch, KwFinally, KwThrow,
    KwIs, KwAs, KwOperator, KwWhere, KwGet, KwSet, KwProperty, KwEvent,

    // punctuation
    LParen, RParen, LBrace, RBrace, LBracket, RBracket,
    Comma, Semi, Dot, Colon, Question, Arrow, FatArrow,

    // operators
    Assign,
    Plus, Minus, Star, Slash, Percent,
    PlusEq, MinusEq, StarEq, SlashEq, PercentEq,
    Amp, Pipe, Caret, Tilde, Bang,
    AmpEq, PipeEq, CaretEq,
    Shl, Shr, ShlEq, ShrEq,
    AndAnd, OrOr,
    Eq, NotEq, Lt, Gt, LtEq, GtEq,
    PlusPlus, MinusMinus,
    QuestionQuestion, QuestionDot, QuestionQuestionEq,

    /// <summary>`..`, which is a RANGE: `s[1..4]`, `s[2..]`, `s[..3]`.</summary>
    DotDot,

    /// <summary>A UTF-8 string literal: `"META"u8`, which is BYTES.</summary>
    Utf8Str,
    KwDelegate,
}

/// <summary>
/// One token, carrying enough position to point a diagnostic at the exact
/// character. Error quality is the first priority for this compiler, and a
/// message that names a line but not a column is half a message.
/// </summary>
/// <param name="Global">The name was written after `global::`: it is looked
/// up from the global namespace, past any local or member of the same name.</param>
public readonly record struct Token(Tok Kind, string Text, int Line, int Col, int Pos, bool Global = false)
{
    public override string ToString() => $"{Kind}('{Text}') at {Line}:{Col}";
}

/// <summary>A source-located diagnostic; errors are also throwable by the parser.</summary>
public sealed class CompileError : Exception
{
    public string File { get; }
    public int Line { get; }
    public int Col { get; }
    public bool Warning { get; }

    public CompileError(string file, int line, int col, string message,
                        bool warning = false) : base(message)
    {
        File = file;
        Line = line;
        Col = col;
        Warning = warning;
    }

    public override string ToString()
        => $"{File}({Line},{Col}): {(Warning ? "warning" : "error")}: {Message}";
}
