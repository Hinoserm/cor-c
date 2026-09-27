#nullable enable
using System.Text;
using Corsac.Lang.Ir;

namespace Corsac.Lang.X64;

/// <summary>
/// The x86-64 code generator: IR module in, relocatable object out, for a
/// long-mode Linux program on anything from the first AMD64 processor (K8)
/// on.
///
/// Per function: instruction selection (Select.cs), register allocation
/// (RegAlloc.cs), encoding (Encoder.cs). Data items go in .rodata, .data or
/// .bss by their flags, holding 64-bit absolute relocations where they hold
/// addresses; the frame table and the stack maps are the x86 formats, with
/// frame slots counted in eight-byte words.
///
/// Static code in the small model: every symbol is within two gigabytes of
/// every instruction, and every one is reached RIP-relative. That is also
/// most of what a position-independent image asks, so the step to one is a
/// GOT for what another image defines, not a new way of addressing.
/// </summary>
public sealed class X64Backend : IBackend
{
    public Target Target => Target.X86_64;

    /// <summary>Alignment of each function's first byte: sixteen, a K8 fetch block.</summary>
    public int FunctionAlign { get; set; } = 16;

    public bool StackMaps { get; set; } = true;

    public const string StackMapSection = ".corsac.stackmaps";
    public const string StackMapStart = "__corsac_stackmaps";
    public const string StackMapEnd = "__corsac_stackmaps_end";

    public List<(string Name, int Bytes)> FunctionSizes { get; } = new();

    public string Statistics()
    {
        StringBuilder sb = new();
        int total = 0;
        foreach ((string name, int bytes) in FunctionSizes.OrderByDescending(f => f.Bytes))
        {
            sb.Append($"{bytes,8}  {name}\n");
            total += bytes;
        }
        sb.Append($"{total,8}  total in {FunctionSizes.Count} functions\n");
        return sb.ToString();
    }

