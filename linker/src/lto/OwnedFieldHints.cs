using System.Text;
using Corsac.Lang.Elf;

namespace Corsac.Lang.Lto;

/// <summary>
/// WHAT ONE UNIT KNOWS OF THE FIELDS THAT OWN WHAT THEY HOLD (Escape's
/// OwnedFields), for the link to decide over every unit together. A field
/// owns its objects only if every store into it, anywhere, stores an object
/// made for it, and every read of it, anywhere, keeps the value within and
/// is dead before anything that may replace or free it. A unit judges its own
/// stores and reads; what depends on other units it writes down instead:
/// conditions on their functions (fresh results, parameters that keep
/// nothing), parameters every caller must hand over (sinks), the calls a
/// read is live across, and what its functions call and store, from which
/// the link finds every function that may replace a field.
/// </summary>
public sealed class OwnedFieldHints
{
    /// <summary>A danger set or a list of callers never grows past this: past it, the field is refused.</summary>
    public const int Limit = 1024;

    /// <summary>Each instance field the unit stores or reads.</summary>
    public SortedDictionary<string, OwnedFieldRecord> Fields { get; } = new(StringComparer.Ordinal);
    /// <summary>Each function with calls, stores or borrowed reads to say.</summary>
    public SortedDictionary<string, OwnedFunctionRecord> Functions { get; } = new(StringComparer.Ordinal);
    /// <summary>
    /// The unit's reads of what a call returns, by callee (a function, or a
    /// virtual call's symbol): if the callee hands back what an owned field
    /// holds, each is a read of that field.
    /// </summary>
    public SortedDictionary<string, OwnedCallRead> CallReads { get; } = new(StringComparer.Ordinal);
    /// <summary>
    /// Callees every direct call here passes, first, an object just made --
    /// or the caller's own first argument, for the callers named: those
    /// callees store into new objects when they store into their first
    /// argument, if the named callers do too.
    /// </summary>
    public SortedDictionary<string, SortedSet<string>> FreshFirst { get; } = new(StringComparer.Ordinal);
    /// <summary>
    /// Arguments every direct call here hands over only if something holds
    /// of other units: a fresh object another unit's function makes, or the
    /// caller's own parameter, itself to be a sink. An argument every call
    /// hands over outright (null, static data, an object made here and not
    /// used again) is in neither this nor <see cref="Kept"/>.
    /// </summary>
    public SortedDictionary<(string Callee, int Argument), OwnedSink> Sinks { get; } = new(PairOrder.Instance);
    /// <summary>Arguments some direct call here does not hand over.</summary>
    public SortedSet<(string Callee, int Argument)> Kept { get; } = new(PairOrder.Instance);
    /// <summary>Functions whose address the unit takes, in code or in data other than a descriptor's.</summary>
    public SortedSet<string> Addressed { get; } = new(StringComparer.Ordinal);
    /// <summary>Functions whose address the unit's code takes.</summary>
    public SortedSet<string> CodeNamed { get; } = new(StringComparer.Ordinal);
    /// <summary>Functions a descriptor of the unit's holds in a slot.</summary>
    public SortedSet<string> Slotted { get; } = new(StringComparer.Ordinal);
    /// <summary>A virtual call the unit could not name by declaring type and slot.</summary>
    public bool UnresolvedVirtual { get; set; }

    // ---- elements owned through a field (Opt.OwnedElements) ----

    /// <summary>Every field the unit reads, or takes the address of: anything but a store.</summary>
    public SortedSet<string> Loaded { get; } = new(StringComparer.Ordinal);
    /// <summary>
    /// The fields every read of which here is proved, as a collection of a
    /// kind ("List", "Dictionary"), to keep the field's value alive past every
    /// use of an element it answered, letting none go -- if what it names of
    /// other units' functions holds.
    /// </summary>
    public SortedDictionary<string, OwnedElementRecord> ElementReads { get; } = new(StringComparer.Ordinal);
    /// <summary>The fields a collection of a kind, whose elements are its own if the condition holds, is handed to here.</summary>
    public SortedDictionary<string, OwnedElementRecord> ElementHandOffs { get; } = new(StringComparer.Ordinal);
    /// <summary>Another unit's parameters such a collection is handed to here: the field is the one that callee stores it into.</summary>
    public SortedDictionary<(string Callee, int Argument), OwnedElementRecord> ElementCalls { get; } = new(PairOrder.Instance);
    /// <summary>The unit's own parameters stored into a field and put to no other use, with the field.</summary>
    public SortedDictionary<(string Callee, int Argument), string> StoredParameters { get; } = new(PairOrder.Instance);
    /// <summary>The fields whose reads here were kept from the inliner for this rule: the link's answer lets them go again.</summary>
    public SortedSet<string> ElementKept { get; } = new(StringComparer.Ordinal);

