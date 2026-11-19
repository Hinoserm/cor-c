using System.Buffers.Binary;
using Corsac.Lang.Elf;
using Corsac.Lang.Ir;
using Corsac.Lang.Lto;

namespace Corsac.Tests.Elf;

/// <summary>
/// The stack-map table ('CSM1', version 0, StackMapTable) as the link's
/// duplicate cutter rewrites it: a cut function's call sites go with it,
/// the rest start where their code now starts, each site kept as it was
/// within its function. A table of another magic or version, one too short
/// for its header, or one whose offsets run past its end leaves the code
/// uncut.
/// </summary>
public static class DuplicateCutterTests
{
    public static void Run()
    {
        void Check(bool value, string message) { if (!value) throw new Exception(message); }
        (ObjectFile Obj, Section Text, Section Maps, Symbol Middle) Make(uint magic, uint version)
        {
            ObjectFile obj = new();
            Section text = new(".text", SectionKind.Code) { Align = 16 };
            text.Bytes.AddRange(new byte[48]);
            obj.Sections.Add(text);
            Symbol[] symbols = new Symbol[3];
            List<StackMapTable.Function> functions = new();
            for (int i = 0; i < 3; i++)
            {
                symbols[i] = new Symbol { Name = "f" + i, IsFunction = true, Section = text, Offset = 16 * i, Size = 16 };
                obj.Symbols.Add(symbols[i]);
                StackMapTable.Function f = new() { Start = 16 * i, FrameSize = 8 };
                f.Sites.Add(new StackMapTable.Site(5, false, 0, [i]));
                functions.Add(f);
            }
            byte[] table = StackMapTable.Encode(functions);
            BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(0), magic);
            BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(4), version);
            Section maps = new(".corsac.stackmaps", SectionKind.ReadOnlyData) { Align = 8 };
            maps.Bytes.AddRange(table);
            maps.Relocs.Add(new Relocation(StackMapTable.BaseOffset, "f0", 0, RelocKind.Abs32));
            obj.Sections.Add(maps);
            return (obj, text, maps, symbols[1]);
        }

        var (obj, text, maps, middle) = Make(StackMapTable.Magic, StackMapTable.Version);
        List<Symbol> left = DuplicateCutter.Cut(obj, new List<Symbol> { middle });
        Check(left.Count == 0 && text.Bytes.Count == 32, "a 'CSM1' table's code left uncut");
        List<StackMapTable.Function>? kept = StackMapTable.Decode(maps.Bytes.Slice(0, maps.Bytes.Count));
        Check(kept is { Count: 2 }, "the cut table, read back");
        Check(kept![0].Start == 0 && kept[1].Start == 16, "the kept functions, where their code now starts");
        Check(kept[0].Sites is [{ Return: 5, Slots: [0] }] && kept[1].Sites is [{ Return: 5, Slots: [2] }],
            "the kept sites, as they were within their functions");

        foreach ((uint magic, uint version) in new[] { (StackMapTable.Magic, 3u), (0x4d534321u, StackMapTable.Version) })
        {
            (obj, text, maps, middle) = Make(magic, version);
            left = DuplicateCutter.Cut(obj, new List<Symbol> { middle });
            Check(left.Count == 1 && text.Bytes.Count == 48, "a stack-map table of another magic or version cut");
        }
        // A section too short for a header, and a pool said to start past the
        // table's end: unknown, the code left, nothing read past it.
        (obj, text, maps, middle) = Make(StackMapTable.Magic, StackMapTable.Version);
        maps.Bytes.Clear(); maps.Bytes.AddRange(new byte[] { 0x43, 0x53, 0x4d });
        left = DuplicateCutter.Cut(obj, new List<Symbol> { middle });
        Check(left.Count == 1 && text.Bytes.Count == 48, "a stack-map section shorter than its header cut");
        (obj, text, maps, middle) = Make(StackMapTable.Magic, StackMapTable.Version);
        maps.Bytes.WriteInt32(32, 0x10000000);
        left = DuplicateCutter.Cut(obj, new List<Symbol> { middle });
        Check(left.Count == 1 && text.Bytes.Count == 48, "a stack-map table pointing past its end cut");
        Console.WriteLine("  duplicate cutter: 'CSM1' functions and sites follow the cut; any other table leaves the code");
    }
}