    public ObjectFile Generate(Module module, List<string> errors)
    {
        ObjectFile obj = new();
        new X86CodeGenerationContract("k8", "k8", "sse2", false, false, false, false).Attach(obj);
        Section text = new(".text", SectionKind.Code) { Align = Math.Max(16, FunctionAlign) };
        Section rodata = new(".rodata", SectionKind.ReadOnlyData) { Align = 8 };
        Section data = new(".data", SectionKind.Data) { Align = 8 };
        Section bss = new(".bss", SectionKind.Uninitialised) { Align = 8 };
        obj.Sections.Add(text);
        obj.Sections.Add(rodata);
        obj.Sections.Add(data);
        obj.Sections.Add(bss);

        HashSet<string> defined = new(StringComparer.Ordinal);
        Encoder encoder = new(text);
        List<Corsac.Lang.X86.FrameTable.Entry> frames = new();
        List<(string Function, int Return, int At, Safepoint? Map, int FrameSize)> maps = new();
        FunctionSizes.Clear();

        foreach (Function f in module.Functions)
        {
            MFunction? m = Compile(f, errors, f.Name == module.Entry);
            if (m is null)
            {
                continue;
            }
            int gap = (FunctionAlign - text.Bytes.Count % FunctionAlign) % FunctionAlign;
            Encoder.Nops(text.Bytes, gap);
            int start = text.Bytes.Count;
            int size;
            try
            {
                size = encoder.Encode(m);
            }
            catch (InvalidOperationException e)
            {
                errors.Add(e.Message);
                encoder.ReleaseFunction();
                continue;
            }
            FunctionSizes.Add((f.Name, size));
            obj.Symbols.Add(new Symbol
            {
                Name = f.Name, Section = text, Offset = start, Size = size, IsFunction = true, Global = f.Exported,
            });
            defined.Add(f.Name);

            frames.Add(new Corsac.Lang.X86.FrameTable.Entry(
                f.Name, f.Display ?? f.Name, f.SourceFile ?? "", start, size,
                new List<(int, int)>(encoder.Lines)));

            if (StackMaps)
            {
                foreach ((MInstr call, int ret) in encoder.CallSites)
                {
                    maps.Add((f.Name, ret, start + ret, m.Safepoints.GetValueOrDefault(call), m.Frame.Size));
                }
            }

            // Jump tables: eight-byte absolute addresses of the blocks.
            foreach ((string sym, List<MBlock> targets) in encoder.Tables)
            {
                Pad(rodata, 8, 0);
                obj.Symbols.Add(new Symbol { Name = sym, Section = rodata, Offset = rodata.Bytes.Count, Size = targets.Count * 8, Global = false });
                defined.Add(sym);
                foreach (MBlock t in targets)
                {
                    rodata.Relocs.Add(new Relocation(rodata.Bytes.Count, f.Name, t.Offset, RelocKind.Abs64));
                    rodata.Bytes.AddRange(new byte[8]);
                }
            }
            encoder.ReleaseFunction();
        }

        // The frame table: the x86 format, whose one address is 32 bits --
        // which in the small model every address in the image is.
        if (frames.Count > 0)
        {
            byte[] bytes = Corsac.Lang.X86.FrameTable.Build(frames, out int fixup, out string baseSymbol);
            Pad(rodata, 8, 0);
            int at = rodata.Bytes.Count;
            obj.Symbols.Add(new Symbol
            {
                Name = Corsac.Lang.X86.FrameTable.Symbol, Section = rodata, Offset = at, Size = bytes.Length, Global = false,
            });
            defined.Add(Corsac.Lang.X86.FrameTable.Symbol);
            rodata.Bytes.AddRange(bytes);
            rodata.Relocs.Add(new Relocation(at + fixup, baseSymbol, 0, RelocKind.Abs32));
        }

        foreach (DataItem d in module.Data)
        {
            Section s = d.Zero ? bss : d.ReadOnly ? rodata : data;
            int align = Math.Max(d.Align, 1);
            long offset;
            if (d.Zero)
            {
                bss.ZeroBytes = (bss.ZeroBytes + align - 1) / align * align;
                offset = bss.ZeroBytes;
                bss.ZeroBytes += d.Bytes.Length;
                if (d.Relocs.Count > 0)
                {
                    errors.Add($"{d.Name}: a zero-filled item cannot hold addresses");
                }
            }
            else
            {
                Pad(s, align, 0);
                offset = s.Bytes.Count;
                s.Bytes.AddRange(d.Bytes);
                foreach (DataReloc r in d.Relocs)
                {
                    s.Relocs.Add(new Relocation((int)offset + r.Offset, r.Symbol, r.Addend, RelocKind.Abs64));
                }
            }
            s.Align = Math.Max(s.Align, align);
            obj.Symbols.Add(new Symbol { Name = d.Name, Section = s, Offset = offset, Size = d.Bytes.Length, Global = d.Exported });
            defined.Add(d.Name);
        }

        if (StackMaps)
        {
            EmitStackMaps(obj, maps, defined);
        }

        HashSet<string> undefined = new(module.Imports, StringComparer.Ordinal);
        foreach (Section s in obj.Sections)
        {
            foreach (Relocation r in s.Relocs)
            {
                undefined.Add(r.Symbol);
            }
        }
        foreach (string name in undefined.OrderBy(n => n, StringComparer.Ordinal))
        {
            if (!defined.Contains(name))
            {
                obj.Symbols.Add(new Symbol { Name = name });
            }
        }
        return obj;
    }

    public string Assembly(Module module)
    {
        StringBuilder sb = new();
        List<string> errors = new();
        sb.Append("; module ").Append(module.Name).Append('\n');
        sb.Append("bits 64\n\nsection .text\n\n");
        foreach (Function f in module.Functions)
        {
            MFunction? m = Compile(f, errors);
            if (m is null)
            {
                continue;
            }
            sb.Append(f.Name).Append(":\n");
            foreach (MBlock b in m.Blocks)
            {
                sb.Append(".").Append(b.Name).Append(":\n");
                foreach (MInstr i in b.Instrs)
                {
                    sb.Append("    ").Append(Print(i)).Append('\n');
                }
            }
            sb.Append('\n');
        }
        foreach (string e in errors)
        {
            sb.Append("; error: ").Append(e).Append('\n');
        }
        return sb.ToString();
    }