    public sealed class PairOrder : IComparer<(string, int)>
    {
        public static readonly PairOrder Instance = new();
        public int Compare((string, int) x, (string, int) y)
        {
            int first = string.CompareOrdinal(x.Item1, y.Item1);
            return first != 0 ? first : x.Item2.CompareTo(y.Item2);
        }
    }

    /// <summary>Every virtual call symbol named here: the link resolves them.</summary>
    public IEnumerable<string> VirtualNames()
    {
        static bool Virtual(string name) => name.StartsWith(VirtualTargets.Prefix, StringComparison.Ordinal);
        foreach (OwnedFunctionRecord function in Functions.Values) foreach (string call in function.Calls) if (Virtual(call)) yield return call;
        foreach ((string callee, OwnedCallRead read) in CallReads)
        {
            if (Virtual(callee)) yield return callee;
            foreach (string danger in read.Danger) if (Virtual(danger)) yield return danger;
        }
        foreach (OwnedFieldRecord field in Fields.Values) foreach (string danger in field.Danger) if (Virtual(danger)) yield return danger;
    }

    internal void Write(BinaryWriter writer)
    {
        SortedSet<string> names = new(StringComparer.Ordinal);
        void Condition(LifetimeCondition c) { foreach (var s in c.Stays) names.Add(s.Callee); names.UnionWith(c.Fresh); foreach (var s in c.Fields) names.Add(s.Callee); }
        foreach ((string field, OwnedFieldRecord record) in Fields)
        {
            names.Add(field); Condition(record.Needs);
            foreach (var sink in record.Sinks) names.Add(sink.Callee);
            names.UnionWith(record.Danger); names.UnionWith(record.Kinds); names.UnionWith(record.Assumes);
        }
        foreach ((string name, OwnedFunctionRecord record) in Functions)
        {
            names.Add(name); names.UnionWith(record.Calls); names.UnionWith(record.Writes);
            names.UnionWith(record.InitWrites); names.UnionWith(record.Borrows);
        }
        foreach ((string callee, OwnedCallRead read) in CallReads)
        {
            names.Add(callee); Condition(read.Needs); names.UnionWith(read.Danger); names.UnionWith(read.DangerFields); names.UnionWith(read.ReturnedBy);
        }
        foreach ((string callee, SortedSet<string> callers) in FreshFirst) { names.Add(callee); names.UnionWith(callers); }
        foreach (((string callee, int _), OwnedSink sink) in Sinks)
        {
            names.Add(callee); Condition(sink.Needs);
            foreach (var inner in sink.Sinks) names.Add(inner.Callee);
        }
        foreach ((string callee, int _) in Kept) names.Add(callee);
        names.UnionWith(Addressed); names.UnionWith(CodeNamed); names.UnionWith(Slotted);
        names.UnionWith(Loaded); names.UnionWith(ElementKept);
        foreach ((string field, OwnedElementRecord record) in ElementReads) { names.Add(field); names.Add(record.Kind); Condition(record.Needs); }
        foreach ((string field, OwnedElementRecord record) in ElementHandOffs) { names.Add(field); names.Add(record.Kind); Condition(record.Needs); }
        foreach (((string callee, int _), OwnedElementRecord record) in ElementCalls) { names.Add(callee); names.Add(record.Kind); Condition(record.Needs); }
        foreach (((string callee, int _), string field) in StoredParameters) { names.Add(callee); names.Add(field); }
        Dictionary<string, int> index = new(StringComparer.Ordinal);
        writer.Write(names.Count);
        foreach (string name in names)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(name);
            if (bytes.Length == 0 || bytes.Length > 16384 || name.Contains('\0')) throw new ElfFormatException("Invalid owned-field hint name");
            writer.Write(bytes.Length); writer.Write(bytes);
            index.Add(name, index.Count);
        }
        void Names(IEnumerable<string> set, int count) { writer.Write(count); foreach (string name in set) writer.Write(index[name]); }
        void Pairs(SortedSet<(string Callee, int Argument)> set) { writer.Write(set.Count); foreach ((string c, int a) in set) { writer.Write(index[c]); writer.Write(a); } }
        void Needs(LifetimeCondition c)
        {
            if (c.Count > LifetimeCondition.Limit) throw new ElfFormatException("Owned-field condition exceeds its bound");
            Pairs(c.Stays); Names(c.Fresh, c.Fresh.Count); Pairs(c.Fields);
        }
        writer.Write(Fields.Count);
        foreach ((string field, OwnedFieldRecord record) in Fields)
        {
            writer.Write(index[field]); writer.Write(record.Offset); writer.Write(record.Refused); writer.Write(record.Stored);
            Needs(record.Needs); Pairs(record.Sinks); Names(record.Danger, record.Danger.Count);
            Names(record.Kinds, record.Kinds.Count); Names(record.Assumes, record.Assumes.Count);
        }
        writer.Write(Functions.Count);
        foreach ((string name, OwnedFunctionRecord record) in Functions)
        {
            writer.Write(index[name]);
            Names(record.Calls, record.Calls.Length); Names(record.Writes, record.Writes.Length);
            Names(record.InitWrites, record.InitWrites.Length); Names(record.Borrows, record.Borrows.Length);
        }
        writer.Write(CallReads.Count);
        foreach ((string callee, OwnedCallRead read) in CallReads)
        {
            writer.Write(index[callee]); writer.Write(read.Refused); Needs(read.Needs);
            Names(read.Danger, read.Danger.Count); Names(read.DangerFields, read.DangerFields.Count); Names(read.ReturnedBy, read.ReturnedBy.Count);
        }
        writer.Write(FreshFirst.Count);
        foreach ((string callee, SortedSet<string> callers) in FreshFirst) { writer.Write(index[callee]); Names(callers, callers.Count); }
        writer.Write(Sinks.Count);
        foreach (((string callee, int argument), OwnedSink sink) in Sinks)
        {
            writer.Write(index[callee]); writer.Write(argument); Needs(sink.Needs); Pairs(sink.Sinks);
        }
        Pairs(Kept);
        Names(Addressed, Addressed.Count); Names(CodeNamed, CodeNamed.Count); Names(Slotted, Slotted.Count);
        writer.Write(UnresolvedVirtual);
        Names(Loaded, Loaded.Count); Names(ElementKept, ElementKept.Count);
        void Map(SortedDictionary<string, OwnedElementRecord> map)
        { writer.Write(map.Count); foreach ((string key, OwnedElementRecord value) in map) { writer.Write(index[key]); writer.Write(index[value.Kind]); Needs(value.Needs); } }
        void PairMap(SortedDictionary<(string, int), OwnedElementRecord> map)
        { writer.Write(map.Count); foreach (((string callee, int argument), OwnedElementRecord value) in map) { writer.Write(index[callee]); writer.Write(argument); writer.Write(index[value.Kind]); Needs(value.Needs); } }
        Map(ElementReads); Map(ElementHandOffs); PairMap(ElementCalls);
        writer.Write(StoredParameters.Count);
        foreach (((string callee, int argument), string field) in StoredParameters) { writer.Write(index[callee]); writer.Write(argument); writer.Write(index[field]); }
    }

    internal static OwnedFieldHints Read(BinaryReader reader, long length)
    {
        Stream stream = reader.BaseStream;
        int Count(int size)
        {
            int count = reader.ReadInt32();
            if (count < 0 || count > (length - stream.Position) / size) throw new ElfFormatException("Invalid owned-field hint count");
            return count;
        }
        string[] names = new string[Count(5)];
        for (int i = 0; i < names.Length; i++)
        {
            int size = reader.ReadInt32();
            if (size < 1 || size > 16384 || size > length - stream.Position) throw new ElfFormatException("Invalid owned-field hint name");
            // One string for each name however many units say it: the link
            // holds every unit's hints at once.
            string name = Encoding.UTF8.GetString(reader.ReadBytes(size));
            if (name.Contains('\0') || i > 0 && string.CompareOrdinal(names[i - 1], name) >= 0) throw new ElfFormatException("Invalid owned-field hint names");
            names[i] = string.IsInterned(name) ?? string.Intern(name);
        }
        string Name()
        {
            int at = reader.ReadInt32();
            if (at < 0 || at >= names.Length) throw new ElfFormatException("Invalid owned-field hint name index");
            return names[at];
        }
        string[] NameArray() { string[] result = new string[Count(4)]; for (int i = 0; i < result.Length; i++) result[i] = Name(); return result; }
        void NameSet(SortedSet<string> into) { foreach (string name in NameArray()) if (!into.Add(name)) throw new ElfFormatException("Duplicate owned-field hint name"); }
        void Pairs(SortedSet<(string, int)> into)
        {
            for (int i = Count(8); i > 0; i--)
                if (!into.Add((Name(), reader.ReadInt32()))) throw new ElfFormatException("Duplicate owned-field hint pair");
        }
        LifetimeCondition Needs()
        {
            LifetimeCondition c = new();
            Pairs(c.Stays); NameSet(c.Fresh); Pairs(c.Fields);
            if (c.Count > LifetimeCondition.Limit) throw new ElfFormatException("Owned-field condition exceeds its bound");
            return c;
        }
        OwnedFieldHints hints = new();
        for (int i = Count(14); i > 0; i--)
        {
            string field = Name();
            OwnedFieldRecord record = new() { Offset = reader.ReadInt64(), Refused = reader.ReadBoolean(), Stored = reader.ReadBoolean() };
            record.Needs.Add(Needs()); Pairs(record.Sinks); NameSet(record.Danger); NameSet(record.Kinds); NameSet(record.Assumes);
            if (!hints.Fields.TryAdd(field, record)) throw new ElfFormatException("Duplicate owned-field record");
        }
        for (int i = Count(20); i > 0; i--)
        {
            string name = Name();
            OwnedFunctionRecord record = new(NameArray(), NameArray(), NameArray(), NameArray());
            if (!hints.Functions.TryAdd(name, record)) throw new ElfFormatException("Duplicate owned-field function");
        }
        for (int i = Count(9); i > 0; i--)
        {
            string callee = Name();
            OwnedCallRead read = new() { Refused = reader.ReadBoolean() };
            read.Needs.Add(Needs()); NameSet(read.Danger); NameSet(read.DangerFields); NameSet(read.ReturnedBy);
            if (!hints.CallReads.TryAdd(callee, read)) throw new ElfFormatException("Duplicate owned-field call read");
        }
        for (int i = Count(8); i > 0; i--)
        {
            string callee = Name();
            SortedSet<string> callers = new(StringComparer.Ordinal);
            NameSet(callers);
            if (!hints.FreshFirst.TryAdd(callee, callers)) throw new ElfFormatException("Duplicate owned-field constructor record");
        }
        for (int i = Count(8); i > 0; i--)
        {
            string callee = Name(); int argument = reader.ReadInt32();
            OwnedSink sink = new();
            sink.Needs.Add(Needs()); Pairs(sink.Sinks);
            if (argument < 0 || !hints.Sinks.TryAdd((callee, argument), sink)) throw new ElfFormatException("Invalid owned-field sink");
        }
        Pairs(hints.Kept);
        NameSet(hints.Addressed); NameSet(hints.CodeNamed); NameSet(hints.Slotted);
        hints.UnresolvedVirtual = reader.ReadBoolean();
        NameSet(hints.Loaded); NameSet(hints.ElementKept);
        OwnedElementRecord Element()
        {
            OwnedElementRecord record = new(Name());
            record.Needs.Add(Needs());
            return record;
        }
        void Map(SortedDictionary<string, OwnedElementRecord> into)
        {
            for (int i = Count(20); i > 0; i--)
                if (!into.TryAdd(Name(), Element())) throw new ElfFormatException("Duplicate owned-elements hint");
        }
        void PairMap(SortedDictionary<(string, int), OwnedElementRecord> into)
        {
            for (int i = Count(24); i > 0; i--)
            {
                string callee = Name(); int argument = reader.ReadInt32();
                if (argument < 0 || !into.TryAdd((callee, argument), Element())) throw new ElfFormatException("Invalid owned-elements hint");
            }
        }
        Map(hints.ElementReads); Map(hints.ElementHandOffs); PairMap(hints.ElementCalls);
        for (int i = Count(12); i > 0; i--)
        {
            string callee = Name(); int argument = reader.ReadInt32();
            if (argument < 0 || !hints.StoredParameters.TryAdd((callee, argument), Name())) throw new ElfFormatException("Invalid owned-elements hint");
        }
        return hints;
    }
}

