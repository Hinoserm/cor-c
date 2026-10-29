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
    /// <summary>Arguments some direct call here does not hand over, past the bound on <see cref="KeptIn"/>: every program's.</summary>
    public SortedSet<(string Callee, int Argument)> Kept { get; } = new(PairOrder.Instance);
    /// <summary>
    /// Arguments a direct call here does not hand over, by the functions
    /// making such a call: kept only in a program that runs one of them.
    /// </summary>
    public SortedDictionary<(string Callee, int Argument), SortedSet<string>> KeptIn { get; } = new(PairOrder.Instance);
    /// <summary>Functions whose address the unit takes, in code or in data other than a descriptor's.</summary>
    public SortedSet<string> Addressed { get; } = new(StringComparer.Ordinal);
    /// <summary>Functions whose address the unit's code takes.</summary>
    public SortedSet<string> CodeNamed { get; } = new(StringComparer.Ordinal);
    /// <summary>Functions a descriptor of the unit's holds in a slot.</summary>
    public SortedSet<string> Slotted { get; } = new(StringComparer.Ordinal);
    /// <summary>
    /// The functions with a virtual call the unit could not name by declaring
    /// type and slot: one the program runs could call any slotted function.
    /// </summary>
    public SortedSet<string> UnresolvedIn { get; } = new(StringComparer.Ordinal);

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
        foreach (OwnedFieldRecord field in Fields.Values)
            foreach (string danger in field.Danger.Concat(field.ElementDanger)) if (Virtual(danger)) yield return danger;
    }

    internal void Write(BinaryWriter writer)
    {
        SortedSet<string> names = new(StringComparer.Ordinal);
        void Condition(LifetimeCondition c) { foreach (var s in c.Stays) names.Add(s.Callee); names.UnionWith(c.Fresh); foreach (var s in c.Fields) names.Add(s.Callee); }
        foreach ((string field, OwnedFieldRecord record) in Fields)
        {
            names.Add(field); Sites(record.Sites); names.UnionWith(record.RefusedBy);
            foreach (var sink in record.Sinks) names.Add(sink.Callee);
            Condition(record.ElementNeeds); names.UnionWith(record.ElementDanger); names.UnionWith(record.ElementDangerFields);
        }
        foreach ((string name, OwnedFunctionRecord record) in Functions)
        {
            names.Add(name); names.UnionWith(record.Calls); names.UnionWith(record.Writes);
            names.UnionWith(record.InitWrites); names.UnionWith(record.Borrows);
        }
        foreach ((string callee, OwnedCallRead read) in CallReads)
        {
            names.Add(callee); Sites(read.Sites); names.UnionWith(read.RefusedBy); names.UnionWith(read.ReturnedBy);
        }
        foreach ((string callee, SortedSet<string> callers) in FreshFirst) { names.Add(callee); names.UnionWith(callers); }
        foreach (((string callee, int _), OwnedSink sink) in Sinks)
        {
            names.Add(callee);
            foreach ((string caller, LifetimeCondition needs) in sink.NeedsIn) { names.Add(caller); Condition(needs); }
            foreach (var inner in sink.Sinks) names.Add(inner.Callee);
        }
        foreach ((string callee, int _) in Kept) names.Add(callee);
        foreach (((string callee, int _), SortedSet<string> callers) in KeptIn) { names.Add(callee); names.UnionWith(callers); }
        void Sites(SortedDictionary<string, OwnedSite> sites)
        {
            foreach ((string function, OwnedSite site) in sites)
            {
                names.Add(function); Condition(site.Needs); names.UnionWith(site.Danger); names.UnionWith(site.DangerFields);
                names.UnionWith(site.Kinds); names.UnionWith(site.Assumes);
            }
        }
        names.UnionWith(Addressed); names.UnionWith(CodeNamed); names.UnionWith(Slotted); names.UnionWith(UnresolvedIn);
        names.UnionWith(Loaded); names.UnionWith(ElementKept);
        foreach ((string field, OwnedElementRecord record) in ElementReads) { names.Add(field); ElementNames(record); }
        foreach ((string field, OwnedElementRecord record) in ElementHandOffs) { names.Add(field); ElementNames(record); }
        foreach (((string callee, int _), OwnedElementRecord record) in ElementCalls) { names.Add(callee); ElementNames(record); }
        void ElementNames(OwnedElementRecord record) { names.Add(record.Kind); Condition(record.Needs); names.UnionWith(record.Accessors); names.UnionWith(record.Fixed); }
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
        void Pairs(IReadOnlyCollection<(string Callee, int Argument)> set) { writer.Write(set.Count); foreach ((string c, int a) in set) { writer.Write(index[c]); writer.Write(a); } }
        void Needs(LifetimeCondition c)
        {
            if (c.Count > LifetimeCondition.Limit) throw new ElfFormatException("Owned-field condition exceeds its bound");
            Pairs(c.Stays); Names(c.Fresh, c.Fresh.Count); Pairs(c.Fields);
        }
        writer.Write(Fields.Count);
        foreach ((string field, OwnedFieldRecord record) in Fields)
        {
            writer.Write(index[field]); writer.Write(record.Offset); writer.Write(record.Refused); writer.Write(record.Stored); writer.Write(record.SelfFreed);
            Names(record.RefusedBy, record.RefusedBy.Count); WriteSites(record.Sites); Pairs(record.Sinks);
            writer.Write(record.ElementsRefused); writer.Write(record.ElementArrays); Needs(record.ElementNeeds);
            Names(record.ElementDanger, record.ElementDanger.Count); Names(record.ElementDangerFields, record.ElementDangerFields.Count);
        }
        void WriteSites(SortedDictionary<string, OwnedSite> sites)
        {
            writer.Write(sites.Count);
            foreach ((string function, OwnedSite site) in sites)
            {
                writer.Write(index[function]); Needs(site.Needs); Names(site.Danger, site.Danger.Count); Names(site.DangerFields, site.DangerFields.Count);
                Names(site.Kinds, site.Kinds.Count); Names(site.Assumes, site.Assumes.Count);
            }
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
            writer.Write(index[callee]); writer.Write(read.Refused); Names(read.RefusedBy, read.RefusedBy.Count); WriteSites(read.Sites);
            Names(read.ReturnedBy, read.ReturnedBy.Count);
        }
        writer.Write(FreshFirst.Count);
        foreach ((string callee, SortedSet<string> callers) in FreshFirst) { writer.Write(index[callee]); Names(callers, callers.Count); }
        writer.Write(Sinks.Count);
        foreach (((string callee, int argument), OwnedSink sink) in Sinks)
        {
            writer.Write(index[callee]); writer.Write(argument);
            writer.Write(sink.NeedsIn.Count);
            foreach ((string caller, LifetimeCondition needs) in sink.NeedsIn) { writer.Write(index[caller]); Needs(needs); }
            Pairs(sink.Sinks);
        }
        Pairs(Kept);
        writer.Write(KeptIn.Count);
        foreach (((string callee, int argument), SortedSet<string> callers) in KeptIn) { writer.Write(index[callee]); writer.Write(argument); Names(callers, callers.Count); }
        Names(Addressed, Addressed.Count); Names(CodeNamed, CodeNamed.Count); Names(Slotted, Slotted.Count);
        Names(UnresolvedIn, UnresolvedIn.Count);
        Names(Loaded, Loaded.Count); Names(ElementKept, ElementKept.Count);
        void Element(OwnedElementRecord value)
        {
            writer.Write(index[value.Kind]); Needs(value.Needs);
            Names(value.Accessors, value.Accessors.Count); Names(value.Fixed, value.Fixed.Count);
        }
        void Map(SortedDictionary<string, OwnedElementRecord> map)
        { writer.Write(map.Count); foreach ((string key, OwnedElementRecord value) in map) { writer.Write(index[key]); Element(value); } }
        void PairMap(SortedDictionary<(string, int), OwnedElementRecord> map)
        { writer.Write(map.Count); foreach (((string callee, int argument), OwnedElementRecord value) in map) { writer.Write(index[callee]); writer.Write(argument); Element(value); } }
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
        string[] NameArray()
        {
            int count = Count(4);
            if (count == 0) return Array.Empty<string>();
            string[] result = new string[count];
            for (int i = 0; i < result.Length; i++) result[i] = Name();
            return result;
        }
        void NameSet(SortedSet<string> into) => NamesInto(into.Add);
        void NamesInto(Func<string, bool> add) { foreach (string name in NameArray()) if (!add(name)) throw new ElfFormatException("Duplicate owned-field hint name"); }
        void Pairs(SortedSet<(string, int)> into) => PairsInto(into.Add);
        void PairsInto(Func<(string, int), bool> add)
        {
            for (int i = Count(8); i > 0; i--)
                if (!add((Name(), reader.ReadInt32()))) throw new ElfFormatException("Duplicate owned-field hint pair");
        }
        LifetimeCondition Needs()
        {
            LifetimeCondition c = new();
            PairsInto(c.Stays.Add); NamesInto(c.Fresh.Add); PairsInto(c.Fields.Add);
            if (c.Count > LifetimeCondition.Limit) throw new ElfFormatException("Owned-field condition exceeds its bound");
            return c;
        }
        // Each function's site, and the unit's sums of them, which its own
        // bounds were kept on (OwnedFieldRecord.Needs and the rest).
        void ReadSites(SortedDictionary<string, OwnedSite> into, LifetimeCondition needs, SmallSortedSet<string> danger, SmallSortedSet<string>? dangerFields,
            SmallSortedSet<string>? kinds, SmallSortedSet<string>? assumes)
        {
            for (int k = Count(24); k > 0; k--)
            {
                string function = Name();
                OwnedSite site = new();
                site.Needs.Add(Needs()); NamesInto(site.Danger.Add); NamesInto(site.DangerFields.Add); NamesInto(site.Kinds.Add); NamesInto(site.Assumes.Add);
                if (!into.TryAdd(function, site)) throw new ElfFormatException("Duplicate owned-field site");
                needs.Add(site.Needs); danger.UnionWith(site.Danger); dangerFields?.UnionWith(site.DangerFields);
                kinds?.UnionWith(site.Kinds); assumes?.UnionWith(site.Assumes);
            }
        }
        OwnedFieldHints hints = new();
        for (int i = Count(32); i > 0; i--)
        {
            string field = Name();
            OwnedFieldRecord record = new() { Offset = reader.ReadInt64(), Refused = reader.ReadBoolean(), Stored = reader.ReadBoolean(), SelfFreed = reader.ReadBoolean() };
            NamesInto(record.RefusedBy.Add); ReadSites(record.Sites, record.Needs, record.Danger, null, record.Kinds, record.Assumes); Pairs(record.Sinks);
            record.ElementsRefused = reader.ReadBoolean(); record.ElementArrays = reader.ReadBoolean();
            record.ElementNeeds.Add(Needs()); NamesInto(record.ElementDanger.Add); NamesInto(record.ElementDangerFields.Add);
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
            NamesInto(read.RefusedBy.Add); ReadSites(read.Sites, read.Needs, read.Danger, read.DangerFields, null, null); NamesInto(read.ReturnedBy.Add);
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
            for (int k = Count(4); k > 0; k--)
            {
                string caller = Name();
                LifetimeCondition needs = Needs();
                if (!sink.NeedsIn.TryAdd(caller, needs)) throw new ElfFormatException("Duplicate owned-field sink caller");
                sink.Needs.Add(needs);
            }
            Pairs(sink.Sinks);
            if (argument < 0 || !hints.Sinks.TryAdd((callee, argument), sink)) throw new ElfFormatException("Invalid owned-field sink");
        }
        Pairs(hints.Kept);
        for (int i = Count(12); i > 0; i--)
        {
            string callee = Name(); int argument = reader.ReadInt32();
            SortedSet<string> callers = new(StringComparer.Ordinal);
            NameSet(callers);
            if (argument < 0 || !hints.KeptIn.TryAdd((callee, argument), callers)) throw new ElfFormatException("Invalid owned-field kept argument");
        }
        NameSet(hints.Addressed); NameSet(hints.CodeNamed); NameSet(hints.Slotted);
        NameSet(hints.UnresolvedIn);
        NameSet(hints.Loaded); NameSet(hints.ElementKept);
        OwnedElementRecord Element()
        {
            OwnedElementRecord record = new(Name());
            record.Needs.Add(Needs());
            NamesInto(record.Accessors.Add); NamesInto(record.Fixed.Add);
            return record;
        }
        void Map(SortedDictionary<string, OwnedElementRecord> into)
        {
            for (int i = Count(28); i > 0; i--)
                if (!into.TryAdd(Name(), Element())) throw new ElfFormatException("Duplicate owned-elements hint");
        }
        void PairMap(SortedDictionary<(string, int), OwnedElementRecord> into)
        {
            for (int i = Count(32); i > 0; i--)
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
    /// <summary>
    /// The functions a store or read of which here settles it: not owned if
    /// the program runs any of them. Every other store and read here is
    /// judged all the same, for when it runs none.
    /// </summary>
    public SmallSortedSet<string> RefusedBy { get; } = new(StringComparer.Ordinal);
    /// <summary>Stored into here.</summary>
    public bool Stored { get; set; }
    /// <summary>
    /// Some store here is a self-replacing free's (Opt.Escape, EscapeSelfFrees):
    /// a collection frees the value it replaces itself, so no call is watched
    /// for replacing it.
    /// </summary>
    public bool SelfFreed { get; set; }
    /// <summary>
    /// What its stores and reads need of other units' functions -- the sum of
    /// its sites', on which the unit keeps its bound; not written (Sites).
    /// </summary>
    public LifetimeCondition Needs { get; } = new();
    /// <summary>Parameters it is stored from, which every caller must hand over.</summary>
    public SortedSet<(string Callee, int Argument)> Sinks { get; } = new(OwnedFieldHints.PairOrder.Instance);
    /// <summary>The calls a read of it is live across: none may replace it. The sum of its sites', as Needs is.</summary>
    public SmallSortedSet<string> Danger { get; } = new(StringComparer.Ordinal);
    /// <summary>
    /// What the unit's stores put in it, by the stamp of the object each
    /// makes for it (`t_List$0024int+48`), or <see cref="UnknownKind"/> for
    /// anything else; null stores say nothing.
    /// </summary>
    public SmallSortedSet<string> Kinds { get; } = new(StringComparer.Ordinal);
    /// <summary>
    /// The one stamp the unit's reads were judged as holding (a virtual call
    /// on what is read reaching that type's method alone): owned only if
    /// every unit's stores put nothing else in it.
    /// </summary>
    public SmallSortedSet<string> Assumes { get; } = new(StringComparer.Ordinal);

    // ---- its arrays owning their elements (Escape.ArrayFieldElements) ----

    /// <summary>A store or read here is not proved to keep the arrays' elements to the field: they are not owned.</summary>
    public bool ElementsRefused { get; set; }
    /// <summary>A store here puts an array whose elements are proved in it.</summary>
    public bool ElementArrays { get; set; }
    /// <summary>What the proof of its elements needs of other units' functions.</summary>
    public LifetimeCondition ElementNeeds { get; } = new();
    /// <summary>The calls an element is live across: none may store into the field.</summary>
    public SmallSortedSet<string> ElementDanger { get; } = new(StringComparer.Ordinal);
    /// <summary>The fields stored into while an element is live: none may be owned.</summary>
    public SmallSortedSet<string> ElementDangerFields { get; } = new(StringComparer.Ordinal);

    /// <summary>A store of something whose type the unit cannot name.</summary>
    public const string UnknownKind = "?";

    /// <summary>
    /// SITES: the needs, dangers, kinds and assumptions above by the function
    /// whose stores and reads have them. The link counts a function's only
    /// where the program runs it -- a store that never runs stores nothing,
    /// a read that never runs holds nothing across anything -- as it does
    /// RefusedBy.
    /// </summary>
    public SortedDictionary<string, OwnedSite> Sites { get; } = new(StringComparer.Ordinal);
    public OwnedSite Site(string function) => Sites.TryGetValue(function, out OwnedSite? site) ? site : Sites[function] = new OwnedSite();
}

/// <summary>
/// What one function's stores and reads of a field say, or its reads of
/// what one callee returns: what they need of other units' functions, the
/// calls a read is live across, the fields stored into meanwhile (a call's
/// reads), the stamps stored (Kinds) and assumed (Assumes).
/// </summary>
public sealed class OwnedSite
{
    public LifetimeCondition Needs { get; } = new();
    public SmallSortedSet<string> Danger { get; } = new(StringComparer.Ordinal);
    public SmallSortedSet<string> DangerFields { get; } = new(StringComparer.Ordinal);
    public SmallSortedSet<string> Kinds { get; } = new(StringComparer.Ordinal);
    public SmallSortedSet<string> Assumes { get; } = new(StringComparer.Ordinal);
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
    /// <summary>The functions whose read lets it go, or lives across a free: as Refused, if the program runs any of them.</summary>
    public SmallSortedSet<string> RefusedBy { get; } = new(StringComparer.Ordinal);
    public LifetimeCondition Needs { get; } = new();
    /// <summary>The calls a read is live across.</summary>
    public SmallSortedSet<string> Danger { get; } = new(StringComparer.Ordinal);
    /// <summary>The fields stored into while a read is live.</summary>
    public SmallSortedSet<string> DangerFields { get; } = new(StringComparer.Ordinal);
    /// <summary>The functions that hand it back again: they hand back whatever the callee does.</summary>
    public SmallSortedSet<string> ReturnedBy { get; } = new(StringComparer.Ordinal);
    /// <summary>Needs, Danger and DangerFields by the calling function (OwnedFieldRecord.Sites); those three are their sum.</summary>
    public SortedDictionary<string, OwnedSite> Sites { get; } = new(StringComparer.Ordinal);
    public OwnedSite Site(string function) => Sites.TryGetValue(function, out OwnedSite? site) ? site : Sites[function] = new OwnedSite();
}

/// <summary>A collection's kind, as a unit found it of a field (OwnedFieldHints.ElementReads and the rest), and what that needs of other units.</summary>
public sealed class OwnedElementRecord
{
    public OwnedElementRecord(string kind) { Kind = kind; }
    public string Kind { get; }
    public LifetimeCondition Needs { get; } = new();
    /// <summary>
    /// Where the unit proved its reads through the field's accessors (Opt.
    /// OwnedElements.AccessorsProved): the functions that are its reads --
    /// the list's getters, the element getters, the adders, a stash's getters
    /// -- each a read in any unit that calls it, which that unit must have
    /// proved too; and the fields the proof found go back where they came
    /// from, which no other unit may store into or read.
    /// </summary>
    public SmallSortedSet<string> Accessors { get; } = new(StringComparer.Ordinal);
    public SmallSortedSet<string> Fixed { get; } = new(StringComparer.Ordinal);
}

/// <summary>An argument every call of one unit hands over, if what it names holds.</summary>
public sealed class OwnedSink
{
    /// <summary>The sum of NeedsIn, on which the unit keeps its bound.</summary>
    public LifetimeCondition Needs { get; } = new();
    /// <summary>What each calling function's calls need: counted where the program runs it.</summary>
    public SortedDictionary<string, LifetimeCondition> NeedsIn { get; } = new(StringComparer.Ordinal);
    /// <summary>The callers' own parameters it passes on, each of which must be a sink too.</summary>
    public SortedSet<(string Callee, int Argument)> Sinks { get; } = new(OwnedFieldHints.PairOrder.Instance);
}
