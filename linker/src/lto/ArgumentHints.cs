using System.Text;
using Corsac.Lang.Elf;
using Corsac.Lang.Ir;

namespace Corsac.Lang.Lto;

/// <summary>
/// WHAT A UNIT SAYS OF ITS FUNCTIONS' ARGUMENTS, for the link to judge over
/// every unit together (ArgumentSolver): each function's parameters -- their
/// kinds, which it reads, how much of it a known value would fold away --
/// and each call it makes, with what stands in each argument's place: a
/// constant, an address, the caller's own parameter handed on, or a value
/// nothing here can name. A parameter every caller in the whole program
/// gives the one constant is no parameter; one a caller here gives a
/// constant the function branches on is a version of the function worth
/// having. No unit can say either alone: the calls are in other units.
///
/// The shape of the regions' hints (RegionHints): made from the IR the link
/// gets -- the module as the late passes find it, before they run, so what
/// is said here is said of the calls the link's own run of those passes
/// starts from -- written into the unit's object beside that IR in a
/// section of its own, read by the link, never changed by it, and sorted
/// throughout so the bytes are the unit's and not the order its functions
/// were met in.
/// </summary>
public sealed class ArgumentHints
{
    public const string SectionName = ".corsac.arguments";
    public const int MaximumBytes = 64 * 1024 * 1024;
    private const uint Magic = 0x47524143; // "CARG"
    // One layout, 0, until a release.
    private const int Version = 0;

    public static void Attach(ObjectFile obj, byte[] written)
    {
        if (obj.Sections.Any(section => section.Name == SectionName)) throw new ElfFormatException("Duplicate argument hints");
        Section section = new(SectionName, SectionKind.Note);
        section.Bytes.AddRange(written);
        obj.Sections.Add(section);
    }

    public static ArgumentHints? Read(ObjectFile obj)
    {
        Section[] sections = obj.Sections.Where(section => section.Name == SectionName).ToArray();
        if (sections.Length == 0) return null;
        if (sections.Length != 1 || sections[0].Bytes.Count > MaximumBytes) throw new ElfFormatException("Invalid argument hint section");
        byte[] bytes = sections[0].Bytes.ToArray();
        try
        {
            using MemoryStream stream = new(bytes, writable: false);
            using BinaryReader reader = new(stream);
            if (reader.ReadUInt32() != Magic || reader.ReadInt32() != Version) throw new ElfFormatException("Unsupported argument hints");
            ArgumentHints hints = Read(reader, bytes.Length);
            if (stream.Position != bytes.Length) throw new ElfFormatException("Trailing argument hint data");
            return hints;
        }
        catch (EndOfStreamException) { throw new ElfFormatException("Truncated argument hints"); }
    }

