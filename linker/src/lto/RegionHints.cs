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
/// VirtualCallee), or nothing for a call nobody can name. And its loops that
/// may be given a region of their own, with what RegionPointsTo's "loops"
/// judge them by, over the same nodes (RegionLoopShape).
/// </summary>
public sealed class RegionHints
{
    public const string SectionName = ".corsac.regions";
    public const int MaximumBytes = 64 * 1024 * 1024;
    private const uint Magic = 0x47455243; // "CREG"
    // 6: a function's number parameters (NumberParams) and how often each
    // of its sites and calls runs, with the unit's word size (Repeats).
    // 7: the symbols whose addresses its code takes (Symbols), after its
    // sites, and the Symbol constraint naming them.
    // 8: an async or iterator body states its state machine's stores
    // (what its registers and slots hold across a suspension), rather
    // than leaking every one: the same bytes, another meaning.
    // 9: the field a load or a store names (RegionConstraint.Family), by the
    // function's Families, after its symbols; each Load and Store states it.
    // 10: the functions only a descriptor's method slots name (MethodsTaken),
    // apart from every other address taken, and whether the unit calls a
    // method it read from a descriptor as no named call (CallsThroughMethods),
    // after the addresses taken.
    // 11: the slots each such function reads its method at (BlindSlots),
    // after its families, so the link calls blind only the methods some
    // descriptor holds at one of them.
    private const int Version = 11;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public List<RegionFunction> Functions { get; } = new();
    /// <summary>The bytes of the unit's target's word, which the arena's blocks are laid out by (RegionLayout).</summary>
    public int WordSize { get; set; } = 4;
    /// <summary>Every symbol the unit names as a value, in code or in data, but in a descriptor's method slots: the functions among them may be called by anything.</summary>
    public SortedSet<string> AddressTaken { get; } = new(StringComparer.Ordinal);
    /// <summary>
    /// The functions the unit's descriptors name in their method slots
    /// (Target.DescriptorBytes on): reached by a virtual call, which the link
    /// follows to them, and by anything else only through a method read out
    /// of a descriptor and called as no virtual call is (CallsThroughMethods),
    /// or a virtual call the link cannot resolve.
    /// </summary>
    public SortedSet<string> MethodsTaken { get; } = new(StringComparer.Ordinal);
    /// <summary>Whether some function of the unit calls a method read out of a descriptor by a call that names no virtual target (RegionSummary).</summary>
    public bool CallsThroughMethods { get; set; }

    public void Attach(ObjectFile obj) => Attach(obj, Write());

