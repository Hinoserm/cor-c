#nullable enable
using System.Text;

namespace Corsac.Lang;

/// <summary>
/// Source text to tokens.
///
/// Deliberately hand-written rather than generated: diagnostics are the first
/// priority for this compiler, and a hand-written lexer can say "unterminated
/// string starting here" instead of "unexpected character".
/// </summary>
public sealed class Lexer
{
    private static readonly string[] CharacterText = CreateCharacterText();
    private static string[] CreateCharacterText()
    {
        string[] result = new string[128];
        for (int i = 0; i < result.Length; i++) result[i] = ((char)i).ToString();
        return result;
    }
    private static readonly (string text, Tok kind)[] ThreePunctuation =
        {
            ("<<=", Tok.ShlEq), (">>=", Tok.ShrEq),

            // `??=`, which has to be matched before `??` or the `=` would
            // be read as a second, separate assignment.
            ("??=", Tok.QuestionQuestionEq),
        };

    private static readonly (string text, Tok kind)[] TwoPunctuation =
        {
            ("=>", Tok.FatArrow), ("->", Tok.Arrow),
            ("==", Tok.Eq), ("!=", Tok.NotEq), ("<=", Tok.LtEq), (">=", Tok.GtEq),
            ("&&", Tok.AndAnd), ("||", Tok.OrOr),
            ("++", Tok.PlusPlus), ("--", Tok.MinusMinus),
            ("+=", Tok.PlusEq), ("-=", Tok.MinusEq), ("*=", Tok.StarEq),
            ("/=", Tok.SlashEq), ("%=", Tok.PercentEq),
            ("&=", Tok.AmpEq), ("|=", Tok.PipeEq), ("^=", Tok.CaretEq),
            ("<<", Tok.Shl), (">>", Tok.Shr),
            ("??", Tok.QuestionQuestion), ("?.", Tok.QuestionDot),

            // `..`, which is a range and not two member accesses.
            ("..", Tok.DotDot),
        };


    /// <summary>
    /// Whether a name is a keyword, which source has to write as `@name` to
    /// use it as an identifier: a name kept without its `@` is written back
    /// with one (Header).
    /// </summary>
    public static bool IsKeyword(string name) => Keywords.ContainsKey(name);

    /// <summary>A name as source writes it: `@base` for a parameter called base.</summary>
    public static string Identifier(string name) => IsKeyword(name) ? "@" + name : name;

    private static readonly Dictionary<string, Tok> Keywords = new(StringComparer.Ordinal)
    {
        ["namespace"] = Tok.KwNamespace, ["using"] = Tok.KwUsing,
        ["class"] = Tok.KwClass, ["interface"] = Tok.KwInterface,
        ["delegate"] = Tok.KwDelegate,
        ["struct"] = Tok.KwStruct, ["enum"] = Tok.KwEnum,
        ["public"] = Tok.KwPublic, ["private"] = Tok.KwPrivate,
        ["protected"] = Tok.KwProtected, ["internal"] = Tok.KwInternal,
        ["static"] = Tok.KwStatic, ["abstract"] = Tok.KwAbstract,
        ["virtual"] = Tok.KwVirtual, ["override"] = Tok.KwOverride,
        ["sealed"] = Tok.KwSealed, ["readonly"] = Tok.KwReadonly, ["const"] = Tok.KwConst,
        ["volatile"] = Tok.KwVolatile,
        ["new"] = Tok.KwNew, ["this"] = Tok.KwThis, ["base"] = Tok.KwBase,
        ["null"] = Tok.KwNull, ["true"] = Tok.KwTrue, ["false"] = Tok.KwFalse,
        ["if"] = Tok.KwIf, ["else"] = Tok.KwElse, ["while"] = Tok.KwWhile,
        ["for"] = Tok.KwFor, ["foreach"] = Tok.KwForeach, ["in"] = Tok.KwIn, ["do"] = Tok.KwDo,
        ["switch"] = Tok.KwSwitch, ["case"] = Tok.KwCase, ["default"] = Tok.KwDefault,
        ["break"] = Tok.KwBreak, ["continue"] = Tok.KwContinue, ["return"] = Tok.KwReturn,
        ["var"] = Tok.KwVar, ["void"] = Tok.KwVoid, ["ref"] = Tok.KwRef, ["out"] = Tok.KwOut,
        ["params"] = Tok.KwParams,
        ["await"] = Tok.KwAwait,
        ["try"] = Tok.KwTry, ["catch"] = Tok.KwCatch, ["finally"] = Tok.KwFinally,
        ["throw"] = Tok.KwThrow,
        ["is"] = Tok.KwIs, ["as"] = Tok.KwAs, ["operator"] = Tok.KwOperator,
        ["event"] = Tok.KwEvent,

        // 'async' is not here either, for the same reason and with the same
        // evidence: `bool async = Bool();` is a line in this compiler's own
        // Gir.cs, and C# allows it because async is contextual -- a modifier
        // only where a member's modifiers belong.
        //
        // 'where', 'get' and 'set' are NOT here, and that is deliberate: C#
        // makes them contextual, and so does this. They were keywords, and a
        // parameter called `set` -- which the standard library's own HashSet
        // enumerator wants -- then failed to parse as a name. See ParseAccessors
        // and ParseConstraints, which match them by their text where they mean
        // something and let them be ordinary words everywhere else.
    };

