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

        // A RUN OF COPIES THAT IS NOT A WHOLE NUMBER OF THE ALIGNMENT: a small
        // function at 16, then a function's stub at 20 running on into its
        // body at 32, which ends where the next function's stub begins, at
        // 76 (its body at 80). The three are one run of 60 bytes: 48 are
        // taken from its end, the 12 at its start left as no-ops -- and what
        // began in those 12 is gone with the rest, its symbols, its stack
        // map and its relocation.
        {
            ObjectFile run = new();
            Section code = new(".text", SectionKind.Code) { Align = 16 };
            code.Bytes.AddRange(new byte[96]);
            for (int i = 0; i < 96; i++) code.Bytes[i] = (byte)i;
            run.Sections.Add(code);
            Symbol Function(string name, int offset, int size)
            {
                Symbol symbol = new() { Name = name, IsFunction = true, Section = code, Offset = offset, Size = size };
                run.Symbols.Add(symbol);
                return symbol;
            }
            Function("f0", 0, 12);
            Symbol small = Function("g", 16, 4), stub = Function("f1", 20, 12), body = Function("f1$r", 32, 40);
            Function("f2", 76, 4);
            Function("f2$r", 80, 16);
            code.Relocs.Add(new Relocation(4, "x", 0, RelocKind.Abs32));
            code.Relocs.Add(new Relocation(26, "x", 0, RelocKind.Abs32));     // in what is left of the run, reaching into the cut
            code.Relocs.Add(new Relocation(40, "x", 0, RelocKind.Abs32));
            code.Relocs.Add(new Relocation(84, "x", 0, RelocKind.Abs32));
            List<StackMapTable.Function> functions = new();
            foreach (int at in new[] { 0, 16, 32, 80 })
            {
                StackMapTable.Function f = new() { Start = at, FrameSize = 8 };
                f.Sites.Add(new StackMapTable.Site(5, false, 0, [at]));
                functions.Add(f);
            }
            Section table = new(".corsac.stackmaps", SectionKind.ReadOnlyData) { Align = 8 };
            table.Bytes.AddRange(StackMapTable.Encode(functions));
            table.Relocs.Add(new Relocation(StackMapTable.BaseOffset, "f0", 0, RelocKind.Abs32));
            run.Sections.Add(table);

            List<Symbol> rest = DuplicateCutter.Cut(run, new List<Symbol> { body, small, stub });
            Check(rest.Count == 0 && code.Bytes.Count == 48, "a run of copies short of a whole alignment: " + rest.Count + " left, " + code.Bytes.Count + " bytes");
            for (int i = 0; i < 16; i++) Check(code.Bytes[i] == i, "the code before the run, as it was");
            for (int i = 16; i < 28; i++) Check(code.Bytes[i] == 0x90, "what is left of the run, no-ops");
            for (int i = 28; i < 48; i++) Check(code.Bytes[i] == i + 48, "the code after the run, moved down by the cut");
            Symbol Defined(string name) => run.Symbols.Single(s => s.Name == name && s.IsDefined);
            Check(Defined("f2").Offset == 28 && Defined("f2$r").Offset == 32, "the function after the run: its stub still ends where its body begins, the body aligned");
            Check(!run.Symbols.Any(s => s.IsDefined && s.Name is "g" or "f1" or "f1$r"), "the run's symbols, gone");
            Check(code.Relocs.Select(r => r.Offset).SequenceEqual(new[] { 4, 36 }), "the relocations: the run's gone, the one after it moved");
            List<StackMapTable.Function>? read = StackMapTable.Decode(table.Bytes.Slice(0, table.Bytes.Count));
            Check(read is { Count: 2 } && read[0].Start == 0 && read[1].Start == 32, "the stack maps: the run's gone, the function after it where it now starts");

            // A run shorter than the alignment is left whole.
            run = new();
            code = new(".text", SectionKind.Code) { Align = 16 };
            code.Bytes.AddRange(new byte[48]);
            run.Sections.Add(code);
            Function("f0", 0, 12);
            stub = Function("f1", 28, 4);
            Function("f1$r", 32, 16);
            rest = DuplicateCutter.Cut(run, new List<Symbol> { stub });
            Check(rest.Count == 1 && code.Bytes.Count == 48, "a run shorter than the alignment cut");
            Console.WriteLine("  duplicate cutter: a run of adjoining copies is cut by whole alignments from its end, the rest of it no-ops");
        }
    }
}
