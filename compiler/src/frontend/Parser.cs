using System.Text;
#nullable enable
namespace Corsac.Lang;

/// <summary>
/// Tokens to an AST. Recursive descent with precedence climbing.
///
/// The one genuinely hard piece is telling <c>Foo&lt;Bar&gt;(x)</c> from
/// <c>a &lt; b &gt; (c)</c>. Both are valid token sequences and only what
/// FOLLOWS the closing angle bracket distinguishes them, so the parser
/// speculates: it tries the type-argument reading, and accepts it only when the
/// next token is one that could legally follow a generic name. Every other
/// approach either mis-parses ordinary comparisons or demands a symbol table
/// the parser does not have yet.
/// </summary>
public sealed class Parser
{
    private readonly ParserTokens _t;
    private readonly string _file;

    /// <summary>
    /// The text the tokens were read from, when there is one: each call keeps
    /// where its receiver and arguments were written in it, which is what
    /// [CallerArgumentExpression] passes.
    /// </summary>
    public string? Source { get; init; }

    /// <summary>
    /// WHERE THE TOKENS SIT, for tokens that are one declaration cut out of a
    /// file (Lexer.TokenizeRange): the namespace it was written in and the
    /// path it was written at (TypeDecl.Outer -- the namespace for a top-level
    /// type, the outer types' path for a nested one). Read so, it comes out
    /// with the same Namespace and Outer, and its nested types with the same
    /// paths, as when its whole file is read. Empty for a whole file.
    /// </summary>
    public string StartNamespace { get; init; } = "";
    public string? StartTypePath { get; init; }

    /// <summary>
    /// Whether anything read took a number from the file-wide count that names
    /// generic local functions and local-function delegates. Those names are
    /// members and types other units name, so a declaration read on its own,
    /// which counts from zero, may not be read so if it took any.
    /// </summary>
    public bool Hoisted => _hoistSerial != 0;

    /// <summary>Spans of the calls being parsed, innermost last; see CallExpr.Spans.</summary>
    private readonly List<int> _spans = new();
    private readonly bool _declarationsOnly;
    private readonly bool _includeTemplateBodies;
    private int _templateDepth;

    /// <summary>
    /// The type parameters of the types being parsed around this point, all of
    /// them: what a type written inside takes first (TypeDecl.OuterParams).
    /// </summary>
    private List<string> _enclosingParams = new();
    private bool _templateMethod;
    private bool SkipImplementation => _declarationsOnly && !(_includeTemplateBodies && (_templateDepth > 0 || _templateMethod));

    /// <summary>
    /// Whether members written here from text (a delegate's multicast and its
    /// Combine and Remove, a record's equality) are read without their
    /// bodies: as this parser reads the declaration they belong to -- a
    /// generic one's kept where template bodies are -- not merely because
    /// it reads declarations. A generic delegate's CombineImpl, read without
    /// its body in a unit that took the library as declarations, came out of
    /// that unit empty, answering whatever its result register held.
    /// </summary>
    private bool SkipWritten(bool generic) => _declarationsOnly && !(_includeTemplateBodies && (_templateDepth > 0 || _templateMethod || generic));
    /// <summary>Source ranges omitted by declaration-only parsing; end is exclusive.</summary>
    public List<(int From, int To, bool Block)> OmittedBodies { get; } = new();
    private int _i;
    private string? _iteratorTarget;
    private TypeRef? _iteratorElement;
    private bool _iteratorSawYield;
    private int _iteratorSerial;
    private int _lockSerial;

    /// Types written inside another type, waiting to be put beside it.
    private readonly List<TypeDecl> _nested = new();

    /// <summary>
    /// The type currently being parsed, spelled the way C# spells it --
    /// `Outer.Inner` -- or empty at the top level.
    ///
    /// A nested type takes this as its <see cref="TypeDecl.Outer"/>, which is
    /// what keeps two nested types of the same simple name apart: `Assembler`
    /// and `ImageFile` both hold a `Section`, and only the full path tells the
    /// checker they are two types rather than one declared twice.
    /// </summary>
    private string _typePath = "";

    /// <summary>The namespace being read, or the empty string for the global one.</summary>
    private string _namespace = "";

    /// <summary>The using directives of the file being read, shared by its types.</summary>
    private FileScope _fileScope = new();

    /// Every '&gt;&gt;' this parser has split into two '&gt;', so a speculative
    /// read that turns out not to be a type can put them back.
    private readonly List<(int At, Token Was)> _splits = new();

    public Parser(List<Token> tokens, string file = "<source>", bool declarationsOnly = false, bool includeTemplateBodies = false)
        : this(new ParserTokens(tokens), file, declarationsOnly, includeTemplateBodies) { }

    public Parser(Token[] tokens, string file = "<source>", bool declarationsOnly = false, bool includeTemplateBodies = false)
        : this(new ParserTokens(tokens), file, declarationsOnly, includeTemplateBodies) { }

    private Parser(ParserTokens tokens, string file, bool declarationsOnly, bool includeTemplateBodies)
    {
        _t = tokens;
        _file = file;
        _declarationsOnly = declarationsOnly;
        _includeTemplateBodies = includeTemplateBodies;
    }

    public static CompilationUnit ParseText(string source, string file = "<source>",
                                            IReadOnlyCollection<string>? symbols = null, bool declarationsOnly = false, bool includeTemplateBodies = false)
    {
        List<PragmaWarning> pragmas = new();
        List<Token> tokens = Lexer.Tokenize(source, file, 1, 1, symbols, pragmas);
        CompilationUnit unit = new Parser(tokens, file, declarationsOnly, includeTemplateBodies) { Source = source }.ParseUnit();

        unit.Pragmas.AddRange(pragmas);
        return unit;
    }

    // ---- token helpers --------------------------------------------------

    /// <summary>Where token <paramref name="index"/> ends in <see cref="Source"/>.</summary>
    private int End(int index) => Lexer.TokenEnd(Source!, _t[index].Pos);

    /// <summary>
    /// Where an argument's expression starts: after `ref`, `out` or `in`,
    /// which C# leaves out of the argument's text.
    /// </summary>
    private int ArgumentStart() => At(Tok.KwOut) || At(Tok.KwRef) || At(Tok.KwIn) ? Ahead().Pos : Cur.Pos;

    private Token Cur => _t[Math.Min(_i, _t.Count - 1)];
    private Token Ahead(int n = 1) => _t[Math.Min(_i + n, _t.Count - 1)];
    private bool At(Tok k) => Cur.Kind == k;

    /// One argument, which may be passed BY REFERENCE.
    ///
    ///     f(x)            an ordinary value
    ///     f(out x)        the address of something already declared
    ///     f(ref x)        the same, with a different rule about who writes first
    ///     f(out int x)    declares x here and passes its address
    ///     f(out var x)    the same, with the type taken from the parameter
    ///
    /// The declaration forms are the reason this needs backtracking: `out x`
    /// and `out int x` both start with the keyword and then an identifier, and
    /// nothing short of reading the SECOND one tells them apart. The parser is
    /// an array and an index, so saving the index and putting it back is the
    /// whole of it.
    private Expr ParseArg()
    {
        // `in x` passes x by read-only reference, which is what the
        // parameter already says: the keyword at the call site is C#'s
        // optional way of saying it too (EvalAs passes a variable by address
        // and anything else by a copy for an `in` parameter).
        if (At(Tok.KwIn))
        {
            _i++;
            return ParseExpr();
        }

        if (!At(Tok.KwOut) && !At(Tok.KwRef) || StartsRefLambda())
        {
            return ParseExpr();
        }

        Token at = Cur;
        bool isOut = At(Tok.KwOut);

        _i++;

        // A DISCARD IS STORAGE THE CALLER CANNOT NAME. Give it a unique hidden
        // local so the ordinary by-reference ABI has a real address to pass,
        // while repeated `out _` arguments never alias one another.
        if (isOut && At(Tok.Ident) && Cur.Text == "_")
        {
            string discard = $"$discard${at.Line}${at.Col}";
            _i++;
            return new RefArgExpr
            {
                Target = new NameExpr { Name = discard, Line = at.Line, Col = at.Col },
                IsOut = true, Name = discard, Line = at.Line, Col = at.Col,
            };
        }

        // `out var x` -- the type comes from the parameter, so there is nothing
        // to resolve here and Declare stays null.
        if (At(Tok.KwVar) && _t[_i + 1].Kind == Tok.Ident)
        {
            _i++;

            string inferred = _t[_i++].Text;

            return new RefArgExpr
            {
                Target = new NameExpr { Name = inferred, Line = at.Line, Col = at.Col },
                IsOut = isOut, Name = inferred, Line = at.Line, Col = at.Col,
            };
        }

        // `out T x` -- try it, and put the index back if what followed the type
        // was not a name. A tuple type is written in brackets, so a '(' here
        // may open one: `TryGetValue(r, out (Block Block, int Index) site)` is
        // how this compiler's own optimiser asks where a register was defined.
        int was = _i;

        if (At(Tok.Ident) || At(Tok.LParen))
        {
            TypeRef? declared = null;

            try
            {
                declared = ParseTypeRef();
            }
            catch (Exception)
            {
                declared = null;
            }

            if (declared is not null && At(Tok.Ident))
            {
                string name = _t[_i++].Text;

                return new RefArgExpr
                {
                    Target = new NameExpr { Name = name, Line = at.Line, Col = at.Col },
                    IsOut = isOut, Declare = declared, Name = name,
                    Line = at.Line, Col = at.Col,
                };
            }
            _i = was;
        }

        // `out seen!`: the `!` says only that the variable is not null, and
        // is no expression of its own to pass the address of -- C# takes it,
        // and the variable is what is passed.
        Expr target = ParseExpr();
        while (target is SuppressExpr { Operand: { } forgiven }) target = forgiven;
        return new RefArgExpr
        {
            Target = target, IsOut = isOut, Line = at.Line, Col = at.Col,
        };
    }

    /// `$"..."` becomes the pieces joined with '+'.
    ///
    /// DESUGARED HERE AND NOWHERE ELSE, which is the whole appeal of it: '+' on
    /// strings already exists, already calls String.Concat, and already knows
    /// how to turn a number into text on the way. So interpolation is a parser
    /// feature and the binder and the code generator never learn the word.
    ///
    /// `{{` and `}}` are a single brace, which is how a literal brace is
    /// written -- and is why the scan cannot simply look for the next '{'.
    private Expr Interpolate(Token at)
    {
        string raw = at.Text;
        Expr? built = null;
        StringBuilder literal = new();
        int i = 0;

        void Flush()
        {
            if (literal.Length == 0)
            {
                return;
            }

            Join(new LiteralExpr
            {
                Kind = Lit.Str, Text = literal.ToString(), Line = at.Line, Col = at.Col,
            });
            literal.Clear();
        }

        void Join(Expr piece)
        {
            built = built is null
                ? piece
                : new BinaryExpr
                  {
                      Op = BinOp.Add, Left = built, Right = piece,
                      Line = at.Line, Col = at.Col,
                  };
        }

        while (i < raw.Length)
        {
            char c = raw[i];

            if (c == '{' && i + 1 < raw.Length && raw[i + 1] == '{')
            {
                literal.Append('{');
                i += 2;
                continue;
            }

            if (c == '}' && i + 1 < raw.Length && raw[i + 1] == '}')
            {
                literal.Append('}');
                i += 2;
                continue;
            }

            if (c == '\\' && i + 1 < raw.Length)
            {
                literal.Append(EscapeAt(raw, i, at, out int used));
                i += used;
                continue;
            }

            if (c != '{')
            {
                literal.Append(c);
                i++;
                continue;
            }

            // THE HOLE. Find its end, counting nested braces and skipping the
            // ones inside a nested string -- `$"{f("}")}"` is legal and the
            // brace in the middle is text.
            int depth = 1;
            int from = ++i;
            bool inString = false;

            while (i < raw.Length && depth > 0)
            {
                char d = raw[i];

                if (d == '\\' && i + 1 < raw.Length)
                {
                    i += 2;
                    continue;
                }

                if (d == '"')
                {
                    inString = !inString;
                }
                else if (!inString && d == '{')
                {
                    depth++;
                }
                else if (!inString && d == '}')
                {
                    depth--;

                    if (depth == 0)
                    {
                        break;
                    }
                }
                i++;
            }

            if (depth != 0)
            {
                throw Error("this interpolated string has a '{' with no '}'");
            }

            string inner = raw[from..i].Trim();

            i++;                                // past the '}'

            if (inner.Length == 0)
            {
                throw Error("this interpolated string has an empty '{}'");
            }

            Flush();

            // PARSED AS AN ORDINARY EXPRESSION, by an ordinary parser. There is
            // no second grammar for what goes between the braces, which is what
            // keeps `{a.B(c)[0]}` working without anybody having thought about
            // it.
            // `{x,5}` and `{x:x8}` -- the ALIGNMENT and the FORMAT, which are
            // not part of the expression and were being read as though they
            // were. `sub.ParseExpr()` stopped at the comma or the colon and
            // nobody looked at what was left, so `$"{x,5}"` printed an
            // unpadded number and `$"{x:x8}"` printed a decimal one. Silently:
            // the string said one thing and meant another.
            string expression = inner;
            string? alignment = null;
            string? format = null;

            int mark = HoleSuffix(inner);

            if (mark >= 0)
            {
                expression = inner[..mark].Trim();

                if (inner[mark] == ',')
                {
                    int colon = HoleSuffix(inner[(mark + 1)..]);

                    if (colon >= 0)
                    {
                        alignment = inner[(mark + 1)..(mark + 1 + colon)].Trim();
                        format = inner[(mark + colon + 2)..].Trim();
                    }
                    else
                    {
                        alignment = inner[(mark + 1)..].Trim();
                    }
                }
                else
                {
                    format = inner[(mark + 1)..].Trim();
                }
            }

            // WHERE THE HOLE REALLY IS, so a diagnostic about what is inside
            // it points there and not at the first line of the file. `from` is
            // an index into the string's decoded text, which is the right
            // column whenever the text before it held no escape -- and an
            // escape only ever shortens it, so this never overshoots the line.
            List<Token> tokens = Lexer.Tokenize(expression, _file, at.Line, at.Col + 2 + from);
            Parser sub = new(tokens, _file) { Source = expression };
            Expr hole = sub.ParseExpr();

            if (format is not null)
            {
                hole = Formatted(hole, format, at);
            }

            if (alignment is not null)
            {
                hole = Aligned(hole, alignment, at);
            }

            Join(hole);
        }

        Flush();

        // `$""` is the empty string rather than nothing at all.
        return built ?? new LiteralExpr { Kind = Lit.Str, Text = "", Line = at.Line, Col = at.Col };
    }

    /// <summary>
    /// Where a hole's alignment or format begins, or -1 when it has neither.
    ///
    /// The comma or colon that starts one is the first at the TOP LEVEL: a
    /// hole may hold a call with arguments, an index, a nested string or a
    /// conditional, and each of those has a comma or a colon of its own that
    /// belongs to the expression rather than to the format.
    /// </summary>
    private static int HoleSuffix(string inner)
    {
        int depth = 0;
        bool inString = false;
        int conditionals = 0;

        for (int i = 0; i < inner.Length; i++)
        {
            char c = inner[i];

            if (inString)
            {
                if (c == '\\')
                {
                    i++;
                }
                else if (c == '"')
                {
                    inString = false;
                }
                continue;
            }

            switch (c)
            {
                case '"':
                    inString = true;
                    break;

                case '(' or '[' or '{':
                    depth++;
                    break;

                case ')' or ']' or '}':
                    depth--;
                    break;

                // A TYPE ARGUMENT LIST IS PASSED OVER WHOLE, its commas and
                // all (`{Make<int, string>()}`); any other `<` or `>` is a
                // comparison or a shift and brackets nothing. Counted as
                // brackets, `{(n > 0 && m > 0 ? F(a, b) : 0)}` went below
                // zero and the comma inside F's arguments became an alignment.
                case '<' when TypeArgumentsEnd(inner, i) is int close:
                    i = close;
                    break;

                // A `?` THAT ASKS A QUESTION, which is the only kind that
                // brings a colon with it. `a?.B`, `a?[0]` and `a ?? b` are
                // not conditionals, and counting them would let the colon of
                // `{a?.B:x8}` be taken for theirs and the format be dropped
                // again.
                case '?' when depth == 0 && i + 1 < inner.Length
                              && inner[i + 1] is not ('.' or '?' or '['):
                    conditionals++;
                    break;

                case ':' when depth == 0 && conditionals > 0:
                    // The colon of a `? :`, which belongs to the expression.
                    conditionals--;
                    break;

                case ',' or ':' when depth == 0:
                    return i;
            }
        }
        return -1;
    }

