using System.Buffers.Binary;
using System.Text;
using Corsac.Lang.Elf;
using Corsac.Lang.Ir;

namespace Corsac.Lang.Lto;

/// <summary>
/// WHAT ONE UNIT'S FUNCTIONS DO WITH POINTERS, for region inference over a
/// separately compiled program (RegionSolver). Each function's points-to
/// constraints as the unit's IR states them before its late passes -- the IR
/// the link runs those passes over again -- over nodes of its own: its
/// parameters, its return, the registers that can hold an address. Its
/// allocation sites are objects keyed by the function and their ordinal
/// among its allocator calls (RegionPointsTo.SiteCalls), which the link
/// numbers again the same way when it regenerates the unit; its frame slots
/// are objects too, and anything it cannot follow is the unknown object.
/// Calls are named: a function, a virtual call's symbol (Escape.
/// VirtualCallee), or nothing for a call nobody can name.
/// </summary>
public sealed class RegionHints
{
    public const string SectionName = ".corsac.regions";
    public const int MaximumBytes = 64 * 1024 * 1024;
    private const uint Magic = 0x47455243; // "CREG"
    private const int Version = 1;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public List<RegionFunction> Functions { get; } = new();
    /// <summary>Every symbol the unit names as a value, in code or in data: the functions among them may be called by anything.</summary>
    public SortedSet<string> AddressTaken { get; } = new(StringComparer.Ordinal);

    public void Attach(ObjectFile obj)
    {
        if (obj.Sections.Any(section => section.Name == SectionName)) throw new ElfFormatException("Duplicate region hints");
        Section section = new(SectionName, SectionKind.Note);
        section.Bytes.AddRange(Write());
        obj.Sections.Add(section);
    }

    public static RegionHints? Read(ObjectFile obj)
    {
        Section[] sections = obj.Sections.Where(section => section.Name == SectionName).ToArray();
        if (sections.Length == 0) return null;
        if (sections.Length != 1 || sections[0].Bytes.Count > MaximumBytes) throw new ElfFormatException("Invalid region hint section");
        return Read(sections[0].Bytes.ToArray());
    }

