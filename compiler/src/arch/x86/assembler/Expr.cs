#nullable enable
using System.Globalization;
using System.Text;

namespace Corsac.Asm;

/// <summary>
/// An assembly error. Formatted as <c>file(line): message</c>, which is what the
/// rest of the toolchain prints, so an editor that can jump to a compiler error
/// can jump to an assembler one without learning a second format.
/// </summary>
public sealed class AsmException : Exception
{
    public string File { get; }
    public int Line { get; }

    public AsmException(string file, int line, string message) : base(message)
    {
        File = file;
        Line = line;
    }

    public override string ToString() => $"{File}({Line}): {Message}";
}

/// <summary>Where an expression's names come from: labels, equates, <c>$</c> and <c>$$</c>.</summary>
internal interface ISymbols
{
    bool TryLookup(string name, out long value);
}

/// <summary>
/// The expression evaluator, kept deliberately the same shape as the CORSAC
/// assembler's so that hand-written assembly reads the same on both targets:
/// C's operators and precedence, hex/binary/decimal/character literals, and
/// names that are labels or equates.
///
/// It is a plain recursive descent over the operand text rather than a token
/// stream, because an operand is short and an assembler that keeps no AST has
/// nothing to get out of step with the source.
/// </summary>
internal static class Expr
{
    /// <summary>
    /// Evaluates <paramref name="text"/>. When a name is not yet known --
    /// which is every forward reference during pass one -- the result is zero
    /// and <paramref name="resolved"/> is false, so the caller can pick the
    /// largest encoding rather than guess a small one it would have to undo.
    /// </summary>
    public static long Eval(string text, ISymbols symbols, string file, int line, out bool resolved)
    {
        Parser p = new(text, symbols, file, line);
        long value = p.Parse();
        resolved = p.Resolved;
        return value;
    }

    /// <summary>Evaluates an expression that must be known now, such as a repeat count.</summary>
    public static long EvalNow(string text, ISymbols symbols, string file, int line)
    {
        long value = Eval(text, symbols, file, line, out bool resolved);
        if (!resolved)
        {
            throw new AsmException(file, line, $"'{text.Trim()}' cannot be resolved here; it names something defined further down");
        }
        return value;
    }

    private sealed class Parser
    {
        private readonly string _text;
        private readonly ISymbols _symbols;
        private readonly string _file;
        private readonly int _line;
        private int _at;

        public bool Resolved = true;

        public Parser(string text, ISymbols symbols, string file, int line)
        {
            _text = text;
            _symbols = symbols;
            _file = file;
            _line = line;
        }

        public long Parse()
        {
            long v = Or();
            Space();
            if (_at < _text.Length)
            {
                throw Error($"unexpected '{_text[_at..].Trim()}' in expression '{_text.Trim()}'");
            }
            return v;
        }

        private AsmException Error(string message) => new(_file, _line, message);

        private void Space()
        {
            while (_at < _text.Length && (_text[_at] == ' ' || _text[_at] == '\t'))
            {
                _at++;
            }
        }

        private bool Take(string op)
        {
            Space();
            if (_at + op.Length > _text.Length)
            {
                return false;
            }
            if (!_text.AsSpan(_at, op.Length).SequenceEqual(op))
            {
                return false;
            }
            // A single '<' must not swallow the first half of '<<'. Every
            // caller tries the doubled form first, so this only has to stop
            // the one-character attempt that follows it.
            if (op.Length == 1 && (op[0] is '<' or '>') && _at + 1 < _text.Length && _text[_at + 1] == op[0])
            {
                return false;
            }
            _at += op.Length;
            return true;
        }

        private long Or()
        {
            long v = Xor();
            while (true)
            {
                if (Take("|"))
                {
                    v |= Xor();
                    continue;
                }
                return v;
            }
        }

        private long Xor()
        {
            long v = And();
            while (Take("^"))
            {
                v ^= And();
            }
            return v;
        }

        private long And()
        {
            long v = Shift();
            while (Take("&"))
            {
                v &= Shift();
            }
            return v;
        }

        private long Shift()
        {
            long v = Sum();
            while (true)
            {
                if (Take("<<"))
                {
                    v <<= (int)(Sum() & 63);
                    continue;
                }
                if (Take(">>"))
                {
                    v >>= (int)(Sum() & 63);
                    continue;
                }
                return v;
            }
        }

        private long Sum()
        {
            long v = Product();
            while (true)
            {
                if (Take("+"))
                {
                    v += Product();
                    continue;
                }
                if (Take("-"))
                {
                    v -= Product();
                    continue;
                }
                return v;
            }
        }

        private long Product()
        {
            long v = Unary();
            while (true)
            {
                if (Take("*"))
                {
                    v *= Unary();
                    continue;
                }
                if (Take("/"))
                {
                    long d = Unary();
                    // A divide by zero here is almost always a layout constant
                    // that has not been filled in yet; saying so beats a crash.
                    if (d == 0)
                    {
                        throw Error("division by zero in expression");
                    }
                    v /= d;
                    continue;
                }
                if (Take("%"))
                {
                    long d = Unary();
                    if (d == 0)
                    {
                        throw Error("division by zero in expression");
                    }
                    v %= d;
                    continue;
                }
                return v;
            }
        }

