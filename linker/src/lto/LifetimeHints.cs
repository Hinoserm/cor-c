using System.Buffers.Binary;
using System.Text;
using Corsac.Lang.Elf;
using Corsac.Lang.Ir;

namespace Corsac.Lang.Lto;

/// <summary>
/// What must hold of other units for something here to be true: the listed
/// parameters of their functions must keep nothing they are handed, the
/// listed functions must hand over what they return, and the listed
/// parameters' objects must have fields the owned field rules can follow
/// (not opaque). Empty: true already.
/// </summary>
public sealed class LifetimeCondition : IEquatable<LifetimeCondition>
{
    /// <summary>A condition is never allowed to grow past this: it becomes "never" instead.</summary>
    public const int Limit = 32;

    public SortedSet<(string Callee, int Argument)> Stays { get; } = new(StaysOrder.Instance);
    public SortedSet<string> Fresh { get; } = new(StringComparer.Ordinal);
    public SortedSet<(string Callee, int Argument)> Fields { get; } = new(StaysOrder.Instance);
    public int Count => Stays.Count + Fresh.Count + Fields.Count;
    public bool IsTrue => Count == 0;

    /// <summary>False when the condition grew past <see cref="Limit"/>, which means "never".</summary>
    public bool Add(LifetimeCondition other)
    {
        Stays.UnionWith(other.Stays);
        Fresh.UnionWith(other.Fresh);
        Fields.UnionWith(other.Fields);
        return Count <= Limit;
    }

    public bool Equals(LifetimeCondition? other)
        => other is not null && Stays.SetEquals(other.Stays) && Fresh.SetEquals(other.Fresh) && Fields.SetEquals(other.Fields);
    public override bool Equals(object? obj) => Equals(obj as LifetimeCondition);
    public override int GetHashCode()
    {
        HashCode hash = new();
        foreach ((string callee, int argument) in Stays) { hash.Add(callee, StringComparer.Ordinal); hash.Add(argument); }
        foreach (string callee in Fresh) hash.Add(callee, StringComparer.Ordinal);
        foreach ((string callee, int argument) in Fields) { hash.Add(callee, StringComparer.Ordinal); hash.Add(~argument); }
        return hash.ToHashCode();
    }

    /// <summary>Two conditions, the null "never" included, alike.</summary>
    public static bool Same(LifetimeCondition? a, LifetimeCondition? b) => a is null ? b is null : a.Equals(b);

    private sealed class StaysOrder : IComparer<(string Callee, int Argument)>
    {
        public static readonly StaysOrder Instance = new();
        public int Compare((string Callee, int Argument) x, (string Callee, int Argument) y)
        {
            int byName = string.CompareOrdinal(x.Callee, y.Callee);
            return byName != 0 ? byName : x.Argument.CompareTo(y.Argument);
        }
    }
}

/// <summary>
/// What a function does to the reference fields of one object -- one it is
/// handed, or the one it returns -- as its unit could state it (the owned
/// field rules, Escape's EscapeFields). Word offsets that are dirty or hold
/// fresh objects whatever other units do; offsets that are fresh (a stored
/// child made by another unit's function) or stay clean (a loaded child
/// handed to one) only if a condition holds, and dirty if not; and the other
/// units' functions the object is handed to at its base, whose own summary
/// for that argument is merged in. Opaque: no field of it is ever freed.
/// </summary>
public sealed class LifetimeFields
{
    /// <summary>A fixed bound on offsets and merges, the same everywhere; past it the object is opaque.</summary>
    public const int Limit = 64;

    public bool Opaque { get; set; }
    public SortedSet<long> Dirty { get; } = new();
    public SortedSet<long> Fresh { get; } = new();
    /// <summary>Stores: the offset holds a fresh object if the condition holds. Otherwise (a load): the offset stays clean if it holds.</summary>
    public List<(long Offset, LifetimeCondition Condition, bool Stores)> Conditional { get; } = new();
    public SortedSet<(string Callee, int Argument)> Merges { get; } = new(MergeOrder.Instance);