    private readonly string _src;
    private readonly string _file;
    private int _pos;
    private int _line = 1;
    private int _col = 1;

    /// <summary>
    /// The conditional-compilation symbols in force: the ones the command line
    /// defined with -D, plus whatever this file's own `#define` adds.
    ///
    /// C# scopes `#define` to the file it is written in -- a symbol is not
    /// carried into the next source -- so this is a copy per lexer and the set
    /// the driver holds is never written to.
    /// </summary>
    private readonly HashSet<string> _symbols;

    /// <summary>One `#if` and the branches under it, innermost last.</summary>
    private readonly List<Conditional> _conditionals = new();

    private sealed class Conditional
    {
        /// <summary>Whether the branch now open is the one being compiled.</summary>
        public bool Emitting;

        /// <summary>Whether a branch of this `#if` has already matched, so no later one can.</summary>
        public bool Taken;

        /// <summary>Whether anything here is compiled at all: the state outside this `#if`.</summary>
        public bool Enclosing;
    }

    /// <summary>Whether the text being read now belongs to the compilation.</summary>
    private bool Emitting => _conditionals.Count == 0 || _conditionals[^1].Emitting;

    /// <summary>
    /// <paramref name="line"/> and <paramref name="col"/> say WHERE THIS TEXT
    /// SITS in the file, for source that is a piece of a larger one.
    ///
    /// The holes of an interpolated string are lexed and parsed on their own --
    /// there is no second grammar for what goes between the braces, which is
    /// what keeps `{a.B(c)[0]}` working -- and without this every node they
    /// produced was stamped line 1, column something. A diagnostic about
    /// `$"{ll.Find("c").Previous.Value}"` then sent the reader to the first
    /// line of the file, which is never where the mistake is.
    /// </summary>
    public Lexer(string source, string file = "<source>", int line = 1, int col = 1,
                 IReadOnlyCollection<string>? symbols = null)
    {
        _src = source ?? throw new ArgumentNullException(nameof(source));
        _file = file;
        _line = line;
        _col = col;
        _symbols = symbols is null
                 ? new HashSet<string>(StringComparer.Ordinal)
                 : new HashSet<string>(symbols, StringComparer.Ordinal);
    }

    public static List<Token> Tokenize(string source, string file = "<source>", int line = 1, int col = 1,
                                       IReadOnlyCollection<string>? symbols = null)
    {
        Lexer lexer = new(source, file, line, col, symbols);
        List<Token> tokens = new(Math.Min(4096, source.Length / 4 + 1));

        while (true)
        {
            Token t = lexer.Next();
            tokens.Add(t);

            if (t.Kind == Tok.End)
            {
                return tokens;
            }
        }
    }

    private char Cur => _pos < _src.Length ? _src[_pos] : '\0';
    private char Peek(int n = 1) => _pos + n < _src.Length ? _src[_pos + n] : '\0';
    private bool Done => _pos >= _src.Length;
    /// <summary>End of the last consumed token, for source-index spelling preservation.</summary>
    public int Position => _pos;

    private CompileError Error(string message, int line, int col) => new(_file, line, col, message);

    private void Advance()
    {
        if (Cur == '\n')
        {
            _line++;
            _col = 1;
        }
        else
        {
            _col++;
        }
        _pos++;
    }

