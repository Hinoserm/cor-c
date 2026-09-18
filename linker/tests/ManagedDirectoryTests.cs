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
            new TargetContract(0).Attach(obj);
            return obj;
        }
        ObjectFile a = Unit("_start"), b = Unit("other");
        byte[] beforeA = ElfWriter.WriteObject(a), beforeB = ElfWriter.WriteObject(b);
        var inputs = new[] { ("a", a), ("b", b) };
        var flat = Linker.LinkFlat(inputs, "_start", 0x10000);
        uint Word(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));
        int directory = checked((int)(Word(flat.Bytes, 0) - flat.Base));
        Check(Word(flat.Bytes, directory) == ManagedDirectory.Magic && Word(flat.Bytes, directory + 4) == 1, "Directory header");
        Check(Word(flat.Bytes, directory + 8) == 2 && Word(flat.Bytes, directory + 12) == 16, "Directory unit count/stride");
        for (int i = 0; i < 2; i++)
        {
            int record = directory + 16 + i * 16;
            uint address = Word(flat.Bytes, record);
            Check(Word(flat.Bytes, checked((int)(address - flat.Base))) == 0x4d524643, "Directory points outside unit frame table");
            Check(Word(flat.Bytes, record + 4) == 20 && Word(flat.Bytes, record + 8) == 0 && Word(flat.Bytes, record + 12) == 0, "Directory bounds/absent stack maps");
        }
        Check(flat.Bytes.SequenceEqual(Linker.LinkFlat(inputs, "_start", 0x10000).Bytes), "Repeated linking mutates inputs");
        Check(Linker.Link(inputs, "_start").Length > 0, "Static metadata link");
        Check(Linker.LinkShared(inputs, "libmetadata.so").Length > 0, "Shared metadata link");
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
        Console.WriteLine("  image metadata: every unit, bounds, flat/static/shared links, no input mutation and stale ABI rejection passed");
    }
}
