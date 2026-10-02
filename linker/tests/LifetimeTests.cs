using Corsac.Lang.Elf;
using Corsac.Lang.Ir;
using Corsac.Lang.Lto;

namespace Corsac.Tests.Elf;

/// <summary>Lifetime hints: the section format, the whole-program solve, and the backend's facts on the wire.</summary>
public static class LifetimeTests
{
    public static void Run()
    {
        void Check(bool value, string message) { if (!value) throw new Exception(message); }
        void Reject(byte[] bytes, string what)
        { try { LifetimeHints.Read(bytes); } catch (ElfFormatException) { return; } throw new Exception("Invalid lifetime hints accepted: " + what); }
        LifetimeCondition Stays(params (string, int)[] needs) { LifetimeCondition c = new(); foreach (var need in needs) c.Stays.Add(need); return c; }
        LifetimeCondition Fresh(params string[] callees) { LifetimeCondition c = new(); foreach (string callee in callees) c.Fresh.Add(callee); return c; }

        // Unit A: Lend(p) keeps nothing if B.Read's parameter 0 keeps nothing;
        // Make() is fresh if B.New is. Unit B: Read keeps nothing, New is
        // fresh, Store keeps its argument; Loop(p) and Round(p) call each
        // other and keep nothing between them; Unknown calls nothing summarised.
        LifetimeHints a = new();
        a.Functions.Add(new("Lend", true, new LifetimeCondition?[] { Stays(("Read", 0)) }, null));
        a.Functions.Add(new("Make", true, Array.Empty<LifetimeCondition?>(), Fresh("New")));
        a.Functions.Add(new("Hold", true, new LifetimeCondition?[] { Stays(("Store", 0)) }, null));
        a.Functions.Add(new("Ask", true, new LifetimeCondition?[] { Stays(("Missing", 0)) }, null));
        a.Functions.Add(new("helper", false, new LifetimeCondition?[] { Stays(("Loop", 0)) }, Fresh("Make")));
        a.Pending.Add(Stays(("Read", 0)));
        a.Pending.Add(Stays(("Store", 0)));
        a.Helpers.Add(RuntimeAbi.Free);
        LifetimeHints b = new();
        b.Functions.Add(new("Read", true, new LifetimeCondition?[] { new() }, null));
        b.Functions.Add(new("New", true, Array.Empty<LifetimeCondition?>(), new()));
        b.Functions.Add(new("Store", true, new LifetimeCondition?[] { null }, null));
        b.Functions.Add(new("Loop", true, new LifetimeCondition?[] { Stays(("Round", 0)) }, null));
        b.Functions.Add(new("Round", true, new LifetimeCondition?[] { Stays(("Loop", 0)) }, null));

        // The format: the same hints are the same bytes, and read back alike.
        byte[] bytes = a.Write();
        Check(bytes.AsSpan().SequenceEqual(a.Write()), "lifetime hints are not deterministic");
        LifetimeHints back = LifetimeHints.Read(bytes);
        Check(back.Functions.Count == 5 && back.Pending.Count == 2 && back.Helpers.Single() == RuntimeAbi.Free, "hint round trip counts");
        LifetimeFunction lend = back.Functions.Single(f => f.Name == "Lend");
        Check(lend.Global && lend.Parameters.Single()!.Stays.Single() == ("Read", 0) && lend.Fresh is null, "hint round trip contents");
        Check(back.Functions.Single(f => f.Name == "helper") is { Global: false }, "local function survives the round trip");
        byte[] truncated = bytes[..^1];
        Reject(truncated, "truncated");
        byte[] wrongLength = (byte[])bytes.Clone(); wrongLength[8] ^= 1;
        Reject(wrongLength, "length field");
        LifetimeCondition big = new();
        for (int i = 0; i <= LifetimeCondition.Limit; i++) big.Stays.Add(("f" + i, 0));
        LifetimeHints oversized = new();
        oversized.Pending.Add(big);
        try { oversized.Write(); throw new Exception("an unbounded condition was written"); }
        catch (ElfFormatException) { }

        // The solve: a condition on a function that keeps nothing holds; on
        // one that keeps its argument, or one nobody summarised, it does not;
        // a cycle that only hands the object round keeps nothing.
        LifetimeSolver solver = new(new[] { a, b });
        Check(!solver.Escapes("Lend", 0), "lent to a reader: stays");
        Check(solver.Escapes("Hold", 0), "lent to a keeper: escapes");
        Check(solver.Escapes("Ask", 0), "lent to an unsummarised function: escapes");
        Check(!solver.Escapes("Loop", 0) && !solver.Escapes("Round", 0), "a cycle that keeps nothing: the least fixed point");
        Check(solver.IsFresh("Make") && solver.IsFresh("New"), "fresh through another unit");
        Check(solver.Holds(a.Pending[0]) && !solver.Holds(a.Pending[1]), "pending conditions");
        Check(!solver.Holds(null), "never holds");

        LifetimeFacts facts = solver.For(a, new[] { "Read", "Store", "New", "Missing" });
        Check(!facts.Escapes["Read"][0] && facts.Escapes["Store"][0] && !facts.Escapes.ContainsKey("Missing"), "facts for the callees");
        Check(!facts.Escapes["helper"][0] && facts.Fresh.Contains("helper") && facts.Fresh.Contains("New"), "facts for the unit's own local function");
        Check(facts.Helpers.Contains(RuntimeAbi.Free), "facts carry the unit's frees");

        // Solving in a different unit order changes nothing but which
        // definition a duplicated name resolves to.
        LifetimeSolver reversed = new(new[] { b, a });
        foreach (string name in new[] { "Lend", "Hold", "Ask", "Loop", "Round" })
            Check(reversed.Escapes(name, 0) == solver.Escapes(name, 0), "order-dependent solve: " + name);

        // On the wire to the backend.
        using MemoryStream wire = new();
        using (BinaryWriter writer = new(wire, BackendProtocol.Utf8, leaveOpen: true))
            BackendProtocol.WriteRequest(writer, new("in.o", "out.o", Array.Empty<IrImport>(), null, facts));
        wire.Position = 0;
        using BinaryReader reader = new(wire, BackendProtocol.Utf8, leaveOpen: true);
        BackendRequest request = BackendProtocol.ReadRequest(reader)!;
        Check(request.Facts is LifetimeFacts f2 && f2.Escapes.Count == facts.Escapes.Count && f2.Escapes["Store"][0]
            && f2.Fresh.SetEquals(facts.Fresh) && f2.Helpers.SetEquals(facts.Helpers), "facts round trip on the backend wire");

        // Fields: what each function does to its argument's fields, solved.
        // Fill(box) fills offset 8 freshly; Pass(box) hands it to Fill;
        // Lost(box) hands it to a function nobody summarised; Mixed(box)
        // stores New()'s object at 16 and Shared()'s at 24, and hands what it
        // loads from 32 to Read and from 40 to Store; New returns a fresh
        // object whose offset 8 it filled.
        LifetimeFields Local(Action<LifetimeFields> fill) { LifetimeFields f = new(); fill(f); return f; }
        LifetimeHints c = new();
        LifetimeCondition stay = new();
        c.Functions.Add(new("Fill", true, new LifetimeCondition?[] { stay }, null,
            new LifetimeFields?[] { Local(f => f.Fresh.Add(8)) }));
        c.Functions.Add(new("Pass", true, new LifetimeCondition?[] { Stays(("Fill", 0)) }, null,
            new LifetimeFields?[] { Local(f => f.Merges.Add(("Fill", 0))) }));
        c.Functions.Add(new("Lost", true, new LifetimeCondition?[] { Stays(("Nowhere", 0)) }, null,
            new LifetimeFields?[] { Local(f => f.Merges.Add(("Nowhere", 0))) }));
        c.Functions.Add(new("Mixed", true, new LifetimeCondition?[] { stay }, null, new LifetimeFields?[] { Local(f =>
        {
            f.Conditional.Add((16, Fresh("New"), true));
            f.Conditional.Add((24, Fresh("Shared"), true));
            f.Conditional.Add((32, Stays(("Read", 0)), false));
            f.Conditional.Add((40, Stays(("Store", 0)), false));
            f.Fresh.Add(32); f.Fresh.Add(40);
        }) }));
        c.Functions.Add(new("Make2", true, Array.Empty<LifetimeCondition?>(), Fresh("New"), null,
            Local(f => f.Merges.Add(("New", -1)))));
        b.Functions.RemoveAll(f => f.Name == "New");
        b.Functions.Add(new("New", true, Array.Empty<LifetimeCondition?>(), new(), null, Local(f => f.Fresh.Add(8))));
        c.FieldSites.Add((Local(f => f.Merges.Add(("Fill", 0))), new() { ("__corsac_field$t$0", 8), ("__corsac_field$t$1", 12) }));

        LifetimeHints cBack = LifetimeHints.Read(c.Write());
        Check(cBack.Functions.Single(f => f.Name == "Mixed").ParameterFields![0]!.Conditional.Count == 4, "field conditions round trip");
        Check(cBack.Functions.Single(f => f.Name == "Make2").FreshFields!.Merges.Single() == ("New", -1), "fresh-return merges round trip");
        Check(cBack.FieldSites.Single().Sites.Count == 2 && cBack.FieldSites.Single().Sites[1] == ("__corsac_field$t$1", 12), "field sites round trip");

        LifetimeSolver fields = new(new[] { a, b, c });
        Check(fields.FieldsOf("Pass", 0) is { Opaque: false } pass && pass.Fresh.SequenceEqual(new long[] { 8 }), "fields merged across units");
        Check(fields.FieldsOf("Lost", 0) is null && fields.Escapes("Lost", 0), "handed to an unknown function: escapes, no fields");
        SolvedFields mixed = fields.FieldsOf("Mixed", 0)!;
        Check(mixed.Fresh.Contains(16) && mixed.Dirty.Contains(24), "a stored child is fresh only if its maker is");
        Check(!mixed.Dirty.Contains(32) && mixed.Dirty.Contains(40), "a loaded child stays clean only if its taker keeps nothing");
        Check(fields.FreshFieldsOf("Make2") is { Opaque: false } made && made.Fresh.Contains(8), "a fresh return's fields merged from its maker");
        SolvedFields site = fields.Solve(cBack.FieldSites.Single().Fields)!;
        Check(!site.Opaque && site.Fresh.Contains(8) && !site.Fresh.Contains(12), "a field site's summary: 8 clean, 12 not");
        LifetimeCondition needsFields = new(); needsFields.Fields.Add(("Pass", 0));
        LifetimeCondition needsLost = new(); needsLost.Fields.Add(("Lost", 0));
        Check(fields.Holds(needsFields) && !fields.Holds(needsLost), "field requirements");

        // Kept only in the box returned: Box(p) escapes p into the box it
        // returns at offset 8, if Read keeps nothing; BoxLost(p) does, if
        // Store does (it does not); Plain(p) keeps nothing at all. A virtual
        // call reaching Box and Plain holds its argument at 8; one reaching
        // BoxLost as well does not. The answers survive the format, the
        // solve, the facts and the backend wire.
        LifetimeHints d = new();
        d.Functions.Add(new("Box", true, new LifetimeCondition?[] { null }, null, null, null,
            new LifetimeHeld?[] { new(Stays(("Read", 0)), new long[] { 8 }) }));
        d.Functions.Add(new("BoxLost", true, new LifetimeCondition?[] { null }, null, null, null,
            new LifetimeHeld?[] { new(Stays(("Store", 0)), new long[] { 8 }) }));
        d.Functions.Add(new("Plain", true, new LifetimeCondition?[] { new() }, null));
        LifetimeHints dBack = LifetimeHints.Read(d.Write());
        Check(dBack.Functions.Single(f => f.Name == "Box").Held is [LifetimeHeld { Offsets: [8] } heldHint] && heldHint.Condition.Stays.Single() == ("Read", 0),
            "held hints round trip");
        Check(dBack.Functions.Single(f => f.Name == "Plain").Held is null, "no held hints round trip");
        Dictionary<string, string[]> heldVirtuals = new() { ["v$walk"] = new[] { "Box", "Plain" }, ["v$lost"] = new[] { "Box", "BoxLost" } };
        LifetimeHints asks = new();
        asks.Pending.Add(Stays(("v$walk", 0), ("v$lost", 0)));
        LifetimeSolver held = new(new[] { a, b, dBack, asks }, heldVirtuals);
        Check(held.Escapes("Box", 0) && held.HeldAt("Box", 0) is [8], "escaping only into its box: held at 8");
        Check(held.HeldAt("BoxLost", 0) is null && held.HeldAt("Plain", 0) is null, "held only where it holds and escapes");
        Check(held.HeldAt("v$walk", 0) is [8] && held.HeldAt("v$lost", 0) is null, "a virtual call holds only if every override does or keeps nothing");
        LifetimeFacts heldFacts = held.For(dBack, new[] { "v$walk", "Read" });
        Check(heldFacts.Held["Box"] is [[8]] && heldFacts.Held["v$walk"] is [[8]] && !heldFacts.Held.ContainsKey("BoxLost"), "held facts for the unit");
        using MemoryStream heldWire = new();
        using (BinaryWriter writer = new(heldWire, BackendProtocol.Utf8, leaveOpen: true))
            BackendProtocol.WriteRequest(writer, new("in.o", "out.o", Array.Empty<IrImport>(), null, heldFacts));
        heldWire.Position = 0;
        using (BinaryReader heldReader = new(heldWire, BackendProtocol.Utf8, leaveOpen: true))
            Check(BackendProtocol.ReadRequest(heldReader)!.Facts is LifetimeFacts f3 && f3.Held["v$walk"] is [[8]] && f3.Held.Count == heldFacts.Held.Count,
                "held facts round trip on the backend wire");

        // Hints ride in an object and are consumed by the link.
        ObjectFile obj = new(); Section text = new(".text", SectionKind.Code); text.Bytes.Add(0xc3); obj.Sections.Add(text);
        a.Attach(obj);
        Check(LifetimeHints.Read(obj)!.Functions.Count == 5, "hints attached to an object");
        // A closed image keeps the runtime's frees for the units the link
        // regenerates: their new code calls them though the archived IR did not.
        ObjectFile Unit(string[] functions, LifetimeHints? unitHints)
        {
            ObjectFile made = new(); Section code = new(".text", SectionKind.Code);
            code.Bytes.AddRange(new byte[functions.Length]); made.Sections.Add(code);
            for (int i = 0; i < functions.Length; i++)
                made.Symbols.Add(new Symbol { Name = functions[i], IsFunction = true, Section = code, Offset = i, Size = 1 });
            unitHints?.Attach(made);
            IrArchive.Attach(made, functions.Select(name => new IrArchiveRecord("F:" + name, false, 1, Array.Empty<string>(), new byte[] { 1 })).ToArray());
            return made;
        }
        LifetimeHints gaining = new();
        gaining.Helpers.Add("helper");
        gaining.Pending.Add(new LifetimeCondition());
        List<(string Name, ObjectFile Object)> closed = new() { ("app", Unit(new[] { "entry" }, gaining)), ("rt", Unit(new[] { "helper", "other" }, null)) };
        PruningBackend pruning = new();
        IrLinkOptimizer.Run(closed, () => pruning, closedImageEntry: "entry");
        Check(pruning.Retained.Any(kept => kept is not null && kept.Contains("F:helper") && !kept.Contains("F:other")),
            "a closed image keeps the free a regenerated unit will call, and drops what nothing calls");

        Console.WriteLine("  lifetime hint format, whole-program solve, cycles, unknown callees, fields, field sites, held boxes, closed-image roots and backend facts passed");
    }
}

/// <summary>Answers a regeneration with the unit cut down to what it was asked to keep, and remembers what that was.</summary>
internal sealed class PruningBackend : IUnitBackend
{
    public List<IReadOnlySet<string>?> Retained { get; } = new();
    public ObjectFile Recompile(ObjectFile original, IReadOnlyList<IrImport> imports, IReadOnlySet<string>? retained = null,
        LifetimeFacts? facts = null)
    {
        Retained.Add(retained);
        ObjectFile kept = new();
        kept.Sections.AddRange(original.Sections.Where(section => section.Name != IrArchive.SectionName && section.Name != LifetimeHints.SectionName));
        kept.Symbols.AddRange(original.Symbols.Where(symbol => !symbol.Global || retained is null
            || retained.Contains("F:" + symbol.Name) || retained.Contains("D:" + symbol.Name)));
        return kept;
    }
}