    public bool Bounded => Dirty.Count + Fresh.Count + Conditional.Count + Merges.Count <= Limit;

    public void Absorb(LifetimeFields other)
    {
        Opaque |= other.Opaque;
        Dirty.UnionWith(other.Dirty);
        Fresh.UnionWith(other.Fresh);
        foreach (var item in other.Conditional)
            if (!Conditional.Any(c => c.Offset == item.Offset && c.Stores == item.Stores && c.Condition.Equals(item.Condition)))
                Conditional.Add(item);
        Merges.UnionWith(other.Merges);
    }

    private sealed class MergeOrder : IComparer<(string Callee, int Argument)>
    {
        public static readonly MergeOrder Instance = new();
        public int Compare((string Callee, int Argument) x, (string Callee, int Argument) y)
        {
            int byName = string.CompareOrdinal(x.Callee, y.Callee);
            return byName != 0 ? byName : x.Argument.CompareTo(y.Argument);
        }
    }
}

/// <summary>
/// One function's lifetime summary as its unit could state it. A parameter's
/// condition says when the function keeps nothing it is handed there; the
/// return's, when what it returns is a fresh object its caller then owns.
/// Null is "never"; an empty condition is "always". The field summaries say
/// what it does to the fields of each parameter's object and of the object
/// it returns (null: none can be stated -- the object escapes).
/// </summary>
public sealed record LifetimeFunction(string Name, bool Global, LifetimeCondition?[] Parameters, LifetimeCondition? Fresh,
    LifetimeFields?[]? ParameterFields = null, LifetimeFields? FreshFields = null);