    /// <summary>
    /// Where the type argument list opened at `open` closes, when the `<` there
    /// begins one: it follows a name, holds only what a type is written with,
    /// and is followed by what may follow a generic name -- C#'s own test for
    /// telling `F<A, B>(x)` from `a < b`. Null when it is an operator.
    /// </summary>
    private static int? TypeArgumentsEnd(string text, int open)
    {
        if (open == 0 || !(char.IsLetterOrDigit(text[open - 1]) || text[open - 1] == '_'))
        {
            return null;
        }
        int nesting = 0;
        for (int i = open; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '<')
            {
                nesting++;
                continue;
            }
            if (c == '>')
            {
                nesting--;
                if (nesting > 0) continue;
                int after = i + 1;
                while (after < text.Length && text[after] == ' ') after++;
                return after >= text.Length || text[after] is '(' or '.' or ')' or ',' or ']' or '}' or '>' or ':' or '?' or '['
                     ? i : null;
            }
            if (!(char.IsLetterOrDigit(c) || c is '_' or '.' or ',' or ' ' or '?' or '[' or ']' or '(' or ')'))
            {
                return null;
            }
        }
        return null;
    }

    /// <summary>
    /// `{x,5}` and `{x,-5}` -- the value padded to a width, on the left for a
    /// positive alignment and on the right for a negative one, exactly as C#
    /// reads the sign.
    /// </summary>
    private Expr Aligned(Expr value, string alignment, Token at)
    {
        if (!int.TryParse(alignment, out int width))
        {
            throw Error($"'{alignment}' is not a width: an alignment is a whole number");
        }

        CallExpr pad = Library("String", width < 0 ? "PadRight" : "PadLeft", at);

        pad.Args.Add(Text(value, at));
        pad.WritableArgNames.Add(null);
        pad.Args.Add(new LiteralExpr
        {
            Kind = Lit.Int, Text = Math.Abs(width).ToString(), IntValue = Math.Abs(width),
            Line = at.Line, Col = at.Col,
        });
        pad.WritableArgNames.Add(null);
        return pad;
    }

    /// <summary>
    /// `{value:format}` -- the value formatting itself, as C#'s interpolation
    /// has it: `value.ToString("format")` where the value's type takes a format
    /// (IFormattable: the numbers, DateTime, TimeSpan and the rest), and the
    /// value as it is otherwise, the format ignored. Which of those it is
    /// depends on the value's type, so the call is marked and the binder
    /// decides (Binder.FormatHole).
    /// </summary>
    private Expr Formatted(Expr value, string format, Token at)
    {
        CallExpr call = new()
        {
            Target = new MemberExpr { Target = value, Name = "ToString", Line = at.Line, Col = at.Col },
            FormatHole = true, Line = at.Line, Col = at.Col,
        };
        call.Args.Add(new LiteralExpr { Kind = Lit.Str, Text = format, Line = at.Line, Col = at.Col });
        call.WritableArgNames.Add(null);
        return call;
    }

    /// <summary>A call to `Type.Member(...)` in the standard library.</summary>
    private static CallExpr Library(string type, string member, Token at)
        => new()
        {
            Target = new MemberExpr
            {
                Target = new NameExpr { Name = type, Line = at.Line, Col = at.Col },
                Name = member, Line = at.Line, Col = at.Col,
            },
            Line = at.Line, Col = at.Col,
        };

    /// <summary>
    /// The value as a string, spelled the way every other stringification in
    /// this language is: `"" + value`, which reaches the same conversion an
    /// interpolated string's pieces already go through.
    /// </summary>
    private static Expr Text(Expr value, Token at)
        => new BinaryExpr
        {
            Op = BinOp.Add,
            Left = new LiteralExpr { Kind = Lit.Str, Text = "", Line = at.Line, Col = at.Col },
            Right = value,
            Line = at.Line, Col = at.Col,
        };

    /// The escapes a literal piece of an interpolated string may carry. The
    /// lexer kept them whole because it could not know which pieces were
    /// literal until the braces had been matched.
    /// <summary>
    /// The escape at `at` of an interpolated string's raw text, as C# has
    /// them all: the one-letter ones, \u and four digits, \x and one to four,
    /// \U and eight (a code point, two UTF-16 units past the basic plane),
    /// and \e. How many characters it took, backslash included.
    /// </summary>
    private string EscapeAt(string raw, int at, Token where, out int used)
    {
        char kind = raw[at + 1];
        int Digits(int least, int most, out int value)
        {
            value = 0;
            int count = 0;
            while (count < most && at + 2 + count < raw.Length && Uri.IsHexDigit(raw[at + 2 + count]))
            {
                value = value * 16 + Convert.ToInt32(raw[at + 2 + count].ToString(), 16);
                count++;
            }
            if (count < least) throw Error($"'\\{kind}' needs {(least == most ? least.ToString() : least + " to " + most)} hexadecimal digits");
            return count;
        }
        switch (kind)
        {
            case 'u':
                used = 2 + Digits(4, 4, out int unit);
                return ((char)unit).ToString();
            case 'x':
                used = 2 + Digits(1, 4, out int shortUnit);
                return ((char)shortUnit).ToString();
            case 'U':
                used = 2 + Digits(8, 8, out int point);
                if (point > 0x10FFFF) throw Error("'\\U' names no character past U+10FFFF");
                return char.ConvertFromUtf32(point);
            case 'e':
                used = 2;
                return "\u001b";
            default:
                used = 2;
                return Unescape(kind, where).ToString();
        }
    }

    private char Unescape(char c, Token at) => c switch
    {
        'a'  => '\a',
        'b'  => '\b',
        'f'  => '\f',
        'n'  => '\n',
        't'  => '\t',
        'r'  => '\r',
        'v'  => '\v',
        '0'  => '\0',
        '\\' => '\\',
        '"'  => '"',
        '\'' => '\'',
        _    => throw Error($"'\\{c}' is not an escape this understands"),
    };

    /// Whether this token could begin the rest of a member declaration -- a
    /// type name, or another modifier.
    ///
    /// This is what tells `required long X;` from a statement that happens to
    /// start with a variable called required. Only used for the contextual
    /// modifier, and deliberately narrow: a word is a modifier only when what
    /// follows it could not be anything else.
    private static bool StartsMember(Tok k)
        => k is Tok.Ident or Tok.KwPublic or Tok.KwPrivate or Tok.KwProtected
             or Tok.KwInternal or Tok.KwStatic or Tok.KwReadonly or Tok.KwConst
             or Tok.KwVirtual or Tok.KwOverride or Tok.KwAbstract or Tok.KwSealed
             or Tok.KwClass or Tok.KwInterface or Tok.KwStruct or Tok.KwEnum
             or Tok.KwVoid or Tok.KwDelegate;

    /// A word that is a keyword only where it is expected. See 'init'.
    private bool TakeContextual(string word)
    {
        if (!At(Tok.Ident) || Cur.Text != word)
        {
            return false;
        }

        _i++;
        return true;
    }

    private bool Take(Tok k)
    {
        if (!At(k))
        {
            return false;
        }
        _i++;
        return true;
    }

    private Token Expect(Tok k, string what)
    {
        if (!At(k))
        {
            throw Error($"expected {what}, found '{Cur.Text}'");
        }
        return _t[_i++];
    }

    private CompileError Error(string message) => new(_file, Cur.Line, Cur.Col, message);

    // ---- compilation unit -----------------------------------------------

    /// <summary>
    /// The project's own using directives, in every file: what an SDK
    /// project's ImplicitUsings and &lt;Using&gt; items make into a generated
    /// file of global usings. `Name` imports a namespace (or a type, for a
    /// static one), `Alias=Name` names one. Set by the driver (--using) for
    /// the whole compilation.
    /// </summary>
    public static IReadOnlyList<string> ProjectUsings { get; set; } = Array.Empty<string>();

    public CompilationUnit ParseUnit()
    {
        Token start = Cur;
        CompilationUnit unit = new() { Line = start.Line, Col = start.Col };

        _namespace = StartNamespace;
        _typePath = StartTypePath ?? StartNamespace;
        _fileScope = new FileScope();
        foreach (string project in ProjectUsings)
        {
            int equals = project.IndexOf('=');
            if (equals > 0) _fileScope.Aliases.Add(("", project[..equals], project[(equals + 1)..]));
            else _fileScope.Imports.Add(("", project));
            unit.Usings.Add(equals > 0 ? project[..equals] : project);
        }

        ParseUsings(unit);

        // A NAMESPACE IS A PATH ON THE TYPES INSIDE IT. `Corsac.Lang.Expr` and
        // `Corsac.Asm.Expr` are two types, and the flat table of types keys
        // them by exactly that path -- the same path a nested type is keyed by,
        // which is why one mechanism serves both.
        //
        // ANYWHERE AT THE TOP LEVEL, as C# has it: before, between and after
        // types of the global namespace, more than one, block form nested in
        // block form. A file-scoped one covers the rest of the file.
        //
        // TOP-LEVEL STATEMENTS: a file that is a program rather than a library.
        // C# wraps them in a Main of its own making, and so does this -- there
        // is nothing else they could mean, and a file written that way is what
        // `dotnet new console` produces today. Recognised by what is there: a
        // statement where a type declaration would be, before any type.
        if (!At(Tok.End) && !At(Tok.RBrace) && !At(Tok.KwNamespace) && !StartsTypeDecl())
        {
            unit.Types.Add(ParseTopLevel(start));
        }

        ParseNamespaceMembers(unit, StartNamespace, StartTypePath ?? StartNamespace);
        unit.UsesDynamic = _sawDynamic;
        return unit;
    }

    /// <summary>Whether a type named `dynamic` was read (CompilationUnit.UsesDynamic).</summary>
    private bool _sawDynamic;

    /// <summary>
    /// Whether the `dynamic` at token i is the type and not a name: a type is
    /// parsed speculatively at many places a name could stand, and a
    /// parameter called `dynamic` (Runtime.StartImages) made the whole file
    /// one that uses dynamic, its classes given late members in its own
    /// object only. The type: declaring something (`dynamic d`), an array of
    /// it, a type argument (`List&lt;dynamic&gt;`, `, dynamic&gt;`), a cast
    /// (`(dynamic)x`).
    /// </summary>
    private bool WrittenAsType(int i)
    {
        Tok Kind(int at) => at >= 0 && at < _t.Count ? _t[at].Kind : Tok.Semi;
        Tok before = Kind(i - 1), after = Kind(i + 1);
        if (after == Tok.Ident) return true;
        if (after == Tok.LBracket && Kind(i + 2) == Tok.RBracket) return true;
        if (after == Tok.Question && Kind(i + 2) == Tok.Ident) return true;
        if (before == Tok.Lt) return true;
        if (before == Tok.Comma && after == Tok.Gt) return true;
        if (before == Tok.LParen && after == Tok.RParen)
            return Kind(i + 2) is Tok.Ident or Tok.Int or Tok.Real or Tok.Str or Tok.Char or Tok.LParen or Tok.KwNew or Tok.KwThis
                                 or Tok.KwNull or Tok.KwTrue or Tok.KwFalse or Tok.InterpStr;
        return false;
    }

    /// The types and namespaces of one namespace (`within`, "" for the
    /// global one), up to the brace that closes it or the end of the file.
    private void ParseNamespaceMembers(CompilationUnit unit, string within, string? path = null)
    {
        while (!At(Tok.End) && !At(Tok.RBrace))
        {
            if (!At(Tok.KwNamespace))
            {
                _namespace = within;
                _typePath = path ?? within;
                TakeTypeDecl(unit);
                continue;
            }

            _i++;
            string name = ParseDottedName();
            string inner = within.Length == 0 ? name : within + "." + name;
            _namespace = inner;
            _typePath = inner;
            bool block = !Take(Tok.Semi);
            if (block)
            {
                Expect(Tok.LBrace, "'{' or ';' after namespace");
            }

            // A namespace's own using directives scope what follows them --
            // the file-scoped form's above all, which current C# projects
            // write before their types.
            ParseUsings(unit);
            ParseNamespaceMembers(unit, inner);

            if (block)
            {
                Expect(Tok.RBrace, "'}' to close the namespace");
                ParseUsings(unit);
            }
            _namespace = within;
            _typePath = within;
        }
    }

    /// <summary>
    /// Every use of a type nested in a generic one, written inside that
    /// generic one or anything else nested in it, given the outer parameters
    /// it takes (TypeDecl.OuterParams) as its first arguments: `Inner` inside
    /// Outer&lt;T&gt; is Outer.Inner&lt;T&gt;. A use from outside is written with
    /// them already -- `Outer&lt;int&gt;.Inner`, which ParseTypeRef joins into
    /// Outer.Inner&lt;int&gt;.
    /// </summary>
    private static void FinishFamily(TypeDecl top, List<TypeDecl> nested)
    {
        List<TypeDecl> family = new() { top };
        family.AddRange(nested);
        static string PathOf(TypeDecl d) => d.Outer is null ? d.Name : d.Outer + "." + d.Name;
        foreach (TypeDecl inner in family)
        {
            if (inner.OuterParams == 0 || inner.Outer is null) continue;
            string outer = inner.Outer, path = PathOf(inner);
            int own = inner.TypeParams.Count - inner.OuterParams;
            foreach (TypeDecl within in family)
            {
                string at = PathOf(within);
                if (at != outer && !at.StartsWith(outer + ".", StringComparison.Ordinal)) continue;
                Corsac.Lang.Metadata.BodyTypeNames.Walk(within, reference =>
                {
                    if ((reference.Name != inner.Name && reference.Name != path) || reference.Args.Count != own) return;
                    for (int k = inner.OuterParams - 1; k >= 0; k--)
                        reference.Arguments.Insert(0, new TypeRef { Name = inner.TypeParams[k].Name, Line = reference.Line, Col = reference.Col });
                    if (reference.Name == inner.Name) reference.Name = path;
                });
            }
        }
    }

    private void TakeTypeDecl(CompilationUnit unit)
    {
        TypeDecl top = ParseTypeDecl();
        FinishFamily(top, _nested);
        if (Ring < 0 || LeaveOutOtherRings(top, _nested)) unit.Types.Add(top);

        // Anything written INSIDE what was just parsed comes out here, at
        // the top level, under its own simple name.
        unit.Types.AddRange(_nested);
        _nested.Clear();
    }

    // ---- rings ----------------------------------------------------------

    /// <summary>
    /// THE RING BEING COMPILED, 0 to 3, or -1 for none (--ring). A driver
    /// that works in two rings is one source file: its ring-0 half -- DMA,
    /// the interrupt -- and its ring-1 half -- what of the mixing is its own
    /// -- are classes marked [Ring0] and [Ring1], and what both use is not
    /// marked at all (docs/software/DRIVERS.md in the OS repository, "Drivers
    /// in every ring"). The file is compiled once per ring, and each compile
    /// sees only its own ring's classes and the unmarked ones.
    ///
    /// LEFT OUT HERE, AS THEY ARE READ, rather than anywhere later, because
    /// a ring-0 class names what only ring 0 has -- Irq, Dma, the port
    /// instructions' wrappers -- and in a ring-1 compile none of that exists
    /// to bind to. Dropped before the binder sees it, it never has to; and
    /// the declaration index, which is built by this parser too, holds the
    /// same classes the compile does. With no ring given the marks mean
    /// nothing and every class is compiled, which is every compile that is
    /// not of a driver.
    /// </summary>
    public static int Ring { get; set; } = -1;

    /// <summary>The rings a declaration is marked for, a bit each, or 0 when it is not marked and so is every ring's.</summary>
    public static int RingsOf(TypeDecl decl)
    {
        int rings = 0;
        foreach (AttributeRef attribute in decl.AttributeParts)
            for (int ring = 0; ring < 4; ring++)
                if (attribute.Target.Length == 0 && attribute.Is("Ring" + ring)) rings |= 1 << ring;
        return rings;
    }

    /// <summary>
    /// Whether <paramref name="top"/> is kept in this ring's compile, taking
    /// out of <paramref name="nested"/> every type written inside one that is
    /// not -- a class marked for another ring goes with all it holds -- and a
    /// delegate's multicast with its delegate.
    /// </summary>
    private static bool LeaveOutOtherRings(TypeDecl top, List<TypeDecl> nested)
    {
        static string PathOf(TypeDecl d) => d.Outer is null ? d.Name : d.Outer + "." + d.Name;
        static bool Other(TypeDecl d) => RingsOf(d) is int rings && rings != 0 && (rings & (1 << Ring)) == 0;
        List<TypeDecl> gone = new();
        if (Other(top)) gone.Add(top);
        gone.AddRange(nested.Where(Other));
        if (gone.Count == 0) return true;
        nested.RemoveAll(d => gone.Any(left => d == left
            || (d.Outer is string outer && (outer == PathOf(left) || outer.StartsWith(PathOf(left) + ".", StringComparison.Ordinal)))
            || (d.Outer == left.Outer && d.Namespace == left.Namespace && d.Name == left.Name + "__Multicast")));
        return !gone.Contains(top);
    }

    /// <summary>
    /// The using directives at the head of a file, in every spelling C# has.
    ///
    /// `using static Foo;`, `global using Foo;` and `using Alias = Foo;` are
    /// recorded as the plain name they bring in, because this compiler has one
    /// flat table of types and no scoping for a directive to affect. What
    /// matters is that a real C# file compiles WITHOUT ITS HEADER BEING
    /// STRIPPED FIRST; a directive that means nothing here must be read, not
    /// rejected.
    /// </summary>
    private void ParseUsings(CompilationUnit unit)
    {
        while (At(Tok.KwUsing)
               || (At(Tok.Ident) && Cur.Text == "global" && Ahead().Kind == Tok.KwUsing))
        {
            if (!At(Tok.KwUsing))
            {
                _i++;                           // 'global'
            }

            _i++;                               // 'using'
            Take(Tok.KwStatic);

            string name = ParseDottedName();

            // `using Block = Corsac.Lang.Ir.Block;` -- a name for one type,
            // which is how a file that means two things called Block says
            // which. C# looks at an alias after the members of the namespace it
            // was written in, so where it was written is kept with it.
            if (Take(Tok.Assign))
            {
                string target = ParseDottedName();

                TryParseTypeArgs(out _);
                _fileScope.Aliases.Add((_namespace, name, target));
            }
            else
            {
                _fileScope.Imports.Add((_namespace, name));
            }

            unit.Usings.Add(name);
            Expect(Tok.Semi, "';' after using");
        }
    }

    /// <summary>
    /// Statements written at the top of a file, as the Main they stand for.
    ///
    /// The return type is always int and a `return 0;` is appended when the
    /// author did not end with a return of their own, so that a program which
    /// simply prints exits successfully -- which is what C# does for top-level
    /// statements with no return in them.
    /// </summary>
    private TypeDecl ParseTopLevel(Token start)
    {
        Block body = new() { Line = start.Line, Col = start.Col };

        while (!At(Tok.End) && !At(Tok.RBrace) && !StartsTypeDecl())
        {
            body.WritableStatements.Add(ParseStmt());
        }

        if (body.Statements.Count == 0 || body.Statements[^1] is not ReturnStmt)
        {
            body.WritableStatements.Add(new ReturnStmt
            {
                Value = new LiteralExpr
                {
                    Kind = Lit.Int, Text = "0", Line = start.Line, Col = start.Col,
                },
                Line = start.Line, Col = start.Col,
            });
        }

        TypeDecl program = new()
        {
            Kind = TypeKind.Class, Name = "Program", Mods = Mods.Static,
            Line = start.Line, Col = start.Col,
        };

        program.Members.Add(new MethodDecl
        {
            Name = "Main",
            Returns = new TypeRef { Name = "int", Line = start.Line, Col = start.Col },
            Mods = Mods.Static | Mods.Public,
            Body = body,
            Line = start.Line, Col = start.Col,
        });

        return program;
    }

    private string ParseDottedName()
    {
        string name = Expect(Tok.Ident, "a name").Text;

        while (Take(Tok.Dot))
        {
            name += "." + Expect(Tok.Ident, "a name after '.'").Text;
        }
        return name;
    }

    // ---- type declarations ----------------------------------------------

    /// <summary>
    /// The attributes on the declaration being read, by NAME: `[Flags]` is
    /// "Flags". Set by ParseMods, which every declaration begins with, and
    /// read by whoever builds the declaration -- so it is only ever the list
    /// belonging to the thing about to be parsed.
    /// </summary>
    private readonly List<string> _attributes = new();

    /// <summary>
    /// The same attributes with the part of them the checker reads: what each
    /// was aimed at (`return:` or nothing), what it is called, and its first
    /// argument where it has one.
    ///
    /// `[NotNullWhen(false)]` and `[return: NotNullIfNotNull(nameof(path))]`
    /// are the two that mean something -- they are how .NET says what a method
    /// PROVES about null, which the checker has to know to accept
    /// `if (!string.IsNullOrEmpty(dir)) { Use(dir); }`.
    /// </summary>
    private readonly List<AttributeRef> _attributeParts = new();

    /// <summary>
    /// Reads an attribute list, keeping what each one is CALLED and dropping
    /// what it was given.
    ///
    /// `[Flags]` is the one that means something here: C# says an enum
    /// marked with it is a set of bits, and that is what decides whether
    /// `(Read | Run).ToString()` says "Read, Run" or says the number. The
    /// arguments of `[Obsolete("...")]` and the rest are still dropped, and
    /// nested brackets are counted while dropping them, because an attribute
    /// argument may contain an array: `[Foo(new[] { 1, 2 })]`.
    ///
    /// A name is taken only when it is the whole attribute or introduces its
    /// arguments, so `[Foo(Flags)]` does not look like `[Flags]`.
    /// </summary>
    private void SkipAttributes()
    {
        _attributes.Clear();
        _attributeParts.Clear();

        while (At(Tok.LBracket))
        {
            int depth = 0;
            string target = "";

            do
            {
                if (At(Tok.LBracket))
                {
                    depth++;

                    // WHAT IT IS AIMED AT, when it says: `[return: X]` is an
                    // attribute on the RESULT and not on the member, and C#
                    // writes the two the same way but for the word in front.
                    int j = _i + 1;
                    target = "";

                    if (depth == 1 && j + 1 < _t.Count && _t[j + 1].Kind == Tok.Colon
                        && _t[j].Kind is Tok.Ident or Tok.KwReturn)
                    {
                        target = _t[j].Kind == Tok.KwReturn ? "return" : _t[j].Text;
                        j += 2;
                    }

                    if (depth == 1) RecordAttribute(j, target);
                }
                // EVERY ATTRIBUTE IN THE SECTION: `[NotNull, CallerMemberName]`
                // is two, and the second is as much the parameter's as the first.
                else if (At(Tok.Comma) && depth == 1)
                {
                    RecordAttribute(_i + 1, target);
                }
                else if (At(Tok.RBracket))
                {
                    depth--;
                }
                else if (At(Tok.End))
                {
                    throw Error("this attribute has no closing ']'");
                }
                _i++;
            }
            while (depth > 0);
        }
    }

    /// <summary>
    /// The attribute whose name starts at token <paramref name="j"/>, by the
    /// last part of its name: `System.Runtime.CompilerServices.CallerMemberName`
    /// is CallerMemberName, as a `using` would have let it be written.
    /// </summary>
    private void RecordAttribute(int j, string target)
    {
        while (j + 2 < _t.Count && _t[j].Kind == Tok.Ident && _t[j + 1].Kind == Tok.Dot && _t[j + 2].Kind == Tok.Ident)
        {
            j += 2;
        }

        if (j < _t.Count && _t[j].Kind == Tok.Ident
            && j + 1 < _t.Count
            && _t[j + 1].Kind is Tok.RBracket or Tok.LParen or Tok.Comma)
        {
            if (target.Length == 0)
            {
                _attributes.Add(_t[j].Text);
            }
            AttributeRef written = new() { Target = target, Name = _t[j].Text };

            written.Arguments.AddRange(Arguments(j + 1));
            _attributeParts.Add(written);
        }
    }

    /// <summary>
    /// An attribute's first argument as the one word it amounts to.
    ///
    /// `(false)` is "false", `("path")` is "path" and `(nameof(path))` is
    /// "path" as well -- the last name or literal before the argument ends is
    /// what every one of those spellings comes down to.
    /// </summary>
    private List<AttributeArgument> Arguments(int open)
    {
        List<AttributeArgument> arguments = new();

        if (open >= _t.Count || _t[open].Kind != Tok.LParen)
        {
            return arguments;
        }

        int depth = 0;
        int from = open + 1;

        for (int j = open; j < _t.Count; j++)
        {
            if (_t[j].Kind == Tok.LParen)
            {
                depth++;
                continue;
            }

            if (_t[j].Kind == Tok.RParen)
            {
                depth--;

                if (depth == 0)
                {
                    Argument(from, j, arguments);
                    return arguments;
                }
                continue;
            }

            if (_t[j].Kind == Tok.Comma && depth == 1)
            {
                Argument(from, j, arguments);
                from = j + 1;
            }
        }
        return arguments;
    }

    /// <summary>
    /// One argument of an attribute, reduced to the one word it amounts to.
    ///
    /// `false` is "false", `"path"` is "path" and `nameof(path)` is "path" as
    /// well -- the last name or literal in it is what every one of those
    /// spellings comes down to. `Closed = true` is that, with a name in
    /// front, which is how C# writes an attribute's named argument.
    /// </summary>
    private void Argument(int from, int to, List<AttributeArgument> into)
    {
        if (from >= to)
        {
            return;                     // `[Foo()]`, or a trailing comma
        }

        string? name = null;

        if (to - from > 2 && _t[from].Kind == Tok.Ident && _t[from + 1].Kind == Tok.Assign)
        {
            name = _t[from].Text;
            from += 2;
        }

        string? last = null;
        List<string> words = new();

        for (int j = from; j < to; j++)
        {
            if (_t[j].Kind is Tok.Ident or Tok.Int)
            {
                words.Add(_t[j].Text);
            }
            if (_t[j].Kind is Tok.Ident or Tok.Str)
            {
                last = _t[j].Text;
            }
            else if (_t[j].Kind is Tok.KwTrue or Tok.KwFalse)
            {
                last = _t[j].Kind == Tok.KwTrue ? "true" : "false";
            }
        }

        if (last is not null)
        {
            into.Add(new AttributeArgument { Name = name, Value = last, Words = words });
        }
    }

    /// <summary>
    /// The attributes just read, as nodes the rest of the compiler can keep.
    ///
    /// `_attributeParts` is scratch and is cleared by the next attribute
    /// list, so anything that wants to remember an attribute copies it out
    /// before parsing anything else.
    /// </summary>
    private List<AttributeRef> CapturedAttributes()
    {
        List<AttributeRef> captured = new();

        foreach (AttributeRef written in _attributeParts)
        {
            AttributeRef copy = new() { Target = written.Target, Name = written.Name };

            copy.Arguments.AddRange(written.Arguments);
            captured.Add(copy);
        }
        return captured;
    }

    private Mods ParseMods()
    {
        Mods m = Mods.None;

        // BEFORE THE MODIFIERS, which is where C# puts them and why skipping
        // here covers both a type declaration and a member: both begin by
        // asking for modifiers.
        SkipAttributes();

        while (true)
        {
            switch (Cur.Kind)
            {
                case Tok.KwPublic:    m |= Mods.Public;    break;
                case Tok.KwPrivate:   m |= Mods.Private;   break;
                case Tok.KwProtected: m |= Mods.Protected; break;
                case Tok.KwInternal:  m |= Mods.Internal;  break;
                case Tok.KwStatic:    m |= Mods.Static;    break;
                case Tok.KwAbstract:  m |= Mods.Abstract;  break;
                case Tok.KwVirtual:   m |= Mods.Virtual;   break;
                case Tok.KwOverride:  m |= Mods.Override;  break;
                case Tok.KwSealed:    m |= Mods.Sealed;    break;
                case Tok.KwReadonly:  m |= Mods.Readonly;  break;
                case Tok.KwConst:     m |= Mods.Const;     break;
                case Tok.KwVolatile:  m |= Mods.Volatile;  break;
                // `new` as a modifier hides an inherited member. It is only a
                // modifier when what follows starts a member; `new Foo()` as
                // an expression never reaches here at member position, but
                // the check keeps a statement-level caller safe too.
                case Tok.KwNew when StartsMember(_t[_i + 1].Kind) && _t[_i + 1].Kind != Tok.LParen:
                    m |= Mods.New;
                    break;
                // CONTEXTUAL, matched by its text: see the lexer.
                case Tok.Ident when Cur.Text == "async" && StartsMember(_t[_i + 1].Kind):
                    m |= Mods.Async;
                    break;

                // `unsafe` is ACCEPTED AND CARRIED rather than enforced.
                //
                // In C# it gates pointers behind a compiler switch, and the
                // gate is a policy about projects rather than a fact about the
                // machine. This machine's whole library touches raw memory --
                // that is what a standard library on a real computer does -- so
                // refusing pointers outside an unsafe context would mean every
                // file carrying the word for no benefit.
                //
                // Taken because C# source says it and must still compile. The
                // day there is a reason to enforce it, the flag is already
                // here and only the check is missing.
                case Tok.Ident when Cur.Text == "unsafe" && StartsMember(_t[_i + 1].Kind):
                    m |= Mods.Unsafe;
                    break;

                // CONTEXTUAL, matched by its text. 'required' is an ordinary
                // word and code uses it as a name -- making it a keyword is how
                // 'init' broke the kernel's serial driver, which has a local
                // called init. It means a modifier only where a modifier can
                // appear and nothing anywhere else.
                case Tok.Ident when Cur.Text == "required" && StartsMember(_t[_i + 1].Kind):
                    m |= Mods.Required;
                    break;

                // Native interop has no direct equivalent inside a CORSAC
                // process. Preserve the declaration and provide a safe stub in
                // FinishMethod; platform libraries may replace it later.
                case Tok.Ident when Cur.Text == "extern" && StartsMember(_t[_i + 1].Kind):
                    m |= Mods.Extern;
                    break;

                // `partial` is carried to the compilation-unit merge. C# says
                // the rest of this type may live in another file; dropping the
                // word made the legal second part a duplicate declaration.
                case Tok.Ident when Cur.Text == "partial" && StartsMember(_t[_i + 1].Kind):
                    m |= Mods.Partial;
                    break;

                default: return m;
            }
            _i++;
        }
    }

    /// Whether a type declaration begins here, without consuming anything.
    ///
    /// A member and a nested type start the same way -- attributes, then
    /// modifiers -- so telling them apart means looking PAST both to the word
    /// that decides it. Nothing here may consume, because the caller has to be
    /// able to parse a member instead if the answer is no.
    private bool StartsTypeDecl()
    {
        int at = _i;

        Token Seen(int j) => _t[Math.Min(j, _t.Count - 1)];

        // Past the attributes, counting brackets so `[A(new[] { 1 })]` does not
        // end the attribute early.
        while (Seen(at).Kind == Tok.LBracket)
        {
            int depth = 0;

            do
            {
                if (Seen(at).Kind == Tok.LBracket)
                {
                    depth++;
                }
                else if (Seen(at).Kind == Tok.RBracket)
                {
                    depth--;
                }
                else if (Seen(at).Kind == Tok.End)
                {
                    return false;
                }
                at++;
            }
            while (depth > 0);
        }

        // Past the modifiers. The contextual ones are matched by text, as they
        // are in ParseMods, so a field called `required` is still a field.
        while (true)
        {
            Tok k = Seen(at).Kind;

            if (k is Tok.KwPublic or Tok.KwPrivate or Tok.KwProtected or Tok.KwInternal
                  or Tok.KwStatic or Tok.KwAbstract or Tok.KwVirtual or Tok.KwOverride
                  or Tok.KwSealed or Tok.KwReadonly or Tok.KwConst or Tok.KwVolatile
             || (k == Tok.Ident && Seen(at).Text is "unsafe" or "required" or "partial" or "async" or "extern"
                 && StartsMember(Seen(at + 1).Kind)))
            {
                at++;
                continue;
            }
            break;
        }

        if (Seen(at).Kind is Tok.KwClass or Tok.KwInterface or Tok.KwStruct or Tok.KwEnum or Tok.KwDelegate)
        {
            return true;
        }

        // `record` is contextual, exactly as ParseTypeDecl reads it.
        return Seen(at).Kind == Tok.Ident && Seen(at).Text == "record"
            && Seen(at + 1).Kind is Tok.Ident or Tok.KwClass or Tok.KwStruct;
    }

    private TypeDecl ParseTypeDecl()
    {
        int saved = _templateDepth;
        HashSet<string>? outer = _createdParams;
        _createdParams = null;
        try
        {
            TypeDecl d = ParseTypeDeclCore();
            if (_createdParams is not null)
            {
                foreach (TypeParam p in d.TypeParams)
                {
                    p.Made |= _createdParams.Contains(p.Name);
                }
            }
            return d;
        }
        finally
        {
            _templateDepth = saved;
            if (outer is not null && _createdParams is not null) outer.UnionWith(_createdParams);
            _createdParams = outer ?? _createdParams;
        }
    }

    /// <summary>
    /// The type names written as `CreateInstance<T>`'s argument in the type
    /// being parsed, its nested types' included: a type parameter among them
    /// is constructed by its declaration (TypeParam.Made).
    /// </summary>
    private HashSet<string>? _createdParams;

    private TypeDecl ParseTypeDeclCore()
    {
        Token start = Cur;
        Mods mods = ParseMods();
        if (Take(Tok.KwDelegate)) return ParseDelegateDeclaration(start, mods);

        // A RECORD IS A CLASS, and `record` is contextual so that nothing which
        // already uses the word as a name stops compiling.
        bool isRecord = At(Tok.Ident) && Cur.Text == "record"
                     && _t[_i + 1].Kind is Tok.Ident or Tok.KwClass or Tok.KwStruct;

        // A RECORD STRUCT IS A STRUCT. `record` says how it is written -- one
        // line of parameters instead of a page of properties -- and class or
        // struct says whether it is held by reference or by value, which is a
        // different question and the one that decides its layout.
        TypeKind recorded = TypeKind.Class;

        if (isRecord)
        {
            _i++;

            if (Take(Tok.KwStruct))
            {
                recorded = TypeKind.Struct;
            }
            else
            {
                // `record class X` is legal C# and means the same thing as
                // `record X`.
                Take(Tok.KwClass);
            }
        }

        TypeKind kind = isRecord ? recorded : Cur.Kind switch
        {
            Tok.KwClass     => TypeKind.Class,
            Tok.KwInterface => TypeKind.Interface,
            Tok.KwStruct    => TypeKind.Struct,
            Tok.KwEnum      => TypeKind.Enum,
            _ => throw Error($"expected a type declaration, found '{Cur.Text}'"),
        };

        if (!isRecord)
        {
            _i++;
        }

        string name = Expect(Tok.Ident, "a type name").Text;
        TypeDecl decl = new()
        {
            Kind = kind, Name = name, Mods = mods, Namespace = _namespace, Scope = _fileScope,
            Line = start.Line, Col = start.Col,
        };

        if (_attributes.Count > 0) decl.WritableAttributes.AddRange(_attributes);
        if (_attributeParts.Count > 0) decl.WritableAttributeParts.AddRange(CapturedAttributes());

        // WHERE THIS ONE WAS WRITTEN, before its own body moves the path on.
        // Empty means the top level, and a top-level type has no outer.
        string outer = _typePath;
        decl.Outer = outer.Length == 0 ? null : outer;
        _typePath = outer.Length == 0 ? name : outer + "." + name;

        if (At(Tok.Lt)) ParseTypeParams(decl.WritableTypeParams);
        // A TYPE INSIDE A GENERIC ONE TAKES THE OUTER'S PARAMETERS FIRST:
        // `class Outer<T> { class Inner { T Value; } }` makes Outer.Inner<T>,
        // whose every use is given Outer's argument (FinishFamily).
        List<string> enclosing = _enclosingParams;
        if (enclosing.Count > 0)
        {
            for (int k = enclosing.Count - 1; k >= 0; k--) decl.WritableTypeParams.Insert(0, new TypeParam { Name = enclosing[k], Line = start.Line, Col = start.Col });
            decl.OuterParams = enclosing.Count;
        }
        _enclosingParams = decl.TypeParams.Select(p => p.Name).ToList();
        if (decl.TypeParams.Count > 0) _templateDepth++;

        // THE POSITIONAL PARAMETERS, which are the whole point of a record: a
        // list of things it holds, written once, becoming both a constructor
        // and the members it fills in.
        List<Param> positional = new();

        // THE SAME PARSER AS EVERY OTHER PARAMETER LIST, deliberately. This had
        // its own loop, which is how it came to be the one list in the language
        // that could not take a default -- `record ParamSym(..., bool ByRef =
        // false)` in the compiler's own Binder.cs was refused, while the very
        // same text in a method signature was accepted.
        //
        // A decision written down twice drifts, and today that has now happened
        // four times in this file alone: the `is` dispatch, the pattern
        // combining loop, the value-pattern predicate, and this. Each one was
        // found by something DOWNSTREAM breaking rather than by the copy itself
        // complaining, because a copy that has fallen behind is still perfectly
        // valid code.
        if (isRecord && At(Tok.LParen))
        {
            List<Param>? positionalRead = positional;
            ParseParams(ref positionalRead);
        }

        if (Take(Tok.Colon))
        {
            do
            {
                decl.WritableBases.Add(ParseTypeRef());

                // A RECORD'S BASE IS CONSTRUCTED, and the arguments are written
                // where the base is named: `record RegPlace(VReg Reg, Type
                // Type) : Place(Type)`. They belong to the constructor the
                // record generates, which does not exist yet, so they are kept
                // on the declaration until it does.
                if (isRecord && At(Tok.LParen))
                {
                    _i++;
                    int spanMark = _spans.Count;
                    if (Source is not null) { _spans.Add(-1); _spans.Add(-1); }

                    if (!At(Tok.RParen))
                    {
                        do
                        {
                            if (At(Tok.Ident) && Ahead().Kind == Tok.Colon)
                            {
                                decl.WritableBaseArgNames.Add(_t[_i++].Text);
                                _i++;
                            }
                            else
                            {
                                decl.WritableBaseArgNames.Add(null);
                            }
                            int argumentAt = ArgumentStart();
                            decl.WritableBaseArgs.Add(ParseArg());
                            if (Source is not null) { _spans.Add(argumentAt); _spans.Add(End(_i - 1)); }
                        }
                        while (Take(Tok.Comma));
                    }

                    Expect(Tok.RParen, "')' after the base constructor's arguments");
                    if (Source is not null)
                    {
                        decl.BaseSpans = _spans.GetRange(spanMark, _spans.Count - spanMark).ToArray();
                        decl.BaseSource = Source;
                        _spans.RemoveRange(spanMark, _spans.Count - spanMark);
                    }
                }
            }
            while (Take(Tok.Comma));
        }

        ParseConstraints(decl.TypeParams);

        // `record X(...);` -- a body is optional when the parameters said
        // everything. A semicolon closes it instead of a brace.
        if (isRecord && Take(Tok.Semi))
        {
            AddRecordMembers(decl, positional, start);
            decl.SourceFrom = start.Pos;
            decl.SourceTo = _t[_i - 1].Pos + 1;
            _typePath = outer;
            _enclosingParams = enclosing;
            return decl;
        }

        Expect(Tok.LBrace, "'{' to open the type body");

        if (kind == TypeKind.Enum)
        {
            while (!At(Tok.RBrace) && !At(Tok.End))
            {
                SkipAttributes();

                if (At(Tok.RBrace) || At(Tok.End))
                {
                    break;                  // a trailing comma, then the brace
                }

                List<AttributeRef> on = CapturedAttributes();
                Token m = Cur;
                string member = Expect(Tok.Ident, "an enum member").Text;
                Expr? value = Take(Tok.Assign) ? ParseExpr() : null;
                EnumMember declared = new() { Name = member, Value = value, Line = m.Line, Col = m.Col };

                declared.WritableAttributes.AddRange(on);
                decl.WritableEnumMembers.Add(declared);

                if (!Take(Tok.Comma))
                {
                    break;
                }
            }
        }
        else
        {
            while (!At(Tok.RBrace) && !At(Tok.End))
            {
                // A NESTED TYPE, hoisted out to sit beside the one it was
                // written inside.
                //
                // The table of types is flat, so a nested type is KEYED by the
                // path C# names it with -- `Outer.Inner` -- which it carries in
                // its Outer. That path is what makes it a distinct type rather
                // than a redeclaration of any other `Inner`: this compiler's
                // own Assembler and ImageFile each hold a `Section`, and under
                // simple names alone the second was "declared more than once".
                //
                // ParseTypeDecl reads the path from _typePath, which is why
                // nothing is assigned here.
                if (StartsTypeDecl())
                {
                    _nested.Add(ParseTypeDecl());
                    continue;
                }

                _inInterface = decl.Kind == TypeKind.Interface;

                MemberDecl member = ParseMember(name);

                member.Scope = _fileScope;
                member.Namespace = _namespace;
                decl.Members.Add(member);

                // Its generic local functions, as members of their own.
                foreach (MethodDecl hoisted in _hoisted)
                {
                    hoisted.Scope = _fileScope;
                    hoisted.Namespace = _namespace;
                    decl.Members.Add(hoisted);
                }
                _hoisted.Clear();

                // `const int A = 0, B = 1;` is several fields written once;
                // the extras arrive with the first and become members here,
                // so nothing below this ever sees a declaration with more
                // than one name in it.
                if (member is FieldDecl { More.Count: > 0 } several)
                {
                    foreach (FieldDecl also in several.More)
                    {
                        also.Scope = _fileScope;
                        also.Namespace = _namespace;
                    }

                    decl.Members.AddRange(several.More);
                    several.ForgetMore();
                }
                _inInterface = false;
            }
        }

        Token close = Expect(Tok.RBrace, "'}' to close the type body");

        AddRecordMembers(decl, positional, start);

        // The whole declaration, brace to brace, so a generic can be written
        // into a header exactly as it was written here.
        decl.SourceFrom = start.Pos;
        decl.SourceTo = close.Pos + 1;
        _typePath = outer;
        _enclosingParams = enclosing;
        return decl;
    }

    private TypeDecl ParseDelegateDeclaration(Token start, Mods mods)
    {
        // `delegate ref int D(...)` RETURNS A VARIABLE, as a method written so
        // does: its Invoke carries Mods.RefReturn, and so does every lambda's
        // made for it (Binder.CheckLambda).
        Mods byReference = Mods.None;
        if (Take(Tok.KwRef))
        {
            byReference = Mods.RefReturn;
            if (Take(Tok.KwReadonly)) byReference |= Mods.RefReadonlyReturn;
        }
        TypeRef returns = ParseTypeRef();
        string name = Expect(Tok.Ident, "a delegate name").Text;
        TypeDecl declaration = new()
        {
            Kind = TypeKind.Interface, IsDelegate = true, Name = name, Mods = mods,
            Namespace = _namespace, Scope = _fileScope, Outer = _typePath.Length == 0 ? null : _typePath,
            File = _file, Line = start.Line, Col = start.Col, SourceFrom = start.Pos,
        };
        if (_attributes.Count > 0) declaration.WritableAttributes.AddRange(_attributes);
        if (_attributeParts.Count > 0) declaration.WritableAttributeParts.AddRange(CapturedAttributes());
        if (At(Tok.Lt)) ParseTypeParams(declaration.WritableTypeParams);
        if (_enclosingParams.Count > 0)
        {
            for (int k = _enclosingParams.Count - 1; k >= 0; k--) declaration.WritableTypeParams.Insert(0, new TypeParam { Name = _enclosingParams[k], Line = start.Line, Col = start.Col });
            declaration.OuterParams = _enclosingParams.Count;
        }
        MethodDecl invoke = new()
        {
            Name = "Invoke", Returns = returns, Mods = Mods.Public | Mods.Abstract | byReference,
            File = _file, Scope = _fileScope, Namespace = _namespace, Line = start.Line, Col = start.Col,
        };
        { List<Param>? read = null; ParseParams(ref read); invoke.AdoptParams(read); } ParseConstraints(declaration.TypeParams);
        declaration.Members.Add(invoke);
        declaration.SourceTo = Expect(Tok.Semi, "';' after delegate declaration").Pos + 1;
        TypeDecl? multicast = Multicast(declaration, invoke);
        if (multicast is not null)
        {
            _nested.Add(multicast);

            // EVERY PARAMETER, the outer type's first: a delegate nested in
            // a generic type is that type's parameters' and its own
            // (Box<T>.Make is Box.Make<T>), and so is its multicast. Named by
            // its own alone, the multicast here was the open template, a
            // Combine no unit made.
            string own = string.Join(", ", declaration.TypeParams.Select(tp => tp.Name));
            string args = own.Length == 0 ? "" : "<" + own + ">";
            string self = name + args;
            string helper = name + "__Multicast" + args;
            System.Text.StringBuilder forward = new();

            // A SINGLE DELEGATE COMBINES THROUGH ITS TYPE: `a += b` and
            // Delegate.Combine(a, b) ask a.CombineImpl(b) (__Delegates,
            // Delegate.Combine), and a closure is a class the compiler made for
            // one lambda, which knows nothing of multicasts. These two are the
            // delegate type's own answers -- explicit implementations of
            // Delegate's, written into the interface -- and the binder gives
            // every closure of the type them (Binder.DelegateMembers). The
            // multicast class answers both itself. That the two delegates are
            // of one type Delegate.Combine has already seen.
            // `follow` IS this type: Delegate.Combine and Remove have seen
            // both of one (Runtime.SameDelegateType), so it is taken as it is
            // (Sys.As). A cast tested it here, where a generic delegate's T is
            // a shared copy's word, against the canonical type -- an
            // Action<string>'s closure is no Action<__canon> -- and refused it.
            forward.Append("System.Delegate? System.Delegate.CombineImpl(System.Delegate follow) { return ")
                   .Append(helper).Append(".Combine(this, Sys.As<").Append(self).Append(">(follow)); }\n");
            forward.Append("System.Delegate? System.Delegate.RemoveImpl(System.Delegate value) { return ")
                   .Append(helper).Append(".Remove(this, Sys.As<").Append(self).Append(">(value)); }\n");

            Parser sub = new(Lexer.Tokenize("interface __Forward { " + forward.ToString() + " }", _file), _file, SkipWritten(declaration.TypeParams.Count > 0), _includeTemplateBodies);
            CompilationUnit wrapped = sub.ParseUnit();
            List<MemberDecl> statics = wrapped.Types[0].Members;
            foreach (MemberDecl member in statics)
            {
                member.File = _file;
            }
            Adopt(statics);
            declaration.Members.AddRange(statics);
        }
        return declaration;
    }

    /// <summary>
    /// A type as source text that names it again, for the classes written
    /// here from text: TypeRef's own spelling, with the pointer stars it
    /// leaves out. Null for what that text cannot say -- a function pointer,
    /// or a nullable written inside an array's element -- and the delegate
    /// is then left without its multicast, as it was before any of this.
    /// </summary>
    private static string? Spelt(TypeRef type)
    {
        if (type.IsFunctionPointer || type.InnerNullable > 0)
        {
            return null;
        }
        foreach (TypeRef argument in type.Args)
        {
            if (Spelt(argument) is null) return null;
        }
        if (type.PointerDepth > 0)
        {
            if (type.ArrayRank > 0 || type.Nullable) return null;
            return type.ToString() + new string('*', type.PointerDepth);
        }
        return type.ToString();
    }

    /// <summary>
    /// The multicast form of a delegate, synthesised beside it: a class that
    /// implements the delegate's interface by invoking a list of them in
    /// order, with Combine and Remove for += and -=. Built from source text
    /// and parsed, because the rules are easier to read as C# than as a tree
    /// built by hand; the text is made from the declaration as parsed -- its
    /// Invoke's return type and parameters -- so a parameter whose type has a
    /// comma in it, a tuple returned, or a default value cannot unsettle it.
    ///
    /// AS .NET's MulticastDelegate DOES IT, all of it:
    ///
    /// - Invoke runs every target in order and answers what the LAST one
    ///   answers. An exception from one stops the rest, as any call's does.
    ///   An out or ref parameter is passed to each in turn -- the same
    ///   variable, so the caller sees what the last target left in it.
    /// - Combine flattens: a multicast of multicasts is one list.
    /// - Remove takes out the LAST contiguous run of the removed delegate's
    ///   targets -- one target, or a whole multicast out of the middle of
    ///   another -- and hands back the delegate it was given when the run is
    ///   not there; what is left of one target is that target, and of none,
    ///   null. Targets compare by their own Equals: the same method on the
    ///   same target (Runtime.DelegateEquals, Runtime.GroupEquals).
    /// - Equals and GetHashCode: equal to a multicast of the same type with
    ///   equal targets in the same order; GetInvocationList the targets.
    /// </summary>
    private TypeDecl? Multicast(TypeDecl delegateDecl, MethodDecl invoke)
    {
        string? returns = invoke.Returns is null ? null : Spelt(invoke.Returns);
        if (returns is null)
        {
            return null;
        }

        List<string> parameters = new();
        List<string> names = new();
        foreach (Param p in invoke.Params)
        {
            if (Spelt(p.Type) is not string spelt)
            {
                return null;
            }
            // HOW IT IS DECLARED and how it is passed on: `in` is passed as a
            // value is, the address taken for it (ParseParams); ref and out
            // are passed as they came, so every target writes the caller's
            // variable.
            string how = p.IsOut ? "out " : p.IsReadOnlyRef ? "in " : p.IsRef ? "ref " : p.IsParams ? "params " : "";
            parameters.Add(how + spelt + " " + p.Name);
            names.Add((p.IsOut ? "out " : p.IsRef && !p.IsReadOnlyRef ? "ref " : "") + p.Name);
        }

        string name = delegateDecl.Name;
        // A GENERIC DELEGATE HAS A GENERIC MULTICAST: `EventHandler<TEventArgs>`
        // is combined by `EventHandler__Multicast<TEventArgs>`, specialised
        // with it. Where a TYPE is meant the parameters go with the name.
        // ITS OWN, not those of a generic type it is written inside: the
        // multicast is written inside that type too, where they are in scope.
        List<TypeParam> own = delegateDecl.TypeParams.Skip(delegateDecl.OuterParams).ToList();
        string typeParams = own.Count == 0 ? ""
            : "<" + string.Join(", ", own.Select(tp => tp.Name)) + ">";
        string args = string.Join(", ", names);
        string signature = string.Join(", ", parameters);
        string ctor = name + "__Multicast";
        string m = ctor + typeParams;
        string d = name + typeParams;
        System.Text.StringBuilder src = new();
        if (!string.IsNullOrEmpty(_namespace)) src.Append("namespace ").Append(_namespace).Append(";\n");
        src.Append("public sealed class ").Append(m).Append(" : ").Append(d).Append(", System.IMulticastDelegate\n{\n");
        src.Append("    public ").Append(d).Append("[] Items;\n");
        src.Append("    public ").Append(ctor).Append("(").Append(d).Append("[] items) { Items = items; }\n");

        // THE FIRST TARGET OUTSIDE THE LOOP, for a result and for an out
        // parameter alike: a multicast always has at least two (only
        // Combine and Remove make one), the first answer starts what is
        // returned -- `default` would be null for a non-nullable class, which
        // the declared return type does not allow -- and an out parameter is
        // then written before the method can return.
        // A delegate that returns by reference answers, combined, the
        // variable its last target does -- C#'s rule for any result -- and
        // its Invoke returns it as the delegate's does.
        string refKind = (invoke.Mods & Mods.RefReadonlyReturn) != 0 ? "ref readonly "
                       : (invoke.Mods & Mods.RefReturn) != 0 ? "ref " : "";
        src.Append("    public ").Append(refKind).Append(returns).Append(" Invoke(").Append(signature).Append(")\n    {\n");
        // Its locals are named as no parameter is: a delegate's own `i` or
        // `last` would otherwise be declared twice in the one method.
        if (returns == "void")
        {
            src.Append("        Items[0].Invoke(").Append(args).Append(");\n");
            src.Append("        for (int __i = 1; __i < Items.Length; __i++) Items[__i].Invoke(").Append(args).Append(");\n");
        }
        else if (refKind.Length > 0)
        {
            src.Append("        for (int __i = 0; __i < Items.Length - 1; __i++) Items[__i].Invoke(").Append(args).Append(");\n");
            src.Append("        return ref Items[Items.Length - 1].Invoke(").Append(args).Append(");\n");
        }
        else
        {
            src.Append("        ").Append(returns).Append(" __last = Items[0].Invoke(").Append(args).Append(");\n");
            src.Append("        for (int __i = 1; __i < Items.Length; __i++) __last = Items[__i].Invoke(").Append(args).Append(");\n");
            src.Append("        return __last;\n");
        }
        src.Append("    }\n");

        src.Append("    public static ").Append(d).Append("? Combine(").Append(d).Append("? a, ").Append(d).Append("? b)\n    {\n");
        src.Append("        if (a == null) return b;\n        if (b == null) return a;\n");
        src.Append("        ").Append(d).Append("[] x = a is ").Append(m).Append(" ma ? ma.Items : new ").Append(d).Append("[] { a };\n");
        src.Append("        ").Append(d).Append("[] y = b is ").Append(m).Append(" mb ? mb.Items : new ").Append(d).Append("[] { b };\n");
        src.Append("        ").Append(d).Append("[] all = new ").Append(d).Append("[x.Length + y.Length];\n");
        src.Append("        for (int i = 0; i < x.Length; i++) all[i] = x[i];\n");
        src.Append("        for (int i = 0; i < y.Length; i++) all[x.Length + i] = y[i];\n");
        src.Append("        return new ").Append(m).Append("(all);\n    }\n");

        src.Append("    public static ").Append(d).Append("? Remove(").Append(d).Append("? a, ").Append(d).Append("? b)\n    {\n");
        src.Append("        if (a == null) return null;\n        if (b == null) return a;\n");
        src.Append("        ").Append(d).Append("[] have = a is ").Append(m).Append(" ma ? ma.Items : new ").Append(d).Append("[] { a };\n");
        src.Append("        ").Append(d).Append("[] take = b is ").Append(m).Append(" mb ? mb.Items : new ").Append(d).Append("[] { b };\n");
        src.Append("        for (int at = have.Length - take.Length; at >= 0; at--)\n        {\n");
        src.Append("            bool found = true;\n");
        src.Append("            for (int k = 0; k < take.Length; k++) { if (!((object)have[at + k]).Equals(take[k])) { found = false; break; } }\n");
        src.Append("            if (!found) continue;\n");
        src.Append("            int left = have.Length - take.Length;\n");
        src.Append("            if (left == 0) return null;\n");
        src.Append("            if (left == 1) return have[at == 0 ? have.Length - 1 : 0];\n");
        src.Append("            ").Append(d).Append("[] rest = new ").Append(d).Append("[left];\n");
        src.Append("            for (int i = 0; i < at; i++) rest[i] = have[i];\n");
        src.Append("            for (int i = at + take.Length; i < have.Length; i++) rest[i - take.Length] = have[i];\n");
        src.Append("            return new ").Append(m).Append("(rest);\n        }\n");
        src.Append("        return a;\n    }\n");

        src.Append("    public System.Delegate[] GetInvocationList()\n    {\n");
        src.Append("        System.Delegate[] list = new System.Delegate[Items.Length];\n");
        src.Append("        for (int i = 0; i < Items.Length; i++) list[i] = Items[i];\n");
        src.Append("        return list;\n    }\n");
        // Taken as the type, not tested (Sys.As): Delegate.Combine and Remove
        // have seen both of one, and this class's code is shared by every
        // reference argument, where a cast tests the canonical type.
        src.Append("    public System.Delegate? CombineImpl(System.Delegate follow) { return Combine(this, Sys.As<").Append(d).Append(">(follow)); }\n");
        src.Append("    public System.Delegate? RemoveImpl(System.Delegate value) { return Remove(this, Sys.As<").Append(d).Append(">(value)); }\n");
        src.Append("    public int InvocationCount() { return Items.Length; }\n");
        src.Append("    public object InvocationAt(int index) { return Items[index]; }\n");
        src.Append("    public override bool Equals(object? obj) { return __Delegates.Equal(this, obj); }\n");
        src.Append("    public override int GetHashCode() { return __Delegates.Hash(this); }\n");
        src.Append("}\n");
        string generated = src.ToString();
        Parser sub = new(Lexer.Tokenize(generated, _file), _file, SkipWritten(delegateDecl.TypeParams.Count > 0), _includeTemplateBodies) { Source = generated };
        CompilationUnit unit = sub.ParseUnit();
        if (unit.Types.Count != 1) return null;
        TypeDecl made = unit.Types[0];
        // The declaration index slices a file by each type's source span and
        // re-parses the slice; this class has no text of its own, so it
        // claims the delegate's span and is re-created by re-parsing that.
        made.SourceFrom = delegateDecl.SourceFrom;
        made.SourceTo = delegateDecl.SourceTo;
        made.File = _file;
        made.Scope = _fileScope;
        // A DELEGATE WRITTEN INSIDE A GENERIC TYPE has that type's parameters
        // (`class Box<T> { public delegate T Make(); }`), and so does its
        // multicast: nested beside it, with the same outer parameters first,
        // so that FinishFamily writes both names with them wherever the
        // family names either -- `Box<T>.Make__Multicast` holding
        // `Box<T>.Make`s and answering a T.
        if (delegateDecl.OuterParams > 0)
        {
            made.Outer = delegateDecl.Outer;
            for (int k = delegateDecl.OuterParams - 1; k >= 0; k--)
            {
                TypeParam outer = delegateDecl.TypeParams[k];
                made.WritableTypeParams.Insert(0, new TypeParam { Name = outer.Name, Line = outer.Line, Col = outer.Col });
            }
            made.OuterParams = delegateDecl.OuterParams;
        }
        Adopt(made.Members);
        return made;
    }

    /// <summary>
    /// MEMBERS MADE FROM GENERATED SOURCE READ NAMES FROM THE FILE THEY ARE
    /// FOR. A sub-parser has no using directives and no namespace of its own,
    /// and each member carries the scope it was parsed in: a record's
    /// equality over a field of type `Operand` then found `Operand` only while
    /// one type in the whole program had that name, and with a second one --
    /// this compiler has an IR Operand and an assembler Operand -- the record
    /// did not compile at all.
    /// </summary>
    private void Adopt(IEnumerable<MemberDecl> members)
    {
        foreach (MemberDecl member in members)
        {
            member.Scope = _fileScope;
            member.Namespace = _namespace;
        }
    }

    /// Turns a record's positional parameters into members.
    ///
    /// ONE PROPERTY AND ONE CONSTRUCTOR PARAMETER EACH, which is what a record
    /// is: a list of things it holds, written once instead of three times. C#
    /// generates get/init properties, so these are get/init too -- a record's
    /// parameters are meant to be read afterwards and not written.
    ///
    /// DECONSTRUCT AND TOSTRING COME WITH IT, because both say only what the
    /// positional list already said: the parameters in order, and the same
    /// parameters printed. `var (x, y) = point` and printing a record are what
    /// records are FOR, and a record without them is a record in spelling only.
    /// Either is skipped when the body declared one, so a hand-written
    /// Deconstruct or ToString still wins.
    ///
    /// AND VALUE EQUALITY, as C# gives every record class: Equals, the
    /// object override, GetHashCode, == and !=, member by member through
    /// EqualityComparer<T>.Default, the runtime types compared first (C#'s
    /// EqualityContract). It was left out once, on the ground that this
    /// compiler's own records are held by identity -- but the compiler runs
    /// on .NET, where they have value equality already, and the tables that
    /// want identity say so with ReferenceEqualityComparer.
    private void AddRecordMembers(TypeDecl decl, List<Param> positional, Token start)
    {
        if (positional.Count == 0)
        {
            return;
        }

        CtorInit? chain = null;

        if (decl.BaseArgs.Count > 0)
        {
            chain = new CtorInit { IsThis = false, Spans = decl.BaseSpans, Source = decl.BaseSource, Line = start.Line, Col = start.Col };
            chain.Args.AddRange(decl.BaseArgs);
            chain.WritableArgNames.AddRange(decl.BaseArgNames);
        }

        MethodDecl ctor = new()
        {
            Name = decl.Name, Mods = Mods.Public, Returns = null, IsCtor = true,
            Body = new Block { Line = start.Line, Col = start.Col },
            Init = chain,
            Line = start.Line, Col = start.Col,
        };

        foreach (Param p in positional)
        {
            // The property, which is what anybody reading the record sees.
            decl.Members.Add(new PropertyDecl
            {
                Name = p.Name, Mods = Mods.Public, Type = p.Type,
                Auto = true, HasSetter = true, FromRecord = true,
                Line = p.Line, Col = p.Col,
            });

            // And the constructor that fills it in. The parameter keeps the
            // name written in the positional list, which is what C# does and
            // what the base's arguments are written against: `: Place(Type)`
            // names this parameter and not the property, which does not exist
            // until the base has run. The assignment reads `this.Name = Name`
            // and is unambiguous for the same reason -- a property is reached
            // through `this`, a parameter by its bare name.
            ctor.WritableParams.Add(new Param
            {
                Name = p.Name, Type = p.Type, Default = p.Default,
                Line = p.Line, Col = p.Col,
            });

            ctor.Body.WritableStatements.Add(new ExprStmt
            {
                Expr = new AssignExpr
                {
                    Target = new MemberExpr
                    {
                        Target = new ThisExpr { Line = p.Line, Col = p.Col },
                        Name = p.Name, Line = p.Line, Col = p.Col,
                    },
                    Value = new NameExpr { Name = p.Name, Line = p.Line, Col = p.Col },
                    Line = p.Line, Col = p.Col,
                },
                Line = p.Line, Col = p.Col,
            });
        }

        decl.Members.Add(ctor);
        AddRecordDeconstruct(decl, positional, start);
        AddRecordToString(decl, positional, start);
        AddRecordEquality(decl, positional, start);
    }

    /// <summary>
    /// A record class's value equality, written as C# and parsed into it, as
    /// the compiler would have written it (C# spec, "Records": Equals(R?),
    /// Equals(object?), GetHashCode, operator == and !=). Any of them the
    /// body declared is left to the body.
    /// </summary>
    private void AddRecordEquality(TypeDecl decl, List<Param> positional, Token start)
    {
        // A record struct has the same members over its fields, with no null
        // and no runtime type to ask about: a value is exactly its type.
        bool value = decl.Kind == TypeKind.Struct;
        if (decl.Kind != TypeKind.Class && !value) return;
        string self = decl.Name + (decl.TypeParams.Count == 0 ? "" : "<" + string.Join(", ", decl.TypeParams.Select(t => t.Name)) + ">");
        string maybe = value ? self : self + "?";
        System.Text.StringBuilder src = new();
        src.Append(value ? "struct " : "class ").Append(self).Append("\n{\n");
        if (!decl.Members.Exists(m => m.Name == "Equals"))
        {
            src.Append(value ? "    public bool Equals(" : "    public virtual bool Equals(").Append(maybe).Append(" other)\n    {\n");
            if (!value)
            {
                src.Append("        if (other is null) return false;\n");
                src.Append("        if ((object)this == (object)other) return true;\n");
                src.Append("        if (GetType() != other.GetType()) return false;\n");
            }
            src.Append("        return true");
            // EqualityComparer<P>.Default.Equals, as the comparer's own body
            // asks it (DefaultEqualityComparer), written out for P: through the
            // comparer, every record's Equals ran its one shared copy, whose
            // key question is asked of any object at all -- every Equals in
            // the program a callee of every record's. Here P is known, and
            // the question is asked of a P (Lowering.KeyEqualsStub).
            foreach (Param p in positional)
                src.Append("\n            && (Sys.IsObject(this.").Append(p.Name).Append(") ? Sys.KeyEquals(this.").Append(p.Name).Append(", other.").Append(p.Name)
                   .Append(") : Sys.EqualValues(this.").Append(p.Name).Append(", other.").Append(p.Name).Append("))");
            src.Append(";\n    }\n");
            src.Append("    public override bool Equals(object? obj) { return obj is ").Append(self).Append(" other && Equals(other); }\n");
        }
        if (!decl.Members.Exists(m => m.Name == "GetHashCode"))
        {
            src.Append("    public override int GetHashCode()\n    {\n        int hash = 0;\n");
            // And EqualityComparer<P>.Default.GetHashCode, written out as Equals is.
            foreach (Param p in positional)
                src.Append("        hash = unchecked(hash * -1521134295 + (Sys.IsObject(this.").Append(p.Name).Append(") ? Sys.KeyHash(this.").Append(p.Name)
                   .Append(") : (int)Sys.HashValue(this.").Append(p.Name).Append(")));\n");
            src.Append("        return hash;\n    }\n");
        }
        if (!decl.Members.Exists(m => m.Name is "op_Equality" or "op_Inequality"))
        {
            src.Append("    public static bool operator ==(").Append(maybe).Append(" left, ").Append(maybe).Append(" right)\n    {\n");
            if (!value)
            {
                src.Append("        if ((object?)left == (object?)right) return true;\n");
                src.Append("        if (left is null || right is null) return false;\n");
            }
            src.Append("        return left.Equals(right);\n    }\n");
            src.Append("    public static bool operator !=(").Append(maybe).Append(" left, ").Append(maybe).Append(" right) { return !(left == right); }\n");
        }
        src.Append("}\n");
        string generated = src.ToString();
        Parser sub = new(Lexer.Tokenize(generated, _file), _file, SkipWritten(decl.TypeParams.Count > 0), _includeTemplateBodies) { Source = generated };
        CompilationUnit unit = sub.ParseUnit();
        if (unit.Types.Count != 1) return;
        Adopt(unit.Types[0].Members);
        decl.Members.AddRange(unit.Types[0].Members);
    }

    /// <summary>
    /// `void Deconstruct(out T a, out T b)`, which is the positional list read
    /// backwards: what went in through the constructor comes out here, in the
    /// same order.
    /// </summary>
    private static void AddRecordDeconstruct(TypeDecl decl, List<Param> positional, Token start)
    {
        if (decl.Members.Exists(m => m.Name == "Deconstruct"))
        {
            return;
        }

        MethodDecl taken = new()
        {
            Name = "Deconstruct", Mods = Mods.Public,
            Returns = new TypeRef { Name = "void", Line = start.Line, Col = start.Col },
            Body = new Block { Line = start.Line, Col = start.Col },
            Line = start.Line, Col = start.Col,
        };

        foreach (Param p in positional)
        {
            // Named apart from the property so the assignment below can say
            // which is which: `out_X = this.X`.
            taken.WritableParams.Add(new Param
            {
                Name = "out_" + p.Name, Type = p.Type, IsOut = true,
                Line = p.Line, Col = p.Col,
            });

            taken.Body.WritableStatements.Add(new ExprStmt
            {
                Expr = new AssignExpr
                {
                    Target = new NameExpr { Name = "out_" + p.Name, Line = p.Line, Col = p.Col },
                    Value = new MemberExpr
                    {
                        Target = new ThisExpr { Line = p.Line, Col = p.Col },
                        Name = p.Name, Line = p.Line, Col = p.Col,
                    },
                    Line = p.Line, Col = p.Col,
                },
                Line = p.Line, Col = p.Col,
            });
        }

        decl.Members.Add(taken);
    }

    /// <summary>
    /// `ToString()` spelled the way C# spells a record's:
    /// `Point { X = 1, Y = 2 }`.
    /// </summary>
    private static void AddRecordToString(TypeDecl decl, List<Param> positional, Token start)
    {
        if (decl.Members.Exists(m => m.Name == "ToString"))
        {
            return;
        }

        Expr text = new LiteralExpr
        {
            Kind = Lit.Str, Text = decl.Name + " { ", Line = start.Line, Col = start.Col,
        };

        for (int i = 0; i < positional.Count; i++)
        {
            Param p = positional[i];

            text = Join(text, new LiteralExpr
            {
                Kind = Lit.Str, Text = (i == 0 ? "" : ", ") + p.Name + " = ",
                Line = p.Line, Col = p.Col,
            });

            text = Join(text, new MemberExpr
            {
                Target = new ThisExpr { Line = p.Line, Col = p.Col },
                Name = p.Name, Line = p.Line, Col = p.Col,
            });
        }

        text = Join(text, new LiteralExpr
        {
            Kind = Lit.Str, Text = " }", Line = start.Line, Col = start.Col,
        });

        Block body = new() { Line = start.Line, Col = start.Col };
        body.WritableStatements.Add(new ReturnStmt { Value = text, Line = start.Line, Col = start.Col });

        decl.Members.Add(new MethodDecl
        {
            Name = "ToString", Mods = Mods.Public | Mods.Override,
            Returns = new TypeRef { Name = "string", Line = start.Line, Col = start.Col },
            Body = body, Line = start.Line, Col = start.Col,
        });
    }

    private static Expr Join(Expr left, Expr right)
        => new BinaryExpr
        {
            Op = BinOp.Add, Left = left, Right = right,
            Line = left.Line, Col = left.Col,
        };

    private void ParseTypeParams(List<TypeParam> into)
    {
        if (!Take(Tok.Lt))
        {
            return;
        }

        do
        {
            Token at = Cur;

            // `out T` and `in T`, which C# writes on an interface's parameters
            // to say which way an argument may vary. `IEnumerable<out T>` is
            // what makes a `List<FieldDecl>` an `IEnumerable<MemberDecl>`.
            Variance varies = Take(Tok.KwOut) ? Variance.Out
                            : Take(Tok.KwIn) ? Variance.In
                            : Variance.None;

            into.Add(new TypeParam
            {
                Name = Expect(Tok.Ident, "a type parameter").Text,
                Variance = varies, Line = at.Line, Col = at.Col,
            });
        }
        while (Take(Tok.Comma));

        Expect(Tok.Gt, "'>' to close the type parameters");
    }

    private void ParseConstraints(List<TypeParam> parameters)
    {
        while (At(Tok.Ident) && Cur.Text == "where")
        {
            _i++;
            string name = Expect(Tok.Ident, "a type parameter name").Text;
            Expect(Tok.Colon, "':' after the constrained parameter");

            TypeParam? target = parameters.Find(p => p.Name == name);

            if (target is null)
            {
                throw Error($"'{name}' is not a type parameter of this declaration");
            }

            do
            {
                // THE SPECIAL CONSTRAINTS ARE NOT TYPES. `where T : class`,
                // `: struct`, `: new()`, `: notnull` and `: unmanaged` say
                // what KIND of type argument is allowed rather than name one,
                // and ParseTypeRef met `class` and stopped -- so ordinary C#
                // carrying any of them did not parse at all.
                //
                // They are read and dropped, all but `struct`, `new()` and
                // `unmanaged`. This compiler makes a COPY of a generic per
                // type argument, so what any other constraint rules out is
                // ruled out by that copy failing to compile, on the line that
                // depended on it rather than on the declaration; there is
                // nothing here for one to be checked against.
                //
                // `struct` IS KEPT because it changes what `T?` means: over a
                // struct-constrained T it is Nullable<T>, a real cell, where
                // over any other T the '?' is an annotation and a value-type
                // T? is plain T (TypeParam.Struct).
                if (At(Tok.KwClass) || At(Tok.KwStruct))
                {
                    if (At(Tok.KwStruct))
                    {
                        target.Struct = true;
                    }
                    _i++;
                    Take(Tok.Question);
                    continue;
                }

                // `new()` IS KEPT: it is what lets `new T()` be written over
                // T, and what a type argument without a public parameterless
                // constructor is refused by (TypeParam.New).
                if (At(Tok.KwNew))
                {
                    _i++;
                    Expect(Tok.LParen, "'(' after 'new' in a constraint");
                    Expect(Tok.RParen, "')' to close 'new()' in a constraint");
                    target.New = true;
                    continue;
                }

                if (At(Tok.Ident) && Cur.Text is "notnull" or "unmanaged")
                {
                    if (Cur.Text == "unmanaged")
                    {
                        target.Unmanaged = true;
                    }
                    _i++;
                    continue;
                }

                target.WritableConstraints.Add(ParseTypeRef());
            }
            while (Take(Tok.Comma));
        }
    }

    /// <summary>Whether the member being read belongs to an interface.</summary>
    private bool _inInterface;

    private MemberDecl ParseMember(string ownerName)
    {
        Token start = Cur;
        Mods mods = ParseMods();
        _memberStatic = (mods & Mods.Static) != 0;
        _memberName = start.Text;
        // `event T Name;` is a field of a delegate type whose compound
        // assignments combine and remove handlers. The keyword is all the
        // syntax there is; the binder and lowering give += and -= their
        // meaning for any delegate-typed place.
        bool isEvent = Take(Tok.KwEvent);

        // WHAT THE RESULT SAYS ABOUT NULL, read before anything else clears
        // the attribute list: `[return: NotNullIfNotNull(nameof(path))]` is
        // .NET's way of saying a method hands back a null only where it was
        // given one, and Path.ChangeExtension is declared with it.
        string? returnsNullOnlyWith = null;
        List<AttributeRef> attributes = CapturedAttributes();

        foreach (AttributeRef written in _attributeParts)
        {
            if (written.Target == "return" && written.Name == "NotNullIfNotNull")
            {
                returnsNullOnlyWith = written.Argument;
                break;
            }
        }

        // A constructor is a method whose name matches its type and which has
        // no return type, so it must be recognised before the type is parsed.
        if (At(Tok.Ident) && Cur.Text == ownerName && Ahead().Kind == Tok.LParen)
        {
            _i++;
            MethodDecl ctor = new()
            {
                Name = ownerName, Mods = mods, Returns = null, IsCtor = true,
                Line = start.Line, Col = start.Col,
                Body = null,
            };
            { List<Param>? read = null; ParseParams(ref read); ctor.AdoptParams(read); }

            // A constructor may chain: ': base(...)', ': this(...)', or the
            // base type named directly, which reads better than 'base' does.
            CtorInit? chain = null;

            if (Take(Tok.Colon))
            {
                Token initAt = Cur;
                bool isThis = At(Tok.KwThis);

                if (!isThis && !At(Tok.KwBase) && !At(Tok.Ident))
                {
                    throw Error($"expected 'base', 'this' or a base type name, found '{Cur.Text}'");
                }
                _i++;

                chain = new CtorInit { IsThis = isThis, Line = initAt.Line, Col = initAt.Col };
                Expect(Tok.LParen, "'(' after the constructor initialiser");

                int spanMark = _spans.Count;
                if (Source is not null) { _spans.Add(-1); _spans.Add(-1); }
                if (!At(Tok.RParen))
                {
                    do
                    {
                        if (At(Tok.Ident) && Ahead().Kind == Tok.Colon)
                        {
                            chain.WritableArgNames.Add(_t[_i++].Text);
                            _i++;
                        }
                        else
                        {
                            chain.WritableArgNames.Add(null);
                        }
                        int argumentAt = ArgumentStart();
                        chain.Args.Add(ParseArg());
                        if (Source is not null) { _spans.Add(argumentAt); _spans.Add(End(_i - 1)); }
                    }
                    while (Take(Tok.Comma));
                }
                Expect(Tok.RParen, "')' after the constructor initialiser");
                if (Source is not null)
                {
                    chain.Spans = _spans.GetRange(spanMark, _spans.Count - spanMark).ToArray();
                    chain.Source = Source;
                    _spans.RemoveRange(spanMark, _spans.Count - spanMark);
                }
            }

            return FinishMethod(ctor, chain);
        }

        // A CONVERSION OPERATOR: `public static implicit operator XName(string
        // name)`. No return type is written before it -- the type after
        // `operator` is both the name and what it returns -- so it is
        // recognised here, before a type is parsed. `implicit` and `explicit`
        // are words only in this position, as C# has them. Desugared, like
        // the others, to the method .NET's metadata names: op_Implicit and
        // op_Explicit, one parameter each.
        if (At(Tok.Ident) && Cur.Text is "implicit" or "explicit" && Ahead().Kind == Tok.KwOperator)
        {
            bool isImplicit = Cur.Text == "implicit";
            _i += 2;
            MethodDecl conversion = new()
            {
                Name = isImplicit ? "op_Implicit" : "op_Explicit", Mods = mods, Returns = ParseTypeRef(),
                Line = start.Line, Col = start.Col, Body = null,
            };
            { List<Param>? read = null; ParseParams(ref read); conversion.AdoptParams(read); }

            if (conversion.Params.Count != 1)
            {
                throw Error("a conversion operator takes one operand");
            }
            if (attributes.Count > 0) conversion.WritableAttributes.AddRange(attributes);
            return FinishMethod(conversion);
        }

        // `ref int Find(...)` RETURNS A VARIABLE (Mods.RefReturn). Only a
        // method does here; a ref-returning property or indexer is refused
        // below rather than read as one returning a value.
        if (Take(Tok.KwRef))
        {
            mods |= Mods.RefReturn;
            if (Take(Tok.KwReadonly)) mods |= Mods.RefReadonlyReturn;
        }

        TypeRef type = At(Tok.KwVoid) ? VoidType() : ParseTypeRef();

        if ((mods & Mods.RefReturn) != 0 && !(At(Tok.Ident) && Ahead().Kind is Tok.LParen or Tok.Lt))
        {
            throw Error("only a method can return by reference here; a ref-returning property, indexer or operator is not supported");
        }

        // AN OPERATOR: `public static TimeSpan operator -(DateTime a, DateTime b)`.
        //
        // Desugared into an ordinary static method under the name .NET gives
        // it -- op_Subtraction -- for the same reason an indexer becomes
        // get_Item: everything below already knows how to find and call a
        // method, and the checker only has to notice that `a - b` on two
        // DateTimes means one. That naming is not ours: it is the name the
        // operator has in metadata, so a C# program and this one agree.
        if (At(Tok.KwOperator))
        {
            return ParseOperator(type, mods, start);
        }

        // AN INDEXER: `public T this[int i] { get { } set { } }`.
        //
        // Desugared here into two ordinary methods, get_Item and set_Item,
        // which is what C# itself compiles one to. That is the whole trick: the
        // binder and the code generator already know how to find and call a
        // method, so an indexer costs a shape in the parser and one case at the
        // use site rather than a concept everything below has to learn.
        //
        // The setter's value is a last parameter called `value`, exactly as an
        // ordinary property's is.
        if (At(Tok.KwThis) && _t[_i + 1].Kind == Tok.LBracket)
        {
            return ParseIndexer(type, mods, start);
        }

        string name = Expect(Tok.Ident, "a member name").Text;

        // AN EXPLICIT INTERFACE IMPLEMENTATION: `bool ISymbols.TryLookup(...)`,
        // which is how this compiler's own assembler answers the evaluator
        // without putting the method on its own surface.
        //
        // The interface is named and the member follows it, so what was read as
        // the member's name is the interface's. C# makes such a member private
        // and reachable only through the interface; nothing here enforces
        // accessibility on any member yet, so what is kept is the name it fills
        // in and the interface it fills it in for.
        string? explicitInterface = null;

        // A QUALIFIED INTERFACE: `object System.Collections.IEnumerator.Current`.
        // The namespaces are stepped over to the interface's own name, which is
        // what an explicit implementation is matched by (Binder.ExplicitName).
        // A segment followed by `<` is a qualifier only when its arguments are
        // followed by `.Name` or `.this[`; otherwise it is a generic method.
        while (At(Tok.Dot) && Ahead().Kind == Tok.Ident)
        {
            Tok after = _t[_i + 2].Kind;
            if (after == Tok.Dot)
            {
                _i++;
                name = _t[_i++].Text;
                continue;
            }
            if (after != Tok.Lt) break;
            int back = _i;
            _i += 2;
            bool qualifier = TryInterfaceArgs() && At(Tok.Dot)
                && (Ahead().Kind == Tok.Ident || Ahead().Kind == Tok.KwThis);
            _i = back;
            if (!qualifier) break;
            _i++;
            name = _t[_i++].Text;
            break;
        }

        if (At(Tok.Dot) && Ahead().Kind == Tok.Ident)
        {
            explicitInterface = name;
            _i++;
            name = _t[_i++].Text;
        }
        else if (At(Tok.Dot) && Ahead().Kind == Tok.KwThis && _t[_i + 2].Kind == Tok.LBracket)
        {
            // AN INDEXER OF AN INTERFACE: `T IList<T>.this[int index]`.
            _i++;
            PropertyDecl indexer = ParseIndexer(type, mods, start);
            indexer.ExplicitInterface = name;
            return indexer;
        }
        else if (At(Tok.Lt))
        {
            // A GENERIC ONE: `IEnumerable<T>.GetEnumerator`. The arguments are
            // the interface's, not the member's type parameters, when a dot
            // and a name follow them.
            //
            // NAMED WITH ITS ARITY, as .NET's metadata names it: IEnumerable`1
            // is not IEnumerable, and a class implementing both explicitly has
            // two GetEnumerators that are not the same method.
            int save = _i;
            if (TryInterfaceArgs(out int arity) && At(Tok.Dot) && Ahead().Kind == Tok.Ident)
            {
                explicitInterface = name + "`" + arity;
                _i++;
                name = _t[_i++].Text;
            }
            else if (_i != save && At(Tok.Dot) && Ahead().Kind == Tok.KwThis && _t[_i + 2].Kind == Tok.LBracket)
            {
                _i++;
                PropertyDecl indexer = ParseIndexer(type, mods, start);
                indexer.ExplicitInterface = name + "`" + arity;
                return indexer;
            }
            else _i = save;
        }

        // AN EVENT WITH ACCESSORS: `event T Name { add { ... } remove { ... } }`,
        // which is two methods, add_Name and remove_Name, each taking the
        // handler as `value` -- what C# compiles one to. `x.Name += h` calls
        // add_Name (Binder). The add method is the member returned; remove
        // joins the type with the hoisted members.
        if (isEvent && At(Tok.LBrace))
        {
            return ParseEventAccessors(type, name, mods, start, explicitInterface);
        }

        // property
        if (At(Tok.LBrace))
        {
            MemberDecl property = ParseProperty(type, name, mods, start);
            property.ExplicitInterface = explicitInterface;
            return property;
        }

        // Expression-bodied property: a getter and nothing else.
        if (Take(Tok.FatArrow))
        {
            Token bodyAt = Cur;
            Expr value = ReadBodyExpression();
            Expect(Tok.Semi, "';' after an expression-bodied property");

            Block getter = new() { Line = bodyAt.Line, Col = bodyAt.Col };
            getter.WritableStatements.Add(new ReturnStmt { Value = value, Line = bodyAt.Line, Col = bodyAt.Col });

            return new PropertyDecl
            {
                Name = name, Mods = mods, Type = type, Getter = getter,
                Auto = false, HasSetter = false, Line = start.Line, Col = start.Col,
                ExplicitInterface = explicitInterface,
            };
        }

        // method
        if (At(Tok.Lt) || At(Tok.LParen))
        {
            MethodDecl m = new()
            {
                Name = name, Mods = mods, Returns = type,
                // `[return: NotNullIfNotNull(nameof(path))]`, which is how
                // .NET's Path.ChangeExtension says it hands back a null only
                // when it was given one. Read here, before the parameter list
                // clears the attributes it was read from.
                NotNullIfNotNull = returnsNullOnlyWith,
                Line = start.Line, Col = start.Col, Body = null,
                ExplicitInterface = explicitInterface,
            };
            // `[DoesNotReturn]` and the rest, which the checker reads off the
            // declaration (Binder.NeverReturns).
            if (attributes.Count > 0) m.WritableAttributes.AddRange(attributes);
            if (At(Tok.Lt)) ParseTypeParams(m.WritableTypeParams);
            { List<Param>? read = null; ParseParams(ref read); m.AdoptParams(read); }
            ParseConstraints(m.TypeParams);
            return FinishMethod(m);
        }

        // field
        Expr? init = Take(Tok.Assign) ? Initialiser(type) : null;

        // ONE TYPE, SEVERAL NAMES: `private const int A = 0, B = 1, C = 2;`,
        // which is how this compiler's own lowering writes the offsets into a
        // descriptor. Each name is a field of its own with the same type and
        // modifiers, so the rest is read here and handed back with the first;
        // whoever declares the members takes the extras with it.
        List<FieldDecl> rest = new();

        while (Take(Tok.Comma))
        {
            Token also = Expect(Tok.Ident, "another name in the declaration");
            Expr? value = Take(Tok.Assign) ? Initialiser(type) : null;

            FieldDecl more = new()
            {
                Name = also.Text, Mods = mods, Type = type, Init = value, IsEvent = isEvent,
                DeclaredInit = value, Line = also.Line, Col = also.Col,
            };

            if (attributes.Count > 0) more.WritableAttributes.AddRange(attributes);
            rest.Add(more);
        }

        Expect(Tok.Semi, "';' after the field");

        FieldDecl first = new()
        {
            Name = name, Mods = mods, Type = type, Init = init, IsEvent = isEvent,
            DeclaredInit = init, Line = start.Line, Col = start.Col,
        };

        if (attributes.Count > 0) first.WritableAttributes.AddRange(attributes);
        if (rest.Count > 0) first.WritableMore.AddRange(rest);

        // AN EVENT IS ITS add AND remove ACCESSORS, as C# compiles one, and a
        // field-like event has them too: they combine into its field. Through
        // them an event an interface declares is implemented by a class's
        // field-like event (or by accessors written out), and reached through
        // the interface (Binder.EventAccessorCall). An interface's event, or
        // an abstract one, is the two accessors and nothing else: there is no
        // field to hold handlers.
        if (isEvent)
        {
            bool accessorsOnly = (_inInterface && !mods.HasFlag(Mods.Static)) || mods.HasFlag(Mods.Abstract);
            MethodDecl? firstAdd = null;
            List<FieldDecl> named = new() { first };
            named.AddRange(rest);
            foreach (FieldDecl declared in named)
            {
                MethodDecl add = FieldEventAccessor(true, declared.Name, type, mods, declared, accessorsOnly);
                MethodDecl remove = FieldEventAccessor(false, declared.Name, type, mods, declared, accessorsOnly);
                if (accessorsOnly && firstAdd is null) firstAdd = add;
                else _hoisted.Add(add);
                _hoisted.Add(remove);
            }
            if (accessorsOnly)
            {
                return firstAdd!;
            }
        }
        return first;
    }

    /// <summary>
    /// One accessor of an event declared without them: `add_Name(T value)`
    /// combining `value` into the field (`Name += value`), or `remove_Name`
    /// taking it out. Without a body where the event has no field.
    /// </summary>
    private MethodDecl FieldEventAccessor(bool add, string name, TypeRef type, Mods mods, Node at, bool abstractOnly)
    {
        Block? body = null;
        if (!abstractOnly)
        {
            body = new Block { Line = at.Line, Col = at.Col };
            body.WritableStatements.Add(new ExprStmt
            {
                Expr = new AssignExpr
                {
                    Target = new NameExpr { Name = name, Line = at.Line, Col = at.Col },
                    Op = add ? BinOp.Add : BinOp.Sub,
                    Value = new NameExpr { Name = "value", Line = at.Line, Col = at.Col },
                    Line = at.Line, Col = at.Col,
                },
                Line = at.Line, Col = at.Col,
            });
        }
        MethodDecl accessor = new()
        {
            Name = (add ? "add_" : "remove_") + name, Mods = mods,
            Returns = new TypeRef { Name = "void", Line = at.Line, Col = at.Col },
            Body = body, Line = at.Line, Col = at.Col,
        };
        accessor.WritableParams.Add(new Param { Name = "value", Type = type, Line = at.Line, Col = at.Col });
        return accessor;
    }

    /// <summary>
    /// `public static TimeSpan operator -(DateTime a, DateTime b)`, as the
    /// static method it is: op_Subtraction, with its two operands as
    /// parameters.
    ///
    /// The unary ones too (C# 15.10.2), named by their operand count where
    /// the symbol is both: `operator -(Vec v)` is op_UnaryNegation and
    /// `operator -(Vec a, Vec b)` op_Subtraction. The conversion operators are
    /// parsed with the members, since no return type precedes them.
    /// </summary>
    private MethodDecl ParseOperator(TypeRef type, Mods mods, Token start)
    {
        Expect(Tok.KwOperator, "'operator'");

        Token symbol = Cur;
        if (OperatorName(symbol.Kind) is null && UnaryOperatorName(symbol.Kind) is null)
        {
            throw Error($"'{symbol.Text}' is not an operator that can be overloaded");
        }
        _i++;

        MethodDecl made = new()
        {
            Name = "", Mods = mods, Returns = type,
            Line = start.Line, Col = start.Col, Body = null,
        };

        { List<Param>? read = null; ParseParams(ref read); made.AdoptParams(read); }

        string? name = made.Params.Count switch
        {
            1 => UnaryOperatorName(symbol.Kind),
            2 => OperatorName(symbol.Kind),
            _ => null,
        };
        if (name is null)
        {
            throw Error(UnaryOperatorName(symbol.Kind) is null
                ? $"'operator {symbol.Text}' takes two operands"
                : OperatorName(symbol.Kind) is null
                    ? $"'operator {symbol.Text}' takes one operand"
                    : $"'operator {symbol.Text}' takes one or two operands");
        }
        made.Name = name;
        return FinishMethod(made);
    }

    /// <summary>The metadata name of a one-operand operator.</summary>
    private static string? UnaryOperatorName(Tok kind) => kind switch
    {
        Tok.Plus => "op_UnaryPlus",
        Tok.Minus => "op_UnaryNegation",
        Tok.Bang => "op_LogicalNot",
        Tok.Tilde => "op_OnesComplement",
        Tok.PlusPlus => "op_Increment",
        Tok.MinusMinus => "op_Decrement",
        Tok.KwTrue => "op_True",
        Tok.KwFalse => "op_False",
        _ => null,
    };

    /// <summary>The name an operator has in metadata, which is the name the
    /// checker looks for when it meets the operator in an expression.</summary>
    private static string? OperatorName(Tok kind)
    {
        return kind switch
        {
            Tok.Plus => "op_Addition",
            Tok.Minus => "op_Subtraction",
            Tok.Star => "op_Multiply",
            Tok.Slash => "op_Division",
            Tok.Percent => "op_Modulus",
            Tok.Amp => "op_BitwiseAnd",
            Tok.Pipe => "op_BitwiseOr",
            Tok.Caret => "op_ExclusiveOr",
            Tok.Shl => "op_LeftShift",
            Tok.Shr => "op_RightShift",
            Tok.UShr => "op_UnsignedRightShift",
            Tok.Eq => "op_Equality",
            Tok.NotEq => "op_Inequality",
            Tok.Lt => "op_LessThan",
            Tok.Gt => "op_GreaterThan",
            Tok.LtEq => "op_LessThanOrEqual",
            Tok.GtEq => "op_GreaterThanOrEqual",
            _ => null,
        };
    }

    /// <summary>
    /// Whether the bracketed thing the parser is on is followed by `=` and
    /// holds a comma: what tells a deconstructing ASSIGNMENT from a
    /// parenthesised expression that happens to be assigned to.
    /// </summary>
    private bool AssignmentAfterBrackets()
    {
        int j = _i;
        int depth = 0;
        bool comma = false;

        while (j < _t.Count)
        {
            if (_t[j].Kind == Tok.LParen) { depth++; }
            else if (_t[j].Kind == Tok.RParen)
            {
                depth--;

                if (depth == 0) { j++; break; }
            }
            else if (_t[j].Kind == Tok.Comma && depth == 1) { comma = true; }
            j++;
        }

        return comma && j < _t.Count && _t[j].Kind == Tok.Assign;
    }

    /// <summary>
    /// Whether a name follows the bracketed thing the parser is on: what tells
    /// a tuple TYPE from a list of names being bound.
    /// </summary>
    private bool NameAfterBrackets()
    {
        int j = _i;
        int depth = 0;

        while (j < _t.Count)
        {
            if (_t[j].Kind == Tok.LParen) { depth++; }
            else if (_t[j].Kind == Tok.RParen)
            {
                depth--;

                if (depth == 0) { j++; break; }
            }
            j++;
        }

        // A nullable tuple is still a type: `(int, int)? pair`.
        if (j < _t.Count && _t[j].Kind == Tok.Question)
        {
            j++;
        }

        return j < _t.Count && _t[j].Kind == Tok.Ident;
    }

    /// <summary>
    /// The targets a deconstruction writes into, the '(' already read and the
    /// ')' left for the caller.
    ///
    /// A TARGET MAY BE ANOTHER LIST. C# takes a value apart into targets and
    /// each of them may be written as a list of its own, which comes apart the
    /// same way out of the element it stands for: `foreach ((long index,
    /// (Block block, Instr at)) in suspends)` is a line in this compiler's own
    /// async transform. The inner list binds a hidden local holding that
    /// element, so every position still names exactly one thing.
    /// </summary>
    private List<Binding> ReadBindings(bool inferred)
    {
        List<Binding> bound = new();

        do
        {
            Token where = Cur;

            // A TARGET MAY BE A TUPLE-TYPED VARIABLE, whose type begins with
            // the same bracket a nested list does: `foreach (((string
            // template, int arity) family, int methods) in families)` binds two
            // names, the first of them a tuple. What follows the closing
            // bracket settles it, as it does everywhere else a tuple type is
            // written.
            if (At(Tok.LParen) && !NameAfterBrackets())
            {
                _i++;

                List<Binding> inner = ReadBindings(inferred);

                Expect(Tok.RParen, "')' after the names being bound");
                bound.Add(new Binding
                {
                    Name = $"$nested${_hidden++}", Nested = inner,
                    Line = where.Line, Col = where.Col,
                });
                continue;
            }

            // `var (a, b)` IN A LIST, a nested list whose names are all
            // inferred: `foreach ((VReg value, var (from, path)) in parked)`.
            if (At(Tok.KwVar) && Ahead().Kind == Tok.LParen)
            {
                _i += 2;

                List<Binding> inner = ReadBindings(true);

                Expect(Tok.RParen, "')' after the names being bound");
                bound.Add(new Binding
                {
                    Name = $"$nested${_hidden++}", Nested = inner,
                    Line = where.Line, Col = where.Col,
                });
                continue;
            }

            // `_` BINDS NOTHING, and has no type written before it.
            bool discard = At(Tok.Ident) && Cur.Text == "_"
                        && Ahead().Kind is Tok.Comma or Tok.RParen;
            // `var` BEFORE ONE NAME infers just that one, as C# allows in a
            // list whose other names have types: `(FrameSlot keep, var list)`.
            bool eachInferred = !inferred && !discard && At(Tok.KwVar) && Ahead().Kind == Tok.Ident;
            if (eachInferred) _i++;
            TypeRef? each = inferred || discard || eachInferred ? null : ParseTypeRef();

            bound.Add(new Binding
            {
                Type = each,
                Name = discard ? _t[_i++].Text : Expect(Tok.Ident, "a name to bind").Text,
                Line = where.Line, Col = where.Col,
            });
        }
        while (Take(Tok.Comma));

        return bound;
    }

    private Block ReadBodyBlock(TypeRef? produces, int line, int col)
    {
        string? savedTarget = _iteratorTarget;
        TypeRef? savedElement = _iteratorElement;
        bool savedYield = _iteratorSawYield;

        _iteratorTarget = "__yield" + _iteratorSerial++;
        // WHAT IT ENUMERATES, when it is an iterator: the T of IEnumerable<T> or
        // IEnumerator<T>, object for the non-generic two.
        _iteratorElement = produces is { Args.Count: 1 } sequence
                         && sequence.Name is "IEnumerable" or "IEnumerable`1" or "IEnumerator" or "IEnumerator`1"
                         ? sequence.Args[0]
                         : produces is { Args.Count: 0, Name: "IEnumerable" or "IEnumerator" or "System.Collections.IEnumerable" or "System.Collections.IEnumerator" }
                         ? new TypeRef { Name = "object", Line = line, Col = col }
                         : null;
        _iteratorSawYield = false;
        // `return default;` IS THE ZERO OF WHAT THIS BODY RETURNS (BareDefault):
        // a local function's own type, not the method around it, whose int
        // or void it was given.
        TypeRef? savedReturns = _returns;
        _returns = produces;

        Block body = ParseBlock();
        _returns = savedReturns;

        if (_iteratorSawYield)
        {
            if (_iteratorElement is null)
            {
                throw new CompileError(_file, line, col,
                    "a method using 'yield return' must return IEnumerable<T>, IEnumerator<T>, IEnumerable or IEnumerator");
            }
            body.Iterator = true;
        }

        _iteratorTarget = savedTarget;
        _iteratorElement = savedElement;
        _iteratorSawYield = savedYield;
        return body;
    }

    private MethodDecl FinishMethod(MethodDecl m, CtorInit? init = null)
    {
        bool saved = _templateMethod;
        _templateMethod = m.TypeParams.Count > 0;
        // Its generic local functions are hoisted out of it (MethodDecl.HoistedIn).
        _hoistParents.Push(m.HoistKey);
        try { return FinishMethodCore(m, init); }
        finally
        {
            _templateMethod = saved;
            _hoistParents.Pop();
        }
    }

    /// <summary>The methods whose bodies are being read, innermost on top, by their HoistKey.</summary>
    private readonly Stack<string> _hoistParents = new();

    private MethodDecl FinishMethodCore(MethodDecl m, CtorInit? init)
    {
        Block? body = null;

        if (At(Tok.LBrace))
        {
            TypeRef? savedReturns = _returns;

            _returns = m.Returns;
            body = ReadBodyBlock(m.Returns, m.Line, m.Col);
            _returns = savedReturns;
        }
        else if (Take(Tok.FatArrow))
        {
            // Expression body: sugar for a block that returns the expression --
            // EXCEPT when the method returns nothing, where it is sugar for the
            // expression as a statement. `public void RelocateLast() =>
            // _codeRelocs.Add(_code.Count - 1);` is C# and there is nothing to
            // return: List.Add answers nothing, and returning it was reported as
            // a void method returning a value.
            Token at = Cur;
            Expr value = ReadBodyExpression();

            Expect(Tok.Semi, "';' after an expression body");
            body = new Block { Line = at.Line, Col = at.Col };
            body.WritableStatements.Add(m.Returns is null or { Name: "void", ArrayRank: 0, PointerDepth: 0 }
                ? new ExprStmt { Expr = value, Line = at.Line, Col = at.Col }
                : new ReturnStmt { Value = value, Line = at.Line, Col = at.Col });
        }
        else
        {
            Expect(Tok.Semi, "'{', '=>' or ';' after the signature");

            if (m.Mods.HasFlag(Mods.Extern))
            {
                body = new Block { Line = m.Line, Col = m.Col };

                if (m.Returns != null
                    && !(m.Returns.Name == "void" && m.Returns.ArrayRank == 0
                         && m.Returns.PointerDepth == 0))
                {
                    body.WritableStatements.Add(new ReturnStmt
                    {
                        Value = new DefaultExpr
                        {
                            Type = m.Returns, Line = m.Line, Col = m.Col,
                        },
                        Line = m.Line,
                        Col = m.Col,
                    });
                }
            }
        }

        return new MethodDecl
        {
            Name = m.Name, Mods = m.Mods, Returns = m.Returns, IsCtor = m.IsCtor,
            NotNullIfNotNull = m.NotNullIfNotNull,
            ExplicitInterface = m.ExplicitInterface,
            Line = m.Line, Col = m.Col, Body = body, Init = init,
        }.CopyListsFrom(m);
    }

    /// `T this[params] { get { } set { } }` -- a property called Item that takes
    /// arguments, which is exactly what C# compiles an indexer to.
    ///
    /// The name is not arbitrary: `Item` is what the CLR calls it, so anything
    /// that later reads a compiled library sees the same member C# would have
    /// produced. An indexer may never be auto-implemented -- there is no single
    /// field to back an arbitrary number of slots -- so both accessors need
    /// bodies and it says so rather than quietly generating a field.
    private MethodDecl ParseEventAccessors(TypeRef type, string name, Mods mods, Token start, string? explicitInterface)
    {
        Expect(Tok.LBrace, "'{' before the event's accessors");
        MethodDecl? add = null, remove = null;

        while (!At(Tok.RBrace))
        {
            CapturedAttributes();
            Token which = Expect(Tok.Ident, "'add' or 'remove'");
            if (which.Text is not ("add" or "remove"))
            {
                throw Error($"an event's accessors are 'add' and 'remove', not '{which.Text}'");
            }
            if ((which.Text == "add" ? add : remove) is not null)
            {
                throw Error($"the event '{name}' has two '{which.Text}' accessors");
            }

            Block body;
            if (Take(Tok.FatArrow))
            {
                Token bodyAt = Cur;
                Expr done = ReadBodyExpression();
                Expect(Tok.Semi, "';' after an expression-bodied accessor");
                body = new Block { Line = bodyAt.Line, Col = bodyAt.Col };
                body.WritableStatements.Add(new ExprStmt { Expr = done, Line = bodyAt.Line, Col = bodyAt.Col });
            }
            else
            {
                body = ParseBlock();
            }

            MethodDecl accessor = new()
            {
                Name = (which.Text == "add" ? "add_" : "remove_") + name, Mods = mods,
                Returns = new TypeRef { Name = "void", Line = which.Line, Col = which.Col },
                Body = body, Line = which.Line, Col = which.Col,
                ExplicitInterface = explicitInterface,
            };
            accessor.WritableParams.Add(new Param { Name = "value", Type = type, Line = which.Line, Col = which.Col });

            if (which.Text == "add") add = accessor;
            else remove = accessor;
        }
        Expect(Tok.RBrace, "'}' after the event's accessors");

        if (add is null || remove is null)
        {
            throw Error($"the event '{name}' needs both an 'add' and a 'remove' accessor");
        }

        _hoisted.Add(remove);
        return add;
    }

    private PropertyDecl ParseIndexer(TypeRef type, Mods mods, Token start)
    {
        _i++;                                   // 'this'
        Expect(Tok.LBracket, "'[' after 'this'");

        List<Param> parameters = new();

        do
        {
            Token at = Cur;
            TypeRef pt = ParseTypeRef();
            string pn = Expect(Tok.Ident, "an index parameter name").Text;

            parameters.Add(new Param { Name = pn, Type = pt, Line = at.Line, Col = at.Col });
        }
        while (Take(Tok.Comma));

        Expect(Tok.RBracket, "']' after the index parameters");

        PropertyDecl p = ParseProperty(type, "Item", mods, start);

        // AN INTERFACE'S INDEXER HAS NO BODIES, and that is not a mistake --
        // an interface member never has one. The refusal is about a CLASS
        // writing `public T this[int i] { get; set; }`, which asks the compiler
        // to invent storage for something with no name to store it under.
        //
        // IReadOnlyList<T> declares an indexer and is how LINQ says what it
        // needs of a sequence, so refusing it here meant the operators could
        // only ever be declared over List.
        if (p.Auto && !_inInterface)
        {
            throw Error("an indexer needs bodies for its accessors; there is no field to back one");
        }

        p.WritableParams.AddRange(parameters);
        return p;
    }

    private PropertyDecl ParseProperty(TypeRef type, string name, Mods mods, Token start)
    {
        // AN EXPRESSION BODY IS A GETTER AND NOTHING ELSE, for an indexer as
        // much as for a property: `public int this[int i] => data[i];`. An
        // ordinary property's form is handled where members are dispatched,
        // before this is reached; an indexer arrives here instead, so the shape
        // is understood in both places by understanding it here.
        if (Take(Tok.FatArrow))
        {
            Token bodyAt = Cur;
            Expr only = ReadBodyExpression();
            Expect(Tok.Semi, "';' after an expression-bodied property");

            Block body = new() { Line = bodyAt.Line, Col = bodyAt.Col };
            body.WritableStatements.Add(new ReturnStmt { Value = only, Line = bodyAt.Line, Col = bodyAt.Col });

            return new PropertyDecl
            {
                Name = name, Mods = mods, Type = type, Getter = body,
                Auto = false, HasSetter = false, Line = start.Line, Col = start.Col,
            };
        }

        Expect(Tok.LBrace, "'{' to open the property");

        Block? getter = null, setter = null;
        bool auto = true, hasSetter = false;

        while (!At(Tok.RBrace) && !At(Tok.End))
        {
            ParseMods();

            if (TakeContextual("get"))
            {
                if (At(Tok.LBrace))
                {
                    auto = false;

                    TypeRef? savedReturns = _returns;

                    _returns = type;
                    getter = ReadBodyBlock(type, start.Line, start.Col);
                    _returns = savedReturns;
                }
                else if (Take(Tok.FatArrow))
                {
                    auto = false;
                    Token at = Cur;
                    Expr value = ReadBodyExpression();
                    Expect(Tok.Semi, "';' after the expression-bodied getter");
                    getter = new Block { Line = at.Line, Col = at.Col };
                    getter.WritableStatements.Add(new ReturnStmt
                    {
                        Value = value, Line = at.Line, Col = at.Col,
                    });
                }
                else
                {
                    Expect(Tok.Semi, "';' after 'get'");
                }
                continue;
            }

            // 'init' IS A SETTER WITH A RULE ABOUT WHEN, and the rule is not
            // enforced yet.
            //
            // C# allows an init-only setter to be called from a constructor and
            // from an object initialiser and nowhere else. Everything about
            // CODE GENERATION is identical to `set`; the difference is entirely
            // a check the binder has to make, and it is a check about the
            // caller rather than about the property.
            //
            // Accepted rather than refused, and said so here rather than
            // quietly: a hundred and twenty-one properties in this compiler's
            // own source are declared this way, and refusing the word means
            // none of them parse. Treating it as a settable property makes them
            // all work and leaves one rule unenforced -- which is a smaller and
            // much more visible debt than a language that cannot read its own
            // source.
            // CONTEXTUAL, matched by its text rather than by being a keyword.
            //
            // Making it a keyword is the obvious thing and it is wrong: 'init'
            // is an ordinary word and code uses it as a name. Adding it to the
            // lexer's table broke os/kernel/serial.cor, which has a local
            // called init, and it would break every program anybody had already
            // written the same way. C# makes it contextual for exactly this
            // reason -- the word means an accessor HERE, between the braces of
            // a property, and means nothing anywhere else.
            if (TakeContextual("set") || TakeContextual("init"))
            {
                hasSetter = true;

                if (At(Tok.LBrace))
                {
                    auto = false;
                    setter = ParseBlock();
                }
                else if (Take(Tok.FatArrow))
                {
                    auto = false;
                    Token at = Cur;
                    Expr value = ReadBodyExpression();
                    Expect(Tok.Semi, "';' after the expression-bodied setter");
                    setter = new Block { Line = at.Line, Col = at.Col };
                    setter.WritableStatements.Add(new ExprStmt
                    {
                        Expr = value, Line = at.Line, Col = at.Col,
                    });
                }
                else
                {
                    Expect(Tok.Semi, "';' after 'set'");
                }
                continue;
            }
            throw Error($"expected 'get', 'set' or 'init', found '{Cur.Text}'");
        }

        Expect(Tok.RBrace, "'}' to close the property");
        Expr? init = Take(Tok.Assign) ? ParseExpr() : null;

        if (init != null)
        {
            Expect(Tok.Semi, "';' after a property initialiser");
        }

        return new PropertyDecl
        {
            Name = name, Mods = mods, Type = type, Getter = getter, Setter = setter,
            Auto = auto, HasSetter = hasSetter, Init = init, Line = start.Line, Col = start.Col,
        };
    }

    /// <summary>
    /// Reads a parameter list into <paramref name="into"/>, made at the first
    /// parameter: `()` makes none, and most members are read with `()`.
    /// </summary>
    private void ParseParams(ref List<Param>? into)
    {
        Expect(Tok.LParen, "'(' to open the parameter list");

        if (Take(Tok.RParen))
        {
            return;
        }

        do
        {
            // ATTRIBUTES BELONG ON A PARAMETER TOO, and one of them is read:
            // `[NotNullWhen(false)] string? value` is how .NET's
            // string.IsNullOrEmpty says what a false answer proves.
            SkipAttributes();

            bool? whenProved = null;
            CallerInfo caller = CallerInfo.None;
            string? callerArgument = null;
            string? marshalAs = null;

            // AND THE CALLER-INFORMATION ATTRIBUTES, which make the compiler
            // pass what it knows at the call for an argument left out: the
            // member it is in, the file, the line, or another argument's text.
            foreach (AttributeRef written in _attributeParts)
            {
                string named = written.Name.EndsWith("Attribute", StringComparison.Ordinal) ? written.Name[..^"Attribute".Length] : written.Name;
                if (named == "NotNullWhen" && written.Argument is "true" or "false")
                {
                    whenProved = written.Argument == "true";
                }
                else if (named == "CallerMemberName") caller = CallerInfo.MemberName;
                else if (named == "CallerFilePath") caller = CallerInfo.FilePath;
                else if (named == "CallerLineNumber") caller = CallerInfo.LineNumber;
                else if (named == "CallerArgumentExpression" && written.Argument is { Length: > 0 } of)
                {
                    caller = CallerInfo.ArgumentExpression;
                    callerArgument = of;
                }
                else if (named == "MarshalAs" && written.Argument is { Length: > 0 } unmanaged) marshalAs = unmanaged;
            }

            Token at = Cur;

            // `this` before the first parameter of a static method makes it an
            // extension method: `list.Where(f)` for `Where(this List<T> l, ...)`.
            bool receiver = Take(Tok.KwThis);
            bool variadic = Take(Tok.KwParams);
            bool byRef = Take(Tok.KwRef);
            bool byOut = !byRef && Take(Tok.KwOut);

            // `in T x` is a ref the callee may not write: C# passes the address
            // exactly as `ref` does and refuses the assignment, and the caller
            // writes nothing at the call site. The address is what matters --
            // it is why `in` is written at all, a struct too big to copy -- so
            // it is a ref here, and a ref the reader may not assign to.
            bool byIn = !byRef && !byOut && Take(Tok.KwIn);
            TypeRef type = ParseTypeRef();
            string name = Expect(Tok.Ident, "a parameter name").Text;
            Expr? def = Take(Tok.Assign) ? ParseExpr() : null;

            (into ??= new()).Add(new Param
            {
                Name = name, Type = type, IsRef = byRef || byIn, IsOut = byOut,
                IsReadOnlyRef = byIn, IsParams = variadic, IsThis = receiver,
                NotNullWhen = whenProved, Caller = caller, CallerArgument = callerArgument,
                MarshalAs = marshalAs, Default = def, Line = at.Line, Col = at.Col,
            });

            if (variadic && type.ArrayRank == 0)
            {
                throw new CompileError(_file, at.Line, at.Col,
                                       "a 'params' parameter must be an array");
            }

            if (variadic && At(Tok.Comma))
            {
                throw new CompileError(_file, at.Line, at.Col,
                                       "a 'params' parameter must be the final parameter");
            }
        }
        while (Take(Tok.Comma));

        Expect(Tok.RParen, "')' to close the parameter list");
    }

    // ---- types ------------------------------------------------------------

    private TypeRef VoidType()
    {
        Token at = _t[_i++];
        return new TypeRef { Name = "void", Line = at.Line, Col = at.Col };
    }

    private TypeRef ParseTypeRef(bool pattern = false)
    {
        Token at = Cur;

        if (At(Tok.KwVoid))
        {
            return VoidType();
        }

        // A FUNCTION POINTER: `delegate* <A, R>`, `delegate* unmanaged<A, R>`,
        // `delegate* unmanaged[Cdecl]<A, R>`. The last type is the result.
        if (At(Tok.KwDelegate) && Ahead().Kind == Tok.Star)
        {
            _i += 2;
            bool unmanaged = false;
            if (At(Tok.Ident) && Cur.Text is "unmanaged" or "managed")
            {
                unmanaged = Cur.Text == "unmanaged";
                _i++;
                // The calling convention in brackets: C's is the only one
                // either target has, so it is read and not kept.
                if (Take(Tok.LBracket))
                {
                    while (!At(Tok.RBracket) && !At(Tok.End))
                    {
                        _i++;
                    }
                    Expect(Tok.RBracket, "']' to close the calling convention");
                }
            }
            Expect(Tok.Lt, "'<' after 'delegate*'");
            List<TypeRef> parts = new();
            do
            {
                parts.Add(ParseTypeRef());
            }
            while (Take(Tok.Comma));
            Expect(Tok.Gt, "'>' to close the function pointer's types");
            TypeRef pointer = new()
            {
                Name = unmanaged ? TypeRef.UnmanagedFunction : TypeRef.ManagedFunction,
                Line = at.Line, Col = at.Col,
            };
            pointer.Arguments.AddRange(parts);
            return Suffixes(pointer, !pattern);
        }

        // A TUPLE TYPE: `(int, string)`, `(int At, string Label)`.
        //
        // It is written like nothing else and so needs no speculation: a type
        // is expected here, and a type that begins with a bracket is this one.
        // The name is what the compiler calls the class it writes for it; the
        // element names travel alongside, because `f.At` has to mean the first
        // one and only the type knows which that is.
        if (At(Tok.LParen))
        {
            _i++;

            TypeRef tuple = new()
            {
                Name = TypeRef.Tuple, TupleNames = new List<string>(),
                Line = at.Line, Col = at.Col,
            };

            do
            {
                tuple.Arguments.Add(ParseTypeRef());
                tuple.TupleNames.Add(At(Tok.Ident) ? _t[_i++].Text : "");
            }
            while (Take(Tok.Comma));

            Expect(Tok.RParen, "')' to close the tuple type");

            if (tuple.Args.Count < 2)
            {
                throw Error("a tuple needs at least two elements");
            }

            return Suffixes(tuple, !pattern);
        }

        string name = Expect(Tok.Ident, "a type name").Text;

        while (At(Tok.Dot) && Ahead().Kind == Tok.Ident)
        {
            _i++;
            name += "." + _t[_i++].Text;
        }

        if (name == "dynamic" && WrittenAsType(_i - 1)) _sawDynamic = true;

        List<TypeRef> args = new();

        if (At(Tok.Lt))
        {
            int save = _i;

            if (!TryParseTypeArgs(out args))
            {
                _i = save;
                args = new List<TypeRef>();
            }
        }

        // A TYPE NESTED IN A GENERIC ONE, named through it: `Outer<int>.Inner`
        // is Outer.Inner with Outer's arguments first and its own after
        // (TypeDecl.OuterParams).
        while (args.Count > 0 && At(Tok.Dot) && Ahead().Kind == Tok.Ident)
        {
            _i++;
            name += "." + _t[_i++].Text;
            if (At(Tok.Lt))
            {
                int again = _i;
                if (TryParseTypeArgs(out List<TypeRef> more)) args.AddRange(more);
                else _i = again;
            }
        }

        TypeRef type = new() { Name = name, Line = at.Line, Col = at.Col };

        type.Arguments.AddRange(args);
        return Suffixes(type, !pattern);
    }

    /// <summary>
    /// The rest of a tuple, from wherever it was recognised.
    ///
    /// The first element may already have been read -- a group and a tuple
    /// start identically and only the comma tells them apart -- so it is handed
    /// back in, with the name it was written with or an empty string.
    /// </summary>
    private TupleExpr FinishTuple(Token at, Expr? first, string? firstName)
    {
        TupleExpr tuple = new() { Line = at.Line, Col = at.Col };

        if (first != null)
        {
            tuple.Items.Add(first);
            tuple.Names.Add(firstName ?? "");
            Expect(Tok.Comma, "',' between the elements of a tuple");
        }

        do
        {
            string name = "";

            if (At(Tok.Ident) && Ahead().Kind == Tok.Colon)
            {
                name = _t[_i].Text;
                _i += 2;
            }

            tuple.Names.Add(name);
            tuple.Items.Add(ParseExpr());
        }
        while (Take(Tok.Comma));

        Expect(Tok.RParen, "')' to close the tuple");

        if (tuple.Items.Count < 2)
        {
            throw Error("a tuple needs at least two elements");
        }

        InferTupleNames(tuple);
        return tuple;
    }

    /// <summary>
    /// C# 7.1's INFERRED TUPLE NAMES: an element written as a name or a
    /// member access is named by it -- `(block, index)` has elements `block`
    /// and `index`, `(b, b.N)` has `b` and `N` -- unless it was named
    /// explicitly, the name would be inferred for two elements, or it is one
    /// of ValueTuple's own members (Item1.., Rest, ToString, Equals,
    /// GetHashCode, CompareTo, GetType), which are never inferred.
    /// </summary>
    private static void InferTupleNames(TupleExpr tuple)
    {
        string?[] inferred = new string?[tuple.Items.Count];
        for (int i = 0; i < tuple.Items.Count; i++)
        {
            if (tuple.Names[i].Length != 0) continue;
            string? name = tuple.Items[i] switch
            {
                NameExpr n when n.TypeArgs.Count == 0 => n.Name,
                MemberExpr m => m.Name,
                _ => null,
            };
            if (name is null || name is "Rest" or "ToString" or "Equals" or "GetHashCode" or "CompareTo" or "GetType"
                || (name.StartsWith("Item", StringComparison.Ordinal) && name.Length > 4 && name[4..].All(char.IsAsciiDigit)))
                continue;
            inferred[i] = name;
        }
        for (int i = 0; i < inferred.Length; i++)
        {
            string? name = inferred[i];
            if (name is null) continue;
            bool taken = false;
            for (int j = 0; j < tuple.Items.Count && !taken; j++)
                if (j != i && (inferred[j] == name || tuple.Names[j] == name)) taken = true;
            if (!taken) tuple.Names[i] = name;
        }
    }

    /// <summary>
    /// The stars, the question mark and the brackets that follow a type name.
    ///
    /// THE STARS COME FIRST and the brackets last, which is C#'s order:
    /// `byte*[]` is an array of pointers, and `byte*?` is not a thing at all
    /// since a pointer is a value type.
    ///
    /// Its own function because a tuple type ends somewhere else -- at its
    /// closing bracket -- and `(int, int)[]` and `(int, int)?` are both
    /// perfectly ordinary things to write.
    /// </summary>
    private TypeRef Suffixes(TypeRef bare, bool allowNullable = true)
    {
        int stars = 0;
        int rank = 0;

        // WHICH SIDE THE QUESTION MARK CAME DOWN ON, not merely that one did.
        //
        // `byte[]?` is a nullable array and `byte?[]` is an array of nullables,
        // and C# tells them apart by what stands to the LEFT of the '?'. This
        // recorded a single `nullable` flag, so both spellings arrived as the
        // same TypeRef and the later one silently won -- an array of nullable
        // strings was read as a nullable array of strings, which is a different
        // type with a different set of things that need checking before use.
        bool beforeArray = false;
        int marks = 0;
        bool afterArray = false;
        while (true)
        {
            if (At(Tok.Star))
            {
                _i++;
                stars++;
                continue;
            }

            if (At(Tok.LBracket) && Ahead().Kind == Tok.RBracket)
            {
                _i += 2;
                rank++;
                continue;
            }

            // `x is string?[]`: where a '?' is not otherwise read as part of
            // the type (after `is`, it may begin `? a : b`), one followed by
            // `[]` still is -- nothing else can follow a type there.
            bool elementMark = !allowNullable && rank == 0 && At(Tok.Question)
                               && Ahead().Kind == Tok.LBracket && Ahead(2).Kind == Tok.RBracket;
            if ((allowNullable || elementMark) && At(Tok.Question))
            {
                _i++;

                if (rank == 0)
                {
                    beforeArray = true;
                }
                else
                {
                    // After some brackets: this level's mark. Whether it is
                    // the type's own or an inner one is known only once the
                    // brackets stop coming.
                    afterArray = true;
                    marks |= 1 << (rank - 1);
                }
                continue;
            }
            break;
        }

        // A '?' seen before any brackets belongs to the ELEMENT once brackets
        // follow, and to the type itself when none do. The mark after the
        // last brackets is the type's own; marks between brackets are the
        // inner levels' -- `byte[]?[]` is an array of arrays that may be null.
        bool nullable = rank > 0 ? (marks & (1 << (rank - 1))) != 0 : beforeArray;
        bool elementNullable = rank > 0 && beforeArray;
        int inner = rank > 1 ? marks & ((1 << (rank - 1)) - 1) : 0;
        afterArray = afterArray || nullable;

        if (stars == 0 && !nullable && !elementNullable && rank == 0)
        {
            return bare;
        }

        TypeRef type = new()
        {
            Name = bare.Name, ArrayRank = rank, Nullable = nullable,
            ElementNullable = elementNullable, InnerNullable = inner,
            PointerDepth = stars, TupleNames = bare.TupleNames,
            Line = bare.Line, Col = bare.Col,
        };

        type.Arguments.AddRange(bare.Args);
        return type;
    }

    /// <summary>
    /// Speculatively reads <c>&lt;A, B&gt;</c>. Succeeds only when what follows
    /// could legally follow a generic name — the only way to tell type
    /// arguments from a chain of comparisons without a symbol table.
    /// </summary>
    /// <summary>
    /// An explicit implementation's interface arguments, `<int>` in
    /// `IMeasure<int>.Size`: any types at all, keywords included, which the
    /// expression-minded TryParseTypeArgs does not take. False, with nothing
    /// consumed that the caller will not put back, when they are not there.
    /// </summary>
    private bool TryInterfaceArgs() => TryInterfaceArgs(out _);

    /// <summary>The `&lt;...&gt;` after an interface's name, and how many arguments it held.</summary>
    private bool TryInterfaceArgs(out int arity)
    {
        arity = 0;
        if (!Take(Tok.Lt)) return false;
        int mark = _splits.Count;
        try
        {
            do { ParseTypeRef(); arity++; }
            while (Take(Tok.Comma));
            if (TakeAngle()) return true;
        }
        catch (CompileError) { }
        Unsplit(mark);
        return false;
    }

    private bool TryParseTypeArgs(out List<TypeRef> args)
    {
        args = new List<TypeRef>();

        if (!Take(Tok.Lt))
        {
            return false;
        }

        int mark = _splits.Count;

        try
        {
            do
            {
                // A bracket starts a TUPLE type, and a type argument may be
                // one: `List<(int At, string Label)>` is how the compiler's own
                // fix-up table is declared.
                if (!At(Tok.Ident) && !At(Tok.KwVoid) && !At(Tok.LParen))
                {
                    Unsplit(mark);
                    return false;
                }
                args.Add(ParseTypeRef());
            }
            while (Take(Tok.Comma));
        }
        catch (CompileError)
        {
            Unsplit(mark);
            return false;
        }

        if (!TakeAngle())
        {
            Unsplit(mark);
            return false;
        }

        // AND THE SUFFIXES A TYPE CAN CARRY: `List<int>?`, `Node<T>[]`, `T<U>*`.
        //
        // Missing the question mark meant a nullable field of a generic type --
        // `IEqualityComparer<K>? by;` -- was read as a comparison and then
        // reported as a member with no name.
        if (Cur.Kind is Tok.LParen or Tok.RParen or Tok.RBracket or Tok.RBrace
                     or Tok.Comma or Tok.Semi or Tok.Dot or Tok.Colon
                     or Tok.Ident or Tok.Gt or Tok.Shr or Tok.Eq or Tok.NotEq or Tok.LBrace
                     or Tok.Question or Tok.Star or Tok.LBracket
                     // A MEMBER'S TYPE, where the member is not named by an
                     // identifier: `public IReadOnlyList<T> this[K key]` and
                     // `public static Vec<T> operator +(...)`.
                     or Tok.KwThis or Tok.KwOperator
                     // AND WHAT MAY FOLLOW A TYPE IN AN EXPRESSION OR CLAUSE:
                     // `where T : IComparable<T> => …`, `x is List<int> && ok`,
                     // `o as List<int> ?? fallback`, `List<int> => …` in an arm.
                     or Tok.FatArrow or Tok.AndAnd or Tok.OrOr or Tok.QuestionQuestion
                     or Tok.End)
        {
            return true;
        }

        Unsplit(mark);
        return false;
    }

    /// <summary>
    /// Takes the '&gt;' that closes a list of type arguments, splitting the
    /// lexer's '&gt;&gt;' in two when that is what it made of the end of a
    /// nested one.
    ///
    /// `IEnumerator&lt;KeyValuePair&lt;K, V&gt;&gt;` ends in a token the lexer
    /// reads as a right shift, because at the time it read it that is exactly
    /// what it could have been. C# has the same problem and the same answer:
    /// the parser, which knows it is reading a type, splits the token.
    /// </summary>
    private bool TakeAngle()
    {
        if (Take(Tok.Gt))
        {
            return true;
        }

        if (!At(Tok.Shr) && !At(Tok.UShr))
        {
            return false;
        }

        // `>>>` CLOSING GENERICS is a `>` and a `>>`, the second split again
        // by the list outside this one: `A<B<C<int>>>`.
        Token was = _t[_i];
        Token half = was with { Kind = Tok.Gt, Text = ">" };

        _t[_i] = was.Kind == Tok.UShr
            ? was with { Kind = Tok.Shr, Text = ">>", Col = was.Col + 1, Pos = was.Pos + 1 }
            : half with { Col = was.Col + 1, Pos = was.Pos + 1 };
        _t.Insert(_i, half);
        _splits.Add((_i, was));
        _i++;
        return true;
    }

    /// Puts back every '&gt;&gt;' split since the mark, in the order that
    /// restores the stream exactly -- a speculative type-argument list that
    /// turned out to be a comparison must leave no trace.
    private void Unsplit(int mark)
    {
        for (int i = _splits.Count - 1; i >= mark; i--)
        {
            (int at, Token was) = _splits[i];

            _t.RemoveAt(at);
            _t[at] = was;
            _splits.RemoveAt(i);
        }
    }

    // ---- statements -------------------------------------------------------

    private Block ParseBlock()
    {
        Token at = Expect(Tok.LBrace, "'{'");
        Block block = new() { Line = at.Line, Col = at.Col };
        int hoistedFrom = _hoisted.Count;
        _blocks.Push(block);
        try { return ParseBlockBody(block); }
        finally
        {
            _blocks.Pop();

            // THE GENERIC LOCAL FUNCTIONS OF THIS BLOCK ARE SEEN FROM EVERY
            // ONE HOISTED OUT OF IT: itself, to call itself, its siblings
            // before and after it, and those nested deeper. Each is a member
            // of the type and is checked as one, where no block is open to
            // name them, so it carries the names (MethodDecl.LocalGenerics).
            if (block.GenericLocals.Count > 0)
            {
                for (int k = hoistedFrom; k < _hoisted.Count; k++)
                {
                    _hoisted[k].WritableLocalGenerics.AddRange(block.GenericLocals);
                }
            }
        }
    }

    /// <summary>The blocks being read, innermost on top: where a generic local function is declared.</summary>
    private readonly Stack<Block> _blocks = new();

    /// <summary>Generic local functions made into members of the type being read, added after the member that declared them.</summary>
    private readonly List<MethodDecl> _hoisted = new();

    /// <summary>Whether the member being read is static: its generic local functions are.</summary>
    private bool _memberStatic;

    /// <summary>The name of the member being read, which its hoisted local functions are named after.</summary>
    private string _memberName = "";
    private int _hoistSerial;

    private Block ParseBlockBody(Block block)
    {
        Token at = _t[_i - 1];

        if (SkipImplementation)
        {
            int depth = 1;
            while (depth > 0)
            {
                if (At(Tok.End)) throw Error("unterminated declaration body");
                Token token = _t[_i++];
                if (token.Kind == Tok.LBrace) depth++;
                else if (token.Kind == Tok.RBrace) depth--;
            }
            OmittedBodies.Add((at.Pos, _t[_i - 1].Pos + 1, true));
            return block;
        }

        while (!At(Tok.RBrace) && !At(Tok.End))
        {
            block.WritableStatements.Add(ParseStmt());
        }

        Expect(Tok.RBrace, "'}' to close the block");
        return LowerUsings(block);
    }

    private Expr ReadBodyExpression()
    {
        // `=> ref _items[i]`: a ref-returning method's body is a reference.
        if (!SkipImplementation) return At(Tok.KwRef) ? ParseRefValue() : ParseExpr();
        Token start = Cur;
        Stack<Tok> close = new();
        while (!At(Tok.Semi) || close.Count != 0)
        {
            if (At(Tok.End)) throw Error("unterminated expression body");
            Token token = _t[_i++];
            if (token.Kind is Tok.LParen or Tok.LBracket or Tok.LBrace)
                close.Push(token.Kind == Tok.LParen ? Tok.RParen : token.Kind == Tok.LBracket ? Tok.RBracket : Tok.RBrace);
            else if (token.Kind is Tok.RParen or Tok.RBracket or Tok.RBrace)
                if (close.Count == 0 || close.Pop() != token.Kind) throw Error("unbalanced expression body");
        }
        OmittedBodies.Add((start.Pos, Cur.Pos, false));
        // This tree is a declaration marker, never executable implementation.
        return new DefaultExpr { Type = new TypeRef { Name = "object" }, Line = start.Line, Col = start.Col };
    }

    private Block LowerUsings(Block block)
    {
        int at = -1;

        for (int i = 0; i < block.Statements.Count; i++)
        {
            if (block.Statements[i] is UsingDeclStmt)
            {
                at = i;
                break;
            }
        }

        if (at < 0)
        {
            return block;
        }

        UsingDeclStmt marker = (UsingDeclStmt)block.Statements[at];
        Block result = new() { Line = block.Line, Col = block.Col };
        // The block's generic local functions are the new block's: dropped,
        // a block with a `using` declaration in it could not call one.
        if (block.GenericLocals.Count > 0) result.WritableGenericLocals.AddRange(block.GenericLocals);

        for (int i = 0; i < at; i++)
        {
            result.WritableStatements.Add(block.Statements[i]);
        }
        result.WritableStatements.Add(marker.Declaration);

        Block tail = new() { Line = marker.Line, Col = marker.Col };
        for (int i = at + 1; i < block.Statements.Count; i++)
        {
            tail.WritableStatements.Add(block.Statements[i]);
        }
        tail = LowerUsings(tail);

        NameExpr resource = new()
        {
            Name = marker.Declaration.Name, Line = marker.Line, Col = marker.Col,
        };
        // `await using` gives the resource back by awaiting its DisposeAsync
        // (IAsyncDisposable), everything else the same.
        Expr dispose = new CallExpr
        {
            Target = new MemberExpr
            {
                Target = resource, Name = marker.Async ? "DisposeAsync" : "Dispose", Line = marker.Line, Col = marker.Col,
            },
            Line = marker.Line,
            Col = marker.Col,
        };
        if (marker.Async)
            dispose = new AwaitExpr { Operand = dispose, Line = marker.Line, Col = marker.Col };
        Block cleanup = new() { Line = marker.Line, Col = marker.Col };
        cleanup.WritableStatements.Add(new IfStmt
        {
            // A struct resource is disposed without a test, as C# does: the
            // binder answers this one true over a value type (PatternNullTest).
            Cond = new BinaryExpr
            {
                Op = BinOp.Ne,
                PatternNullTest = true,
                Left = new NameExpr
                {
                    Name = marker.Declaration.Name, Line = marker.Line, Col = marker.Col,
                },
                Right = new LiteralExpr
                {
                    Kind = Lit.Null, Text = "null", Line = marker.Line, Col = marker.Col,
                },
                Line = marker.Line,
                Col = marker.Col,
            },
            Then = new ExprStmt { Expr = dispose, Line = marker.Line, Col = marker.Col },
            Line = marker.Line,
            Col = marker.Col,
        });

        result.WritableStatements.Add(new TryStmt
        {
            Body = tail,
            Finally = cleanup,
            Line = marker.Line,
            Col = marker.Col,
        });
        return result;
    }

    /// <summary>
    /// A using, at `using` (after any `await`). THE STATEMENT FORM:
    /// `using (Res r = new Res()) body`, or `using (expression) body`. It is
    /// the declaration form with the body as the rest of the block, so it is
    /// built as exactly that and lowered by the same code -- one try/finally
    /// per resource, disposed in reverse order, on every way out. THE
    /// DECLARATION FORM, `using var r = ...;`, is lowered by the block that
    /// holds it. `await using` (async) gives each resource back by awaiting
    /// its DisposeAsync rather than calling Dispose.
    /// </summary>
    private Stmt ParseUsing(Token at, bool async)
    {
        Expect(Tok.KwUsing, "'using'");
        if (Take(Tok.LParen))
        {
            Stmt resource = ParseSimpleStmt();
            Expect(Tok.RParen, "')' after the resource");
            Stmt body = ParseStmt();

            Block built = new() { Line = at.Line, Col = at.Col };

            if (resource is LocalDecl first)
            {
                List<LocalDecl> each = new() { first };
                each.AddRange(first.Also);
                foreach (LocalDecl d in each)
                {
                    built.WritableStatements.Add(new UsingDeclStmt
                    {
                        Declaration = new LocalDecl
                        {
                            Type = d.Type ?? first.Type, Name = d.Name, Init = d.Init,
                            Line = d.Line, Col = d.Col,
                        },
                        Async = async,
                        Line = d.Line,
                        Col = d.Col,
                    });
                }
            }
            else if (resource is ExprStmt held)
            {
                // No name was given, so one is made: the resource still has
                // to be held somewhere the finally can reach.
                built.WritableStatements.Add(new UsingDeclStmt
                {
                    Declaration = new LocalDecl
                    {
                        Type = null, Name = "$using$" + _lockSerial++, Init = held.Expr,
                        Line = at.Line, Col = at.Col,
                    },
                    Async = async,
                    Line = at.Line,
                    Col = at.Col,
                });
            }
            else
            {
                throw new CompileError(_file, at.Line, at.Col, "a using statement needs a declaration or an expression");
            }

            built.WritableStatements.Add(body);
            return LowerUsings(built);
        }

        TypeRef? type = Take(Tok.KwVar) ? null : ParseTypeRef();
        string name = Expect(Tok.Ident, "a resource name").Text;
        Expect(Tok.Assign, "'=' in a using declaration");
        Expr init = ParseExpr();
        Expect(Tok.Semi, "';' after the using declaration");

        return new UsingDeclStmt
        {
            Declaration = new LocalDecl
            {
                Type = type, Name = name, Init = init,
                Line = at.Line, Col = at.Col,
            },
            Async = async,
            Line = at.Line,
            Col = at.Col,
        };
    }

    private Stmt ParseStmt()
    {
        Token at = Cur;

        switch (Cur.Kind)
        {
            case Tok.Ident when at.Text is "checked" or "unchecked" && Ahead().Kind == Tok.LBrace:
            {
                _i++;
                Block block = ParseBlock();
                block.ArithmeticContext = at.Text == "checked" ? (byte)1 : (byte)2;
                return block;
            }
            case Tok.LBrace:
                return ParseBlock();

            case Tok.Semi:
                _i++;
                return new Block { Line = at.Line, Col = at.Col };

            // Both using forms (ParseUsing), and `await using`.
            case Tok.KwAwait when Ahead().Kind == Tok.KwUsing:
            {
                _i++;
                return ParseUsing(at, async: true);
            }

            case Tok.KwUsing:
                return ParseUsing(at, async: false);

            case Tok.Ident when Cur.Text == "lock" && Ahead().Kind == Tok.LParen:
            {
                _i++;
                Expect(Tok.LParen, "'(' after 'lock'");
                Expr guarded = ParseExpr();
                Expect(Tok.RParen, "')' after the lock object");
                Stmt body = ParseStmt();
                string held = "$lock$" + _lockSerial++;

                NameExpr Held() => new()
                {
                    Name = held, Line = at.Line, Col = at.Col,
                };
                CallExpr MonitorCall(string member) => new()
                {
                    Target = new MemberExpr
                    {
                        Target = new NameExpr
                        {
                            Name = "Monitor", Line = at.Line, Col = at.Col,
                        },
                        Name = member, Line = at.Line, Col = at.Col,
                    },
                    Line = at.Line,
                    Col = at.Col,
                };

                CallExpr enter = MonitorCall("Enter");
                enter.Args.Add(Held());
                CallExpr exit = MonitorCall("Exit");
                exit.Args.Add(Held());

                Block protectedBody = body as Block
                    ?? new Block { Line = body.Line, Col = body.Col };
                if (body is not Block)
                {
                    protectedBody.WritableStatements.Add(body);
                }

                Block cleanup = new() { Line = at.Line, Col = at.Col };
                cleanup.WritableStatements.Add(new ExprStmt
                {
                    Expr = exit, Line = at.Line, Col = at.Col,
                });

                Block lowered = new() { Line = at.Line, Col = at.Col };
                lowered.WritableStatements.Add(new LocalDecl
                {
                    Name = held, Init = guarded, Line = at.Line, Col = at.Col,
                });
                lowered.WritableStatements.Add(new ExprStmt
                {
                    Expr = enter, Line = at.Line, Col = at.Col,
                });
                lowered.WritableStatements.Add(new TryStmt
                {
                    Body = protectedBody, Finally = cleanup,
                    Line = at.Line, Col = at.Col,
                });
                return lowered;
            }

            case Tok.KwIf:
            {
                _i++;
                Expect(Tok.LParen, "'(' after 'if'");
                Expr cond = ParseExpr();
                Expect(Tok.RParen, "')' after the condition");
                Stmt then = ParseStmt();
                Stmt? els = Take(Tok.KwElse) ? ParseStmt() : null;
                return new IfStmt { Cond = cond, Then = then, Else = els, Line = at.Line, Col = at.Col };
            }

            case Tok.KwWhile:
            {
                _i++;
                Expect(Tok.LParen, "'(' after 'while'");
                Expr cond = ParseExpr();
                Expect(Tok.RParen, "')' after the condition");
                return new WhileStmt { Cond = cond, Body = ParseStmt(), Line = at.Line, Col = at.Col };
            }

            case Tok.KwDo:
            {
                _i++;
                Stmt body = ParseStmt();
                Expect(Tok.KwWhile, "'while' after the body of a 'do'");
                Expect(Tok.LParen, "'(' after 'while'");
                Expr cond = ParseExpr();
                Expect(Tok.RParen, "')' after the condition");
                Expect(Tok.Semi, "';' after 'do ... while (...)'");
                return new DoStmt { Cond = cond, Body = body, Line = at.Line, Col = at.Col };
            }

            case Tok.KwFor:
            {
                _i++;
                Expect(Tok.LParen, "'(' after 'for'");
                Stmt? init = At(Tok.Semi) ? null : ParseSimpleStmt();
                Expect(Tok.Semi, "';' after the initialiser");
                Expr? cond = At(Tok.Semi) ? null : ParseExpr();
                Expect(Tok.Semi, "';' after the condition");

                ForStmt f = new() { Init = init, Cond = cond, Body = null!, Line = at.Line, Col = at.Col };

                if (!At(Tok.RParen))
                {
                    do
                    {
                        f.Step.Add(ParseExpr());
                    }
                    while (Take(Tok.Comma));
                }

                Expect(Tok.RParen, "')' after the for clauses");
                Stmt body = ParseStmt();

                ForStmt built = new() { Init = init, Cond = cond, Body = body, Line = at.Line, Col = at.Col };
                built.Step.AddRange(f.Step);
                return built;
            }

            case Tok.KwForeach:
            {
                _i++;
                Expect(Tok.LParen, "'(' after 'foreach'");

                // A DECONSTRUCTING LOOP: `foreach ((Op op, Fmt f) in table)`,
                // and `foreach (var (op, f) in table)` for the same thing with
                // the types left to the element.
                //
                // A TUPLE TYPE BEGINS WITH THE SAME BRACKET, and what settles
                // it is what comes after the closing one: `foreach ((string In,
                // string Alias) alias in file.Aliases)` names ONE variable
                // whose type is a tuple, while a deconstruction has the names
                // inside the brackets and `in` straight after them.
                if ((At(Tok.LParen) && !NameAfterBrackets())
                    || (At(Tok.KwVar) && Ahead().Kind == Tok.LParen))
                {
                    bool inferred = Take(Tok.KwVar);

                    _i++;                       // the '('

                    List<Binding> bound = ReadBindings(inferred);

                    Expect(Tok.RParen, "')' after the names being bound");
                    Expect(Tok.KwIn, "'in' after the names being bound");

                    Expr over = ParseExpr();

                    Expect(Tok.RParen, "')' after the sequence");

                    return new ForeachStmt
                    {
                        Name = bound[0].Name, Sequence = over, Bindings = bound,
                        Body = ParseStmt(), Line = at.Line, Col = at.Col,
                    };
                }

                TypeRef? type = At(Tok.KwVar) ? null : ParseTypeRef();

                if (type is null)
                {
                    _i++;
                }

                string name = Expect(Tok.Ident, "the loop variable").Text;
                Expect(Tok.KwIn, "'in' after the loop variable");
                Expr seq = ParseExpr();
                Expect(Tok.RParen, "')' after the sequence");
                return new ForeachStmt { Type = type, Name = name, Sequence = seq, Body = ParseStmt(), Line = at.Line, Col = at.Col };
            }

            // ITERATORS ARE LOWERED TO THEIR EAGER SEQUENCE at the syntax
            // boundary. The language-visible contract used by the compiler is
            // IEnumerable<T>: values arrive in order and enumeration may begin
            // after the method returns. A hidden List<T> implements that exact
            // surface, while keeping yield out of the binder, GIR and backend.
            //
            // This deliberately recognises `yield` contextually, as C# does;
            // it remains a legal identifier everywhere it is not followed by
            // return or break.
            case Tok.Ident when Cur.Text == "yield" && Ahead().Kind == Tok.KwReturn:
            {
                if (_iteratorTarget is null)
                {
                    throw Error("'yield return' is only valid inside a method");
                }

                _i += 2;
                Expr value = ParseExpr();
                Expect(Tok.Semi, "';' after 'yield return'");
                _iteratorSawYield = true;
                return new YieldStmt { Value = value, Line = at.Line, Col = at.Col };
            }

            case Tok.Ident when Cur.Text == "yield" && Ahead().Kind == Tok.KwBreak:
            {
                if (_iteratorTarget is null)
                {
                    throw Error("'yield break' is only valid inside a method");
                }

                _i += 2;
                Expect(Tok.Semi, "';' after 'yield break'");
                _iteratorSawYield = true;
                return new YieldStmt { Line = at.Line, Col = at.Col };
            }

            case Tok.KwReturn:
            {
                _i++;
                // `return ref x;` answers the variable itself, from a method
                // that returns by reference (ParseRefValue).
                Expr? value = At(Tok.Semi) ? null : At(Tok.KwRef) ? ParseRefValue() : BareDefault(_returns) ?? ParseExpr();
                Expect(Tok.Semi, "';' after 'return'");
                return new ReturnStmt { Value = value, Line = at.Line, Col = at.Col };
            }

            case Tok.KwBreak:
                _i++;
                Expect(Tok.Semi, "';' after 'break'");
                return new BreakStmt { Line = at.Line, Col = at.Col };

            case Tok.KwContinue:
                _i++;
                Expect(Tok.Semi, "';' after 'continue'");
                return new ContinueStmt { Line = at.Line, Col = at.Col };

            case Tok.Ident when Cur.Text == "goto" && Ahead().Kind == Tok.Ident:
            {
                _i++;
                string label = Cur.Text;
                _i++;
                Expect(Tok.Semi, "';' after 'goto' and its label");
                return new GotoStmt { Label = label, Line = at.Line, Col = at.Col };
            }

            // `name: statement`. Nothing else starts a statement with a name
            // and a colon.
            case Tok.Ident when Ahead().Kind == Tok.Colon:
            {
                _i += 2;
                return new LabeledStmt { Label = at.Text, Body = ParseStmt(), Line = at.Line, Col = at.Col };
            }

            case Tok.Ident when Cur.Text == "goto"
                                  && Ahead().Kind is Tok.KwCase or Tok.KwDefault:
            {
                _i++;
                bool toDefault = Take(Tok.KwDefault);

                if (!toDefault)
                {
                    Expect(Tok.KwCase, "'case' after 'goto'");
                }

                Expr? value = toDefault ? null : ParseExpr();
                Expect(Tok.Semi, "';' after 'goto case'");
                return new GotoCaseStmt
                {
                    Value = value, IsDefault = toDefault,
                    Line = at.Line, Col = at.Col,
                };
            }

            case Tok.KwThrow:
            {
                _i++;

                // A BARE `throw;` RETHROWS WHAT THE ENCLOSING CATCH CAUGHT, and
                // it is spelt as exactly that: a throw of the clause's
                // variable. A clause that did not name its exception is given a
                // hidden name for the purpose, which the binder declares like
                // any other. Preserve its rethrow identity for stack traces.
                if (At(Tok.Semi))
                {
                    if (_catches.Count == 0)
                    {
                        throw Error("'throw;' with nothing to throw is only allowed inside a catch block");
                    }

                    CatchScope caught = _catches[^1];
                    caught.Name ??= $"$caught${_hidden++}";

                    _i++;
                    return new ThrowStmt
                    {
                        Value = new NameExpr { Name = caught.Name, Line = at.Line, Col = at.Col },
                        IsRethrow = true,
                        Line = at.Line, Col = at.Col,
                    };
                }

                Expr value = ParseExpr();
                Expect(Tok.Semi, "';' after 'throw'");
                return new ThrowStmt { Value = value, Line = at.Line, Col = at.Col };
            }

            // `unsafe { ... }` is the block form, and it is exactly the block.
            // Nothing is gated, so there is nothing for the statement to be
            // other than what it contains -- see Mods.Unsafe for why.
            case Tok.Ident when Cur.Text == "unsafe" && Ahead().Kind == Tok.LBrace:
                _i++;
                return ParseBlock();

            // `static int Add(int a, int b) { ... }` -- a local function that
            // promises to capture nothing. The promise is about what the BODY
            // may name, which the binder enforces for every local function
            // already by giving it no enclosing scope it should not have; the
            // word therefore adds no meaning here and is stepped over.
            // `static`, `async`, both: a local function's modifiers. `async`
            // makes it an async method of its own, as C# has it; `static`
            // adds no meaning here and is stepped over.
            case Tok.KwStatic or Tok.Ident when LocalFunctionModifiers() is int modifiers and > 0:
            {
                bool isAsync = false;
                for (int k = 0; k < modifiers; k++)
                {
                    isAsync |= _t[_i].Kind == Tok.Ident;
                    _i++;
                }
                return ParseLocalFunction(Cur, isAsync);
            }

            case Tok.KwSwitch:
                return ParseSwitch();

            case Tok.KwTry:
                return ParseTry();

            default:
            {
                // A LOCAL FUNCTION ENDS WITH ITS BLOCK, not a semicolon, which
                // is the one thing that makes it look unlike the declaration it
                // becomes. Recognised here rather than inside ParseSimpleStmt
                // so that the semicolon below is never asked for.
                if (StartsLocalFunction())
                {
                    return ParseLocalFunction(Cur);
                }

                Stmt s = ParseSimpleStmt();
                Expect(Tok.Semi, "';' after the statement");
                return s;
            }
        }
    }

    /// The arms of a switch EXPRESSION.
    ///
    ///     x switch { 1 => "one", 2 => "two", _ => "many" }
    ///     n switch { IsExpr i => i.Type, AsExpr a => a.Type, _ => null }
    ///     n switch { long v when v > 0 => 1, _ => 0 }
    ///
    /// Three pattern shapes, and the second is the one that matters here: the
    /// compiler's own source is full of switches over a node kind, and a switch
    /// expression that only took constants would be a switch expression that
    /// could not read this file.
    /// Names the value a property pattern in a switch arm matched, when the
    /// author did not name it.
    private int _patterns;

    private SwitchExpr ParseSwitchExpr(Expr subject)
    {
        Token at = _t[_i++];                    // 'switch'

        Expect(Tok.LBrace, "'{' to open the switch arms");

        SwitchExpr sw = new() { Subject = subject, Line = at.Line, Col = at.Col };

        while (!At(Tok.RBrace) && !At(Tok.End))
        {
            Token armAt = Cur;
            int armStart = _i;

            // THE ARM'S OWN ARROW IS NEVER A LAMBDA'S, whatever comes before
            // it: `Point(0, 0) =>` and `nameof(X) =>` are a pattern and a
            // constant, not a lambda with its result written (C# 10). Roslyn
            // reads an arm's head as a pattern, which has no lambda in it.
            int armWas = _armArrow;
            _armArrow = ArmArrow();
            Expr? value = null;
            TypeRef? type = null;
            string? binding = null;
            Expr? guard = null;
            bool discard = false;
            List<TypeRef> typeAlternatives = new();

            // `_` matches anything. It is an ordinary identifier to the lexer,
            // so it is recognised by its text and only where a pattern belongs.
            if (At(Tok.Ident) && Cur.Text == "_")
            {
                _i++;
                discard = true;
            }
            // A RELATIONAL PATTERN -- `>= 90 => "A"` -- is a guard with no
            // constant to compare first, which is exactly how it is built: the
            // arm matches anything, as `_` does, and the comparison over the
            // subject is its `when`. The pattern is read by the same code that
            // reads it after `is`, so `> 0 and < 10` and `< 0 or > 100` come
            // with it; a SubjectExpr stands for the subject, and the binder
            // resolves it to the slot the switch evaluated its subject into.
            // THE SAME PATTERN PARSER `is` USES, for every shape an arm can
            // take that is not a type or a constant on its own.
            //
            // A relational pattern -- `>= 90 => "A"` -- is a guard with no
            // constant to compare first, and so are `not null`, `null or ""`
            // and a positional pattern with a discard in it: the arm matches
            // anything, as `_` does, and the question is its `when`. Routing
            // them all through one parser is what gives them `and`/`or` with
            // the right precedence, `not`, nesting and parenthesised groups
            // without any of that being written twice.
            //
            // A SubjectExpr stands for the subject, and the binder resolves it
            // to the slot the switch evaluated its subject into -- so the
            // subject is evaluated once however many times the pattern asks
            // about it.
            // `var big => …` MATCHES ANYTHING AND NAMES IT: `_` with a name on
            // it. There is no test, and the name has the subject's own type --
            // nullability and all, since a pattern that asked nothing proved
            // nothing. `var _` is the discard designation and names nothing.
            else if (At(Tok.KwVar) && Ahead().Kind == Tok.Ident
                     && Ahead().Text is not ("or" or "and" or "when"))
            {
                _i++;
                string named = _t[_i++].Text;

                discard = true;
                binding = named == "_" ? null : named;
            }
            // A PROPERTY PATTERN WITH NO TYPE BEFORE IT -- `{ Label: var name }
            // => …` -- asks about the subject's members and nothing about what
            // it is. The typed form `Leaf { Kind: 2 } =>` is read further down,
            // where the type is; this is the same arm with the type left out,
            // which C# allows wherever the subject's own type already says
            // enough.
            else if (Cur.Kind is Tok.Lt or Tok.Gt or Tok.LtEq or Tok.GtEq or Tok.KwNull
                     || (At(Tok.Ident) && Cur.Text == "not")
                     || At(Tok.LBrace)
                     || (At(Tok.LParen) && PositionalPatternAhead())
                     || (At(Tok.KwVar) && Ahead().Kind == Tok.LParen)
                     || At(Tok.LBracket)
                     || TypeThenParen())
            {
                discard = true;
                guard = ParseIsPattern(new SubjectExpr { Line = armAt.Line, Col = armAt.Col }, armAt);
            }
            else
            {
                // A TYPE PATTERN, if what follows the type is a name or the
                // arrow. Told apart from a constant the same way an `out`
                // argument is: try it, and put the index back if it was not one.
                int was = _i;
                bool matched = false;

                if (!matched && At(Tok.Ident))
                {
                    TypeRef? parsed = null;

                    try
                    {
                        parsed = ParseTypeRef();
                    }
                    catch (Exception)
                    {
                        parsed = null;
                    }

                    // `or` and `and` are excluded for the same reason `when`
                    // is: they are not keywords in this grammar, so
                    // `Prim.Bool or Prim.I8 => 1` otherwise reads as the type
                    // Prim.Bool bound to a variable called `or`, and the arm
                    // then wants an arrow where the next alternative is.
                    // A QUALIFIED NAME IS A CONSTANT, NOT A TYPE, here for the
                    // same reason it is after `is`: `Lit.Str => 4` names an
                    // enum member, and reading it as a type test made the arm
                    // report that 'Lit.Str' is not a known type -- which is
                    // true, and not what was written.
                    if (parsed is not null && parsed.Name.Contains('.'))
                    {
                        parsed = null;
                    }

                    // `LiteralExpr { Kind: Lit.Int } l => …` -- a TYPE PATTERN
                    // with a property pattern inside it, which is how this
                    // compiler's own Header.cs and Fold.cs are written.
                    //
                    // The property test becomes the arm's GUARD, over the name
                    // the arm binds -- which is what it means, and needs no new
                    // machinery: `when l.Kind == Lit.Int`. A name is invented
                    // when the author did not write one, since the guard has to
                    // call the matched value something.
                    if (parsed is not null && At(Tok.LBrace))
                    {
                        type = parsed;
                        matched = true;

                        Token braceAt = Cur;
                        string held = At(Tok.Ident) ? "" : $"$matched${_patterns++}";
                        Expr test = ParseMemberPattern(
                            new NameExpr { Name = "$held$", Line = braceAt.Line, Col = braceAt.Col },
                            braceAt, designates: false);

                        binding = At(Tok.Ident) && Cur.Text is not ("when" or "or" or "and")
                                ? _t[_i++].Text
                                : held;

                        // The pattern was built against a placeholder, because
                        // the name it binds is written AFTER it.
                        Rename(test, "$held$", binding);
                        guard = test;
                    }
                    else if (parsed is not null && At(Tok.Ident) && Cur.Text is not ("when" or "or" or "and"))
                    {
                        type = parsed;
                        binding = _t[_i++].Text;
                        matched = true;
                    }
                    else if (parsed is not null && (At(Tok.FatArrow) || At(Tok.Ident)))
                    {
                        // `Foo => ...` -- a type with nothing bound. Only when
                        // an arrow really follows, or it is a constant whose
                        // name happens to parse as a type.
                        if (At(Tok.FatArrow))
                        {
                            type = parsed;
                            matched = true;
                        }
                        else if (Cur.Text == "or")
                        {
                            // `ReturnStmt or ThrowStmt => true`: alternatives
                            // may be TYPES just as readily as constants. They
                            // bind no variable, so each becomes an equivalent
                            // arm after the shared result has been parsed.
                            type = parsed;
                            matched = true;
                        }
                    }

                    if (!matched)
                    {
                        _i = was;
                    }
                }

                if (!matched)
                {
                    value = ParseExpr();
                }
            }

            // A COMBINATOR AFTER A SIMPLE ARM -- `int i and (< 0 or > 100)`,
            // `Circle or Rect(_, 1)`, `1 or > 5` -- makes it a whole pattern,
            // which the shared pattern parser reads with its `and`/`or`
            // precedence, as `is` does: read again from the arm's start.
            if (AtPatternCombinator())
            {
                _i = armStart;
                value = null;
                type = null;
                binding = null;
                typeAlternatives.Clear();
                discard = true;
                guard = ParseIsPattern(new SubjectExpr { Line = armAt.Line, Col = armAt.Col }, armAt);
            }

            // `Prim.Bool or Prim.I8 or Prim.U8 => 1`.
            //
            // ONE ARM EACH, sharing the result. Arms are tried in order and
            // the first that matches wins, so three arms with the same answer
            // IS the alternative -- no new node, nothing for the binder or the
            // code generator to learn. `or` arrives as an identifier because it
            // is not a keyword in this grammar.
            List<Expr> alternatives = new();

            while (type != null && binding is null
                   && At(Tok.Ident) && Cur.Text == "or")
            {
                _i++;
                typeAlternatives.Add(ParseTypeRef(pattern: true));
            }

            while (value != null && At(Tok.Ident) && Cur.Text == "or")
            {
                _i++;
                alternatives.Add(ParseExpr());
            }

            if (At(Tok.Ident) && Cur.Text == "when")
            {
                _i++;

                // A property pattern already put a test here; `when` on top of
                // it means both must hold, which is what C# means too.
                //
                // THE ARM'S OWN ARROW ENDS THE GUARD. `when w == h => …` is
                // not `h => …`, a lambda: the first `=>` outside any bracket
                // belongs to the arm, and a lambda inside the guard -- `when
                // xs.Any(x => x > 1)` -- is inside brackets.
                int wasArrow = _armArrow;
                _armArrow = ArmArrow();
                Expr written;
                try
                {
                    written = ParseExpr();
                }
                finally
                {
                    _armArrow = wasArrow;
                }

                guard = guard is null
                      ? written
                      : new BinaryExpr
                        {
                            Op = BinOp.AndAlso, Left = guard, Right = written,
                            Line = written.Line, Col = written.Col,
                        };
            }

            _armArrow = armWas;
            Expect(Tok.FatArrow, "'=>' after the pattern");

            SwitchArm arm = new()
            {
                Value = value, Type = type, Binding = binding, When = guard,
                Discard = discard, Result = ParseExpr(),
                Line = armAt.Line, Col = armAt.Col,
            };

            sw.Arms.Add(arm);

            foreach (Expr other in alternatives)
            {
                sw.Arms.Add(new SwitchArm
                {
                    Value = other, Type = type, Binding = binding, When = guard,
                    Discard = discard, Result = arm.Result,
                    Line = armAt.Line, Col = armAt.Col,
                });
            }

            foreach (TypeRef other in typeAlternatives)
            {
                sw.Arms.Add(new SwitchArm
                {
                    Type = other,
                    Result = arm.Result,
                    Line = armAt.Line,
                    Col = armAt.Col,
                });
            }

            if (!Take(Tok.Comma))
            {
                break;
            }
        }

        Expect(Tok.RBrace, "'}' to close the switch arms");

        // NOTHING MATCHED: C# throws SwitchExpressionException, handing it the
        // value (C# 12.11). Unless the last arm already takes everything.
        if (sw.Arms.Count > 0 && sw.Arms[^1] is not { Discard: true, When: null })
        {
            NewExpr thrown = new()
            {
                Type = new TypeRef { Name = "SwitchExpressionException", Line = at.Line, Col = at.Col },
                Line = at.Line, Col = at.Col,
            };
            // AS AN OBJECT, whatever it is: a string subject would otherwise
            // reach the (string? message) constructor.
            thrown.Args.Add(new CastExpr
            {
                Type = new TypeRef { Name = "object", Nullable = true, Line = at.Line, Col = at.Col },
                Operand = new SubjectExpr { Line = at.Line, Col = at.Col },
                Line = at.Line, Col = at.Col,
            });
            thrown.WritableArgNames.Add(null);
            sw.Arms.Add(new SwitchArm
            {
                Discard = true, Fallback = true,
                Result = new ThrowExpr { Value = thrown, Line = at.Line, Col = at.Col },
                Line = at.Line, Col = at.Col,
            });
        }
        return sw;
    }

    /// <summary>
    /// `subject is &lt; 0 or &gt; 63`, as the comparison it means.
    ///
    /// A relational pattern is a question about a value, and the answer is an
    /// ordinary boolean expression -- so this builds one rather than inventing
    /// a node for it. `or` becomes ||, `and` becomes &amp;&amp;, a bare constant
    /// becomes ==, and a relational operator becomes itself.
    ///
    /// THE SUBJECT MUST BE SIMPLE, and that restriction is real rather than
    /// laziness. Desugaring repeats the subject once per alternative, so a
    /// subject with side effects would have them more than once -- where C#
    /// evaluates it exactly once. A name or a literal can be repeated safely;
    /// anything else is refused with a message rather than quietly run twice.
    /// </summary>
    /// <summary>
    /// Puts the real name into a pattern that was built against a placeholder.
    ///
    /// A property pattern in a switch arm is written BEFORE the name it binds
    /// -- `LiteralExpr { Kind: Lit.Int } l` -- so the test is built first and
    /// the name arrives after it.
    /// </summary>
    private static void Rename(Expr e, string from, string to)
    {
        switch (e)
        {
            case NameExpr n when n.Name == from:
                n.Name = to;
                break;

            case BinaryExpr b:
                Rename(b.Left, from, to);
                Rename(b.Right, from, to);
                break;

            case MemberExpr m:
                Rename(m.Target, from, to);
                break;

            case UnaryExpr u:
                Rename(u.Operand, from, to);
                break;

            case CallExpr c:
                Rename(c.Target, from, to);

                foreach (Expr a in c.Args)
                {
                    Rename(a, from, to);
                }
                break;

            case IndexExpr ix:
                Rename(ix.Target, from, to);

                foreach (Expr a in ix.Args)
                {
                    Rename(a, from, to);
                }
                break;

            case IsExpr isx:
                Rename(isx.Operand, from, to);
                break;
        }
    }

    /// <summary>
    /// Whether the '(' at the cursor opens a lambda's parameter list.
    ///
    /// Scans to the matching ')' and looks at what follows. Nothing shorter
    /// works: `(a, b) => ...` and `(a, b)` as a tuple-ish expression, and
    /// `(int)x` as a cast, all begin the same way, and only the arrow after
    /// the close tells them apart. Bounded by the nesting count, so a
    /// parenthesised expression of any depth is scanned once and cheaply.
    /// </summary>
    private bool IsLambdaHead() => IsLambdaHeadAt(_i);

    /// <summary>Where the switch arm being guarded has its `=>`, or -1. See the `when` in a switch expression.</summary>
    private int _armArrow = -1;

    /// <summary>The first `=>` from the cursor outside any bracket.</summary>
    private int ArmArrow()
    {
        int depth = 0;
        for (int j = _i; j < _t.Count; j++)
        {
            switch (_t[j].Kind)
            {
                case Tok.LParen or Tok.LBracket or Tok.LBrace: depth++; break;
                case Tok.RParen or Tok.RBracket or Tok.RBrace: if (--depth < 0) return -1; break;
                case Tok.FatArrow when depth == 0: return j;
                case Tok.End: return -1;
            }
        }
        return -1;
    }

    /// <summary>The name C#'s synthesised delegate of a shape is declared under (AnonymousDelegates).</summary>
    public static string AnonymousDelegateName(string shape) => "__AnonymousDelegate_" + shape;

    /// <summary>
    /// THE DELEGATE C# SYNTHESISES for a lambda's or a method group's natural
    /// type where no Func or Action can say it (C# 10): a parameter passed by
    /// reference, a result returned by one, more than sixteen parameters.
    /// Declared once a unit, by its SHAPE -- a letter a parameter, v by
    /// value, r ref, o out, i in, then the result: V nothing, R a value, F
    /// by reference, G by ref readonly -- and generic over the parameters'
    /// types and the result, so one declaration serves every signature of
    /// that shape (Binder.NaturalTypes asks for it; the driver adds it).
    /// </summary>
    public static List<TypeDecl> AnonymousDelegates(string shape)
    {
        int split = shape.LastIndexOf('_');
        string modes = shape[..split];
        char result = shape[split + 1];
        System.Text.StringBuilder text = new("internal delegate ");
        text.Append(result switch { 'V' => "void", 'F' => "ref TR", 'G' => "ref readonly TR", _ => "TR" });
        text.Append(' ').Append(AnonymousDelegateName(shape));
        List<string> typeParams = Enumerable.Range(0, modes.Length).Select(k => "T" + k).ToList();
        if (result != 'V') typeParams.Add("TR");
        if (typeParams.Count > 0) text.Append('<').Append(string.Join(", ", typeParams)).Append('>');
        text.Append('(');
        for (int k = 0; k < modes.Length; k++)
        {
            if (k > 0) text.Append(", ");
            text.Append(modes[k] switch { 'r' => "ref ", 'o' => "out ", 'i' => "in ", _ => "" }).Append('T').Append(k).Append(" a").Append(k);
        }
        text.Append(");");
        string source = text.ToString();
        Parser sub = new(Lexer.Tokenize(source, "<anonymous delegate>"), "<anonymous delegate>") { Source = source };
        CompilationUnit made = sub.ParseUnit();
        foreach (TypeDecl declared in made.Types)
        {
            // This unit's alone, as a local function's delegate is.
            declared.LocalOnly = true;
        }
        return made.Types;
    }

    /// <summary>
    /// A lambda with its result written in front, `[ref [readonly]] T
    /// (params) => body`, the cursor on the first word of the result.
    /// </summary>
    private LambdaExpr TypedLambda(Token at)
    {
        Mods returnMods = Mods.None;
        if (Take(Tok.KwRef))
        {
            returnMods = Mods.RefReturn;
            if (Take(Tok.KwReadonly)) returnMods |= Mods.RefReadonlyReturn;
        }
        TypeRef returns = ParseTypeRef();
        LambdaExpr inner = (LambdaExpr)ParseUnary();
        LambdaExpr made = new()
        {
            Body = inner.Body, BlockBody = inner.BlockBody, Async = inner.Async, TypesWritten = inner.TypesWritten,
            Returns = returns, ReturnMods = returnMods,
            Line = at.Line, Col = at.Col,
        };
        made.WritableParams.AddRange(inner.Params);
        made.WritableAttributes.AddRange(inner.Attributes);
        return made;
    }

    /// <summary>
    /// Whether `ref` here begins a LAMBDA WITH ITS RESULT WRITTEN, `ref int
    /// (int[] a) => ref a[0]` or `ref readonly T (...) => ...` (C# 10): a
    /// type, then a bracket that the arrow follows. Anywhere else `ref` is a
    /// variable's, and `ref a[0]` and `ref F(x)` are not types and brackets
    /// with an arrow after.
    /// </summary>
    private bool StartsRefLambda()
    {
        if (!At(Tok.KwRef)) return false;
        int j = _i + 1;
        if (j < _t.Count && _t[j].Kind == Tok.KwReadonly) j++;
        return TypedLambdaAt(j);
    }

    /// <summary>
    /// Whether a LAMBDA WITH ITS RESULT WRITTEN starts here: `int (int x) =>
    /// x`, `List<int> (string s) => ...`, `(int, int) (int x) => (x, x)` (C#
    /// 10). A type, then a bracketed parameter list the arrow follows.
    ///
    /// AS ROSLYN HAS IT, in an expression and nowhere else. `Point(1, 2) =>`
    /// in a switch arm is a positional pattern and the arm's arrow, never a
    /// lambda: Roslyn reads an arm's pattern with its pattern parser, which
    /// has no lambdas in it, and here the pattern forms are taken before an
    /// arm falls back to an expression (TypeThenParen), and the arm's own
    /// arrow is never a lambda's (_armArrow, IsLambdaHeadAt).
    /// </summary>
    private bool StartsTypedLambda() => TypedLambdaAt(_i);

    private bool TypedLambdaAt(int j)
    {
        int end = TypeEndAt(j);
        return end > j && end < _t.Count && _t[end].Kind == Tok.LParen && IsLambdaHeadAt(end)
            && !(_t[end - 1].Kind == Tok.Question && ColonAhead(end));
    }

    /// <summary>
    /// `b ? () => A(1) : null`: a type ending in `?` before a lambda's head
    /// reads as a nullable result, `int? (x) => ...` -- or as a conditional,
    /// whose `:` then follows at the same depth before the expression ends.
    /// That `:` makes it the conditional, as Roslyn reads it.
    /// </summary>
    private bool ColonAhead(int from)
    {
        int depth = 0, inner = 0;
        for (int k = from; k < _t.Count; k++)
        {
            switch (_t[k].Kind)
            {
                case Tok.LParen or Tok.LBracket or Tok.LBrace: depth++; break;
                case Tok.RParen or Tok.RBracket or Tok.RBrace: if (--depth < 0) return false; break;
                case Tok.Semi or Tok.End: return false;
                case Tok.Comma when depth == 0: return false;
                // A conditional inside the lambda's body takes its own `:`.
                case Tok.Question when depth == 0: inner++; break;
                case Tok.Colon when depth == 0: if (inner == 0) return true; inner--; break;
            }
        }
        return false;
    }

    /// <summary>
    /// Where a type written from token <paramref name="j"/> ends, by its
    /// shape alone and consuming nothing: a dotted name with its type
    /// arguments, or a tuple of two or more, then `?`, `[]` and `*`. -1 when
    /// no type starts there.
    /// </summary>
    private int TypeEndAt(int j)
    {
        if (j >= _t.Count) return -1;
        if (_t[j].Kind == Tok.LParen)
        {
            int depth = 0, commas = 0;
            for (; j < _t.Count; j++)
            {
                Tok k = _t[j].Kind;
                if (k == Tok.LParen) depth++;
                else if (k == Tok.RParen) { if (--depth == 0) { j++; break; } }
                else if (k == Tok.Comma && depth == 1) commas++;
                else if (k is Tok.End or Tok.Semi or Tok.LBrace or Tok.FatArrow) return -1;
            }
            if (depth != 0 || commas == 0) return -1;
        }
        else
        {
            if (_t[j].Kind != Tok.Ident) return -1;
            j++;
            while (j + 1 < _t.Count && _t[j].Kind == Tok.Dot && _t[j + 1].Kind == Tok.Ident) j += 2;
            if (j < _t.Count && _t[j].Kind == Tok.Lt)
            {
                int depth = 0;
                for (; j < _t.Count; j++)
                {
                    Tok k = _t[j].Kind;
                    if (k == Tok.Lt) depth++;
                    else if (k == Tok.Gt) depth--;
                    else if (k == Tok.Shr) depth -= 2;
                    else if (k == Tok.UShr) depth -= 3;
                    else if (k is not (Tok.Ident or Tok.Comma or Tok.Dot or Tok.Question or Tok.LBracket or Tok.RBracket
                             or Tok.LParen or Tok.RParen))
                    {
                        return -1;
                    }
                    if (depth <= 0) { j++; break; }
                }
                if (depth != 0) return -1;
            }
        }
        while (j < _t.Count)
        {
            if (_t[j].Kind is Tok.Question or Tok.Star) { j++; continue; }
            if (_t[j].Kind == Tok.LBracket)
            {
                int k = j + 1;
                while (k < _t.Count && _t[k].Kind == Tok.Comma) k++;
                if (k < _t.Count && _t[k].Kind == Tok.RBracket) { j = k + 1; continue; }
            }
            break;
        }
        return j;
    }

    /// <summary>
    /// Whether attributes here belong to a LAMBDA, `[Pure] (int x) => x` or
    /// `[A] x => x` (C# 10): bracketed groups, then a lambda's head. A
    /// collection expression is brackets with no arrow after the head that
    /// would follow.
    /// </summary>
    private bool StartsAttributedLambda()
    {
        int j = _i;
        while (j < _t.Count && _t[j].Kind == Tok.LBracket)
        {
            int depth = 0;
            for (; j < _t.Count; j++)
            {
                if (_t[j].Kind == Tok.LBracket) depth++;
                else if (_t[j].Kind == Tok.RBracket) { if (--depth == 0) { j++; break; } }
                else if (_t[j].Kind is Tok.End or Tok.Semi) return false;
            }
            if (depth != 0) return false;
        }
        if (j >= _t.Count) return false;
        if (_t[j].Kind == Tok.Ident && _t[j].Text == "async") j++;
        if (j + 1 < _t.Count && _t[j].Kind == Tok.Ident && _t[j + 1].Kind == Tok.FatArrow && j + 1 != _armArrow) return true;
        if (_t[j].Kind == Tok.LParen && IsLambdaHeadAt(j)) return true;
        if (_t[j].Kind == Tok.KwRef)
        {
            j++;
            if (j < _t.Count && _t[j].Kind == Tok.KwReadonly) j++;
        }
        return TypedLambdaAt(j);
    }

    /// <summary>
    /// Reads attribute groups written on a lambda or one of its parameters,
    /// into <paramref name="into"/> when given, without disturbing the ones
    /// the member being read collected (SkipAttributes keeps only the last).
    /// </summary>
    private void SkipLambdaAttributes(List<AttributeRef>? into)
    {
        List<string> names = new(_attributes);
        List<AttributeRef> parts = new(_attributeParts);
        SkipAttributes();
        into?.AddRange(CapturedAttributes());
        _attributes.Clear();
        _attributes.AddRange(names);
        _attributeParts.Clear();
        _attributeParts.AddRange(parts);
    }

    private bool IsLambdaHeadAt(int from)
    {
        int depth = 0;

        for (int i = from; i < _t.Count; i++)
        {
            if (_t[i].Kind == Tok.LParen)
            {
                depth++;
            }
            else if (_t[i].Kind == Tok.RParen)
            {
                depth--;

                if (depth == 0)
                {
                    return i + 1 < _t.Count && _t[i + 1].Kind == Tok.FatArrow && i + 1 != _armArrow;
                }
            }
            else if (_t[i].Kind == Tok.End)
            {
                return false;
            }
        }
        return false;
    }

    /// <summary>
    /// Reads a lambda's body, which is one expression or a block.
    ///
    /// A parameter's TYPE IS LEFT EMPTY, deliberately. What a lambda is
    /// depends entirely on what it is being passed to -- `x => x + 1` is a
    /// Func&lt;int,int&gt; in one call and a Func&lt;long,long&gt; in the next --
    /// so the binder fills these in from the wanted type and nothing before
    /// the binder could.
    /// </summary>
    private LambdaExpr Finish(LambdaExpr made, List<string> names, List<Tok>? modifiers = null, List<TypeRef?>? types = null)
    {
        // A block lambda's `return default;` waits for the binder, which knows
        // what the lambda is converted to; the method's return type is not it.
        TypeRef? savedReturns = _returns;
        _returns = null;
        // `=> ref a[0]`: the body of a lambda that returns by reference is
        // the variable it answers (ParseRefValue).
        // EVERY TYPE WRITTEN, or none: C# allows no mixture, and only the
        // first gives the lambda a natural type.
        bool typed = types is { Count: > 0 } && types.All(t => t is not null);
        LambdaExpr done = At(Tok.LBrace)
            ? new LambdaExpr { BlockBody = ParseBlock(), Line = made.Line, Col = made.Col, Async = made.Async, TypesWritten = typed }
            : new LambdaExpr { Body = At(Tok.KwRef) ? ParseRefValue() : ParseExpr(), Line = made.Line, Col = made.Col, Async = made.Async, TypesWritten = typed };
        _returns = savedReturns;

        for (int k = 0; k < names.Count; k++)
        {
            Tok modifier = modifiers is null ? Tok.End : modifiers[k];
            done.WritableParams.Add(new Param
            {
                Name = names[k],
                Type = typed ? types![k]! : new TypeRef { Name = "", Line = made.Line, Col = made.Col },
                // `in` is passed by address, read only, as a method's is
                // (ParseParams): a ref the body may not assign to.
                IsRef = modifier is Tok.KwRef or Tok.KwIn, IsOut = modifier == Tok.KwOut, IsReadOnlyRef = modifier == Tok.KwIn,
                Line = made.Line, Col = made.Col,
            });
        }
        return done;
    }

    /// <summary>
    /// `is { Prop: a or b }` -- not null, and Prop is one of these.
    /// </summary>
    /// <param name="designates">Whether a name after the braces is this
    /// pattern's to bind (`x is { } y`). A type in front (`x is Foo { } y`)
    /// binds that name itself, in its type test, and passes false.</param>
    private Expr ParseMemberPattern(Expr subject, Token at, bool designates = true)
    {
        // NO REFUSAL HERE ANY MORE. This threw for a subject that was not a
        // bare name, on the grounds that a property pattern reads the subject
        // twice -- once to test it for null and again to read the member -- and
        // told the programmer to put it in a local first.
        //
        // Reading it twice is a real constraint and the reason is right. It is
        // also the compiler's problem, not the programmer's: hoisting a subject
        // into a temporary is exactly what SubjectExpr and PatternExpr are for,
        // and they were already used for value patterns, which repeat the
        // subject for the same reason. ParseIsPattern hoists for this now, so
        // whatever arrives here is read once by construction.

        Expect(Tok.LBrace, "'{' to open the pattern");

        // AN EMPTY PROPERTY PATTERN IS A NULL TEST, and a useful one: `x is { }`
        // says x is not null, and `x is { } y` says so AND names it. It reads
        // oddly until you see that a property pattern always tests for null
        // first and then checks the members -- with no members, the null test
        // is the whole of it.
        //
        // The compiler's own Monomorphiser.cs and Codegen.cs both use the
        // binding form, which is the shortest way to write "if this is there,
        // call it this".
        if (At(Tok.RBrace))
        {
            _i++;

            // WITH A NAME AFTER IT, `x is { } y` also binds. There is no type
            // written and the parser cannot invent one -- only the checker
            // knows what x is -- so it writes the sentinel and the binder
            // resolves it against the operand.
            if (designates && PeekBinding() is string bound)
            {
                _i++;

                return new IsExpr
                {
                    Operand = subject,
                    Type = new TypeRef { Name = TypeRef.Same, Line = at.Line, Col = at.Col },
                    Binding = bound, Line = at.Line, Col = at.Col,
                };
            }

            return new BinaryExpr
            {
                Op = BinOp.Ne, Left = subject, PatternNullTest = true,
                Right = new LiteralExpr { Kind = Lit.Null, Text = "null", Line = at.Line, Col = at.Col },
                Line = at.Line, Col = at.Col,
            };
        }

        Expr member = PatternMember(subject, at, out Expr? reach);

        Expect(Tok.Colon, "':' after the member name");

        // NOT NULL, AND THE MEMBER MATCHES -- in that order, joined by '&&' so
        // the member is never read on a null.
        //
        // The read is marked as guarded, because it is: the test to its left is
        // the one that proves it, and it was written by this method three lines
        // up. Requiring it to be proved again would mean narrowing a MEMBER
        // ACCESS, which is a wider change than this pattern is worth.
        // A MEMBER'S TEST IS A WHOLE PATTERN, not only a constant.
        // `{ Target: NameExpr or ThisExpr }` asks whether the member is one of
        // two TYPES, and reading only constants here left the type name to be
        // taken as a constant and the rest of the pattern to fail somewhere
        // else. This compiler's own Parser.cs is full of that shape.
        Expr built = ParsePrimaryPattern(member, at);

        if (reach != null)
        {
            built = new BinaryExpr
            {
                Op = BinOp.AndAlso, Left = reach, Right = built,
                Line = at.Line, Col = at.Col,
            };
        }

        while (AtPatternCombinator())
        {
            bool any = Cur.Text == "or";

            _i++;
            built = new BinaryExpr
            {
                Op = any ? BinOp.OrElse : BinOp.AndAlso,
                Left = built, Right = ParsePrimaryPattern(member, at),
                Line = at.Line, Col = at.Col,
            };
        }

        // MORE THAN ONE MEMBER, joined by commas and meaning ALL of them:
        // `{ Kind: Lit.Int, Width: 4 }`. Only one was read, so a second landed
        // on the comma and reported a missing brace -- and this is the last
        // thing in the language the compiler's own sources needed.
        //
        // Each member is its own test against the same subject, which is why
        // they can simply be AND-ed: the null check in front of them all is
        // what makes reading any member safe.
        while (Take(Tok.Comma))
        {
            // C# permits the final member of a property pattern to carry a
            // trailing comma, which is especially common in multi-line
            // compiler code. The comma separates another member only when one
            // actually follows it.
            if (At(Tok.RBrace))
            {
                break;
            }

            Expr other = PatternMember(subject, at, out Expr? alsoReach);

            Expect(Tok.Colon, "':' after the member name");

            Expr more = ParsePrimaryPattern(other, at);

            if (alsoReach != null)
            {
                more = new BinaryExpr
                {
                    Op = BinOp.AndAlso, Left = alsoReach, Right = more,
                    Line = at.Line, Col = at.Col,
                };
            }

            while (AtPatternCombinator())
            {
                bool anyOf = Cur.Text == "or";

                _i++;
                more = new BinaryExpr
                {
                    Op = anyOf ? BinOp.OrElse : BinOp.AndAlso,
                    Left = more, Right = ParsePrimaryPattern(other, at),
                    Line = at.Line, Col = at.Col,
                };
            }

            built = new BinaryExpr
            {
                Op = BinOp.AndAlso, Left = built, Right = more,
                Line = at.Line, Col = at.Col,
            };
        }

        Expect(Tok.RBrace, "'}' to close the pattern");

        return new BinaryExpr
        {
            Op = BinOp.AndAlso,
            Left = new BinaryExpr
            {
                Op = BinOp.Ne, Left = subject, PatternNullTest = true,
                Right = new LiteralExpr { Kind = Lit.Null, Text = "null", Line = at.Line, Col = at.Col },
                Line = at.Line, Col = at.Col,
            },
            Right = built,
            Line = at.Line, Col = at.Col,
        };
    }

    /// <summary>
    /// Whether a pattern subject may simply be read again, or has to be put in
    /// a place of its own first.
    ///
    /// ANYTHING WITH A CALL IN IT IS EVALUATED ONCE. A value pattern repeats
    /// the test per alternative -- `x is 'a' or 'b'` is two comparisons -- so a
    /// subject that DOES something moves into a slot and the alternatives read
    /// that. A name, a literal, a field of one and an element of one are left
    /// alone, because reading them twice is reading the same word twice.
    ///
    /// A SubjectExpr is already a slot read -- it is what hoisting PRODUCES --
    /// so hoisting one again would wrap a PatternExpr round a PatternExpr and
    /// hand the inner one the outer one's subject. Switch labels arrive spelt
    /// that way, hoisted once by the switch on behalf of all of them.
    ///
    /// ONE COPY, DELIBERATELY. This predicate existed twice, here and in the
    /// `is` path, and they had already drifted apart once this session.
    /// </summary>
    private static bool Rereadable(Expr subject)
    {
        // An element only when its index is as simple as its array: `a[i++]`
        // read twice moved i twice.
        return subject is NameExpr or LiteralExpr or SubjectExpr
                       or MemberExpr { Target: NameExpr or ThisExpr }
                       or IndexExpr { Target: NameExpr, Args: [NameExpr or LiteralExpr] };
    }

    private Expr ParseValuePattern(Expr subject, Token at)
    {
        bool simple = Rereadable(subject);
        Expr stands = simple ? subject : new SubjectExpr { Line = at.Line, Col = at.Col };
        Expr built = ParseOnePattern(stands, at);

        while (AtPatternCombinator())
        {
            bool any = Cur.Text == "or";

            _i++;

            built = new BinaryExpr
            {
                Op = any ? BinOp.OrElse : BinOp.AndAlso,
                Left = built, Right = ParseOnePattern(stands, at),
                Line = at.Line, Col = at.Col,
            };
        }

        return simple
             ? built
             : new PatternExpr { Subject = subject, Test = built, Line = at.Line, Col = at.Col };
    }

    /// <summary>
    /// One whole pattern after `is`, whatever shape it takes.
    ///
    /// The same three readings the ordinary path uses -- a value pattern, a
    /// property pattern, a type test -- gathered so that `is not …` can negate
    /// any of them without each one learning the word.
    /// </summary>
    private Expr ParseIsPattern(Expr subject, Token at)
    {
        // `or` AND `and` JOIN ANY TWO PATTERNS, not only two constants.
        //
        // The alternatives used to be read inside the VALUE pattern alone, so
        // `x is 1 or 2` worked and `x is null or 0` did not: `null` has its own
        // branch, which returned before anything looked for an `or`. Every
        // shape of pattern combines in C# -- a constant, a null, a type, a
        // property pattern -- so the combining belongs out here, above all of
        // them, rather than inside one.
        //
        // ANYTHING WITH A CALL IN IT IS EVALUATED ONCE. The test is repeated
        // per alternative, so a subject that DOES something moves into a place
        // of its own and the alternatives refer to that. A name, a literal, a
        // field of one and an element of one are left alone, because reading
        // them twice is reading the same word twice.
        // THE SUBJECT IS ONLY HOISTED WHEN IT HAS TO BE.
        //
        // A value pattern repeats the test per alternative, so a subject that
        // DOES something has to be put somewhere first -- that is what
        // SubjectExpr is for. A type test, a null test and a property pattern
        // each read the subject once, and hoisting those wrapped a plain
        // `x.Foo() is Bar b` in a PatternExpr the code generator has no case
        // for. It reads the subject once; leave it alone.
        bool simple = Rereadable(subject);
        bool repeats = StartsValuePattern() || At(Tok.LBracket);
        Expr stands = simple || !repeats ? subject : new SubjectExpr { Line = at.Line, Col = at.Col };
        Expr built = ParsePrimaryPattern(stands, at);

        // WHAT `and` TESTS NEXT IS WHAT THE LEFT NARROWED TO (C# 11.2.12):
        // `o is int i and (< 0 or > 100)` compares the int, not the object.
        // A type test binds the narrowed value (a hidden name when none was
        // written), and the pattern after `and` reads that. `or` starts again
        // from the subject itself.
        Expr narrowed = Narrowed(built) ?? stands;

        // `and` BINDS TIGHTER THAN `or`, as it does everywhere else in C#. Read
        // left to right with one precedence, `a or b and c` would mean
        // `(a or b) and c`, which is the wrong grouping and silently so.
        while (AtPatternCombinator())
        {
            bool any = Cur.Text == "or";

            _i++;

            if (!any)
            {
                Expr next = ParsePrimaryPattern(narrowed, at);
                built = new BinaryExpr
                {
                    Op = BinOp.AndAlso, Left = built, Right = next, Line = at.Line, Col = at.Col,
                };
                narrowed = Narrowed(next) ?? narrowed;
                continue;
            }

            Expr right = ParsePrimaryPattern(stands, at);
            Expr rightNarrowed = Narrowed(right) ?? stands;

            while (AtPatternCombinator() && Cur.Text == "and")
            {
                _i++;

                Expr next = ParsePrimaryPattern(rightNarrowed, at);
                right = new BinaryExpr
                {
                    Op = BinOp.AndAlso, Left = right, Right = next, Line = at.Line, Col = at.Col,
                };
                rightNarrowed = Narrowed(next) ?? rightNarrowed;
            }

            built = new BinaryExpr
            {
                Op = BinOp.OrElse, Left = built, Right = right, Line = at.Line, Col = at.Col,
            };
            narrowed = stands;
        }

        return ReferenceEquals(stands, subject)
             ? built
             : new PatternExpr { Subject = subject, Test = built, Line = at.Line, Col = at.Col };
    }

    /// <summary>
    /// The name a pattern binds, if the next token is one. Not consumed: `or`,
    /// `and` and `when` are words that follow a pattern rather than name it.
    /// </summary>
    /// <summary>Names the hidden binding a property pattern needs, one per use.</summary>
    private int _hidden;

    /// <summary>
    /// What the method being read returns, while its body is being read.
    ///
    /// `return default;` is the one place a bare `default` has a type the
    /// PARSER knows -- everywhere else only the checker does -- so the type is
    /// carried down to the statement that needs it rather than invented there.
    /// </summary>
    private TypeRef? _returns;

    /// <summary>
    /// The name designated after a property pattern's braces, without moving.
    ///
    /// `x is Leaf { Kind: 2 } named` binds AFTER the members, which is where C#
    /// puts it and the opposite of where a bare type pattern binds. The member
    /// test has to read the same name the type test bound, so the name must be
    /// known before the members are parsed -- hence a look over the braces
    /// rather than a second pass.
    /// </summary>
    private string? BindingAfterBraces()
    {
        int j = _i;
        int depth = 0;

        while (j < _t.Count)
        {
            if (_t[j].Kind == Tok.LBrace) { depth++; }
            else if (_t[j].Kind == Tok.RBrace)
            {
                depth--;

                if (depth == 0) { j++; break; }
            }
            j++;
        }

        return j < _t.Count && _t[j].Kind == Tok.Ident
            && (_t[j].Text is not ("or" or "and" or "when")
                || _t[j].Text is "or" or "and"
                && j + 1 < _t.Count && !StartsPattern(_t[j + 1].Kind)
                || WhenNames(j))
             ? _t[j].Text
             : null;
    }

    /// <summary>
    /// `when` NAMING WHAT A PATTERN MATCHED, not a guard: a guard is
    /// followed by its condition, a designation by what ends the pattern --
    /// `if (c is not Condition when) continue;` names the condition `when`.
    /// </summary>
    private bool WhenNames(int j)
        => j + 1 < _t.Count && _t[j].Kind == Tok.Ident && _t[j].Text == "when"
        && _t[j + 1].Kind is Tok.RParen or Tok.Semi or Tok.Comma or Tok.AndAnd or Tok.OrOr
            or Tok.Question or Tok.RBracket or Tok.RBrace or Tok.Colon;

    private string? PeekBinding()
        => At(Tok.Ident)
        && (Cur.Text is not ("or" or "and" or "when")
            || Cur.Text is "or" or "and" && !StartsPattern(Ahead().Kind)
            || WhenNames(_i))
         ? Cur.Text
         : null;

    /// <summary>
    /// Whether a token could BEGIN a pattern -- a constant, a relational
    /// operator, a bracket, a brace, a name.
    ///
    /// This is the whole of C#'s rule for `and` and `or`, which are ordinary
    /// words and not keywords: they join two patterns when a pattern follows
    /// them and NAME the thing matched when one does not. `case BinaryExpr
    /// { Op: BinOp.AndAlso } and:` binds a variable called `and`, and is a line
    /// in this compiler's own lowering; `x is A and B` joins two type tests.
    /// Nothing but the token after the word tells them apart.
    /// </summary>
    private static bool StartsPattern(Tok k)
        => k is Tok.Ident or Tok.Int or Tok.Real or Tok.Char or Tok.Str or Tok.Minus
             or Tok.LParen or Tok.LBrace or Tok.LBracket or Tok.KwVar or Tok.KwNull or Tok.KwTrue or Tok.KwFalse
             or Tok.Lt or Tok.Gt or Tok.LtEq or Tok.GtEq;

    /// <summary>Whether the parser is on an `and` or `or` that joins two patterns.</summary>
    private bool AtPatternCombinator()
        => At(Tok.Ident) && Cur.Text is "or" or "and" && StartsPattern(Ahead().Kind);

    /// <summary>
    /// Whether the '(' the parser is on opens a CAST rather than a group.
    ///
    /// A pattern may test against any constant and `(int)Gpr.Ecx` is one, while
    /// `('r' or 'R')` is two patterns in brackets. Both start with the same
    /// token, so this asks the question ParseUnary asks -- a type, a ')' and
    /// something a cast can be applied to -- without consuming anything.
    /// </summary>
    private bool LooksLikeCast()
    {
        int was = _i;

        try
        {
            _i++;

            bool tuple = At(Tok.LParen);
            if (!tuple && !At(Tok.Ident) && !At(Tok.KwVoid) && !(At(Tok.KwDelegate) && Ahead().Kind == Tok.Star))
            {
                return false;
            }

            TypeRef type = ParseTypeRef();

            return At(Tok.RParen) && (!tuple || type.Args.Count >= 2)
                && (CastCanFollow(Ahead().Kind) || !tuple && CannotBeExpression(type));
        }
        catch (CompileError)
        {
            return false;                       // not a type, so it is a group
        }
        finally
        {
            _i = was;
        }
    }

    /// <summary>Whether the next token begins a constant or relational pattern,
    /// which is the only kind that tests the subject once per alternative.</summary>

    /// <summary>
    /// Whether a statement begins a LOCAL FUNCTION: a return type, a name and
    /// an opening bracket, with a body rather than a semicolon after it.
    ///
    /// `Foo(x)` is a call and `Foo Bar(x) { }` is a local function, so the
    /// decision needs the name AND the bracket -- one token is not enough, the
    /// same reason the declaration below is tried and fallen back from.
    /// </summary>
    /// <summary>
    /// How many words here -- `static`, `async`, in either order -- are a
    /// local function's modifiers, or 0 when what follows them is no local
    /// function (an `async` lambda, a name that is only called async).
    /// </summary>
    private int LocalFunctionModifiers()
    {
        int j = _i;
        while (j < _t.Count && (_t[j].Kind == Tok.KwStatic || _t[j].Kind == Tok.Ident && _t[j].Text == "async"))
        {
            j++;
        }
        if (j == _i) return 0;
        int was = _i;
        _i = j;
        bool yes = StartsLocalFunction();
        _i = was;
        return yes ? j - was : 0;
    }

    private bool StartsLocalFunction()
    {
        // `ref int At(int[] xs, int i) => ref xs[i];` RETURNS A VARIABLE. The
        // word in front is the return's, as a method's is; `ref int x = ref
        // y;`, the ref local, has `=` where this has its bracket, which is
        // what tells the two apart below.
        int j = _i;
        if (_t[j].Kind == Tok.KwRef)
        {
            j++;
            if (j < _t.Count && _t[j].Kind == Tok.KwReadonly) j++;
            if (j >= _t.Count || _t[j].Kind is not (Tok.Ident or Tok.LParen)) return false;
        }
        else if (!At(Tok.KwVoid) && !At(Tok.Ident) && !At(Tok.LParen))
        {
            return false;
        }

        // Step over the return type: a name, possibly dotted and generic, a
        // tuple in brackets, or the keyword void.
        if (_t[j].Kind == Tok.KwVoid)
        {
            j++;
        }
        else if (_t[j].Kind == Tok.LParen)
        {
            // A TUPLE RETURN TYPE, which is the one type written in brackets:
            // `(Block End, Block LastHop) Final(Block b)` is a local function
            // in this compiler's own branch simplifier. The brackets are
            // stepped over as a unit -- what is inside them is a type list and
            // settles nothing that the name and bracket after them do not.
            int nesting = 0;

            while (j < _t.Count)
            {
                if (_t[j].Kind == Tok.LParen) { nesting++; }
                else if (_t[j].Kind == Tok.RParen)
                {
                    nesting--;
                    if (nesting == 0) { j++; break; }
                }
                j++;
            }

            if (nesting != 0)
            {
                return false;
            }

            if (j < _t.Count && _t[j].Kind == Tok.Question)
            {
                j++;
            }
        }
        else
        {
            while (j + 2 < _t.Count && _t[j].Kind == Tok.Ident && _t[j + 1].Kind == Tok.Dot)
            {
                j += 2;
            }

            if (j >= _t.Count || _t[j].Kind != Tok.Ident)
            {
                return false;
            }

            j++;

            // Generic arguments and array brackets belong to the type.
            int depth = 0;

            // `>>` and `>>>` close two and three at once -- the lexer reads
            // `List<List<int>> Groups(...)` as a shift -- and only inside
            // the arguments, where no shift can be.
            while (j < _t.Count && (depth > 0 || _t[j].Kind is Tok.Lt or Tok.LBracket))
            {
                if (_t[j].Kind is Tok.Lt or Tok.LBracket) { depth++; }
                else if (_t[j].Kind is Tok.Gt or Tok.RBracket) { depth--; }
                else if (_t[j].Kind == Tok.Shr) { depth -= 2; }
                else if (_t[j].Kind == Tok.UShr) { depth -= 3; }
                if (depth < 0) { return false; }
                j++;
            }

            // Nullable is part of the return type too: `Node? Find(...)` is a
            // local function, not a declaration followed by a stray call.
            if (j < _t.Count && _t[j].Kind == Tok.Question)
            {
                j++;
            }
        }

        // Then the name, its type parameters if it is generic, and the
        // bracket that opens its parameters.
        if (j >= _t.Count || _t[j].Kind != Tok.Ident) return false;
        j++;
        if (j < _t.Count && _t[j].Kind == Tok.Lt)
        {
            int angles = 0;
            while (j < _t.Count)
            {
                if (_t[j].Kind == Tok.Lt) angles++;
                else if (_t[j].Kind == Tok.Gt) { angles--; if (angles == 0) { j++; break; } }
                else if (_t[j].Kind is not (Tok.Ident or Tok.Comma)) return false;
                j++;
            }
        }
        return j < _t.Count && _t[j].Kind == Tok.LParen;
    }

    /// <summary>
    /// A local function, read as the local-holding-a-lambda that it is.
    ///
    /// The delegate type is worked out from the shape: no result is an Action
    /// and a result is a Func, with the parameter types before the result --
    /// which is exactly how C# spells them, so the type a user could write by
    /// hand is the type this builds.
    /// </summary>
    private Stmt ParseGenericLocalFunction(Token at, TypeRef? returns, string name, Mods byReference)
    {
        if (_blocks.Count == 0) throw Error("a generic local function must be declared in a block");
        MethodDecl m = new()
        {
            Name = name + "$" + _memberName + "$" + _hoistSerial++,
            Returns = returns ?? VoidType(),
            Mods = (_memberStatic ? Mods.Static | Mods.Private : Mods.Private) | byReference,
            Line = at.Line, Col = at.Col, Body = null,
            HoistedName = name,
        };
        string? parent = _hoistParents.Count > 0 ? _hoistParents.Peek() : null;
        if (At(Tok.Lt)) ParseTypeParams(m.WritableTypeParams);
        { List<Param>? read = null; ParseParams(ref read); m.AdoptParams(read); }
        ParseConstraints(m.TypeParams);
        MethodDecl finished = FinishMethod(m);
        finished.HoistedName = name;
        finished.HoistedIn = parent;
        _blocks.Peek().WritableGenericLocals.Add((name, finished.Name));
        _hoisted.Add(finished);
        return new Block { Line = at.Line, Col = at.Col };
    }

    /// <summary>
    /// The delegate a local function's signature needs when Func and Action
    /// cannot say it: a parameter passed by ref, out or in, more than eight,
    /// or a result returned by reference (<paramref name="byReference"/>,
    /// Mods.RefReturn on its Invoke, as `delegate ref int D(...)` has it).
    /// Declared nested in the type being read, under a name no source can
    /// write, with that type's own type parameters as a nested type has them.
    /// </summary>
    private TypeRef LocalFunctionDelegate(Token at, string name, TypeRef? returns, List<Param> parameters, Mods byReference)
    {
        string delegateName = "LocalFunction$" + name + "$" + _hoistSerial++;
        TypeDecl declaration = new()
        {
            Kind = TypeKind.Interface, IsDelegate = true, Name = delegateName, Mods = Mods.Private,
            // This unit's alone: no index lists it, no other unit has it
            // (Binder's slot numbering keeps it out of the shared tiers).
            LocalOnly = true,
            Namespace = _namespace, Scope = _fileScope, Outer = _typePath.Length == 0 ? null : _typePath,
            File = _file, Line = at.Line, Col = at.Col, SourceFrom = at.Pos, SourceTo = at.Pos,
        };
        for (int k = _enclosingParams.Count - 1; k >= 0; k--)
        {
            declaration.WritableTypeParams.Insert(0, new TypeParam { Name = _enclosingParams[k], Line = at.Line, Col = at.Col });
        }
        declaration.OuterParams = _enclosingParams.Count;
        MethodDecl invoke = new()
        {
            Name = "Invoke", Returns = returns ?? new TypeRef { Name = "void", Line = at.Line, Col = at.Col },
            Mods = Mods.Public | Mods.Abstract | byReference,
            File = _file, Scope = _fileScope, Namespace = _namespace, Line = at.Line, Col = at.Col,
        };
        foreach (Param p in parameters)
        {
            invoke.WritableParams.Add(new Param
            {
                Type = p.Type, Name = p.Name, IsRef = p.IsRef, IsOut = p.IsOut, IsReadOnlyRef = p.IsReadOnlyRef,
                Line = p.Line, Col = p.Col,
            });
        }
        declaration.Members.Add(invoke);
        _nested.Add(declaration);
        return new TypeRef { Name = delegateName, Line = at.Line, Col = at.Col };
    }

    private Stmt ParseLocalFunction(Token at, bool isAsync = false)
    {
        // `ref int At(...)` and `ref readonly int At(...)` RETURN A VARIABLE,
        // as a method written so does (Mods.RefReturn): the generic one is
        // such a method, and the delegate the others are has an Invoke that
        // says so.
        Mods byReference = Mods.None;
        if (Take(Tok.KwRef))
        {
            byReference = Mods.RefReturn;
            if (Take(Tok.KwReadonly)) byReference |= Mods.RefReadonlyReturn;
        }

        TypeRef? returns = byReference == Mods.None && Take(Tok.KwVoid)
                         ? null
                         : ParseTypeRef();

        string name = Expect(Tok.Ident, "the local function's name").Text;

        // A GENERIC LOCAL FUNCTION is a generic method of the type, hidden
        // under a name of its own and known by its written name only in this
        // block (Block.GenericLocals): a delegate, which the other local
        // functions are, cannot be generic.
        if (At(Tok.Lt))
        {
            return ParseGenericLocalFunction(at, returns, name, byReference | (isAsync ? Mods.Async : Mods.None));
        }

        // THE SAME PARAMETER LIST A METHOD HAS: attributes (caller
        // information among them), `params`, ref, out and in, and defaults.
        LambdaExpr made = new() { BlockBody = null!, Line = at.Line, Col = at.Col };
        { List<Param>? read = null; ParseParams(ref read); made.AdoptParams(read); }
        List<TypeRef> takes = made.Params.Select(p => p.Type).ToList();
        bool passedByReference = made.Params.Any(p => p.IsRef || p.IsOut);

        // EXPRESSION-BODIED, which is how the short ones are written:
        // `long Twice(long n) => n * 2;` -- and that form DOES end with a
        // semicolon, unlike the block form.
        LambdaExpr lam;

        if (Take(Tok.FatArrow))
        {
            // `=> ref xs[i]`: the body of one that returns by reference is
            // the variable it answers (ParseRefValue).
            lam = new LambdaExpr { Body = At(Tok.KwRef) ? ParseRefValue() : ParseExpr(), Async = isAsync, Line = at.Line, Col = at.Col };
            Expect(Tok.Semi, "';' after the expression body");
        }
        else
        {
            // ITS OWN BODY, as a method's is: a local function may be an
            // iterator of its own, and its `yield`s are not its enclosing
            // method's.
            lam = new LambdaExpr { BlockBody = ReadBodyBlock(returns, at.Line, at.Col), Async = isAsync, Line = at.Line, Col = at.Col };
        }

        lam.WritableParams.AddRange(made.Params);

        // Action for no result, Func with the result last -- C#'s own spelling
        // -- when one fits: nothing passed by reference, the result not
        // returned by reference, and no more than the eight parameters those
        // carry. Otherwise the local function's type is a delegate of its
        // exact signature, as C# gives it one, declared beside the type it is
        // written in.
        TypeRef shape;
        if (passedByReference || byReference != Mods.None || takes.Count > 8)
        {
            shape = LocalFunctionDelegate(at, name, returns, made.WritableParams, byReference);
        }
        else
        {
            shape = new TypeRef
            {
                Name = returns is null ? "Action" : "Func",
                Line = at.Line, Col = at.Col,
            };

            shape.Arguments.AddRange(takes);

            if (returns != null)
            {
                shape.Arguments.Add(returns);
            }
        }

        return new LocalDecl
        {
            Type = shape, Name = name, Init = lam, LocalFunction = true,
            Line = at.Line, Col = at.Col,
        };
    }


    /// <summary>
    /// The member a property pattern tests, which may be a PATH.
    ///
    /// `{ Args.Count: 1 }` reaches through one member to another, and C# allows
    /// it precisely so a pattern can ask about a shape rather than only a
    /// field. Reading a single name left the dot to be met where a colon was
    /// expected -- and the compiler's own Parser.cs writes exactly this.
    ///
    /// Each step is Guarded for the same reason the first is: the null test in
    /// front of the whole pattern is what proves the subject, and a step that
    /// answers null makes the next read a null dereference the checker would
    /// otherwise demand a test for. C# has the same hole and closes it the same
    /// way -- a property pattern never reads through a null because the pattern
    /// fails first.
    /// </summary>
    private Expr PatternMember(Expr subject, Token at, out Expr? reachable)
    {
        string first = Expect(Tok.Ident, "a member name in the pattern").Text;
        Expr path = new MemberExpr
        {
            Target = subject, Name = first, Guarded = true,
            Line = at.Line, Col = at.Col,
        };

        reachable = null;

        while (At(Tok.Dot) && Ahead().Kind == Tok.Ident)
        {
            _i++;

            // EVERY STEP BUT THE LAST MUST BE TESTED FOR NULL, and the pattern
            // FAILS rather than faulting when one is. `x is Leaf { Link.Kind: 3 }`
            // where Link is null is a pattern that does not match, not a null
            // dereference -- C# is explicit about this and it is most of why the
            // form is safe to write.
            //
            // The first draft of this marked each step Guarded and stopped
            // there, which silences the CHECKER without generating the test:
            // the null then reached the machine. Its own comment claimed the
            // steps were guarded, which is the exact mistake this tree keeps
            // making -- a safeguard described and not written. tests/
            // proppattern.cor caught it on the first run.
            Expr step = new BinaryExpr
            {
                Op = BinOp.Ne, Left = path, PatternNullTest = true,
                Right = new LiteralExpr { Kind = Lit.Null, Text = "null", Line = at.Line, Col = at.Col },
                Line = at.Line, Col = at.Col,
            };

            reachable = reachable is null
                      ? step
                      : new BinaryExpr
                        {
                            Op = BinOp.AndAlso, Left = reachable, Right = step,
                            Line = at.Line, Col = at.Col,
                        };

            path = new MemberExpr
            {
                Target = path, Name = _t[_i++].Text, Guarded = true,
                Line = at.Line, Col = at.Col,
            };
        }

        return path;
    }

    private bool StartsValuePattern()
        // A PROPERTY PATTERN READS THE SUBJECT TWICE TOO -- once for the null
        // test and again for the member -- so it needs hoisting for the same
        // reason a constant alternative does.
        => At(Tok.LBrace)
           || At(Tok.LParen) || Cur.Kind is Tok.Lt or Tok.Gt or Tok.LtEq or Tok.GtEq
           or Tok.Int or Tok.Minus or Tok.Char or Tok.Real
           or Tok.KwTrue or Tok.KwFalse or Tok.Str
           || (At(Tok.Ident) && Ahead().Kind == Tok.Dot && !QualifiedTypePattern())
           // `case unchecked((int)0x80000000):` is a constant, not a type
           // named `unchecked` -- nor is `checked(...)` or `nameof(...)`.
           || (At(Tok.Ident) && Cur.Text is "unchecked" or "checked" or "nameof" && Ahead().Kind == Tok.LParen);

    /// <summary>
    /// Whether a dotted name after `is` is a TYPE with a binding rather than a
    /// constant.
    ///
    /// `t is Prim.I8` is a constant and `x is Hardware.CbuComponent cbu` is a
    /// type pattern, and the dot alone cannot tell them apart -- both are a name
    /// with dots in it. What settles it is what comes AFTER: a constant pattern
    /// is complete at the end of the name, and a type pattern that binds has one
    /// more identifier. Reading the dotted name as a constant left the binding
    /// sitting there and the label reported a missing bracket.
    /// </summary>
    private bool QualifiedTypePattern()
    {
        int j = _i;

        while (j + 2 < _t.Count && _t[j].Kind == Tok.Ident && _t[j + 1].Kind == Tok.Dot)
        {
            j += 2;
        }

        // Landed on the last segment of the name. What follows settles it: a
        // binding is one more word, and a '{' opens a property pattern -- both
        // mean the dotted name was a TYPE. `Devices.MemoryCard { Kind: ... }`
        // is a type test, `Prim.I8` is a constant, and only the token after the
        // name tells them apart.
        if (j >= _t.Count || _t[j].Kind != Tok.Ident)
        {
            return false;
        }

        if (j + 1 < _t.Count && _t[j + 1].Kind is Tok.LBrace or Tok.LParen)
        {
            return true;
        }

        // A '<' after the name opens its type arguments: a constant is never
        // followed by one (a relational pattern begins with it instead), so
        // `o is System.Collections.Generic.List<int>` is a generic type, with
        // or without a binding after the '>'. Read as a constant, its first
        // segment was looked up as a variable: "'System' is not declared".
        if (j + 1 < _t.Count && _t[j + 1].Kind == Tok.Lt)
        {
            return true;
        }

        return j + 1 < _t.Count && _t[j + 1].Kind == Tok.Ident
            && _t[j + 1].Text is not ("or" or "and" or "when");
    }

    /// <summary>
    /// One pattern with no `or` or `and` in it: a constant, a null, a property
    /// pattern or a type, whichever the next token begins.
    /// </summary>
    private Expr ParsePrimaryPattern(Expr subject, Token at)
    {
        // `not` IS A PATTERN OPERATOR, not merely a prefix accepted once after
        // `is`. It may begin either side of `and`/`or`: `x is not A and not B`
        // is the ordinary form used by the assembler. Keeping it here gives it
        // the required tighter precedence and permits nested/grouped forms.
        if (At(Tok.Ident) && Cur.Text == "not")
        {
            _i++;
            return new UnaryExpr
            {
                Op = UnOp.Not,
                Operand = ParsePrimaryPattern(subject, at),
                Line = at.Line,
                Col = at.Col,
            };
        }

        // `_` MATCHES ANYTHING AND BINDS NOTHING, wherever a pattern belongs
        // and not only as a whole switch arm. `t is (1, _)` asks one question
        // about the first item and none at all about the second, so the second
        // becomes the answer it always gives. Read as a type it was reported as
        // an unknown type called `_`, which is true of the name and not of the
        // pattern.
        //
        // Only when nothing follows that would make it a name: `_` before a
        // dot is a qualified constant, and `_ x` is a type called `_` binding
        // a variable.
        if (At(Tok.Ident) && Cur.Text == "_"
            && Ahead().Kind is not (Tok.Dot or Tok.Ident or Tok.LBrace))
        {
            Token any = _t[_i++];

            return new LiteralExpr
            {
                Kind = Lit.Bool, Text = "true", IntValue = 1, Line = any.Line, Col = any.Col,
            };
        }

        // A VAR PATTERN MATCHES ANYTHING AND NAMES IT.
        // `resolved is not ParamSym { Index: var index }` asks nothing of Index
        // and calls what it found index, with the type it already had. It is
        // not `is { } index`: that one tests for null, and a var pattern in C#
        // matches null as readily as anything else.
        //
        // `var _` is the discard designation -- it matches and names nothing,
        // so it is the answer it always gives.
        if (At(Tok.KwVar) && Ahead().Kind == Tok.Ident
            && Ahead().Text is not ("or" or "and" or "when"))
        {
            Token word = _t[_i++];
            Token named = _t[_i++];

            if (named.Text == "_")
            {
                return new LiteralExpr
                {
                    Kind = Lit.Bool, Text = "true", IntValue = 1, Line = word.Line, Col = word.Col,
                };
            }

            return new IsExpr
            {
                Operand = subject,
                Type = new TypeRef { Name = TypeRef.Anything, Line = word.Line, Col = word.Col },
                Binding = named.Text,
                Line = at.Line,
                Col = at.Col,
            };
        }

        // A LIST PATTERN: `[1, .., var last]`. See ParseListPattern.
        if (At(Tok.LBracket))
        {
            return ParseListPattern(subject, at);
        }

        // `var (a, b)`, which takes the subject apart into names.
        if (At(Tok.KwVar) && Ahead().Kind == Tok.LParen)
        {
            _i++;
            return ParsePositional(subject, null, at, designations: true);
        }

        // A POSITIONAL PATTERN WITH NO TYPE: `t is (1, _)`. See ParsePositional.
        if (At(Tok.LParen) && PositionalPatternAhead())
        {
            return ParsePositional(subject, null, at);
        }

        // ONE PREDICATE, asked here and by the hoisting decision above. Two
        // copies of it is how `is Hardware.CbuComponent cbu` kept being read
        // as a constant: the copy that learned better was not the copy that
        // ran. That is the third time in this parser -- the `is` dispatch was
        // duplicated too.
        if (StartsValuePattern() && !At(Tok.LBrace) && !TupleArrayTypeAhead())
        {
            return ParseOnePattern(subject, at);
        }

        if (At(Tok.LBrace))
        {
            // `x is { A: 1 } name` binds the subject as well as testing it.
            // With no type written there is nothing to narrow to, so the name
            // takes the subject's own type -- the same sentinel `is { } name`
            // uses.
            // AN EMPTY `{ }` WITH A NAME IS ALREADY HANDLED inside
            // ParseMemberPattern, which consumes the designation itself. Taking
            // it here as well ate the token after it -- my own property-pattern
            // test caught that, reporting the body's opening brace as
            // unexpected several tokens later.
            bool empty = Ahead().Kind == Tok.RBrace;

            if (!empty && BindingAfterBraces() is string also)
            {
                IsExpr held = new()
                {
                    Operand = subject,
                    Type = new TypeRef { Name = TypeRef.Same, Line = at.Line, Col = at.Col },
                    Binding = also, Line = at.Line, Col = at.Col,
                };

                Expr tested = ParseMemberPattern(
                    new NameExpr { Name = also, Line = at.Line, Col = at.Col }, at, designates: false);

                _i++;                           // the designation

                return new BinaryExpr
                {
                    Op = BinOp.AndAlso, Left = held, Right = tested,
                    Line = at.Line, Col = at.Col,
                };
            }

            return ParseMemberPattern(subject, at);
        }

        if (At(Tok.KwNull))
        {
            Token nul = _t[_i++];

            return new BinaryExpr
            {
                Op = BinOp.Eq, Left = subject,
                Right = new LiteralExpr { Kind = Lit.Null, Text = "null", Line = nul.Line, Col = nul.Col },
                Line = at.Line, Col = at.Col,
            };
        }

        TypeRef type = ParseTypeRef(pattern: true);

        // A TYPE FOLLOWED BY A POSITIONAL PATTERN: `s is Rect(var w, var h)`.
        if (At(Tok.LParen))
        {
            return ParsePositional(subject, type, at);
        }

        // A TYPE FOLLOWED BY A PROPERTY PATTERN: `x is Foo { Bar: 1 }`, which
        // asks two things -- that x is a Foo, and that the Foo's Bar is 1.
        //
        // It becomes exactly what it means: a type test that BINDS, and a
        // member test on the name it bound. The binding is the one written if
        // there is one and a hidden name otherwise, because the member test has
        // to read the value the type test narrowed rather than the subject
        // again -- reading the subject twice would evaluate it twice, and the
        // member being read only exists on the narrowed type.
        if (At(Tok.LBrace))
        {
            // THE DESIGNATION COMES AFTER THE BRACES in C# --
            // `x is Leaf { Kind: 2 } named` -- so it has to be found before the
            // members are parsed, because the member test reads whatever the
            // type test bound and both must name the same thing.
            string held = BindingAfterBraces() ?? $"__pat{_hidden++}";

            IsExpr test = new()
            {
                Operand = subject, Type = type, Binding = held,
                Line = at.Line, Col = at.Col,
            };

            // The designation is the type test's (above), so the member
            // pattern must not bind it a second time -- which an empty
            // `{ }` in front of it did: `x is Foo { } y` declared y twice.
            Expr members = ParseMemberPattern(
                new NameExpr { Name = held, Line = at.Line, Col = at.Col }, at, designates: false);

            // Consume the designation the lookahead found.
            if (PeekBinding() != null)
            {
                _i++;
            }

            return new BinaryExpr
            {
                Op = BinOp.AndAlso, Left = test, Right = members,
                Line = at.Line, Col = at.Col,
            };
        }

        // A BINDING IS A NAME, and `or` and `and` are not bindings -- they are
        // what comes NEXT. Taking one as the binding turns `is Foo or Bar` into
        // a type test binding a variable called `or`, and the rest of the
        // pattern then fails to parse somewhere confusing.
        string? bound = PeekBinding();

        if (bound != null)
        {
            _i++;
        }

        // A TYPE TEST THAT `and` FOLLOWS names what it narrowed to, so the
        // pattern after it can test that (ParseIsPattern).
        if (bound is null && AtPatternCombinator() && Cur.Text == "and")
        {
            bound = $"__pat{_hidden++}";
        }

        return new IsExpr { Operand = subject, Type = type, Binding = bound, Line = at.Line, Col = at.Col };
    }

    /// <summary>
    /// The name a pattern bound the narrowed subject to, when it narrowed it:
    /// a type test with a binding, or a typed positional or property pattern
    /// (an `and` whose left is that test). Null when it narrowed nothing.
    /// </summary>
    private static NameExpr? Narrowed(Expr pattern) => pattern switch
    {
        IsExpr { Binding: string name } test when test.Type.Name != TypeRef.Anything
            => new NameExpr { Name = name, Line = test.Line, Col = test.Col },
        BinaryExpr { Op: BinOp.AndAlso } both => Narrowed(both.Left),
        _ => null,
    };

    /// <summary>
    /// A POSITIONAL PATTERN (C# 11.2.6): `Rect(var w, var h)`, `(1, _)`,
    /// `Point(X: 0, Y: var y) { Length: > 1 } p`.
    ///
    /// The subject is tested for the type when one is written (or for null
    /// when not), then TAKEN APART ONCE -- by its items when it is a tuple and
    /// through its Deconstruct otherwise, exactly as a deconstructing
    /// declaration is -- into hidden locals, and each element's pattern tests
    /// its own. It used to read Item1, Item2 ... straight off the subject,
    /// which is right for a tuple and names nothing on a record.
    /// </summary>
    private Expr ParsePositional(Expr subject, TypeRef? type, Token at, bool designations = false)
    {
        LiteralExpr True() => new() { Kind = Lit.Bool, Text = "true", IntValue = 1, Line = at.Line, Col = at.Col };
        NameExpr Named(string name) => new() { Name = name, Line = at.Line, Col = at.Col };
        Expr And(Expr left, Expr right) => new BinaryExpr
        {
            Op = BinOp.AndAlso, Left = left, Right = right, Line = at.Line, Col = at.Col,
        };

        // THE DESIGNATION COMES LAST -- after the brackets and after any
        // property pattern -- and names the matched value, so it is found
        // first: the type test binds it, and everything after reads it.
        int after = SkipBalanced(_i, Tok.LParen, Tok.RParen);
        bool members = after < _t.Count && _t[after].Kind == Tok.LBrace;
        int end = members ? SkipBalanced(after, Tok.LBrace, Tok.RBrace) : after;
        string? designation = end < _t.Count && _t[end].Kind == Tok.Ident
            && (_t[end].Text is not ("or" or "and" or "when")
                || _t[end].Text is "or" or "and" && end + 1 < _t.Count && !StartsPattern(_t[end + 1].Kind)
                || WhenNames(end))
            ? _t[end].Text : null;

        Expr built;
        Expr source;
        string? held = null;
        if (type is not null || members || designation is not null)
        {
            held = designation ?? $"__pat{_hidden++}";
            built = new IsExpr
            {
                Operand = subject,
                Type = type ?? new TypeRef { Name = TypeRef.Same, Line = at.Line, Col = at.Col },
                Binding = held, Line = at.Line, Col = at.Col,
            };
            source = Named(held);
        }
        else
        {
            built = new BinaryExpr
            {
                Op = BinOp.Ne, Left = subject, PatternNullTest = true,
                Right = new LiteralExpr { Kind = Lit.Null, Text = "null", Line = at.Line, Col = at.Col },
                Line = at.Line, Col = at.Col,
            };
            source = subject;
        }

        Expect(Tok.LParen, "'(' to open the positional pattern");
        List<Binding> names = new();
        List<Expr> tests = new();
        if (!At(Tok.RParen))
        {
            do
            {
                // `Y: var y` NAMES THE ELEMENT, which C# checks against the
                // tuple's or the Deconstruct's own name; the position says
                // which element it is either way.
                if (At(Tok.Ident) && Ahead().Kind == Tok.Colon)
                {
                    _i += 2;
                }

                Token where = Cur;
                string part = $"$part${_hidden++}";
                names.Add(new Binding { Name = part, Line = where.Line, Col = where.Col });
                if (designations)
                {
                    // `var (a, (b, _))`: names, discards and nested lists of
                    // them, every one a var pattern.
                    if (At(Tok.LParen))
                    {
                        tests.Add(ParsePositional(Named(part), null, at, designations: true));
                    }
                    else if (At(Tok.Ident) && Cur.Text == "_")
                    {
                        _i++;
                        tests.Add(True());
                    }
                    else
                    {
                        Token name = Expect(Tok.Ident, "a name in the var pattern");
                        tests.Add(new IsExpr
                        {
                            Operand = Named(part),
                            Type = new TypeRef { Name = TypeRef.Anything, Line = name.Line, Col = name.Col },
                            Binding = name.Text, Line = at.Line, Col = at.Col,
                        });
                    }
                    continue;
                }
                // EACH ELEMENT IS A WHOLE PATTERN, `or` and `and` and all:
                // `("fldcw", 0 or 2)` is as good C# as `x is 0 or 2`.
                tests.Add(ParseIsPattern(Named(part), at));
            }
            while (Take(Tok.Comma));
        }
        Expect(Tok.RParen, "')' to close the positional pattern");

        if (names.Count > 0)
        {
            DeconstructStmt apart = new() { Value = source, Line = at.Line, Col = at.Col };
            apart.Names.AddRange(names);
            built = And(built, new SequenceExpr { Effect = apart, Value = True(), Line = at.Line, Col = at.Col });
            foreach (Expr test in tests)
            {
                built = And(built, test);
            }
        }

        if (members)
        {
            built = And(built, ParseMemberPattern(Named(held!), at, designates: false));
        }

        if (designation is not null)
        {
            _i++;
        }
        return built;
    }

    /// <summary>
    /// Whether a type name followed by `(` is next -- `Rect(var w, var h)`,
    /// `Shapes.Pair<int, string>(1, _)` -- which in a pattern can only be a
    /// positional pattern: no call is a constant. Except the three operators
    /// that look like calls and are: `nameof(...)`, `checked(...)`,
    /// `unchecked(...)`.
    /// </summary>
    private bool TypeThenParen()
    {
        int j = _i;
        if (j >= _t.Count || _t[j].Kind != Tok.Ident || _t[j].Text is "nameof" or "checked" or "unchecked")
        {
            return false;
        }
        j++;
        while (j + 1 < _t.Count && _t[j].Kind == Tok.Dot && _t[j + 1].Kind == Tok.Ident)
        {
            j += 2;
        }
        if (j < _t.Count && _t[j].Kind == Tok.Lt)
        {
            int depth = 0;
            for (; j < _t.Count; j++)
            {
                Tok k = _t[j].Kind;
                if (k == Tok.Lt) depth++;
                else if (k == Tok.Gt) depth--;
                else if (k == Tok.Shr) depth -= 2;
                else if (k == Tok.UShr) depth -= 3;
                else if (k is not (Tok.Ident or Tok.Comma or Tok.Dot or Tok.Question or Tok.LBracket or Tok.RBracket
                         or Tok.LParen or Tok.RParen)
                         && !_t[j].Text.All(char.IsLetter))
                {
                    return false;
                }
                if (depth <= 0) { j++; break; }
            }
            if (depth != 0) return false;
        }
        return j < _t.Count && _t[j].Kind == Tok.LParen;
    }

    /// <summary>
    /// A LIST PATTERN (C# 11.2.11): `[]`, `[var x]`, `[1, .., var last]`,
    /// `[_, .. var middle, _] all`.
    ///
    /// The subject is tested for null, then its count -- `Length`, or `Count`
    /// when that is what it has -- against the number of elements (at least
    /// that many when a slice `..` is among them), then each element's pattern
    /// against the item in its place: counted from the front before the
    /// slice and from the back after it. A slice with a pattern of its own
    /// tests the range between, `subject[before..^after]`. The subject is
    /// read more than once, so ParseIsPattern hoists anything that is not
    /// cheap to read again.
    /// </summary>
    private Expr ParseListPattern(Expr subject, Token at)
    {
        LiteralExpr Number(long n) => new()
        {
            Kind = Lit.Int, Text = n.ToString(System.Globalization.CultureInfo.InvariantCulture),
            IntValue = n, Line = at.Line, Col = at.Col,
        };
        Expr And(Expr left, Expr right) => new BinaryExpr
        {
            Op = BinOp.AndAlso, Left = left, Right = right, Line = at.Line, Col = at.Col,
        };
        MemberExpr Count() => new()
        {
            Target = subject, Name = "Length", Else = "Count", Guarded = true, Line = at.Line, Col = at.Col,
        };

        // THE SHAPE FIRST: how many elements, and where the slice is.
        int elements = 0, slice = -1, depth = 0;
        bool empty = _i + 1 < _t.Count && _t[_i + 1].Kind == Tok.RBracket;
        for (int j = _i; j < _t.Count && !empty; j++)
        {
            Tok k = _t[j].Kind;
            if (k is Tok.LParen or Tok.LBracket or Tok.LBrace)
            {
                if (depth++ == 0 && k == Tok.LBracket) { elements = 1; }
                if (depth == 1 && _t[j + 1].Kind == Tok.DotDot) slice = 0;
            }
            else if (k is Tok.RParen or Tok.RBracket or Tok.RBrace)
            {
                if (--depth == 0) break;
            }
            else if (k == Tok.Comma && depth == 1)
            {
                if (_t[j + 1].Kind == Tok.DotDot) slice = elements;
                elements++;
            }
        }
        int fixedCount = slice >= 0 ? elements - 1 : elements;

        Expr built = new BinaryExpr
        {
            Op = BinOp.Ne, Left = subject, PatternNullTest = true,
            Right = new LiteralExpr { Kind = Lit.Null, Text = "null", Line = at.Line, Col = at.Col },
            Line = at.Line, Col = at.Col,
        };
        built = And(built, new BinaryExpr
        {
            Op = slice >= 0 ? BinOp.Ge : BinOp.Eq, Left = Count(), Right = Number(fixedCount),
            Line = at.Line, Col = at.Col,
        });

        Expect(Tok.LBracket, "'[' to open the list pattern");
        for (int position = 0; position < elements; position++)
        {
            if (position > 0)
            {
                Expect(Tok.Comma, "',' between the elements of the list pattern");
            }

            if (position == slice)
            {
                Expect(Tok.DotDot, "'..'");
                if (At(Tok.Comma) || At(Tok.RBracket))
                {
                    continue;
                }
                IndexExpr between = new()
                {
                    Target = subject, Line = at.Line, Col = at.Col,
                };
                between.Args.Add(new RangeExpr
                {
                    From = Number(slice),
                    To = new FromEndExpr { Offset = Number(elements - 1 - slice), Line = at.Line, Col = at.Col },
                    Line = at.Line, Col = at.Col,
                });
                built = And(built, ParseIsPattern(between, at));
                continue;
            }

            // FROM THE FRONT before the slice, FROM THE BACK after it --
            // `subject[Count - n]`, which is what `^n` means and needs no
            // Index support from the type.
            Expr place = slice >= 0 && position > slice
                ? new BinaryExpr
                  {
                      Op = BinOp.Sub, Left = Count(), Right = Number(elements - position),
                      Line = at.Line, Col = at.Col,
                  }
                : Number(position);
            IndexExpr item = new() { Target = subject, Line = at.Line, Col = at.Col };
            item.Args.Add(place);
            built = And(built, ParseIsPattern(item, at));
        }
        Expect(Tok.RBracket, "']' to close the list pattern");

        // `[1, ..] all` names the whole list.
        if (PeekBinding() is string name)
        {
            _i++;
            built = And(built, new IsExpr
            {
                Operand = subject, Type = new TypeRef { Name = TypeRef.Same, Line = at.Line, Col = at.Col },
                Binding = name, Line = at.Line, Col = at.Col,
            });
        }
        return built;
    }

    /// <summary>The index just past the bracket that closes the one at <paramref name="j"/>.</summary>
    private int SkipBalanced(int j, Tok open, Tok close)
    {
        int depth = 0;
        for (; j < _t.Count; j++)
        {
            if (_t[j].Kind == open) depth++;
            else if (_t[j].Kind == close && --depth == 0) return j + 1;
        }
        return j;
    }

    /// <summary>
    /// `(int, int[])[]` at the cursor: brackets in a tuple's shape and an
    /// array's after them, a type -- tested as one, not grouped as a pattern.
    /// </summary>
    private bool TupleArrayTypeAhead()
    {
        if (!At(Tok.LParen)) return false;
        int depth = 0;
        bool comma = false;
        for (int j = _i; j < _t.Count; j++)
        {
            if (_t[j].Kind == Tok.LParen) depth++;
            else if (_t[j].Kind == Tok.RParen && --depth == 0) return comma && j + 1 < _t.Count && _t[j + 1].Kind == Tok.LBracket;
            else if (_t[j].Kind == Tok.Comma && depth == 1) comma = true;
        }
        return false;
    }

    private bool PositionalPatternAhead()
    {
        int depth = 0;
        bool comma = false;

        for (int j = _i; j < _t.Count; j++)
        {
            if (_t[j].Kind == Tok.LParen)
            {
                depth++;
            }
            else if (_t[j].Kind == Tok.RParen)
            {
                depth--;
                // A TUPLE TYPE'S ARRAY, `o is (int, int[])[]`: brackets after
                // the group make it a type, which no positional pattern is
                // ever followed by.
                if (depth == 0) { return comma && !(j + 1 < _t.Count && _t[j + 1].Kind == Tok.LBracket); }
            }
            else if (_t[j].Kind == Tok.Comma && depth == 1)
            {
                comma = true;
            }
        }
        return false;
    }

    /// <summary>One alternative of a value pattern: `&lt; 0`, `&gt;= 10`, or `5`.</summary>
    private Expr ParseOnePattern(Expr subject, Token at)
    {
        // A PARENTHESISED PATTERN, which is how alternatives are grouped:
        // `c is not ('r' or 'R')`. The brackets are the grouping and nothing
        // else, so what is inside them is a whole pattern again -- of any
        // shape, not only constants: `y is not (MReg { IsPhys: false } or
        // MImm)` was a syntax error at the brace.
        if (At(Tok.LParen) && !LooksLikeCast())
        {
            _i++;

            Expr grouped = ParseIsPattern(subject, at);

            Expect(Tok.RParen, "')' to close the pattern");
            return grouped;
        }

        BinOp op = Cur.Kind switch
        {
            Tok.Lt   => BinOp.Lt,
            Tok.Gt   => BinOp.Gt,
            Tok.LtEq => BinOp.Le,
            Tok.GtEq => BinOp.Ge,
            _        => BinOp.Eq,           // a bare constant is an equality test
        };

        if (op != BinOp.Eq)
        {
            _i++;
        }

        // Unary, so that `or` and `and` bind looser than the operand does and
        // an alternative cannot swallow the one after it.
        return new BinaryExpr
        {
            Op = op, Left = subject, Right = ParseUnary(),
            PatternConstant = op == BinOp.Eq,
            Line = at.Line, Col = at.Col,
        };
    }

    private SwitchStmt ParseSwitch()
    {
        Token at = _t[_i++];
        Expect(Tok.LParen, "'(' after 'switch'");
        Expr subject = ParseExpr();
        Expect(Tok.RParen, "')' after the switch subject");
        Expect(Tok.LBrace, "'{' to open the switch body");

        SwitchStmt sw = new() { Subject = subject, Line = at.Line, Col = at.Col };

        while (!At(Tok.RBrace) && !At(Tok.End))
        {
            Token caseAt = Cur;
            Expr? label = null;

            if (Take(Tok.KwCase))
            {
                // THE SAME PATTERN PARSER `is` USES, over the switch subject.
                //
                // This was ninety lines of speculation -- parse a type, look at
                // what follows, put it all back if it was not a type after all
                // -- around a hand-written property pattern that accepted one
                // member and a list of constants. It was a second, poorer copy
                // of the pattern grammar, and it could not be extended: `case
                // MemberExpr { Target: NameExpr type } m:` is an ordinary line
                // in this compiler's own Binder.cs and was a syntax error here,
                // while the identical test spelled with `is` was accepted.
                //
                // NONE OF THE SPECULATION IS NEEDED, because the ambiguity it
                // was written for does not exist in the pattern grammar. `case
                // Red:` and `case Foo f:` differ in exactly the way `x is Red`
                // and `x is Foo f` differ, and that function already decides it
                // -- a bare name is a value pattern unless a binding follows.
                //
                // The subject is a SubjectExpr rather than the subject
                // expression itself, so `switch (Next())` calls Next once and
                // every label reads the answer. The old code re-emitted the
                // subject after each guard and each property load, which for a
                // subject that DID something was three calls for one switch.
                label = ParseIsPattern(new SubjectExpr { Line = caseAt.Line, Col = caseAt.Col }, caseAt);
            }
            else if (!Take(Tok.KwDefault))
            {
                throw Error($"expected 'case' or 'default', found '{Cur.Text}'");
            }

            // `when <expr>`, after the pattern and before the colon, exactly
            // where C# puts it. `when` is an ordinary word to the lexer.
            //
            // It is an `and` on the end of the pattern, which is precisely what
            // it means: tested only once the pattern matched, and falling
            // through to the NEXT label when it fails rather than out of the
            // switch. Written as `&&` it gets that ordering and that
            // short-circuit from the ordinary expression rules, instead of from
            // eighty lines of hand-placed branches.
            if (At(Tok.Ident) && Cur.Text == "when")
            {
                _i++;

                Expr guard = ParseExpr();

                label = label is null
                      ? guard
                      : new BinaryExpr
                        {
                            Op = BinOp.AndAlso, Left = label, Right = guard,
                            Line = caseAt.Line, Col = caseAt.Col,
                        };
            }

            Expect(Tok.Colon, "':' after the case label");
            SwitchCase c = new() { Pattern = label, Line = caseAt.Line, Col = caseAt.Col };

            while (!At(Tok.KwCase) && !At(Tok.KwDefault) && !At(Tok.RBrace) && !At(Tok.End))
            {
                c.Body.Add(ParseStmt());
            }
            sw.Cases.Add(c);
        }

        Expect(Tok.RBrace, "'}' to close the switch");
        return sw;
    }

    /// <summary>
    /// The catch clause a `throw;` refers to: the innermost one whose body is
    /// being read. Its name is settable because the statement may be the
    /// first thing to need one.
    /// </summary>
    private sealed class CatchScope
    {
        public string? Name { get; set; }
    }

    private readonly List<CatchScope> _catches = new();

    private TryStmt ParseTry()
    {
        Token at = _t[_i++];
        Block body = ParseBlock();
        TryStmt t = new() { Body = body, Line = at.Line, Col = at.Col };

        while (At(Tok.KwCatch))
        {
            Token catchAt = _t[_i++];
            TypeRef? type = null;
            string? name = null;

            if (Take(Tok.LParen))
            {
                type = ParseTypeRef();

                if (At(Tok.Ident))
                {
                    name = _t[_i++].Text;
                }
                Expect(Tok.RParen, "')' after the catch clause");
            }

            // `when (cond)` between the clause and its block, where C# puts
            // it. The brackets are required here, unlike a switch guard.
            Expr? filter = null;

            if (At(Tok.Ident) && Cur.Text == "when")
            {
                _i++;
                Expect(Tok.LParen, "'(' after 'when'");
                filter = ParseExpr();
                Expect(Tok.RParen, "')' after the filter");
            }

            // The body may contain a bare `throw;`, which needs the clause to
            // have a name -- and gives it one if the author did not.
            CatchScope scope = new() { Name = name };

            _catches.Add(scope);
            Block caughtBody = ParseBlock();
            _catches.RemoveAt(_catches.Count - 1);

            t.Catches.Add(new CatchClause
            {
                Type = type, Name = scope.Name, When = filter, Body = caughtBody,
                Line = catchAt.Line, Col = catchAt.Col,
            });
        }

        Block? fin = Take(Tok.KwFinally) ? ParseBlock() : null;

        TryStmt built = new() { Body = body, Finally = fin, Line = at.Line, Col = at.Col };
        built.Catches.AddRange(t.Catches);
        return built;
    }

    /// <summary>A local declaration or an expression statement, without the semicolon.</summary>
    private Stmt ParseSimpleStmt()
    {
        Token at = Cur;

        // A REF LOCAL: `ref ulong word = ref _bits[i];`, `ref readonly var p
        // = ref points[0];`. No expression statement starts with `ref`, so
        // the word alone says this is one. `scoped` in front narrows how far
        // the reference may escape, which a ref local's own rules already
        // keep to this method, so it is read and has nothing more to do.
        if (At(Tok.KwRef) || (At(Tok.Ident) && Cur.Text == "scoped" && Ahead().Kind == Tok.KwRef))
        {
            if (At(Tok.Ident)) _i++;
            _i++;
            bool readOnly = Take(Tok.KwReadonly);
            TypeRef? type = Take(Tok.KwVar) ? null : ParseTypeRef();
            LocalDecl RefLocal(Token where)
            {
                string name = Expect(Tok.Ident, "a variable name").Text;
                Expect(Tok.Assign, "'=' -- a ref local must be initialised with 'ref' and a variable");
                return new LocalDecl
                {
                    Type = type, Name = name, Init = ParseRefValue(), IsRef = true, IsReadOnlyRef = readOnly,
                    Line = where.Line, Col = where.Col,
                };
            }
            LocalDecl first = RefLocal(at);
            while (Take(Tok.Comma))
            {
                first.Also.Add(RefLocal(Cur));
            }
            return first;
        }

        // `var (a, b) = …` is a DECONSTRUCTION and is handled below; `var x =`
        // is a declaration and is handled here.
        if (At(Tok.KwVar) && Ahead().Kind != Tok.LParen)
        {
            _i++;
            string name = Expect(Tok.Ident, "a variable name").Text;
            Expect(Tok.Assign, "'=' — a 'var' declaration must be initialised");

            LocalDecl inferred = new()
            {
                Type = null, Name = name, Init = ParseExpr(), Line = at.Line, Col = at.Col,
            };

            while (Take(Tok.Comma))
            {
                Token where = Cur;
                string next = Expect(Tok.Ident, "another variable name").Text;

                Expect(Tok.Assign, "'=' — a 'var' declaration must be initialised");
                inferred.Also.Add(new LocalDecl
                {
                    Type = null, Name = next, Init = ParseExpr(), Line = where.Line, Col = where.Col,
                });
            }

            return inferred;
        }

        // `(string file, int at) = _origins[i];` and `var (a, b) = pair;` --
        // taking a value apart into several names, which C# writes as a
        // statement and this compiler's own Assembler.cs does twice.
        //
        // Told from a tuple EXPRESSION by what follows the bracket: a
        // deconstruction is a list of names and then an '='.
        if (At(Tok.LParen) || (At(Tok.KwVar) && Ahead().Kind == Tok.LParen))
        {
            int was = _i;
            bool inferred = Take(Tok.KwVar);

            try
            {
                _i++;                           // the '('

                List<Binding> names = ReadBindings(inferred);

                if (names.Count > 1 && Take(Tok.RParen) && Take(Tok.Assign))
                {
                    DeconstructStmt taken = new()
                    {
                        Value = ParseExpr(), Line = at.Line, Col = at.Col,
                    };

                    taken.Names.AddRange(names);
                    return taken;
                }
            }
            catch (CompileError)
            {
                // Not a deconstruction into new names; the targets may still
                // be things that already exist -- see below.
            }

            _i = was;

            // A DECONSTRUCTING ASSIGNMENT: `(a, b) = (b, a)`, whose targets are
            // not names being declared but places that already exist. C# allows
            // any assignable expression in each position, which is why the two
            // forms cannot be told apart until the '=' is reached: `(int a, int
            // b) = p` declares and `(a, b) = p` assigns.
            if (!inferred && AssignmentAfterBrackets())
            {
                _i++;                           // the '('

                List<Binding> targets = new();

                do
                {
                    Token where = Cur;

                    targets.Add(new Binding
                    {
                        Name = $"$assign${_hidden++}", Target = ParseExpr(),
                        Line = where.Line, Col = where.Col,
                    });
                }
                while (Take(Tok.Comma));

                Expect(Tok.RParen, "')' after the places being assigned");
                Expect(Tok.Assign, "'=' after the places being assigned");

                DeconstructStmt into = new()
                {
                    Value = ParseExpr(), Line = at.Line, Col = at.Col,
                };

                into.Names.AddRange(targets);
                return into;
            }
        }

        // A LOCAL FUNCTION: `void Flush() { ... }` written inside a method.
        //
        // It becomes a local holding a lambda, which is what it is -- a body
        // that closes over the enclosing method's variables. That lowering is
        // only honest now that closures capture BY REFERENCE: a local function
        // accumulating into an enclosing variable is the commonest possible
        // use, and until this week it would have compiled and quietly updated a
        // copy.
        //
        // Recursion works because the name is in scope inside the body and the
        // capture is a cell: the lambda reads the local when it is CALLED, by
        // which time the local holds the lambda.
        // A declaration and an expression can start identically, so try the
        // declaration and fall back rather than guessing from one token.
        //
        // A bracket is included because a TUPLE TYPE begins with one:
        // `(int, string) t = Two();` is a declaration and `(a, b)` is an
        // expression, and only what follows the closing bracket tells them
        // apart -- which is exactly what trying and falling back does.
        if (At(Tok.Ident) || At(Tok.KwConst) || At(Tok.LParen) || (At(Tok.KwDelegate) && Ahead().Kind == Tok.Star))
        {
            int save = _i;
            bool constant = Take(Tok.KwConst);

            try
            {
                TypeRef type = ParseTypeRef();

                if (At(Tok.Ident) && (Ahead().Kind is Tok.Assign or Tok.Semi or Tok.Comma))
                {
                    string name = _t[_i++].Text;
                    Expr? init = Take(Tok.Assign) ? Initialiser(type) : null;
                    LocalDecl first = new()
                    {
                        Type = type, Name = name, Init = init, IsConst = constant,
                        Line = at.Line, Col = at.Col,
                    };

                    // `int line = _line, col = _col, start = _pos;` -- one
                    // declaration, several variables, all of the same type and
                    // all in the same scope.
                    while (Take(Tok.Comma))
                    {
                        Token where = Cur;
                        string next = Expect(Tok.Ident, "another variable name").Text;

                        first.Also.Add(new LocalDecl
                        {
                            Type = type, Name = next,
                            // As the first: `ulong[] a = { 1 }, b = { 2 };`.
                            Init = Take(Tok.Assign) ? Initialiser(type) : null,
                            IsConst = constant,
                            Line = where.Line, Col = where.Col,
                        });
                    }

                    return first;
                }
            }
            catch (CompileError why)
            {
                if (Switches.TraceDecl)
                {
                    Console.Error.WriteLine($"[decl] gave up: {why}");
                }
            }
            _i = save;
        }

        return new ExprStmt { Expr = ParseExpr(), Line = at.Line, Col = at.Col };
    }

    // ---- expressions ------------------------------------------------------

    public Expr ParseExpr() => ParseAssign();

    private Expr ParseAssign()
    {
        Expr left = ParseConditional();
        Token at = Cur;

        // `x ??= v` IS `x = x ?? v`, and it is written out as that rather than
        // carried to the binder as a compound operation. Coalesce is not an
        // arithmetic operator -- it short-circuits -- so a compound assignment
        // that evaluated both sides would not mean what was written, while the
        // spelled-out form gets the short-circuit from the ordinary rules.
        //
        // THE TARGET IS READ TWICE by that rewrite, so a target that DOES
        // something is refused rather than quietly done twice. It is the same
        // restriction, for the same reason, that a pattern subject has.
        if (At(Tok.QuestionQuestionEq))
        {
            _i++;

            Expr right = ParseAssign();
            AssignExpr Coalescing(Expr target) => new()
            {
                Target = target,
                Value = new BinaryExpr { Op = BinOp.Coalesce, Left = target, Right = right, Line = at.Line, Col = at.Col },
                Line = at.Line, Col = at.Col,
            };
            if (Rereadable(left)) return Coalescing(left);

            // WHAT THE TARGET IS READ THROUGH IS EVALUATED ONCE, as C# has it:
            // `Use(n).Loads ??= new()` calls Use once, and the field of what it
            // answered is read and written. The object -- and an element's
            // index -- is put somewhere first (SubjectExpr), and the target
            // names it there.
            if (left is MemberExpr { Target: { } owner } field)
            {
                MemberExpr through = new() { Target = new SubjectExpr { Line = at.Line, Col = at.Col }, Name = field.Name, Line = field.Line, Col = field.Col };
                return new PatternExpr { Subject = owner, Test = Coalescing(through), Line = at.Line, Col = at.Col };
            }
            if (left is IndexExpr { Target: { } array, Args.Count: 1 } element)
            {
                IndexExpr through = new() { Target = new SubjectExpr { Outer = 1, Line = at.Line, Col = at.Col }, Line = element.Line, Col = element.Col };
                through.Args.Add(new SubjectExpr { Line = at.Line, Col = at.Col });
                PatternExpr index = new() { Subject = element.Args[0], Test = Coalescing(through), Line = at.Line, Col = at.Col };
                return new PatternExpr { Subject = array, Test = index, Line = at.Line, Col = at.Col };
            }
            throw Error("the left of '??=' must be a name, a field or an element");
        }

        // Two questions, not one: is this an assignment at all, and if so is it
        // compound? A single nullable cannot answer both, because plain '='
        // legitimately has no operation.
        bool isAssignment = true;
        BinOp? compound = Cur.Kind switch
        {
            Tok.Assign    => null,
            Tok.PlusEq    => BinOp.Add,
            Tok.MinusEq   => BinOp.Sub,
            Tok.StarEq    => BinOp.Mul,
            Tok.SlashEq   => BinOp.Div,
            Tok.PercentEq => BinOp.Rem,
            Tok.AmpEq     => BinOp.And,
            Tok.PipeEq    => BinOp.Or,
            Tok.CaretEq   => BinOp.Xor,
            Tok.ShlEq     => BinOp.Shl,
            Tok.ShrEq     => BinOp.Shr,
            Tok.UShrEq    => BinOp.UShr,
            _             => Not(out isAssignment),
        };

        if (!isAssignment)
        {
            return left;
        }

        _i++;
        // `r = ref other;` POINTS A REF LOCAL ELSEWHERE rather than writing
        // through it: the value is the reference itself (ParseRefValue).
        if (compound is null && At(Tok.KwRef))
        {
            return new AssignExpr { Target = left, Value = ParseRefValue(), Line = at.Line, Col = at.Col };
        }
        // Right associative: a = b = c groups as a = (b = c).
        return new AssignExpr { Op = compound, Target = left, Value = ParseAssign(), Line = at.Line, Col = at.Col };
    }

    /// <summary>
    /// `ref x`, where a REFERENCE is wanted rather than a value: a ref local's
    /// initialiser, what a ref local is pointed at again. It is the node a
    /// by-reference argument already is (RefArgExpr) -- the address of a
    /// variable -- so everything that walks one walks this.
    /// </summary>
    private Expr ParseRefValue()
    {
        Token at = Cur;
        // Unless what follows is a LAMBDA THAT RETURNS BY REFERENCE, `return
        // ref int (int[] a) => ref a[0];`, whose `ref` is its own.
        if (StartsRefLambda()) return ParseConditional();
        Expect(Tok.KwRef, "'ref' and the variable referred to");
        return new RefArgExpr { Target = ParseConditional(), Line = at.Line, Col = at.Col };
    }

    /// <summary>Switch arms must yield a value, so "no" needs a shape to return.</summary>
    private static BinOp? Not(out bool flag)
    {
        flag = false;
        return null;
    }

    private Expr ParseConditional()
    {
        Expr cond = ParseBinary(0);

        if (!At(Tok.Question))
        {
            return cond;
        }

        Token at = _t[_i++];
        Expr then = ParseExpr();
        Expect(Tok.Colon, "':' in a conditional expression");
        return new ConditionalExpr { Cond = cond, Then = then, Else = ParseExpr(), Line = at.Line, Col = at.Col };
    }

    /// <summary>
    /// A RANGE, `a..b`, which binds tighter than any binary operator and
    /// looser than a unary one (C# 12.10): its bounds are unary expressions,
    /// and either may be left out -- `..`, `2..`, `..^1`.
    /// </summary>
    private Expr ParseRange()
    {
        Token at = Cur;
        Expr? first = At(Tok.DotDot) ? null : ParseUnary();
        if (!At(Tok.DotDot))
        {
            return first!;
        }
        _i++;
        return new RangeExpr
        {
            From = first, To = StartsOperand(Cur.Kind) ? ParseUnary() : null,
            Line = at.Line, Col = at.Col,
        };
    }

    /// <summary>Whether a token can begin a unary expression.</summary>
    private static bool StartsOperand(Tok k)
        => k is Tok.Ident or Tok.Int or Tok.Real or Tok.Str or Tok.InterpStr or Tok.Char or Tok.Utf8Str
             or Tok.LParen or Tok.Minus or Tok.Plus or Tok.Bang or Tok.Tilde or Tok.Caret
             or Tok.PlusPlus or Tok.MinusMinus or Tok.Star or Tok.Amp
             or Tok.KwNew or Tok.KwThis or Tok.KwBase or Tok.KwTrue or Tok.KwFalse or Tok.KwNull
             or Tok.KwDefault or Tok.KwAwait;

    private static int Precedence(Tok k) => k switch
    {
        Tok.QuestionQuestion => 1,
        Tok.OrOr   => 2,
        Tok.AndAnd => 3,
        Tok.Pipe   => 4,
        Tok.Caret  => 5,
        Tok.Amp    => 6,
        Tok.Eq or Tok.NotEq => 7,
        Tok.Lt or Tok.Gt or Tok.LtEq or Tok.GtEq or Tok.KwIs or Tok.KwAs => 8,
        Tok.Shl or Tok.Shr or Tok.UShr => 9,
        Tok.Plus or Tok.Minus => 10,
        Tok.Star or Tok.Slash or Tok.Percent => 11,
        _ => 0,
    };

    private Expr ParseBinary(int minPrec)
    {
        Expr left = ParseRange();

        // `subject switch { ... }` binds tighter than any operator, which is
        // why it is taken here rather than given a precedence: the subject is
        // whatever was just parsed, and the arms are a brace-delimited list, so
        // there is nothing for precedence to decide.
        if (At(Tok.KwSwitch) && _t[_i + 1].Kind == Tok.LBrace)
        {
            left = ParseSwitchExpr(left);
        }

        // `source with { A = 1 }` -- a COPY with those members changed.
        //
        // POSTFIX, not a binary operator: `with` is an ordinary word to the
        // lexer and so has no precedence, and the loop below returns as soon as
        // it meets a token with none. Put there, this was simply never reached.
        //
        // Looped, because `a with { X = 1 } with { Y = 2 }` is two copies and
        // reads left to right like any other postfix.
        while (At(Tok.Ident) && Cur.Text == "with" && Ahead().Kind == Tok.LBrace)
        {
            Token where = Cur;

            _i++;
            _i++;                               // the '{'

            WithExpr copy = new() { Source = left, Line = where.Line, Col = where.Col };

            while (!At(Tok.RBrace))
            {
                Token member = Expect(Tok.Ident, "a member name");

                Expect(Tok.Assign, "'=' after the member name");

                copy.Body.WritableInits.Add(new InitAssign
                {
                    Name = member.Text, Value = ParseExpr(),
                    Line = member.Line, Col = member.Col,
                });

                if (!Take(Tok.Comma))
                {
                    break;
                }
            }

            Expect(Tok.RBrace, "'}' to close the with expression");
            left = copy;
        }

        while (true)
        {
            int prec = Precedence(Cur.Kind);

            if (prec == 0 || prec < minPrec)
            {
                return left;
            }

            Token at = Cur;

            if (At(Tok.KwIs))
            {
                _i++;

                // ONE PATH FOR EVERY PATTERN, which is what lets `or` and `and`
                // join any two of them.
                //
                // This used to dispatch on the shape here as well as in
                // ParseIsPattern -- two copies of the same decision, and the
                // copies had drifted. The one here returned as soon as it
                // recognised a null or a type, so `x is null or 0` and
                // `x is Foo or Bar` stopped at the `or` and reported a missing
                // bracket. The other copy is gone; this calls the parser.
                //
                // `not` is an ordinary word to the lexer, matched by its text,
                // and applied once at the end so every shape is negatable
                // without each of them knowing about it.
                bool notted = At(Tok.Ident) && Cur.Text == "not";

                if (notted)
                {
                    _i++;
                }

                left = ParseIsPattern(left, at);

                if (notted)
                {
                    left = new UnaryExpr { Op = UnOp.Not, Operand = left, Line = at.Line, Col = at.Col };
                }
                continue;
            }

            if (At(Tok.KwAs))
            {
                _i++;
                left = new AsExpr { Operand = left, Type = ParseTypeRef(), Line = at.Line, Col = at.Col };
                continue;
            }

            BinOp op = Cur.Kind switch
            {
                Tok.QuestionQuestion => BinOp.Coalesce,
                Tok.OrOr   => BinOp.OrElse,
                Tok.AndAnd => BinOp.AndAlso,
                Tok.Pipe   => BinOp.Or,
                Tok.Caret  => BinOp.Xor,
                Tok.Amp    => BinOp.And,
                Tok.Eq     => BinOp.Eq,
                Tok.NotEq  => BinOp.Ne,
                Tok.Lt     => BinOp.Lt,
                Tok.Gt     => BinOp.Gt,
                Tok.LtEq   => BinOp.Le,
                Tok.GtEq   => BinOp.Ge,
                Tok.Shl    => BinOp.Shl,
                Tok.Shr    => BinOp.Shr,
                Tok.UShr   => BinOp.UShr,
                Tok.Plus   => BinOp.Add,
                Tok.Minus  => BinOp.Sub,
                Tok.Star   => BinOp.Mul,
                Tok.Slash  => BinOp.Div,
                _          => BinOp.Rem,
            };
            _i++;

            // Left associative, so the right side binds tighter by one.
            Expr right = ParseBinary(prec + 1);
            left = new BinaryExpr { Op = op, Left = left, Right = right, Line = at.Line, Col = at.Col };
        }
    }

    /// <summary>
    /// What follows the '=' of a declaration.
    ///
    /// Almost always an ordinary expression -- with ONE exception, which is
    /// C#'s: `Tok[] two = { a, b };` leaves out the `new[]` because the
    /// declaration has already said what the elements are. The braces mean
    /// exactly what `new T[] { … }` means, so that is what they become.
    /// </summary>
    /// <summary>
    /// What is inside one pair of initialiser braces, reading the `{` this is
    /// called on and the `}` that closes it.
    ///
    /// C# writes four kinds of element between the same braces and one token of
    /// lookahead tells them apart: `[key] = value` calls the indexer, `Name = …`
    /// sets a member, `{ k, v }` is one element added with two arguments, and
    /// anything else is one element added with one. `Name = { … }` is the odd
    /// one -- it does not assign the member at all but initialises what the
    /// member already holds, so its braces are read here again.
    ///
    /// A trailing comma is allowed because this is a list somebody edits, and a
    /// list somebody edits gains and loses lines.
    /// </summary>
    private void ReadInitBody(InitBody body)
    {
        Expect(Tok.LBrace, "'{' to open the initialiser");

        while (!At(Tok.RBrace) && !At(Tok.End))
        {
            Token where = Cur;

            // `[key] = value`, which C# defines as a call to the INDEXER. A
            // keyword table is written this way.
            if (Take(Tok.LBracket))
            {
                List<Expr> index = new();

                do
                {
                    index.Add(ParseExpr());
                }
                while (Take(Tok.Comma));

                Expect(Tok.RBracket, "']' after the index");
                Expect(Tok.Assign, "'=' after the index");

                InitIndex one = new()
                {
                    Value = ParseExpr(), Line = where.Line, Col = where.Col,
                };

                one.Args.AddRange(index);
                body.WritableIndexes.Add(one);
            }
            else if (At(Tok.Ident) && Ahead().Kind == Tok.Assign)
            {
                Token member = _t[_i++];

                _i++;                           // the '='

                // `Name = { … }` -- a NESTED initialiser, not a value.
                if (At(Tok.LBrace))
                {
                    InitBody inner = new() { Line = Cur.Line, Col = Cur.Col };

                    ReadInitBody(inner);
                    body.WritableInits.Add(new InitAssign
                    {
                        Name = member.Text, Nested = inner,
                        Line = member.Line, Col = member.Col,
                    });
                }
                else
                {
                    body.WritableInits.Add(new InitAssign
                    {
                        Name = member.Text, Value = ParseExpr(),
                        Line = member.Line, Col = member.Col,
                    });
                }
            }
            else
            {
                InitAdd add = new() { Line = where.Line, Col = where.Col };

                // `{ k, v }` -- one element, added with two arguments.
                if (Take(Tok.LBrace))
                {
                    do
                    {
                        add.Args.Add(ParseExpr());
                    }
                    while (Take(Tok.Comma));

                    Expect(Tok.RBrace, "'}' to close this element");
                }
                else
                {
                    add.Args.Add(ParseExpr());
                }

                body.WritableAdds.Add(add);
            }

            if (!Take(Tok.Comma))
            {
                break;
            }
        }

        Expect(Tok.RBrace, "'}' to close the initialiser");
    }

    private Expr Initialiser(TypeRef declared)
    {
        if (BareDefault(declared) is Expr zero)
        {
            return zero;
        }

        // A COLLECTION EXPRESSION -- `int[] a = [1, 2, 3];` -- is the braced
        // initialiser with square brackets, and it is read as exactly that.
        // C# target-types it from the declaration, which is the type this
        // already has in hand; what it means is the same array either way.
        // One with a spread in it (`[..head, 3]`) is the general collection
        // expression, which the checker makes from the declared type.
        if (At(Tok.LBracket) && declared.ArrayRank > 0 && !SpreadInBrackets())
        {
            return ElementList(declared, Tok.RBracket);
        }

        if (!At(Tok.LBrace) || declared.ArrayRank == 0)
        {
            return ParseExpr();
        }

        return ElementList(declared, Tok.RBrace);
    }

    /// <summary>
    /// Whether the bracketed list starting here has a spread (`..x`) at its
    /// own level, where an element begins.
    /// </summary>
    private bool SpreadInBrackets()
    {
        int depth = 0;
        for (int j = _i; j < _t.Count && _t[j].Kind != Tok.End; j++)
        {
            Tok kind = _t[j].Kind;
            if (kind is Tok.LBracket or Tok.LParen or Tok.LBrace) depth++;
            else if (kind is Tok.RBracket or Tok.RParen or Tok.RBrace)
            {
                if (--depth == 0) return false;
            }
            else if (depth == 1 && kind == Tok.DotDot && _t[j - 1].Kind is Tok.LBracket or Tok.Comma) return true;
        }
        return false;
    }

    /// <summary>
    /// `default` with no type after it, as the `default(T)` it stands for.
    ///
    /// Only where the type is WRITTEN NEARBY -- a declaration's type, a
    /// method's return type -- because that is the only thing the parser knows
    /// and the checker's target typing is not reachable from here. Null when
    /// the next token is not a bare `default`, so the caller reads an ordinary
    /// expression instead.
    /// </summary>
    private Expr? BareDefault(TypeRef? declared)
    {
        if (declared is null || !At(Tok.KwDefault) || Ahead().Kind == Tok.LParen)
        {
            return null;
        }

        Token at = _t[_i++];
        Expr zero = new DefaultExpr { Type = declared, Line = at.Line, Col = at.Col };
        // `T Item = default!;` -- forgiven, as any value may be.
        while (At(Tok.Bang) && Ahead().Kind is not (Tok.Assign or Tok.End))
        {
            _i++;
            zero = new SuppressExpr { Operand = zero, Line = at.Line, Col = at.Col };
        }
        return zero;
    }

    /// <summary>The elements of an array initialiser, however they are bracketed.</summary>
    private Expr ElementList(TypeRef declared, Tok closer)
    {
        Token at = _t[_i++];
        NewExpr listed = new()
        {
            // ONE RANK OFF, AND THE ELEMENT'S NULLABILITY COMES WITH IT.
            // `string?[]` is an array whose ELEMENTS may be null, which the
            // written type records apart from its own nullability; take a rank
            // away and that is now the type's own. Without this
            // `string?[] a = { x, null };` read its elements as plain strings
            // and reported the null as one.
            Type = new TypeRef
            {
                Name = declared.Name, Arguments = declared.Args,
                Nullable = declared.ArrayRank == 1 ? declared.ElementNullable : (declared.InnerNullable & 1) != 0,
                ElementNullable = declared.ArrayRank > 1 && declared.ElementNullable,
                InnerNullable = declared.InnerNullable >> 1,
                PointerDepth = declared.PointerDepth, ArrayRank = declared.ArrayRank - 1,
                TupleNames = declared.TupleNames, Line = at.Line, Col = at.Col,
            },
            Elements = new List<Expr>(),
            Line = at.Line, Col = at.Col,
        };

        while (!At(closer) && !At(Tok.End))
        {
            // AN ELEMENT MAY BE A LIST TOO: `int[][] rows = [[1, 2], [3, 4]];`
            // is an array of arrays, and each row is initialised the same way
            // the whole is. Read through Initialiser so a nested list gets the
            // element type, whichever brackets it is written with.
            listed.Elements.Add(Initialiser(listed.Type));

            if (!Take(Tok.Comma))
            {
                break;
            }
        }

        Expect(closer, closer == Tok.RBrace ? "'}' to close the elements"
                                            : "']' to close the elements");
        return listed;
    }

    /// <summary>
    /// One index, which may be a RANGE: `s[1..4]`, `s[2..]`, `s[..3]`, `s[..]`.
    ///
    /// Written the way C# writes it, and it means the same: the first bound is
    /// where the slice starts and the second is one past where it ends, with an
    /// omitted one standing for the beginning or the end of what is sliced.
    /// </summary>
    private Expr Ranged()
    {
        Token at = Cur;

        if (Take(Tok.DotDot))
        {
            return new RangeExpr
            {
                From = null,
                To = At(Tok.RBracket) || At(Tok.Comma) ? null : End(),
                Line = at.Line, Col = at.Col,
            };
        }

        Expr first = End();

        if (first is RangeExpr || !Take(Tok.DotDot))
        {
            return first;
        }

        return new RangeExpr
        {
            From = first,
            To = At(Tok.RBracket) || At(Tok.Comma) ? null : End(),
            Line = at.Line, Col = at.Col,
        };
    }

    /// <summary>
    /// One end of an index or a range, which may be counted from the END:
    /// `s[^1]`, `s[1..^1]`.
    /// </summary>
    private Expr End() => ParseExpr();

    private Expr ParseUnary()
    {
        Token at = Cur;

        // `throw` WHERE A VALUE BELONGS: `source ?? throw new …`, and either
        // arm of a conditional. Taken here rather than at the top of an
        // expression because that is where an OPERAND is read from -- the right
        // of `??` is parsed by ParseBinary and never sees the top.
        //
        // It swallows the rest of the expression, which is right: what is
        // thrown is one expression and control does not come back.
        if (At(Tok.KwThrow))
        {
            _i++;
            return new ThrowExpr { Value = ParseExpr(), Line = at.Line, Col = at.Col };
        }

        switch (Cur.Kind)
        {
            case Tok.Minus:
                _i++;
                return new UnaryExpr { Op = UnOp.Neg, Operand = ParseUnary(), Line = at.Line, Col = at.Col };

            // `^1`, AN INDEX FROM THE END: a System.Index anywhere, and a
            // position counted back from the end inside an index.
            case Tok.Caret:
                _i++;
                return new FromEndExpr { Offset = ParseUnary(), Line = at.Line, Col = at.Col };

            case Tok.Bang:
                _i++;
                return new UnaryExpr { Op = UnOp.Not, Operand = ParseUnary(), Line = at.Line, Col = at.Col };

            case Tok.Tilde:
                _i++;
                return new UnaryExpr { Op = UnOp.BitNot, Operand = ParseUnary(), Line = at.Line, Col = at.Col };

            case Tok.PlusPlus:
                _i++;
                return new UnaryExpr { Op = UnOp.PreInc, Operand = ParseUnary(), Line = at.Line, Col = at.Col };

            case Tok.MinusMinus:
                _i++;
                return new UnaryExpr { Op = UnOp.PreDec, Operand = ParseUnary(), Line = at.Line, Col = at.Col };

            case Tok.Plus:
                _i++;
                return new UnaryExpr { Op = UnOp.Plus, Operand = ParseUnary(), Line = at.Line, Col = at.Col };

            // `*p` -- the thing at an address. Unambiguous here: multiplication
            // is binary and never begins an expression, so a star in prefix
            // position can only be a dereference.
            case Tok.Star:
                _i++;
                return new UnaryExpr { Op = UnOp.Deref, Operand = ParseUnary(), Line = at.Line, Col = at.Col };

            // `&x` -- the address of a thing. Same reasoning: bitwise-and is
            // binary, so a leading ampersand is an address-of.
            case Tok.Amp:
                _i++;
                return new UnaryExpr { Op = UnOp.AddressOf, Operand = ParseUnary(), Line = at.Line, Col = at.Col };

            case Tok.KwAwait:
                _i++;
                return new AwaitExpr { Operand = ParseUnary(), Line = at.Line, Col = at.Col };

            // A LAMBDA, IN ITS THREE SHAPES. `x => ...`, `(a, b) => ...` and
            // `() => ...`.
            //
            // Tried before anything else at this position because the opening
            // of a lambda is the opening of three other things: a bare name, a
            // parenthesised expression, and a cast. What settles it is the
            // arrow, which is why this scans ahead for one rather than
            // committing and backing out.
            // `static x => ...`, `static (a, b) => ...`, `static async ...`: a
            // lambda that captures nothing, by its own promise -- nothing to
            // do but read past the word, as a capture would be refused by
            // C# before it ever reached here.
            case Tok.KwStatic
                when Ahead().Kind == Tok.Ident && _t[_i + 2].Kind == Tok.FatArrow && _i + 2 != _armArrow
                  || Ahead().Kind == Tok.LParen && IsLambdaHeadAt(_i + 1)
                  || Ahead().Kind == Tok.Ident && Ahead().Text == "async":
            {
                _i++;
                return ParseUnary();
            }

            // `async x => ...` and `async (a, b) => ...`: the word is a
            // modifier here and nowhere else in an expression.
            case Tok.Ident when Cur.Text == "async"
                && (Ahead().Kind == Tok.Ident && _t[_i + 2].Kind == Tok.FatArrow && _i + 2 != _armArrow
                    || Ahead().Kind == Tok.LParen && IsLambdaHeadAt(_i + 1)):
            {
                _i++;
                LambdaExpr inner = (LambdaExpr)ParseUnary();
                LambdaExpr made = new()
                {
                    Body = inner.Body, BlockBody = inner.BlockBody, Async = true, TypesWritten = inner.TypesWritten,
                    Returns = inner.Returns, ReturnMods = inner.ReturnMods,
                    Line = at.Line, Col = at.Col,
                };
                made.WritableAttributes.AddRange(inner.Attributes);
                made.WritableParams.AddRange(inner.Params);
                return made;
            }

            // A LAMBDA WITH ITS RESULT WRITTEN: `ref int (int[] a) => ref
            // a[0]` (C# 10). The lambda is read as any other, and keeps what
            // it said it returns for the binder to hold the delegate to.
            case Tok.KwRef when StartsRefLambda():
                return TypedLambda(at);

            case Tok.Ident when Ahead().Kind == Tok.FatArrow && _i + 1 != _armArrow:
            {
                List<string> one = new() { _t[_i++].Text };

                _i++;
                return Finish(new LambdaExpr { Line = at.Line, Col = at.Col }, one);
            }

            // A LAMBDA WITH ITS RESULT WRITTEN, `int (int x) => x` (C# 10;
            // StartsTypedLambda says how it is told from a call). Read as any
            // lambda, keeping the result for the binder to hold the delegate
            // to and to give the lambda its natural type by.
            case Tok.Ident when StartsTypedLambda():
                return TypedLambda(at);

            // `[A] (int x) => x`: a lambda's attributes, kept on it.
            case Tok.LBracket when StartsAttributedLambda():
            {
                List<AttributeRef> attributes = new();
                SkipLambdaAttributes(attributes);
                LambdaExpr inner = (LambdaExpr)ParseUnary();
                inner.WritableAttributes.InsertRange(0, attributes);
                return inner;
            }

            case Tok.LParen when IsLambdaHead():
            {
                _i++;

                List<string> names = new();
                List<Tok> modifiers = new();
                List<TypeRef?> types = new();

                while (!At(Tok.RParen))
                {
                    // `([NotNull] string s) => ...`: a parameter's attributes
                    // (C# 10), read past -- nothing here reads them.
                    if (At(Tok.LBracket)) SkipLambdaAttributes(null);

                    // `(string s, out int v) => ...`: the word says how the
                    // argument is passed, and the delegate's Invoke must say
                    // the same (C# 12.19.2). Kept for the binder.
                    Tok modifier = At(Tok.KwRef) || At(Tok.KwOut) || At(Tok.KwIn) ? _t[_i++].Kind : Tok.End;

                    // `(int a, List<string> b) => ...`: a type before the name
                    // is kept. Where the lambda converts to a delegate the
                    // delegate says the types anyway; where nothing does --
                    // `var f = (int a) => a + 1;` -- they are what its natural
                    // type is made of (C# 10).
                    TypeRef? written = null;
                    if (!(At(Tok.Ident) && Ahead().Kind is Tok.Comma or Tok.RParen))
                    {
                        written = ParseTypeRef();
                    }

                    names.Add(Expect(Tok.Ident, "a lambda parameter name").Text);
                    modifiers.Add(modifier);
                    types.Add(written);

                    if (!Take(Tok.Comma))
                    {
                        break;
                    }
                }

                Expect(Tok.RParen, "')' after the lambda parameters");
                Expect(Tok.FatArrow, "'=>' after the lambda parameters");
                return Finish(new LambdaExpr { Line = at.Line, Col = at.Col }, names, modifiers, types);
            }

            // `(int, int) (int x) => (x, x)`: the written result a tuple.
            case Tok.LParen when StartsTypedLambda():
                return TypedLambda(at);

            case Tok.LParen:
            {
                // A cast and a parenthesised expression start identically.
                int save = _i;
                _i++;

                // A CAST TO A TUPLE TYPE, `((long, string))x`, begins with a
                // second bracket: the type written as one, then the operand.
                bool tuple = At(Tok.LParen);
                if (tuple || At(Tok.Ident) || At(Tok.KwVoid) || (At(Tok.KwDelegate) && Ahead().Kind == Tok.Star))
                {
                    try
                    {
                        TypeRef type = ParseTypeRef();

                        if (At(Tok.RParen) && (!tuple || type.Args.Count >= 2)
                            && (CastCanFollow(Ahead().Kind) || !tuple && CannotBeExpression(type)))
                        {
                            _i++;
                            return new CastExpr { Type = type, Operand = ParseUnary(), Line = at.Line, Col = at.Col };
                        }
                    }
                    catch (CompileError)
                    {
                        // Not a type; fall through and read it as a group.
                    }
                }
                _i = save;
                break;
            }
        }
        return ParsePostfix();
    }

    /// <summary>
    /// Whether a token could begin the operand of a cast. Without this,
    /// <c>(a) - b</c> reads as a cast of <c>-b</c> to type <c>a</c>.
    /// </summary>
    private static bool CastCanFollow(Tok k)
        => k is Tok.Ident or Tok.Int or Tok.Real or Tok.Str or Tok.InterpStr or Tok.Char
             or Tok.LParen or Tok.KwThis or Tok.KwBase or Tok.KwNew
             or Tok.KwTrue or Tok.KwFalse or Tok.KwNull or Tok.Bang or Tok.Tilde;

    /// <summary>The names that can only ever be a type, never a variable.</summary>
    private static readonly HashSet<string> Predefined = new(StringComparer.Ordinal)
    {
        "bool", "byte", "sbyte", "short", "ushort", "int", "uint", "long", "ulong",
        "nint", "nuint", "char", "float", "double", "decimal", "string", "object", "void",
    };

    /// <summary>
    /// Whether a parenthesised sequence that PARSES as a type could not also
    /// have been an expression -- which is what C# uses to settle
    /// <c>(short)-2</c>. A cast to a predefined type, an array, a pointer or a
    /// nullable is not a name anything could be bound to, so the '-' after it
    /// is the sign of the operand and not a subtraction. <c>(a)-b</c>, where a
    /// is an ordinary name, stays a subtraction.
    /// </summary>
    private static bool CannotBeExpression(TypeRef type)
        => Predefined.Contains(type.Name) || type.ArrayRank > 0
            || type.PointerDepth > 0 || type.Nullable;

    private Expr ParsePostfix()
    {
        int start = Cur.Pos;
        Expr e = ParsePrimary();

        // Where the receiver of the member last read ends, for a call on it:
        // `value.Guard()` passes "value" to a [CallerArgumentExpression]
        // naming the extension's `this`.
        int receiverLast = -1;

        while (true)
        {
            Token at = Cur;

            bool spacedConditional = At(Tok.Question) && Ahead().Kind == Tok.Dot;
            int beforeDot = _i - 1;

            if (Take(Tok.Dot) || (At(Tok.QuestionDot) && Take(Tok.QuestionDot))
                              || spacedConditional)
            {
                receiverLast = beforeDot;
                bool nullCond = at.Kind == Tok.QuestionDot || spacedConditional;

                if (spacedConditional)
                {
                    _i += 2;
                }

                string name = Expect(Tok.Ident, "a member name after '.'").Text;
                MemberExpr m = new() { Target = e, Name = name, NullConditional = nullCond, Line = at.Line, Col = at.Col };

                if (At(Tok.Lt))
                {
                    int save = _i;

                    if (TryParseTypeArgs(out List<TypeRef> args))
                    {
                        m.WritableTypeArgs.AddRange(args);
                        if (name == "CreateInstance" && args is [{ Args.Count: 0, ArrayRank: 0, PointerDepth: 0 } made])
                        {
                            (_createdParams ??= new(StringComparer.Ordinal)).Add(made.Name);
                        }
                    }
                    else
                    {
                        _i = save;
                    }
                }
                e = m;
                continue;
            }

            if (At(Tok.LParen))
            {
                _i++;
                CallExpr call = new() { Target = e, Line = at.Line, Col = at.Col };
                int spanMark = _spans.Count;
                if (Source is not null)
                {
                    bool onMember = e is MemberExpr && receiverLast >= 0;
                    _spans.Add(onMember ? start : -1);
                    _spans.Add(onMember ? End(receiverLast) : -1);
                }

                if (!At(Tok.RParen))
                {
                    do
                    {
                        // A NAMED ARGUMENT: `With(nullable: true)`.
                        //
                        // Recorded rather than acted on -- which parameter it
                        // is depends on which overload is chosen, and only the
                        // binder knows that. `a ? b : c` cannot be confused
                        // with one, because a conditional's colon comes after
                        // a whole expression and this one comes after a bare
                        // name at the very start of an argument.
                        if (At(Tok.Ident) && Ahead().Kind == Tok.Colon)
                        {
                            call.WritableArgNames.Add(_t[_i++].Text);
                            _i++;
                        }
                        else
                        {
                            call.WritableArgNames.Add(null);
                        }

                        int argumentAt = ArgumentStart();
                        call.Args.Add(ParseArg());
                        if (Source is not null)
                        {
                            _spans.Add(argumentAt);
                            _spans.Add(End(_i - 1));
                        }
                    }
                    while (Take(Tok.Comma));
                }

                Expect(Tok.RParen, "')' to close the argument list");
                if (Source is not null)
                {
                    call.Spans = _spans.GetRange(spanMark, _spans.Count - spanMark).ToArray();
                    call.Source = Source;
                    _spans.RemoveRange(spanMark, _spans.Count - spanMark);
                }
                receiverLast = -1;
                e = call;
                continue;
            }

            // `a?[i]`, THE NULL-CONDITIONAL INDEX: the '?' written against the
            // '[', which is how it is told from a conditional whose result is
            // a collection expression, `a ? [x] : y`.
            bool conditionalIndex = At(Tok.Question) && Ahead().Kind == Tok.LBracket && Ahead().Pos == Cur.Pos + 1;
            if (conditionalIndex || At(Tok.LBracket))
            {
                _i += conditionalIndex ? 2 : 1;
                IndexExpr idx = new() { Target = e, NullConditional = conditionalIndex, Line = at.Line, Col = at.Col };

                do
                {
                    idx.Args.Add(Ranged());
                }
                while (Take(Tok.Comma));

                Expect(Tok.RBracket, "']' to close the index");
                e = idx;
                continue;
            }

            // `x!` -- the null-forgiving operator, which is a promise rather
            // than a test and generates nothing. Taken here because it is
            // POSTFIX; the lexer has already preferred `!=` where that was
            // written, so there is nothing to disambiguate.
            if (At(Tok.Bang) && Ahead().Kind is not (Tok.Assign or Tok.End))
            {
                _i++;
                e = new SuppressExpr { Operand = e, Line = at.Line, Col = at.Col };
                continue;
            }

            if (Take(Tok.PlusPlus))
            {
                e = new UnaryExpr { Op = UnOp.PostInc, Operand = e, Line = at.Line, Col = at.Col };
                continue;
            }

            if (Take(Tok.MinusMinus))
            {
                e = new UnaryExpr { Op = UnOp.PostDec, Operand = e, Line = at.Line, Col = at.Col };
                continue;
            }
            return e;
        }
    }

    private Expr ParsePrimary()
    {
        Token at = Cur;

        switch (Cur.Kind)
        {
            // A COLLECTION EXPRESSION, `[1, 2, ..more]`: C# 12's target-typed
            // collection, read as the target-typed `new() { 1, 2 }` it is,
            // brackets for braces (NewExpr.Collection).
            case Tok.LBracket:
            {
                _i++;
                NewExpr collection = new()
                {
                    Type = new TypeRef { Name = "", Line = at.Line, Col = at.Col },
                    Collection = true,
                    Line = at.Line, Col = at.Col,
                };
                while (!At(Tok.RBracket))
                {
                    Token elementAt = Cur;
                    InitAdd add = new() { Spread = Take(Tok.DotDot), Line = elementAt.Line, Col = elementAt.Col };
                    add.Args.Add(ParseExpr());
                    collection.Body.WritableAdds.Add(add);
                    if (!Take(Tok.Comma)) break;
                }
                Expect(Tok.RBracket, "']' to close the collection expression");
                return collection;
            }

            case Tok.Int:
                _i++;
                return new LiteralExpr { Kind = Lit.Int, Text = at.Text, IntValue = ParseIntText(at), Line = at.Line, Col = at.Col };

            case Tok.Real:
                _i++;
                return new LiteralExpr
                {
                    Kind = Lit.Real, Text = at.Text,
                    RealValue = double.Parse(RealDigits(at.Text).Replace("_", ""),
                                             System.Globalization.CultureInfo.InvariantCulture),
                    Line = at.Line, Col = at.Col,
                };

            case Tok.Str:
                _i++;
                return new LiteralExpr { Kind = Lit.Str, Text = at.Text, Line = at.Line, Col = at.Col };

            // `"META"u8` IS ITS BYTES, written out here where the characters
            // are still in hand. C# gives it the type ReadOnlySpan<byte>; an
            // array of byte converts to one, so that is what it becomes.
            case Tok.Utf8Str:
            {
                _i++;

                // `"text"u8` IS A ReadOnlySpan<byte> OVER DATA, as C# 11 has
                // it: the bytes are laid down once, where the program's data
                // is, and each evaluation allocates nothing (EmitNew, the
                // array marked Utf8Bytes). Read-only by its type, so nothing
                // can write the one copy every use shares.
                byte[] encoded = System.Text.Encoding.UTF8.GetBytes(at.Text);
                NewExpr bytes = new()
                {
                    Type = new TypeRef { Name = "byte", Line = at.Line, Col = at.Col },
                    Elements = new List<Expr>(),
                    Utf8Bytes = encoded,
                    Line = at.Line, Col = at.Col,
                };

                foreach (byte b in encoded)
                {
                    bytes.Elements.Add(new LiteralExpr
                    {
                        Kind = Lit.Int, Text = b.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        IntValue = b, Line = at.Line, Col = at.Col,
                    });
                }

                NewExpr span = new()
                {
                    Type = new TypeRef
                    {
                        Name = "ReadOnlySpan",
                        Arguments = { new TypeRef { Name = "byte", Line = at.Line, Col = at.Col } },
                        Line = at.Line, Col = at.Col,
                    },
                    Line = at.Line, Col = at.Col,
                };
                span.Args.Add(bytes);
                span.WritableArgNames.Add(null);
                return span;
            }

            case Tok.InterpStr:
                _i++;
                return Interpolate(at);

            case Tok.Char:
                _i++;
                return new LiteralExpr { Kind = Lit.Char, Text = at.Text, IntValue = at.Text[0], Line = at.Line, Col = at.Col };

            case Tok.KwTrue:
                _i++;
                return new LiteralExpr { Kind = Lit.Bool, Text = "true", IntValue = 1, Line = at.Line, Col = at.Col };

            case Tok.KwFalse:
                _i++;
                return new LiteralExpr { Kind = Lit.Bool, Text = "false", IntValue = 0, Line = at.Line, Col = at.Col };

            case Tok.KwNull:
                _i++;
                return new LiteralExpr { Kind = Lit.Null, Text = "null", Line = at.Line, Col = at.Col };

            case Tok.KwThis:
                _i++;
                return new ThisExpr { Line = at.Line, Col = at.Col };

            case Tok.KwBase:
                _i++;
                return new BaseExpr { Line = at.Line, Col = at.Col };

            case Tok.KwNew:
            {
                _i++;

                // TARGET-TYPED `new()`, where the type is the one already
                // written on the left: `List<TypeRef> Args { get; init; } =
                // new();`. Saying it twice is what this exists to avoid, and
                // the compiler's own source uses it on nearly every collection
                // it declares.
                //
                // An EMPTY NAME means "the type has not been said here". The
                // binder fills it in from the declaration when it checks the
                // initialiser, which is the one place that knows the answer.
                // `new[] { ... }` -- IMPLICITLY TYPED, its element taken from
                // the first element written. `new[] { Op.Halt, Op.Nop }` is
                // how this compiler's own Isa.cs builds every one of its
                // opcode tables.
                // `new (string text, Tok kind)[1]` -- an array of TUPLES, whose
                // type begins with the same bracket a target-typed `new (a, b)`
                // does. Told apart by reading it as a type and seeing what
                // follows: a type here is followed by a size, a list or a
                // constructor's arguments, and nothing else.
                TypeRef? tupled = null;

                if (At(Tok.LParen))
                {
                    int was = _i;

                    try
                    {
                        TypeRef maybe = ParseTypeRef();

                        // A BRACE AFTER IT IS A TYPE ONLY IF THE TYPE IS AN
                        // ARRAY. `new (int, int)[] { … }` is a list of tuples;
                        // `new(sym, block) { ReadOnly = true }` is a
                        // target-typed new with arguments AND an initialiser,
                        // whose arguments read as a tuple type and are not one.
                        if (At(Tok.LBracket) || At(Tok.LParen)
                            || (At(Tok.LBrace) && maybe.ArrayRank > 0))
                        {
                            tupled = maybe;
                        }
                    }
                    catch (CompileError)
                    {
                        // Not a type; it is a target-typed new with arguments.
                    }

                    if (tupled is null)
                    {
                        _i = was;
                    }
                }

                TypeRef type = tupled
                            ?? (At(Tok.LParen) || At(Tok.LBracket)
                              ? new TypeRef { Name = "", Line = at.Line, Col = at.Col }
                              : ParseTypeRef());
                NewExpr n = new() { Type = type, Line = at.Line, Col = at.Col };

                // `new int[] { 1, 2 }` -- ParseTypeRef has already taken the
                // `[]` as part of the type, so the brackets are gone by the
                // time the loop below looks for them and only the list is
                // left. The element is the type with one rank taken off.
                if (type.ArrayRank > 0 && At(Tok.LBrace))
                {
                    NewExpr listed = new()
                    {
                        Type = new TypeRef
                        {
                            Name = type.Name, Arguments = type.Args,
                            Nullable = type.ArrayRank == 1 ? type.ElementNullable : (type.InnerNullable & 1) != 0,
                            ElementNullable = type.ArrayRank > 1 && type.ElementNullable,
                            InnerNullable = type.InnerNullable >> 1,
                            PointerDepth = type.PointerDepth, ArrayRank = type.ArrayRank - 1,
                            Line = type.Line, Col = type.Col,
                        },
                        Line = at.Line, Col = at.Col,
                    };

                    _i++;
                    listed.Elements = new List<Expr>();

                    while (!At(Tok.RBrace))
                    {
                        listed.Elements.Add(ParseExpr());

                        if (!Take(Tok.Comma))
                        {
                            break;
                        }
                    }

                    Expect(Tok.RBrace, "'}' to close the array's elements");
                    return listed;
                }

                if (Take(Tok.LBracket))
                {
                    // `new int[3]` says how many; `new int[] { ... }` and
                    // `new[] { ... }` say which.
                    Expr? size = At(Tok.RBracket) ? null : ParseExpr();

                    Expect(Tok.RBracket, "']' after the array size");

                    // A JAGGED ARRAY: `new string[n][]` is n references to
                    // string arrays, none of which exist yet. Only the FIRST
                    // pair carries a length -- the rest of the shape is the
                    // element's type, not a size, which is why C# writes the
                    // empty brackets after the sized one and refuses a length
                    // in them. ParseTypeRef took none of this, because it stops
                    // at the '[' that starts a length; so the ranks are put
                    // back on the element type here.
                    int jagged = 0;

                    while (At(Tok.LBracket) && _t[_i + 1].Kind == Tok.RBracket)
                    {
                        _i += 2;
                        jagged++;
                    }

                    if (jagged > 0)
                    {
                        // The type's own '?' becomes an inner mark once
                        // brackets are added around it: `new byte[]?[n][]`.
                        type = new TypeRef
                        {
                            Name = type.Name,
                            Arguments = type.Args,
                            Nullable = false,
                            ElementNullable = type.ArrayRank > 0 ? type.ElementNullable : type.Nullable,
                            InnerNullable = type.InnerNullable | (type.ArrayRank > 0 && type.Nullable ? 1 << (type.ArrayRank - 1) : 0),
                            PointerDepth = type.PointerDepth,
                            ArrayRank = type.ArrayRank + jagged,
                            TupleNames = type.TupleNames,
                            Line = type.Line,
                            Col = type.Col,
                        };
                    }

                    NewExpr made = new()
                    {
                        Type = type, ArraySize = size, Line = at.Line, Col = at.Col,
                    };

                    if (At(Tok.LBrace))
                    {
                        _i++;
                        made.Elements = new List<Expr>();

                        while (!At(Tok.RBrace))
                        {
                            made.Elements.Add(ParseExpr());

                            if (!Take(Tok.Comma))
                            {
                                break;
                            }
                        }

                        Expect(Tok.RBrace, "'}' to close the array's elements");
                    }
                    else if (size is null)
                    {
                        throw Error("an array needs a length or a list of elements");
                    }

                    return made;
                }

                if (Take(Tok.LParen))
                {
                    int spanMark = _spans.Count;
                    if (Source is not null) { _spans.Add(-1); _spans.Add(-1); }
                    if (!At(Tok.RParen))
                    {
                        do
                        {
                            if (At(Tok.Ident) && Ahead().Kind == Tok.Colon)
                            {
                                n.WritableArgNames.Add(_t[_i++].Text);
                                _i++;
                            }
                            else
                            {
                                n.WritableArgNames.Add(null);
                            }
                            int argumentAt = ArgumentStart();
                            n.Args.Add(ParseArg());
                            if (Source is not null) { _spans.Add(argumentAt); _spans.Add(End(_i - 1)); }
                        }
                        while (Take(Tok.Comma));
                    }
                    Expect(Tok.RParen, "')' after the constructor arguments");
                    if (Source is not null)
                    {
                        n.Spans = _spans.GetRange(spanMark, _spans.Count - spanMark).ToArray();
                        n.Source = Source;
                        _spans.RemoveRange(spanMark, _spans.Count - spanMark);
                    }
                }

                // AN INITIALISER, with or without a constructor before it:
                // `new T { A = 1 }` and `new T(x) { A = 1 }` are both ordinary,
                // and `new T()` with an empty brace list is legal too.
                if (At(Tok.LBrace))
                {
                    ReadInitBody(n.Body);
                }
                return n;
            }

            // `default(T)`. The bare `default` of newer C# is not taken: it
            // needs the type the expression is being used AS, which this
            // checker does not push down, and guessing would be worse than
            // making the author name it.
            // Checked is a CONTEXT rather than one operator.  Keep the wrapper
            // in the tree so every arithmetic node beneath it can choose the
            // managed-runtime instruction on a CPU which has one, or the
            // bit-exact software overflow test on a CPU which does not.
            case Tok.Ident when at.Text is "unchecked" or "checked" && _t[_i + 1].Kind == Tok.LParen:
            {
                _i++;
                Expect(Tok.LParen, $"'(' after '{at.Text}'");

                Expr inner = ParseExpr();

                Expect(Tok.RParen, "')' after the expression");
                return new UnaryExpr
                {
                    Op = at.Text == "checked" ? UnOp.Checked : UnOp.Unchecked,
                    Operand = inner, Line = at.Line, Col = at.Col,
                };
            }

            // `sizeof(T)`, contextual so nothing using the word as a name
            // stops compiling.
            // `nameof(x)` -- the name, as a string, worked out here because
            // that is where the name still exists. C# takes the LAST part of a
            // dotted name, so `nameof(a.b.C)` is "C".
            // `stackalloc byte[2]` -- in C# a block of memory on the stack,
            // typed as a Span<T> in a safe context.
            //
            // HEAP HERE, and that is a difference in WHERE rather than in what:
            // the span is the same span, the reads and writes are the same, and
            // what it costs is that the block outlives the call and is collected
            // instead of being popped. This machine's stack is not something a
            // program may hand out pieces of.
            case Tok.Ident when at.Text == "stackalloc" && _t[_i + 1].Kind == Tok.Ident:
            {
                _i++;

                TypeRef of = ParseTypeRef();

                Expect(Tok.LBracket, "'[' after the type in a stackalloc");

                Expr many = ParseExpr();

                Expect(Tok.RBracket, "']' after the length");
                return new NewExpr { Type = of, ArraySize = many, Line = at.Line, Col = at.Col };
            }

            case Tok.Ident when at.Text == "nameof" && _t[_i + 1].Kind == Tok.LParen:
            {
                _i += 2;

                string spelt = Expect(Tok.Ident, "a name inside 'nameof'").Text;

                while (Take(Tok.Dot))
                {
                    spelt = Expect(Tok.Ident, "a name after '.'").Text;
                }

                Expect(Tok.RParen, "')' after 'nameof'");
                return new LiteralExpr { Kind = Lit.Str, Text = spelt, Line = at.Line, Col = at.Col };
            }

            case Tok.Ident when at.Text == "sizeof" && _t[_i + 1].Kind == Tok.LParen:
            {
                _i++;
                Expect(Tok.LParen, "'(' after 'sizeof'");

                TypeRef what = ParseTypeRef();

                Expect(Tok.RParen, "')' after the type");
                return new SizeOfExpr { Type = what, Line = at.Line, Col = at.Col };
            }

            // `typeof(T)`, contextual for the same reason as sizeof: the word
            // is not reserved in this grammar and making it so would stop
            // anything already using it as a name from compiling.
            case Tok.Ident when at.Text == "typeof" && _t[_i + 1].Kind == Tok.LParen:
            {
                _i++;
                Expect(Tok.LParen, "'(' after 'typeof'");

                TypeRef named = ParseTypeRef();

                Expect(Tok.RParen, "')' after the type");
                return new TypeOfExpr { Type = named, Line = at.Line, Col = at.Col };
            }

            case Tok.KwDefault when _t[_i + 1].Kind == Tok.LParen:
            {
                _i++;
                Expect(Tok.LParen, "'(' after 'default'");

                TypeRef of = ParseTypeRef();

                Expect(Tok.RParen, "')' after the type");
                return new DefaultExpr { Type = of, Line = at.Line, Col = at.Col };
            }

            // A BARE `default` WITH NOTHING BESIDE IT TO TAKE A TYPE FROM.
            //
            // BareDefault answers the ones written where the type is on the
            // line -- a declaration, a return -- and the rest are arguments:
            // `entries.Add(default)` and `new() { default }`, which is a call
            // to Add too. The type belongs to a parameter of an overload that
            // has not been chosen yet, so the name is left empty here and the
            // checker fills it in once it has, exactly as it does for `out var`
            // and for a target-typed `new()`.
            case Tok.KwDefault:
            {
                _i++;
                return new DefaultExpr
                {
                    Type = new TypeRef { Name = "", Line = at.Line, Col = at.Col },
                    Line = at.Line, Col = at.Col,
                };
            }

            case Tok.LParen:
            {
                _i++;

                // A NAMED ELEMENT, which only a tuple has: `(text: "x", kind: k)`.
                // Told from a group by the colon, which nothing else in an
                // expression can have here.
                if (At(Tok.Ident) && Ahead().Kind == Tok.Colon)
                {
                    return FinishTuple(at, null, null);
                }

                Expr inner = ParseExpr();

                // ONE ELEMENT IS A GROUP AND TWO ARE A TUPLE, which is the
                // whole of the rule and is why `(x)` still means x.
                if (At(Tok.Comma))
                {
                    return FinishTuple(at, inner, "");
                }

                Expect(Tok.RParen, "')' to close the group");
                return inner;
            }

            case Tok.Ident:
            {
                _i++;
                NameExpr name = new() { Name = at.Text, Global = at.Global, Line = at.Line, Col = at.Col };

                if (At(Tok.Lt))
                {
                    int save = _i;

                    if (TryParseTypeArgs(out List<TypeRef> args))
                    {
                        name.WritableTypeArgs.AddRange(args);
                    }
                    else
                    {
                        _i = save;
                    }
                }
                return name;
            }

            default:
                throw Error($"expected an expression, found '{Cur.Text}'");
        }
    }

    private long ParseIntText(Token t)
    {
        string s = IntegerDigits(t.Text).Replace("_", "");

        try
        {
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                return (long)Convert.ToUInt64(s[2..], 16);
            }
            if (s.StartsWith("0b", StringComparison.OrdinalIgnoreCase))
            {
                return (long)Convert.ToUInt64(s[2..], 2);
            }
            // THE RANGE IS ULONG'S, not long's: `18446744073709551615UL` is a
            // legal C# literal, and so is the same number without its suffix
            // -- a decimal literal that fits no signed type is a ulong. The
            // value is carried as the same 64 bits either way; the binder
            // reads the suffix and the size to decide what the bits mean.
            return unchecked((long)ulong.Parse(s));
        }
        catch (Exception e) when (e is OverflowException or FormatException)
        {
            throw new CompileError(_file, t.Line, t.Col, $"'{t.Text}' does not fit in a 64-bit integer");
        }
    }

    private static string IntegerDigits(string text)
    {
        int end = text.Length;

        while (end > 0 && text[end - 1] is 'L' or 'l' or 'U' or 'u')
        {
            end--;
        }
        return text[..end];
    }

    private static string RealDigits(string text)
        => text.Length > 0 && text[^1] is 'F' or 'f' or 'D' or 'd' or 'M' or 'm'
         ? text[..^1] : text;
}

internal static class MethodDeclExtensions
{
    /// <summary>Copies the list-valued parts a record-style rebuild would drop.</summary>
    public static MethodDecl CopyListsFrom(this MethodDecl to, MethodDecl from)
    {
        if (from.TypeParams.Count > 0) to.WritableTypeParams.AddRange(from.TypeParams);
        to.WritableParams.AddRange(from.Params);
        if (from.Attributes.Count > 0) to.WritableAttributes.AddRange(from.Attributes);
        return to;
    }
}
