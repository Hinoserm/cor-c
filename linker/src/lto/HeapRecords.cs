using System.Buffers.Binary;
using System.Text;
using Corsac.Lang.Elf;
using Corsac.Lang.Ir;

namespace Corsac.Lang.Lto;

/// <summary>
/// WHAT A UNIT LEAVES TO THE COLLECTOR, for the link to say whether the
/// program needs one at all (HeapDecision). A compile of the whole program
/// says it itself (Escape.CollectorSites): from the entry, over every call
/// and every address code or data names, it finds the allocations nothing
/// frees and that do not run just once or hold a table for the life of the
/// program. A unit sees only itself, so it writes down what that walk reads
/// of each of its functions, after its own ownership is decided -- its
/// calls, the addresses it names, the allocations it left to the collector
/// with what could still excuse them -- and the link walks every unit's at
/// once, as the compile walks one module.
/// </summary>
public sealed class HeapRecords
{
    public const string SectionName = ".corsac.heap";
    /// <summary>A unit's records never grow past this.</summary>
    public const int MaximumBytes = 64 * 1024 * 1024;
    private const uint Magic = 0x50414548; // "HEAP"
    private static readonly UTF8Encoding Utf8 = new(false, true);

    /// <summary>The program's own source calls into the collector (Module.CallsCollector): it has one.</summary>
    public bool CallsCollector { get; set; }
    /// <summary>The unit was compiled to run without a collector (--no-collector).</summary>
    public bool NoCollector { get; set; }
    public List<HeapFunction> Functions { get; } = new();
    /// <summary>The static fields the unit stores to outside a static initialiser (Escape.PermanentStatics).</summary>
    public SortedSet<string> StaticWrites { get; } = new(StringComparer.Ordinal);
    /// <summary>
    /// Per function of the unit's IR archive, as archived (before its late
    /// passes, as the archive's references are taken), the names it would
    /// not refer to without a collector (Escape.GoneWithoutCollector): the
    /// edges the link's reachability drops in a program that has none
    /// (HeapDecision.GoneEdges). Only a compile's records hold them.
    /// </summary>
    public SortedDictionary<string, string[]> Gone { get; } = new(StringComparer.Ordinal);

    public void Attach(ObjectFile obj) => Attach(obj, Write());

    public static void Attach(ObjectFile obj, byte[] written)
    {
        if (obj.Sections.Any(section => section.Name == SectionName)) throw new ElfFormatException("Duplicate heap records");
        Section section = new(SectionName, SectionKind.Note);
        section.Bytes.AddRange(written);
        obj.Sections.Add(section);
    }