    public Token Next()
    {
        SkipTrivia();

        int line = _line, col = _col, start = _pos;

        if (Done)
        {
            // AN `#if` THAT NOBODY CLOSED. Left unsaid, the rest of the file
            // simply vanished: everything after it was excluded and the token
            // stream ended where the condition began.
            if (_conditionals.Count > 0)
            {
                throw Error("this file ends inside an '#if' that has no '#endif'", line, col);
            }

            return new Token(Tok.End, "", line, col, start);
        }

        char c = Cur;

        if (char.IsLetter(c) || c == '_')
        {
            Token word = Word(line, col, start);

            // `global::` NAMES THE GLOBAL NAMESPACE, which is where a qualified
            // name already begins here: namespaces are not a tree, and a name
            // resolves from its last parts. So the qualifier is read and
            // dropped, and what follows it is the name. Any other alias before
            // `::` is an extern alias, which a program here cannot have.
            if (word.Kind == Tok.Ident && Cur == ':' && Peek() == ':')
            {
                if (word.Text != "global")
                {
                    throw Error($"'{word.Text}::' names an extern alias; only 'global::' is known here", line, col);
                }
                Advance();
                Advance();
                return Next();
            }
            return word;
        }

        if (char.IsDigit(c) || (c == '.' && char.IsDigit(Peek())))
        {
            return Number(line, col, start);
        }

        // $"..." -- taken here rather than in Punct, because what follows the
        // dollar decides whether it is a string at all.
        if (c == '$' && Peek(1) == '"')
        {
            return InterpolatedLiteral(line, col, start);
        }

        // @"..." -- VERBATIM, which is C#'s way of writing a string that is
        // full of backslashes without doubling every one of them. `@"\u needs
        // four digits"` is a message about an escape and must not be read as
        // one; this compiler's own lexer says exactly that.
        //
        // Also `$@"..."` and `@$"..."`, which C# accepts in either order.
        if (c == '@' && Peek(1) == '"')
        {
            return VerbatimLiteral(line, col, start);
        }

        if (c == '@' && Peek(1) == '$' && Peek(2) == '"')
        {
            Advance();
            return InterpolatedLiteral(line, col, start, verbatim: true);
        }

        if (c == '$' && Peek(1) == '@' && Peek(2) == '"')
        {
            // The dollar is what the interpolated path expects to see first,
            // so the at-sign is taken out of the way by reading past it after.
            return InterpolatedLiteral(line, col, start, skipAt: true, verbatim: true);
        }

        // @name -- A VERBATIM IDENTIFIER, which is how a name that is also a
        // keyword is written. `MMem(MReg? @base, int disp)` calls a parameter
        // `base`, in this compiler's own machine IR.
        //
        // The at-sign is NOT part of the name: `@base` and a `base` written
        // anywhere it is not a keyword are the same identifier, which is what
        // C# says and is why the character is dropped here. Nothing above the
        // lexer needs to know the spelling existed.
        if (c == '@' && (char.IsLetter(Peek(1)) || Peek(1) == '_'))
        {
            // `@base`: the name is `base`, and the token STARTS at the `@`,
            // so that anything copying source by token position -- the
            // declaration index's slices -- keeps the `@` that makes the name
            // an identifier rather than the keyword.
            Advance();
            Token named = Word(line, col, _pos, verbatim: true);
            return new Token(named.Kind, named.Text, line, col, start);
        }

        return c switch
        {
            '"'  => StringLiteral(line, col, start),
            '\'' => CharLiteral(line, col, start),
            _    => Punct(line, col, start),
        };
    }

    private void SkipTrivia()
    {
        while (!Done)
        {
            char c = Cur;

            if (c is ' ' or '\t' or '\r' or '\n')
            {
                Advance();
                continue;
            }

            if (c == '/' && Peek() == '/')
            {
                while (!Done && Cur != '\n')
                {
                    Advance();
                }
                continue;
            }

            // ---- a preprocessor directive ---------------------------------
            //
            // Recognised only at the START OF A LINE, which is C#'s rule: a
            // '#' anywhere else is not a directive and this must not eat it.
            //
            // The ones that only affect what the compiler CHECKS are skipped:
            // this language is nullable-aware always, so `#nullable enable` is
            // already true and saying so costs nothing. #region and #pragma
            // say nothing about meaning either.
            //
            // CONDITIONAL COMPILATION IS REFUSED RATHER THAN SKIPPED, and that
            // distinction is the whole reason this is not a two-line skip. An
            // unrecognised `#if FOO` quietly ignored would compile BOTH arms of
            // it -- the code that was meant to be excluded and the code that
            // replaced it -- and the result builds. Refusing says so.
            if (c == '#' && AtLineStart())
            {
                Directive();
                continue;
            }

            // TEXT UNDER A CONDITION THAT DID NOT HOLD is not read at all: it
            // is skipped a LINE at a time, looking only for the directive that
            // ends the branch. That is C#'s rule and not merely an easier one
            // -- the excluded text need not even be a valid program, which is
            // half of what conditional compilation is for.
            if (!Emitting)
            {
                SkipExcluded();
                continue;
            }

            if (c == '/' && Peek() == '*')
            {
                int line = _line, col = _col;
                Advance();
                Advance();

                while (true)
                {
                    if (Done)
                    {
                        throw Error("unterminated block comment", line, col);
                    }
                    if (Cur == '*' && Peek() == '/')
                    {
                        Advance();
                        Advance();
                        break;
                    }
                    Advance();
                }
                continue;
            }
            return;
        }
    }

    /// <summary>
    /// Whether nothing but whitespace precedes this on its line.
    ///
    /// A directive must be the first thing on a line. Without this test a '#'
    /// in the middle of an expression would be taken for one -- and while this
    /// language has no operator spelled '#' today, a lexer that is wrong only
    /// because of what does not exist yet is wrong already.
    /// </summary>
    private bool AtLineStart()
    {
        for (int i = _pos - 1; i >= 0; i--)
        {
            if (_src[i] == '\n')
            {
                return true;
            }

            if (_src[i] is not (' ' or '\t' or '\r'))
            {
                return false;
            }
        }
        return true;                            // the first line of the file
    }

