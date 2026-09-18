#nullable enable
using System.Globalization;
using System.Text;

namespace Corsac;

public sealed class AsmException : Exception
{
    public int Line { get; }

    public AsmException(int line, string message) : base(message)
    {
        Line = line;
    }

    public override string ToString() => Line > 0 ? $"line {Line}: {Message}" : Message;
}

/// <summary>A linked program: an immutable code image, its constant pool, and its initial data.</summary>
public sealed class Image
{
    public required ulong[] Code       { get; init; }
    public required byte[]  Data       { get; init; }
    /// <summary>Source line per code word, for diagnostics that point at the right place.</summary>
    public required int[]   LineOf     { get; init; }
    public required int     Entry      { get; init; }
    /// <summary>Where this image's data begins. Firmware is assembled to the chip it lives on.</summary>
    public long Base { get; init; } = Isa.DataBase;
    // Addresses, and they are LONG: firmware lives at the top of a 48-bit
    // space, which an int cannot hold. Truncating one produced a jump thirty-five
    // trillion words out of range.
    public required IReadOnlyDictionary<string, long> CodeLabels { get; init; }
    public required IReadOnlyDictionary<string, long> DataLabels { get; init; }

    /// <summary>
    /// Every absolute address baked into this image, so it can be loaded
    /// somewhere other than <see cref="Base"/>.
    ///
    /// Without these an image runs at one address and nowhere else, which means
    /// two copies of one program cannot run at once: there is one set of
    /// statics and one set of literals in one place, and the second copy writes
    /// over the first one's variables. On a machine with no MMU that is the
    /// difference between having processes and running one program at a time.
    ///
    /// Empty for hand-written assembly, which names its own addresses and is
    /// built for the chip it lives on.
    /// </summary>
    public IReadOnlyList<int> CodeRelocs { get; init; } = Array.Empty<int>();

    // ---- separate compilation ----------------------------------------------
    //
    // What a library offers and what a program is missing. Empty for anything
    // built the old way, which is every hand-written assembly file and every
    // program compiled whole.

    /// <summary>
    /// The symbols this image publishes: a name, and the code word it begins
    /// at. What makes an image a LIBRARY rather than a program.
    /// </summary>
    public IReadOnlyList<(string Name, int Word)> Exports { get; init; } = Array.Empty<(string, int)>();

    /// <summary>
    /// The symbols this image CALLS and does not contain: a name, and the code
    /// word whose displacement has to be pointed at it once whoever provides it
    /// has an address.
    /// </summary>
    public IReadOnlyList<(string Name, int Word)> Imports { get; init; } = Array.Empty<(string, int)>();

    /// <summary>
    /// The interface, as source, carried inside the library that implements it.
    ///
    /// A header in a separate file is a header that drifts from the code, and
    /// the drift is silent until something calls a method whose signature moved.
    /// Keeping it in the image means asking a library what it offers is reading
    /// the library, and there is exactly one copy of the answer.
    /// </summary>
    public string Header { get; init; } = "";

    /// <summary>
    /// The generic templates this library offers, as a TREE rather than as
    /// source. See Lang/Gir.cs and docs/image-format.md.
    ///
    /// A declaration with its body stripped -- which is what a header is --
    /// cannot be instantiated, so a generic needs more than a signature to
    /// cross a library boundary. It needs the body, and it needs the layout and
    /// dispatch decisions the library made when it compiled the canonical copy,
    /// or a consumer building its own specialisation would lay it out
    /// differently from the shared code that reads it.
    ///
    /// Empty for a program and for a library with no generics in it.
    /// </summary>
    public byte[] Gir { get; init; } = Array.Empty<byte>();

    /// <summary>
    /// The types in this image: what they are called, how big they are, and
    /// where each one's descriptor sits.
    ///
    /// Reflection works on an object without this, because the descriptor is in
    /// front of the vtable the object already points at. What this adds is
    /// ENUMERATION -- asking what types exist rather than what type this is --
    /// and it is where tiers 2 and 3 will hang their field and method rows.
    ///
    /// See Lang/Meta.cs and docs/image-format.md.
    /// </summary>
    public byte[] Meta { get; init; } = Array.Empty<byte>();

    /// <summary>
    /// Where this library's statics sit in the block every process gets, and
    /// how many bytes of them there are.
    ///
    /// Assigned when the library is built, not when a program links it: the
    /// code is SHARED, so the offset baked into it has to mean the same thing
    /// in every process that runs it.
    /// </summary>
    public int LibSlot { get; init; }
    public int LibData { get; init; }

    /// <summary>Whether this image is a library rather than something to run.</summary>
    public bool IsLibrary { get; init; }

    /// <summary>
    /// How many words of ancestor mask sit in front of every vtable in this
    /// image, and therefore how far back the rest of a type descriptor is.
    ///
    /// PART OF THE ABI BETWEEN AN IMAGE AND ANYTHING THAT LINKS IT. The mask is
    /// one bit per type and a compilation with more types needs more words --
    /// so a program with 74 types laid its descriptors out 40 bytes deep while
    /// the library it linked, with 31, reads them 32 bytes deep. Nothing fails
    /// to build. The first virtual call through the library's code jumps into
    /// nowhere, which is what happened to Exception.ToString on 2026-08-13.
    ///
    /// So a library states it, and a program that links one adopts it. Zero
    /// means an image from before this was recorded, which is one word.
    /// </summary>

    /// <summary>Byte offsets into the data segment of words holding an address.</summary>
    public IReadOnlyList<int> DataRelocs { get; init; } = Array.Empty<int>();
}

/// <summary>
/// The CORSAC assembler. Two passes: the first lays out addresses and collects
/// labels, the second encodes. Pseudo-instructions have a size fixed in pass
/// one so that layout never depends on a value that is not yet known.
///
/// Size suffixes attach to the mnemonic: <c>add.w</c>, <c>ld.b</c>. The default
/// is 64-bit for everything that takes one.
/// </summary>
public sealed class Assembler
{
    /// <summary>
    /// Emits coarse pass progress for long self-hosted assemblies. Disabled by
    /// default so the hosted compiler and library callers remain quiet.
    /// </summary>
    public static bool ReportProgress;

    private static void Progress(string message)
    {
        if (!ReportProgress)
        {
            return;
        }
        Console.Error.WriteLine("assembler: " + message);
    }

    private enum Section : byte { Code, Data }

    private sealed class Item
    {
        public required int      Line     { get; init; }
        public required Section  Section  { get; init; }
        public required int      Address  { get; init; }
        public required string[] Operands { get; init; }
        public string?  Mnemonic { get; init; }
        public Size     Size     { get; init; }
        public int      Words    { get; init; }
        public byte[]?  Bytes    { get; init; }
    }

    private readonly Dictionary<string, long> _codeLabels = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _dataLabels = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _equates    = new(StringComparer.Ordinal);
    private readonly List<Item> _items = new();
    private readonly List<long> _constants = new();
    private readonly Dictionary<long, int> _constantIndex = new();

    /// <summary>Where code sits, known once the data size is. Zero during pass one.</summary>
    private long _codeBase;

    /// <summary>Where the constant pool sits: aligned above the data it is part of.</summary>
    private long _poolBase;

    /// <summary>
    /// Where this image is assembled to live.
    ///
    /// Ordinary programs are loaded wherever the loader puts them and take the
    /// default. FIRMWARE does not get that luxury: it is soldered to a
    /// particular chip at a particular address, and every absolute reference in
    /// it has to agree with where that chip answers.
    /// </summary>
    private long _imageBase = Isa.DataBase;

    private Section _section = Section.Code;
    private int _codeWords;
    private int _dataBytes;
    private string? _entryLabel;

    public static Image Assemble(string source, string? baseDir = null)
        => new Assembler().Run(source, baseDir);

    /// <summary>Where an assembled line came from, so an error in an include names the include.</summary>
    private readonly List<(string File, int Line)> _origins = new();