    public byte[] Write()
    {
        // Every name once, in ordinal order: the same records are the same bytes.
        SortedSet<string> names = new(StringComparer.Ordinal);
        names.UnionWith(StaticWrites);
        foreach ((string function, string[] gone) in Gone) { names.Add(function); names.UnionWith(gone); }
        foreach (HeapFunction function in Functions)
        {
            names.Add(function.Name);
            names.UnionWith(function.LatchSets); names.UnionWith(function.LatchBreaks);
            foreach (HeapEvent e in function.Events)
            {
                names.Add(e.Name);
                names.UnionWith(e.Latches);
                if (e.Permanent is not null) names.Add(e.Permanent);
                if (e.Thrown is not null) names.Add(e.Thrown);
                if (e.Held is not null) names.Add(e.Held);
            }
        }
        Dictionary<string, int> index = new(StringComparer.Ordinal);
        foreach (string name in names) index.Add(name, index.Count);

        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream, Utf8, leaveOpen: true);
        writer.Write(Magic); writer.Write(0); // length, filled below
        writer.Write(names.Count);
        foreach (string name in names)
        {
            byte[] bytes = Utf8.GetBytes(name);
            if (bytes.Length == 0 || bytes.Length > 16384 || name.Contains('\0')) throw new ElfFormatException("Invalid heap record name");
            writer.Write(bytes.Length); writer.Write(bytes);
        }
        writer.Write(CallsCollector); writer.Write(NoCollector);
        Names(StaticWrites);
        writer.Write(Gone.Count);
        foreach ((string function, string[] gone) in Gone) { writer.Write(index[function]); Names(gone); }
        writer.Write(Functions.Count);
        foreach (HeapFunction function in Functions.OrderBy(f => f.Name, StringComparer.Ordinal))
        {
            writer.Write(index[function.Name]); writer.Write(function.Async); writer.Write(function.KeepsAwaited);
            Names(function.LatchSets); Names(function.LatchBreaks);
            writer.Write(function.Events.Count);
            foreach (HeapEvent e in function.Events)
            {
                writer.Write((byte)e.Kind); writer.Write((ushort)e.Flags); writer.Write(index[e.Name]);
                Names(e.Latches);
                writer.Write(e.Permanent is null ? -1 : index[e.Permanent]);
                writer.Write(e.Thrown is null ? -1 : index[e.Thrown]);
                writer.Write(e.Held is null ? -1 : index[e.Held]);
            }
        }
        void Names(IReadOnlyCollection<string> list)
        {
            writer.Write(list.Count);
            foreach (string name in list) writer.Write(index[name]);
        }
        writer.Flush();
        byte[] result = stream.ToArray();
        if (result.Length > MaximumBytes) throw new ElfFormatException("Heap records exceed their budget");
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(4), result.Length);
        return result;
    }

    public static HeapRecords? Read(ObjectFile obj)
    {
        Section[] sections = obj.Sections.Where(section => section.Name == SectionName).ToArray();
        if (sections.Length == 0) return null;
        if (sections.Length != 1 || sections[0].Size > MaximumBytes) throw new ElfFormatException("Invalid heap record section");
        using Stream stream = sections[0].OpenRead();
        return Read(stream, sections[0].Size);
    }

    public static HeapRecords Read(Stream stream, int total)
    {
        using BinaryReader reader = new(stream, Utf8);
        try
        {
            if (reader.ReadUInt32() != Magic) throw new ElfFormatException("Unsupported heap records");
            if (reader.ReadInt32() != total) throw new ElfFormatException("Invalid heap record length");
            int Count(int size)
            {
                int count = reader.ReadInt32();
                if (count < 0 || count > (total - stream.Position) / size) throw new ElfFormatException("Invalid heap record count");
                return count;
            }
            string[] names = new string[Count(5)];
            for (int i = 0; i < names.Length; i++)
            {
                int length = reader.ReadInt32();
                if (length < 1 || length > 16384 || length > total - stream.Position) throw new ElfFormatException("Invalid heap record name");
                names[i] = string.Intern(Utf8.GetString(reader.ReadBytes(length)));
                if (names[i].Contains('\0') || i > 0 && string.CompareOrdinal(names[i - 1], names[i]) >= 0)
                    throw new ElfFormatException("Invalid heap record name table");
            }
            string Name()
            {
                int at = reader.ReadInt32();
                if (at < 0 || at >= names.Length) throw new ElfFormatException("Invalid heap record name index");
                return names[at];
            }
            string? MaybeName()
            {
                int at = reader.ReadInt32();
                if (at == -1) return null;
                if (at < 0 || at >= names.Length) throw new ElfFormatException("Invalid heap record name index");
                return names[at];
            }
            string[] NameList()
            {
                // Most lists are empty: one array for all of them, not one each.
                int count = Count(4);
                if (count == 0) return Array.Empty<string>();
                string[] list = new string[count];
                for (int i = 0; i < list.Length; i++) list[i] = Name();
                return list;
            }
            List<string> latchBuffer = new();
            HeapRecords records = new() { CallsCollector = reader.ReadBoolean(), NoCollector = reader.ReadBoolean() };
            records.StaticWrites.UnionWith(NameList());
            for (int g = Count(8); g > 0; g--)
            {
                string function = Name();
                if (!records.Gone.TryAdd(function, NameList())) throw new ElfFormatException("Duplicate heap record");
            }
            for (int f = Count(14); f > 0; f--)
            {
                HeapFunction function = new(Name()) { Async = reader.ReadBoolean(), KeepsAwaited = reader.ReadBoolean() };
                function.LatchSets.AddRange(NameList()); function.LatchBreaks.AddRange(NameList());
                int events = Count(23);
                function.Events.Capacity = events;
                // A block's events are latched alike: the latches last read
                // are held once, and so is the rare part of an event that has
                // nothing else -- every such event after it shares both.
                string[] latches = Array.Empty<string>();
                HeapEvent? latchedOnly = null;
                for (int e = events; e > 0; e--)
                {
                    byte kind = reader.ReadByte();
                    if (kind > (byte)HeapEventKind.Store) throw new ElfFormatException("Invalid heap event");
                    HeapFlags flags = (HeapFlags)reader.ReadUInt16();
                    string name = Name();
                    latchBuffer.Clear();
                    for (int l = Count(4); l > 0; l--) latchBuffer.Add(Name());
                    if (!latchBuffer.SequenceEqual(latches)) { latches = latchBuffer.Count == 0 ? Array.Empty<string>() : latchBuffer.ToArray(); latchedOnly = null; }
                    string? permanent = MaybeName(), thrown = MaybeName(), held = MaybeName();
                    bool plain = permanent is null && thrown is null && held is null;
                    HeapEvent read = plain && latchedOnly is { } same ? same with { Kind = (HeapEventKind)kind, Name = name, Flags = flags }
                        : held is null ? new((HeapEventKind)kind, name, flags, latches, permanent, thrown)
                        : new((HeapEventKind)kind, name, flags, latches, permanent, thrown) { Held = held };
                    if (plain && latches.Length > 0) latchedOnly = read;
                    function.Events.Add(read);
                }
                records.Functions.Add(function);
            }
            if (stream.Position != total) throw new ElfFormatException("Heap records have trailing bytes");
            return records;
        }
        catch (EndOfStreamException) { throw new ElfFormatException("Truncated heap records"); }
    }
}

