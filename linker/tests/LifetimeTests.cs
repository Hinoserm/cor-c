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
        a.Helpers.Add("m_Runtime_Free_1_V$I64");
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
        Check(back.Functions.Count == 5 && back.Pending.Count == 2 && back.Helpers.Single() == "m_Runtime_Free_1_V$I64", "hint round trip counts");
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
        Check(facts.Helpers.Contains("m_Runtime_Free_1_V$I64"), "facts carry the unit's frees");

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

        // Hints ride in an object and are consumed by the link.
        ObjectFile obj = new(); Section text = new(".text", SectionKind.Code); text.Bytes.Add(0xc3); obj.Sections.Add(text);
        a.Attach(obj);
        Check(LifetimeHints.Read(obj)!.Functions.Count == 5, "hints attached to an object");
        Console.WriteLine("  lifetime hint format, whole-program solve, cycles, unknown callees and backend facts passed");
    }
}