    private Image Run(string source, string? baseDir)
    {
        ArgumentNullException.ThrowIfNull(source);

        List<string> lines = new();
        Expand(source, baseDir ?? Directory.GetCurrentDirectory(), "<source>", lines, depth: 0);
        Progress("expanded " + lines.Count + " source lines");

        for (int i = 0; i < lines.Count; i++)
        {
            try
            {
                ParseLine(lines[i], i + 1);
            }
            catch (AsmException e)
            {
                // Re-point at the file somebody actually edited, which is not
                // the combined stream once includes are spliced in.
                throw new AsmException(e.Line, $"{Where(e.Line)}: {e.Message}");
            }
            if ((i + 1) % 500 == 0 || i + 1 == lines.Count)
            {
                Progress("pass 1 " + (i + 1) + "/" + lines.Count + " lines");
            }
        }
        Progress("pass 2 begins with " + _items.Count + " items");
        return Encode();
    }

    /// <summary>
    /// Splices includes in before parsing, recording where each line came from
    /// so diagnostics keep pointing at the file somebody actually edited.
    /// </summary>
    private void Expand(string source, string baseDir, string file, List<string> into, int depth)
    {
        Progress("expanding " + file + " (" + source.Length + " characters)");
        if (depth > 16)
        {
            throw new AsmException(into.Count + 1, $"includes nested more than 16 deep at '{file}'");
        }

        string[] lines = source.Replace("\r\n", "\n").Split('\n');
        Progress("split " + file + " into " + lines.Length + " lines");

        for (int i = 0; i < lines.Length; i++)
        {
            if (i % 500 == 0)
            {
                Progress("expansion " + (i + 1) + "/" + lines.Length
                    + " lines from " + file);
            }
            string trimmed = StripComment(lines[i]).Trim();

            if (!trimmed.StartsWith(".include", StringComparison.OrdinalIgnoreCase))
            {
                into.Add(lines[i]);
                _origins.Add((file, i + 1));
                continue;
            }

            string[] ops = SplitOperands(trimmed[".include".Length..].Trim());

            if (ops.Length != 1)
            {
                throw new AsmException(into.Count + 1, ".include takes one quoted path");
            }

            string path = Path.Combine(baseDir, ParseQuoted(ops[0], into.Count + 1));

            if (!File.Exists(path))
            {
                throw new AsmException(into.Count + 1, $".include: no such file: {path}");
            }

            Expand(File.ReadAllText(path), Path.GetDirectoryName(path) ?? baseDir,
                   Path.GetFileName(path), into, depth + 1);
        }
    }

    /// <summary>Names the file and line a combined line number came from.</summary>
    private string Where(int line)
    {
        if (line >= 1 && line <= _origins.Count)
        {
            (string file, int at) = _origins[line - 1];
            return $"{file}:{at}";
        }
        return $"line {line}";
    }

    // ---- pass one -------------------------------------------------------

    private void ParseLine(string raw, int line)
    {
        string text = StripComment(raw).Trim();
        if (text.Length == 0)
        {
            return;
        }

        while (true)
        {
            int colon = IndexOfLabelColon(text);
            if (colon < 0)
            {
                break;
            }

            string name = text[..colon].Trim();
            if (!IsIdentifier(name))
            {
                throw new AsmException(line, $"'{name}' is not a valid label name");
            }

            DefineLabel(name, line);
            text = text[(colon + 1)..].Trim();
            if (text.Length == 0)
            {
                return;
            }
        }

        int split = text.IndexOfAny(new[] { ' ', '\t' });
        string head = split < 0 ? text : text[..split];
        string rest = split < 0 ? "" : text[(split + 1)..].Trim();

        if (head.StartsWith('.'))
        {
            ParseDirective(head, rest, line);
            return;
        }

        if (_section != Section.Code)
        {
            throw new AsmException(line, $"instruction '{head}' appears in the data section");
        }

        (string mnemonic, Size size) = SplitSize(head, line);
        string[] ops = SplitOperands(rest);

        // PushM/PopM are architectural register-PAIR operations. The source
        // conveniences `push rN` and `pop rN` therefore expand to scalar stack
        // accesses; encoding either spelling as a one-bit pair mask silently
        // saves the neighbouring register and corrupts nested call frames.
        if (mnemonic.Equals("push", StringComparison.OrdinalIgnoreCase) ||
            mnemonic.Equals("pop", StringComparison.OrdinalIgnoreCase))
        {
            if (size != Size.D)
            {
                throw new AsmException(line, $"{mnemonic} does not take a size suffix");
            }
            Expect(ops, 1, line, mnemonic);
            _ = Reg(ops[0], line);

            if (mnemonic.Equals("push", StringComparison.OrdinalIgnoreCase))
            {
                AddInstruction("addi", Size.D, new[] { "sp", "sp", "-8" }, line);
                AddInstruction("st", Size.D, new[] { ops[0], "0(sp)" }, line);
            }
            else
            {
                AddInstruction("ld", Size.D, new[] { ops[0], "0(sp)" }, line);
                AddInstruction("addi", Size.D, new[] { "sp", "sp", "8" }, line);
            }
            return;
        }

        AddInstruction(mnemonic, size, ops, line);
    }

    private void AddInstruction(string mnemonic, Size size, string[] ops, int line)
    {

        // Intern wide constants HERE, in pass one, not while encoding. The
        // pool is part of the data segment, the data size decides where code
        // begins, and where code begins decides every absolute label — so a
        // constant discovered during encoding would move things that had
        // already been resolved.
        Reserve(mnemonic, ops);

        _items.Add(new Item
        {
            Line = line, Section = Section.Code, Address = _codeWords,
            Mnemonic = mnemonic, Size = size, Operands = ops,
            Words = SizeOf(mnemonic, ops, line),
        });
        _codeWords += SizeOf(mnemonic, ops, line);
    }

    /// <summary>Splits "add.w" into its mnemonic and size. Absent, the size is 64-bit.</summary>
    private static (string, Size) SplitSize(string head, int line)
    {
        // The dotted floating-point names are whole mnemonics, not sized ones.
        if (Isa.TryParseOp(head, out _))
        {
            return (head, Size.D);
        }

        int dot = head.LastIndexOf('.');
        if (dot <= 0)
        {
            return (head, Size.D);
        }

        string stem = head[..dot];
        if (!Isa.TryParseSize(head[(dot + 1)..], out Size size))
        {
            return (head, Size.D);
        }
        return (stem, size);
    }

