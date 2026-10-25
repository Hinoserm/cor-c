#nullable enable
using System.Text;
using Corsac.Lang.Ir;
using Corsac.Lang.Lto;

namespace Corsac.Lang.X64;

/// <summary>
/// The x86-64 code generator: IR module in, relocatable object out, for a
/// long-mode Linux program on anything from the first AMD64 processor (K8)
/// on.
///
/// Per function: instruction selection (Select.cs), address arithmetic
/// folded into the addressing modes (AddressFold.cs), register allocation
/// (RegAlloc.cs), encoding (Encoder.cs). Data items go in .rodata, .data or
/// .bss by their flags, holding 64-bit absolute relocations where they hold
/// addresses; the frame table is the x86 format. There are no stack maps:
/// the long-mode collector reads every stack whole, word by word, so a table
/// of where the references are at each call would have no reader.
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

    /// <summary>
    /// Record what the link-time optimizer patches without the IR: direct
    /// calls with no arguments and a 32-bit result, and functions that
    /// return a constant (OptimizationSummary). A `call rel32` and the
    /// `mov eax, imm32` that replaces it are both five bytes, and the move
    /// zero-extends, so the patch is the i386 one.
    /// </summary>
    public bool EmitLinkSummary { get; set; }

    /// <summary>
    /// Position-independent code, for a shared object: what another image
    /// may define is reached through the GOT, calls go through the PLT, and
    /// every address held in data is one the loader relocates.
    /// </summary>
    public bool PositionIndependent { get; set; }

    /// <summary>
    /// A safepoint poll on the back edges of every loop that may not end by
    /// itself (BackEdgePolls), as the x86 backend's: an image with a
    /// collector and an operating system to run its threads.
    /// </summary>
    public bool SafepointPolls { get; set; }

    /// <summary>
    /// Names a shared library supplies at load time, which a dynamically
    /// linked executable reaches through its GOT rather than by a copy.
    /// </summary>
    public HashSet<string> Imported { get; } = new(StringComparer.Ordinal);

    /// <summary>Functions compiled at once, each on its own thread; encoding stays serial.</summary>
    public int Workers { get; set; } = 1;

    /// <summary>
    /// Loads a function's body on demand, by index, for the link-time
    /// backend, which holds only headers until a batch is compiled; with
    /// its working cost, batches are sized to FunctionMemoryBudget.
    /// </summary>
    public Func<int, Function>? FunctionLoader { get; set; }
    public Func<int, long>? FunctionLoadBytes { get; set; }
    public long FunctionMemoryBudget { get; set; } = 64L * 1024 * 1024;
    public long PeakBatchBytes { get; private set; }
    public int PeakBatchFunctions { get; private set; }

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
        new X86CodeGenerationContract("k8", "sse2", Corsac.Lang.X86.X86Feature.None, false).Attach(obj);
        Section text = new(".text", SectionKind.Code) { Align = Math.Max(16, FunctionAlign) };
        Section rodata = new(".rodata", SectionKind.ReadOnlyData) { Align = 8 };
        Section data = new(".data", SectionKind.Data) { Align = 8 };
        Section bss = new(".bss", SectionKind.Uninitialised) { Align = 8 };
        // Read-only after the loader has written the addresses in it: what
        // holds an address in a shared object, or an address a library
        // supplies in a program, so no relocation lands in a page the text
        // shares.
        Section relocatedConstants = new(".data.rel.ro", SectionKind.Data) { Align = 8 };
        obj.Sections.Add(text);
        obj.Sections.Add(rodata);
        obj.Sections.Add(data);
        obj.Sections.Add(bss);
        obj.Sections.Add(relocatedConstants);

        // WHAT ANOTHER IMAGE DEFINES, reached through a GOT slot. In a
        // shared object that is everything this object does not define
        // (the link relaxes a slot back to a lea when a sibling object turns
        // out to define it); in a program, what its libraries supply.
        HashSet<string> definedNames = new(StringComparer.Ordinal);
        foreach (Function f in module.Functions) definedNames.Add(f.Name);
        foreach (DataItem d in module.Data) definedNames.Add(d.Name);
        definedNames.Add(Corsac.Lang.X86.FrameTable.Symbol);
        Func<string, bool>? isExternal = PositionIndependent ? name => !definedNames.Contains(name)
            : Imported.Count > 0 ? name => Imported.Contains(name) && !definedNames.Contains(name)
            : null;
        bool loaderRelocates = PositionIndependent || Imported.Count > 0;

        HashSet<string> defined = new(StringComparer.Ordinal);
        Encoder encoder = new(text);
        List<Corsac.Lang.X86.FrameTable.Entry> frames = new();
        FunctionSizes.Clear();

        if (Workers < 1 || Workers > 64) throw new ArgumentOutOfRangeException(nameof(Workers));
        OptimizationSummary summary = new();

        // A bounded window, as the i386 backend keeps: workers select and
        // allocate disjoint functions; encoding and layout stay serial, so
        // the object is the same whatever the worker count.
        int workers = Workers;
        int window = FunctionLoader is null ? workers * 4 : workers;
        MFunction?[] compiled = new MFunction?[window];
        List<string>?[] diagnostics = new List<string>?[window];
        int batchStart = 0, batchEnd = 0;
        PeakBatchBytes = 0; PeakBatchFunctions = 0;
        for (int functionIndex = 0; functionIndex < module.Functions.Count; functionIndex++)
        {
            MFunction? m;
            if (workers == 1 && FunctionLoader is null)
            {
                Function only = module.Functions[functionIndex];
                m = Compile(only, errors, only.Name == module.Entry, isExternal);
            }
            else
            {
                if (functionIndex == batchEnd)
                {
                    int first = functionIndex;
                    int count = Math.Min(window, module.Functions.Count - first);
                    long bytes = 0;
                    if (FunctionLoader is not null)
                    {
                        count = 0;
                        while (count < window && first + count < module.Functions.Count)
                        {
                            long cost = FunctionLoadBytes?.Invoke(first + count)
                                ?? throw new InvalidOperationException("Deferred functions require memory costs");
                            if (cost <= 0)
                                throw new InvalidDataException("Function has no backend working cost: " + module.Functions[first + count].Name);
                            if (count > 0 && cost > FunctionMemoryBudget - bytes) break;
                            bytes += cost; count++;
                            // Bigger than the whole budget: compiled alone, never refused.
                            if (cost > FunctionMemoryBudget) break;
                        }
                    }
                    batchStart = first; batchEnd = first + count;
                    PeakBatchBytes = Math.Max(PeakBatchBytes, bytes);
                    PeakBatchFunctions = Math.Max(PeakBatchFunctions, count);
                    int active = Math.Min(workers, count);
                    Task[] tasks = new Task[active];
                    for (int worker = 0; worker < active; worker++)
                    {
                        int lane = worker;
                        tasks[worker] = Task.Run(() =>
                        {
                            for (int item = lane; item < count; item += active)
                            {
                                List<string> localErrors = new();
                                Function body = FunctionLoader?.Invoke(first + item) ?? module.Functions[first + item];
                                compiled[item] = Compile(body, localErrors, body.Name == module.Entry, isExternal);
                                diagnostics[item] = localErrors;
                            }
                        });
                    }
                    Task.WhenAll(tasks).Wait();
                }
                int slot = functionIndex - batchStart;
                errors.AddRange(diagnostics[slot]!);
                m = compiled[slot];
                compiled[slot] = null;
                diagnostics[slot] = null;
            }
            if (m is null)
            {
                continue;
            }
            Function f = m.Source;
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
            if (EmitLinkSummary)
            {
                HashSet<string> eligible = new(f.Blocks.SelectMany(b => b.Instrs)
                    .Where(i => i.Op == Opcode.Call && i.Operands.Count == 0 && i.Dest?.Type == IrType.I32 && i.Callee is not null)
                    .Select(i => i.Callee!), StringComparer.Ordinal);
                foreach ((MInstr call, int ret) in encoder.CallSites)
                    if (call.Op == MOp.Call && call.Operands[0] is MImm { Symbol: { } callee, Value: 0 } && eligible.Contains(callee))
                        summary.Calls.Add(new DirectCall(start + ret - 4, callee));
            }
            FunctionSizes.Add((f.Name, size));
            obj.Symbols.Add(new Symbol
            {
                Name = f.Name, Section = text, Offset = start, Size = size, IsFunction = true, Global = f.Exported,
            });
            defined.Add(f.Name);
            if (EmitLinkSummary)
                Corsac.Lang.Opt.LinkSummary.AddConstantReturns(new[] { f }, obj, summary);

            frames.Add(new Corsac.Lang.X86.FrameTable.Entry(
                f.Name, f.Display ?? f.Name, f.SourceFile ?? "", start, size,
                new List<(int, int)>(encoder.Lines)));

            // Jump tables: eight-byte absolute addresses of the blocks, which
            // the loader relocates in a shared object.
            Section tables = PositionIndependent ? relocatedConstants : rodata;
            foreach ((string sym, List<MBlock> targets) in encoder.Tables)
            {
                Pad(tables, 8, 0);
                obj.Symbols.Add(new Symbol { Name = sym, Section = tables, Offset = tables.Bytes.Count, Size = targets.Count * 8, Global = false });
                defined.Add(sym);
                foreach (MBlock t in targets)
                {
                    tables.Relocs.Add(new Relocation(tables.Bytes.Count, f.Name, t.Offset, RelocKind.Abs64));
                    tables.Bytes.AddRange(new byte[8]);
                }
            }
            encoder.ReleaseFunction();
        }

        // The frame table: the x86 format, whose one address is 32 bits. In
        // long mode it is the distance from the word to the first function
        // (PC32), which a shared object loaded anywhere can hold without the
        // loader's help; the runtime's TableAddress reads it back.
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
            rodata.Relocs.Add(new Relocation(at + fixup, baseSymbol, 0, RelocKind.Rel32));
        }

        foreach (DataItem d in module.Data)
        {
            bool loaderWrites = d.Relocs.Count > 0
                && (PositionIndependent || d.Relocs.Any(r => isExternal is not null && isExternal(r.Symbol)));
            Section s = d.Zero ? bss : d.ReadOnly ? (loaderWrites ? relocatedConstants : rodata) : data;
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
        if (EmitLinkSummary)
        {
            summary.Attach(obj);
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

    private MFunction? Compile(Function f, List<string> errors, bool entry = false, Func<string, bool>? isExternal = null)
    {
        int before = errors.Count;
        if (SafepointPolls) Corsac.Lang.Opt.BackEdgePolls.Run(f);
        // The frees before a trap, one copy for every trap that has them (SharedTrapFrees).
        Corsac.Lang.Opt.SharedTrapFrees.Run(f);
        // Frame slots whose lives do not meet share their bytes (SlotShare).
        Corsac.Lang.Opt.SlotShare.Run(f);
        MFunction m = Selector.Run(f, errors, isExternal);
        m.RealignsStack = entry;
        if (errors.Count > before)
        {
            return null;
        }
        // A function that makes a system call saves every callee-saved
        // register: the collector is entered through one, and a caller's
        // reference may be only in one of them (GcRoots.Enter).
        // AND SO DOES ONE THAT CATCHES: a throw restores only the stack and
        // frame pointers, so the registers the unwound frames saved and used
        // reach the landing pad as they left them. Saved here, the catching
        // function's epilogue puts back its caller's, whatever happened below;
        // saved only where it uses them, a caller's reference in R15 came back
        // as whatever the thrower's callee had put there.
        m.SavesEverything = m.Blocks.Any(b => b.Source?.IsLandingPad == true
            || b.Instrs.Any(i => i.Op is MOp.Syscall or MOp.SoftInt));
        try
        {
            AddressFold.Run(m);
            IfConvert.Run(m);
            Allocator.Run(m);
            Layout.Run(m);
            LoopRotate.Run(m);
            Peephole.Run(m);
        }
        catch (InvalidOperationException e)
        {
            errors.Add(e.Message);
            return null;
        }
        return m;
    }

    private static void Pad(Section s, int align, byte fill)
    {
        while (s.Bytes.Count % align != 0)
        {
            s.Bytes.Add(fill);
        }
    }
}
