using Corsac.Lang.Ir;
using Corsac.Lang.Lto;

namespace Corsac.Tests.Elf;

public static class IrReachabilityTests
{
    public static void Run()
    {
        ObjectFile managed = new(); Section code = new(".text", SectionKind.Code); code.Bytes.AddRange(new byte[] { 0xc3, 0xc3, 0xc3 }); managed.Sections.Add(code);
        Section data = new(".rodata", SectionKind.ReadOnlyData); data.Bytes.AddRange(new byte[4]); managed.Sections.Add(data);
        managed.Symbols.Add(new Symbol { Name = "entry", IsFunction = true, Section = code, Size = 1 });
        managed.Symbols.Add(new Symbol { Name = "callback", IsFunction = true, Section = code, Offset = 1, Size = 1 });
        managed.Symbols.Add(new Symbol { Name = "unused", IsFunction = true, Section = code, Offset = 2, Size = 1 });
        managed.Symbols.Add(new Symbol { Name = "table", Section = data, Size = 4, Global = false });
        IrArchive.Attach(managed, new[] {
            new IrArchiveRecord("F:entry", false, 1, Array.Empty<string>(), new byte[] { 1 }, new[] { "table" }),
            new IrArchiveRecord("D:table", false, 0, Array.Empty<string>(), new byte[] { 2 }, new[] { "callback" }),
            new IrArchiveRecord("F:callback", true, 1, Array.Empty<string>(), new byte[] { 3 }),
            new IrArchiveRecord("F:unused", true, 1, Array.Empty<string>(), new byte[] { 4 }) });
        Dictionary<ObjectFile, IrArchive> archives = new() { [managed] = IrArchive.Read(managed)! };
        Dictionary<string, ObjectFile> owners = new() { ["entry"] = managed, ["callback"] = managed, ["unused"] = managed };
        var keep = IrReachability.Find(new[] { ("managed", managed) }, archives, owners, "entry")[managed];
        if (!keep.SetEquals(new[] { "F:entry", "D:table", "F:callback" })) throw new Exception("Address-taken callback/local data reachability failed");
        ObjectFile native = new(); Section vectors = new(".vectors", SectionKind.ReadOnlyData); vectors.Bytes.AddRange(new byte[4]);
        vectors.Relocs.Add(new Relocation(0, "unused", 0, RelocKind.Abs32)); native.Sections.Add(vectors);
        keep = IrReachability.Find(new[] { ("managed", managed), ("native", native) }, archives, owners, "entry")[managed];
        if (!keep.Contains("F:unused")) throw new Exception("Native vector relocation was not retained as a root");
        // A routine only the link will refer to (a field site's target) is kept when named as a root.
        keep = IrReachability.Find(new[] { ("managed", managed) }, archives, owners, "entry", new[] { "unused" })[managed];
        if (!keep.Contains("F:unused")) throw new Exception("An extra root was not retained");
        // A function the unit's IR holds and its object does not -- one the
        // compile's late inliner took into every caller and dropped -- is
        // kept for the callers the archived IR still has.
        ObjectFile unit = new(); Section unitCode = new(".text", SectionKind.Code); unitCode.Bytes.Add(0xc3); unit.Sections.Add(unitCode);
        unit.Symbols.Add(new Symbol { Name = "main", IsFunction = true, Section = unitCode, Size = 1 });
        IrArchive.Attach(unit, new[] {
            new IrArchiveRecord("F:main", false, 1, new[] { "main$constant$0" }, new byte[] { 1 }, new[] { "main$constant$0" }),
            new IrArchiveRecord("F:main$constant$0", false, 1, Array.Empty<string>(), new byte[] { 2 }) });
        keep = IrReachability.Find(new[] { ("unit", unit) }, new Dictionary<ObjectFile, IrArchive> { [unit] = IrArchive.Read(unit)! },
            new Dictionary<string, ObjectFile> { ["main"] = unit }, "main")[unit];
        if (!keep.SetEquals(new[] { "F:main", "F:main$constant$0" })) throw new Exception("A body only the unit's IR defines was not retained");
        // A generic copy two units compile, kept in the first: there it
        // inlined its helper at the compile and calls nothing, while the
        // second unit's copy still calls the helper. The link's late passes
        // may inline the second unit's own copy into its caller, so the
        // helper is reached and kept where it is owned -- the kernel's
        // Dictionary<__canon,__canon>.Same, kept nowhere, failed the link --
        // and the second unit's copy itself is still not kept.
        ObjectFile first = new(), second = new();
        foreach (ObjectFile obj in new[] { first, second })
        {
            Section text = new(".text", SectionKind.Code); text.Bytes.AddRange(new byte[] { 0xc3, 0xc3, 0xc3 }); obj.Sections.Add(text);
            obj.Symbols.Add(new Symbol { Name = "generic", IsFunction = true, Section = text, Size = 1, Global = true });
            obj.Symbols.Add(new Symbol { Name = "helper", IsFunction = true, Section = text, Offset = 1, Size = 1, Global = true });
        }
        second.Symbols.Add(new Symbol { Name = "start", IsFunction = true, Section = second.Sections[0], Offset = 2, Size = 1, Global = true });
        IrArchive.Attach(first, new[] {
            new IrArchiveRecord("F:generic", true, 1, Array.Empty<string>(), new byte[] { 1 }),
            new IrArchiveRecord("F:helper", true, 1, Array.Empty<string>(), new byte[] { 2 }) });
        IrArchive.Attach(second, new[] {
            new IrArchiveRecord("F:generic", true, 1, new[] { "helper" }, new byte[] { 3 }, new[] { "helper" }),
            new IrArchiveRecord("F:helper", true, 1, Array.Empty<string>(), new byte[] { 4 }),
            new IrArchiveRecord("F:start", false, 1, new[] { "generic" }, new byte[] { 5 }, new[] { "generic" }) });
        Dictionary<ObjectFile, IrArchive> pair = new() { [first] = IrArchive.Read(first)!, [second] = IrArchive.Read(second)! };
        Dictionary<string, ObjectFile> firstOwns = new() { ["generic"] = first, ["helper"] = first, ["start"] = second };
        var kept = IrReachability.Find(new[] { ("first", first), ("second", second) }, pair, firstOwns, "start");
        if (!kept[first].SetEquals(new[] { "F:generic", "F:helper" }))
            throw new Exception("What a unit's own copy of a coalesced function calls was not kept where it is owned: "
                + string.Join(",", kept[first].Order(StringComparer.Ordinal)));
        if (!kept[second].SetEquals(new[] { "F:start" }))
            throw new Exception("A unit's own copy of a function kept elsewhere was kept: " + string.Join(",", kept[second].Order(StringComparer.Ordinal)));
        Console.WriteLine("  closed-image reachability: code, private data, callbacks, native vectors, link roots, IR-only bodies and own copies' callees passed");
    }
}
