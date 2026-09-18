#nullable enable
using System.Text;
using System.Threading.Tasks;
using Corsac.Lang.Ir;
using Corsac.Lang.Lto;

namespace Corsac.Lang.X86;

/// <summary>
/// The 486 code generator: IR module in, relocatable object out.
///
/// Per function: instruction selection (Select.cs), register allocation
/// (RegAlloc.cs), encoding (Encoder.cs). The module's data items are placed
/// in .rodata, .data or .bss by their flags, and every name the code or data
/// refers to without defining becomes an undefined symbol for the linker.
///
/// Static, non-PIC code. The pieces a position-independent mode will need
/// are already separate: the selector's symbol-reference helpers are the
/// one place absolute relocations are chosen, and the allocator can drop
/// EBX from its set per function.
/// </summary>
public sealed class X86Backend : IBackend
{
    public Target Target => Target.X86;

    /// <summary>
    /// Alignment of each function's first byte. Four by default: a 486
    /// gains nothing measurable from more, and on a machine this size the
    /// padding is bytes that could have been code. Raise it per build when
    /// a hot function wants its entry on a fetch line.
    /// </summary>
    public int FunctionAlign { get; set; } = 4;

    /// <summary>Bounded task workers for selection/allocation; emission stays ordered.</summary>
    public int Workers { get; set; } = 1;
    public bool EmitLinkSummary { get; set; }
    /// <summary>Hosted ABI owns x87/MMX state; freestanding kernel code must not borrow it implicitly.</summary>
    public bool AutomaticPacked { get; set; } = true;
    public Func<int, Function>? FunctionLoader { get; set; }
    public Func<int, long>? FunctionLoadBytes { get; set; }
    public long FunctionMemoryBudget { get; set; } = 64L * 1024 * 1024;
    public long PeakBatchBytes { get; private set; }
    public int PeakBatchFunctions { get; private set; }

    /// <summary>
    /// Position-independent code: globals through the GOT, calls through
    /// the PLT, EBX pinned. On for a shared object, off for an executable,
    /// which is why the flag is here and not in the selector.
    /// </summary>
    public bool PositionIndependent { get; set; }