    private static string Print(MInstr i)
    {
        string name = i.Op switch
        {
            MOp.Jcc => "j" + i.Cond.Mnemonic(),
            MOp.Setcc => "set" + i.Cond.Mnemonic(),
            MOp.Cmovcc => "cmov" + i.Cond.Mnemonic(),
            _ => i.Op.ToString().ToLowerInvariant(),
        };
        string width = i.Op is MOp.Jcc or MOp.Jmp or MOp.Call ? "" : $".{i.Width}";
        return $"{(i.Lock ? "lock " : "")}{name}{width} {string.Join(", ", i.Operands)}";
    }

    private static MFunction? Compile(Function f, List<string> errors, bool entry = false)
    {
        int before = errors.Count;
        MFunction m = Selector.Run(f, errors);
        m.RealignsStack = entry;
        if (errors.Count > before)
        {
            return null;
        }
        // A function that makes a system call saves every callee-saved
        // register: the collector is entered through one, and a caller's
        // reference may be only in one of them (GcRoots.Enter).
        m.SavesEverything = m.Blocks.Any(b => b.Instrs.Any(i => i.Op == MOp.Syscall));
        try
        {
            Allocator.Run(m);
            Peephole.Run(m);
        }
        catch (InvalidOperationException e)
        {
            errors.Add(e.Message);
            return null;
        }
        return m;
    }

    /// <summary>
    /// The stack-map table: the x86 layout (docs/X86-BACKEND.md) at version
    /// 3, whose bitmaps count eight-byte slots -- bit i of word k stands for
    /// [RBP - 8*(32k + i + 1)] -- and whose register mask is by x86-64
    /// hardware number.
    /// </summary>
    private static void EmitStackMaps(ObjectFile obj, List<(string Function, int Return, int At, Safepoint? Map, int FrameSize)> maps, HashSet<string> defined)
    {
        Section s = new(StackMapSection, SectionKind.ReadOnlyData) { Align = 8 };
        obj.Sections.Add(s);

        void Word(long v)
        {
            for (int i = 0; i < 4; i++)
            {
                s.Bytes.Add((byte)(v >> (8 * i)));
            }
        }

        Word(0x314d5343);       // 'CSM1'
        Word(3);
        Word(maps.Count);
        Word(16);
        int baseAt = maps.Count > 0 ? maps[0].At - maps[0].Return : 0;
        if (maps.Count > 0)
        {
            s.Relocs.Add(new Relocation(s.Bytes.Count, maps[0].Function, 0, RelocKind.Abs32));
        }
        Word(0);

        List<uint[]> pool = new();
        Dictionary<string, int> shared = new(StringComparer.Ordinal);
        int[] at = new int[maps.Count];
        int poolStart = 20 + maps.Count * 16;
        int poolWords = 0;
        for (int i = 0; i < maps.Count; i++)
        {
            List<int> slots = maps[i].Map?.SlotOffsets ?? new List<int>();
            if (slots.Count == 0)
            {
                continue;
            }
            int top = 0;
            foreach (int off in slots)
            {
                top = Math.Max(top, -off / 8);
            }
            uint[] bits = new uint[(top + 31) / 32];
            foreach (int off in slots)
            {
                int bit = -off / 8 - 1;
                bits[bit / 32] |= 1u << (bit % 32);
            }
            string key = string.Join(',', bits);
            if (shared.TryGetValue(key, out int existing))
            {
                at[i] = existing;
                continue;
            }
            at[i] = poolStart + poolWords * 4;
            shared.Add(key, at[i]);
            poolWords += 1 + bits.Length;
            pool.Add(bits);
        }

        for (int i = 0; i < maps.Count; i++)
        {
            Word(maps[i].At - baseAt);
            Word(maps[i].Map?.Registers ?? 0);
            Word(at[i]);
            Word(maps[i].FrameSize);
        }
        foreach (uint[] bits in pool)
        {
            Word(bits.Length);
            foreach (uint w in bits)
            {
                Word(w);
            }
        }

        obj.Symbols.Add(new Symbol { Name = StackMapStart, Section = s, Offset = 0, Size = s.Bytes.Count, Global = false });
        obj.Symbols.Add(new Symbol { Name = StackMapEnd, Section = s, Offset = s.Bytes.Count, Size = 0, Global = false });
        defined.Add(StackMapStart);
        defined.Add(StackMapEnd);
    }

    private static void Pad(Section s, int align, byte fill)
    {
        while (s.Bytes.Count % align != 0)
        {
            s.Bytes.Add(fill);
        }
    }
}
