#nullable enable
using System.Text;
using Corsac.Lang.Elf;
using Corsac.Lang.Ir;

namespace Corsac.Lang.Lto;

/// <summary>
/// FREESTANDING FLOAT CODE RUNS INSIDE A PACKED BLOCK -- its own, or its
/// callers' (X86Backend.X87InBlocksOnly). A function whose every call is
/// made inside a block, or from a function that is itself so, runs under
/// its callers' block, whose save covers the call: a per-pixel helper needs
/// no block of its own, which would save and restore the x87 state per
/// pixel. Who calls a function is the whole program's to say, so each unit
/// says, for every function it compiles, what the link needs to judge it:
/// the function's symbol, its name as written and its file, the first line
/// of its float code outside its own blocks (or none), and its direct calls,
/// each with its line and whether it is made inside a block; and every
/// function whose address it takes -- a delegate, a function pointer, a
/// virtual slot -- which runs where no one can say. In a section of its own
/// (`.corsac.x87`); the link (Check) refuses the image where float code can
/// run outside every block, naming the call that takes it there.
/// </summary>
public static class X87Notes
{
    public const string SectionName = ".corsac.x87";
    const int Version = 0;

    /// <summary>A direct call: whom, at which line, and whether inside a packed block of the caller's.</summary>
    public readonly record struct Call(string Callee, int Line, bool Inside);

    /// <summary>
    /// One function: its symbol, its name as written, its file, the line of
    /// its first float code outside its own packed blocks (-1 for none, or
    /// for a function the check passes over: the system library's), whether
    /// its address is taken, and its direct calls. A function whose address
    /// a unit takes and does not compile is one with its address taken, and
    /// nothing else.
    /// </summary>
    public sealed record Fact(string Name, string Display, string File, int FloatLine, bool Escapes, Call[] Calls)
    {
        public bool Floats => FloatLine >= 0;
    }