    /// <summary>Reads one preprocessor directive, acting on it or refusing it.</summary>
    private void Directive()
    {
        int line = _line, col = _col;

        Advance();                              // the '#'

        while (!Done && Cur is ' ' or '\t')
        {
            Advance();
        }

        int start = _pos;

        while (!Done && char.IsLetter(Cur))
        {
            Advance();
        }

        string name = _src[start.._pos];

        // The rest of the line is the directive's argument, and every directive
        // ends at the newline -- so the line is consumed either way, and the
        // only question is what consuming it meant.
        int argument = _pos;

        while (!Done && Cur != '\n')
        {
            Advance();
        }

        string rest = Trailing(_src[argument.._pos]);

        switch (name)
        {
            // THE BRANCHING ONES ARE READ WHEREVER THEY ARE, including inside
            // a branch that is not being compiled: an `#if` nested in a skipped
            // one still has to be matched with its `#endif`, or the wrong
            // `#endif` ends the outer one.
            case "if":
                _conditionals.Add(new Conditional
                {
                    Enclosing = Emitting,
                    Emitting = Emitting && Condition(rest, line, col),
                });
                _conditionals[^1].Taken = _conditionals[^1].Emitting;
                return;

            case "elif":
            {
                if (_conditionals.Count == 0)
                {
                    throw Error("'#elif' with no '#if' before it", line, col);
                }

                Conditional open = _conditionals[^1];

                open.Emitting = open.Enclosing && !open.Taken && Condition(rest, line, col);
                open.Taken = open.Taken || open.Emitting;
                return;
            }

            case "else":
            {
                if (_conditionals.Count == 0)
                {
                    throw Error("'#else' with no '#if' before it", line, col);
                }

                Conditional open = _conditionals[^1];

                open.Emitting = open.Enclosing && !open.Taken;
                open.Taken = true;
                return;
            }

            case "endif":
                if (_conditionals.Count == 0)
                {
                    throw Error("'#endif' with no '#if' before it", line, col);
                }

                _conditionals.RemoveAt(_conditionals.Count - 1);
                return;
        }

        // EVERYTHING ELSE IS IGNORED WHERE IT IS NOT BEING COMPILED. C# reads
        // a skipped branch for its conditionals and for nothing else, so a
        // `#error` under a condition that did not hold says nothing, and a
        // directive this compiler does not know is not a complaint either.
        if (!Emitting)
        {
            return;
        }

        switch (name)
        {
            case "define":
                _symbols.Add(Symbol(rest, line, col));
                return;

            case "undef":
                _symbols.Remove(Symbol(rest, line, col));
                return;

            case "error":
                throw Error(rest.Length == 0 ? "#error" : rest, line, col);

            case "warning":
                Console.Error.WriteLine($"{_file}({line},{col}): warning: {rest}");
                return;

            case "nullable":
            case "region":
            case "endregion":
            case "pragma":
            case "line":
                return;                         // nothing about meaning

            default:
                throw Error($"'#{name}' is not a directive this compiler knows", line, col);
        }
    }

    /// <summary>
    /// What a `#if` or `#elif` asks, answered.
    ///
    /// C#'s preprocessor expression is a small language of its own and this is
    /// all of it: a symbol, which is true when it is defined, `true` and
    /// `false`, `!`, `==`, `!=`, `&amp;&amp;`, `||` and brackets. There are no
    /// numbers in it and nothing is compared but truth values, which is why it
    /// fits in thirty lines and needs none of the parser below.
    /// </summary>
    private bool Condition(string text, int line, int col)
    {
        int at = 0;

        bool Value()
        {
            Space();

            if (at < text.Length && text[at] == '!')
            {
                at++;
                return !Value();
            }

            if (at < text.Length && text[at] == '(')
            {
                at++;

                bool inside = Or();

                Space();

                if (at >= text.Length || text[at] != ')')
                {
                    throw Error("a '(' in this condition has no ')'", line, col);
                }
                at++;
                return inside;
            }

            int start = at;

            while (at < text.Length && (char.IsLetterOrDigit(text[at]) || text[at] == '_'))
            {
                at++;
            }

            if (at == start)
            {
                throw Error($"'{text}' is not a condition this compiler understands", line, col);
            }

            string name = text[start..at];

            return name switch
            {
                "true" => true,
                "false" => false,
                _ => _symbols.Contains(name),
            };
        }

        bool Equality()
        {
            bool left = Value();

            while (true)
            {
                Space();

                if (at + 1 < text.Length && text[at] == '=' && text[at + 1] == '=')
                {
                    at += 2;
                    left = left == Value();
                    continue;
                }

                if (at + 1 < text.Length && text[at] == '!' && text[at + 1] == '=')
                {
                    at += 2;
                    left = left != Value();
                    continue;
                }
                return left;
            }
        }

        bool And()
        {
            bool left = Equality();

            while (true)
            {
                Space();

                if (at + 1 < text.Length && text[at] == '&' && text[at + 1] == '&')
                {
                    at += 2;

                    // BOTH SIDES ARE READ WHATEVER THE FIRST SAID: this is not
                    // running code, it is deciding what to compile, and the
                    // text after the operator has to be consumed either way.
                    left = Equality() && left;
                    continue;
                }
                return left;
            }
        }

        bool Or()
        {
            bool left = And();

            while (true)
            {
                Space();

                if (at + 1 < text.Length && text[at] == '|' && text[at + 1] == '|')
                {
                    at += 2;
                    left = And() || left;
                    continue;
                }
                return left;
            }
        }

        void Space()
        {
            while (at < text.Length && (text[at] is ' ' or '\t')) { at++; }
        }

        if (text.Length == 0)
        {
            throw Error("'#if' needs a condition", line, col);
        }

        bool answer = Or();

        Space();

        if (at != text.Length)
        {
            throw Error($"'{text[at..]}' is not part of a condition", line, col);
        }
        return answer;
    }

