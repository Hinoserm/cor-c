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
/// among its allocator calls (RegionPointsTo.MarkSites), which the link
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
    private const int Version = 2;
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
            foreach (RegionSite site in function.Sites) if (site.Table is not null) names.Add(site.Table);
        }
        Dictionary<string, int> index = new(StringComparer.Ordinal);
        foreach (string name in names) index.Add(name, index.Count);
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream, Utf8, leaveOpen: true);
        // Numbers as seven bits a byte, signed ones zigzagged: most are small.
        void Var(long value)
        {
            ulong bits = (ulong)((value << 1) ^ (value >> 63));
            while (bits >= 0x80) { writer.Write((byte)(bits | 0x80)); bits >>= 7; }
            writer.Write((byte)bits);
        }
        writer.Write(Magic); writer.Write(Version); writer.Write(0); // length, filled below
        Var(names.Count);
        foreach (string name in names)
        {
            byte[] bytes = Utf8.GetBytes(name);
            if (bytes.Length == 0 || bytes.Length > 16384 || name.Contains('\0')) throw new ElfFormatException("Invalid region hint name");
            Var(bytes.Length); writer.Write(bytes);
        }
        Var(AddressTaken.Count);
        foreach (string name in AddressTaken) Var(index[name]);
        Var(Functions.Count);
        foreach (RegionFunction function in Functions)
        {
            Var(index[function.Name]);
            writer.Write((byte)((function.Global ? 1 : 0) | (function.MayBeBoundary ? 2 : 0) | (function.Instance ? 4 : 0)));
            Var(function.Parameters); Var(function.Nodes); Var(function.Slots);
            Var(function.Sites.Length);
            foreach (RegionSite site in function.Sites)
            {
                writer.Write((byte)((site.Rewritable ? 1 : 0) | (int)site.Words << 1)); Var(site.Line);
                Var(site.Table is null ? -1 : index[site.Table]); Var(site.At);
            }
            Var(function.Constraints.Count);
            foreach (RegionConstraint c in function.Constraints) { writer.Write((byte)c.Kind); Var(c.A); Var(c.B); Var(c.C); }
            Var(function.Calls.Count);
            foreach (RegionCall call in function.Calls)
            {
                Var(call.Callee is null ? -1 : index[call.Callee]);
                Var(call.Dest);
                Var(call.Arguments.Length);
                foreach (int argument in call.Arguments) Var(argument);
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
            long Var()
            {
                ulong bits = 0;
                for (int shift = 0; ; shift += 7)
                {
                    if (shift > 63) throw new ElfFormatException("Invalid region hint number");
                    byte b = reader.ReadByte();
                    bits |= (ulong)(b & 0x7F) << shift;
                    if (b < 0x80) break;
                }
                return (long)(bits >> 1) ^ -(long)(bits & 1);
            }
            int Int()
            {
                long value = Var();
                if (value < int.MinValue || value > int.MaxValue) throw new ElfFormatException("Invalid region hint number");
                return (int)value;
            }
            // Every element takes a byte at least: no count is more than what is left.
            int Count()
            {
                int count = Int();
                if (count < 0 || count > bytes.Length - stream.Position) throw new ElfFormatException("Invalid region hint count");
                return count;
            }
            string[] names = new string[Count()];
            for (int i = 0; i < names.Length; i++)
            {
                int length = Int();
                if (length < 1 || length > 16384 || length > bytes.Length - stream.Position) throw new ElfFormatException("Invalid region hint name");
                string name = Utf8.GetString(reader.ReadBytes(length));
                if (name.Contains('\0') || i > 0 && string.CompareOrdinal(names[i - 1], name) >= 0) throw new ElfFormatException("Invalid region hint name table");
                // One string for each name however many units say it.
                names[i] = string.IsInterned(name) ?? string.Intern(name);
            }
            string Name()
            {
                int at = Int();
                if (at < 0 || at >= names.Length) throw new ElfFormatException("Invalid region hint name index");
                return names[at];
            }
            RegionHints hints = new();
            for (int i = Count(); i > 0; i--) hints.AddressTaken.Add(Name());
            for (int i = Count(); i > 0; i--)
            {
                string name = Name();
                byte flags = reader.ReadByte();
                int parameters = Int(), nodes = Int(), slots = Int();
                if (parameters < 0 || nodes <= parameters || nodes > RegionFunction.NodeLimit || slots < 0 || slots > RegionFunction.NodeLimit)
                    throw new ElfFormatException("Invalid region hint function");
                RegionSite[] sites = new RegionSite[Count()];
                for (int s = 0; s < sites.Length; s++)
                {
                    byte bits = reader.ReadByte();
                    int line = Int(), table = Int();
                    long at = Var();
                    if (bits > 5 || table < -1 || table >= names.Length) throw new ElfFormatException("Invalid region hint stamp");
                    sites[s] = new RegionSite((bits & 1) != 0, line, table < 0 ? null : names[table], at, (RegionWords)(bits >> 1));
                }
                RegionFunction function = new(name, (flags & 1) != 0, (flags & 2) != 0, (flags & 4) != 0, parameters, nodes, slots, sites);
                bool Node(int n) => n >= 0 && n < nodes;
                for (int k = Count(); k > 0; k--)
                {
                    RegionConstraint c = new((RegionConstraintKind)reader.ReadByte(), Int(), Int(), Var());
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
                for (int k = Count(); k > 0; k--)
                {
                    int callee = Int();
                    string? named = callee == -1 ? null : callee >= 0 && callee < names.Length ? names[callee] : throw new ElfFormatException("Invalid region hint call");
                    int dest = Int();
                    int[] arguments = new int[Count()];
                    for (int a = 0; a < arguments.Length; a++) arguments[a] = Int();
                    if (dest < -1 || dest >= nodes || arguments.Any(a => a < -1 || a >= nodes)) throw new ElfFormatException("Invalid region hint call");
                    function.Calls.Add(new RegionCall(named, dest, arguments));
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

    public RegionFunction(string name, bool global, bool mayBeBoundary, bool instance, int parameters, int nodes, int slots, RegionSite[] sites)
    {
        Name = name; Global = global; MayBeBoundary = mayBeBoundary; Instance = instance;
        Parameters = parameters; Nodes = nodes; Slots = slots; Sites = sites;
    }

    public string Name { get; }
    /// <summary>Exported: other units name it. Otherwise its name is its unit's own.</summary>
    public bool Global { get; }
    /// <summary>Not an async or iterator body (its frame outlives a return) nor a type's initialiser (run wherever first asked).</summary>
    public bool MayBeBoundary { get; }
    /// <summary>An instance method: its first parameter the object it is called on, by which the link tells its calls apart.</summary>
    public bool Instance { get; }
    public int Parameters { get; }
    public int Nodes { get; }
    public int Slots { get; }
    /// <summary>Its allocator calls, by ordinal (RegionPointsTo.MarkSites).</summary>
    public RegionSite[] Sites { get; }
    public List<RegionConstraint> Constraints { get; } = new();
    public List<RegionCall> Calls { get; } = new();
}

/// <summary>
/// An allocation site: whether a region may take it (a collecting allocator's
/// call), its line for reports, and the descriptor its object is stamped
/// with and where in it the method table begins (null: none known) -- what
/// a virtual call made on the object runs; and how the collector reads its
/// words (Words), which says where no reference is ever kept.
/// </summary>
public readonly record struct RegionSite(bool Rewritable, int Line, string? Table, long At, RegionWords Words = RegionWords.Any);

/// <summary>How the collector reads an allocation's words (Gc.ScanBlockWithin): by the allocator its site calls.</summary>
public enum RegionWords : byte
{
    /// <summary>Any word may be a reference.</summary>
    Any,
    /// <summary>A leaf (AllocLeaf): never scanned, no word a reference.</summary>
    Leaf,
    /// <summary>Made with a descriptor (AllocObject): scanned by the descriptor it is stamped with.</summary>
    Described,
}

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
/// address) and its result's.
/// </summary>
public sealed record RegionCall(string? Callee, int Dest, int[] Arguments);

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
