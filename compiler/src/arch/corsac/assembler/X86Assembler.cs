#nullable enable
using System.Linq;
using System.Text;
using Corsac.Lang.Ir;

namespace Corsac.Asm;

/// <summary>
/// The 16-bit x86 real-mode assembler.
///
/// It exists because the CORSAC assembler cannot be retargeted: that one lays
/// out fixed-width 64-bit words and the constant pool that goes with them, and
/// x86 is a byte stream whose instructions are one to a dozen bytes long and
/// whose length depends on values that are not known until the labels are. So
/// this is a second assembler, and what is deliberately shared with the first
/// is everything a person types: the <c>.base</c>/<c>.entry</c>/<c>.equ</c>/
/// <c>.include</c> directives, the label syntax, the expression grammar and the
/// <c>file(line): message</c> error format. Assembly for the two machines
/// should read as though one person wrote it, because one person did.
///
/// TWO PASSES, and the second one is not allowed to change its mind. Pass one
/// lays out addresses and records, for every encoding choice it makes, which
/// way it went; pass two replays those choices rather than making them again.
/// That is what keeps the output stable: a forward jump is sized in pass one
/// when its target is unknown, and if pass two were free to shrink it, every
/// address after it would move and every decision made about them would be
/// wrong. The cost is a near jump where a short one would have done; the
/// benefit is that a boot sector's byte count is a fact rather than a fixpoint.
/// Write <c>jmp short</c> where the two bytes matter.
/// </summary>
public sealed class X86Assembler : ISymbols
{
    /// <summary>The result: a flat binary and the entry address <c>.entry</c> named.</summary>
    public sealed class Result
    {
        public required byte[] Bytes { get; init; }
        public required long Base { get; init; }
        public required long Entry { get; init; }
        public required IReadOnlyDictionary<string, long> Labels { get; init; }

        /// <summary>
        /// The same assembly as a relocatable object, or null when it was
        /// assembled flat. Sections, the labels <c>.global</c> exports, and a
        /// relocation for every reference the linker has to finish.
        /// </summary>
        public ObjectFile? Object { get; init; }
    }

    /// <summary>Assembles <paramref name="source"/>, read from <paramref name="path"/>.</summary>
    public static Result Assemble(string source, string path, string? baseDir = null, int bits = 16, bool asObject = false)
        => new X86Assembler(bits, asObject).Run(source, path, baseDir);

    public static Result AssembleFile(string path, int bits = 16, bool asObject = false)
        => Assemble(File.ReadAllText(path), Path.GetFileName(path),
                    Path.GetDirectoryName(Path.GetFullPath(path)), bits, asObject);

    // ---- state -------------------------------------------------------------

    /// <summary>
    /// One output section. A flat assembly has exactly one and it is called
    /// .text; an object may have as many as the source names, and each keeps
    /// its own bytes, its own relocations and its own notion of where it is up
    /// to, because in an object nothing knows its address yet.
    /// </summary>
    private sealed class Sec
    {
        public required string Name { get; init; }
        public required SectionKind Kind { get; init; }
        public List<byte> Bytes { get; } = new();
        public List<Relocation> Relocs { get; } = new();
        public int Align { get; set; } = 4;

        /// <summary>.bss: bytes that are counted rather than written.</summary>
        public bool Zeroed => Kind == SectionKind.Uninitialised;
        public int Zeros { get; set; }

        public int Size => Zeroed ? Zeros : Bytes.Count;

        public void Clear()
        {
            Bytes.Clear();
            Relocs.Clear();
            Zeros = 0;
        }
    }

    private readonly List<(string File, int Line, string Text)> _lines = new();
    private readonly Dictionary<string, long> _labels = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _labelSection = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _equates = new(StringComparer.Ordinal);
    private readonly HashSet<string> _globals = new(StringComparer.Ordinal);
    private readonly HashSet<string> _externs = new(StringComparer.Ordinal);
    private readonly List<int> _decisions = new();
    private readonly List<Sec> _sections = new();
    private Sec _sec;

    /// <summary>
    /// Assembling to an object rather than to a flat image: addresses are
    /// section-relative, every absolute reference to a label becomes a
    /// relocation, and a name that is not defined here is one the linker
    /// supplies.
    /// </summary>
    private readonly bool _object;

    private int _pass;
    private int _decisionAt;
    private int _bits = 16;
    private readonly int _defaultBits;
    private long _base;
    private bool _baseLocked;
    private long _here;
    private string? _entryLabel;

    private string _file = "<source>";
    private int _line;

    private X86Assembler(int bits, bool asObject)
    {
        _defaultBits = bits;
        _object = asObject;
        _sec = new Sec { Name = ".text", Kind = SectionKind.Code };
        _sections.Add(_sec);
    }

    private List<byte> _out => _sec.Bytes;

    private long Pc => _base + _sec.Size;

    // ---- driving -----------------------------------------------------------

    private Result Run(string source, string path, string? baseDir)
    {
        Expand(source, baseDir ?? Directory.GetCurrentDirectory(), path, depth: 0);

        for (_pass = 1; _pass <= 2; _pass++)
        {
            foreach (Sec sec in _sections)
            {
                sec.Clear();
            }
            _sec = _sections[0];
            _decisionAt = 0;
            _bits = _defaultBits;
            _baseLocked = false;
            if (_pass == 1)
            {
                _decisions.Clear();
            }
            else
            {
                _base = _passOneBase;
            }

            foreach ((string file, int line, string text) in _lines)
            {
                _file = file;
                _line = line;
                Line(text);
            }

            if (_pass == 1)
            {
                _passOneBase = _base;
                _passOneSize = Total();
            }
        }

        // A size that moved between the passes means a decision was replayed
        // out of order, which is an assembler bug rather than a source error;
        // saying so beats handing back a binary that is quietly wrong.
        if (Total() != _passOneSize)
        {
            throw new AsmException(_file, 0,
                $"internal: the image changed size between passes ({_passOneSize} then {_out.Count} bytes)");
        }

        long entry = _base;
        if (_entryLabel is not null)
        {
            if (!_labels.TryGetValue(_entryLabel, out entry))
            {
                throw new AsmException(_file, 0, $".entry names '{_entryLabel}', which is not a label");
            }
        }

        return new Result
        {
            Bytes = _sections[0].Bytes.ToArray(),
            Base = _base,
            Entry = entry,
            Labels = new Dictionary<string, long>(_labels, StringComparer.Ordinal),
            Object = _object ? BuildObject() : null,
        };
    }

    private int Total()
    {
        int n = 0;
        foreach (Sec sec in _sections)
        {
            n += sec.Size;
        }
        return n;
    }

    /// <summary>
    /// The sections, the exported labels and the relocations, as the object
    /// the linker takes. A label named by <c>.global</c> is a global symbol at
    /// its offset in its own section; everything else stays private to the
    /// object, and a name the source declared <c>.extern</c> is left for the
    /// relocations to turn into an undefined symbol.
    /// </summary>
    private ObjectFile BuildObject()
    {
        ObjectFile obj = new();
        Dictionary<string, Section> byName = new(StringComparer.Ordinal);
        foreach (Sec sec in _sections)
        {
            if (sec.Size == 0 && sec.Relocs.Count == 0)
            {
                continue;
            }
            Section s = new(sec.Name, sec.Kind) { Align = sec.Align };
            if (sec.Zeroed)
            {
                s.ZeroBytes = sec.Zeros;
            }
            else
            {
                s.Bytes.AddRange(sec.Bytes);
            }
            s.Relocs.AddRange(sec.Relocs);
            obj.Sections.Add(s);
            byName[sec.Name] = s;
        }

        foreach (string name in _globals)
        {
            if (!_labels.TryGetValue(name, out long at) || !_labelSection.TryGetValue(name, out string? where))
            {
                throw new AsmException(_file, 0, $".global names '{name}', which no label in this file defines");
            }
            if (!byName.TryGetValue(where, out Section? section))
            {
                throw new AsmException(_file, 0, $"'{name}' is in section '{where}', which is empty");
            }
            obj.Symbols.Add(new Symbol { Name = name, Section = section, Offset = at, Global = true });
        }
        return obj;
    }

    private long _passOneBase;
    private int _passOneSize;