    /// <summary>
    /// Hints already written (Write), attached as Attach would: a compile
    /// writes them when they are made and keeps the bytes, not the hints,
    /// until its object is put together (Driver.Compile).
    /// </summary>
    public static void Attach(ObjectFile obj, byte[] written)
    {
        if (obj.Sections.Any(section => section.Name == SectionName)) throw new ElfFormatException("Duplicate region hints");
        Section section = new(SectionName, SectionKind.Note);
        section.Bytes.AddRange(written);
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
        names.UnionWith(MethodsTaken);
        foreach (RegionFunction function in Functions)
        {
            names.Add(function.Name);
            foreach (RegionCall call in function.Calls) if (call.Callee is not null) names.Add(call.Callee);
            foreach (RegionSite site in function.Sites) if (site.Table is not null) names.Add(site.Table);
            names.UnionWith(function.Symbols);
            names.UnionWith(function.Families);
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
        Var(WordSize);
        Var(names.Count);
        foreach (string name in names)
        {
            byte[] bytes = Utf8.GetBytes(name);
            if (bytes.Length == 0 || bytes.Length > 16384 || name.Contains('\0')) throw new ElfFormatException("Invalid region hint name");
            Var(bytes.Length); writer.Write(bytes);
        }
        Var(AddressTaken.Count);
        foreach (string name in AddressTaken) Var(index[name]);
        Var(MethodsTaken.Count);
        foreach (string name in MethodsTaken) Var(index[name]);
        writer.Write((byte)(CallsThroughMethods ? 1 : 0));
        Var(Functions.Count);
        foreach (RegionFunction function in Functions)
        {
            Var(index[function.Name]);
            writer.Write((byte)((function.Global ? 1 : 0) | (function.MayBeBoundary ? 2 : 0) | (function.Instance ? 4 : 0) | (function.Main ? 8 : 0)
                | (function.CallsThroughMethod ? 16 : 0)));
            Var(function.Parameters); Var(function.Nodes); Var(function.Slots);
            Var(function.NumberParams.Length);
            foreach (int k in function.NumberParams) Var(k);
            Var(function.Sites.Length);
            foreach (RegionSite site in function.Sites)
            {
                writer.Write((byte)((site.Rewritable ? 1 : 0) | (int)site.Words << 1)); Var(site.Line);
                Var(site.Table is null ? -1 : index[site.Table]); Var(site.At);
            }
            Var(function.Symbols.Length);
            foreach (string symbol in function.Symbols) Var(index[symbol]);
            Var(function.Families.Length);
            foreach (string family in function.Families) Var(index[family]);
            if (function.CallsThroughMethod)
            {
                Var(function.BlindSlots.Length);
                foreach (long slot in function.BlindSlots) Var(slot);
            }
            Var(function.Constraints.Count);
            foreach (RegionConstraint c in function.Constraints)
            {
                writer.Write((byte)c.Kind); Var(c.A); Var(c.B); Var(c.C);
                if (c.Kind is RegionConstraintKind.Load or RegionConstraintKind.Store) Var(c.Family);
            }
            Var(function.Calls.Count);
            foreach (RegionCall call in function.Calls)
            {
                Var(call.Callee is null ? -1 : index[call.Callee]);
                Var(call.Dest);
                Var(call.Arguments.Length);
                foreach (int argument in call.Arguments) Var(argument);
            }
            void Ints(int[] values)
            {
                Var(values.Length);
                foreach (int value in values) Var(value);
            }
            Ints(function.MustCalls); Ints(function.MustSites);
            Var(function.Loops.Count);
            foreach (RegionLoopShape loop in function.Loops)
            {
                Var(loop.Header);
                Ints(loop.Sites); Ints(loop.Calls); Ints(loop.AlwaysSites); Ints(loop.AlwaysCalls);
                Ints(loop.Live); Ints(loop.Invariant); Ints(loop.KeptSlots);
            }
            // How often each site and call runs in one call of the function.
            Var(function.Repeats.Length);
            foreach (RegionRepeat repeat in function.Repeats) { Var(repeat.Header); Var(repeat.Parent); Var(repeat.Trip); }
            for (int s = 0; s < function.Sites.Length; s++) { Var(function.BytesOf(s)); Var(function.LoopOfSite(s)); }
            for (int k = 0; k < function.Calls.Count; k++) Var(function.LoopOfCall(k));
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
        return Read(stream, bytes.Length);
    }

    /// <summary>The hints read from `total` bytes of `stream`, from its start (Section.OpenRead).</summary>
    public static RegionHints Read(Stream stream, int total)
    {
        using BinaryReader reader = new(stream, Utf8);
        try
        {
            if (reader.ReadUInt32() != Magic || reader.ReadInt32() != Version) throw new ElfFormatException("Unsupported region hints");
            if (reader.ReadInt32() != total) throw new ElfFormatException("Invalid region hint length");
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
                if (count < 0 || count > total - stream.Position) throw new ElfFormatException("Invalid region hint count");
                return count;
            }
            int wordSize = Int();
            if (wordSize is not (4 or 8)) throw new ElfFormatException("Invalid region hint word size");
            string[] names = new string[Count()];
            for (int i = 0; i < names.Length; i++)
            {
                int length = Int();
                if (length < 1 || length > 16384 || length > total - stream.Position) throw new ElfFormatException("Invalid region hint name");
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
            RegionHints hints = new() { WordSize = wordSize };
            for (int i = Count(); i > 0; i--) hints.AddressTaken.Add(Name());
            for (int i = Count(); i > 0; i--) hints.MethodsTaken.Add(Name());
            byte calls = reader.ReadByte();
            if (calls > 1) throw new ElfFormatException("Invalid region hint flags");
            hints.CallsThroughMethods = calls == 1;
            for (int i = Count(); i > 0; i--)
            {
                string name = Name();
                byte flags = reader.ReadByte();
                int parameters = Int(), nodes = Int(), slots = Int();
                if (parameters < 0 || nodes <= parameters || nodes > RegionFunction.NodeLimit || slots < 0 || slots > RegionFunction.NodeLimit)
                    throw new ElfFormatException("Invalid region hint function");
                // Each a parameter, in order, once.
                int[] numbers = new int[Count()];
                for (int k = 0; k < numbers.Length; k++)
                    if ((numbers[k] = Int()) < 0 || numbers[k] >= parameters || k > 0 && numbers[k] <= numbers[k - 1])
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
                string[] symbols = new string[Count()];
                for (int s = 0; s < symbols.Length; s++) symbols[s] = Name();
                string[] families = new string[Count()];
                for (int s = 0; s < families.Length; s++) families[s] = Name();
                // In order, each once; Any (every slot) first where it is one.
                long[] blindSlots = Array.Empty<long>();
                if ((flags & 16) != 0)
                {
                    blindSlots = new long[Count()];
                    for (int k = 0; k < blindSlots.Length; k++)
                        if ((blindSlots[k] = Var()) < 0 && blindSlots[k] != RegionConstraint.Any || k > 0 && blindSlots[k] <= blindSlots[k - 1])
                            throw new ElfFormatException("Invalid region hint function");
                }
                RegionFunction function = new(name, (flags & 1) != 0, (flags & 2) != 0, (flags & 4) != 0, parameters, nodes, slots, sites)
                    { Main = (flags & 8) != 0, CallsThroughMethod = (flags & 16) != 0, NumberParams = numbers, Symbols = symbols, Families = families, BlindSlots = blindSlots };
                bool Node(int n) => n >= 0 && n < nodes;
                for (int k = Count(); k > 0; k--)
                {
                    RegionConstraint c = new((RegionConstraintKind)reader.ReadByte(), Int(), Int(), Var());
                    if (c.Kind is RegionConstraintKind.Load or RegionConstraintKind.Store)
                    {
                        int family = Int();
                        if (family < -1 || family >= families.Length) throw new ElfFormatException("Invalid region hint constraint");
                        c = c with { Family = family };
                    }
                    bool valid = c.Kind switch
                    {
                        RegionConstraintKind.Site => Node(c.A) && c.B >= 0 && c.B < sites.Length,
                        RegionConstraintKind.Slot => Node(c.A) && c.B >= 0 && c.B < slots,
                        RegionConstraintKind.Unknown or RegionConstraintKind.Leak => Node(c.A),
                        RegionConstraintKind.Symbol => Node(c.A) && c.B >= 0 && c.B < symbols.Length,
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
                // Indices each below its bound: a call's, a site's, a node's, a slot's.
                int[] Ints(int bound)
                {
                    int[] values = new int[Count()];
                    for (int k = 0; k < values.Length; k++)
                        if ((values[k] = Int()) < 0 || values[k] >= bound) throw new ElfFormatException("Invalid region hint loop");
                    return values;
                }
                function.MustCalls = Ints(function.Calls.Count);
                function.MustSites = Ints(sites.Length);
                for (int k = Count(); k > 0; k--)
                {
                    int header = Int();
                    if (header < 0) throw new ElfFormatException("Invalid region hint loop");
                    function.Loops.Add(new RegionLoopShape(header, Ints(sites.Length), Ints(function.Calls.Count), Ints(sites.Length), Ints(function.Calls.Count),
                        Ints(nodes), Ints(nodes), Ints(slots)));
                }
                // Each loop's parent before it; each site and call in one of them, or none, or past knowing.
                RegionRepeat[] repeats = new RegionRepeat[Count()];
                for (int k = 0; k < repeats.Length; k++)
                {
                    RegionRepeat r = new(Int(), Int(), Var());
                    if (r.Header < 0 || r.Parent < -1 || r.Parent >= k || r.Trip < 0) throw new ElfFormatException("Invalid region hint repeat");
                    repeats[k] = r;
                }
                bool InLoop(int at) => at >= RegionFunction.Throwing && at < repeats.Length;
                long[] siteBytes = new long[sites.Length];
                int[] siteLoops = new int[sites.Length];
                for (int s = 0; s < sites.Length; s++)
                    if ((siteBytes[s] = Var()) < 0 || !InLoop(siteLoops[s] = Int())) throw new ElfFormatException("Invalid region hint repeat");
                int[] callLoops = new int[function.Calls.Count];
                for (int k = 0; k < callLoops.Length; k++)
                    if (!InLoop(callLoops[k] = Int())) throw new ElfFormatException("Invalid region hint repeat");
                function.Repeats = repeats; function.SiteBytes = siteBytes; function.SiteLoops = siteLoops; function.CallLoops = callLoops;
                hints.Functions.Add(function);
            }
            if (stream.Position != total) throw new ElfFormatException("Trailing region hint data");
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
    /// <summary>Not an async or iterator body (its frame outlives a return), a type's initialiser (run wherever first asked) or Main (run for the whole run).</summary>
    public bool MayBeBoundary { get; }
    /// <summary>An instance method: its first parameter the object it is called on, by which the link tells its calls apart.</summary>
    public bool Instance { get; }
    /// <summary>The program's Main (Module.Main): never a boundary, and what the entry calls besides it is the entry's setup.</summary>
    public bool Main { get; init; }
    /// <summary>Calls a method it read out of a descriptor by a call naming no virtual target (RegionHints.CallsThroughMethods), for a report.</summary>
    public bool CallsThroughMethod { get; init; }
    /// <summary>
    /// WHERE IN A METHOD TABLE SUCH A CALL READS ITS METHOD: the offsets from
    /// where an object's first word points, in order, each once
    /// (RegionConstraint.Any, first: some offset nobody can say). Only a
    /// method some descriptor holds at one of them is called so
    /// (RegionSolver.Addressed); a function that is no such caller reads none.
    /// </summary>
    public long[] BlindSlots { get; init; } = Array.Empty<long>();
    public int Parameters { get; }
    public int Nodes { get; }
    public int Slots { get; }
    /// <summary>
    /// Its parameters of a number type, in order: an int, a char, a double,
    /// an enum held in thirty-two bits (RegionSummary). What a caller hands
    /// one is never an address, so nothing it holds is handed on. A long, a
    /// nint, a pointer, a struct or a type parameter may be one, and is not
    /// named here.
    /// </summary>
    public int[] NumberParams { get; init; } = Array.Empty<int>();
    public bool IsNumber(int k) => NumberParams.Length > 0 && Array.BinarySearch(NumberParams, k) >= 0;
    /// <summary>Its allocator calls, by ordinal (RegionPointsTo.MarkSites).</summary>
    public RegionSite[] Sites { get; }
    public List<RegionConstraint> Constraints { get; } = new();
    public List<RegionCall> Calls { get; } = new();
    /// <summary>Its calls, by their place in Calls, and its sites, by ordinal, made on every way to a return (RegionPointsTo.MustRun).</summary>
    public int[] MustCalls { get; set; } = Array.Empty<int>();
    public int[] MustSites { get; set; } = Array.Empty<int>();
    /// <summary>Its loops that may be given a region: none in an async or iterator body, a type's initialiser, or a function with a landing pad or a label's address.</summary>
    public List<RegionLoopShape> Loops { get; } = new();

    /// <summary>
    /// HOW OFTEN EACH SITE AND CALL RUNS in one call of the function
    /// (RegionSummary.Repeats): its natural loops, each with the loop it is
    /// in (an earlier one, -1 for none) and the most laps it makes each time
    /// it is entered (0: not known); and per site and per call, the innermost
    /// loop it is in, -1 for none, Unbounded where nothing bounds how often
    /// it runs (a cycle that is no natural loop, a handler's way back), and
    /// Throwing where it runs only on the way to a throw.
    /// </summary>
    public RegionRepeat[] Repeats { get; set; } = Array.Empty<RegionRepeat>();
    /// <summary>Per site: the bytes its block takes in the arena (RegionLayout.Block), 0 when its size is not a constant.</summary>
    public long[] SiteBytes { get; set; } = Array.Empty<long>();
    public int[] SiteLoops { get; set; } = Array.Empty<int>();
    public int[] CallLoops { get; set; } = Array.Empty<int>();
    public const int Unbounded = -2, Throwing = -3;
    public long BytesOf(int site) => site < SiteBytes.Length ? SiteBytes[site] : 0;
    public int LoopOfSite(int site) => site < SiteLoops.Length ? SiteLoops[site] : Unbounded;
    public int LoopOfCall(int call) => call < CallLoops.Length ? CallLoops[call] : Unbounded;
    /// <summary>The symbols whose addresses its code takes, named by its Symbol constraints.</summary>
    public string[] Symbols { get; init; } = Array.Empty<string>();
    /// <summary>The fields its typed loads and stores name (RegionConstraint.Family).</summary>
    public string[] Families { get; set; } = Array.Empty<string>();
    /// <summary>Whether the link has made every Symbol constraint left one of a constant (RegionConstants): never written into the hints.</summary>
    public bool ConstantsKnown { get; set; }
}

/// <summary>A natural loop of a function, for how often what is in it runs: its header's place, the loop it is in (-1: none), the most laps it makes a time it is entered (0: not known).</summary>
public readonly record struct RegionRepeat(int Header, int Parent, long Trip);

/// <summary>
/// THE ARENA'S BLOCKS, as Gc lays them out ("regions"): a header of two
/// words and a footer of one round every block, its size rounded to eight
/// and never below four words; a region's record is a block of two words.
/// What the link sizes a region by (RegionSolver.Sizes) and the runtime
/// lays it down by must agree, or a region sized too small grows as an
/// unsized one does.
/// </summary>
public static class RegionLayout
{
    public static long Block(long payload, int wordSize)
    {
        long need = (payload + 3L * wordSize + 7) & -8L;
        return Math.Max(need, 4L * wordSize);
    }

    public static long Record(int wordSize) => Block(2L * wordSize, wordSize);
}

/// <summary>
/// A NATURAL LOOP OF A FUNCTION, as RegionPointsTo.LoopShape states it: its
/// header's place among the function's blocks (Block.Order over the IR the
/// link regenerates the unit from, which names the loop to it); the sites,
/// by ordinal, and calls, by their place in the function's Calls, in its
/// body, and those of them in blocks every lap that goes round runs; the
/// nodes live where a lap ends; those live into the header that the body
/// never writes; and the frame slots, by index, kept from before the loop.
/// </summary>
public sealed record RegionLoopShape(int Header, int[] Sites, int[] Calls, int[] AlwaysSites, int[] AlwaysCalls, int[] Live, int[] Invariant, int[] KeptSlots);

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
    /// <summary>
    /// A holds the address of the function's symbol B (RegionFunction.Symbols):
    /// the unknown object, unless the link finds it a constant. The link
    /// makes each one that is not Unknown (RegionConstants); one left names
    /// a constant, where RegionFunction.ConstantsKnown says so.
    /// </summary>
    Symbol,
}

/// <summary>
/// One constraint over a function's nodes. A Load or a Store may name the
/// field it reads or writes (Family, an index into RegionFunction.Families;
/// -1 for none): a field of a class, read or written at its offset from the
/// start of an object of that class, as C# compiles a field access -- never
/// a struct's field, an element, a raw read or write of memory (Sys.Peek
/// and Poke, a pointer), or a copy. Two accesses naming different fields at
/// one offset are never of one object in a type-safe program (a class's
/// fields lie after all its bases' at offsets of their own), which the
/// escape engine's aliasing of what places are written relies on
/// (RegionEscape.Aliased).
/// </summary>
public readonly record struct RegionConstraint(RegionConstraintKind Kind, int A, int B, long C, int Family = -1)
{
    /// <summary>A copy's shift, or a copy's count, when it is no constant.</summary>
    public const long Any = long.MinValue;
    /// <summary>
    /// A copy's shift when B is an index scaled into an address
    /// (RegionPointsTo.IndexShift): anywhere in its object, as Any, and
    /// never the unknown object.
    /// </summary>
    public const long Index = long.MinValue + 1;
    /// <summary>The largest k an index scaled by 2^k is told by (Strided element words, k at most 6).</summary>
    public const int MostScale = 6;
    /// <summary>An index scaled by 2^k: Index + k (k of 0, a scale not known).</summary>
    public static long IndexScaled(int k) => Index + k;
    public static bool IsIndex(long c) => c >= Index && c <= Index + MostScale;
    /// <summary>
    /// The address such an index is added to: moved by a multiple of 2^k, to
    /// a word at a residue of 2^k in its object -- an element's word in an
    /// array of structs -- the unknown object kept.
    /// </summary>
    public const long MovedBy = Index + 64;
    public static long MovedByScaled(int k) => MovedBy + k;
    public static bool IsMovedBy(long c) => c > MovedBy && c <= MovedBy + MostScale;
}

/// <summary>
/// A call: of a function or a virtual call's symbol, or of something nobody
/// can name (null). Its arguments' nodes in order (-1: none that can hold an
/// address) and its result's.
/// </summary>
public sealed record RegionCall(string? Callee, int Dest, int[] Arguments);

/// <summary>
/// The link's region answer for one unit (RegionSolver): the functions to
/// open a region on entry, the allocation sites -- by function and ordinal
/// -- to make in the innermost open region (Runtime.AllocRegion), and the
/// loops -- by function and their header's place in its blocks -- whose laps
/// each get a region (Runtime.RegionLoop). With a boundary or a loop, where
/// the link proved it, the most bytes its region holds in one call or one
/// lap (RegionSolver.Sizes); one not named is not known.
/// </summary>
public sealed class RegionFacts
{
    public SortedSet<string> Boundaries { get; } = new(StringComparer.Ordinal);
    public SortedSet<(string Function, int Ordinal)> Sites { get; } = new(SiteOrder.Instance);
    public SortedSet<(string Function, int Header)> Loops { get; } = new(SiteOrder.Instance);
    public SortedDictionary<string, long> BoundaryBytes { get; } = new(StringComparer.Ordinal);
    public SortedDictionary<(string Function, int Header), long> LoopBytes { get; } = new(SiteOrder.Instance);
    public bool IsEmpty => Boundaries.Count == 0 && Sites.Count == 0 && Loops.Count == 0;

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