/// <summary>
/// One function's part of the walk (Escape.CollectorSites, OnceRun): its
/// events in block order, and what the whole program's answers need of it --
/// the latch flags it sets only to true and those it touches any other way.
/// </summary>
public sealed class HeapFunction
{
    public HeapFunction(string name) => Name = name;
    public string Name { get; }

    /// <summary>An async body or an iterator's MoveNext: run a step at a time, as often as it is resumed, so never just once.</summary>
    public bool Async { get; set; }

    /// <summary>
    /// An async body, whose catch-all keeps in its Task what its body and its
    /// awaits throw, as often as they do: the throw budget's to count. An
    /// iterator's handler ends only the stop signal, which leaves nothing
    /// behind (Escape.RepeatingCatches).
    /// </summary>
    public bool KeepsAwaited { get; set; }
    public List<string> LatchSets { get; } = new();
    public List<string> LatchBreaks { get; } = new();
    public List<HeapEvent> Events { get; } = new();
}

public enum HeapEventKind : byte
{
    /// <summary>A call by name: Opcode.Call, or an allocation the unit left to the collector (Site).</summary>
    Call,
    /// <summary>A symbol named as an operand: an address taken, a static read or written.</summary>
    Named,
    /// <summary>A target of an indirect call the unit resolved.</summary>
    Target,
    /// <summary>A virtual call by its slot's name (Escape.VirtualCallee), for the link to resolve; "*" when the unit could not name it.</summary>
    Virtual,
    /// <summary>The end of a catch of the type named ("*" for all), in its block: the throw budget asks whether it runs more than once.</summary>
    Catch,
    /// <summary>A store into the field named that may replace a value nobody frees (Escape.StoreMayReplace).</summary>
    Store,
}