    /// <summary>Splices includes in, remembering which file each line came from.</summary>
    private void Expand(string source, string baseDir, string file, int depth)
    {
        if (depth > 16)
        {
            throw new AsmException(file, 0, "includes nested more than 16 deep");
        }

        string[] lines = source.Replace("\r\n", "\n").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string trimmed = StripComment(lines[i]).Trim();
            if (!trimmed.StartsWith(".include", StringComparison.OrdinalIgnoreCase))
            {
                _lines.Add((file, i + 1, lines[i]));
                continue;
            }

            string arg = trimmed[".include".Length..].Trim();
            if (arg.Length < 2 || arg[0] != '"' || arg[^1] != '"')
            {
                throw new AsmException(file, i + 1, ".include takes one quoted path");
            }

            string path = Path.Combine(baseDir, Expr.Unescape(arg[1..^1]));
            if (!File.Exists(path))
            {
                throw new AsmException(file, i + 1, $".include: no such file: {path}");
            }

            Expand(File.ReadAllText(path), Path.GetDirectoryName(Path.GetFullPath(path)) ?? baseDir,
                   Path.GetFileName(path), depth + 1);
        }
    }

    // ---- symbols -----------------------------------------------------------

    bool ISymbols.TryLookup(string name, out long value)
    {
        if (name == "$")
        {
            value = _here;
            // Where a statement is, is where the linker puts it: in an object
            // `$` moves with its section, exactly as a label does, and an
            // expression using it is relocatable for the same reason.
            if (_object)
            {
                _refs.Add("$");
            }
            return true;
        }
        if (name == "$$")
        {
            value = _base;
            return true;
        }
        if (_equates.TryGetValue(name, out value))
        {
            return true;
        }
        // In an object, naming a label is naming something whose address the
        // linker decides, so the name is remembered and the field it lands in
        // becomes a relocation. An .extern name has no value here at all.
        if (_object && _externs.Contains(name) && !_labels.ContainsKey(name))
        {
            _refs.Add(name);
            value = 0;
            // Unresolved on purpose: a name only the linker knows must make
            // pass one choose the widest encoding, exactly as a forward label
            // does, because there is no value to be short about.
            return false;
        }
        bool found = _labels.TryGetValue(name, out value);
        // Noted whether or not it is known yet: a name that is neither an
        // equate nor .extern is a label this file defines somewhere, and in
        // pass one a forward one has no value but is still a reference --
        // which is what lets `later - earlier` be seen as the two-name
        // difference it is, and cancel, instead of as one relocatable label.
        if (_object && (found || _pass == 1) && IsName(name))
        {
            _refs.Add(name);
        }
        return found;
    }

    /// <summary>Names the expression just evaluated referred to, and the ones the next field emitted will be relocated against.</summary>
    private readonly List<string> _refs = new();
    private List<string> _pending = new();

    /// <summary>The next four-byte field is a displacement from its own end, not an address.</summary>
    private bool _pcRelative;

    private long Value(string text, out bool resolved)
    {
        _refs.Clear();
        long v = Expr.Eval(text, this, _file, _line, out resolved);
        _pending = new List<string>(_refs);
        return v;
    }

    private long ValueNow(string text)
    {
        _refs.Clear();
        long v = Expr.EvalNow(text, this, _file, _line);
        _pending = new List<string>(_refs);
        return v;
    }

    /// <summary>Takes the pending reference set away, so that a field emitted later can still carry it.</summary>
    private List<string> Take()
    {
        List<string> p = _pending;
        _pending = new List<string>();
        return p;
    }

    private AsmException Error(string message) => new(_file, _line, message);

    /// <summary>
    /// Records an encoding choice in pass one and replays it in pass two. See
    /// the class comment: this is the whole reason the output is stable.
    /// </summary>
    private int Decide(Func<int> choose)
    {
        if (_pass == 1)
        {
            int v = choose();
            _decisions.Add(v);
            return v;
        }
        if (_decisionAt >= _decisions.Count)
        {
            throw Error("internal: pass two asked for an encoding choice pass one did not make");
        }
        return _decisions[_decisionAt++];
    }

    // ---- lines -------------------------------------------------------------

    private void Line(string raw)
    {
        string text = StripComment(raw).Trim();

        while (text.Length > 0)
        {
            int colon = LabelEnd(text);
            if (colon < 0)
            {
                break;
            }
            DefineLabel(text[..colon]);
            text = text[(colon + 1)..].Trim();
        }

        if (text.Length == 0)
        {
            return;
        }

        _here = Pc;
        Statement(text);
    }

    private void Statement(string text)
    {
        string head = Head(text);
        string rest = text[head.Length..].Trim();
        string lower = head.ToLowerInvariant();

        if (lower == ".times")
        {
            Times(rest);
            return;
        }

        if (lower.StartsWith('.'))
        {
            Directive(lower, rest);
            return;
        }

        Instruction(lower, rest);
    }

    /// <summary>
    /// <c>.times N thing</c>: the statement after the count, N times over. The
    /// count is evaluated where it stands and so may use <c>$</c>, which is what
    /// makes the padding idioms work.
    /// </summary>
    private void Times(string rest)
    {
        int split = StatementStart(rest);
        string countText = rest[..split].Trim();
        string body = rest[split..].Trim();

        if (countText.Length == 0 || body.Length == 0)
        {
            throw Error(".times takes a repeat count and then a statement");
        }

        long count = ValueNow(countText);
        if (count < 0)
        {
            throw Error($".times count is {count}; it cannot be negative");
        }

        for (long i = 0; i < count; i++)
        {
            _here = Pc;
            Statement(body);
        }
    }

    /// <summary>
    /// Finds where the repeated statement begins. The count is an expression
    /// and expressions contain spaces, so the split cannot be at the first one;
    /// what it looks for instead is the earliest word that is a directive or a
    /// mnemonic, which a count can never be.
    /// </summary>
    private static int StatementStart(string text)
    {
        int depth = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c is '(' or '[')
            {
                depth++;
                continue;
            }
            if (c is ')' or ']')
            {
                depth--;
                continue;
            }
            if (depth != 0 || !char.IsWhiteSpace(c) || i == 0)
            {
                continue;
            }

            string candidate = text[(i + 1)..].TrimStart();
            int at = text.Length - candidate.Length;
            string word = Head(candidate).ToLowerInvariant();
            if (word.StartsWith('.') || IsMnemonic(word))
            {
                return at;
            }
        }
        return 0;
    }

    private void DefineLabel(string name)
    {
        name = name.Trim();
        if (name.Length == 0 || !IsName(name))
        {
            throw Error($"'{name}' is not a valid label name");
        }

        _labelSection[name] = _sec.Name;

        if (_pass == 1)
        {
            if (_labels.ContainsKey(name) || _equates.ContainsKey(name))
            {
                throw Error($"'{name}' is already defined");
            }
            _labels[name] = Pc;
            return;
        }

        // Pass two re-defines every label at the address it now has, which for
        // a correct assembly is the same one; a difference would mean the
        // layout moved, and the size check at the end catches that.
        _labels[name] = Pc;
    }

    private static string Head(string text)
    {
        int i = 0;
        while (i < text.Length && !char.IsWhiteSpace(text[i]))
        {
            i++;
        }
        return text[..i];
    }

    private static int LabelEnd(string text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == ':')
            {
                return i;
            }
            if (char.IsWhiteSpace(c) || c is '[' or '\'' or '"' or ',')
            {
                return -1;
            }
        }
        return -1;
    }

    private static bool IsName(string text)
    {
        if (text.Length == 0 || (!char.IsLetter(text[0]) && text[0] != '_' && text[0] != '.'))
        {
            return false;
        }
        foreach (char c in text)
        {
            if (!char.IsLetterOrDigit(c) && c != '_' && c != '.')
            {
                return false;
            }
        }
        return true;
    }

    private static string StripComment(string text)
    {
        bool inString = false;
        bool inChar = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\\' && (inString || inChar))
            {
                i++;
                continue;
            }
            if (c == '"' && !inChar)
            {
                inString = !inString;
                continue;
            }
            if (c == '\'' && !inString)
            {
                inChar = !inChar;
                continue;
            }
            if (c == ';' && !inString && !inChar)
            {
                return text[..i];
            }
        }
        return text;
    }

    /// <summary>Splits an operand list on the commas that separate operands, not the ones inside brackets or strings.</summary>
    private static string[] Split(string text)
    {
        if (text.Trim().Length == 0)
        {
            return Array.Empty<string>();
        }

        List<string> parts = new();
        StringBuilder current = new();
        int depth = 0;
        bool inString = false;
        bool inChar = false;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if ((inString || inChar) && c == '\\' && i + 1 < text.Length)
            {
                current.Append(c).Append(text[i + 1]);
                i++;
                continue;
            }
            if (c == '"' && !inChar)
            {
                inString = !inString;
            }
            else if (c == '\'' && !inString)
            {
                inChar = !inChar;
            }
            else if (!inString && !inChar && c is '[' or '(')
            {
                depth++;
            }
            else if (!inString && !inChar && c is ']' or ')')
            {
                depth--;
            }
            else if (c == ',' && depth == 0 && !inString && !inChar)
            {
                parts.Add(current.ToString().Trim());
                current.Clear();
                continue;
            }
            current.Append(c);
        }

        parts.Add(current.ToString().Trim());
        return parts.Where(p => p.Length > 0).ToArray();
    }

    // ---- directives --------------------------------------------------------

    private void Directive(string name, string rest)
    {
        string[] ops = Split(rest);

        switch (name)
        {
            case ".base":
            case ".org":
                if (ops.Length != 1)
                {
                    throw Error($"{name} takes one address");
                }
                if (_baseLocked)
                {
                    throw Error($"{name} must come before anything is emitted");
                }
                if (_object)
                {
                    throw Error($"{name} sets an address, and in an object the linker does that");
                }
                _base = ValueNow(ops[0]);
                _here = _base;
                return;

            case ".entry":
                if (ops.Length != 1 || !IsName(ops[0]))
                {
                    throw Error(".entry takes one label");
                }
                _entryLabel = ops[0];
                return;

            case ".bits":
                if (ops.Length != 1)
                {
                    throw Error(".bits takes 16 or 32");
                }
                long bits = ValueNow(ops[0]);
                if (bits is not (16 or 32))
                {
                    throw Error($".bits {bits}: this assembler emits 16-bit and 32-bit code only");
                }
                _bits = (int)bits;
                return;

            // GAS's spellings of the same thing, so that a source written for
            // the GNU assembler needs no translation of its mode switches.
            case ".code16":
                if (ops.Length != 0)
                {
                    throw Error(".code16 takes no operand");
                }
                _bits = 16;
                return;

            case ".code32":
                if (ops.Length != 0)
                {
                    throw Error(".code32 takes no operand");
                }
                _bits = 32;
                return;

            case ".equ":
            case ".set":
                if (ops.Length != 2)
                {
                    throw Error($"{name} takes a name and a value");
                }
                if (!IsName(ops[0]))
                {
                    throw Error($"'{ops[0]}' is not a valid name");
                }
                if (_pass == 1 && (_equates.ContainsKey(ops[0]) || _labels.ContainsKey(ops[0])) && name == ".equ")
                {
                    throw Error($"'{ops[0]}' is already defined");
                }
                _equates[ops[0]] = ValueNow(ops[1]);
                return;

            case ".db":  Data(ops, 1); return;
            case ".dw":  Data(ops, 2); return;
            case ".dd":  Data(ops, 4); return;

            case ".ascii": Ascii(ops, false); return;
            case ".asciz":
            case ".asciiz": Ascii(ops, true); return;

            case ".align":
            {
                if (ops.Length is 0 or > 2)
                {
                    throw Error(".align takes an alignment and optionally a fill byte");
                }
                long align = ValueNow(ops[0]);
                if (align <= 0 || (align & (align - 1)) != 0)
                {
                    throw Error($".align {align} needs a power of two");
                }
                byte fill = ops.Length == 2 ? (byte)ValueNow(ops[1]) : (byte)0;
                // Aligning within a section is only half the answer: the
                // section itself has to be at least as aligned, or the linker
                // may put its first byte anywhere.
                _sec.Align = Math.Max(_sec.Align, (int)align);
                while (Pc % align != 0)
                {
                    Emit(fill);
                }
                return;
            }

            case ".fill":
            case ".space":
            {
                if (ops.Length is 0 or > 2)
                {
                    throw Error($"{name} takes a byte count and optionally a fill byte");
                }
                long count = ValueNow(ops[0]);
                if (count < 0)
                {
                    // The idiom this catches is `.fill 510-($-$$), 0` in a
                    // sector that has outgrown 510 bytes, and the count going
                    // negative is exactly the news the author needs.
                    throw Error($"{name} count is {count}; the code before it is {-count} bytes too long");
                }
                byte fill = ops.Length == 2 ? (byte)ValueNow(ops[1]) : (byte)0;
                for (long i = 0; i < count; i++)
                {
                    Emit(fill);
                }
                return;
            }

            case ".code":
            case ".text":    Section(".text", rest); return;
            case ".data":    Section(".data", rest); return;
            case ".rodata":  Section(".rodata", rest); return;
            case ".bss":     Section(".bss", rest); return;
            case ".section": Section(ops.Length == 1 ? ops[0] : "", rest); return;

            case ".global":
            case ".globl":
                // What this object offers the linker. The label itself is
                // ordinary; this only says it may be named from outside.
                Names(ops, name, _globals);
                return;

            case ".extern":
                // What this object expects FROM the linker. A name declared
                // here has no value while assembling; every field that uses
                // one becomes a relocation.
                Names(ops, name, _externs);
                return;

            default:
                throw Error($"unknown directive '{name}'");
        }
    }

    /// <summary>
    /// <c>.section .data</c>, and the shorthands. A flat image has one section
    /// and always did, so outside an object this only checks the name: bytes
    /// go on coming out in the order the source wrote them.
    /// </summary>
    private void Section(string name, string rest)
    {
        if (name.Length == 0)
        {
            throw Error(".section takes one section name");
        }
        SectionKind kind = name switch
        {
            ".text" => SectionKind.Code,
            ".rodata" => SectionKind.ReadOnlyData,
            ".data" => SectionKind.Data,
            ".bss" => SectionKind.Uninitialised,
            _ => throw Error($".section {name}: this assembler knows .text, .rodata, .data and .bss"),
        };
        if (!_object)
        {
            if (kind != SectionKind.Code && kind != SectionKind.Data && kind != SectionKind.ReadOnlyData)
            {
                throw Error($"{name} has no place in a flat image; assemble with --obj");
            }
            return;
        }
        foreach (Sec sec in _sections)
        {
            if (sec.Name == name)
            {
                _sec = sec;
                return;
            }
        }
        Sec made = new() { Name = name, Kind = kind };
        _sections.Add(made);
        _sec = made;
    }

    private void Names(string[] ops, string directive, HashSet<string> into)
    {
        if (ops.Length == 0)
        {
            throw Error($"{directive} takes at least one name");
        }
        foreach (string op in ops)
        {
            if (!IsName(op))
            {
                throw Error($"'{op}' is not a valid name");
            }
            into.Add(op);
        }
    }

    private void Data(string[] ops, int size)
    {
        if (ops.Length == 0)
        {
            throw Error("a data directive needs at least one value");
        }

        foreach (string op in ops)
        {
            string t = op.Trim();
            if (t.Length >= 2 && t[0] == '"' && t[^1] == '"')
            {
                foreach (byte b in Encoding.UTF8.GetBytes(Expr.Unescape(t[1..^1])))
                {
                    for (int i = 0; i < size; i++)
                    {
                        Emit(i == 0 ? b : (byte)0);
                    }
                }
                continue;
            }

            long v = Value(t, out _);
            EmitImm(v, size);
        }
    }

    private void Ascii(string[] ops, bool terminate)
    {
        if (ops.Length != 1)
        {
            throw Error("a string directive takes one quoted string");
        }
        string t = ops[0].Trim();
        if (t.Length < 2 || t[0] != '"' || t[^1] != '"')
        {
            throw Error("a string directive takes one quoted string");
        }
        foreach (byte b in Encoding.UTF8.GetBytes(Expr.Unescape(t[1..^1])))
        {
            Emit(b);
        }
        if (terminate)
        {
            Emit(0);
        }
    }

    // ---- emission ----------------------------------------------------------

    private void Emit(byte b)
    {
        _baseLocked = true;
        if (_sec.Zeroed)
        {
            // .bss holds a size, not bytes. Reserving space in it is fine;
            // putting something there is a source error, because the file
            // this becomes has nowhere to keep it.
            if (b != 0)
            {
                throw Error($"'{_sec.Name}' is uninitialised; it can be reserved with .space but not written to");
            }
            _sec.Zeros++;
            return;
        }
        _out.Add(b);
    }

    private void Emit(params byte[] bytes)
    {
        foreach (byte b in bytes)
        {
            Emit(b);
        }
    }

    private void EmitImm(long value, int size) => EmitImm(value, size, Take());

    /// <summary>
    /// A field, and the relocation it needs if it names something the linker
    /// has to place. Absolute fields against a label in this object are
    /// relocated against that label's SECTION plus the label's offset, which
    /// is what an assembler's local labels always become; a field against a
    /// name this file declared .extern is relocated against that name.
    /// </summary>
    private void EmitImm(long value, int size, List<string> refs)
    {
        bool pcRelative = _pcRelative;
        _pcRelative = false;

        if (_object && refs.Count > 0)
        {
            Relocate(value, size, refs, pcRelative);
        }

        for (int i = 0; i < size; i++)
        {
            Emit((byte)(value >> (i * 8)));
        }
    }

    /// <summary>The section a name is in, or null when only the linker knows.</summary>
    private string? SectionOf(string name)
    {
        if (name == "$")
        {
            return _sec.Name;
        }
        return _labels.ContainsKey(name) ? _labelSection[name] : null;
    }

    private void Relocate(long value, int size, List<string> refs, bool pcRelative)
    {
        // Relocations are decided when bytes are written, in pass two, when
        // every label of this file has a section. Pass one only sizes.
        if (_pass == 1)
        {
            return;
        }

        if (refs.Count > 1)
        {
            // Two or more names in one expression only mean anything if they
            // cancel -- the distance between two places in one section, which
            // is the same number wherever the linker puts that section. So
            // that is what it is taken to be, and anything else is refused.
            string? one = SectionOf(refs[0]);
            foreach (string other in refs)
            {
                if (one is null || SectionOf(other) != one)
                {
                    throw Error($"'{string.Join(" and ", refs)}' are in different sections, or not in this "
                                + "object at all; an expression can only name more than one when they are "
                                + "in the same section and cancel");
                }
            }
            return;
        }
        string name = refs[0];
        bool external = SectionOf(name) is null;
        string section = SectionOf(name) ?? "";

        // A branch to a label in this same section needs nothing: both ends
        // move together, so the displacement the assembler computed is final.
        if (pcRelative && !external && section == _sec.Name)
        {
            return;
        }
        if (size != 4)
        {
            throw Error($"'{name}' is {(external ? "supplied by the linker" : "in section '" + section + "'")}, "
                        + $"so it needs a four-byte field; this one is {size}");
        }

        // REL, so the addend lives in the word. For an absolute field that is
        // the value as computed; for a PC-relative one the assembler has
        // already taken this field's own offset off, and the linker will take
        // its address off again, so the offset goes back on.
        // Truncated to the field: the value here is a placeholder plus an
        // offset and may have wrapped, and what the linker adds is the
        // 32-bit addend the word can actually hold.
        long addend = unchecked((int)(pcRelative ? value + _sec.Size : value));
        _sec.Relocs.Add(new Relocation(_sec.Size, external ? name : section, addend,
                                       pcRelative ? RelocKind.Rel32 : RelocKind.Abs32));
    }

    /// <summary>A value that must be known by the time bytes are written for real.</summary>
    private long Val(string text)
    {
        long v = Value(text, out bool resolved);
        if (!resolved && _pass == 2 && !_pending.Any(r => SectionOf(r) is null))
        {
            throw Error($"'{text.Trim()}' is not a number, an equate, or a known label");
        }
        return v;
    }

    private Operand P(string text) => Operands.Parse(text, _bits, _file, _line);

    private static Operand? MemOf(params Operand[] ops)
    {
        foreach (Operand o in ops)
        {
            if (o.Kind == OperandKind.Memory)
            {
                return o;
            }
        }
        return null;
    }

    /// <summary>
    /// The prefix bytes, in the order the processor expects them: segment
    /// override, then operand size, then address size. A <c>rep</c> goes in
    /// front of all three and is emitted by its own handler.
    /// </summary>
    private void Prefixes(int opSize, Operand? mem)
    {
        if (mem is not null && mem.Segment >= 0)
        {
            Emit(Operands.SegPrefix[mem.Segment]);
        }

        // The 0x66 and 0x67 prefixes do not say "32-bit"; they say "not the
        // default". That is why they are computed against _bits rather than
        // written down per instruction, and why `.bits 32` needs no second
        // encoder: the same table emits 32-bit code with the prefixes inverted.
        if ((opSize == 2 && _bits == 32) || (opSize == 4 && _bits == 16))
        {
            Emit(0x66);
        }

        if (mem is not null && mem.Kind == OperandKind.Memory && mem.AddrSize != _bits)
        {
            Emit(0x67);
        }
    }

    private void EmitRM(int reg, Operand rm)
    {
        if (rm.Kind is OperandKind.Register or OperandKind.Segment or OperandKind.Control)
        {
            Emit((byte)(0xC0 | ((reg & 7) << 3) | (rm.Reg & 7)));
            return;
        }

        if (rm.Kind != OperandKind.Memory)
        {
            throw Error($"'{rm.Text}' cannot be a register-or-memory operand");
        }

        if (rm.AddrSize == 16)
        {
            EmitRM16(reg, rm);
            return;
        }
        EmitRM32(reg, rm);
    }

    private void EmitRM16(int reg, Operand rm)
    {
        const int Bx = 3, Bp = 5, Si = 6, Di = 7;
        int b = rm.Base, ix = rm.Index;
        bool noRegs = b < 0 && ix < 0;

        int rmField;
        if (noRegs)                    { rmField = 6; }
        else if (b == Bx && ix == Si)  { rmField = 0; }
        else if (b == Bx && ix == Di)  { rmField = 1; }
        else if (b == Bp && ix == Si)  { rmField = 2; }
        else if (b == Bp && ix == Di)  { rmField = 3; }
        else if (b < 0 && ix == Si)    { rmField = 4; }
        else if (b < 0 && ix == Di)    { rmField = 5; }
        else if (b == Bp && ix < 0)    { rmField = 6; }
        else if (b == Bx && ix < 0)    { rmField = 7; }
        else
        {
            throw Error($"'{rm.Text}' is not a 16-bit addressing form");
        }

        // [bp] with no displacement has no encoding: mod 00 with r/m 110 is
        // spelled "disp16 and no register", which is the one thing the table
        // spends that slot on. So it becomes [bp+0].
        bool mustHaveDisp = b == Bp && ix < 0;

        int form = Decide(() =>
        {
            if (noRegs)
            {
                return 2;
            }
            if (!rm.HasDisp)
            {
                return mustHaveDisp ? 1 : 0;
            }
            long v = Value(rm.Disp, out bool ok);
            if (!ok)
            {
                return 2;
            }
            if (v == 0 && !mustHaveDisp)
            {
                return 0;
            }
            return v is >= -128 and <= 127 ? 1 : 2;
        });

        int mod = noRegs ? 0 : form;
        Emit((byte)((mod << 6) | ((reg & 7) << 3) | rmField));

        long disp = rm.HasDisp ? Val(rm.Disp) : 0;
        if (noRegs || form == 2)
        {
            // A 16-bit address field cannot hold a 32-bit address, and silently
            // keeping the bottom half of one is how a loader reads the wrong
            // sector. Say so instead.
            CheckImm(disp, 2, $"the displacement of '{rm.Text}'");
            EmitImm(disp, 2);
            return;
        }
        if (form == 1)
        {
            CheckDisp8(disp, rm);
            EmitImm(disp, 1);
        }
    }

    private void EmitRM32(int reg, Operand rm)
    {
        const int Esp = 4, Ebp = 5;
        int b = rm.Base, ix = rm.Index;
        bool needSib = ix >= 0 || b == Esp;
        bool noBase = b < 0;
        bool mustHaveDisp = b == Ebp;

        int form = Decide(() =>
        {
            if (noBase)
            {
                return 2;
            }
            if (!rm.HasDisp)
            {
                return mustHaveDisp ? 1 : 0;
            }
            long v = Value(rm.Disp, out bool ok);
            if (!ok)
            {
                return 2;
            }
            if (v == 0 && !mustHaveDisp)
            {
                return 0;
            }
            return v is >= -128 and <= 127 ? 1 : 2;
        });

        long disp = rm.HasDisp ? Val(rm.Disp) : 0;
        int scaleBits = rm.Scale switch { 1 => 0, 2 => 1, 4 => 2, 8 => 3, _ => 0 };

        if (noBase)
        {
            if (ix < 0)
            {
                Emit((byte)(((reg & 7) << 3) | 5));
                EmitImm(disp, 4);
                return;
            }
            // No base at all still needs a SIB, because the base field has to
            // say 101 with mod 00 to mean "there isn't one, here is a disp32".
            Emit((byte)(((reg & 7) << 3) | 4));
            Emit((byte)((scaleBits << 6) | ((ix & 7) << 3) | 5));
            EmitImm(disp, 4);
            return;
        }

        int rmField = needSib ? 4 : b;
        Emit((byte)((form << 6) | ((reg & 7) << 3) | rmField));
        if (needSib)
        {
            Emit((byte)((scaleBits << 6) | (((ix < 0 ? Esp : ix) & 7) << 3) | (b & 7)));
        }

        if (form == 1)
        {
            CheckDisp8(disp, rm);
            EmitImm(disp, 1);
        }
        else if (form == 2)
        {
            EmitImm(disp, 4);
        }
    }

    private void CheckDisp8(long disp, Operand rm)
    {
        if (_pass == 2 && disp is < -128 or > 127)
        {
            throw Error($"'{rm.Text}' has a displacement of {disp}, which pass one sized as one byte");
        }
    }

    // ---- instructions ------------------------------------------------------

    /// <summary>
    /// Every spelling the Intel manual gives a condition, so that source
    /// written elsewhere assembles here without being reworded.
    /// </summary>
    private static readonly (string Name, int Code)[] Conditions =
    {
        ("o", 0), ("no", 1),
        ("b", 2), ("c", 2), ("nae", 2),
        ("ae", 3), ("nb", 3), ("nc", 3),
        ("e", 4), ("z", 4),
        ("ne", 5), ("nz", 5),
        ("be", 6), ("na", 6),
        ("a", 7), ("nbe", 7),
        ("s", 8), ("ns", 9),
        ("p", 10), ("pe", 10),
        ("np", 11), ("po", 11),
        ("l", 12), ("nge", 12),
        ("ge", 13), ("nl", 13),
        ("le", 14), ("ng", 14),
        ("g", 15), ("nle", 15),
    };

    private static int ConditionCode(string suffix)
    {
        foreach ((string name, int code) in Conditions)
        {
            if (name == suffix)
            {
                return code;
            }
        }
        return -1;
    }

    private static readonly HashSet<string> Mnemonics = new(StringComparer.Ordinal)
    {
        "mov", "push", "pop", "add", "sub", "adc", "sbb", "and", "or", "xor", "cmp", "test",
        "inc", "dec", "shl", "sal", "shr", "sar", "rol", "ror", "rcl", "rcr",
        "mul", "imul", "div", "idiv", "neg", "not", "lea",
        "jmp", "call", "ret", "retn", "retf", "iret", "iretd", "loop", "loope", "loopz",
        "loopne", "loopnz", "jcxz", "jecxz", "int", "int3", "into",
        "cli", "sti", "hlt", "nop", "cld", "std", "clc", "stc", "cmc",
        "in", "out", "lgdt", "lidt", "xchg", "cbw", "cwde", "cwd", "cdq",
        "lodsb", "lodsw", "lodsd", "stosb", "stosw", "stosd",
        "movsb", "movsw", "movsd", "scasb", "scasw", "scasd", "cmpsb", "cmpsw", "cmpsd",
        "insb", "insw", "insd", "outsb", "outsw", "outsd",
        "rep", "repe", "repz", "repne", "repnz", "lock",
        "pusha", "pushaw", "pushad", "popa", "popaw", "popad",
        "pushf", "pushfw", "pushfd", "popf", "popfw", "popfd", "ltr", "lldt", "str", "sldt",
    };

    private static bool IsMnemonic(string word)
        => Mnemonics.Contains(word) || (word.StartsWith('j') && word.Length > 1 && ConditionCode(word[1..]) >= 0);

    private void Instruction(string mn, string rest)
    {
        switch (mn)
        {
            case "rep":
            case "repe":
            case "repz":
            case "repne":
            case "repnz":
            case "lock":
            {
                if (rest.Length == 0)
                {
                    throw Error($"{mn} has to be followed by an instruction");
                }
                Emit(mn switch { "rep" or "repe" or "repz" => (byte)0xF3, "lock" => (byte)0xF0, _ => (byte)0xF2 });
                string inner = Head(rest).ToLowerInvariant();
                Instruction(inner, rest[Head(rest).Length..].Trim());
                return;
            }
        }

        string[] a = Split(rest);

        switch (mn)
        {
            case "nop":  Bare(a, 0x90); return;
            case "cli":  Bare(a, 0xFA); return;
            case "sti":  Bare(a, 0xFB); return;
            case "hlt":  Bare(a, 0xF4); return;
            case "cld":  Bare(a, 0xFC); return;
            case "std":  Bare(a, 0xFD); return;
            case "clc":  Bare(a, 0xF8); return;
            case "stc":  Bare(a, 0xF9); return;
            case "cmc":  Bare(a, 0xF5); return;
            case "int3": Bare(a, 0xCC); return;
            case "into": Bare(a, 0xCE); return;
            case "iret": Bare(a, 0xCF); return;
            // PUSHA and POPA are how an interrupt stub saves and restores the
            // eight general registers in one instruction each.
            // Plain PUSHA is whatever the mode is: sixteen bits of registers
            // in .bits 16 and thirty-two in .bits 32, which is what every
            // other assembler means by it. The w and d spellings insist.
            case "pusha":  Bare(a, 0x60); return;
            case "popa":   Bare(a, 0x61); return;
            case "pushaw": Sized(a, 2, 0x60); return;
            case "pushad": Sized(a, 4, 0x60); return;
            case "popaw":  Sized(a, 2, 0x61); return;
            case "popad":  Sized(a, 4, 0x61); return;
            case "iretd": Sized(a, 4, 0xCF); return;

            // The flags, pushed and popped: the mode's own width bare, as
            // with PUSHA, and a width insisted on with the w and d spellings.
            case "pushf":  Bare(a, 0x9C); return;
            case "popf":   Bare(a, 0x9D); return;
            case "pushfw": Sized(a, 2, 0x9C); return;
            case "pushfd": Sized(a, 4, 0x9C); return;
            case "popfw":  Sized(a, 2, 0x9D); return;
            case "popfd":  Sized(a, 4, 0x9D); return;

            case "cbw":  Sized(a, 2, 0x98); return;
            case "cwde": Sized(a, 4, 0x98); return;
            case "cwd":  Sized(a, 2, 0x99); return;
            case "cdq":  Sized(a, 4, 0x99); return;

            case "lodsb": Bare(a, 0xAC); return;
            case "lodsw": Sized(a, 2, 0xAD); return;
            case "lodsd": Sized(a, 4, 0xAD); return;
            case "stosb": Bare(a, 0xAA); return;
            case "stosw": Sized(a, 2, 0xAB); return;
            case "stosd": Sized(a, 4, 0xAB); return;
            case "movsb": Bare(a, 0xA4); return;
            case "movsw": Sized(a, 2, 0xA5); return;
            case "movsd": Sized(a, 4, 0xA5); return;
            case "scasb": Bare(a, 0xAE); return;
            case "scasw": Sized(a, 2, 0xAF); return;
            case "scasd": Sized(a, 4, 0xAF); return;
            case "cmpsb": Bare(a, 0xA6); return;
            case "cmpsw": Sized(a, 2, 0xA7); return;
            case "cmpsd": Sized(a, 4, 0xA7); return;
            // Port strings: DX to ES:DI, DS:SI to DX. What a boot sector
            // reads a disc with once it stops asking the BIOS to.
            case "insb":  Bare(a, 0x6C); return;
            case "insw":  Sized(a, 2, 0x6D); return;
            case "insd":  Sized(a, 4, 0x6D); return;
            case "outsb": Bare(a, 0x6E); return;
            case "outsw": Sized(a, 2, 0x6F); return;
            case "outsd": Sized(a, 4, 0x6F); return;

            case "mov":  Mov(a); return;
            case "lea":  Lea(a); return;
            case "push": Push(a); return;
            case "pop":  Pop(a); return;
            case "xchg": Xchg(a); return;

            case "add": Alu(0, mn, a); return;
            case "or":  Alu(1, mn, a); return;
            case "adc": Alu(2, mn, a); return;
            case "sbb": Alu(3, mn, a); return;
            case "and": Alu(4, mn, a); return;
            case "sub": Alu(5, mn, a); return;
            case "xor": Alu(6, mn, a); return;
            case "cmp": Alu(7, mn, a); return;
            case "test": Test(a); return;

            case "inc": IncDec(a, 0, mn); return;
            case "dec": IncDec(a, 1, mn); return;

            case "rol": Shift(0, mn, a); return;
            case "ror": Shift(1, mn, a); return;
            case "rcl": Shift(2, mn, a); return;
            case "rcr": Shift(3, mn, a); return;
            case "shl": case "sal": Shift(4, mn, a); return;
            case "shr": Shift(5, mn, a); return;
            case "sar": Shift(7, mn, a); return;

            case "not":  Unary(2, mn, a); return;
            case "neg":  Unary(3, mn, a); return;
            case "mul":  Unary(4, mn, a); return;
            case "imul": Unary(5, mn, a); return;
            case "div":  Unary(6, mn, a); return;
            case "idiv": Unary(7, mn, a); return;

            case "jmp":  Jmp(a, rest); return;
            case "call": Call(a, rest); return;
            case "ret":  case "retn": Ret(a, 0xC3, 0xC2); return;
            case "retf": Ret(a, 0xCB, 0xCA); return;

            case "loop":   Rel8(a, 0xE2, mn); return;
            case "loope":  case "loopz":  Rel8(a, 0xE1, mn); return;
            case "loopne": case "loopnz": Rel8(a, 0xE0, mn); return;
            case "jcxz":   Rel8(a, 0xE3, mn); return;
            case "jecxz":  Rel8(a, 0xE3, mn); return;

            case "int": Int(a); return;
            case "in":  In(a); return;
            case "out": Out(a); return;

            case "lgdt": Descriptor(a, 2, mn); return;
            case "lidt": Descriptor(a, 3, mn); return;

            // LTR takes a 16-bit selector, in a register or in memory; the
            // task register is loaded once, before the first trap from ring 3.
            case "ltr":
            case "lldt":
            case "str":
            case "sldt":
                TaskRegister(a, mn); return;
        }

        if (mn.StartsWith('j'))
        {
            int cc = ConditionCode(mn[1..]);
            if (cc >= 0)
            {
                Jcc(cc, mn, a, rest);
                return;
            }
        }

        throw Error($"unknown instruction '{mn}'");
    }

    private void Bare(string[] a, byte op)
    {
        if (a.Length != 0)
        {
            throw Error("this instruction takes no operands");
        }
        Emit(op);
    }

    /// <summary>An instruction with no operands whose meaning depends on the operand size, such as cbw and stosw.</summary>
    private void Sized(string[] a, int size, byte op)
    {
        if (a.Length != 0)
        {
            throw Error("this instruction takes no operands");
        }
        Prefixes(size, null);
        Emit(op);
    }

    private void Need(string[] a, int count, string mn)
    {
        if (a.Length != count)
        {
            throw Error($"{mn} takes {count} operand{(count == 1 ? "" : "s")}, got {a.Length}");
        }
    }

    private int SizeOf(Operand a, Operand b, string mn)
    {
        if (a.Size != 0 && b.Size != 0 && a.Size != b.Size
            && a.Kind != OperandKind.Immediate && b.Kind != OperandKind.Immediate)
        {
            throw Error($"{mn}: '{a.Text}' is {a.Size * 8} bits and '{b.Text}' is {b.Size * 8} bits");
        }
        if (a.Size != 0)
        {
            return a.Size;
        }
        if (b.Size != 0 && b.Kind != OperandKind.Immediate)
        {
            return b.Size;
        }
        if (b.Size != 0)
        {
            return b.Size;
        }
        throw Error($"{mn}: the operand size is not given; write byte, word or dword in front of the memory reference");
    }

    /// <summary>
    /// Refuses an immediate that does not fit the field it is being put in.
    /// Both signed and unsigned spellings are allowed, because assembly writes
    /// 0xFF and -1 for the same byte and neither is wrong.
    /// </summary>
    private void CheckImm(long v, int size, string what, List<string>? refs = null)
    {
        if (_pass != 2)
        {
            return;
        }
        // A field the linker is going to fill in holds a placeholder, and a
        // placeholder is not a number anybody should be checking the range of.
        if (_object && (refs ?? _pending).Count > 0)
        {
            return;
        }
        (long lo, long hi) = size switch
        {
            1 => (-128L, 255L),
            2 => (-32768L, 65535L),
            _ => (int.MinValue, uint.MaxValue),
        };
        if (v < lo || v > hi)
        {
            throw Error($"{what}: {v} does not fit in {size * 8} bits");
        }
    }

    private void CheckRel8(long rel, string target)
    {
        if (_pass == 2 && rel is < -128 or > 127)
        {
            throw Error($"'{target}' is {rel} bytes away; a short jump reaches -128 to +127");
        }
    }

    private int WordSize => _bits / 8;

    private void Mov(string[] a)
    {
        Need(a, 2, "mov");
        Operand d = P(a[0]);
        Operand s = P(a[1]);

        // The control registers are reached by their own two-byte opcode and
        // are always 32 bits wide, prefix or no prefix; that is how `mov eax,
        // cr0` works in 16-bit code without a 0x66 in front of it.
        if (d.Kind == OperandKind.Control)
        {
            if (s.Kind != OperandKind.Register || s.Size != 4)
            {
                throw Error($"mov {d.Text}, {s.Text}: a control register is loaded from a 32-bit register");
            }
            Emit(0x0F, 0x22);
            Emit((byte)(0xC0 | ((d.Reg & 7) << 3) | (s.Reg & 7)));
            return;
        }

        if (s.Kind == OperandKind.Control)
        {
            if (d.Kind != OperandKind.Register || d.Size != 4)
            {
                throw Error($"mov {d.Text}, {s.Text}: a control register is read into a 32-bit register");
            }
            Emit(0x0F, 0x20);
            Emit((byte)(0xC0 | ((s.Reg & 7) << 3) | (d.Reg & 7)));
            return;
        }

        if (s.Kind == OperandKind.Segment)
        {
            Prefixes(d.Kind == OperandKind.Register ? d.Size : 0, MemOf(d));
            Emit(0x8C);
            EmitRM(s.Reg, d);
            return;
        }

        if (d.Kind == OperandKind.Segment)
        {
            Prefixes(0, MemOf(s));
            Emit(0x8E);
            EmitRM(d.Reg, s);
            return;
        }

        if (s.Kind == OperandKind.Immediate)
        {
            int size = SizeOf(d, s, "mov");
            long v = Val(s.Value);
            // The destination's own displacement is evaluated between here and
            // the field below, so what the immediate names is set aside now.
            List<string> named = Take();
            CheckImm(v, size, $"mov {d.Text}, {s.Text}", named);

            if (d.Kind == OperandKind.Register)
            {
                Prefixes(size, null);
                Emit((byte)((size == 1 ? 0xB0 : 0xB8) + d.Reg));
                EmitImm(v, size, named);
                return;
            }

            Prefixes(size, MemOf(d));
            Emit(size == 1 ? (byte)0xC6 : (byte)0xC7);
            EmitRM(0, d);
            EmitImm(v, size, named);
            return;
        }

        if (s.Kind == OperandKind.Register)
        {
            int size = SizeOf(s, d, "mov");
            Prefixes(size, MemOf(d));
            Emit(size == 1 ? (byte)0x88 : (byte)0x89);
            EmitRM(s.Reg, d);
            return;
        }

        if (d.Kind == OperandKind.Register && s.Kind == OperandKind.Memory)
        {
            int size = d.Size;
            Prefixes(size, s);
            Emit(size == 1 ? (byte)0x8A : (byte)0x8B);
            EmitRM(d.Reg, s);
            return;
        }

        throw Error($"mov {d.Text}, {s.Text} has no encoding; one side has to be a register");
    }

    private void Lea(string[] a)
    {
        Need(a, 2, "lea");
        Operand d = P(a[0]);
        Operand s = P(a[1]);
        if (d.Kind != OperandKind.Register || d.Size == 1)
        {
            throw Error("lea loads a 16-bit or 32-bit register");
        }
        if (s.Kind != OperandKind.Memory)
        {
            throw Error($"lea {d.Text}, {s.Text}: the second operand has to be a memory reference");
        }
        Prefixes(d.Size, s);
        Emit(0x8D);
        EmitRM(d.Reg, s);
    }

    private void Push(string[] a)
    {
        Need(a, 1, "push");
        Operand o = P(a[0]);

        if (o.Kind == OperandKind.Segment)
        {
            switch (o.Reg)
            {
                case 0: Emit(0x06); return;
                case 1: Emit(0x0E); return;
                case 2: Emit(0x16); return;
                case 3: Emit(0x1E); return;
                case 4: Emit(0x0F, 0xA0); return;
                default: Emit(0x0F, 0xA8); return;
            }
        }

        if (o.Kind == OperandKind.Register)
        {
            if (o.Size == 1)
            {
                throw Error($"push {o.Text}: there is no byte push");
            }
            Prefixes(o.Size, null);
            Emit((byte)(0x50 + o.Reg));
            return;
        }

        if (o.Kind == OperandKind.Immediate)
        {
            long v = Val(o.Value);
            int size = o.SizeGiven ? o.Size : WordSize;
            int form = Decide(() =>
            {
                long x = Value(o.Value, out bool ok);
                return !o.SizeGiven && ok && x is >= -128 and <= 127 ? 1 : 0;
            });

            if (form == 1)
            {
                Emit(0x6A);
                EmitImm(v, 1);
                return;
            }
            Prefixes(size, null);
            Emit(0x68);
            EmitImm(v, size);
            return;
        }

        int msize = o.Size != 0 ? o.Size : WordSize;
        Prefixes(msize, o);
        Emit(0xFF);
        EmitRM(6, o);
    }

    private void Pop(string[] a)
    {
        Need(a, 1, "pop");
        Operand o = P(a[0]);

        if (o.Kind == OperandKind.Segment)
        {
            switch (o.Reg)
            {
                case 0: Emit(0x07); return;
                // The processor will not let cs be popped, because that would
                // change what is executing halfway through an instruction.
                case 1: throw Error("cs cannot be popped");
                case 2: Emit(0x17); return;
                case 3: Emit(0x1F); return;
                case 4: Emit(0x0F, 0xA1); return;
                default: Emit(0x0F, 0xA9); return;
            }
        }

        if (o.Kind == OperandKind.Register)
        {
            if (o.Size == 1)
            {
                throw Error($"pop {o.Text}: there is no byte pop");
            }
            Prefixes(o.Size, null);
            Emit((byte)(0x58 + o.Reg));
            return;
        }

        if (o.Kind == OperandKind.Immediate)
        {
            throw Error("pop needs somewhere to put the value");
        }

        int msize = o.Size != 0 ? o.Size : WordSize;
        Prefixes(msize, o);
        Emit(0x8F);
        EmitRM(0, o);
    }

    private void Xchg(string[] a)
    {
        Need(a, 2, "xchg");
        Operand x = P(a[0]);
        Operand y = P(a[1]);

        if (x.Kind == OperandKind.Register && y.Kind == OperandKind.Register && x.Size > 1 && x.Size == y.Size
            && (x.Reg == 0 || y.Reg == 0))
        {
            Prefixes(x.Size, null);
            Emit((byte)(0x90 + (x.Reg == 0 ? y.Reg : x.Reg)));
            return;
        }

        if (y.Kind == OperandKind.Register)
        {
            int size = SizeOf(y, x, "xchg");
            Prefixes(size, MemOf(x));
            Emit(size == 1 ? (byte)0x86 : (byte)0x87);
            EmitRM(y.Reg, x);
            return;
        }

        if (x.Kind == OperandKind.Register)
        {
            int size = SizeOf(x, y, "xchg");
            Prefixes(size, MemOf(y));
            Emit(size == 1 ? (byte)0x86 : (byte)0x87);
            EmitRM(x.Reg, y);
            return;
        }

        throw Error("xchg needs at least one register");
    }

    private void Alu(int op, string mn, string[] a)
    {
        Need(a, 2, mn);
        Operand d = P(a[0]);
        Operand s = P(a[1]);
        byte b = (byte)(op * 8);

        if (d.Kind is OperandKind.Segment or OperandKind.Control
            || s.Kind is OperandKind.Segment or OperandKind.Control)
        {
            throw Error($"{mn} does not work on segment or control registers");
        }

        if (s.Kind == OperandKind.Immediate)
        {
            int size = SizeOf(d, s, mn);
            long v = Val(s.Value);

            // 0x83 carries a one-byte immediate the processor sign-extends, so
            // `and eax, -16` is three bytes rather than six. Worth having: the
            // small constants are the common ones.
            int form = Decide(() =>
            {
                long x = Value(s.Value, out bool ok);
                if (size > 1 && ok && x is >= -128 and <= 127)
                {
                    return 2;
                }
                return d.Kind == OperandKind.Register && d.Reg == 0 ? 1 : 0;
            });

            // As in Mov: the immediate's symbol is set aside before the
            // destination's displacement is evaluated over the top of it.
            List<string> named = Take();

            if (form == 2)
            {
                Prefixes(size, MemOf(d));
                Emit(0x83);
                EmitRM(op, d);
                EmitImm(v, 1, named);
                return;
            }

            CheckImm(v, size, $"{mn} {d.Text}, {s.Text}", named);

            if (form == 1)
            {
                Prefixes(size, null);
                Emit((byte)(b + (size == 1 ? 4 : 5)));
                EmitImm(v, size, named);
                return;
            }

            Prefixes(size, MemOf(d));
            Emit(size == 1 ? (byte)0x80 : (byte)0x81);
            EmitRM(op, d);
            EmitImm(v, size, named);
            return;
        }

        if (s.Kind == OperandKind.Register)
        {
            int size = SizeOf(s, d, mn);
            Prefixes(size, MemOf(d));
            Emit((byte)(b + (size == 1 ? 0 : 1)));
            EmitRM(s.Reg, d);
            return;
        }

        if (d.Kind == OperandKind.Register && s.Kind == OperandKind.Memory)
        {
            int size = d.Size;
            Prefixes(size, s);
            Emit((byte)(b + (size == 1 ? 2 : 3)));
            EmitRM(d.Reg, s);
            return;
        }

        throw Error($"{mn} {d.Text}, {s.Text} has no encoding; one side has to be a register");
    }

    private void Test(string[] a)
    {
        Need(a, 2, "test");
        Operand d = P(a[0]);
        Operand s = P(a[1]);

        if (s.Kind == OperandKind.Immediate)
        {
            int size = SizeOf(d, s, "test");
            long v = Val(s.Value);
            CheckImm(v, size, $"test {d.Text}, {s.Text}");

            if (d.Kind == OperandKind.Register && d.Reg == 0)
            {
                Prefixes(size, null);
                Emit(size == 1 ? (byte)0xA8 : (byte)0xA9);
                EmitImm(v, size);
                return;
            }

            Prefixes(size, MemOf(d));
            Emit(size == 1 ? (byte)0xF6 : (byte)0xF7);
            EmitRM(0, d);
            EmitImm(v, size);
            return;
        }

        // test has no direction bit: the register is always the reg field, so
        // `test [bx], ax` and `test ax, [bx]` are the same instruction.
        Operand reg = s.Kind == OperandKind.Register ? s : d;
        Operand rm = s.Kind == OperandKind.Register ? d : s;
        if (reg.Kind != OperandKind.Register)
        {
            throw Error("test needs at least one register");
        }

        int width = SizeOf(reg, rm, "test");
        Prefixes(width, MemOf(rm));
        Emit(width == 1 ? (byte)0x84 : (byte)0x85);
        EmitRM(reg.Reg, rm);
    }

    private void IncDec(string[] a, int which, string mn)
    {
        Need(a, 1, mn);
        Operand d = P(a[0]);

        if (d.Kind == OperandKind.Register && d.Size > 1)
        {
            Prefixes(d.Size, null);
            Emit((byte)((which == 0 ? 0x40 : 0x48) + d.Reg));
            return;
        }

        if (d.Size == 0)
        {
            throw Error($"{mn} {d.Text}: the operand size is not given; write byte, word or dword");
        }

        Prefixes(d.Size, MemOf(d));
        Emit(d.Size == 1 ? (byte)0xFE : (byte)0xFF);
        EmitRM(which, d);
    }

    private void Shift(int op, string mn, string[] a)
    {
        Need(a, 2, mn);
        Operand d = P(a[0]);
        Operand c = P(a[1]);

        if (d.Size == 0)
        {
            throw Error($"{mn} {d.Text}: the operand size is not given; write byte, word or dword");
        }

        if (c.Kind == OperandKind.Register)
        {
            if (c.Size != 1 || c.Reg != 1)
            {
                throw Error($"{mn} takes a count of 1, an immediate, or cl");
            }
            Prefixes(d.Size, MemOf(d));
            Emit(d.Size == 1 ? (byte)0xD2 : (byte)0xD3);
            EmitRM(op, d);
            return;
        }

        if (c.Kind != OperandKind.Immediate)
        {
            throw Error($"{mn} takes a count of 1, an immediate, or cl");
        }

        long n = Val(c.Value);
        int form = Decide(() =>
        {
            long x = Value(c.Value, out bool ok);
            return ok && x == 1 ? 1 : 0;
        });

        Prefixes(d.Size, MemOf(d));
        if (form == 1)
        {
            Emit(d.Size == 1 ? (byte)0xD0 : (byte)0xD1);
            EmitRM(op, d);
            return;
        }
        Emit(d.Size == 1 ? (byte)0xC0 : (byte)0xC1);
        EmitRM(op, d);
        CheckImm(n, 1, $"{mn} {d.Text}, {c.Text}");
        EmitImm(n, 1);
    }

    private void Unary(int op, string mn, string[] a)
    {
        Need(a, 1, mn);
        Operand d = P(a[0]);
        if (d.Size == 0)
        {
            throw Error($"{mn} {d.Text}: the operand size is not given; write byte, word or dword");
        }
        Prefixes(d.Size, MemOf(d));
        Emit(d.Size == 1 ? (byte)0xF6 : (byte)0xF7);
        EmitRM(op, d);
    }

    /// <summary>Strips a short/near/far hint, which says which encoding rather than naming an operand.</summary>
    private static string Hint(string rest, out int hint)
    {
        hint = -1;
        string w = Head(rest).ToLowerInvariant();
        switch (w)
        {
            case "short": hint = 0; return rest[w.Length..].Trim();
            case "near":  hint = 1; return rest[w.Length..].Trim();
            case "far":   hint = 2; return rest[w.Length..].Trim();
            default: return rest.Trim();
        }
    }

    private void Jmp(string[] a, string rest)
    {
        string text = Hint(rest, out int hint);
        if (text.Length == 0)
        {
            throw Error("jmp needs a target");
        }
        Operand o = P(text);

        if (o.Kind == OperandKind.FarPointer)
        {
            FarJump(o, 0xEA);
            return;
        }

        if (o.Kind == OperandKind.Immediate)
        {
            long target = Val(o.Value);
            int form = Decide(() =>
            {
                long x = Value(o.Value, out bool ok);
                if (hint == 0)
                {
                    return 0;
                }
                if (hint == 1 || !ok)
                {
                    return 1;
                }
                return x - (Pc + 2) is >= -128 and <= 127 ? 0 : 1;
            });

            if (form == 0)
            {
                Emit(0xEB);
                long rel = target - (Pc + 1);
                CheckRel8(rel, o.Text);
                _pcRelative = true;
                EmitImm(rel, 1);
                return;
            }

            Emit(0xE9);
            _pcRelative = true;
            EmitImm(target - (Pc + WordSize), WordSize);
            return;
        }

        int size = o.Size != 0 ? o.Size : WordSize;
        Prefixes(size, MemOf(o));
        Emit(0xFF);
        EmitRM(hint == 2 ? 5 : 4, o);
    }

    private void FarJump(Operand o, byte op)
    {
        int size = o.SizeGiven ? o.Size : WordSize;
        long selector = Val(o.FarSegment);
        long offset = Val(o.Value);
        Prefixes(size, null);
        Emit(op);
        EmitImm(offset, size);
        CheckImm(selector, 2, "the selector of a far jump");
        EmitImm(selector, 2);
    }

    private void Call(string[] a, string rest)
    {
        string text = Hint(rest, out int hint);
        if (text.Length == 0)
        {
            throw Error("call needs a target");
        }
        Operand o = P(text);

        if (o.Kind == OperandKind.FarPointer)
        {
            FarJump(o, 0x9A);
            return;
        }

        if (o.Kind == OperandKind.Immediate)
        {
            long target = Val(o.Value);
            Emit(0xE8);
            _pcRelative = true;
            EmitImm(target - (Pc + WordSize), WordSize);
            return;
        }

        int size = o.Size != 0 ? o.Size : WordSize;
        Prefixes(size, MemOf(o));
        Emit(0xFF);
        EmitRM(hint == 2 ? 3 : 2, o);
    }

    private void Ret(string[] a, byte plain, byte withCount)
    {
        if (a.Length == 0)
        {
            Emit(plain);
            return;
        }
        if (a.Length != 1)
        {
            throw Error("ret takes at most one operand");
        }
        long v = Val(P(a[0]).Value);
        CheckImm(v, 2, "the byte count of a ret");
        Emit(withCount);
        EmitImm(v, 2);
    }

    private void Rel8(string[] a, byte op, string mn)
    {
        Need(a, 1, mn);
        Operand o = P(a[0]);
        if (o.Kind != OperandKind.Immediate)
        {
            throw Error($"{mn} takes a label");
        }
        long target = Val(o.Value);
        Emit(op);
        long rel = target - (Pc + 1);
        CheckRel8(rel, o.Text);
        _pcRelative = true;
        EmitImm(rel, 1);
    }

    private void Jcc(int cc, string mn, string[] a, string rest)
    {
        string text = Hint(rest, out int hint);
        if (text.Length == 0)
        {
            throw Error($"{mn} needs a target");
        }
        Operand o = P(text);
        if (o.Kind != OperandKind.Immediate)
        {
            throw Error($"{mn} takes a label; there is no indirect conditional jump");
        }

        long target = Val(o.Value);
        int form = Decide(() =>
        {
            long x = Value(o.Value, out bool ok);
            if (hint == 0)
            {
                return 0;
            }
            if (hint == 1 || !ok)
            {
                return 1;
            }
            return x - (Pc + 2) is >= -128 and <= 127 ? 0 : 1;
        });

        if (form == 0)
        {
            Emit((byte)(0x70 + cc));
            long rel = target - (Pc + 1);
            CheckRel8(rel, o.Text);
            _pcRelative = true;
            EmitImm(rel, 1);
            return;
        }

        Emit(0x0F, (byte)(0x80 + cc));
        _pcRelative = true;
        EmitImm(target - (Pc + WordSize), WordSize);
    }

    private void Int(string[] a)
    {
        Need(a, 1, "int");
        Operand o = P(a[0]);
        if (o.Kind != OperandKind.Immediate)
        {
            throw Error("int takes a vector number");
        }
        long v = Val(o.Value);
        CheckImm(v, 1, "an interrupt vector");
        Emit(0xCD);
        EmitImm(v, 1);
    }

    private void In(string[] a)
    {
        Need(a, 2, "in");
        Operand d = P(a[0]);
        Operand p = P(a[1]);
        if (d.Kind != OperandKind.Register || d.Reg != 0)
        {
            throw Error("in reads into al, ax or eax");
        }

        if (p.Kind == OperandKind.Register)
        {
            if (p.Size != 2 || p.Reg != 2)
            {
                throw Error("in takes a port number or dx");
            }
            Prefixes(d.Size, null);
            Emit(d.Size == 1 ? (byte)0xEC : (byte)0xED);
            return;
        }

        long port = Val(p.Value);
        CheckImm(port, 1, "a port number in an immediate in");
        Prefixes(d.Size, null);
        Emit(d.Size == 1 ? (byte)0xE4 : (byte)0xE5);
        EmitImm(port, 1);
    }

    private void Out(string[] a)
    {
        Need(a, 2, "out");
        Operand p = P(a[0]);
        Operand s = P(a[1]);
        if (s.Kind != OperandKind.Register || s.Reg != 0)
        {
            throw Error("out writes from al, ax or eax");
        }

        if (p.Kind == OperandKind.Register)
        {
            if (p.Size != 2 || p.Reg != 2)
            {
                throw Error("out takes a port number or dx");
            }
            Prefixes(s.Size, null);
            Emit(s.Size == 1 ? (byte)0xEE : (byte)0xEF);
            return;
        }

        long port = Val(p.Value);
        CheckImm(port, 1, "a port number in an immediate out");
        Prefixes(s.Size, null);
        Emit(s.Size == 1 ? (byte)0xE6 : (byte)0xE7);
        EmitImm(port, 1);
    }

    /// <summary>LTR/LLDT/STR/SLDT: the 0F 00 group, on a 16-bit selector.</summary>
    private void TaskRegister(string[] a, string mn)
    {
        Need(a, 1, mn);
        Operand o = P(a[0]);
        if (o.Kind == OperandKind.Register && o.Size != 2)
        {
            throw Error($"{mn} {o.Text}: a selector is 16 bits");
        }
        if (o.Kind is not (OperandKind.Register or OperandKind.Memory))
        {
            throw Error($"{mn} takes a 16-bit register or a memory reference");
        }
        int reg = mn switch { "sldt" => 0, "str" => 1, "lldt" => 2, _ => 3 };
        // No 0x66: the operand is always a 16-bit selector, whatever the mode.
        Prefixes(0, MemOf(o));
        Emit(0x0F, 0x00);
        EmitRM(reg, o);
    }

    private void Descriptor(string[] a, int reg, string mn)
    {
        Need(a, 1, mn);
        Operand o = P(a[0]);
        if (o.Kind != OperandKind.Memory)
        {
            throw Error($"{mn} takes a memory reference to a six-byte limit-and-base pair");
        }
        // No operand-size prefix: in 16-bit code this loads a 24-bit base,
        // which is all a real-mode loader has addresses for, and the kernel
        // reloads the table properly once it is in protected mode.
        Prefixes(0, o);
        Emit(0x0F, 0x01);
        EmitRM(reg, o);
    }
}