    private void ParseDirective(string name, string rest, int line)
    {
        string[] ops = SplitOperands(rest);

        switch (name.ToLowerInvariant())
        {
            case ".code":
            case ".text":
                _section = Section.Code;
                return;

            case ".data":
                _section = Section.Data;
                return;

            case ".entry":
                if (ops.Length != 1)
                {
                    throw new AsmException(line, ".entry takes one label");
                }
                _entryLabel = ops[0];
                return;

            case ".equ":
                if (ops.Length != 2)
                {
                    throw new AsmException(line, ".equ takes a name and a value");
                }
                if (!IsIdentifier(ops[0]))
                {
                    throw new AsmException(line, $"'{ops[0]}' is not a valid name");
                }
                // Equates are resolved before labels, so allowing a redefinition
                // would let one silently shadow a label everywhere it is used.
                // Labels already reject collisions; this side must match.
                if (_equates.ContainsKey(ops[0]) || _codeLabels.ContainsKey(ops[0]) || _dataLabels.ContainsKey(ops[0]))
                {
                    throw new AsmException(line, $"'{ops[0]}' is already defined");
                }
                _equates[ops[0]] = ParseLiteral(ops[1], line);
                return;

            case ".base":
            {
                if (ops.Length != 1)
                {
                    throw new AsmException(line, ".base takes one address");
                }

                if (_dataBytes > 0 || _codeWords > 0)
                {
                    throw new AsmException(line, ".base must come before anything is emitted");
                }
                _imageBase = ParseLiteral(ops[0], line);
                return;
            }

            case ".quad":  AddData(ops, 8, line); return;
            case ".word":  AddData(ops, 4, line); return;
            case ".half":  AddData(ops, 2, line); return;
            case ".byte":  AddData(ops, 1, line); return;

            // One architecture-1.2 protected vector descriptor. Keeping this
            // as a directive makes a ROM table readable: the two operands are
            // the only fields firmware chooses, while the reserved words are
            // visibly and invariably zero.
            case ".vector2":
            {
                if (ops.Length != 2)
                {
                    throw new AsmException(line, ".vector2 takes a handler and control word");
                }
                AddData(new[] { ops[0], "0", ops[1], "0" }, 8, line);
                return;
            }

            case ".string": AddString(ops, false, line); return;
            case ".asciiz": AddString(ops, true,  line); return;

            case ".space":
            {
                if (ops.Length != 1)
                {
                    throw new AsmException(line, ".space takes a byte count");
                }
                long n = ParseLiteral(ops[0], line);
                if (n < 0 || n > 1 << 24)
                {
                    throw new AsmException(line, ".space count is out of range");
                }
                EmitBytes(new byte[n], line);
                return;
            }

            case ".align":
            {
                if (ops.Length != 1)
                {
                    throw new AsmException(line, ".align takes a byte alignment");
                }
                long a = ParseLiteral(ops[0], line);
                if (a <= 0 || (a & (a - 1)) != 0)
                {
                    throw new AsmException(line, ".align needs a power of two");
                }
                if (_section == Section.Data)
                {
                    int pad = (int)((a - (_dataBytes % a)) % a);
                    if (pad > 0)
                    {
                        EmitBytes(new byte[pad], line);
                    }
                }
                return;
            }

            default:
                throw new AsmException(line, $"unknown directive '{name}'");
        }
    }

    /// <summary>
    /// Lays down initialised data.
    ///
    /// Values that are already known are written now; anything naming a label
    /// that has not been seen yet is recorded and filled in during pass two.
    /// A forward reference from data to code is not an edge case — it is what a
    /// trap vector table IS, and firmware that must run before it has any
    /// writable memory has no other way to build one.
    /// </summary>
    private void AddData(string[] ops, int size, int line)
    {
        if (ops.Length == 0)
        {
            throw new AsmException(line, "data directive needs at least one value");
        }

        byte[] bytes = new byte[ops.Length * size];

        for (int i = 0; i < ops.Length; i++)
        {
            if (TryValue(ops[i], out long v))
            {
                for (int b = 0; b < size; b++)
                {
                    bytes[i * size + b] = (byte)(v >> (b * 8));
                }
                continue;
            }

            if (!IsIdentifier(ops[i]))
            {
                throw new AsmException(line, $"'{ops[i]}' is not a number, an equate, or a known label");
            }
            _dataRefs.Add((_dataBytes + i * size, size, ops[i], line));
        }
        EmitBytes(bytes, line);
    }

    /// <summary>Data that names a label defined later. Resolved in pass two.</summary>
    private readonly List<(int At, int Size, string Label, int Line)> _dataRefs = new();

    /// <summary>Resolves a value that may be a literal, an equate, or an already-known label.</summary>
    private bool TryValue(string text, out long value)
    {
        if (TryParseLiteral(text, out value))
        {
            return true;
        }

        if (_equates.TryGetValue(text, out value))
        {
            return true;
        }

        if (_dataLabels.TryGetValue(text, out long d))
        {
            value = d;
            return true;
        }
        value = 0;
        return false;
    }

    private void AddString(string[] ops, bool terminate, int line)
    {
        if (ops.Length != 1)
        {
            throw new AsmException(line, "string directive takes one quoted string");
        }

        byte[] body = Encoding.UTF8.GetBytes(ParseQuoted(ops[0], line));
        if (!terminate)
        {
            EmitBytes(body, line);
            return;
        }

        byte[] withNul = new byte[body.Length + 1];
        body.CopyTo(withNul, 0);
        EmitBytes(withNul, line);
    }

    private void EmitBytes(byte[] bytes, int line)
    {
        if (_section != Section.Data)
        {
            throw new AsmException(line, "data may only be emitted in the .data section");
        }

        _items.Add(new Item
        {
            Line = line, Section = Section.Data, Address = _dataBytes,
            Operands = Array.Empty<string>(), Bytes = bytes,
        });
        _dataBytes += bytes.Length;
    }

    private void DefineLabel(string name, int line)
    {
        if (_codeLabels.ContainsKey(name) || _dataLabels.ContainsKey(name) || _equates.ContainsKey(name))
        {
            throw new AsmException(line, $"'{name}' is already defined");
        }

        if (_section == Section.Code)
        {
            _codeLabels[name] = _codeWords;
        }
        else
        {
            // An ADDRESS, not an offset into the segment. Data is loaded at
            // DataBase because address zero is null and nothing may answer
            // there, so a dereference of null finds no board and faults.
            _dataLabels[name] = _imageBase + _dataBytes;
        }
    }

    /// <summary>
    /// How many code words an instruction occupies. Pseudo-instructions have a
    /// fixed size so layout never depends on an address not yet resolved.
    /// </summary>
    private int SizeOf(string mnemonic, string[] ops, int line)
    {
        switch (mnemonic.ToLowerInvariant())
        {
            case "li":
            case "mov":
            case "neg":
            case "not":
            case "ret":
            case "br":
            case "beqz":
            case "bnez":
                return 1;

            default:
                if (!Isa.TryParseOp(mnemonic, out _))
                {
                    throw new AsmException(line, $"unknown instruction '{mnemonic}'");
                }
                return 1;
        }
    }

    // ---- pass two -------------------------------------------------------

    private Image Encode()
    {
        ulong[] code   = new ulong[_codeWords];
        int[]   lineOf = new int[_codeWords];

        // Encoding is iterated to a fixed point, and it has to be.
        //
        // A wide constant lives in the pool, the pool is part of the data, the
        // data size decides where code begins, and where code begins decides
        // every absolute label — which in turn decides which labels are too
        // wide to sit in an instruction and therefore need the pool. Firmware
        // makes that circle real: assembled to the top of a 48-bit space, NO
        // label fits in an immediate, so every one of them goes through the
        // pool. Sizing the pool once and then encoding overflowed it.
        //
        // It settles in two rounds in practice; the cap is a backstop.
        Dictionary<string, long> words = new(_codeLabels, StringComparer.Ordinal);
        byte[] data = Array.Empty<byte>();
        int poolCount = _constants.Count;
        int poolOffset = checked((_dataBytes + 7) & ~7);

        for (int round = 0; ; round++)
        {
            Progress("pass 2 round " + (round + 1) + " begins");
            _constants.Clear();
            _constantIndex.Clear();

            _poolBase = _imageBase + poolOffset;
            data = new byte[poolOffset + poolCount * 8];
            _codeBase = (_imageBase + data.Length + 7) & ~7L;

            foreach ((string name, long at) in words)
            {
                _codeLabels[name] = _codeBase + at * 8L;
            }

            int encoded = 0;
            foreach (Item item in _items)
            {
                if (item.Section == Section.Data)
                {
                    continue;
                }

                code[item.Address]   = EncodeInstruction(item);
                lineOf[item.Address] = item.Line;
                encoded++;
                if (encoded % 500 == 0 || encoded == _codeWords)
                {
                    Progress("pass 2 round " + (round + 1) + " "
                        + encoded + "/" + _codeWords + " instructions");
                }
            }

            Progress("pass 2 round " + (round + 1) + " found "
                + _constants.Count + " constants");

            if (_constants.Count == poolCount)
            {
                break;
            }

            if (round >= 8)
            {
                throw new AsmException(0, "the constant pool will not settle; this is an assembler bug");
            }
            poolCount = _constants.Count;
        }

        foreach (Item item in _items)
        {
            if (item.Section == Section.Data)
            {
                item.Bytes!.CopyTo(data, item.Address);
            }
        }

        for (int i = 0; i < _constants.Count; i++)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(
                data.AsSpan(poolOffset + i * 8), _constants[i]);
        }