    /// <summary>A directive's argument with any trailing `//` comment taken off.</summary>
    private static string Trailing(string text)
    {
        int comment = text.IndexOf("//", StringComparison.Ordinal);

        return (comment < 0 ? text : text[..comment]).Trim();
    }

    /// <summary>The one name `#define` and `#undef` take.</summary>
    private string Symbol(string text, int line, int col)
    {
        if (text.Length == 0 || !(char.IsLetter(text[0]) || text[0] == '_')
            || !text.All(c => char.IsLetterOrDigit(c) || c == '_'))
        {
            throw Error($"'{text}' is not a name a conditional symbol can have", line, col);
        }
        return text;
    }

    /// <summary>
    /// Reads past text a condition excluded, stopping at the next directive.
    ///
    /// LINE BY LINE, because that is all C# does with such text: it is never
    /// tokenized, so an unterminated string or a half-written statement inside
    /// it is not an error, and a '#' at the start of a line is a directive even
    /// where the same characters inside a compiled string would not be.
    /// </summary>
    private void SkipExcluded()
    {
        while (!Done)
        {
            while (!Done && Cur is ' ' or '\t' or '\r')
            {
                Advance();
            }

            if (!Done && Cur == '#')
            {
                return;
            }

            while (!Done && Cur != '\n')
            {
                Advance();
            }

            if (!Done)
            {
                Advance();                      // the newline
            }
        }
    }

    private Token Word(int line, int col, int start, bool verbatim = false)
    {
        // '$' CONTINUES A NAME, and never starts one.
        //
        // A specialisation is called `List$__canon`, and that name is not only
        // internal: a library's generated header declares the signatures a
        // consumer compiles against, and `string.Join` takes a `List<T>` --
        // which is a `List$__canon` by the time the header is written. Refusing
        // the character meant a library could export a generic method and no
        // program could then be compiled against it.
        //
        // Never at the START, so `$"..."` is still an interpolated string and
        // not an identifier followed by one.
        while (!Done && (char.IsLetterOrDigit(Cur) || Cur == '_' || Cur == '$'))
        {
            Advance();
        }

        string text = _src[start.._pos];
        return new Token(!verbatim && Keywords.TryGetValue(text, out Tok kw) ? kw : Tok.Ident,
                         text, line, col, start);
    }

