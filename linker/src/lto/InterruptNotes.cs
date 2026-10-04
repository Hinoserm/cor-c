#nullable enable
using System.Text;
using Corsac.Lang.Elf;
using Corsac.Lang.Ir;

namespace Corsac.Lang.Lto;

/// <summary>
/// AN INTERRUPT HANDLER MAY NOT ALLOCATE -- through any unit. The compiler
/// checks a handler and every method it calls directly whose body the unit
/// compiles (Binder.AwaitChecks); a call into another unit it cannot follow.
/// So each unit says, for every method it compiles, what the link needs to
/// follow it: the method's symbol, whether it is a handler, the first thing
/// it allocates itself (or nothing), and the symbols it calls directly
/// (static or non-virtual: the calls the check follows). In a section of its
/// own (`.corsac.irq`); the link (Check) walks from every handler through
/// every unit's facts, refuses the image when a handler reaches an
/// allocation, and takes the section out.
/// </summary>
public static class InterruptNotes
{
    public const string SectionName = ".corsac.irq";
    const int Version = 1;

    /// <summary>
    /// One method: its symbol, its name as written, whether it is an
    /// interrupt handler, the first allocation its own body makes as the
    /// compiler words it ("makes a new 'X' at file:line"), or null, and the
    /// symbols it calls directly.
    /// </summary>
    public sealed record Fact(string Name, string Display, bool Handler, string? Allocates, string[] Calls);

    public static void Attach(ObjectFile obj, IReadOnlyList<Fact> facts)
    {
        using MemoryStream stream = new();
        using (BinaryWriter writer = new(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(Version);
            writer.Write(facts.Count);
            foreach (Fact f in facts)
            {
                writer.Write(f.Name);
                writer.Write(f.Display);
                writer.Write(f.Handler);
                writer.Write(f.Allocates ?? "");
                writer.Write(f.Calls.Length);
                foreach (string call in f.Calls) writer.Write(call);
            }
        }
        Section section = new(SectionName, SectionKind.Note) { Align = 1 };
        section.Bytes.AddRange(stream.ToArray());
        obj.Sections.Add(section);
    }

    /// <summary>The object's facts, or null when it carries none.</summary>
    public static List<Fact>? Read(ObjectFile obj)
    {
        Section? section = obj.Sections.FirstOrDefault(s => s.Name == SectionName);
        if (section is null) return null;
        using MemoryStream stream = new(section.Content());
        using BinaryReader reader = new(stream, Encoding.UTF8);
        if (reader.ReadInt32() != Version) throw new ElfFormatException("Unknown " + SectionName + " version");
        int count = reader.ReadInt32();
        List<Fact> facts = new(count);
        for (int i = 0; i < count; i++)
        {
            string name = reader.ReadString();
            string display = reader.ReadString();
            bool handler = reader.ReadBoolean();
            string allocates = reader.ReadString();
            string[] calls = new string[reader.ReadInt32()];
            for (int k = 0; k < calls.Length; k++) calls[k] = reader.ReadString();
            facts.Add(new Fact(name, display, handler, allocates.Length == 0 ? null : allocates, calls));
        }
        return facts;
    }

    /// <summary>
    /// Every handler of every unit walked through every unit's facts: what
    /// each that reaches an allocation reaches, as an error naming the
    /// handler, the method it calls, and the allocation. Methods no unit
    /// describes -- assembled code, a unit compiled without facts -- are not
    /// followed.
    /// </summary>
    public static List<string> Check(IEnumerable<ObjectFile> objects)
    {
        Dictionary<string, Fact> facts = new(StringComparer.Ordinal);
        List<Fact> handlers = new();
        foreach (ObjectFile obj in objects)
        {
            List<Fact>? read = Read(obj);
            if (read is null) continue;
            foreach (Fact f in read)
            {
                facts.TryAdd(f.Name, f);
                if (f.Handler) handlers.Add(f);
            }
        }
        List<string> errors = new();
        if (handlers.Count == 0) return errors;
        // What each method reaches, once: the allocation and the method that
        // makes it, or null for nothing.
        Dictionary<string, (string What, Fact Where)?> known = new(StringComparer.Ordinal);
        HashSet<string> visiting = new(StringComparer.Ordinal);
        (string What, Fact Where)? Reaches(Fact f)
        {
            if (known.TryGetValue(f.Name, out var had)) return had;
            if (!visiting.Add(f.Name)) return null;
            (string What, Fact Where)? found = f.Allocates is string what ? (what, f) : null;
            if (found is null)
            {
                foreach (string call in f.Calls)
                {
                    if (!facts.TryGetValue(call, out Fact? callee)) continue;
                    (string What, Fact Where)? deeper = Reaches(callee);
                    if (deeper.HasValue) { found = deeper; break; }
                }
            }
            visiting.Remove(f.Name);
            known[f.Name] = found;
            return found;
        }
        HashSet<string> said = new(StringComparer.Ordinal);
        foreach (Fact handler in handlers)
        {
            if (!said.Add(handler.Name)) continue;
            if (handler.Allocates is string own)
            {
                errors.Add($"'{handler.Display}' is an interrupt handler and must not allocate: here it {own}");
                continue;
            }
            foreach (string call in handler.Calls)
            {
                if (!facts.TryGetValue(call, out Fact? callee)) continue;
                (string What, Fact Where)? found = Reaches(callee);
                if (!found.HasValue) continue;
                string inner = ReferenceEquals(found.Value.Where, callee) ? "" : $" (in '{found.Value.Where.Display}', which it reaches)";
                errors.Add($"'{handler.Display}' is an interrupt handler and must not allocate: '{callee.Display}' {found.Value.What}{inner}");
                break;
            }
        }
        return errors;
    }

    public static void Strip(IEnumerable<ObjectFile> objects)
    {
        foreach (ObjectFile obj in objects) obj.Sections.RemoveAll(section => section.Name == SectionName);
    }
}