        // Data that named a label defined further down the file. Code labels
        // are only addresses once the code base is known, which is why this
        // waits until here.
        foreach ((int at, int size, string label, int line) in _dataRefs)
        {
            long v;

            if (_codeLabels.TryGetValue(label, out long target))
            {
                v = target;
            }
            else if (!TryValue(label, out v))
            {
                throw new AsmException(line, $"'{label}' is not a number, an equate, or a known label");
            }

            for (int b = 0; b < size; b++)
            {
                data[at + b] = (byte)(v >> (b * 8));
            }
        }

        int entry = 0;
        if (_entryLabel is not null)
        {
            if (!_codeLabels.TryGetValue(_entryLabel, out long entryAddr))
            {
                throw new AsmException(0, $".entry names '{_entryLabel}', which is not a code label");
            }

            // The image records the entry as an instruction INDEX. Where the
            // code lands is the loader's business, and an image that hard-coded
            // an address could only ever be loaded in one place.
            entry = (int)((entryAddr - _codeBase) / 8);

            if (entry >= code.Length)
            {
                throw new AsmException(0,
                    $".entry '{_entryLabel}' is at word {entry}, past the end of {code.Length} words of code");
            }
        }

        Progress("complete: " + code.Length + " instructions, "
            + data.Length + " data bytes");

