using Corsac.Lang.Elf;
using Corsac.Lang.Ir;
using Corsac.Lang.Lto;

namespace Corsac.Tests.Elf;

public static class IrArchiveTests
{
    public static void Run()
    {
        ByteListReadStreamTests.Run();
        void Check(bool value, string message) { if (!value) throw new Exception(message); }
        void Reject(Action action)
        { try { action(); } catch (ElfFormatException) { return; } throw new Exception("Invalid IR archive accepted"); }
        ObjectFile Make(string name, bool importable, string[] calls)
        {
            ObjectFile obj = new(); Section text = new(".text", SectionKind.Code);
            text.Bytes.Add(0xc3); obj.Sections.Add(text);
            obj.Symbols.Add(new Symbol { Name = name, IsFunction = true, Section = text, Size = 1 });
            IrArchive.Attach(obj, new[] { new IrArchiveRecord("F:" + name, importable, 1, calls, new byte[] { 1, 2, 3 }) });
            return obj;
        }
        ObjectFile source = Make("function", true, new[] { "other" });
        ObjectFile roundTrip = ElfReader.ReadObject(ElfWriter.WriteObject(source));
        IrArchive archive = IrArchive.Read(roundTrip)!;
        Check(archive.Entries.Count == 1 && archive.Entries["F:function"].Calls.Single() == "other", "IR summary round trip");
        Check(archive.ReadBody("F:function").SequenceEqual(new byte[] { 1, 2, 3 }), "IR body round trip");
        roundTrip.Section(IrArchive.SectionName).Bytes[^1] ^= 1;
        Reject(() => archive.ReadBody("F:function"));
        // Opening summaries must not clone the full, potentially huge payload.
        ObjectFile largeArchive = new();
        IrArchive.Attach(largeArchive, new[] { new IrArchiveRecord("F:large", false, 0,
            Array.Empty<string>(), new byte[4 * 1024 * 1024]) });
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        IrArchive largeView = IrArchive.Read(largeArchive)!;
        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        Check(allocated < 1024 * 1024 && largeView.Entries.Count == 1,
            "Opening IR summaries copied payload bytes: " + allocated);
        Console.WriteLine("  IR summary view: 4194304 payload bytes, " + allocated + " bytes allocated while opening");
        source.Section(".text").Bytes[0] = 0x90;
        Reject(() => IrArchive.Read(source));
        source = Make("function", true, Array.Empty<string>()); source.Section(IrArchive.SectionName).Bytes[4] = 99;
        Reject(() => IrArchive.Read(source));
        source = Make("function", true, Array.Empty<string>()); source.Section(IrArchive.SectionName).Bytes[90] ^= 1;
        Reject(() => IrArchive.Read(source));
        source = Make("function", true, Array.Empty<string>()); source.Section(IrArchive.SectionName).Bytes[^1] ^= 1;
        archive = IrArchive.Read(source)!; // Untouched bodies remain lazy.
        Reject(() => archive.ReadBody("F:function"));
        source = Make("function", true, Array.Empty<string>()); source.Section(IrArchive.SectionName).Bytes.RemoveAt(8);
        Reject(() => IrArchive.Read(source));

        List<(string Name, ObjectFile Object)> inputs = new() { ("caller", Make("caller", false, new[] { "callee", "large" })),
            ("callee", Make("callee", true, Array.Empty<string>())), ("large", Make("large", false, Array.Empty<string>())) };
        RecordingBackend backend = new();
        Check(IrLinkOptimizer.Run(inputs, () => backend, importBytes: 3) == 1 && backend.Imports.SequenceEqual(new[] { "callee" }), "bounded import plan");
        Check(inputs.All(input => input.Object.Sections.All(section => section.Name != IrArchive.SectionName)), "final link retained IR");
        inputs = new() { ("caller", Make("caller", false, new[] { "callee" })), ("callee", Make("callee", true, Array.Empty<string>())) };
        Check(IrLinkOptimizer.Run(inputs, () => throw new Exception("backend started with insufficient budget"), importBytes: 2) == 0, "over-budget import");
        inputs = new() { ("caller", Make("caller", false, new[] { "callee" })), ("callee", Make("callee", true, Array.Empty<string>())) };
        Check(IrLinkOptimizer.Run(inputs, () => throw new Exception("disabled backend started"), enabled: false) == 0, "disabled imports");

        using MemoryStream wire = new();
        using BinaryWriter writer = new(wire, BackendProtocol.Utf8, leaveOpen: true);
        BackendProtocol.WriteRequest(writer, new("in.o", "out.o", new[] { new IrImport("callee", new byte[] { 3, 2, 1 }) }));
        wire.Position = 0;
        using BinaryReader reader = new(wire, BackendProtocol.Utf8, leaveOpen: true);
        BackendRequest request = BackendProtocol.ReadRequest(reader)!;
        Check(request.Input == "in.o" && request.Output == "out.o" && request.Imports.Single().Body[0] == 3, "backend request round trip");
        Check(BackendProtocol.ReadRequest(reader) is null, "clean protocol EOF");
        Console.WriteLine("  indexed IR integrity, lazy bodies, budgets, import selection and backend protocol passed");
    }
}