    private Token Number(int line, int col, int start)
    {
        // Hex and binary are integer-only; a radix prefix on a real is a
        // mistake worth naming rather than silently half-parsing.
        if (Cur == '0' && (Peek() is 'x' or 'X' or 'b' or 'B'))
        {
            bool hex = Peek() is 'x' or 'X';
            Advance();
            Advance();
            int digits = 0;

            while (!Done && (IsRadixDigit(Cur, hex) || Cur == '_'))
            {
                if (Cur != '_')
                {
                    digits++;
                }
                Advance();
            }

            if (digits == 0)
            {
                throw Error($"'{_src[start.._pos]}' has no digits after its radix prefix", line, col);
            }
            // A hex or binary literal takes a suffix too: `0xFFFFu`. Only
            // 'L' and 'U' -- 'd' and 'f' are HEX DIGITS, and trimming them off
            // the end turned `0xd` into `0x`.
            while (!Done && Cur is 'L' or 'l' or 'U' or 'u')
            {
                Advance();
            }

            // Keep the suffix in the token.  Its letters are not part of the
            // numeric value, but they ARE part of the literal's type: 1L must
            // remain a long even though its value fits in an int.
            return new Token(Tok.Int, _src[start.._pos], line, col, start);
        }

        bool real = false;

        // C# permits the leading zero of a fractional literal to be omitted:
        // `.1f` is the same value as `0.1f`. This must be recognised before a
        // dot is tokenised as member access; Castle's generated source retains
        // the original BASIC spellings and uses this ordinary C# form.
        if (Cur == '.')
        {
            real = true;
            Advance();
        }

        while (!Done && (char.IsDigit(Cur) || Cur == '_'))
        {
            Advance();
        }

        // A dot is only a decimal point when a digit follows: 1.ToString() and
        // ranges must not be swallowed.
        if (Cur == '.' && char.IsDigit(Peek()))
        {
            real = true;
            Advance();

            while (!Done && (char.IsDigit(Cur) || Cur == '_'))
            {
                Advance();
            }
        }

        if (Cur is 'e' or 'E')
        {
            int save = _pos, saveLine = _line, saveCol = _col;
            Advance();

            if (Cur is '+' or '-')
            {
                Advance();
            }

            if (char.IsDigit(Cur))
            {
                real = true;
                while (!Done && char.IsDigit(Cur))
                {
                    Advance();
                }
            }
            else
            {
                _pos = save;
                _line = saveLine;
                _col = saveCol;
            }
        }


        // THE SUFFIX SAYS WHICH TYPE, and is read and dropped.
        //
        // `1L`, `0xFFu`, `1UL`, `2.5f`, `3d`, `4m`. What the suffix decides in
        // C# is the literal's TYPE, and every integer literal here is already a
        // 64-bit value that narrows where it is used -- so the letter is
        // recorded by being consumed and nothing else. Refusing it meant
        // `-(1L << 63)` did not lex, which is how this compiler's own Isa.cs
        // writes the smallest long there is.
        if (Cur is 'L' or 'l' or 'U' or 'u' or 'F' or 'f' or 'D' or 'd' or 'M' or 'm')
        {
            bool floating = Cur is 'F' or 'f' or 'D' or 'd' or 'M' or 'm';

            Advance();

            // `UL` and `LU` are one suffix in two letters.
            if (!floating && Cur is 'L' or 'l' or 'U' or 'u')
            {
                Advance();
            }

            real = real || floating;
        }

        // Keep the complete spelling. The parser strips a suffix only after it
        // knows the radix, so hexadecimal digits named d/f are never mistaken
        // for decimal floating suffixes, and the binder can preserve L's type.
        return new Token(real ? Tok.Real : Tok.Int, _src[start.._pos], line, col, start);
    }

    private static bool IsRadixDigit(char c, bool hex)
        => hex ? Uri.IsHexDigit(c) : c is '0' or '1';

    /// <summary>
    /// A VERBATIM string: no escapes at all, and a doubled quote is one quote.
    ///
    /// The one rule beyond that is that a newline is ALLOWED, which is the
    /// other reason C# has these -- a block of text written where it is used
    /// rather than joined out of pieces.
    /// </summary>
    private Token VerbatimLiteral(int line, int col, int start)
    {
        Advance();                              // the '@'
        Advance();                              // the opening quote

        StringBuilder sb = new();

        while (true)
        {
            if (Done)
            {
                throw Error("unterminated verbatim string literal", line, col);
            }

            if (Cur == '"')
            {
                Advance();

                if (Cur != '"')
                {
                    return new Token(Tok.Str, sb.ToString(), line, col, start);
                }
            }

            sb.Append(Cur);
            Advance();
        }
    }

    /// <summary>
    /// A RAW string literal: three or more quotes, and no escapes at all inside.
    ///
    /// C# 11's, and the reason it exists is exactly why this compiler's own
    /// sources use it -- Program.cs and Codegen.cs hold blocks of COR source as
    /// string constants, and writing those with backslash escapes makes them
    /// unreadable and unmaintainable. A compiler that cannot lex its own source
    /// is not going to compile itself.
    ///
    /// The rules, and each is load-bearing:
    ///
    ///   * The opening run of quotes sets the length. A longer run closes it,
    ///     which is what lets a literal contain a shorter run -- three quotes
    ///     inside a four-quote literal are just characters.
    ///   * A NEWLINE straight after the opening delimiter makes it multi-line,
    ///     and that newline is not content. Nor is the last one before the
    ///     closing delimiter.
    ///   * The closing delimiter's own INDENTATION is stripped from every line.
    ///     This is what lets the literal sit at the indentation of the code
    ///     around it without the whitespace becoming part of the value.
    /// </summary>
    private Token RawStringLiteral(int line, int col, int start)
    {
        int fence = 0;

        while (Cur == '"')
        {
            fence++;
            Advance();
        }

        // A SINGLE-LINE raw literal ends at the first run of `fence` quotes.
        bool multi = Cur == '\r' || Cur == '\n';

        if (multi)
        {
            if (Cur == '\r') { Advance(); }
            if (Cur == '\n') { Advance(); }
        }

        StringBuilder sb = new();

        while (true)
        {
            if (Done)
            {
                throw Error("unterminated raw string literal", line, col);
            }

            if (Cur == '"')
            {
                int run = 0;
                int at = _pos;

                while (Peek(run) == '"')
                {
                    run++;
                }

                if (run >= fence)
                {
                    for (int i = 0; i < run; i++)
                    {
                        Advance();
                    }

                    return new Token(Tok.Str, Finish(sb.ToString(), multi, at), line, col, start);
                }

                for (int i = 0; i < run; i++)
                {
                    sb.Append('"');
                    Advance();
                }
                continue;
            }

            sb.Append(Cur);
            Advance();
        }
    }

