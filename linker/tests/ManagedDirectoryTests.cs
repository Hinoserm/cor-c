using System.Buffers.Binary;
using Corsac.Lang.Elf;
using Corsac.Lang.Ir;

namespace Corsac.Tests.Elf;

public static class ManagedDirectoryTests
{
    public static void Run()
    {
        void Check(bool value, string message) { if (!value) throw new Exception(message); }
        void Reject(Action action)
        { try { action(); } catch (LinkException) { return; } catch (ElfFormatException) { return; } throw new Exception("Invalid image metadata accepted"); }
        ObjectFile Unit(string name)
        {
            ObjectFile obj = new();
            Section code = new(".text", SectionKind.Code); code.Bytes.AddRange(new byte[5]); obj.Sections.Add(code);
            code.Relocs.Add(new(0, ManagedDirectory.Symbol, 0, RelocKind.Abs32));
            obj.Symbols.Add(new Symbol { Name = name, Section = code, Size = 5, IsFunction = true });
            Section frames = new(".rodata", SectionKind.ReadOnlyData);
            byte[] header = new byte[20]; BinaryPrimitives.WriteUInt32LittleEndian(header, 0x4d524643);
            frames.Bytes.AddRange(header); obj.Sections.Add(frames);
            obj.Symbols.Add(new Symbol { Name = ManagedDirectory.FrameSymbol, Section = frames, Size = 20, Global = false });
            // Every contract a compiled unit carries, which the link checks
            // and leaves behind (Linker.ConsumedContract).
            new TargetContract(0, requiresManagedLayouts: true, requiresCodeGenerationContract: true).Attach(obj);
            ManagedLayoutContract.Attach(obj, new[] { new ManagedTypeLayout("type:Example", new byte[32]) });
            Corsac.Lang.X86.X86Cpu.Parse(["--cpu=486"]).Contract.Attach(obj);
            return obj;
        }
        // The names of a linked i386 image's sections, from its section
        // headers, forty bytes each.
        List<string> SectionNames(byte[] image)
        {
            const int header = 40;
            int table = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(32)));
            int count = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(48)), strings = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(50));
            int names = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(table + strings * header + 16)));
            List<string> list = new();
            for (int i = 0; i < count; i++)
            {
                int at = names + (int)BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(table + i * header));
                list.Add(System.Text.Encoding.ASCII.GetString(image, at, Array.IndexOf(image, (byte)0, at) - at));
            }
            return list;
        }
        ObjectFile a = Unit("_start"), b = Unit("other");
        byte[] beforeA = ElfWriter.WriteObject(a), beforeB = ElfWriter.WriteObject(b);
        var inputs = new[] { ("a", a), ("b", b) };
        var flat = Linker.LinkFlat(inputs, "_start", 0x10000);
        uint Word(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));
        int directory = checked((int)(Word(flat.Bytes, 0) - flat.Base));
        Check(Word(flat.Bytes, directory) == ManagedDirectory.Magic && Word(flat.Bytes, directory + 4) == ManagedDirectory.Version, "Directory header");
        Check(Word(flat.Bytes, directory + 8) == 2 && Word(flat.Bytes, directory + 12) == 16, "Directory unit count/stride");
        for (int i = 0; i < 2; i++)
        {
            int record = directory + 16 + i * 16;
            uint address = Word(flat.Bytes, record);
            Check(Word(flat.Bytes, checked((int)(address - flat.Base))) == 0x4d524643, "Directory points outside unit frame table");
            Check(Word(flat.Bytes, record + 4) == 20 && Word(flat.Bytes, record + 8) == 0 && Word(flat.Bytes, record + 12) == 0, "Directory bounds/absent stack maps");
        }
        Check(flat.Bytes.SequenceEqual(Linker.LinkFlat(inputs, "_start", 0x10000).Bytes), "Repeated linking mutates inputs");
        byte[] image = Linker.Link(inputs, "_start");
        Check(image.Length > 0, "Static metadata link");
        byte[] shared = Linker.LinkShared(inputs, "libmetadata.so");
        Check(shared.Length > 0, "Shared metadata link");
        // NO CONTRACT THE LINK CHECKED REACHES THE IMAGE, static or shared,
        // and the objects keep theirs: the shared link validates the objects
        // the static one has just linked, and the comparison below with
        // beforeA and beforeB finds them as they were. The three are named
        // here, not asked of the predicate, so a name it lost is caught.
        string[] contracts = { TargetContract.SectionName, X86CodeGenerationContract.SectionName, ManagedLayoutContract.SectionName };
        Check(SectionNames(image).Contains(".text") && SectionNames(shared).Contains(".text"), "Image section names");
        Check(!SectionNames(image).Intersect(contracts).Any(), "Static link carried its inputs' contracts into the image");
        Check(!SectionNames(shared).Intersect(contracts).Any(), "Shared link carried its inputs' contracts into the image");
        Check(!ElfReader.ExportsOf(shared).Any(name => name == ManagedDirectory.Symbol || name.StartsWith("__corsac_unit_", StringComparison.Ordinal)),
            "Image-local metadata leaked into dynamically interposable exports");
        Check(beforeA.SequenceEqual(ElfWriter.WriteObject(a)) && beforeB.SequenceEqual(ElfWriter.WriteObject(b)), "Metadata synthesis changed original objects");
        ObjectFile bad = Unit("_start");
        bad.Symbols.Add(new Symbol { Name = ManagedDirectory.Symbol, Section = bad.Sections[0] });
        Reject(() => Linker.Link(new[] { ("bad", bad) }, "_start"));
        bad = Unit("_start"); bad.Symbols.Add(new Symbol { Name = "__corsac_unit_0_frames" });
        Reject(() => Linker.Link(new[] { ("bad", bad) }, "_start"));
        bad = Unit("_start"); bad.Sections.First(s => s.Name == ".rodata").Bytes.RemoveAt(0);
        Reject(() => Linker.Link(new[] { ("bad", bad) }, "_start"));
        bad = Unit("_start"); bad.Sections.Single(s => s.Name == TargetContract.SectionName).Bytes[4] = 2;
        Reject(() => Linker.Link(new[] { ("old", bad) }, "_start"));
        Console.WriteLine("  image metadata: every unit, bounds, flat/static/shared links, no input mutation, no consumed contract in an image and stale ABI rejection passed");
    }
}
