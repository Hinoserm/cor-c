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
    // The handlers are counted in the header, so a link with none reads no
    // more of the facts than that.
    const int Version = 0;

    /// <summary>
    /// One method: its symbol, its name as written, whether it is an
    /// interrupt handler, the first allocation its own body makes as the
    /// compiler words it ("makes a new 'X' at file:line"), or null, and the
    /// symbols it calls directly.
    /// </summary>
    public sealed record Fact(string Name, string Display, bool Handler, string? Allocates, string[] Calls, string[] Takes);

    /// <summary>
    /// A SPIN LOCK A METHOD TAKES ITSELF, as a Fact's Takes word it: the
    /// lock's kind (SpinLock, or IrqSpinLock, which shuts interrupts off),
    /// what it is ("Owner._gate"), and where.
    /// </summary>
    public static string Take(string kind, string what, string where) => kind + "|" + what + "|" + where;

    public static void Attach(ObjectFile obj, IReadOnlyList<Fact> facts) => Attach(obj, Encode(facts));

    /// <summary>
    /// The facts as the section holds them. A compiler keeps them so from the
    /// end of binding to the writing of its object: as records, every name a
    /// string and every list an array, they were twice the section's size,
    /// held through the whole of lowering.
    /// </summary>
    public static byte[] Encode(IReadOnlyList<Fact> facts)
    {
        // INTO AN ARRAY OF ITS EXACT SIZE, counted first: a large unit's facts
        // are megabytes, and a stream grown by doubling to them, then copied
        // out, left several times that to the collector.
        long size = 12;
        int handlers = 0;
        foreach (Fact f in facts)
        {
            size += Written(f.Name) + Written(f.Display) + 1 + Written(f.Allocates ?? "") + 4 + 4;
            foreach (string call in f.Calls) size += Written(call);
            foreach (string take in f.Takes) size += Written(take);
            if (f.Handler) handlers++;
        }
        byte[] bytes = new byte[checked((int)size)];
        using MemoryStream stream = new(bytes);
        using (BinaryWriter writer = new(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(Version);
            writer.Write(handlers);
            writer.Write(facts.Count);
            foreach (Fact f in facts)
            {
                writer.Write(f.Name);
                writer.Write(f.Display);
                writer.Write(f.Handler);
                writer.Write(f.Allocates ?? "");
                writer.Write(f.Calls.Length);
                foreach (string call in f.Calls) writer.Write(call);
                writer.Write(f.Takes.Length);
                foreach (string take in f.Takes) writer.Write(take);
            }
        }
        if (stream.Position != bytes.Length) throw new InvalidOperationException("interrupt notes: counted " + bytes.Length + " bytes, wrote " + stream.Position);
        return bytes;
    }

    // What BinaryWriter.Write(string) writes: its UTF-8 length as seven-bit
    // groups, then the bytes.
    private static long Written(string text)
    {
        int length = Encoding.UTF8.GetByteCount(text);
        int prefix = 1;
        for (uint rest = (uint)length >> 7; rest != 0; rest >>= 7) prefix++;
        return prefix + length;
    }

    /// <summary>Facts already in the section's form (Encode).</summary>
    public static void Attach(ObjectFile obj, byte[] encoded)
    {
        Section section = new(SectionName, SectionKind.Note) { Align = 1 };
        section.Bytes.AddRange(encoded);
        obj.Sections.Add(section);
    }

    /// <summary>How many interrupt handlers the object's facts name: its header's count alone, read.</summary>
    public static int HandlerCount(ObjectFile obj)
    {
        Section? section = obj.Sections.FirstOrDefault(s => s.Name == SectionName);
        if (section is null) return 0;
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
        using BinaryReader reader = new(stream, Encoding.UTF8);
        if (reader.ReadInt32() != Version) throw new ElfFormatException("Unknown " + SectionName + " version");
        reader.ReadInt32();
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
            string[] takes = new string[reader.ReadInt32()];
            for (int k = 0; k < takes.Length; k++) takes[k] = reader.ReadString();
            facts.Add(new Fact(name, display, handler, allocates.Length == 0 ? null : allocates, calls, takes));
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
    public static List<string> Check(IEnumerable<ObjectFile> objects, IReadOnlySet<string>? neverRun = null)
    {
        // NO HANDLER ANYWHERE, NOTHING TO WALK: each object's header says how
        // many it has, and a link of none -- every program but a kernel's --
        // reads no fact. Read whole, the compiler's own link parsed half a
        // gigabyte of facts it had no handler to ask about.
        List<ObjectFile> all = objects.ToList();
        if (all.All(obj => HandlerCount(obj) == 0)) return new List<string>();
        Dictionary<string, Fact> facts = new(StringComparer.Ordinal);
        List<Fact> handlers = new();
        foreach (ObjectFile obj in all)
        {
            List<Fact>? read = Read(obj);
            if (read is null) continue;
            foreach (Fact f in read)
            {
                facts.TryAdd(f.Name, f);
                if (f.Handler) handlers.Add(f);
            }
        }
        // A STATIC THE LINK LAYS DOWN MADE (Preinit) is never made at run
        // time: its type's initialiser skips the step that would make it.
        if (neverRun is not null) foreach (string step in neverRun) facts.Remove(step);
        List<string> errors = new();
        if (handlers.Count == 0) return errors;
        // What each method reaches, once: the allocation and the method that
        // makes it, or null for nothing.
        // NOTHING FOUND PAST A METHOD STILL BEING WALKED IS NO ANSWER YET
        // (Binder.AllocatesThrough): kept only once its cycle is closed, so
        // the answer for one is the same whichever handler asked it first.
        // A METHOD STAYS OPEN UNTIL ITS CYCLE CLOSES, as Tarjan's walk keeps
        // it: let go with no answer, every other way into it walked it again,
        // and a dense cycle that allocates nothing was walked once a path --
        // exponentially many times.
        Dictionary<string, (string What, Fact Where)?> known = new(StringComparer.Ordinal);
        Dictionary<string, int> open = new(StringComparer.Ordinal);
        List<string> stack = new();
        int met = 0;
        (string What, Fact Where)? Reaches(Fact f, ref int low)
        {
            if (known.TryGetValue(f.Name, out var had)) return had;
            if (open.TryGetValue(f.Name, out int number)) { low = Math.Min(low, number); return null; }
            int mine = met++, at = stack.Count, reached = mine;
            open[f.Name] = mine;
            stack.Add(f.Name);
            (string What, Fact Where)? found = f.Allocates is string what ? (what, f) : null;
            if (found is null)
            {
                foreach (string call in f.Calls)
                {
                    if (!facts.TryGetValue(call, out Fact? callee)) continue;
                    (string What, Fact Where)? deeper = Reaches(callee, ref reached);
                    if (deeper.HasValue) { found = deeper; break; }
                }
            }
            // An allocation found is this one's answer; the methods still open
            // above it reach it too, and are let go to find their own. A cycle
            // closed here with none is every member's answer.
            if (found.HasValue || reached == mine)
            {
                if (found.HasValue) known[f.Name] = found;
                for (int i = at; i < stack.Count; i++)
                {
                    open.Remove(stack[i]);
                    if (!found.HasValue) known[stack[i]] = null;
                }
                stack.RemoveRange(at, stack.Count - at);
            }
            low = Math.Min(low, reached);
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
                int low = int.MaxValue;
                (string What, Fact Where)? found = Reaches(callee, ref low);
                if (!found.HasValue) continue;
                string inner = ReferenceEquals(found.Value.Where, callee) ? "" : $" (in '{found.Value.Where.Display}', which it reaches)";
                errors.Add($"'{handler.Display}' is an interrupt handler and must not allocate: '{callee.Display}' {found.Value.What}{inner}");
                break;
            }
        }
        if (errors.Count > 0) errors.AddRange(EveryAllocation(handlers, facts));
        errors.AddRange(SpinLocksFromHandlers(handlers, facts, report: false));
        return errors;
    }

    /// <summary>
    /// A SPIN LOCK THAT KEEPS INTERRUPTS ON IS NO HANDLER'S (Linux's
    /// spin_lock against spin_lock_irqsave): an interrupt that lands on its
    /// holder and takes it spins for ever on its own processor. Each
    /// SpinLock a handler reaches, with the way it gets there; with
    /// `report`, instead, every IrqSpinLock taken anywhere and whether any
    /// handler reaches it (one that none does may keep interrupts on).
    /// </summary>
    public static List<string> SpinLocksFromHandlers(List<Fact> handlers, Dictionary<string, Fact> facts, bool report)
    {
        List<string> lines = new();
        Dictionary<string, string?> cameFrom = new(StringComparer.Ordinal);
        Queue<Fact> work = new();
        foreach (Fact handler in handlers)
            if (cameFrom.TryAdd(handler.Name, null)) work.Enqueue(handler);
        HashSet<string> said = new(StringComparer.Ordinal);
        while (work.Count > 0)
        {
            Fact f = work.Dequeue();
            foreach (string take in f.Takes)
            {
                string[] parts = take.Split('|');
                if (parts.Length != 3 || parts[0] != (report ? "IrqSpinLock" : "SpinLock") || !said.Add(parts[1] + " " + parts[2])) continue;
                List<string> path = new();
                for (string? at = f.Name; at is not null; at = cameFrom[at])
                    path.Add(facts.TryGetValue(at, out Fact? step) ? step.Display : at);
                path.Reverse();
                lines.Add(report
                    ? $"irq locks: IrqSpinLock '{parts[1]}' at {parts[2]}: reached from an interrupt handler, via {string.Join(" -> ", path)}"
                    : $"'{path[0]}' is an interrupt handler and reaches SpinLock.Enter on '{parts[1]}' at {parts[2]}, via {string.Join(" -> ", path)}; use an IrqSpinLock");
            }
            foreach (string call in f.Calls)
            {
                if (!facts.TryGetValue(call, out Fact? callee)) continue;
                if (cameFrom.TryAdd(callee.Name, f.Name)) work.Enqueue(callee);
            }
        }
        if (report)
            foreach (Fact f in facts.Values)
                foreach (string take in f.Takes)
                {
                    string[] parts = take.Split('|');
                    if (parts.Length == 3 && parts[0] == "IrqSpinLock" && !cameFrom.ContainsKey(f.Name) && said.Add(parts[1] + " " + parts[2]))
                        lines.Add($"irq locks: IrqSpinLock '{parts[1]}' at {parts[2]}: no interrupt handler reaches it");
                }
        return lines;
    }

    /// <summary>--irq-locks-report: every IrqSpinLock taken, and whether an interrupt handler reaches it.</summary>
    public static List<string> IrqLocksReport(IEnumerable<ObjectFile> objects)
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
        return SpinLocksFromHandlers(handlers, facts, report: true);
    }

    /// EVERY METHOD THAT ALLOCATES AND A HANDLER REACHES, each once, with the
    /// path from the first handler found to reach it: the errors above name
    /// only the first allocation on each handler's way, so fixing them one
    /// build at a time found the next one each time.
    static List<string> EveryAllocation(List<Fact> handlers, Dictionary<string, Fact> facts)
    {
        List<string> lines = new();
        Dictionary<string, string?> cameFrom = new(StringComparer.Ordinal);
        Queue<Fact> work = new();
        foreach (Fact handler in handlers)
        {
            if (cameFrom.TryAdd(handler.Name, null)) work.Enqueue(handler);
        }
        while (work.Count > 0)
        {
            Fact f = work.Dequeue();
            if (f.Allocates is string what)
            {
                List<string> path = new();
                for (string? at = f.Name; at is not null; at = cameFrom[at])
                    path.Add(facts.TryGetValue(at, out Fact? step) ? step.Display : at);
                path.Reverse();
                lines.Add($"  allocation reached from an interrupt handler: '{f.Display}' {what}; via {string.Join(" -> ", path)}");
            }
            foreach (string call in f.Calls)
            {
                if (!facts.TryGetValue(call, out Fact? callee)) continue;
                if (cameFrom.TryAdd(callee.Name, f.Name)) work.Enqueue(callee);
            }
        }
        return lines;
    }

    public static void Strip(IEnumerable<ObjectFile> objects)
    {
        foreach (ObjectFile obj in objects) obj.Sections.RemoveAll(section => section.Name == SectionName);
    }
}
