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

    /// <summary>
    /// A SLOT'S FACT is named by this and the definition of the method that
    /// declares the slot (Binder.SlotOf): a call through the slot -- virtual,
    /// abstract, an interface's -- is a call to it, and it calls every method
    /// a unit knows to fill it. Every unit's facts for one slot are joined at
    /// the link. Every check follows them: a clock tick reached a socket's
    /// SpinLock through one, and the spin-lock check never saw it.
    /// </summary>
    public const string Dispatch = "~";

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
        return Decode(stream);
    }

    /// <summary>
    /// FACTS BESIDE AN IMAGE OTHERS LINK AGAINST: a kernel's exports, a
    /// module another module links with (--link-shared). Neither carries its
    /// units' facts -- a module's would be several times its own size -- so
    /// a module's link saw nothing past the edge, and every wait the kernel
    /// or the seat's module has was invisible from it. The image's link
    /// writes them beside it (path + ".irq"), every unit's joined, its
    /// handlers marked -- a kernel's handler reaches a module's code through
    /// a slot, and the module's link is where that is seen -- and a link reading the image
    /// reads them too (ReadBeside). With no facts, one left from before is
    /// taken away.
    /// </summary>
    public static void WriteBeside(string path, IEnumerable<ObjectFile> objects)
    {
        string beside = path + ".irq";
        List<Fact> all = Gather(objects);
        if (all.Count == 0) { if (File.Exists(beside)) File.Delete(beside); return; }
        File.WriteAllBytes(beside, Encode(all));
    }

    /// <summary>The facts an image's link wrote beside it (WriteBeside), or none.</summary>
    public static List<Fact> ReadBeside(string path)
    {
        string beside = path + ".irq";
        if (!File.Exists(beside)) return new List<Fact>();
        using FileStream stream = File.OpenRead(beside);
        return Decode(stream);
    }

    /// <summary>Every object's facts, one each by name (a slot's fillers joined), its handlers still handlers.</summary>
    public static List<Fact> Gather(IEnumerable<ObjectFile> objects)
    {
        Dictionary<string, Fact> facts = new(StringComparer.Ordinal);
        foreach (ObjectFile obj in objects)
            if (Read(obj) is { } read)
                foreach (Fact f in read) Add(facts, f);
        return facts.Values.ToList();
    }

    // One fact by name; a slot's met again adds the fillers it names.
    private static void Add(Dictionary<string, Fact> facts, Fact f)
    {
        if (!facts.TryAdd(f.Name, f) && f.Name.StartsWith(Dispatch, StringComparison.Ordinal))
            facts[f.Name] = facts[f.Name] with { Calls = facts[f.Name].Calls.Union(f.Calls, StringComparer.Ordinal).ToArray() };
    }

    private static List<Fact> Decode(Stream stream)
    {
        using BinaryReader reader = new(stream, Encoding.UTF8, leaveOpen: true);
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

    /// <summary>What an interrupt handler's report says it must not do, by what was found -- the compile's words and the link's alike: open a packed block (PackedBlock; the facts carry one as an allocation) or allocate.</summary>
    public static string Forbidden(string what)
        => what.StartsWith(PackedBlock, StringComparison.Ordinal) ? "must not use the packed registers"
         : what.StartsWith(FloatCode, StringComparison.Ordinal) ? "must not use float code"
         : "must not allocate";

    /// <summary>How a packed operation's report begins (Binder.AllocationsIn), which Forbidden tells it by.</summary>
    public const string PackedBlock = "uses the packed registers";

    /// <summary>How float code's report begins, freestanding.</summary>
    public const string FloatCode = "does float code";

    /// <summary>
    /// Every handler of every unit walked through every unit's facts: what
    /// each that reaches an allocation reaches, as an error naming the
    /// handler, the method it calls, and the allocation. Methods no unit
    /// describes -- assembled code, a unit compiled without facts -- are not
    /// followed.
    /// </summary>
    public static List<string> Check(IEnumerable<ObjectFile> objects, IReadOnlySet<string>? neverRun = null, IReadOnlyList<Fact>? imported = null)
    {
        // NO HANDLER ANYWHERE, NOTHING TO WALK: each object's header says how
        // many it has, and a link of none -- every program but a kernel's --
        // reads no fact. Read whole, the compiler's own link parsed half a
        // gigabyte of facts it had no handler to ask about.
        List<ObjectFile> all = objects.ToList();
        bool rooted = Corsac.Switches.IrqWaits is { Length: > 0 } && Corsac.Switches.WaitFreeRoots is { Length: > 0 }
            || Corsac.Switches.IrqRoots is { Length: > 0 };
        if (!rooted && all.All(obj => HandlerCount(obj) == 0)) return new List<string>();
        Dictionary<string, Fact> facts = new(StringComparer.Ordinal);
        List<Fact> handlers = new();
        foreach (ObjectFile obj in all)
        {
            List<Fact>? read = Read(obj);
            if (read is null) continue;
            foreach (Fact f in read)
            {
                // A slot filled in several units: every unit's fillers.
                Add(facts, f);
                if (f.Handler) handlers.Add(f);
            }
        }

        // AND WHAT THE IMAGES THIS ONE LINKS AGAINST SAY (ReadBeside): the
        // kernel's, a shared module's, after the link's own -- their
        // handlers too, whose slots reach this link's code.
        if (imported is not null)
            foreach (Fact f in imported)
            {
                bool fresh = !facts.ContainsKey(f.Name);
                Add(facts, f);
                if (fresh && f.Handler) handlers.Add(f);
            }
        // A STATIC THE LINK LAYS DOWN MADE (Preinit) is never made at run
        // time: its type's initialiser skips the step that would make it.
        if (neverRun is not null) foreach (string step in neverRun) facts.Remove(step);
        List<string> errors = new();
        // AND WHATEVER ELSE RUNS IN AN INTERRUPT (--irq-roots): a clock
        // tick's reschedule, an interrupt's dispatch, an IPI's work -- no
        // handler by its attribute, held to all a handler is.
        if (Corsac.Switches.IrqRoots is { Length: > 0 } irqRoots)
        {
            string[] named = Names(irqRoots);
            HashSet<string> found = new(StringComparer.Ordinal);
            foreach (Fact f in facts.Values.ToList())
            {
                if (f.Handler || !Matches(named, f.Display)) continue;
                Fact root = f with { Handler = true };
                facts[f.Name] = root;
                handlers.Add(root);
                foreach (string name in named) if (Matches(new[] { name }, f.Display)) found.Add(name);
            }
            foreach (string name in named)
                if (!found.Contains(name)) errors.Add($"--irq-roots: no method '{name}' in the link");
        }
        if (handlers.Count == 0 && !rooted) return errors;
        // NOT FOLLOWED BY THE INTERRUPT CHECKS (--irq-stop): code that runs
        // on another stack, or with interrupts as it says.
        string[] irqStop = Corsac.Switches.IrqStop is { Length: > 0 } stops ? Names(stops) : Array.Empty<string>();
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
                    if (!facts.TryGetValue(call, out Fact? callee) || Matches(irqStop, callee.Display)) continue;
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
                errors.Add($"'{handler.Display}' is an interrupt handler and {Forbidden(own)}: here it {own}");
                continue;
            }
            foreach (string call in handler.Calls)
            {
                if (!facts.TryGetValue(call, out Fact? callee) || Matches(irqStop, callee.Display)) continue;
                int low = int.MaxValue;
                (string What, Fact Where)? found = Reaches(callee, ref low);
                if (!found.HasValue) continue;
                string inner = ReferenceEquals(found.Value.Where, callee) ? "" : $" (in '{found.Value.Where.Display}', which it reaches)";
                errors.Add($"'{handler.Display}' is an interrupt handler and {Forbidden(found.Value.What)}: '{callee.Display}' {found.Value.What}{inner}");
                break;
            }
        }
        if (errors.Count > 0) errors.AddRange(EveryAllocation(handlers, facts, irqStop));
        errors.AddRange(SpinLocksFromHandlers(handlers, facts, report: false, irqStop));
        if (Corsac.Switches.IrqWaits is { Length: > 0 } waits)
        {
            // AND WHATEVER ELSE MAY NEVER WAIT (--wait-free-roots): a kernel's
            // scheduler pass, which runs on no thread of its own to park.
            List<Fact> roots = new(handlers);
            if (Corsac.Switches.WaitFreeRoots is { Length: > 0 } more)
            {
                HashSet<string> named = new(Names(more), StringComparer.Ordinal);
                HashSet<string> found = new(StringComparer.Ordinal);
                foreach (Fact f in facts.Values)
                    if (named.Contains(f.Display)) { roots.Add(f); found.Add(f.Display); }
                // A ROOT NAMED WRONG checks nothing, quietly: say so.
                foreach (string name in named)
                    if (!found.Contains(name)) errors.Add($"--wait-free-roots: no method '{name}' in the link");
            }
            errors.AddRange(WaitsFromHandlers(roots, facts, waits, Corsac.Switches.WaitFreeStop));
        }
        return errors;
    }

    /// <summary>
    /// NO HANDLER WAITS (--irq-waits Type.Method,...): an interrupt handler
    /// that parks or yields runs on the block of whatever it interrupted, and
    /// a wait there turns a thread the collector counts as stopped into a
    /// running one it waits for (GcThreads.EndBlocking) -- or simply parks the
    /// processor with interrupts off. Each named method a handler reaches,
    /// through any of the calls the facts follow, with the way there. A
    /// TryEnter that does not wait is named by not being listed.
    /// </summary>
    public static List<string> WaitsFromHandlers(List<Fact> handlers, Dictionary<string, Fact> facts, string waits, string? stops = null)
    {
        string[] named = Names(waits);
        // NOT FOLLOWED (--wait-free-stop): a call that goes on on another
        // stack -- a parked continuation resumed -- is not the caller waiting.
        string[] stop = stops is null ? Array.Empty<string>() : Names(stops);
        List<string> lines = new();
        HashSet<string> said = new(StringComparer.Ordinal);
        Dictionary<string, string?> cameFrom = new(StringComparer.Ordinal);
        Queue<Fact> work = new();
        foreach (Fact handler in handlers)
            if (cameFrom.TryAdd(handler.Name, null)) work.Enqueue(handler);
        // SAID WHERE A WAIT IS CALLED, once for each method that calls it,
        // by the shortest way to that method: every place that waits, not
        // one way to each wait. A wait with facts (the kernel's, beside its
        // exports) and one without -- matched by the name its definition
        // spells -- are said alike, and neither is walked into.
        while (work.Count > 0)
        {
            Fact f = work.Dequeue();
            foreach (string call in f.Calls)
            {
                facts.TryGetValue(call, out Fact? callee);
                string? shown = callee?.Display ?? (call.StartsWith(Dispatch, StringComparison.Ordinal) ? null : DisplayOf(call));
                if (shown is null || Matches(stop, shown)) continue;
                if (Matches(named, shown))
                {
                    if (!said.Add(f.Name + "\n" + shown)) continue;
                    List<string> way = new() { shown };
                    for (string? at = f.Name; at is not null; at = cameFrom[at])
                        way.Add(facts.TryGetValue(at, out Fact? step) ? step.Display : at);
                    way.Reverse();
                    string from = facts.TryGetValue(FirstName(f.Name, cameFrom), out Fact? start) && start.Handler
                        ? "is an interrupt handler" : "may not wait (--wait-free-roots)";
                    lines.Add($"'{way[0]}' {from} and reaches '{shown}', which waits, via {string.Join(" -> ", way)}");
                    continue;
                }
                if (callee is not null && cameFrom.TryAdd(callee.Name, f.Name)) work.Enqueue(callee);
            }
        }
        return lines;
    }

    /// <summary>
    /// A SPIN LOCK THAT KEEPS INTERRUPTS ON IS NO HANDLER'S (Linux's
    /// spin_lock against spin_lock_irqsave): an interrupt that lands on its
    /// holder and takes it spins for ever on its own processor. Each
    /// SpinLock a handler reaches, with the way it gets there; with
    /// `report`, instead, every IrqSpinLock taken anywhere and whether any
    /// handler reaches it (one that none does may keep interrupts on).
    /// </summary>
    public static List<string> SpinLocksFromHandlers(List<Fact> handlers, Dictionary<string, Fact> facts, bool report, string[]? stop = null)
    {
        stop ??= Array.Empty<string>();
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
                if (!facts.TryGetValue(call, out Fact? callee) || Matches(stop, callee.Display)) continue;
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

    private static string[] Names(string list) => list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    // A NAME ENDING IN '*' names every method it begins (FilesystemGuard.Enter*),
    // one beginning with it every method it ends (*.StaticInit$: every type's
    // initialiser), one with both every method it holds.
    private static bool Matches(string[] names, string display)
    {
        foreach (string name in names)
        {
            bool head = name.StartsWith('*'), tail = name.Length > 1 && name.EndsWith('*');
            string core = name[(head ? 1 : 0)..(tail ? name.Length - 1 : name.Length)];
            if (head && tail ? display.Contains(core, StringComparison.Ordinal)
                : head ? display.EndsWith(core, StringComparison.Ordinal)
                : tail ? display.StartsWith(core, StringComparison.Ordinal)
                : display == name) return true;
        }
        return false;
    }

    /// <summary>
    /// "Type.Method" from a definition's name (Binder.DefinitionName:
    /// Owner`n.Method`n(parameters)), the owner's last part alone, as a
    /// fact's Display is; null when it is not one.
    /// </summary>
    internal static string? DisplayOf(string definition)
    {
        int open = definition.IndexOf('(');
        if (open < 0) return null;
        string head = definition[..open];
        int tick = head.LastIndexOf('`');
        if (tick < 0) return null;
        head = head[..tick];
        int dot = head.LastIndexOf('.');
        if (dot < 0) return null;
        string owner = head[..dot], method = head[(dot + 1)..];
        int ownerTick = owner.LastIndexOf('`');
        if (ownerTick >= 0) owner = owner[..ownerTick];
        int ownerDot = owner.LastIndexOf('.');
        return (ownerDot >= 0 ? owner[(ownerDot + 1)..] : owner) + "." + method;
    }

    // The root a walk came from (cameFrom's chain to its start).
    private static string FirstName(string name, Dictionary<string, string?> cameFrom)
    {
        string at = name;
        while (cameFrom.TryGetValue(at, out string? up) && up is not null) at = up;
        return at;
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
                Add(facts, f);
                if (f.Handler) handlers.Add(f);
            }
        }
        return SpinLocksFromHandlers(handlers, facts, report: true);
    }

    /// EVERY METHOD THAT ALLOCATES AND A HANDLER REACHES, each once, with the
    /// path from the first handler found to reach it: the errors above name
    /// only the first allocation on each handler's way, so fixing them one
    /// build at a time found the next one each time.
    static List<string> EveryAllocation(List<Fact> handlers, Dictionary<string, Fact> facts, string[] stop)
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
                if (!facts.TryGetValue(call, out Fact? callee) || Matches(stop, callee.Display)) continue;
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