    public byte[] Write()
    {
        // Every name once, in ordinal order: the same hints are the same bytes.
        SortedSet<string> names = new(StringComparer.Ordinal);
        names.UnionWith(AddressTaken);
        foreach (RegionFunction function in Functions)
        {
            names.Add(function.Name);
            foreach (RegionCall call in function.Calls) if (call.Callee is not null) names.Add(call.Callee);
        }
        Dictionary<string, int> index = new(StringComparer.Ordinal);
        foreach (string name in names) index.Add(name, index.Count);
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream, Utf8, leaveOpen: true);
        writer.Write(Magic); writer.Write(Version); writer.Write(0); // length, filled below
        writer.Write(names.Count);
        foreach (string name in names)
        {
            byte[] bytes = Utf8.GetBytes(name);
            if (bytes.Length == 0 || bytes.Length > 16384 || name.Contains('\0')) throw new ElfFormatException("Invalid region hint name");
            writer.Write(bytes.Length); writer.Write(bytes);
        }
        writer.Write(AddressTaken.Count);
        foreach (string name in AddressTaken) writer.Write(index[name]);
        writer.Write(Functions.Count);
        foreach (RegionFunction function in Functions)
        {
            writer.Write(index[function.Name]);
            writer.Write((byte)((function.Global ? 1 : 0) | (function.MayBeBoundary ? 2 : 0)));
            writer.Write(function.Parameters); writer.Write(function.Nodes); writer.Write(function.Slots);
            writer.Write(function.Sites.Length);
            foreach (RegionSite site in function.Sites) { writer.Write(site.Rewritable); writer.Write(site.Line); }
            writer.Write(function.Constraints.Count);
            foreach (RegionConstraint c in function.Constraints) { writer.Write((byte)c.Kind); writer.Write(c.A); writer.Write(c.B); writer.Write(c.C); }
            writer.Write(function.Calls.Count);
            foreach (RegionCall call in function.Calls)
            {
                writer.Write(call.Callee is null ? -1 : index[call.Callee]);
                writer.Write(call.GraphOnly); writer.Write(call.Dest);
                writer.Write(call.Arguments.Length);
                foreach (int argument in call.Arguments) writer.Write(argument);
            }
        }
        writer.Flush();
        byte[] result = stream.ToArray();
        if (result.Length > MaximumBytes) throw new ElfFormatException("Region hints exceed their budget");
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(8), result.Length);
        return result;
    }

    public static RegionHints Read(byte[] bytes)
    {
        using MemoryStream stream = new(bytes, writable: false);
        using BinaryReader reader = new(stream, Utf8);
        try
        {
            if (reader.ReadUInt32() != Magic || reader.ReadInt32() != Version) throw new ElfFormatException("Unsupported region hints");
            if (reader.ReadInt32() != bytes.Length) throw new ElfFormatException("Invalid region hint length");
            int Count(int size)
            {
                int count = reader.ReadInt32();
                if (count < 0 || count > (bytes.Length - stream.Position) / size) throw new ElfFormatException("Invalid region hint count");
                return count;
            }
            string[] names = new string[Count(5)];
            for (int i = 0; i < names.Length; i++)
            {
                int length = reader.ReadInt32();
                if (length < 1 || length > 16384 || length > bytes.Length - stream.Position) throw new ElfFormatException("Invalid region hint name");
                string name = Utf8.GetString(reader.ReadBytes(length));
                if (name.Contains('\0') || i > 0 && string.CompareOrdinal(names[i - 1], name) >= 0) throw new ElfFormatException("Invalid region hint name table");
                // One string for each name however many units say it.
                names[i] = string.IsInterned(name) ?? string.Intern(name);
            }
            string Name()
            {
                int at = reader.ReadInt32();
                if (at < 0 || at >= names.Length) throw new ElfFormatException("Invalid region hint name index");
                return names[at];
            }
            RegionHints hints = new();
            for (int i = Count(4); i > 0; i--) hints.AddressTaken.Add(Name());
            for (int i = Count(22); i > 0; i--)
            {
                string name = Name();
                byte flags = reader.ReadByte();
                int parameters = reader.ReadInt32(), nodes = reader.ReadInt32(), slots = reader.ReadInt32();
                if (parameters < 0 || nodes <= parameters || nodes > RegionFunction.NodeLimit || slots < 0 || slots > RegionFunction.NodeLimit)
                    throw new ElfFormatException("Invalid region hint function");
                RegionSite[] sites = new RegionSite[Count(5)];
                for (int s = 0; s < sites.Length; s++) sites[s] = new RegionSite(reader.ReadBoolean(), reader.ReadInt32());
                RegionFunction function = new(name, (flags & 1) != 0, (flags & 2) != 0, parameters, nodes, slots, sites);
                bool Node(int n) => n >= 0 && n < nodes;
                for (int k = Count(17); k > 0; k--)
                {
                    RegionConstraint c = new((RegionConstraintKind)reader.ReadByte(), reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt64());
                    bool valid = c.Kind switch
                    {
                        RegionConstraintKind.Site => Node(c.A) && c.B >= 0 && c.B < sites.Length,
                        RegionConstraintKind.Slot => Node(c.A) && c.B >= 0 && c.B < slots,
                        RegionConstraintKind.Unknown or RegionConstraintKind.Leak => Node(c.A),
                        RegionConstraintKind.Copy or RegionConstraintKind.Load or RegionConstraintKind.Store or RegionConstraintKind.MemCopy => Node(c.A) && Node(c.B),
                        _ => false,
                    };
                    if (!valid) throw new ElfFormatException("Invalid region hint constraint");
                    function.Constraints.Add(c);
                }
                for (int k = Count(13); k > 0; k--)
                {
                    int callee = reader.ReadInt32();
                    string? named = callee == -1 ? null : callee >= 0 && callee < names.Length ? names[callee] : throw new ElfFormatException("Invalid region hint call");
                    bool graphOnly = reader.ReadBoolean();
                    int dest = reader.ReadInt32();
                    int[] arguments = new int[Count(4)];
                    for (int a = 0; a < arguments.Length; a++) arguments[a] = reader.ReadInt32();
                    if (dest < -1 || dest >= nodes || arguments.Any(a => a < -1 || a >= nodes)) throw new ElfFormatException("Invalid region hint call");
                    function.Calls.Add(new RegionCall(named, graphOnly, dest, arguments));
                }
                hints.Functions.Add(function);
            }
            if (stream.Position != bytes.Length) throw new ElfFormatException("Trailing region hint data");
            return hints;
        }
        catch (EndOfStreamException) { throw new ElfFormatException("Truncated region hints"); }
        catch (DecoderFallbackException) { throw new ElfFormatException("Invalid region hint UTF-8"); }
    }
}