    /// <summary>
    /// Drops the final newline and the closing delimiter's indentation from a
    /// multi-line raw literal.
    ///
    /// The indentation to remove is whatever whitespace precedes the closing
    /// quotes on their own line. Every line must start with it -- C# makes a
    /// line that does not an error, and so does this, because the alternative
    /// is a literal whose value depends on how the file happens to be indented.
    /// </summary>
    private string Finish(string body, bool multi, int closeAt)
    {
        if (!multi)
        {
            return body;
        }

        // THE LAST LINE OF THE BODY IS THE CLOSING FENCE'S INDENTATION, because
        // scanning stopped at the quotes and everything before them on that
        // line was collected as content. So the newline that ends the real
        // content and the indentation that follows it come off together --
        // taking the newline first finds none, since the body ends in spaces.
        List<string> lines = new(body.Replace("\r\n", "\n").Split('\n'));

        if (lines.Count == 0)
        {
            return "";
        }

        string indent = lines[^1];

        if (indent.Trim().Length != 0)
        {
            // Closing quotes were not alone on their line: nothing to strip,
            // and nothing to drop either.
            return string.Join("\n", lines);
        }

        lines.RemoveAt(lines.Count - 1);

        for (int i = 0; i < lines.Count; i++)
        {
            if (lines[i].StartsWith(indent, StringComparison.Ordinal))
            {
                lines[i] = lines[i][indent.Length..];
            }
            else if (lines[i].Trim().Length == 0)
            {
                // A BLANK LINE NEED NOT BE PADDED. An editor that strips
                // trailing whitespace would otherwise change what the program
                // means, which is not a thing a program's value should depend
                // on.
                lines[i] = "";
            }
        }

        return string.Join("\n", lines);
    }

    private Token StringLiteral(int line, int col, int start)
    {
        // THREE OR MORE QUOTES IS A RAW LITERAL, and has to be recognised here
        // before the ordinary scanner reads the first quote and then meets the
        // second as an immediate close.
        if (Peek(1) == '"' && Peek(2) == '"')
        {
            return RawStringLiteral(line, col, start);
        }

        Advance();
        StringBuilder sb = new();

        while (true)
        {
            if (Done || Cur == '\n')
            {
                throw Error("unterminated string literal", line, col);
            }

            if (Cur == '"')
            {
                Advance();

                // `"META"u8` -- a UTF-8 literal, which is BYTES rather than a
                // string. C# 11 spells it this way and it is how a magic number
                // that is really four characters gets written.
                if (Cur == 'u' && Peek() == '8' && !char.IsLetterOrDigit(Peek(2)) && Peek(2) != '_')
                {
                    Advance();
                    Advance();
                    return new Token(Tok.Utf8Str, sb.ToString(), line, col, start);
                }

                return new Token(Tok.Str, sb.ToString(), line, col, start);
            }

            sb.Append(Cur == '\\' ? Escape() : ReadChar());
        }
    }

    /// An interpolated string, kept RAW.
    ///
    /// Escapes are not processed and braces are not matched here. Both belong
    /// to the parser: what is between a pair of braces is an EXPRESSION, and
    /// deciding where that expression ends means knowing that a brace inside a
    /// nested string literal is not a closing brace. A lexer that tried would
    /// be a second, worse parser.
    ///
    /// What this does have to do is find the end of the literal, and that needs
    /// the same knowledge in miniature: a quote inside the braces is the start
    /// of a nested string, not the end of this one.
    /// <summary>
    /// VERBATIM AND INTERPOLATED AT ONCE -- `$@"..."` and `@$"..."` -- is the
    /// same string with two of its rules changed: a backslash is a backslash,
    /// `""` is one quote, and a newline is allowed inside it. The holes still
    /// work exactly as they do in `$"..."`.
    ///
    /// The difference is carried to the parser by DOUBLING the backslashes
    /// rather than by a second kind of token: the parser unescapes the literal
    /// pieces after it has split the holes out, and `\\` unescapes to the one
    /// backslash that was written. Until this, `$@"C:\n"` meant a newline --
    /// the one thing the `@` was there to prevent.
    /// </summary>
    private Token InterpolatedLiteral(int line, int col, int start, bool skipAt = false,
                                      bool verbatim = false)
    {
        Advance();                              // '$'

        if (skipAt)
        {
            Advance();                          // '@', in `$@"..."`
        }

        Advance();                              // '"'

        StringBuilder sb = new();
        int depth = 0;

        while (true)
        {
            if (Done || (Cur == '\n' && !verbatim))
            {
                throw Error("unterminated interpolated string", line, col);
            }

            if (Cur == '"' && depth == 0)
            {
                // `""` INSIDE A VERBATIM STRING IS ONE QUOTE, not the end of
                // it. Passed on as an escaped quote, which is what the parser
                // already knows how to read.
                if (verbatim && Peek(1) == '"')
                {
                    Advance();
                    Advance();
                    sb.Append('\\');
                    sb.Append('"');
                    continue;
                }

                Advance();
                return new Token(Tok.InterpStr, sb.ToString(), line, col, start);
            }

            if (Cur == '\\' && verbatim)
            {
                Advance();
                sb.Append('\\');
                sb.Append('\\');
                continue;
            }

            if (Cur == '\\')
            {
                // Kept whole, both characters, so the parser can unescape the
                // literal pieces after it has split them out.
                sb.Append(ReadChar());

                if (!Done)
                {
                    sb.Append(ReadChar());
                }
                continue;
            }

            if (Cur == '{')
            {
                depth = Peek(1) == '{' ? depth : depth + 1;
            }
            else if (Cur == '}' && depth > 0)
            {
                depth--;
            }

            sb.Append(ReadChar());
        }
    }