/// <summary>A field as one unit found it: refused outright, or owned if what it names holds.</summary>
public sealed class OwnedFieldRecord
{
    /// <summary>Its byte offset in the object.</summary>
    public long Offset { get; set; }
    /// <summary>A store or read here settles it: not owned, whatever the other units do.</summary>
    public bool Refused { get; set; }
    /// <summary>Stored into here.</summary>
    public bool Stored { get; set; }
    /// <summary>What its stores and reads need of other units' functions.</summary>
    public LifetimeCondition Needs { get; } = new();
    /// <summary>Parameters it is stored from, which every caller must hand over.</summary>
    public SortedSet<(string Callee, int Argument)> Sinks { get; } = new(OwnedFieldHints.PairOrder.Instance);
    /// <summary>The calls a read of it is live across: none may replace it.</summary>
    public SortedSet<string> Danger { get; } = new(StringComparer.Ordinal);
    /// <summary>
    /// What the unit's stores put in it, by the stamp of the object each
    /// makes for it (`t_List$0024int+48`), or <see cref="UnknownKind"/> for
    /// anything else; null stores say nothing.
    /// </summary>
    public SortedSet<string> Kinds { get; } = new(StringComparer.Ordinal);
    /// <summary>
    /// The one stamp the unit's reads were judged as holding (a virtual call
    /// on what is read reaching that type's method alone): owned only if
    /// every unit's stores put nothing else in it.
    /// </summary>
    public SortedSet<string> Assumes { get; } = new(StringComparer.Ordinal);

