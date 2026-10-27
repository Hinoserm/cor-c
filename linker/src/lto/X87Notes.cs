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
    const int Version = 2;

    /// <summary>
    /// The routine whose address `field` holds, as a call through a load of
    /// it names it (Selector.X87Fact) and as an assembled object that stores
    /// one there says what it is (X86Assembler.FloatFacts): a kernel's
    /// natives, called through Sys.CallNative.
    /// </summary>
    public static string InField(string field) => "*" + field;

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

        /// <summary>
        /// Whether the processor or a loader may enter it by itself -- an
        /// assembled routine's (X86Assembler.FloatFacts): nothing this link
        /// knows calling it, it runs outside every block, where a compiled
        /// function nothing calls never runs.
        /// </summary>
        public bool Entered { get; init; }
    }

    public static byte[] Encode(IReadOnlyList<Fact> facts)
    {
        using MemoryStream stream = new();
        using (BinaryWriter writer = new(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(Version);
            writer.Write(facts.Count(f => f.Floats));
            writer.Write(facts.Count);
            foreach (Fact f in facts)
            {
                writer.Write(f.Name);
                writer.Write(f.Display);
                writer.Write(f.File);
                writer.Write(f.FloatLine);
                writer.Write(f.Escapes);
                writer.Write(f.Entered);
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
        reader.ReadInt32();
        int count = reader.ReadInt32();
        List<Fact> facts = new(count);
        for (int k = 0; k < count; k++)
        {
            string name = reader.ReadString(), display = reader.ReadString(), file = reader.ReadString();
            int floatLine = reader.ReadInt32();
            bool escapes = reader.ReadBoolean();
            bool entered = reader.ReadBoolean();
            Call[] calls = new Call[reader.ReadInt32()];
            for (int c = 0; c < calls.Length; c++) calls[c] = new Call(reader.ReadString(), reader.ReadInt32(), reader.ReadBoolean());
            facts.Add(new Fact(name, display, file, floatLine, escapes, calls) { Entered = entered });
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

    /// <summary>How many functions of the object's facts have float code outside their blocks: the header's count alone, read; -1 for an object with no facts.</summary>
    public static int FloatCount(ObjectFile obj)
    {
        Section? section = obj.Sections.FirstOrDefault(s => s.Name == SectionName);
        if (section is null) return -1;
        using Stream stream = section.OpenRead();
        using BinaryReader reader = new(stream, Encoding.UTF8);
        if (reader.ReadInt32() != Version) throw new ElfFormatException("Unknown " + SectionName + " version");
        return reader.ReadInt32();
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
    /// interrupt checks' are (InterruptNotes.WriteBeside): a call another
    /// image makes into this one is judged at that image's link, against
    /// this one's functions as this link saw them. Only what can reach float
    /// code by calls made outside a block: a float function, and every
    /// function that calls one of these outside a block of its own. Written
    /// where the image's units carry facts (Judges: every freestanding i386
    /// unit's, the rule kept or not), with nothing in it where nothing
    /// floats; taken away where they carry none -- hosted units, foreign objects --
    /// so that a missing file says the image's calls were never written down
    /// (ReadBeside).
    /// </summary>
    public static void WriteBeside(string path, IEnumerable<ObjectFile> objects)
    {
        List<ObjectFile> units = objects.ToList();
        if (!Judges(units)) { if (File.Exists(path + ".x87")) File.Delete(path + ".x87"); return; }
        Dictionary<string, Fact> all = Joined(units.Select(Read).Where(read => read is not null).SelectMany(read => read!));
        Dictionary<string, List<string>> outsideCallers = new(StringComparer.Ordinal);
        foreach (Fact f in all.Values)
            foreach (Call c in f.Calls)
                if (!c.Inside)
                {
                    if (!outsideCallers.TryGetValue(c.Callee, out var list)) outsideCallers[c.Callee] = list = new();
                    list.Add(f.Name);
                }
        HashSet<string> reach = new(StringComparer.Ordinal);
        Stack<string> work = new();
        foreach (Fact f in all.Values) if (f.Floats && reach.Add(f.Name)) work.Push(f.Name);
        while (work.Count > 0)
            if (outsideCallers.TryGetValue(work.Pop(), out var by))
                foreach (string caller in by) if (reach.Add(caller)) work.Push(caller);
        // CALLED HERE, ENTERED NOWHERE: an assembled routine this image calls
        // keeps that, whichever calls are written beside it (Check's start).
        HashSet<string> called = new(StringComparer.Ordinal);
        foreach (Fact f in all.Values) foreach (Call c in f.Calls) called.Add(c.Callee);
        List<Fact> kept = all.Values.Where(f => reach.Contains(f.Name))
            .Select(f => f with { Calls = f.Calls.Where(c => reach.Contains(c.Callee)).ToArray(), Entered = f.Entered && !called.Contains(f.Name) }).ToList();
        File.WriteAllBytes(path + ".x87", Encode(kept));
    }

    /// <summary>
    /// The facts an image's link wrote beside it (WriteBeside). Refused where
    /// there are none and `needed` (this link's own units carry facts, so it
    /// judges calls into the image): an image linked without them -- by an
    /// older compiler, or a link that writes none -- would let every call
    /// into its float code pass unjudged.
    /// </summary>
    public static List<Fact> ReadBeside(string path, bool needed)
    {
        string beside = path + ".x87";
        if (!File.Exists(beside))
        {
            if (needed) throw new LinkException(new List<string> { $"{path}: no float-code facts beside it ({beside}), so this link cannot judge its calls into {Path.GetFileName(path)}: it was linked without them (by an older compiler, or from hosted units); link it again with this compiler" });
            return new List<Fact>();
        }
        using FileStream stream = File.OpenRead(beside);
        try { return Decode(stream); }
        catch (ElfFormatException e) { throw new LinkException(new List<string> { $"{beside}: {e.Message}: link {Path.GetFileName(path)} again with this compiler" }); }
    }

    /// <summary>Whether any of the objects carries facts: a link that judges float code, and so needs the facts of the images it links against.</summary>
    public static bool Judges(IEnumerable<ObjectFile> objects) => objects.Any(obj => obj.Sections.Any(s => s.Name == SectionName));

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
                Entered = had.Entered || f.Entered,
                Calls = had.Calls.Length == 0 ? f.Calls : f.Calls.Length == 0 ? had.Calls : had.Calls.Concat(f.Calls).ToArray(),
            };
        }
        return joined;
    }

    /// <summary>
    /// Every function with float code outside its own blocks that can run
    /// outside every block, as an error naming the way it gets there. A
    /// function RUNS OUTSIDE when it is where the image starts (`entries`,
    /// and every unit's module initializers, which the link calls, and an
    /// assembled routine nothing calls), when its address is taken, when
    /// code no fact describes refers to it (an object with no facts -- a
    /// foreign one -- calls whatever its relocations name), or when a
    /// function that runs outside calls it outside a block of its own; every other function runs only inside its callers'
    /// blocks. One nothing calls at all never runs here -- a body left
    /// behind where every call was inlined -- and another image's calls into
    /// this one are that image's link's to judge, against the facts this
    /// link writes beside it. Nothing to judge where no unit has such float
    /// code.
    /// </summary>
    public static List<string> Check(IEnumerable<ObjectFile> objects, IReadOnlyList<Fact>? imported = null, IReadOnlyCollection<string>? entries = null)
    {
        List<ObjectFile> all = objects.ToList();
        List<string> errors = new();
        if (all.All(obj => FloatCount(obj) <= 0) && imported?.Any(f => f.Floats) != true) return errors;
        List<Fact> read = new();
        List<ObjectFile> without = new();
        foreach (ObjectFile obj in all)
        {
            if (Read(obj) is { } own) read.AddRange(own);
            else without.Add(obj);
        }
        if (imported is not null) read.AddRange(imported);
        Dictionary<string, Fact> facts = Joined(read);

        // WHAT AN OBJECT WITHOUT FACTS REFERS TO is called, or its address
        // taken, by code no fact describes: assembly, a foreign object.
        HashSet<string> unknown = new(StringComparer.Ordinal);
        foreach (ObjectFile obj in without)
            foreach (Section section in obj.Sections)
                foreach (Relocation r in section.Relocs)
                    unknown.Add(r.Symbol);
        // AND WHAT THE LINK ITSELF CALLS: each unit's [ModuleInitializer]
        // methods (ModuleInfo.InitializerSection), from the stub it writes
        // after this -- at a module's load, before a program's Main. Seeded
        // where no stub calls them too (a freestanding _start that names
        // none): a refusal there is of code that never runs, which a block
        // costs nothing to keep.
        HashSet<string> started = new(entries ?? Array.Empty<string>(), StringComparer.Ordinal);
        // AND AN ASSEMBLED ROUTINE NOTHING KNOWN CALLS (Fact.Entered): a
        // processor's or a loader's to enter.
        HashSet<string> called = new(StringComparer.Ordinal);
        foreach (Fact f in facts.Values) foreach (Call c in f.Calls) called.Add(c.Callee);
        foreach (Fact f in facts.Values) if (f.Entered && !called.Contains(f.Name)) started.Add(f.Name);
        foreach (ObjectFile obj in all)
            foreach (Section section in obj.Sections)
                if (section.Name == ModuleInfo.InitializerSection)
                    started.UnionWith(ModuleInfo.Entries(section.Content()));

        // WHAT RUNS OUTSIDE, from its seeds along the calls made outside a
        // block: what is left runs only inside one. Each keeps the call that
        // took it there (`by`), the way its error names.
        HashSet<string> outside = new(StringComparer.Ordinal);
        Dictionary<string, (Fact Caller, Call At)> by = new(StringComparer.Ordinal);
        Stack<string> work = new();
        foreach (Fact f in facts.Values)
            if (f.Escapes || unknown.Contains(f.Name) || started.Contains(f.Name))
                if (outside.Add(f.Name)) work.Push(f.Name);
        while (work.Count > 0)
        {
            Fact f = facts[work.Pop()];
            foreach (Call c in f.Calls)
                if (!c.Inside && facts.ContainsKey(c.Callee) && outside.Add(c.Callee))
                {
                    by[c.Callee] = (f, c);
                    work.Push(c.Callee);
                }
        }

        foreach (Fact f in facts.Values.Where(f => f.Floats && outside.Contains(f.Name)).OrderBy(f => f.File, StringComparer.Ordinal).ThenBy(f => f.FloatLine))
            errors.Add($"'{f.Display}' has float code ({Where(f, f.FloatLine)}) that can run where no kept state is: {Way(f, out bool call)}; "
                + (call ? "the float code, or the outermost call named, belongs in a method the compiler keeps the state around -- not a lambda, an async body, a virtual method reached by a table, or code reached from assembly"
                        : "the float code belongs in a method the compiler keeps the state around -- not a lambda, an async body, a virtual method reached by a table, or code reached from assembly")
                + ", whose save keeps the interrupted thread's x87 state first");
        return errors;

        // HOW IT GETS THERE: back along the calls that took each function
        // outside (`by`), to the seed -- the entry, a taken address, code no
        // fact describes. `call` says whether a call outside a block is the
        // way, which a block around it closes.
        string Way(Fact from, out bool call)
        {
            List<string> steps = new();
            Fact at = from;
            call = false;
            while (by.TryGetValue(at.Name, out var step))
            {
                steps.Add($"'{step.Caller.Display}' calls '{at.Display}' at {Where(step.Caller, step.At.Line)}");
                call = true;
                at = step.Caller;
            }
            if (at.Escapes) steps.Add($"'{at.Display}' has its address taken (a delegate, a function pointer, a virtual method, a datum or an assembled reference), so whatever calls it may");
            else if (unknown.Contains(at.Name)) steps.Add($"'{at.Display}' is called from code the link has no facts for (assembly, a foreign object)");
            else steps.Add($"'{at.Display}' is where the image starts (its entry, a module initializer the link calls, or an assembled routine nothing here calls)");
            return string.Join(", and ", steps);
        }
    }

    private static string Where(Fact f, int line) => (f.File.Length > 0 ? f.File : f.Display) + (line > 0 ? ":" + line : "");
}