    public byte[] Write()
    {
        using MemoryStream stream = new();
        using (BinaryWriter writer = new(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(Magic);
            writer.Write(Version);
            Write(writer);
        }
        return stream.ToArray();
    }

    /// <summary>Each function the unit defines, by its label.</summary>
    public SortedDictionary<string, ArgumentFunction> Functions { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Every function whose address the unit's code or data takes: one of
    /// these is called through a word by callers nobody names, and keeps
    /// its parameters as they are unless every call through its slot is
    /// known (a virtual one's, judged by the slot's whole family).
    /// </summary>
    public SortedSet<string> Addressed { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Every function a descriptor of the unit's holds in a slot: called
    /// through the slot by whatever calls the slot, which the link knows
    /// only where it knows every such call (the slot's virtual name, and
    /// the image closed).
    /// </summary>
    public SortedSet<string> Slotted { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Whether the unit makes a call through an address it could name by no
    /// function and no slot (Sys.Call of a word, a delegate's own target):
    /// such a call reaches any function whose address is taken, with
    /// whatever it passes.
    /// </summary>
    public bool Unnamed { get; set; }

    public bool IsEmpty => Functions.Count == 0 && Addressed.Count == 0 && Slotted.Count == 0 && !Unnamed;

    internal void Write(BinaryWriter writer)
    {
        void Text(string text)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            writer.Write(bytes.Length);
            writer.Write(bytes);
        }
        writer.Write(Functions.Count);
        foreach ((string name, ArgumentFunction f) in Functions)
        {
            Text(name);
            writer.Write((byte)((f.Fixed ? 1 : 0) | (f.Recursive ? 2 : 0) | (f.Copies ? 4 : 0)));
            writer.Write(f.Size);
            writer.Write(f.Kinds.Length);
            for (int k = 0; k < f.Kinds.Length; k++)
            {
                writer.Write((byte)f.Kinds[k]);
                writer.Write(f.Reads[k]);
                writer.Write(f.Folds[k]);
                writer.Write((byte)f.Shapes[k]);
            }
            writer.Write(f.Calls.Count);
            foreach (ArgumentCall call in f.Calls)
            {
                Text(call.Callee);
                writer.Write(call.Depth);
                writer.Write(call.Count);
                writer.Write(call.Arguments.Length);
                foreach (ArgumentValue value in call.Arguments)
                {
                    writer.Write((byte)value.Kind);
                    switch (value.Kind)
                    {
                        case ArgumentValueKind.Constant: writer.Write(value.Number); break;
                        case ArgumentValueKind.Address: Text(value.Symbol!); writer.Write(value.Number); break;
                        case ArgumentValueKind.Parameter: writer.Write((int)value.Number); break;
                    }
                }
            }
        }
        writer.Write(Addressed.Count);
        foreach (string name in Addressed) Text(name);
        writer.Write(Slotted.Count);
        foreach (string name in Slotted) Text(name);
        writer.Write(Unnamed);
    }

    internal static ArgumentHints Read(BinaryReader reader, long total)
    {
        Stream stream = reader.BaseStream;
        string Text()
        {
            int length = reader.ReadInt32();
            if (length < 1 || length > 16384 || length > total - stream.Position) throw new ElfFormatException("Invalid argument hint text");
            string text = Encoding.UTF8.GetString(reader.ReadBytes(length));
            if (text.Contains('\0')) throw new ElfFormatException("Invalid argument hint text");
            return text;
        }
        int Count(int each)
        {
            int count = reader.ReadInt32();
            if (count < 0 || count > (total - stream.Position) / each) throw new ElfFormatException("Invalid argument hint count");
            return count;
        }
        ArgumentHints hints = new();
        for (int i = Count(14); i > 0; i--)
        {
            string name = Text();
            byte flags = reader.ReadByte();
            if (flags > 7) throw new ElfFormatException("Invalid argument hint flags");
            int size = reader.ReadInt32();
            int parameters = Count(11);
            ArgumentFunction f = new(parameters) { Fixed = (flags & 1) != 0, Recursive = (flags & 2) != 0, Copies = (flags & 4) != 0, Size = size };
            for (int k = 0; k < parameters; k++)
            {
                byte kind = reader.ReadByte();
                if (kind > (byte)ArgumentKind.Float) throw new ElfFormatException("Invalid argument hint kind");
                f.Kinds[k] = (ArgumentKind)kind;
                f.Reads[k] = reader.ReadInt32();
                f.Folds[k] = reader.ReadInt32();
                byte shape = reader.ReadByte();
                if (shape > (byte)ArgumentShape.Written) throw new ElfFormatException("Invalid argument hint shape");
                f.Shapes[k] = (ArgumentShape)shape;
            }
            for (int c = Count(13); c > 0; c--)
            {
                string callee = Text();
                byte depth = reader.ReadByte();
                int sites = reader.ReadInt32();
                if (sites < 1) throw new ElfFormatException("Invalid argument hint call count");
                ArgumentValue[] arguments = new ArgumentValue[Count(1)];
                for (int a = 0; a < arguments.Length; a++)
                {
                    byte kind = reader.ReadByte();
                    arguments[a] = (ArgumentValueKind)kind switch
                    {
                        ArgumentValueKind.Unknown => default,
                        ArgumentValueKind.Constant => ArgumentValue.Of(reader.ReadInt64()),
                        ArgumentValueKind.Address => ArgumentValue.At(Text(), reader.ReadInt64()),
                        ArgumentValueKind.Parameter => ArgumentValue.Passed(reader.ReadInt32()),
                        _ => throw new ElfFormatException("Invalid argument hint value"),
                    };
                    if (arguments[a].Kind == ArgumentValueKind.Parameter && (arguments[a].Number < 0 || arguments[a].Number >= parameters))
                        throw new ElfFormatException("Invalid argument hint parameter");
                }
                f.Calls.Add(new ArgumentCall(callee, depth, sites, arguments));
            }
            if (!hints.Functions.TryAdd(name, f)) throw new ElfFormatException("Duplicate argument hint function");
        }
        for (int i = Count(5); i > 0; i--) hints.Addressed.Add(Text());
        for (int i = Count(5); i > 0; i--) hints.Slotted.Add(Text());
        hints.Unnamed = reader.ReadBoolean();
        return hints;
    }
}

/// <summary>What an argument is on the machine: a word, eight bytes, or a four-byte float (the x86 backend's RegisterArgs.Shape has the same three).</summary>
public enum ArgumentKind : byte { Word, Wide, Float }

/// <summary>What a function does with a parameter that a known value would change most.</summary>
public enum ArgumentShape : byte
{
    /// <summary>Nothing told apart: read, stored, passed on.</summary>
    Plain,
    /// <summary>Compared with a constant or tested, a branch taken on the answer: known, a way of the function goes.</summary>
    Compared,
    /// <summary>Written again inside the function: what a caller passed is not what every read of it finds.</summary>
    Written,
}

/// <summary>One function's side: its parameters and its calls.</summary>
public sealed class ArgumentFunction
{
    public ArgumentFunction(int parameters)
    {
        Kinds = new ArgumentKind[parameters];
        Reads = new int[parameters];
        Folds = new int[parameters];
        Shapes = new ArgumentShape[parameters];
    }

    /// <summary>
    /// Its parameters may not be changed whoever calls it: foreign code
    /// enters it (C's ways in, [StackArguments]), it pops its own
    /// arguments, it is an async method's or a state machine's, or the
    /// unit could not tell.
    /// </summary>
    public bool Fixed { get; set; }
    /// <summary>It calls itself, directly: a version of it would call the function, not the version.</summary>
    public bool Recursive { get; set; }
    /// <summary>
    /// Other units may define it too (a generic body compiled where it is
    /// used, merged at the link): what is taken from it is taken from every
    /// copy alike, but nothing new is made beside it under a name of its
    /// own -- each copy's unit would make one.
    /// </summary>
    public bool Copies { get; set; }
    /// <summary>Its instructions, as the unit had it when it said this.</summary>
    public int Size { get; set; }
    public ArgumentKind[] Kinds { get; }
    /// <summary>How many instructions read each parameter (0: none, the parameter is dead).</summary>
    public int[] Reads { get; }
    /// <summary>How many instructions the unit judges a known value of each would fold away.</summary>
    public int[] Folds { get; }
    public ArgumentShape[] Shapes { get; }
    /// <summary>Every call it makes by name -- a function's label, or a virtual slot's name (VirtualTargets.Prefix).</summary>
    public List<ArgumentCall> Calls { get; } = new();
}

/// <summary>A call: whom, how deep in loops, how many times the function makes it just so, and what stands in each argument's place.</summary>
public readonly record struct ArgumentCall(string Callee, byte Depth, int Count, ArgumentValue[] Arguments);

public enum ArgumentValueKind : byte { Unknown, Constant, Address, Parameter }

/// <summary>
/// What a call passes in one place: a number, a symbol's address (and an
/// offset from it), the caller's own parameter of that index handed on
/// unchanged, or nothing the unit can name.
/// </summary>
public readonly record struct ArgumentValue(ArgumentValueKind Kind, long Number, string? Symbol)
{
    public static ArgumentValue Of(long constant) => new(ArgumentValueKind.Constant, constant, null);
    public static ArgumentValue At(string symbol, long offset) => new(ArgumentValueKind.Address, offset, symbol);
    public static ArgumentValue Passed(int parameter) => new(ArgumentValueKind.Parameter, parameter, null);
    public bool Known => Kind is ArgumentValueKind.Constant or ArgumentValueKind.Address;
}