/// <summary>
/// LINK-TIME HINTS FOR THE LIFETIME RULES. A unit compiled on its own knows
/// nothing of the functions other units define, so an object handed to one,
/// or made by one, is left to the collector. The unit says instead what it
/// would need to know: its functions' summaries in terms of other units'
/// (<see cref="Functions"/>), and the conditions under which an object it
/// had to leave to the collector could be freed or kept in a frame after all
/// (<see cref="Pending"/>). The link solves every unit's summaries together
/// and recompiles only the units a pending condition now holds for.
///
/// The section is small by construction -- a few names per function, every
/// condition bounded by <see cref="LifetimeCondition.Limit"/> -- so the link
/// holds every unit's at once whatever the machine; how much memory there
/// is decides only how the recompiling is batched, never what is decided.
/// </summary>
public sealed class LifetimeHints
{
    public const string SectionName = ".corsac.life";
    /// <summary>The two routines a field site becomes: Runtime.FreeField and Runtime.KeepField, by label.</summary>
    public const string FieldFreer = "m_Runtime_FreeField_2_V$I64_V$I64";
    public const string FieldKeeper = "m_Runtime_KeepField_2_V$I64_V$I64";
    public const int MaximumBytes = 16 * 1024 * 1024;
    /// <summary>At most this many pending conditions per unit; a fixed bound, so the same everywhere.</summary>
    public const int PendingLimit = 4096;
    private const uint Magic = 0x46494c43; // "CLIF"
    private const int Version = 3;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public List<LifetimeFunction> Functions { get; } = new();
    public List<LifetimeCondition> Pending { get; } = new();
    /// <summary>
    /// Field frees of objects the unit already owns that only the link can
    /// decide: each object's field summary, and for each field it may free,
    /// the symbol its free calls. The link defines every such symbol as
    /// Runtime.FreeField where the whole program leaves the field clean, and
    /// as Runtime.KeepField, which does nothing, where it does not.
    /// </summary>
    public List<(LifetimeFields Fields, List<(string Symbol, long Offset)> Sites)> FieldSites { get; } = new();
    /// <summary>The runtime's frees the unit may call (Module.RuntimeHelpers), by label.</summary>
    public SortedSet<string> Helpers { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// WHAT THE UNIT THROWS THAT IT DID NOT JUST MAKE, for the link to judge
    /// which catches may free what they caught (Escape.ForeignThrows over the
    /// whole program): "*" for something it cannot name, "s:" and a static
    /// field it read the exception from, "f:" and another unit's function
    /// whose result it threw -- nothing, if the whole program finds that
    /// function hands over a fresh object.
    /// </summary>
    public SortedSet<string> Throws { get; } = new(StringComparer.Ordinal);
    /// <summary>What the unit stores into static fields: the field and the stamped type, "*" where it cannot say.</summary>
    public SortedSet<(string Field, string Type)> StaticStores { get; } = new(PairOrder.Instance);
    /// <summary>The static fields the unit stores to outside a static initialiser (Escape.PermanentStatics).</summary>
    public SortedSet<string> StaticWrites { get; } = new(StringComparer.Ordinal);

    private sealed class PairOrder : IComparer<(string, string)>
    {
        public static readonly PairOrder Instance = new();
        public int Compare((string, string) x, (string, string) y)
        {
            int first = string.CompareOrdinal(x.Item1, y.Item1);
            return first != 0 ? first : string.CompareOrdinal(x.Item2, y.Item2);
        }
    }

    public bool IsEmpty => Pending.Count == 0 && Functions.Count == 0 && FieldSites.Count == 0
        && Throws.Count == 0 && StaticStores.Count == 0 && StaticWrites.Count == 0;

    /// <summary>Every (function, argument) any condition here names, and every field merge: what the link must answer.</summary>
    public IEnumerable<(string Callee, int Argument)> Named()
    {
        IEnumerable<(string, int)> Of(LifetimeCondition? condition)
            => condition is null ? Enumerable.Empty<(string, int)>() : condition.Stays.Concat(condition.Fields).Concat(condition.Fresh.Select(name => (name, -1)));
        IEnumerable<(string, int)> OfFields(LifetimeFields? fields)
            => fields is null ? Enumerable.Empty<(string, int)>() : fields.Conditional.SelectMany(item => Of(item.Condition)).Concat(fields.Merges);
        foreach (LifetimeFunction function in Functions)
        {
            foreach (LifetimeCondition? condition in function.Parameters) foreach (var named in Of(condition)) yield return named;
            foreach (var named in Of(function.Fresh)) yield return named;
            foreach (LifetimeFields? fields in function.ParameterFields ?? Array.Empty<LifetimeFields?>()) foreach (var named in OfFields(fields)) yield return named;
            foreach (var named in OfFields(function.FreshFields)) yield return named;
        }
        foreach (LifetimeCondition condition in Pending) foreach (var named in Of(condition)) yield return named;
        foreach (var site in FieldSites) foreach (var named in OfFields(site.Fields)) yield return named;
    }

    public void Attach(ObjectFile obj)
    {
        if (obj.Sections.Any(section => section.Name == SectionName)) throw new ElfFormatException("Duplicate lifetime hints");
        Section section = new(SectionName, SectionKind.Note);
        section.Bytes.AddRange(Write());
        obj.Sections.Add(section);
    }

    public byte[] Write()
    {
        // Every name once, in ordinal order: the same hints are the same bytes.
        SortedSet<string> names = new(StringComparer.Ordinal);
        names.UnionWith(Helpers);
        foreach (LifetimeFunction function in Functions)
        {
            names.Add(function.Name);
            foreach (LifetimeCondition? condition in function.Parameters) Names(condition);
            Names(function.Fresh);
            foreach (LifetimeFields? fields in function.ParameterFields ?? Array.Empty<LifetimeFields?>()) FieldNames(fields);
            FieldNames(function.FreshFields);
        }
        foreach (LifetimeCondition condition in Pending) Names(condition);
        foreach ((LifetimeFields fields, List<(string Symbol, long Offset)> sites) in FieldSites)
        {
            FieldNames(fields);
            foreach ((string symbol, long _) in sites) names.Add(symbol);
        }
        void Names(LifetimeCondition? condition)
        {
            if (condition is null) return;
            foreach ((string callee, int _) in condition.Stays) names.Add(callee);
            names.UnionWith(condition.Fresh);
            foreach ((string callee, int _) in condition.Fields) names.Add(callee);
        }
        void FieldNames(LifetimeFields? fields)
        {
            if (fields is null) return;
            foreach (var item in fields.Conditional) Names(item.Condition);
            foreach ((string callee, int _) in fields.Merges) names.Add(callee);
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
            if (bytes.Length == 0 || bytes.Length > 16384 || name.Contains('\0')) throw new ElfFormatException("Invalid lifetime hint name");
            writer.Write(bytes.Length); writer.Write(bytes);
        }
        writer.Write(Helpers.Count);
        foreach (string helper in Helpers) writer.Write(index[helper]);
        writer.Write(Functions.Count);
        foreach (LifetimeFunction function in Functions.OrderBy(f => f.Name, StringComparer.Ordinal).ThenBy(f => f.Global))
        {
            writer.Write(index[function.Name]); writer.Write(function.Global);
            writer.Write(function.Parameters.Length);
            foreach (LifetimeCondition? condition in function.Parameters) Condition(condition);
            Condition(function.Fresh);
            LifetimeFields?[]? parameterFields = function.ParameterFields;
            writer.Write(parameterFields?.Length ?? -1);
            foreach (LifetimeFields? fields in parameterFields ?? Array.Empty<LifetimeFields?>()) Fields(fields);
            Fields(function.FreshFields);
        }
        List<LifetimeCondition> pending = Pending.Distinct().ToList();
        writer.Write(pending.Count);
        foreach (LifetimeCondition condition in pending) Condition(condition);
        writer.Write(FieldSites.Count);
        foreach ((LifetimeFields fields, List<(string Symbol, long Offset)> sites) in FieldSites)
        {
            Fields(fields);
            writer.Write(sites.Count);
            foreach ((string symbol, long offset) in sites) { writer.Write(index[symbol]); writer.Write(offset); }
        }
        writer.Write(Throws.Count);
        foreach (string thrown in Throws) Text(thrown);
        writer.Write(StaticStores.Count);
        foreach ((string field, string type) in StaticStores) { Text(field); Text(type); }
        writer.Write(StaticWrites.Count);
        foreach (string field in StaticWrites) Text(field);
        void Text(string text)
        {
            byte[] bytes = Utf8.GetBytes(text);
            if (bytes.Length == 0 || bytes.Length > 16384 || text.Contains('\0')) throw new ElfFormatException("Invalid lifetime hint text");
            writer.Write(bytes.Length); writer.Write(bytes);
        }
        void Condition(LifetimeCondition? condition)
        {
            if (condition is null) { writer.Write(-1); return; }
            if (condition.Count > LifetimeCondition.Limit) throw new ElfFormatException("Lifetime condition exceeds its bound");
            writer.Write(condition.Stays.Count);
            foreach ((string callee, int argument) in condition.Stays) { writer.Write(index[callee]); writer.Write(argument); }
            writer.Write(condition.Fresh.Count);
            foreach (string callee in condition.Fresh) writer.Write(index[callee]);
            writer.Write(condition.Fields.Count);
            foreach ((string callee, int argument) in condition.Fields) { writer.Write(index[callee]); writer.Write(argument); }
        }
        void Fields(LifetimeFields? fields)
        {
            if (fields is null) { writer.Write(false); return; }
            if (!fields.Bounded) throw new ElfFormatException("Lifetime field summary exceeds its bound");
            writer.Write(true); writer.Write(fields.Opaque);
            writer.Write(fields.Dirty.Count);
            foreach (long offset in fields.Dirty) writer.Write(offset);
            writer.Write(fields.Fresh.Count);
            foreach (long offset in fields.Fresh) writer.Write(offset);
            writer.Write(fields.Conditional.Count);
            foreach (var item in fields.Conditional.OrderBy(c => c.Offset).ThenBy(c => c.Stores))
            { writer.Write(item.Offset); writer.Write(item.Stores); Condition(item.Condition); }
            writer.Write(fields.Merges.Count);
            foreach ((string callee, int argument) in fields.Merges) { writer.Write(index[callee]); writer.Write(argument); }
        }
        writer.Flush();
        byte[] result = stream.ToArray();
        if (result.Length > MaximumBytes) throw new ElfFormatException("Lifetime hints exceed their budget");
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(8), result.Length);
        return result;
    }

