using System.Buffers.Binary;
using System.Text;
using Corsac.Lang.Elf;
using Corsac.Lang.Ir;

namespace Corsac.Lang.Lto;

/// <summary>
/// What must hold of other units for something here to be true: the listed
/// parameters of their functions must keep nothing they are handed, and the
/// listed functions must hand over what they return. Empty: true already.
/// </summary>
public sealed class LifetimeCondition : IEquatable<LifetimeCondition>
{
    /// <summary>A condition is never allowed to grow past this: it becomes "never" instead.</summary>
    public const int Limit = 32;

    public SortedSet<(string Callee, int Argument)> Stays { get; } = new(StaysOrder.Instance);
    public SortedSet<string> Fresh { get; } = new(StringComparer.Ordinal);
    public int Count => Stays.Count + Fresh.Count;
    public bool IsTrue => Count == 0;

    /// <summary>False when the condition grew past <see cref="Limit"/>, which means "never".</summary>
    public bool Add(LifetimeCondition other)
    {
        Stays.UnionWith(other.Stays);
        Fresh.UnionWith(other.Fresh);
        return Count <= Limit;
    }

    public bool Equals(LifetimeCondition? other)
        => other is not null && Stays.SetEquals(other.Stays) && Fresh.SetEquals(other.Fresh);
    public override bool Equals(object? obj) => Equals(obj as LifetimeCondition);
    public override int GetHashCode()
    {
        HashCode hash = new();
        foreach ((string callee, int argument) in Stays) { hash.Add(callee, StringComparer.Ordinal); hash.Add(argument); }
        foreach (string callee in Fresh) hash.Add(callee, StringComparer.Ordinal);
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
/// One function's lifetime summary as its unit could state it. A parameter's
/// condition says when the function keeps nothing it is handed there; the
/// return's, when what it returns is a fresh object its caller then owns.
/// Null is "never"; an empty condition is "always".
/// </summary>
public sealed record LifetimeFunction(string Name, bool Global, LifetimeCondition?[] Parameters, LifetimeCondition? Fresh);

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
    public const int MaximumBytes = 16 * 1024 * 1024;
    /// <summary>At most this many pending conditions per unit; a fixed bound, so the same everywhere.</summary>
    public const int PendingLimit = 4096;
    private const uint Magic = 0x46494c43; // "CLIF"
    private const int Version = 1;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public List<LifetimeFunction> Functions { get; } = new();
    public List<LifetimeCondition> Pending { get; } = new();
    /// <summary>The runtime's frees the unit may call (Module.RuntimeHelpers), by label.</summary>
    public SortedSet<string> Helpers { get; } = new(StringComparer.Ordinal);

    public bool IsEmpty => Pending.Count == 0 && Functions.Count == 0;

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
        }
        foreach (LifetimeCondition condition in Pending) Names(condition);
        void Names(LifetimeCondition? condition)
        {
            if (condition is null) return;
            foreach ((string callee, int _) in condition.Stays) names.Add(callee);
            names.UnionWith(condition.Fresh);
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
        }
        List<LifetimeCondition> pending = Pending.Distinct().ToList();
        writer.Write(pending.Count);
        foreach (LifetimeCondition condition in pending) Condition(condition);
        void Condition(LifetimeCondition? condition)
        {
            if (condition is null) { writer.Write(-1); return; }
            if (condition.Count > LifetimeCondition.Limit) throw new ElfFormatException("Lifetime condition exceeds its bound");
            writer.Write(condition.Stays.Count);
            foreach ((string callee, int argument) in condition.Stays) { writer.Write(index[callee]); writer.Write(argument); }
            writer.Write(condition.Fresh.Count);
            foreach (string callee in condition.Fresh) writer.Write(index[callee]);
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
                return condition;
            }
            LifetimeHints hints = new();
            for (int i = Count(4); i > 0; i--) hints.Helpers.Add(Name());
            for (int i = Count(13); i > 0; i--)
            {
                string name = Name(); bool global = reader.ReadBoolean();
                LifetimeCondition?[] parameters = new LifetimeCondition?[Count(4)];
                for (int p = 0; p < parameters.Length; p++) parameters[p] = Condition();
                hints.Functions.Add(new(name, global, parameters, Condition()));
            }
            int pending = Count(8);
            if (pending > PendingLimit) throw new ElfFormatException("Too many pending lifetime conditions");
            for (int i = 0; i < pending; i++)
                hints.Pending.Add(Condition() ?? throw new ElfFormatException("A pending lifetime condition is never"));
            if (stream.Position != bytes.Length) throw new ElfFormatException("Trailing lifetime hint data");
            return hints;
        }
        catch (EndOfStreamException) { throw new ElfFormatException("Truncated lifetime hints"); }
        catch (DecoderFallbackException) { throw new ElfFormatException("Invalid lifetime hint UTF-8"); }
    }
}