        return new Image
        {
            Code = code, Data = data, Base = _imageBase,
            LineOf = lineOf, Entry = entry,
            CodeLabels = _codeLabels, DataLabels = _dataLabels,
        };
    }

    private ulong EncodeInstruction(Item item)
    {
        string m     = item.Mnemonic!.ToLowerInvariant();
        string[] ops = item.Operands;
        int line     = item.Line;
        Size sz      = item.Size;

        switch (m)
        {
            case "mov":
                Expect(ops, 2, line, m);
                return Isa.Encode(Op.Add, Reg(ops[0], line), Reg(ops[1], line), Isa.RegZero, size: sz);

            case "neg":
                Expect(ops, 2, line, m);
                return Isa.Encode(Op.Sub, Reg(ops[0], line), Isa.RegZero, Reg(ops[1], line), size: sz);

            case "not":
                Expect(ops, 2, line, m);
                return Isa.EncodeWide(Op.XorI, Reg(ops[0], line), Reg(ops[1], line), -1, sz);

            case "ret":
                Expect(ops, 0, line, m);
                return Isa.Encode(Op.JmpR, 0, Isa.RegLr, size: Size.B);

            case "br":
                Expect(ops, 1, line, m);
                return Isa.EncodeJump(Op.Jmp, JumpDisp(ops[0], item, line));

            case "beqz":
                Expect(ops, 2, line, m);
                return Isa.EncodeBranch(Op.Beq, Reg(ops[0], line),
                    Isa.RegZero, BranchDisp(ops[1], item, line), sz);

            case "bnez":
                Expect(ops, 2, line, m);
                return Isa.EncodeBranch(Op.Bne, Reg(ops[0], line),
                    Isa.RegZero, BranchDisp(ops[1], item, line), sz);

            case "li":
            {
                // One instruction always: small values fit the wide immediate,
                // and anything else joins the constant pool.
                Expect(ops, 2, line, m);
                int rd = Reg(ops[0], line);
                long v = Value(ops[1], line);
                if (v >= Isa.WideMin && v <= Isa.WideMax)
                {
                    return Isa.EncodeWide(Op.AddI, rd, Isa.RegZero, v, sz);
                }

                // Anything larger comes from the constant pool, which loads the
                // whole 64-bit value and has no width to narrow to. Silently
                // dropping the suffix would make li mean different things
                // either side of a magnitude the author cannot see.
                if (sz != Size.D)
                {
                    throw new AsmException(line,
                        $"li.{Isa.SizeSuffix(sz)}: {v} needs the constant pool, which loads a full 64-bit value; drop the suffix");
                }

                // A wide constant lives in the data segment and is loaded like
                // anything else. It used to index a table held beside the
                // machine, which was a leftover from code and data being
                // separate address spaces: with one space there is no reason
                // for the constant pool to be anywhere but in it.
                // Relative to this instruction. An absolute pool address does not
                // fit in the immediate once an image lives high in the space.
                long here = _codeBase + item.Address * 8L;
                return Isa.EncodeWide(
                    Op.LdPc, rd, Isa.RegZero,
                    _poolBase + Intern(v) * 8L - here, Size.D);
            }
        }

        if (!Isa.TryParseOp(m, out Op op))
        {
            throw new AsmException(line, $"unknown instruction '{m}'");
        }

        if (!Isa.UsesSize(op) && sz != Size.D)
        {
            throw new AsmException(line, $"{m} does not take a size suffix");
        }

        // Opcodes that ignore the size field must encode it as zero.
        Size enc = Isa.UsesSize(op) ? sz : Size.B;
        bool fp  = Isa.ReadsFpBank(op) || Isa.WritesFpBank(op);
        // An element accessor carries a float in rd and integers everywhere else.
        bool split = Isa.FpValueIntAddress(op);
        bool srcFp = fp && !split;
        bool dstFp = Isa.WritesFpBank(op) || split;

        // Appendix A names the base register form.  The String chapter also
        // assigns the otherwise independent immediate bits to an explicit
        // S-register selector or operation option; expose that operand without
        // inventing a nonarchitectural instruction format.
        if (op is Op.MTS or Op.MFS or Op.MFZ or Op.MTZ)
        {
            Expect(ops, 2, line, m);
            // The manual spells these in data-flow order even though the
            // selector occupies the same immediate overlay in both forms:
            //     MTS Sselector, rs1
            //     MFS rd, Sselector
            bool toUnitRegister = op is Op.MTS or Op.MTZ;
            long which = WideImm(toUnitRegister ? ops[0] : ops[1], line, m);
            int count = op is Op.MTS or Op.MFS
                ? Isa.StrRegCount : Isa.CompressionRegCount;
            if ((ulong)which >= (ulong)count)
            {
                throw new AsmException(line,
                    $"{m} names unit register {which}; the valid range is 0 to {count - 1}");
            }
            return toUnitRegister
                ? Isa.Encode(op, 0, Reg(ops[1], line), 0, imm: which, size: enc)
                : Isa.Encode(op, Reg(ops[0], line), 0, 0, imm: which, size: enc);
        }

        if (((int)op >> 9) == 0x0A && op is not Op.MTS and not Op.MFS)
        {
            if (ops.Length is not 4 and not 5)
            {
                throw new AsmException(line,
                    $"{m} takes four registers and an optional immediate, got {ops.Length}");
            }
            long which = ops.Length == 5 ? WideImm(ops[4], line, m) : 0;
            long limit = op is Op.SSpan or Op.SBrk ? 16
                : op is Op.SHash or Op.STrans ? Isa.StrRegCount
                : 0x200;
            if (which < 0 || which >= limit)
            {
                throw new AsmException(line,
                    op is Op.SSpan or Op.SBrk
                        ? $"{m} set selector {which} is outside 0..15"
                        : $"{m} names string register {which}; there are {Isa.StrRegCount}, s0 to s{Isa.StrRegCount - 1}");
            }
            return Isa.Encode(op, Reg(ops[0], line), Reg(ops[1], line),
                              Reg(ops[2], line), Reg(ops[3], line),
                              which, enc);
        }

        if (op == Op.TlbInv)
        {
            Expect(ops, 4, line, m);
            long mode = ops[3].ToLowerInvariant() switch
            {
                "page_asid" => Isa.TlbInvPageAsid,
                "asid" => Isa.TlbInvAsid,
                "range_asid" => Isa.TlbInvRangeAsid,
                "all_nonglobal" => Isa.TlbInvAllNonGlobal,
                "all" => Isa.TlbInvAll,
                _ => WideImm(ops[3], line, m),
            };
            if (mode < Isa.TlbInvPageAsid || mode > Isa.TlbInvAll)
            {
                throw new AsmException(line,
                    $"{m} mode {mode} is outside {Isa.TlbInvPageAsid}..{Isa.TlbInvAll}");
            }
            return Isa.Encode(op, Reg(ops[0], line), Reg(ops[1], line),
                              Reg(ops[2], line), imm: mode, size: enc);
        }

        if (op == Op.TmrSet)
        {
            Expect(ops, 3, line, m);
            return Isa.Encode(op, Reg(ops[0], line), Reg(ops[1], line),
                              Reg(ops[2], line), size: enc);
        }

        if (op == Op.TmrGet)
        {
            Expect(ops, 2, line, m);
            return Isa.Encode(op, Reg(ops[0], line), Reg(ops[1], line),
                              size: enc);
        }

        if (op == Op.LpicGet)
        {
            Expect(ops, 2, line, m);
            return Isa.Encode(op, Reg(ops[0], line), Reg(ops[1], line),
                              size: enc);
        }

        if (op is Op.HMD5 or Op.HSHA1 or Op.HSHA2)
        {
            Expect(ops, 1, line, m);
            return Isa.Encode(op, 0, Reg(ops[0], line), 0, size: enc);
        }
        if (op == Op.HInit)
        {
            Expect(ops, 1, line, m);
            return Isa.EncodeWide(op, 0, 0, WideImm(ops[0], line, m), enc);
        }
        if (op == Op.Rand)
        {
            Expect(ops, 1, line, m);
            return Isa.Encode(op, Reg(ops[0], line), size: enc);
        }
        if (op is Op.BitGet or Op.BitPut or Op.BitPeek or Op.BitFlush)
        {
            if (ops.Length is not 4 and not 5)
            {
                throw new AsmException(line,
                    $"{m} takes four registers and an optional OPTION value, got {ops.Length} operands");
            }
            long option = ops.Length == 5
                ? WideImm(ops[4], line, m)
                : op == Op.BitFlush ? 2 : 0;
            bool legal = op == Op.BitFlush
                ? option is 2 or 3
                : option is 0 or 1;
            if (!legal)
            {
                throw new AsmException(line,
                    op == Op.BitFlush
                        ? $"{m} OPTION must be 2 (zero pad) or 3 (one pad)"
                        : $"{m} OPTION must be 0 (LSB first) or 1 (MSB first)");
            }
            return Isa.Encode(op, Reg(ops[0], line), Reg(ops[1], line),
                              Reg(ops[2], line), Reg(ops[3], line),
                              option << 3, enc);
        }

        // The decimal unit has its own eight-register bank.  Accepting r0-r7
        // happened to encode the same field bits, but concealed mixed-bank
        // mistakes such as DFromF d0,r1 and made disassembly lie about what an
        // instruction reads.  Keep rN as an input compatibility spelling, as
        // the vector assembler does, while always printing canonical dN.
        if (op is Op.DAdd or Op.DSub or Op.DMul or Op.DDiv or Op.DRem)
        {
            Expect(ops, 3, line, m);
            return Isa.Encode(op, DecimalReg(ops[0], line),
                              DecimalReg(ops[1], line),
                              DecimalReg(ops[2], line), size: enc);
        }
        if (op == Op.DCmp)
        {
            Expect(ops, 3, line, m);
            return Isa.Encode(op, Reg(ops[0], line),
                              DecimalReg(ops[1], line),
                              DecimalReg(ops[2], line), size: enc);
        }
        if (op is Op.DNeg or Op.DAbs or Op.DTrunc or Op.DFloor or Op.DCeil)
        {
            Expect(ops, 2, line, m);
            return Isa.Encode(op, DecimalReg(ops[0], line),
                              DecimalReg(ops[1], line), size: enc);
        }
        if (op is Op.DRound or Op.DScale)
        {
            Expect(ops, 3, line, m);
            return Isa.EncodeWide(op, DecimalReg(ops[0], line),
                                  DecimalReg(ops[1], line),
                                  WideImm(ops[2], line, m), enc);
        }
        if (op == Op.DFromI)
        {
            Expect(ops, 2, line, m);
            return Isa.Encode(op, DecimalReg(ops[0], line),
                              Reg(ops[1], line), size: enc);
        }
        if (op == Op.DToI)
        {
            Expect(ops, 2, line, m);
            return Isa.Encode(op, Reg(ops[0], line),
                              DecimalReg(ops[1], line), size: enc);
        }
        if (op == Op.DFromF)
        {
            Expect(ops, 2, line, m);
            return Isa.Encode(op, DecimalReg(ops[0], line),
                              FpReg(ops[1], line), size: enc);
        }
        if (op == Op.DToF)
        {
            Expect(ops, 2, line, m);
            return Isa.Encode(op, FpReg(ops[0], line),
                              DecimalReg(ops[1], line), size: enc);
        }
        if (op == Op.CvtF2I)
        {
            if (ops.Length is not 2 and not 3)
            {
                throw new AsmException(line,
                    $"{m} takes two registers and an optional rounding override");
            }
            long rounding = ops.Length == 3 ? WideImm(ops[2], line, m) : 0;
            if (rounding is < 0 or > 4)
            {
                throw new AsmException(line,
                    $"{m} rounding override {rounding} is outside 0..4");
            }
            return Isa.Encode(op, Reg(ops[0], line), FpReg(ops[1], line),
                              imm: rounding, size: enc);
        }
        if (op == Op.MTD)
        {
            Expect(ops, 2, line, m);
            return Isa.Encode(op, DecimalReg(ops[0], line),
                              Reg(ops[1], line), size: enc);
        }
        if (op == Op.MFD)
        {
            Expect(ops, 2, line, m);
            return Isa.Encode(op, Reg(ops[0], line),
                              DecimalReg(ops[1], line), size: enc);
        }
        if (op is Op.DLd or Op.DSt)
        {
            Expect(ops, 2, line, m);
            (int baseReg, long off) = ParseMemory(ops[1], line);
            return Isa.EncodeWide(op, DecimalReg(ops[0], line),
                                  baseReg, off, enc);
        }
        if (op is Op.DPack or Op.DFmt)
        {
            Expect(ops, 4, line, m);
            return Isa.Encode(op, Reg(ops[0], line),
                              DecimalReg(ops[1], line),
                              Reg(ops[2], line), Reg(ops[3], line),
                              size: enc);
        }
        if (op is Op.DUnpk or Op.DNum)
        {
            Expect(ops, 4, line, m);
            return Isa.Encode(op, DecimalReg(ops[0], line),
                              Reg(ops[1], line),
                              Reg(ops[2], line), Reg(ops[3], line),
                              size: enc);
        }

        // Unit 05 has mixed register banks and several architecturally
        // reserved fields, so spelling it as a generic R/R4 instruction is
        // both misleading and incapable of naming the element/mask overlay.
        // The final overlay operand is optional when zero.
        if (Isa.IsVectorInstruction(op))
        {
            int Overlay(int baseCount, int allowedMask)
            {
                int count = ops.Length;
                if (count != baseCount && count != baseCount + 1)
                {
                    throw new AsmException(line,
                        $"{m} takes {baseCount} operands plus an optional vector overlay, got {ops.Length}");
                }
                long value = ops.Length == baseCount ? 0 : WideImm(ops[^1], line, m);
                if (value < 0 || (value & ~allowedMask) != 0)
                {
                    throw new AsmException(line,
                        $"{m} vector overlay 0x{value:X} has reserved bits set; allowed mask is 0x{allowedMask:X}");
                }
                return (int)value;
            }

            if (op == Op.VLen)
            {
                Expect(ops, 2, line, m);
                return Isa.Encode(op, Reg(ops[0], line), Reg(ops[1], line), size: enc);
            }
            if (op is Op.VLd or Op.VSt)
            {
                int overlay = Overlay(2, 0x1F);
                return Isa.Encode(op, VectorReg(ops[0], line), Reg(ops[1], line),
                                  imm: overlay, size: enc);
            }
            if (op == Op.VSplat)
            {
                int overlay = Overlay(2, 0x03);
                return Isa.Encode(op, VectorReg(ops[0], line), Reg(ops[1], line),
                                  imm: overlay, size: enc);
            }
            if (op is Op.VLdS or Op.VStS)
            {
                int overlay = Overlay(3, 0x1F);
                return Isa.Encode(op, VectorReg(ops[0], line), Reg(ops[1], line),
                                  Reg(ops[2], line), imm: overlay, size: enc);
            }
            if (op is Op.VGath or Op.VScat)
            {
                int overlay = Overlay(4, 0x03);
                return Isa.Encode(op, VectorReg(ops[0], line), Reg(ops[1], line),
                                  VectorReg(ops[2], line), Reg(ops[3], line),
                                  overlay, enc);
            }
            if (op is Op.VSel or Op.VShuf)
            {
                int overlay = Overlay(4, 0x03);
                return Isa.Encode(op, VectorReg(ops[0], line), VectorReg(ops[1], line),
                                  VectorReg(ops[2], line), Reg(ops[3], line),
                                  overlay, enc);
            }
            if (op == Op.VCmp)
            {
                int overlay = Overlay(4, 0x03);
                return Isa.Encode(op, VectorMaskReg(ops[0], line),
                                  VectorReg(ops[1], line), VectorReg(ops[2], line),
                                  Reg(ops[3], line), overlay, enc);
            }
            if (op == Op.VRed)
            {
                int overlay = Overlay(4, 0x03);
                return Isa.Encode(op, Reg(ops[0], line), VectorReg(ops[1], line),
                                  Reg(ops[2], line), Reg(ops[3], line),
                                  overlay, enc);
            }
            if (op == Op.MTV)
            {
                int overlay = Overlay(3, 0x03);
                return Isa.Encode(op, VectorReg(ops[0], line), Reg(ops[1], line),
                                  Reg(ops[2], line), imm: overlay, size: enc);
            }
            if (op == Op.MFV)
            {
                int overlay = Overlay(3, 0x03);
                return Isa.Encode(op, Reg(ops[0], line), VectorReg(ops[1], line),
                                  Reg(ops[2], line), imm: overlay, size: enc);
            }
        }

        switch (Isa.FormatOf(op))
        {
            case Fmt.None:
                Expect(ops, 0, line, m);
                return Isa.Encode(op, size: enc);

            case Fmt.R:
            {
                bool vector = Isa.IsVectorSize(enc) && Isa.IsVectorizable(op);
                if (Isa.IsVectorSize(enc) && !vector)
                {
                    throw new AsmException(line, $"{m} cannot use a vector size");
                }
                int expected = vector ? 4 : 3;
                Expect(ops, expected, line, m);
                return Isa.Encode(op,
                    vector ? VectorReg(ops[0], line) : dstFp ? FpReg(ops[0], line) : Reg(ops[0], line),
                    vector ? VectorReg(ops[1], line) : srcFp ? FpReg(ops[1], line) : Reg(ops[1], line),
                    vector ? VectorReg(ops[2], line) : srcFp ? FpReg(ops[2], line) : Reg(ops[2], line),
                    imm: vector ? VectorOverlay(ops[3], line, m) : 0,
                    size: enc);
            }

            case Fmt.R4I:
            {
                // Four registers and a small literal. The string engine uses
                // this for operations such as invariant case conversion whose
                // cursor state occupies all four register fields.
                Expect(ops, 5, line, m);
                return Isa.Encode(op, Reg(ops[0], line), Reg(ops[1], line), Reg(ops[2], line),
                                  Reg(ops[3], line), WideImm(ops[4], line, m), enc);
            }

            case Fmt.R4S:
            {
                Expect(ops, 5, line, m);
                long which = WideImm(ops[4], line, m);

                long limit = op is Op.SSpan or Op.SBrk ? 16 : Isa.StrRegCount;

                if (which < 0 || which >= limit)
                {
                    throw new AsmException(line,
                        op is Op.SSpan or Op.SBrk
                            ? $"{m} set selector {which} is outside 0..15"
                            : $"{m} names string register {which}; there are {Isa.StrRegCount}, s0 to s{Isa.StrRegCount - 1}");
                }

                return Isa.Encode(op, Reg(ops[0], line), Reg(ops[1], line), Reg(ops[2], line),
                                  Reg(ops[3], line), which, enc);
            }

            case Fmt.RS:
            {
                // rd, rs1, rs2, and the string register the operation works
                // against. Named explicitly at every site: the 8086's REP MOVSB
                // took its operands from registers the instruction did not
                // mention, and that is most of why compilers would not emit it.
                Expect(ops, 4, line, m);

                long which = WideImm(ops[3], line, m);

                if (which < 0 || which >= Isa.StrRegCount)
                {
                    throw new AsmException(line,
                        $"{m} names string register {which}; there are {Isa.StrRegCount}, s0 to s{Isa.StrRegCount - 1}");
                }

                return Isa.Encode(op, Reg(ops[0], line), Reg(ops[1], line), Reg(ops[2], line),
                                  imm: which, size: enc);
            }

            case Fmt.R4:
            {
                bool vector = Isa.IsVectorSize(enc) && Isa.IsVectorizable(op);
                if (Isa.IsVectorSize(enc) && !vector)
                {
                    throw new AsmException(line, $"{m} cannot use a vector size");
                }
                Expect(ops, vector ? 5 : 4, line, m);
                // The condition of a select is always an integer register.
                bool condIsInt = op is Op.CSel or Op.FCSel;
                return Isa.Encode(op,
                    vector ? VectorReg(ops[0], line) : dstFp ? FpReg(ops[0], line) : Reg(ops[0], line),
                    vector ? VectorReg(ops[1], line) : (fp && !split) ? FpReg(ops[1], line) : Reg(ops[1], line),
                    vector ? VectorReg(ops[2], line) : (fp && !split) ? FpReg(ops[2], line) : Reg(ops[2], line),
                    vector ? VectorReg(ops[3], line) : (fp && !split && !condIsInt) ? FpReg(ops[3], line) : Reg(ops[3], line),
                    imm: vector ? VectorOverlay(ops[4], line, m) : 0,
                    size: enc);
            }

            case Fmt.R1:
            {
                bool vector = Isa.IsVectorSize(enc) && Isa.IsVectorizable(op);
                if (Isa.IsVectorSize(enc) && !vector)
                {
                    throw new AsmException(line, $"{m} cannot use a vector size");
                }
                Expect(ops, vector ? 3 : 2, line, m);
                return Isa.Encode(op,
                    vector ? VectorReg(ops[0], line) : Isa.WritesFpBank(op) ? FpReg(ops[0], line) : Reg(ops[0], line),
                    vector ? VectorReg(ops[1], line) : Isa.ReadsFpBank(op)  ? FpReg(ops[1], line) : Reg(ops[1], line),
                    imm: vector ? VectorOverlay(ops[2], line, m) : 0,
                    size: enc);
            }

            case Fmt.I:
                if (Isa.IsVectorSize(enc))
                {
                    if (!Isa.IsVectorizable(op))
                    {
                        throw new AsmException(line, $"{m} cannot use a vector size");
                    }
                    Expect(ops, 4, line, m);
                    long scalar = Value(ops[2], line);
                    if (scalar < -(1L << 24) || scalar >= (1L << 24))
                    {
                        throw new AsmException(line,
                            $"{m}: vector broadcast immediate {scalar} does not fit in signed 25 bits");
                    }
                    return Isa.EncodeVectorWide(op, VectorReg(ops[0], line),
                        VectorReg(ops[1], line), scalar,
                        checked((int)VectorOverlay(ops[3], line, m)), enc);
                }
                Expect(ops, 3, line, m);
                return Isa.EncodeWide(op, Reg(ops[0], line), Reg(ops[1], line),
                                      WideImm(ops[2], line, m), enc);

            case Fmt.M:
            {
                Expect(ops, 2, line, m);
                (int baseReg, long off) = ParseMemory(ops[1], line);
                return Isa.EncodeWide(op,
                    fp ? FpReg(ops[0], line) : Reg(ops[0], line),
                    baseReg, off, enc);
            }

            case Fmt.B:
                if (op == Op.Djnz)
                {
                    Expect(ops, 2, line, m);
                    return Isa.EncodeBranch(op, Reg(ops[0], line),
                        Isa.RegZero, BranchDisp(ops[1], item, line), enc);
                }
                Expect(ops, 3, line, m);
                return Isa.EncodeBranch(op, Reg(ops[0], line),
                    Reg(ops[1], line), BranchDisp(ops[2], item, line), enc);

            case Fmt.J:
                Expect(ops, 1, line, m);
                return Isa.EncodeJump(op, JumpDisp(ops[0], item, line));

            case Fmt.Reg:
                Expect(ops, 1, line, m);
                return Isa.Encode(op, 0, Reg(ops[0], line), size: Size.B);

            case Fmt.OutReg:
                Expect(ops, 1, line, m);
                return Isa.Encode(op, Reg(ops[0], line), size: Size.B);

            case Fmt.Imm:
                Expect(ops, 1, line, m);
                return Isa.EncodeWide(op, 0, 0, WideImm(ops[0], line, m), Size.B);

            case Fmt.U:
                Expect(ops, 2, line, m);
                return Isa.EncodeWide(op, Reg(ops[0], line), 0,
                    op == Op.Adr
                        ? AddressImmediate(ops[1], item, line, m)
                        : WideImm(ops[1], line, m), Size.B);

            case Fmt.Mask:
            {
                if (ops.Length == 0)
                {
                    throw new AsmException(line, $"{m} needs at least one register");
                }

                // The two opcodes are the two halves of one 32-register file:
                // PushM/PopM use one bit per r0-r15 register and PushM2/PopM2
                // use one bit per r16-r31 register.  Pairing even and odd
                // registers was an obsolete prototype encoding.  Besides
                // disagreeing with the Rev. 1.4 Mask form, it made the BIOS's
                // all-register PushM2 save only eight registers and shortened
                // every protected trap frame by sixty-four bytes.
                int first = op is Op.PushM2 or Op.PopM2 ? 16 : 0;
                int mask = 0;

                foreach (string o in ops)
                {
                    int r = Reg(o, line);

                    if (r < first || r >= first + 16)
                    {
                        throw new AsmException(line,
                            $"{m} selects r{first}-r{first + 15}, not r{r}");
                    }

                    mask |= 1 << (r - first);
                }
                return Isa.Encode(op, imm: mask, size: Size.B);
            }

            default:
                throw new AsmException(line, $"'{m}' has no encoding");
        }
    }

    /// <summary>
    /// Claims a pool slot in pass one for anything that will need one.
    ///
    /// Only a numeric literal can be too wide to sit in an instruction: a label
    /// is an address, and every address in an image is comfortably inside the
    /// immediate. So this looks at literals and at equates already defined, and
    /// leaves everything else alone.
    /// </summary>
    private void Reserve(string mnemonic, string[] ops)
    {
        if (!mnemonic.Equals("li", StringComparison.OrdinalIgnoreCase) || ops.Length != 2)
        {
            return;
        }

        long value;

        if (TryParseLiteral(ops[1], out long literal))
        {
            value = literal;
        }
        else if (_equates.TryGetValue(ops[1], out long equate))
        {
            value = equate;
        }
        else
        {
            return;
        }

        if (value < Isa.WideMin || value > Isa.WideMax)
        {
            Intern(value);
        }
    }

    /// <summary>Adds a constant to the pool, reusing an existing entry when it matches.</summary>
    private int Intern(long value)
    {
        if (_constantIndex.TryGetValue(value, out int at))
        {
            return at;
        }
        at = _constants.Count;
        _constants.Add(value);
        _constantIndex[value] = at;
        return at;
    }

    private static void Expect(string[] ops, int count, int line, string what)
    {
        if (ops.Length != count)
        {
            throw new AsmException(line, $"{what} takes {count} operand{(count == 1 ? "" : "s")}, got {ops.Length}");
        }
    }

    private static int Reg(string text, int line)
    {
        int r = Isa.ParseReg(text);
        if (r < 0)
        {
            throw new AsmException(line, $"'{text}' is not an integer register");
        }
        if (r >= Isa.RegCount)
        {
            throw new AsmException(line, $"'{text}' is beyond r{Isa.RegCount - 1}; this CPU implements {Isa.RegCount} registers");
        }
        return r;
    }

    private static int FpReg(string text, int line)
    {
        int r = Isa.ParseFpReg(text);
        if (r < 0)
        {
            throw new AsmException(line, $"'{text}' is not a floating-point register");
        }
        if (r >= Isa.FpRegCount)
        {
            throw new AsmException(line, $"'{text}' is beyond f{Isa.FpRegCount - 1}; this CPU implements {Isa.FpRegCount}");
        }
        return r;
    }

    private static int VectorReg(string text, int line)
        => BankReg(text, 'v', 16, "vector", line, allowIntegerSpelling: true);

    private static int DecimalReg(string text, int line)
        => BankReg(text, 'd', Isa.DecimalRegCount, "decimal", line,
                   allowIntegerSpelling: true);

    private static int VectorMaskReg(string text, int line)
    {
        if (text.Length >= 3
            && text.StartsWith("vm", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(text.AsSpan(2), out int mask)
            && mask >= 0 && mask < 8)
        {
            return mask;
        }
        int general = Isa.ParseReg(text);
        if (general >= 0 && general < 8)
        {
            return general;
        }
        throw new AsmException(line, $"'{text}' is not a vector-mask register vm0-vm7");
    }

    private static int BankReg(string text, char prefix, int count, string bank,
                               int line, bool allowIntegerSpelling)
    {
        if (text.Length >= 2
            && char.ToLowerInvariant(text[0]) == prefix
            && int.TryParse(text.AsSpan(1), out int named)
            && named >= 0 && named < count)
        {
            return named;
        }
        if (allowIntegerSpelling)
        {
            int general = Isa.ParseReg(text);
            if (general >= 0 && general < count)
            {
                return general;
            }
        }
        throw new AsmException(line,
            $"'{text}' is not a {bank} register {prefix}0-{prefix}{count - 1}");
    }

    private long VectorOverlay(string text, int line, string mnemonic)
    {
        long overlay = WideImm(text, line, mnemonic);
        if (overlay < 0 || overlay > 0x1F)
        {
            throw new AsmException(line,
                $"{mnemonic}: vector element/mask overlay 0x{overlay:X} is outside 0x00..0x1F");
        }
        return overlay;
    }

    private long WideImm(string text, int line, string what)
    {
        long v = Value(text, line);
        if (v < Isa.WideMin || v > Isa.WideMax)
        {
            throw new AsmException(line, $"{what}: {v} does not fit in a {Isa.WideBits}-bit immediate (use li)");
        }
        return v;
    }

    /// <summary>
    /// ADR's immediate is a byte displacement from the ADR instruction itself.
    /// A symbolic operand therefore relocates with the image; a numeric operand
    /// remains the literal displacement written by the programmer.
    /// </summary>
    private long AddressImmediate(string text, Item item, int line, string what)
    {
        long target;

        if (!_codeLabels.TryGetValue(text, out target)
            && !_dataLabels.TryGetValue(text, out target))
        {
            return WideImm(text, line, what);
        }

        long displacement = target - (_codeBase + item.Address * 8L);

        if (displacement < Isa.WideMin || displacement > Isa.WideMax)
        {
            throw new AsmException(line,
                $"{what}: label '{text}' is {displacement} bytes away, out of range");
        }
        return displacement;
    }

    private (int Reg, long Off) ParseMemory(string text, int line)
    {
        int open = text.IndexOf('(');
        if (open < 0 || !text.EndsWith(')'))
        {
            throw new AsmException(line, $"'{text}' is not a memory operand; write it as off(reg)");
        }

        string offText = text[..open].Trim();
        long off = offText.Length == 0 ? 0 : Value(offText, line);
        if (off < Isa.WideMin || off > Isa.WideMax)
        {
            throw new AsmException(line, $"offset {off} does not fit in a {Isa.WideBits}-bit field");
        }
        return (Reg(text[(open + 1)..^1].Trim(), line), off);
    }

    private long BranchDisp(string label, Item item, int line)
    {
        long disp = (CodeLabel(label, line) - (_codeBase + (item.Address + 1) * 8L)) / 8;
        if (disp < Isa.BrMin || disp > Isa.BrMax)
        {
            throw new AsmException(line, $"branch to '{label}' is {disp} words away, out of range");
        }
        return disp;
    }

    private long JumpDisp(string label, Item item, int line)
    {
        long disp = (CodeLabel(label, line) - (_codeBase + (item.Address + 1) * 8L)) / 8;
        if (disp < Isa.JmpMin || disp > Isa.JmpMax)
        {
            throw new AsmException(line, $"jump to '{label}' is {disp} words away, out of range");
        }
        return disp;
    }

    /// <summary>
    /// Resolves a branch or jump target. A bare number is an absolute word
    /// address, which is what the disassembler emits when it has no label for
    /// the target — so its output stays valid assembler input even for an image
    /// built without debug information.
    /// </summary>
    private long CodeLabel(string name, int line)
    {
        if (_codeLabels.TryGetValue(name, out long at))
        {
            return at;
        }
        if (TryParseLiteral(name, out long absolute))
        {
            if (absolute < 0 || absolute > int.MaxValue)
            {
                throw new AsmException(line, $"branch target {absolute} is not a valid code address");
            }
            return (int)absolute;
        }
        if (_equates.TryGetValue(name, out long e))
        {
            return (int)e;
        }
        if (_dataLabels.ContainsKey(name))
        {
            throw new AsmException(line, $"'{name}' is a data label; branches need a code label or an address");
        }
        throw new AsmException(line, $"'{name}' is not defined");
    }

    /// <summary>A literal, an equate, or the address of a label.</summary>
    private long Value(string text, int line)
    {
        if (TryParseLiteral(text, out long v))
        {
            return v;
        }
        if (_equates.TryGetValue(text, out long e))
        {
            return e;
        }
        if (_dataLabels.TryGetValue(text, out long d))
        {
            return d;
        }
        if (_codeLabels.TryGetValue(text, out long c))
        {
            return c;
        }
        throw new AsmException(line, $"'{text}' is not a number, an equate, or a known label");
    }

    private long ParseLiteral(string text, int line)
    {
        if (!TryParseLiteral(text, out long v))
        {
            throw new AsmException(line, $"'{text}' is not a number");
        }
        return v;
    }

    private static bool TryParseLiteral(string text, out long value)
    {
        value = 0;
        if (text.Length == 0)
        {
            return false;
        }

        bool negative = text[0] == '-';
        ReadOnlySpan<char> body = (negative || text[0] == '+') ? text.AsSpan(1) : text.AsSpan();
        if (body.Length == 0)
        {
            return false;
        }

        if (body.Length >= 3 && body[0] == '\'' && body[^1] == '\'')
        {
            string inner = Unescape(body[1..^1].ToString());
            if (inner.Length != 1)
            {
                return false;
            }
            value = negative ? -inner[0] : inner[0];
            return true;
        }

        bool ok;
        bool bitPattern = false;
        ulong magnitude;
        if (body.Length > 2 && body[0] == '0' && body[1] is 'x' or 'X')
        {
            ok = ulong.TryParse(body[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out magnitude);
            bitPattern = true;
        }
        else if (body.Length > 2 && body[0] == '0' && body[1] is 'b' or 'B')
        {
            ok = TryParseBinary(body[2..], out magnitude);
            bitPattern = true;
        }
        else
        {
            ok = ulong.TryParse(body, NumberStyles.Integer, CultureInfo.InvariantCulture, out magnitude);
        }

        if (!ok)
        {
            return false;
        }

        // Writing a bit pattern in hex or binary and meaning the negative value
        // it denotes is ordinary assembler idiom, so 0xFFFF...FF is -1. Writing
        // a plain decimal above the signed range is a mistake, not an idiom, and
        // silently flipping its sign would be the worst possible answer.
        if (!bitPattern && !negative && magnitude > (ulong)long.MaxValue)
        {
            return false;
        }

        // Cast before negating: -(long)ulong.MaxValue would overflow the other way.
        value = negative ? unchecked(-(long)magnitude) : unchecked((long)magnitude);
        return true;
    }

    private static bool TryParseBinary(ReadOnlySpan<char> text, out ulong value)
    {
        value = 0;
        if (text.Length == 0 || text.Length > 64)
        {
            return false;
        }

        foreach (char c in text)
        {
            if (c is not ('0' or '1'))
            {
                return false;
            }
            value = (value << 1) | (uint)(c - '0');
        }
        return true;
    }

    // ---- lexing ---------------------------------------------------------

    /// <summary>Strips ';' and '//' comments without cutting inside a string literal.</summary>
    private static string StripComment(string text)
    {
        bool inString = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '"' && !IsEscaped(text, i))
            {
                inString = !inString;
                continue;
            }
            if (inString)
            {
                continue;
            }
            if (c == ';' || (c == '/' && i + 1 < text.Length && text[i + 1] == '/'))
            {
                return text[..i];
            }
        }
        return text;
    }

    /// <summary>Counts preceding backslashes, so an escaped backslash before a quote does not hide it.</summary>
    private static bool IsEscaped(string text, int at)
    {
        int slashes = 0;
        for (int i = at - 1; i >= 0 && text[i] == '\\'; i--)
        {
            slashes++;
        }
        return (slashes & 1) == 1;
    }

    /// <summary>Position of a label's colon, or -1. A space before any colon means this is an instruction.</summary>
    private static int IndexOfLabelColon(string text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '"')
            {
                return -1;
            }
            if (c == ':')
            {
                return i;
            }
            if (c is ' ' or '\t')
            {
                return -1;
            }
        }
        return -1;
    }

    private static string[] SplitOperands(string text)
    {
        if (text.Trim().Length == 0)
        {
            return Array.Empty<string>();
        }

        List<string> parts = new();
        StringBuilder current = new();
        bool inString = false;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '"' && !IsEscaped(text, i))
            {
                inString = !inString;
                current.Append(c);
                continue;
            }
            if (c == ',' && !inString)
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

    private static string ParseQuoted(string text, int line)
    {
        if (text.Length < 2 || text[0] != '"' || text[^1] != '"')
        {
            throw new AsmException(line, "expected a quoted string");
        }
        return Unescape(text[1..^1]);
    }

    private static string Unescape(string text)
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
            sb.Append(text[i] switch
            {
                'n'  => '\n',
                't'  => '\t',
                'r'  => '\r',
                '0'  => '\0',
                '\\' => '\\',
                '"'  => '"',
                '\'' => '\'',
                _    => text[i],
            });
        }
        return sb.ToString();
    }

    private static bool IsIdentifier(string text)
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
}
