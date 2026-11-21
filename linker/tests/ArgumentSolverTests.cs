using Corsac.Lang.Lto;

namespace Corsac.Tests.Elf;

/// <summary>
/// The whole program's arguments where slots' families meet calls nothing
/// names (ArgumentSolver): which families lose a parameter every call
/// passes alike, and which are held -- by an unnamed call of as many
/// arguments or more, a slot call the link has no answer for, a call
/// through a static cell written with what is not a named address -- and
/// the hints' format for them.
/// </summary>
public static class ArgumentSolverTests
{
    const string Big = VirtualTargets.Prefix + "Big+48", Small = VirtualTargets.Prefix + "Small+52",
        Raw = VirtualTargets.Prefix + "Raw+56", Gone = VirtualTargets.Prefix + "Gone+60", Dead = VirtualTargets.Prefix + "Dead+64",
        Fix = VirtualTargets.Prefix + "Fix+68", Mis = VirtualTargets.Prefix + "Mis+72";

    public static void Run()
    {
        void Check(bool value, string message) { if (!value) throw new Exception(message); }

        // Big's family (five parameters) is always passed 7 last; Small's
        // (two) 3; Raw's (five) 1, 2, 3, 4. Gone's slot has no answer and
        // is called with three arguments; Raw's also with unknown ones; and
        // the hook cell s_Hook is called with five. Dead's family (three)
        // never reads its last parameter.
        ArgumentHints Unit(bool cellWritten, bool installedUnknown = false, bool installCalled = true)
        {
            ArgumentHints unit = new();
            ArgumentFunction main = new(0) { Fixed = true };
            ArgumentValue u = default;
            main.Calls.Add(new ArgumentCall(Big, 0, 1, new[] { u, u, u, u, ArgumentValue.Of(7) }));
            main.Calls.Add(new ArgumentCall(Small, 0, 1, new[] { u, ArgumentValue.Of(3) }));
            main.Calls.Add(new ArgumentCall(Raw, 0, 1, new[] { u, ArgumentValue.Of(1), ArgumentValue.Of(2), ArgumentValue.Of(3), ArgumentValue.Of(4) }));
            main.Calls.Add(new ArgumentCall(Gone, 0, 1, new[] { u, u, u }));
            main.Calls.Add(new ArgumentCall(Dead, 0, 1, new[] { u, u, u }));
            main.Calls.Add(new ArgumentCall(Fix, 0, 1, new[] { u, u, u }));
            // (MisA called by name with two arguments, though it has three: a misfit.)
            main.Calls.Add(new ArgumentCall(Mis, 0, 1, new[] { u, u, u }));
            main.Calls.Add(new ArgumentCall("MisA", 0, 1, new[] { u, u }));
            unit.Functions["main"] = main;
            foreach ((string name, int parameters) in new[] { ("BigA", 5), ("BigB", 5), ("SmallA", 2), ("SmallB", 2), ("RawA", 5), ("DeadA", 3), ("DeadB", 3), ("FixA", 3), ("MisA", 3) })
            {
                // (FixA one register twice among its parameters: Fixed, its
                // reads all said of the first.)
                ArgumentFunction f = new(parameters) { Fixed = name == "FixA" };
                for (int k = 0; k < parameters; k++) f.Reads[k] = (name.StartsWith("Dead") || name is "FixA" or "MisA") && k == 2 ? 0 : 1;
                unit.Functions[name] = f;
                unit.Slotted.Add(name);
            }
            // (Raw's slot also called with what nothing names: read raw, a
            // call through the slot like any, its arguments unknown.)
            main.Calls.Add(new ArgumentCall(Raw, 0, 1, new[] { u, u, u, u, u }));
            unit.CellCalls["s_Hook"] = new SortedSet<int> { 5 };
            if (cellWritten) unit.CellsWritten.Add("s_Hook");
            // s_Set is written with Install's parameter, which main passes a
            // named address (or, asked, something unknown), and called with
            // five arguments.
            ArgumentFunction install = new(1);
            install.Reads[0] = 1;
            unit.Functions["Install"] = install;
            if (installCalled) main.Calls.Add(new ArgumentCall("Install", 0, 1, new[] { installedUnknown ? u : ArgumentValue.At("Handler", 0) }));
            unit.CellParams["s_Set"] = new SortedSet<string>(StringComparer.Ordinal) { "0:Install" };
            unit.CellCalls["s_Set"] = new SortedSet<int> { 5 };
            return unit;
        }
        Dictionary<string, string[]> virtuals = new(StringComparer.Ordinal)
        {
            [Big] = new[] { "BigA", "BigB" },
            [Small] = new[] { "SmallA", "SmallB" },
            [Raw] = new[] { "RawA" },
            [Dead] = new[] { "DeadA", "DeadB" },
            [Fix] = new[] { "FixA" },
            [Mis] = new[] { "MisA" },
        };
        bool Loses(ArgumentFacts? facts, string name, int parameter)
            => facts is not null && facts.Dropped.TryGetValue(name, out ArgumentDrop[]? drops) && drops.Any(drop => drop.Parameter == parameter);

        // The cell holds only named addresses: Big's family loses its last
        // parameter, through its slot too; Small's is held by Gone's call
        // (three arguments, more than Small's members have); Raw's keeps
        // its numbers, a call through its slot passing others; Dead's too, by Gone's, whose
        // unread parameter stays in place and is passed as 0.
        List<string> said = new();
        ArgumentFacts? facts = ArgumentSolver.Solve(new[] { Unit(false) }, virtuals, _ => false, closed: true, said.Add);
        Check(Loses(facts, "BigA", 4) && Loses(facts, "BigB", 4), "a family no unnamed call reaches keeps a parameter every call passes alike");
        Check(facts!.DroppedSlots.TryGetValue(Big, out int[]? gone) && gone.SequenceEqual(new[] { 4 }), "the slot of a family that lost a parameter does not say so");
        Check(!Loses(facts, "SmallA", 1) && !Loses(facts, "SmallB", 1) && !facts.DroppedSlots.ContainsKey(Small),
            "a family an unanswered slot call of as many arguments reaches lost a parameter");
        Check(!Loses(facts, "RawA", 1) && !facts.DroppedSlots.ContainsKey(Raw), "a family's parameter some call through its slot passes otherwise was lost");
        Check(!Loses(facts, "DeadA", 2) && !facts.DroppedSlots.ContainsKey(Dead) && facts.UnsetSlots.TryGetValue(Dead, out int[]? unset) && unset.SequenceEqual(new[] { 2 }),
            "a held family's unread parameter is not passed as 0 in place");
        Check(!facts.UnsetSlots.ContainsKey(Small) && !facts.UnsetSlots.ContainsKey(Big), "a parameter some member reads, or of a family that lost it, is passed as 0");
        Check(!facts.UnsetSlots.ContainsKey(Fix) && !facts.DroppedSlots.ContainsKey(Fix), "a Fixed member's parameter, its reads not its own, is passed as 0");
        Check(!facts.UnsetSlots.ContainsKey(Mis) && !facts.DroppedSlots.ContainsKey(Mis), "a misfit member's parameter, its calls' places unknown, is passed as 0");
        Check(said.Any(line => line.Contains("1 slot calls without an answer") && line.Contains("2 calls through cells or parameters holding only named addresses")),
            "the report says neither the unanswered call nor the cell: " + string.Join(" | ", said));

        // The cell written with what nothing names, or open to code the
        // link does not see: its call of five arguments holds Big's too.
        facts = ArgumentSolver.Solve(new[] { Unit(true) }, virtuals, _ => false, closed: true);
        Check(!Loses(facts, "BigA", 4) && !Loses(facts, "BigB", 4), "a family a written cell's call of as many arguments reaches lost a parameter");
        facts = ArgumentSolver.Solve(new[] { Unit(false) }, virtuals, name => name == "s_Hook", closed: true);
        Check(!Loses(facts, "BigA", 4), "a family a foreign-written cell's call reaches lost a parameter");
        // The setter given what nothing names: its cell's call holds Big's.
        facts = ArgumentSolver.Solve(new[] { Unit(false, installedUnknown: true) }, virtuals, _ => false, closed: true);
        Check(!Loses(facts, "BigA", 4), "a family the call through a cell a setter was given anything reaches lost a parameter");
        // A setter nothing calls leaves its cell as it began.
        facts = ArgumentSolver.Solve(new[] { Unit(false, installCalled: false) }, virtuals, _ => false, closed: true);
        Check(Loses(facts, "BigA", 4), "a cell no call of its setter writes held a family");
        // A function whose body is another image's: no parameter dropped,
        // and no way in or version made beside it, though every call passes
        // it the one number it branches on.
        ArgumentHints program = new();
        ArgumentFunction caller = new(0) { Fixed = true };
        for (int c = 0; c < 4; c++) caller.Calls.Add(new ArgumentCall("Lib", 0, 1, new[] { ArgumentValue.Of(9), ArgumentValue.Of(c) }));
        program.Functions["caller"] = caller;
        ArgumentFunction lib = new(2) { Size = 20 };
        lib.Reads[0] = 1; lib.Reads[1] = 1; lib.Folds[0] = 10; lib.Shapes[0] = ArgumentShape.Compared;
        program.Functions["Lib"] = lib;
        ArgumentFacts? ours = ArgumentSolver.Solve(new[] { program }, new Dictionary<string, string[]>(StringComparer.Ordinal), _ => false, closed: false);
        ArgumentFacts? theirs = ArgumentSolver.Solve(new[] { program }, new Dictionary<string, string[]>(StringComparer.Ordinal), _ => false, closed: false,
            elsewhere: name => name == "Lib");
        Check(Loses(ours, "Lib", 0), "the image's own function does not lose the parameter every call passes alike");
        Check(theirs is null || !theirs.Dropped.ContainsKey("Lib") && !theirs.Entries.ContainsKey("Lib") && !theirs.Versions.ContainsKey("Lib") && !theirs.Splits.ContainsKey("Lib"),
            "another image's function changed, or was given a way in or a version");

        // An image not closed judges no family at all.
        facts = ArgumentSolver.Solve(new[] { Unit(false) }, virtuals, _ => false, closed: false);
        Check(!Loses(facts, "BigA", 4), "a family of an image not closed lost a parameter");

        // The format: what is said of unnamed calls comes back as written.
        ArgumentHints written = Unit(true);
        written.Unnamed.Add(3);
        byte[] bytes = written.Write();
        using MemoryStream stream = new(bytes, writable: false);
        using BinaryReader reader = new(stream);
        reader.ReadUInt32();
        reader.ReadInt32();
        ArgumentHints read = ArgumentHints.Read(reader, bytes.Length);
        Check(stream.Position == bytes.Length && read.Write().AsSpan().SequenceEqual(bytes), "argument hints do not read back as written");
        Check(read.CellCalls["s_Hook"].SequenceEqual(new[] { 5 }) && read.CellsWritten.Contains("s_Hook")
            && read.Unnamed.SequenceEqual(new[] { 3 }), "unnamed calls do not read back as written");
    }
}