    public static byte[] Encode(IReadOnlyList<Fact> facts)
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
                writer.Write(f.File);
                writer.Write(f.FloatLine);
                writer.Write(f.Escapes);
                writer.Write(f.Calls.Length);
                foreach (Call c in f.Calls)
                {
                    writer.Write(c.Callee);
                    writer.Write(c.Line);
                    writer.Write(c.Inside);
                }
            }
        }
        return stream.ToArray();
    }

    private static List<Fact> Decode(Stream stream)
    {
        using BinaryReader reader = new(stream, Encoding.UTF8, leaveOpen: true);
        if (reader.ReadInt32() != Version) throw new ElfFormatException("Unknown " + SectionName + " version");
        int count = reader.ReadInt32();
        List<Fact> facts = new(count);
        for (int k = 0; k < count; k++)
        {
            string name = reader.ReadString(), display = reader.ReadString(), file = reader.ReadString();
            int floatLine = reader.ReadInt32();
            bool escapes = reader.ReadBoolean();
            Call[] calls = new Call[reader.ReadInt32()];
            for (int c = 0; c < calls.Length; c++) calls[c] = new Call(reader.ReadString(), reader.ReadInt32(), reader.ReadBoolean());
            facts.Add(new Fact(name, display, file, floatLine, escapes, calls));
        }
        return facts;
    }

    public static void Attach(ObjectFile obj, IReadOnlyList<Fact> facts)
    {
        if (facts.Count == 0) return;
        Section section = new(SectionName, SectionKind.Note) { Align = 1 };
        section.Bytes.AddRange(Encode(facts));
        obj.Sections.Add(section);
    }

    /// <summary>The object's facts, or null when it carries none.</summary>
    public static List<Fact>? Read(ObjectFile obj)
    {
        Section? section = obj.Sections.FirstOrDefault(s => s.Name == SectionName);
        if (section is null) return null;
        using Stream stream = section.OpenRead();
        return Decode(stream);
    }

    public static void Strip(IEnumerable<ObjectFile> objects)
    {
        foreach (ObjectFile obj in objects) obj.Sections.RemoveAll(section => section.Name == SectionName);
    }

    /// <summary>
    /// FACTS BESIDE AN IMAGE OTHERS LINK AGAINST (path + ".x87"), as the
    /// interrupt checks' are (InterruptNotes.WriteBeside): a module's call
    /// into the kernel is judged at the module's link, against the kernel's
    /// functions as the kernel's link saw them. With no facts, one left from
    /// before is taken away.
    /// </summary>
    public static void WriteBeside(string path, IEnumerable<ObjectFile> objects)
    {
        string beside = path + ".x87";
        List<Fact> all = Joined(objects.Select(Read).Where(read => read is not null).SelectMany(read => read!)).Values.ToList();
        if (all.Count == 0) { if (File.Exists(beside)) File.Delete(beside); return; }
        File.WriteAllBytes(beside, Encode(all));
    }

    /// <summary>The facts an image's link wrote beside it (WriteBeside), or none.</summary>
    public static List<Fact> ReadBeside(string path)
    {
        string beside = path + ".x87";
        if (!File.Exists(beside)) return new List<Fact>();
        using FileStream stream = File.OpenRead(beside);
        return Decode(stream);
    }

    // ONE FACT A NAME: a function several units compile (a generic's
    // specialisation, coalesced) is every unit's, its float code and its
    // calls joined; a taken address is taken.
    private static Dictionary<string, Fact> Joined(IEnumerable<Fact> facts)
    {
        Dictionary<string, Fact> joined = new(StringComparer.Ordinal);
        foreach (Fact f in facts)
        {
            if (!joined.TryGetValue(f.Name, out Fact? had)) { joined[f.Name] = f; continue; }
            joined[f.Name] = had with
            {
                Display = had.Display.Length > 0 ? had.Display : f.Display,
                File = had.File.Length > 0 ? had.File : f.File,
                FloatLine = had.Floats ? had.FloatLine : f.FloatLine,
                Escapes = had.Escapes || f.Escapes,
                Calls = had.Calls.Length == 0 ? f.Calls : f.Calls.Length == 0 ? had.Calls : had.Calls.Concat(f.Calls).ToArray(),
            };
        }
        return joined;
    }

    /// <summary>
    /// Every function with float code outside its own blocks that can run
    /// outside every block, as an error naming the way it gets there. A
    /// function RUNS OUTSIDE when its address is taken, when nothing this
    /// link knows calls it (an entry point, an export, code in assembly: an
    /// object with no facts calls whatever its relocations name), or when a
    /// function that runs outside calls it outside a block of its own; every
    /// other function runs only inside its callers' blocks. Nothing to judge
    /// where no unit has such float code.
    /// </summary>
    public static List<string> Check(IEnumerable<ObjectFile> objects, IReadOnlyList<Fact>? imported = null)
    {
        List<ObjectFile> all = objects.ToList();
        List<Fact> read = new();
        List<ObjectFile> without = new();
        foreach (ObjectFile obj in all)
        {
            if (Read(obj) is { } own) read.AddRange(own);
            else without.Add(obj);
        }
        if (imported is not null) read.AddRange(imported);
        List<string> errors = new();
        if (!read.Any(f => f.Floats)) return errors;
        Dictionary<string, Fact> facts = Joined(read);

        // Who calls each function, and from where.
        Dictionary<string, List<(Fact Caller, Call At)>> callers = new(StringComparer.Ordinal);
        foreach (Fact f in facts.Values)
            foreach (Call c in f.Calls)
            {
                if (!callers.TryGetValue(c.Callee, out var list)) callers[c.Callee] = list = new();
                list.Add((f, c));
            }
        // WHAT AN OBJECT WITHOUT FACTS REFERS TO is called, or its address
        // taken, by code no fact describes: assembly, a foreign object.
        HashSet<string> unknown = new(StringComparer.Ordinal);
        foreach (ObjectFile obj in without)
            foreach (Section section in obj.Sections)
                foreach (Relocation r in section.Relocs)
                    unknown.Add(r.Symbol);

        // WHAT RUNS OUTSIDE, from its seeds along the calls made outside a
        // block: what is left runs only inside one.
        HashSet<string> outside = new(StringComparer.Ordinal);
        Stack<string> work = new();
        foreach (Fact f in facts.Values)
            if (f.Escapes || unknown.Contains(f.Name) || !callers.ContainsKey(f.Name))
                if (outside.Add(f.Name)) work.Push(f.Name);
        while (work.Count > 0)
        {
            Fact f = facts[work.Pop()];
            foreach (Call c in f.Calls)
                if (!c.Inside && facts.ContainsKey(c.Callee) && outside.Add(c.Callee)) work.Push(c.Callee);
        }

        foreach (Fact f in facts.Values.Where(f => f.Floats && outside.Contains(f.Name)).OrderBy(f => f.File, StringComparer.Ordinal).ThenBy(f => f.FloatLine))
            errors.Add($"'{f.Display}' has float code outside a packed block ({Where(f, f.FloatLine)}) and can run outside one: {Way(f)}; "
                + "put it, or that call, inside using (Packed.X87()) { ... }, which saves the interrupted thread's x87 state first");
        return errors;

        // HOW IT GETS THERE: up the calls made outside a block, from callers
        // that run outside, to what has no caller inside one.
        string Way(Fact from)
        {
            List<string> steps = new();
            HashSet<string> seen = new(StringComparer.Ordinal);
            Fact at = from;
            while (seen.Add(at.Name))
            {
                if (at.Escapes) { steps.Add($"'{at.Display}' has its address taken (a delegate, a function pointer or a virtual method), so whatever calls it can"); break; }
                if (unknown.Contains(at.Name)) { steps.Add($"'{at.Display}' is called from code the link has no facts for (assembly, a foreign object)"); break; }
                if (!callers.TryGetValue(at.Name, out var list)) { steps.Add($"nothing this link knows calls '{at.Display}' (an entry point, or an export another image calls)"); break; }
                (Fact Caller, Call At) by = list.First(c => !c.At.Inside && outside.Contains(c.Caller.Name));
                steps.Add($"'{by.Caller.Display}' calls '{at.Display}' at {Where(by.Caller, by.At.Line)}, outside a packed block");
                at = by.Caller;
            }
            return string.Join(", and ", steps);
        }
    }

    private static string Where(Fact f, int line) => (f.File.Length > 0 ? f.File : f.Display) + (line > 0 ? ":" + line : "");
}
