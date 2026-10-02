using Corsac.Lang.Elf;
using Corsac.Lang.Lto;

namespace Corsac.Tests.Elf;

/// <summary>Region hints: the section format, the whole-program solve and its soundness rules, and the backend's facts on the wire.</summary>
public static class RegionTests
{
    public static void Run()
    {
        void Check(bool value, string message) { if (!value) throw new Exception(message); }
        RegionSite Made() => new(true, 1, null, 0);
        RegionFunction Function(string name, int parameters, int nodes, bool global = true, bool boundary = true, params RegionSite[] sites)
            => new(name, global, boundary, false, parameters, nodes, 0, sites);

        // Unit A: the entry calls Main, which calls Work, Keep and Pass (what
        // the entry calls itself is never a boundary). Work keeps what B's
        // Make makes to itself; Keep stores what B's MakeKept makes where
        // nobody follows; Pass hands what B's MakeHanded makes to a call
        // nobody can name. Hook, whose address A takes, stores what it makes
        // into what it is handed.
        RegionHints a = new();
        RegionFunction start = Function("_start", 0, 1, boundary: false);
        start.Calls.Add(new("Main", -1, Array.Empty<int>()));
        a.Functions.Add(start);
        RegionFunction main = Function("Main", 0, 1);
        main.Calls.Add(new("Work", -1, Array.Empty<int>()));
        main.Calls.Add(new("Keep", -1, Array.Empty<int>()));
        main.Calls.Add(new("Pass", -1, Array.Empty<int>()));
        a.Functions.Add(main);
        RegionFunction work = Function("Work", 0, 2);
        work.Calls.Add(new("Make", 1, Array.Empty<int>()));
        a.Functions.Add(work);
        RegionFunction keep = Function("Keep", 0, 3);
        keep.Calls.Add(new("MakeKept", 1, Array.Empty<int>()));
        keep.Constraints.Add(new(RegionConstraintKind.Unknown, 2, 0, 0));
        keep.Constraints.Add(new(RegionConstraintKind.Store, 2, 1, 8));
        a.Functions.Add(keep);
        RegionFunction pass = Function("Pass", 0, 2);
        pass.Calls.Add(new("MakeHanded", 1, Array.Empty<int>()));
        pass.Calls.Add(new(null, -1, new[] { 1 }));
        a.Functions.Add(pass);
        RegionFunction hook = Function("Hook", 1, 3, global: false, sites: Made());
        hook.Constraints.Add(new(RegionConstraintKind.Site, 2, 0, 0));
        hook.Constraints.Add(new(RegionConstraintKind.Store, 0, 2, 4));
        a.Functions.Add(hook);
        a.AddressTaken.Add("Hook");
        // Stash stores what MakeHeld makes into a leaf, and what MakeHeld2
        // makes at a word its descriptor's map says no reference is kept in,
        // and leaves both where nobody follows: the collector never reads
        // either word, so neither object is reached by it.
        RegionFunction stash = Function("Stash", 0, 7);
        stash.Calls.Add(new("MakeLeaf", 1, Array.Empty<int>()));
        stash.Calls.Add(new("MakeHeld", 2, Array.Empty<int>()));
        stash.Calls.Add(new("MakeDescribed", 4, Array.Empty<int>()));
        stash.Calls.Add(new("MakeHeld2", 5, Array.Empty<int>()));
        stash.Constraints.Add(new(RegionConstraintKind.Store, 1, 2, 8));
        stash.Constraints.Add(new(RegionConstraintKind.Store, 4, 5, 8));
        stash.Constraints.Add(new(RegionConstraintKind.Unknown, 3, 0, 0));
        stash.Constraints.Add(new(RegionConstraintKind.Store, 3, 1, 0));
        stash.Constraints.Add(new(RegionConstraintKind.Store, 3, 4, 0));
        a.Functions.Add(stash);
        main.Calls.Add(new("Stash", -1, Array.Empty<int>()));

        // Unit B: the makers, each handing back what it makes.
        RegionHints b = new();
        foreach (string name in new[] { "Make", "MakeKept", "MakeHanded", "MakeLeaf", "MakeHeld", "MakeDescribed", "MakeHeld2" })
        {
            RegionSite site = name switch
            {
                "MakeLeaf" => new(true, 1, null, 0, RegionWords.Leaf),
                "MakeDescribed" => new(true, 1, "t_T", 48, RegionWords.Described),
                _ => Made(),
            };
            RegionFunction maker = Function(name, 0, 2, sites: site);
            maker.Constraints.Add(new(RegionConstraintKind.Site, 1, 0, 0));
            maker.Constraints.Add(new(RegionConstraintKind.Copy, 0, 1, 0));
            b.Functions.Add(maker);
        }

        // The format: the same hints are the same bytes, read back alike;
        // a cut-short section is refused.
        byte[] bytes = a.Write();
        Check(bytes.AsSpan().SequenceEqual(a.Write()), "region hints are not deterministic");
        RegionHints again = RegionHints.Read(bytes);
        Check(again.Write().AsSpan().SequenceEqual(bytes), "region hints do not read back alike");
        bool refused = false;
        try { RegionHints.Read(bytes[..^3]); } catch (ElfFormatException) { refused = true; }
        Check(refused, "truncated region hints accepted");

        RegionHints unitB = RegionHints.Read(b.Write());
        Check(unitB.Functions.Single(f => f.Name == "MakeDescribed").Sites[0] == new RegionSite(true, 1, "t_T", 48, RegionWords.Described),
            "a site's words do not read back alike");
        RegionFacts?[]? facts = RegionSolver.Solve(new[] { again, unitB }, new(StringComparer.Ordinal),
            (_, _) => null, "_start", new HashSet<string>(StringComparer.Ordinal), null,
            noReference: (table, at, offset) => table == "t_T" && at == 48 && offset == 8);
        Check(facts is not null, "the region solve gave up");
        Check(facts![0] is { } inA && inA.Boundaries.SetEquals(new[] { "Work", "Stash" }) && inA.Sites.Count == 0,
            "unit A: Work and Stash alone are boundaries, and Hook's object -- stored into what anything may hand it -- is never in a region");
        Check(facts[1] is { } inB && inB.Boundaries.Count == 0 && inB.Sites.SetEquals(new[] { ("Make", 0), ("MakeHeld", 0), ("MakeHeld2", 0) }),
            "unit B: Make's object and those kept only in words no reference is kept in are in a region; what is stored where nobody follows, or handed to a call nobody can name, is not");

        // The facts on the backend's wire.
        LifetimeFacts lifetime = new() { Regions = facts[1] };
        using MemoryStream stream = new();
        using (BinaryWriter writer = new(stream, BackendProtocol.Utf8, leaveOpen: true))
            BackendProtocol.WriteRequest(writer, new("in.o", "out.o", Array.Empty<IrImport>(), null, lifetime));
        stream.Position = 0;
        using BinaryReader reader = new(stream, BackendProtocol.Utf8);
        BackendRequest request = BackendProtocol.ReadRequest(reader)!;
        Check(request.Facts?.Regions is { } read && read.Sites.SetEquals(facts[1]!.Sites) && read.Boundaries.Count == 0,
            "region facts do not cross the backend protocol");
    }
}