    public static LifetimeHints? Read(ObjectFile obj)
    {
        Section[] sections = obj.Sections.Where(section => section.Name == SectionName).ToArray();
        if (sections.Length == 0) return null;
        if (sections.Length != 1 || sections[0].Bytes.Count > MaximumBytes) throw new ElfFormatException("Invalid lifetime hint section");
        return Read(sections[0].Bytes.ToArray());
    }

    public static LifetimeHints Read(byte[] bytes)
    {
        using MemoryStream stream = new(bytes, writable: false);
        using BinaryReader reader = new(stream, Utf8);
        try
        {
            if (reader.ReadUInt32() != Magic || reader.ReadInt32() != Version) throw new ElfFormatException("Unsupported lifetime hints");
            if (reader.ReadInt32() != bytes.Length) throw new ElfFormatException("Invalid lifetime hint length");
            int Count(int size)
            {
                int count = reader.ReadInt32();
                if (count < 0 || count > (bytes.Length - stream.Position) / size) throw new ElfFormatException("Invalid lifetime hint count");
                return count;
            }
            string[] names = new string[Count(5)];
            for (int i = 0; i < names.Length; i++)
            {
                int length = reader.ReadInt32();
                if (length < 1 || length > 16384 || length > bytes.Length - stream.Position) throw new ElfFormatException("Invalid lifetime hint name");
                names[i] = Utf8.GetString(reader.ReadBytes(length));
                if (names[i].Contains('\0') || i > 0 && string.CompareOrdinal(names[i - 1], names[i]) >= 0)
                    throw new ElfFormatException("Invalid lifetime hint name table");
            }
            string Name()
            {
                int at = reader.ReadInt32();
                if (at < 0 || at >= names.Length) throw new ElfFormatException("Invalid lifetime hint name index");
                return names[at];
            }
            LifetimeCondition? Condition()
            {
                int stays = reader.ReadInt32();
                if (stays == -1) return null;
                if (stays < 0 || stays > LifetimeCondition.Limit) throw new ElfFormatException("Invalid lifetime condition");
                LifetimeCondition condition = new();
                for (int i = 0; i < stays; i++)
                {
                    string callee = Name(); int argument = reader.ReadInt32();
                    if (argument < 0 || !condition.Stays.Add((callee, argument))) throw new ElfFormatException("Invalid lifetime condition");
                }
                int fresh = reader.ReadInt32();
                if (fresh < 0 || stays + fresh > LifetimeCondition.Limit) throw new ElfFormatException("Invalid lifetime condition");
                for (int i = 0; i < fresh; i++)
                    if (!condition.Fresh.Add(Name())) throw new ElfFormatException("Invalid lifetime condition");
                int fields = reader.ReadInt32();
                if (fields < 0 || stays + fresh + fields > LifetimeCondition.Limit) throw new ElfFormatException("Invalid lifetime condition");
                for (int i = 0; i < fields; i++)
                {
                    string callee = Name(); int argument = reader.ReadInt32();
                    if (argument < 0 || !condition.Fields.Add((callee, argument))) throw new ElfFormatException("Invalid lifetime condition");
                }
                return condition;
            }
            LifetimeFields? Fields()
            {
                if (!reader.ReadBoolean()) return null;
                LifetimeFields fields = new() { Opaque = reader.ReadBoolean() };
                for (int i = Count(8); i > 0; i--) if (!fields.Dirty.Add(reader.ReadInt64())) throw new ElfFormatException("Invalid lifetime fields");
                for (int i = Count(8); i > 0; i--) if (!fields.Fresh.Add(reader.ReadInt64())) throw new ElfFormatException("Invalid lifetime fields");
                for (int i = Count(21); i > 0; i--)
                {
                    long offset = reader.ReadInt64(); bool stores = reader.ReadBoolean();
                    fields.Conditional.Add((offset, Condition() ?? throw new ElfFormatException("A field condition is never"), stores));
                }
                for (int i = Count(8); i > 0; i--)
                {
                    // Argument -1: the object the callee returns.
                    string callee = Name(); int argument = reader.ReadInt32();
                    if (argument < -1 || !fields.Merges.Add((callee, argument))) throw new ElfFormatException("Invalid lifetime fields");
                }
                if (!fields.Bounded) throw new ElfFormatException("Lifetime field summary exceeds its bound");
                return fields;
            }
            LifetimeHints hints = new();
            for (int i = Count(4); i > 0; i--) hints.Helpers.Add(Name());
            for (int i = Count(18); i > 0; i--)
            {
                string name = Name(); bool global = reader.ReadBoolean();
                LifetimeCondition?[] parameters = new LifetimeCondition?[Count(4)];
                for (int p = 0; p < parameters.Length; p++) parameters[p] = Condition();
                LifetimeCondition? freshCondition = Condition();
                int fieldCount = reader.ReadInt32();
                if (fieldCount < -1 || fieldCount > parameters.Length) throw new ElfFormatException("Invalid lifetime field count");
                LifetimeFields?[]? parameterFields = fieldCount < 0 ? null : new LifetimeFields?[fieldCount];
                for (int p = 0; p < fieldCount; p++) parameterFields![p] = Fields();
                hints.Functions.Add(new(name, global, parameters, freshCondition, parameterFields, Fields()));
            }
            int pending = Count(8);
            if (pending > PendingLimit) throw new ElfFormatException("Too many pending lifetime conditions");
            for (int i = 0; i < pending; i++)
                hints.Pending.Add(Condition() ?? throw new ElfFormatException("A pending lifetime condition is never"));
            HashSet<string> siteNames = new(StringComparer.Ordinal);
            for (int i = Count(9); i > 0; i--)
            {
                LifetimeFields fields = Fields() ?? throw new ElfFormatException("A field site without a summary");
                List<(string Symbol, long Offset)> sites = new();
                for (int k = Count(12); k > 0; k--)
                {
                    string symbol = Name(); long offset = reader.ReadInt64();
                    if (!siteNames.Add(symbol)) throw new ElfFormatException("Duplicate lifetime field site");
                    sites.Add((symbol, offset));
                }
                hints.FieldSites.Add((fields, sites));
            }
            string Text()
            {
                int length = reader.ReadInt32();
                if (length < 1 || length > 16384 || length > bytes.Length - stream.Position) throw new ElfFormatException("Invalid lifetime hint text");
                string text = Utf8.GetString(reader.ReadBytes(length));
                if (text.Contains('\0')) throw new ElfFormatException("Invalid lifetime hint text");
                return text;
            }
            for (int i = Count(5); i > 0; i--) hints.Throws.Add(Text());
            for (int i = Count(10); i > 0; i--) { string field = Text(); hints.StaticStores.Add((field, Text())); }
            for (int i = Count(5); i > 0; i--) hints.StaticWrites.Add(Text());
            if (stream.Position != bytes.Length) throw new ElfFormatException("Trailing lifetime hint data");
            return hints;
        }
        catch (EndOfStreamException) { throw new ElfFormatException("Truncated lifetime hints"); }
        catch (DecoderFallbackException) { throw new ElfFormatException("Invalid lifetime hint UTF-8"); }
    }
}