    /// <summary>
    /// Names a shared library will supply at load time, which this object
    /// must not reach by absolute address. Empty for a static link, and for
    /// a shared object, where the GOT already answers everything.
    /// See <see cref="Imports"/> for what it costs and why it is not a copy
    /// relocation.
    /// </summary>
    public HashSet<string> Imported { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Emit the stack-map table: a call-site index the collector walks to
    /// find the live references in a suspended frame. On by default -- it
    /// is read-only data no instruction touches, so a program with no
    /// collector pays only its bytes.
    /// </summary>
    public bool StackMaps { get; set; } = true;

    /// <summary>
    /// Emit a card-marking write barrier on every store of a reference into
    /// a heap object. OFF, and it stays off until the stack maps are precise
    /// rather than conservative. Where it goes: Select.cs, the Store cases
    /// that write a word through an object pointer.
    /// </summary>
    public bool WriteBarriers { get; set; }

    /// <summary>
    /// Emit a safepoint poll at loop back-edges. OFF, for the same reason.
    /// Where it goes: Select.cs, where a jump to an already-emitted block is
    /// selected. A call is already a safepoint by virtue of having a map.
    /// </summary>
    public bool SafepointPolls { get; set; }

    /// <summary>The section stack maps go in, and the symbols the runtime finds them by.</summary>
    public const string StackMapSection = ".corsac.stackmaps";
    public const string StackMapStart = "__corsac_stackmaps";
    public const string StackMapEnd = "__corsac_stackmaps_end";

    /// <summary>Code bytes per function from the last Generate, in module order.</summary>
    public List<(string Name, int Bytes)> FunctionSizes { get; } = new();

    /// <summary>Per-function code sizes as a table, for `--stats`.</summary>
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
        if (Workers < 1 || Workers > 64) throw new ArgumentOutOfRangeException(nameof(Workers));
        ObjectFile obj = new();
        (Target.X86Profile.Contract with { AutomaticPacked = AutomaticPacked }).Attach(obj);
        OptimizationSummary summary = new();
        Section text = new(".text", SectionKind.Code) { Align = Math.Max(4, FunctionAlign) };
        Section rodata = new(".rodata", SectionKind.ReadOnlyData);
        Section data = new(".data", SectionKind.Data);
        Section relocatedConstants = new(".data.rel.ro", SectionKind.Data);
        Section bss = new(".bss", SectionKind.Uninitialised);
        obj.Sections.Add(text);
        obj.Sections.Add(rodata);
        obj.Sections.Add(data);
        obj.Sections.Add(bss);
        obj.Sections.Add(relocatedConstants);

        HashSet<string> defined = new(StringComparer.Ordinal);
        Encoder encoder = new(text);
        List<FrameTable.Entry> frames = new();
        FunctionSizes.Clear();
        List<(string Function, int Return, Safepoint? Map, int FrameSize)> maps = new();

        // GOTOFF is sound only for a name this object defines and does not
        // export: anything exported can be interposed at load time, and then
        // its offset from this object's GOT is not where it lives.
        HashSet<string> privateNames = new(StringComparer.Ordinal);
        if (PositionIndependent)
        {
            // The tables this generator writes are one per image and named
            // by nobody else, so they are reached from the GOT pointer
            // directly rather than through a slot.
            privateNames.Add(FrameTable.Symbol);
            privateNames.Add(StackMapStart);
            privateNames.Add(StackMapEnd);
            foreach (Function f in module.Functions)
            {
                if (!f.Exported)
                {
                    privateNames.Add(f.Name);
                }
            }
            foreach (DataItem d in module.Data)
            {
                if (!d.Exported)
                {
                    privateNames.Add(d.Name);
                }
            }
        }
        bool IsPrivate(string name) => privateNames.Contains(name);

        // A name this object defines, exported or not. A call to one is a
        // direct relative call even in a shared object, because the linker
        // binds a defined symbol to its own definition.
        HashSet<string> definedNames = new(StringComparer.Ordinal);
        if (PositionIndependent)
        {
            definedNames.Add(FrameTable.Symbol);
            definedNames.Add(StackMapStart);
            definedNames.Add(StackMapEnd);
            foreach (Function f in module.Functions)
            {
                definedNames.Add(f.Name);
            }
            foreach (DataItem d in module.Data)
            {
                definedNames.Add(d.Name);
            }
        }
        bool IsDefined(string name) => definedNames.Contains(name);

        // What the loader will supply. Only a non-position-independent
        // object asks: a shared one reaches everything it does not define
        // through its GOT already.
        HashSet<string> imported = PositionIndependent || Imported.Count == 0
            ? new HashSet<string>(StringComparer.Ordinal)
            : Imported;
        bool IsImported(string name) => imported.Contains(name);

        // A bounded window avoids retaining a whole module of machine IR.
        // Workers only read the symbol sets and each owns disjoint functions,
        // results and diagnostics. Encoding and layout below remain serial.
        int workers = Workers;
        int window = FunctionLoader is null ? workers * 4 : workers;
        MFunction?[] compiled = new MFunction?[window];
        List<string>?[] diagnostics = new List<string>?[window];
        int batchStart = 0, batchEnd = 0;
        PeakBatchBytes = 0; PeakBatchFunctions = 0;
        for (int functionIndex = 0; functionIndex < module.Functions.Count; functionIndex++)
        {
            Function f = module.Functions[functionIndex];
            MFunction? m;
            if (workers == 1 && FunctionLoader is null)
            {
                m = Compile(f, errors, PositionIndependent ? IsPrivate : null, IsDefined,
                    imported.Count == 0 ? null : IsImported);
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
                            if (cost <= 0 || cost > FunctionMemoryBudget)
                                throw new InvalidDataException("Function exceeds backend working budget: " + module.Functions[first + count].Name);
                            if (cost > FunctionMemoryBudget - bytes) break;
                            bytes += cost; count++;
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
                                compiled[item] = Compile(FunctionLoader?.Invoke(first + item) ?? module.Functions[first + item], localErrors,
                                    PositionIndependent ? IsPrivate : null, IsDefined,
                                    imported.Count == 0 ? null : IsImported);
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
            f = m.Source;
            // The gap before a function is never executed, but it is filled
            // with real no-ops rather than zeros so a disassembly reads cleanly.
            int gap = (FunctionAlign - text.Bytes.Count % FunctionAlign) % FunctionAlign;
            Encoder.Nops(text.Bytes, gap);
            int start = text.Bytes.Count;
            int size = encoder.Encode(m);
            if (EmitLinkSummary && !PositionIndependent)
            {
                HashSet<string> eligible = new(f.Blocks.SelectMany(b => b.Instrs)
                    .Where(i => i.Op == Opcode.Call && i.Operands.Count == 0 && i.Dest?.Type == IrType.I32 && i.Callee is not null)
                    .Select(i => i.Callee!), StringComparer.Ordinal);
                foreach ((MInstr call, int ret) in encoder.CallSites)
                    if (call.Op == MOp.Call && call.CallReloc == RelocKind.Rel32
                        && call.Operands[0] is MImm { Symbol: { } callee, Value: 0 } && eligible.Contains(callee))
                        summary.Calls.Add(new DirectCall(start + ret - 4, callee));
            }
            FunctionSizes.Add((f.Name, size));
            obj.Symbols.Add(new Symbol
            {
                Name = f.Name, Section = text, Offset = start, Size = size, IsFunction = true, Global = f.Exported,
            });
            defined.Add(f.Name);
            if (EmitLinkSummary && !PositionIndependent)
                Corsac.Lang.Opt.LinkSummary.AddConstantReturns(new[] { f }, obj, summary);

            // WHAT THIS FUNCTION IS, for a fault to read back. Recorded per
            // function as it is encoded, because this is the one moment the
            // name, the source and the final byte offsets are all in hand.
            frames.Add(new FrameTable.Entry(
                f.Name, f.Display ?? f.Name, f.SourceFile ?? "", start, size,
                new List<(int, int)>(encoder.Lines)));

            if (StackMaps)
            {
                foreach ((MInstr call, int ret) in encoder.CallSites)
                {
                    maps.Add((f.Name, ret, m.Safepoints.GetValueOrDefault(call), m.Frame.Size));
                }
            }

            Section tables = PositionIndependent ? relocatedConstants : rodata;
            foreach ((string sym, List<MBlock> targets) in encoder.Tables)
            {
                Pad(tables, 4, 0);
                obj.Symbols.Add(new Symbol { Name = sym, Section = tables, Offset = tables.Bytes.Count, Size = targets.Count * 4, Global = false });
                defined.Add(sym);
                foreach (MBlock t in targets)
                {
                    tables.Relocs.Add(new Relocation(tables.Bytes.Count, f.Name, t.Offset, RelocKind.Abs32));
                    tables.Bytes.AddRange(new byte[4]);
                }
            }
            encoder.ReleaseFunction();
        }

        // THE FRAME TABLE, once every function's bytes are placed. It goes in
        // .rodata, where it is mapped and readable, rather than a section of
        // its own that every linker script would have to learn about -- and so
        // a flat freestanding image carries it exactly as an ELF does.
        if (frames.Count > 0)
        {
            byte[] bytes = FrameTable.Build(frames, out int fixup, out string baseSymbol);
            // It holds one address, so in a shared object it holds a
            // relocation, and a relocation may not land in a page the loader
            // has to keep read-only. Jump tables move for the same reason.
            Section frameSection = PositionIndependent ? relocatedConstants : rodata;
            Pad(frameSection, 4, 0);
            int at = frameSection.Bytes.Count;
            obj.Symbols.Add(new Symbol
            {
                Name = FrameTable.Symbol, Section = frameSection, Offset = at, Size = bytes.Length, Global = false,
            });
            defined.Add(FrameTable.Symbol);
            frameSection.Bytes.AddRange(bytes);
            frameSection.Relocs.Add(new Relocation(at + fixup, baseSymbol, 0, RelocKind.Abs32));
        }

        foreach (DataItem d in module.Data)
        {
            // READ-ONLY UNTIL SOMEBODY HAS TO WRITE IN IT. A data item
            // holding an address is a relocation, and a relocation in a
            // read-only page is one the loader must make writable -- the
            // whole page, privately, for every process. In a shared object
            // that is every address; in an executable it is only an address
            // a library supplies, which a string literal has (the descriptor
            // of `byte[]` is the runtime's).
            bool loaderWrites = d.Relocs.Count > 0
                && (PositionIndependent || d.Relocs.Any(r => imported.Contains(r.Symbol)));
            // Immutable relocations need loader writes, not conservative
            // heap-root scanning. Keep them distinct from mutable statics.
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
                    s.Relocs.Add(new Relocation((int)offset + r.Offset, r.Symbol, r.Addend, RelocKind.Abs32));
                }
            }
            s.Align = Math.Max(s.Align, align);
            obj.Symbols.Add(new Symbol { Name = d.Name, Section = s, Offset = offset, Size = d.Bytes.Length, Global = d.Exported });
            defined.Add(d.Name);
        }