        private long Unary()
        {
            Space();
            if (Take("-"))
            {
                return -Unary();
            }
            if (Take("+"))
            {
                return Unary();
            }
            if (Take("~"))
            {
                return ~Unary();
            }
            return Primary();
        }

        private long Primary()
        {
            Space();
            if (_at >= _text.Length)
            {
                throw Error($"expression '{_text.Trim()}' ends early");
            }

            if (_text[_at] == '(')
            {
                _at++;
                long v = Or();
                Space();
                if (_at >= _text.Length || _text[_at] != ')')
                {
                    throw Error($"unbalanced parentheses in '{_text.Trim()}'");
                }
                _at++;
                return v;
            }

            if (_text[_at] == '\'')
            {
                return Character();
            }

            if (char.IsDigit(_text[_at]))
            {
                return Number();
            }

            // '$' is the address of the instruction being assembled and '$$'
            // the address the section started at, so that `.fill 510-($-$$), 0`
            // says what it means without the author counting bytes.
            if (_text[_at] == '$')
            {
                string name = _at + 1 < _text.Length && _text[_at + 1] == '$' ? "$$" : "$";
                _at += name.Length;
                return Lookup(name);
            }

            if (IsNameStart(_text[_at]))
            {
                int start = _at;
                while (_at < _text.Length && IsNamePart(_text[_at]))
                {
                    _at++;
                }
                return Lookup(_text[start.._at]);
            }

            throw Error($"'{_text[_at..].Trim()}' is not a number, a name, or an operator");
        }

        private long Lookup(string name)
        {
            if (_symbols.TryLookup(name, out long value))
            {
                return value;
            }
            // Not an error: pass one meets every forward label this way. The
            // caller widens the encoding and pass two resolves it for real.
            Resolved = false;
            return 0;
        }

        private long Character()
        {
            _at++;
            StringBuilder sb = new();
            while (_at < _text.Length && _text[_at] != '\'')
            {
                if (_text[_at] == '\\' && _at + 1 < _text.Length)
                {
                    _at++;
                    sb.Append(Escape(_text[_at]));
                    _at++;
                    continue;
                }
                sb.Append(_text[_at]);
                _at++;
            }
            if (_at >= _text.Length)
            {
                throw Error("unterminated character literal");
            }
            _at++;

            if (sb.Length is < 1 or > 4)
            {
                throw Error("a character literal holds one to four characters");
            }

            long value = 0;
            for (int i = sb.Length - 1; i >= 0; i--)
            {
                value = (value << 8) | (byte)sb[i];
            }
            return value;
        }

        public static char Escape(char c) => c switch
        {
            'n' => '\n',
            't' => '\t',
            'r' => '\r',
            '0' => '\0',
            'b' => '\b',
            'f' => '\f',
            '\\' => '\\',
            '"' => '"',
            '\'' => '\'',
            _ => c,
        };

        private long Number()
        {
            int start = _at;
            if (_text[_at] == '0' && _at + 1 < _text.Length && (_text[_at + 1] is 'x' or 'X'))
            {
                _at += 2;
                int digits = _at;
                while (_at < _text.Length && (Uri.IsHexDigit(_text[_at]) || _text[_at] == '_'))
                {
                    _at++;
                }
                if (_at == digits)
                {
                    throw Error("a hexadecimal literal needs at least one digit");
                }
                return (long)ulong.Parse(_text[digits.._at].Replace("_", ""), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            }

            if (_text[_at] == '0' && _at + 1 < _text.Length && (_text[_at + 1] is 'b' or 'B'))
            {
                _at += 2;
                long v = 0;
                int digits = _at;
                while (_at < _text.Length && (_text[_at] is '0' or '1' or '_'))
                {
                    if (_text[_at] != '_')
                    {
                        v = (v << 1) | (long)(_text[_at] - '0');
                    }
                    _at++;
                }
                if (_at == digits)
                {
                    throw Error("a binary literal needs at least one digit");
                }
                return v;
            }

            while (_at < _text.Length && (char.IsDigit(_text[_at]) || _text[_at] == '_'))
            {
                _at++;
            }
            return long.Parse(_text[start.._at].Replace("_", ""), CultureInfo.InvariantCulture);
        }

        private static bool IsNameStart(char c) => char.IsLetter(c) || c == '_' || c == '.' || c == '?' || c == '@';

        private static bool IsNamePart(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '.' || c == '?' || c == '@';
    }

    /// <summary>Expands the backslash escapes a quoted string may carry.</summary>
    public static string Unescape(string text)
    {
        StringBuilder sb = new(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] != '\\' || i + 1 >= text.Length)
            {
                sb.Append(text[i]);
                continue;
            }
            i++;
            sb.Append(Parser.Escape(text[i]));
        }
        return sb.ToString();
    }
}