/// <summary>
/// One function's constraints. Nodes 0 to Parameters-1 are its parameters,
/// node Parameters is what it returns, the rest its own.
/// </summary>
public sealed class RegionFunction
{
    /// <summary>No function states more nodes, or more frame slots, than this.</summary>
    public const int NodeLimit = 1 << 22;

    public RegionFunction(string name, bool global, bool mayBeBoundary, int parameters, int nodes, int slots, RegionSite[] sites)
    {
        Name = name; Global = global; MayBeBoundary = mayBeBoundary;
        Parameters = parameters; Nodes = nodes; Slots = slots; Sites = sites;
    }

    public string Name { get; }
    /// <summary>Exported: other units name it. Otherwise its name is its unit's own.</summary>
    public bool Global { get; }
    /// <summary>Not an async or iterator body (its frame outlives a return) nor a type's initialiser (run wherever first asked).</summary>
    public bool MayBeBoundary { get; }
    public int Parameters { get; }
    public int Nodes { get; }
    public int Slots { get; }
    /// <summary>Its allocator calls, by ordinal (RegionPointsTo.SiteCalls).</summary>
    public RegionSite[] Sites { get; }
    public List<RegionConstraint> Constraints { get; } = new();
    public List<RegionCall> Calls { get; } = new();
}

/// <summary>An allocation site: whether a region may take it (a collecting allocator's call), and its line for reports.</summary>
public readonly record struct RegionSite(bool Rewritable, int Line);

public enum RegionConstraintKind : byte
{
    /// <summary>A holds site B's object.</summary>
    Site,
    /// <summary>A holds frame slot B's address.</summary>
    Slot,
    /// <summary>A holds the unknown object: something nobody follows.</summary>
    Unknown,
    /// <summary>A holds what B holds, moved C bytes (Any: anywhere in its object).</summary>
    Copy,
    /// <summary>A holds what is read C bytes from where B points.</summary>
    Load,
    /// <summary>What B holds is written C bytes from where A points.</summary>
    Store,
    /// <summary>C bytes (Any: however many) copied from where B points to where A points.</summary>
    MemCopy,
    /// <summary>What A holds goes where nobody follows it (a throw).</summary>
    Leak,
}

/// <summary>One constraint over a function's nodes.</summary>
public readonly record struct RegionConstraint(RegionConstraintKind Kind, int A, int B, long C)
{
    /// <summary>A copy's shift, or a copy's count, when it is no constant.</summary>
    public const long Any = long.MinValue;
}

/// <summary>
/// A call: of a function or a virtual call's symbol, or of something nobody
/// can name (null). Its arguments' nodes in order (-1: none that can hold an
/// address) and its result's. A call only for the call graph (an allocator's,
/// the collector's notes) binds nothing.
/// </summary>
public sealed record RegionCall(string? Callee, bool GraphOnly, int Dest, int[] Arguments);

/// <summary>
/// The link's region answer for one unit (RegionSolver): the functions to
/// open a region on entry, and the allocation sites -- by function and
/// ordinal -- to make in the innermost open region (Runtime.AllocRegion).
/// </summary>
public sealed class RegionFacts
{
    public SortedSet<string> Boundaries { get; } = new(StringComparer.Ordinal);
    public SortedSet<(string Function, int Ordinal)> Sites { get; } = new(SiteOrder.Instance);
    public bool IsEmpty => Boundaries.Count == 0 && Sites.Count == 0;

    public sealed class SiteOrder : IComparer<(string Function, int Ordinal)>
    {
        public static readonly SiteOrder Instance = new();
        public int Compare((string Function, int Ordinal) x, (string Function, int Ordinal) y)
        {
            int first = string.CompareOrdinal(x.Function, y.Function);
            return first != 0 ? first : x.Ordinal.CompareTo(y.Ordinal);
        }
    }
}