[Flags]
public enum HeapFlags : ushort
{
    None = 0,
    /// <summary>In a block on no cycle of its function (Escape.Repeating).</summary>
    Once = 1,
    /// <summary>In a block that ends in a trap: the program dying, which decides nothing.</summary>
    Trapping = 2,
    /// <summary>What the function hands back fresh: an allocation of it, or a call whose result it hands on as its own.</summary>
    Origin = 4,
    /// <summary>A call whose fresh result this function frees (Escape._ownedCalls).</summary>
    OwnedResult = 8,
    /// <summary>An allocation this function owns: not a site, and no call the walk follows.</summary>
    OwnedAllocation = 16,
    /// <summary>An allocation left to the collector: a site, unless the whole program excuses it.</summary>
    Site = 32,
    /// <summary>A direct call (Opcode.Call), which OnceRun counts against its callee.</summary>
    Direct = 64,
    /// <summary>
    /// In a trapping block, from its last call on where that call ends the
    /// program (Escape.DyingFrom), and what follows it. The walk does not
    /// follow it (Escape.CollectorSites).
    /// </summary>
    Dying = 128,
    /// <summary>A Store into what is being thrown (Escape.IntoThrown): bounded with the unwinds.</summary>
    AtThrow = 256,
}

/// <summary>
/// One call, named address or site, catch or store. A site's Permanent is the
/// static field it is stored to in a static initialiser, on no cycle
/// (Escape.PermanentStatics, which holds where nothing else writes the field);
/// Thrown, the type it is thrown as ("*" where not known): made on the way to
/// one of boundedly many unwinds, where the throw budget holds. Latches are the flags whose latch lets the
/// event's block through once (OnceRun.LatchedBlocks). Held, the field a
/// fresh object the event makes or hands back is stored into, going nowhere
/// else: the field's owner frees it where the whole program finds the field
/// owns what it holds (Escape.OwnedFields' held pairs).
///
/// A VALUE, AND SMALL: the kind, the flags and the name are every event's,
/// and the rest -- latches, Permanent, Thrown, Held -- is held apart, and
/// only where an event has any of it: a block's events share their latches
/// (HeapRecords.Read), and the compiler's own link holds 23 thousand rare
/// parts for its 2.7 million events. The heap decision holds every unit's
/// events at once; each an object of seven fields, they were 173 MB, and
/// the phase's live heap went from 1347 MB to 1255 MB.
/// </summary>
public readonly record struct HeapEvent
{
    private sealed class Rare(string[] latches, string? permanent, string? thrown, string? held)
    {
        public string[] Latches { get; } = latches;
        public string? Permanent { get; } = permanent;
        public string? Thrown { get; } = thrown;
        public string? Held { get; } = held;
    }
    private readonly Rare? _rare;

    public HeapEvent(HeapEventKind Kind, string Name, HeapFlags Flags, string[] Latches, string? Permanent, string? Thrown)
    {
        this.Kind = Kind;
        this.Name = Name;
        this.Flags = Flags;
        _rare = Of(Latches, Permanent, Thrown, null);
    }

    public HeapEventKind Kind { get; init; }
    public string Name { get; init; }
    public HeapFlags Flags { get; init; }
    public string[] Latches { get => _rare?.Latches ?? Array.Empty<string>(); init => _rare = Of(value, Permanent, Thrown, Held); }
    public string? Permanent { get => _rare?.Permanent; init => _rare = Of(Latches, value, Thrown, Held); }
    public string? Thrown { get => _rare?.Thrown; init => _rare = Of(Latches, Permanent, value, Held); }
    public string? Held { get => _rare?.Held; init => _rare = Of(Latches, Permanent, Thrown, value); }

    private static Rare? Of(string[] latches, string? permanent, string? thrown, string? held)
        => latches.Length == 0 && permanent is null && thrown is null && held is null ? null : new(latches, permanent, thrown, held);
}