    /// <summary>A store of something whose type the unit cannot name.</summary>
    public const string UnknownKind = "?";
}

/// <summary>
/// What one function calls (directly, and virtual calls by their symbol), the
/// fields it stores into (Writes) and into its own first argument
/// (InitWrites: not a replacement if every call hands it a new object), and
/// the fields whose value it hands back (Borrows).
/// </summary>
public sealed record OwnedFunctionRecord(string[] Calls, string[] Writes, string[] InitWrites, string[] Borrows);

/// <summary>A unit's reads of what one callee returns, together.</summary>
public sealed class OwnedCallRead
{
    /// <summary>Some read lets it go, or lives across a free: no field the callee hands back is owned.</summary>
    public bool Refused { get; set; }
    public LifetimeCondition Needs { get; } = new();
    /// <summary>The calls a read is live across.</summary>
    public SortedSet<string> Danger { get; } = new(StringComparer.Ordinal);
    /// <summary>The fields stored into while a read is live.</summary>
    public SortedSet<string> DangerFields { get; } = new(StringComparer.Ordinal);
    /// <summary>The functions that hand it back again: they hand back whatever the callee does.</summary>
    public SortedSet<string> ReturnedBy { get; } = new(StringComparer.Ordinal);
}

/// <summary>A collection's kind, as a unit found it of a field (OwnedFieldHints.ElementReads and the rest), and what that needs of other units.</summary>
public sealed class OwnedElementRecord
{
    public OwnedElementRecord(string kind) { Kind = kind; }
    public string Kind { get; }
    public LifetimeCondition Needs { get; } = new();
}

/// <summary>An argument every call of one unit hands over, if what it names holds.</summary>
public sealed class OwnedSink
{
    public LifetimeCondition Needs { get; } = new();
    /// <summary>The callers' own parameters it passes on, each of which must be a sink too.</summary>
    public SortedSet<(string Callee, int Argument)> Sinks { get; } = new(OwnedFieldHints.PairOrder.Instance);
}
