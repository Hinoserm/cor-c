using System.Buffers.Binary;
using Corsac.Lang.Elf;
using Corsac.Lang.Ir;
using Corsac.Lang.Lto;

namespace Corsac.Tests.Elf;

/// <summary>
/// The x86-64 generator's stack maps ('CSMX', version 0, fixed sixteen-byte
/// entries) as the link's duplicate cutter rewrites them: a cut function's
/// call sites go with it, the rest are measured from the code as cut, and
/// each kept entry's pointer into the bitmap pool follows the entries that
/// moved. A table of another magic or version leaves the code uncut.
/// </summary>
public static class DuplicateCutterTests
{
    public static void Run()
    {
        void Check(bool value, string message) { if (!value) throw new Exception(message); }
        const int Entries = 20, Pool = Entries + 3 * 16;
        (ObjectFile Obj, Section Text, Section Maps, Symbol Middle) Make(uint magic, uint version)
        {
            ObjectFile obj = new();
            Section text = new(".text", SectionKind.Code) { Align = 16 };
            text.Bytes.AddRange(new byte[48]);
            obj.Sections.Add(text);
            Symbol[] functions = new Symbol[3];
            for (int i = 0; i < 3; i++)
            {
                functions[i] = new Symbol { Name = "f" + i, IsFunction = true, Section = text, Offset = 16 * i, Size = 16 };
                obj.Symbols.Add(functions[i]);
            }
            byte[] table = new byte[Pool + 3 * 4];
            void Word(int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(at), value);
            Word(0, magic); Word(4, version); Word(8, 3); Word(12, 16);
            for (int i = 0; i < 3; i++)
            {
                Word(Entries + 16 * i, (uint)(16 * i + 5));          // a return address, from the base
                Word(Entries + 16 * i + 8, (uint)(Pool + 4 * i));    // its bitmap, in the pool
                Word(Pool + 4 * i, 0x100u + (uint)i);
            }
            Section maps = new(".corsac.stackmaps", SectionKind.ReadOnlyData) { Align = 8 };
            maps.Bytes.AddRange(table);
            maps.Relocs.Add(new Relocation(16, "f0", 0, RelocKind.Abs32));
            obj.Sections.Add(maps);
            return (obj, text, maps, functions[1]);
        }

        var (obj, text, maps, middle) = Make(0x584d5343, 0);
        List<Symbol> left = DuplicateCutter.Cut(obj, new List<Symbol> { middle });
        byte[] cut = maps.Bytes.Slice(0, maps.Bytes.Count);
        uint At(int offset) => BinaryPrimitives.ReadUInt32LittleEndian(cut.AsSpan(offset));
        Check(left.Count == 0 && text.Bytes.Count == 32, "a 'CSMX' table's code left uncut");
        Check(At(0) == 0x584d5343 && At(4) == 0 && At(8) == 2 && At(12) == 16, "the cut 'CSMX' header");
        Check(At(Entries) == 5 && At(Entries + 16) == 21, "the kept sites, measured from the cut code");
        Check(At(Entries + 8) == Pool - 16 && At(Entries + 24) == Pool - 16 + 8, "the kept sites' bitmaps, past the entry that went");
        Check(At((int)At(Entries + 8)) == 0x100 && At((int)At(Entries + 24)) == 0x102, "the bitmaps the kept sites point at");

        foreach ((uint magic, uint version) in new[] { (0x584d5343u, 3u), (0x4d534321u, 0u) })
        {
            (obj, text, maps, middle) = Make(magic, version);
            left = DuplicateCutter.Cut(obj, new List<Symbol> { middle });
            Check(left.Count == 1 && text.Bytes.Count == 48, "a stack-map table of another magic or version cut");
        }
        Console.WriteLine("  duplicate cutter: 'CSMX' sites and bitmaps follow the cut; any other table leaves the code");
    }
}