        if (StackMaps)
        {
            EmitStackMaps(obj, maps, defined, PositionIndependent);
        }

        // Whatever is referenced and not defined here is the linker's to find.
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
        if (EmitLinkSummary && !PositionIndependent)
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
        sb.Append("bits 32\n\n");
        sb.Append("section .text\n\n");
        foreach (Function f in module.Functions)
        {
            MFunction? m = Compile(f, errors);
            if (m is not null)
            {
                AsmText.Print(sb, m);
            }
        }
        foreach (string section in new[] { ".rodata", ".data", ".bss" })
        {
            List<DataItem> items = module.Data.Where(d => (d.Zero ? ".bss" : d.ReadOnly ? ".rodata" : ".data") == section).ToList();
            if (items.Count == 0)
            {
                continue;
            }
            sb.Append("section ").Append(section).Append('\n');
            foreach (DataItem d in items)
            {
                sb.Append("    align ").Append(d.Align).Append('\n');
                sb.Append(d.Name).Append(":\n");
                if (d.Zero)
                {
                    sb.Append("    resb ").Append(d.Bytes.Length).Append('\n');
                    continue;
                }
                sb.Append("    db ").Append(string.Join(", ", d.Bytes.Select(b => $"0x{b:x2}"))).Append('\n');
                foreach (DataReloc r in d.Relocs)
                {
                    sb.Append("    ; +").Append(r.Offset).Append(": dd ").Append(r.Symbol);
                    if (r.Addend != 0)
                    {
                        sb.Append('+').Append(r.Addend);
                    }
                    sb.Append('\n');
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

    /// <summary>Select and allocate one function; null, with errors reported, if it cannot be compiled.</summary>
    private static MFunction? Compile(Function f, List<string> errors, Func<string, bool>? isPrivate = null, Func<string, bool>? isDefined = null, Func<string, bool>? isImported = null)
    {
        int before = errors.Count;
        MFunction m = Selector.Run(f, errors, AutomaticPacked);
        if (errors.Count > before)
        {
            return null;
        }
        if (isPrivate is not null)
        {
            Pic.Run(m, isPrivate, isDefined ?? (_ => false), errors);
            if (errors.Count > before)
            {
                return null;
            }
        }
        else if (isImported is not null)
        {
            Imports.Run(m, isImported, errors);
            if (errors.Count > before)
            {
                return null;
            }
        }
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
    /// The stack-map table, as described in docs/X86-BACKEND.md.
    ///
    ///   header, 16 bytes: magic 'CSM1', version, entry count, entry stride
    ///   entries, 16 bytes each, in code order within each function:
    ///     +0  the return address of the call (an absolute relocation)
    ///     +4  callee-saved registers holding references, bit per hardware number
    ///     +8  byte offset from the table's start to this entry's slot
    ///         bitmap, or zero when the frame holds no live reference
    ///     +12 bytes of frame below EBP, so a walker can check a bitmap
    ///         against the frame it is reading
    ///   bitmaps, after the entries: a word of length in words, then that
    ///   many words, bit i of word k standing for [EBP - 4*(32k + i + 1)].
    ///
    /// Entries are not sorted here: the linker decides the addresses, so
    /// ordering is the runtime's to do once at startup if it wants a binary
    /// search rather than a scan.
    /// </summary>
    private static void EmitStackMaps(ObjectFile obj, List<(string Function, int Return, Safepoint? Map, int FrameSize)> maps, HashSet<string> defined, bool pic)
    {
        // Every entry holds a return address, so in a shared object every
        // entry is a relocation, and a relocation in a read-only section is a
        // page the loader has to write to and every process a private copy of
        // it. Writable in that mode, as the frame table and the jump tables are.
        Section s = new(pic ? ".data.rel.ro" + StackMapSection : StackMapSection,
            pic ? SectionKind.Data : SectionKind.ReadOnlyData) { Align = 4 };
        obj.Sections.Add(s);

        void Word(long v)
        {
            for (int i = 0; i < 4; i++)
            {
                s.Bytes.Add((byte)(v >> (8 * i)));
            }
        }

        Word(0x314d5343);       // 'CSM1'
        Word(1);
        Word(maps.Count);
        Word(16);

        // The bitmaps are sized first so an entry can name where its own one
        // will land: the pool starts right after the fixed-size entries.
        List<uint[]> pool = new();
        int[] at = new int[maps.Count];
        int poolStart = 16 + maps.Count * 16;
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
                top = Math.Max(top, -off / 4);
            }
            uint[] bits = new uint[(top + 31) / 32];
            foreach (int off in slots)
            {
                int bit = -off / 4 - 1;
                bits[bit / 32] |= 1u << (bit % 32);
            }
            at[i] = poolStart + poolWords * 4;
            poolWords += 1 + bits.Length;
            pool.Add(bits);
        }

        for (int i = 0; i < maps.Count; i++)
        {
            s.Relocs.Add(new Relocation(s.Bytes.Count, maps[i].Function, maps[i].Return, RelocKind.Abs32));
            Word(0);
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

        // Each independently compiled object owns a complete table. These
        // boundaries must not collide or bind another object's table. A future
        // image-wide precise collector needs a directory of tables, not one
        // arbitrarily selected global definition.
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