    private Token CharLiteral(int line, int col, int start)
    {
        Advance();

        if (Done || Cur == '\'')
        {
            throw Error("empty character literal", line, col);
        }

        char value = Cur == '\\' ? Escape() : ReadChar();

        if (Cur != '\'')
        {
            throw Error("character literal holds more than one character", line, col);
        }
        Advance();

        return new Token(Tok.Char, value.ToString(), line, col, start);
    }

    private char ReadChar()
    {
        char c = Cur;
        Advance();
        return c;
    }

    private char Escape()
    {
        int line = _line, col = _col;
        Advance();

        if (Done)
        {
            throw Error("escape sequence runs off the end of the file", line, col);
        }

        char c = Cur;
        Advance();

        switch (c)
        {
            case 'a':  return '\a';
            case 'b':  return '\b';
            case 'f':  return '\f';
            case 'n':  return '\n';
            case 't':  return '\t';
            case 'r':  return '\r';
            case 'v':  return '\v';
            case '0':  return '\0';
            case '\\': return '\\';
            case '"':  return '"';
            case '\'': return '\'';

            case 'u':
            {
                int value = 0;

                for (int i = 0; i < 4; i++)
                {
                    if (Done || !Uri.IsHexDigit(Cur))
                    {
                        throw Error(@"\u needs exactly four hexadecimal digits", line, col);
                    }
                    value = value * 16 + Convert.ToInt32(Cur.ToString(), 16);
                    Advance();
                }
                return (char)value;
            }

            // C#'s variable-length form: one to four hexadecimal digits,
            // as many as there are. "\x1b[0m" is the escape and then "[0m".
            case 'x':
            {
                int value = 0, digits = 0;
                while (digits < 4 && !Done && Uri.IsHexDigit(Cur))
                {
                    value = value * 16 + Convert.ToInt32(Cur.ToString(), 16);
                    Advance();
                    digits++;
                }
                if (digits == 0)
                {
                    throw Error(@"\x needs one to four hexadecimal digits", line, col);
                }
                return (char)value;
            }

            // C# 13's escape character, U+001B.
            case 'e':  return '\u001b';

            default:
                throw Error($"'\\{c}' is not an escape sequence", line, col);
        }
    }

    private Token Punct(int line, int col, int start)
    {
        char c = Cur;
        char n = Peek();
        char n2 = Peek(2);

        // Longest match first, so >>= does not lex as >> then =.
        foreach ((string text, Tok kind) in ThreePunctuation)
        {
            if (c == text[0] && n == text[1] && n2 == text[2])
            {
                Advance();
                Advance();
                Advance();
                return new Token(kind, text, line, col, start);
            }
        }

        foreach ((string text, Tok kind) in TwoPunctuation)
        {
            if (c == text[0] && n == text[1])
            {
                Advance();
                Advance();
                return new Token(kind, text, line, col, start);
            }
        }

        Tok single = c switch
        {
            '(' => Tok.LParen, ')' => Tok.RParen,
            '{' => Tok.LBrace, '}' => Tok.RBrace,
            '[' => Tok.LBracket, ']' => Tok.RBracket,
            ',' => Tok.Comma, ';' => Tok.Semi, '.' => Tok.Dot,
            ':' => Tok.Colon, '?' => Tok.Question,
            '=' => Tok.Assign,
            '+' => Tok.Plus, '-' => Tok.Minus, '*' => Tok.Star,
            '/' => Tok.Slash, '%' => Tok.Percent,
            '&' => Tok.Amp, '|' => Tok.Pipe, '^' => Tok.Caret,
            '~' => Tok.Tilde, '!' => Tok.Bang,
            '<' => Tok.Lt, '>' => Tok.Gt,
            _   => Tok.End,
        };

        if (single == Tok.End)
        {
            throw Error($"'{c}' is not valid here", line, col);
        }

        Advance();
        return new Token(single, CharacterText[c], line, col, start);
    }
}
