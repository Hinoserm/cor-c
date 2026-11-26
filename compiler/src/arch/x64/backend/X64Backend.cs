#nullable enable
using System.Text;
using Corsac.Lang.Ir;
using Corsac.Lang.Lto;

using Corsac.Lang.X86;

namespace Corsac.Lang.X64;

using Encoder = Corsac.Lang.X86.Encoder;


/// <summary>
/// The x86-64 code generator: IR module in, relocatable object out, for a
/// long-mode Linux program on anything from the first AMD64 processor (K8)
/// on.
///
/// Per function: instruction selection (x86/backend/Select.Long.cs), address arithmetic
/// folded into the addressing modes (AddressFold.cs), register allocation
/// (RegAlloc.cs), encoding (the family's Encoder, x86/backend). Data items go in .rodata, .data or
/// .bss by their flags, holding 64-bit absolute relocations where they hold
/// addresses; the frame table is the x86 format. The stack maps are the
/// same table i386 writes (StackMapTable), in this target's layout: at each
/// call the spill slots, the IR frame slot words and the callee-saved
/// registers that hold live references, which the collector reads a stack
/// by (Gc.ScanOwnStack) instead of every word of it.
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

    /// <summary>--opt-size: no loop head padded to its line (Encoder.OptimizeSize), as i386's.</summary>
    public bool OptimizeSize { get; set; }

    /// <summary>
    /// A safepoint poll on the back edges of every loop that may not end by
    /// itself (BackEdgePolls), as the x86 backend's: an image with a
    /// collector and an operating system to run its threads.
    /// </summary>
    public bool SafepointPolls { get; set; }

    /// <summary>
    /// Whether the object carries its stack maps (`.corsac.stackmaps`): at
    /// each call, the spill slots, the IR frame slot words and the
    /// callee-saved registers holding live references -- the table i386
    /// writes (StackMapTable), in x86-64's layout (StackMapLayout.X64).
    /// Off for a program that needs no collector, as i386's.
    /// </summary>
    public bool StackMaps { get; set; } = true;

    /// <summary>
    /// Names a shared library supplies at load time, which a dynamically
    /// linked executable reaches through its GOT rather than by a copy.
    /// </summary>
    public HashSet<string> Imported { get; } = new(StringComparer.Ordinal);

    /// <summary>--count-spills: each block counts its spill slots' reads and writes as it runs (SpillCount).</summary>
    public bool CountSpills { get; set; }

    /// <summary>--count-spills-map: where each counted block is listed (SpillCounter), or null.</summary>
    public string? SpillMap { get; set; }

    /// <summary>Functions compiled at once, each on its own thread; encoding stays serial.</summary>
    public int Workers { get; set; } = 1;

    /// <summary>
    /// Done to each function just before it is selected, on whichever worker
    /// selects it (the allocation put in place, AllocatorFastPaths.AtCodegen),
    /// as the i386 backend does.
    /// </summary>
    public Action<Function>? Prepare { get; set; }
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

    /// <summary>
    /// Whether the code runs under an operating system (Linux): entered
    /// with the stack at sixteen before every call, so a packed value's
    /// spill slot can be moved by MOVAPS (MFunction.StackAtSixteen).
    /// False for a freestanding image. Written into the object's contract
    /// (its AutomaticPacked word, which i386 uses for the same question of
    /// whose state the code owns) and read back where the unit is made again.
    /// </summary>
    public bool Hosted { get; init; }

    /// <summary>
    /// Whether a function's IR and a data item's bytes are let go once they
    /// are placed, for a caller that reads nothing of the module's bodies
    /// after Generate (Driver.Compile), as X86Backend's: the whole module's
    /// IR lived through code generation beside the machine code otherwise.
    /// </summary>
    public bool ReleaseBodies { get; set; }

    /// <summary>
    /// The runtime's streaming copy and fill, where the module has them to
    /// call (Module.RuntimeHelpers: lowering kept them) and the code is
    /// hosted: a copy or a fill calls them at 256 KB and over -- behind a
    /// compare where its length is known only as it runs
    /// (Selector.StreamedOr) -- as i386's.
    /// </summary>
    private string? _streamCopy, _streamFill;

    private void FindStreams(Module module)
    {
        _streamCopy = Hosted && module.RuntimeHelpers.Contains(Corsac.Lang.Lto.RuntimeAbi.StreamCopy) ? Corsac.Lang.Lto.RuntimeAbi.StreamCopy : null;
        _streamFill = Hosted && module.RuntimeHelpers.Contains(Corsac.Lang.Lto.RuntimeAbi.StreamFill) ? Corsac.Lang.Lto.RuntimeAbi.StreamFill : null;
    }

    public ObjectFile Generate(Module module, List<string> errors)
    {
        // Long mode, for the machine IR the two backends share (X86Mode).
        X86Mode.Long = true;
        FindStreams(module);
        ObjectFile obj = new();
        // The processor --cpu named, its instructions what this unit may use.
        // And whether the code is an operating system's guest (Hosted): the
        // unit made again at the link reads it back (UnitBackend).
        Target.X86_64.X86Profile.LongModeContract(Hosted).Attach(obj);
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
        HashSet<string> definedNames = DefinedNames(module);
        Func<string, bool>? isExternal = External(definedNames);
        bool loaderRelocates = PositionIndependent || Imported.Count > 0;

        HashSet<string> defined = new(StringComparer.Ordinal);
        Encoder encoder = new(text, OptimizeSize);
        List<Corsac.Lang.X86.FrameTable.Entry> frames = new();
        // The stack maps' records as each function is encoded, starts from the first function with a call (the table's base).
        List<StackMapTable.Function> mapFunctions = new();
        string? mapBase = null;
        int mapBaseAt = 0;
        FunctionSizes.Clear();

        if (Workers < 1 || Workers > 64) throw new ArgumentOutOfRangeException(nameof(Workers));
        OptimizationSummary summary = new();

        // A bounded window (CompileWindow, both backends'): workers select
        // and allocate disjoint functions; encoding and layout stay serial,
        // so the object is the same whatever the worker count.
        Corsac.Lang.RegAlloc.SpillCounter? spills = CountSpills ? new(SpillMap) : null;
        Corsac.Lang.Backends.CompileWindow<MFunction> window = new(module.Functions, Workers, FunctionLoader, FunctionLoadBytes, FunctionMemoryBudget,
            (body, said) => Compile(body, said, body.Name == module.Entry, isExternal));
        PeakBatchBytes = 0; PeakBatchFunctions = 0;
        for (int functionIndex = 0; functionIndex < module.Functions.Count; functionIndex++)
        {
            MFunction? m = window.Take(functionIndex, errors);
            (PeakBatchBytes, PeakBatchFunctions) = (window.PeakBatchBytes, window.PeakBatchFunctions);
            if (m is null)
            {
                continue;
            }
            // Here, in the order the functions are laid down, not on the
            // workers: the blocks' numbers are the same from run to run.
            if (spills is not null) SpillCount.Run(m, spills);
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

            if (StackMaps)
            {
                // A record only for a function with a call, from the first
                // such function's start (the table's base), its sites in
                // the order of the code -- as i386's (StackMapRecords).
                StackMapTable.Function? record = null;
                List<int>? slotWords = null;
                foreach ((MInstr call, int ret) in encoder.CallSites)
                {
                    if (record is null)
                    {
                        if (m.Frame.Size > 0xFFFFFF) throw new InvalidOperationException("a frame over 16 MB has no stack map");
                        if (mapBase is null) { mapBase = f.Name; mapBaseAt = start; }
                        // Every IR slot word, where the liveness left the function whole: only then.
                        slotWords = m.SlotLiveness is null ? ObjectWords(m) : new List<int>();
                        record = Corsac.Lang.Frames.StackMapRecords.Function(StackMapLayout.X64, start - mapBaseAt, m.Frame.Size,
                            m.FramePointerFree ? 0 : SavedMask(m), slotWords, m.SlotLiveness);
                        mapFunctions.Add(record);
                    }
                    if (ret > 0x7FFFFF) throw new InvalidOperationException("a function over 8 MB has no stack map");
                    // A function that gives RBP a value has no frame a map
                    // could count from: its calls (none but a fault's way
                    // out) have no map, and its frame is read whole.
                    record.Sites.Add(Corsac.Lang.Frames.StackMapRecords.Site(StackMapLayout.X64, call, ret,
                        m.FramePointerFree ? null : m.Safepoints.GetValueOrDefault(call), slotWords!, m.SlotLiveness));
                }
            }

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
            // (Not a unit made again at the link: UnitBackend asks for no release.)
            if (ReleaseBodies) Corsac.Lang.Backends.ObjectData.Release(f);
        }

        EmitStoreSequences(module, text, obj, defined);

        if (StackMaps) EmitStackMaps(obj, mapFunctions, mapBase, defined);

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

        spills?.Table(module, pointerBytes: 8, errors);

        // The data, each item into its section, and the float constants the
        // code reads RIP-relative (ObjectData, both backends': the family's
        // pool, X86Backend.FloatPoolPrefix, Select.Sse's FloatConstant names
        // them by their bits). A name is another image's where the loader
        // supplies it (isExternal).
        // What this module defines, as i386's backend asks it: its functions
        // and its data -- not the tables every object carries.
        HashSet<string> moduleDefines = new(StringComparer.Ordinal);
        foreach (Function function in module.Functions) moduleDefines.Add(function.Name);
        foreach (DataItem item in module.Data) moduleDefines.Add(item.Name);
        Section? importConstants = null, numbers = null;
        Section ImportConstants()
        {
            if (importConstants is null) obj.Sections.Add(importConstants = new Section(Corsac.Lang.Backends.ObjectData.ImportConstantsSection, SectionKind.Data) { Align = 8 });
            return importConstants;
        }
        Section Numbers()
        {
            if (numbers is null) obj.Sections.Add(numbers = new Section(Corsac.Lang.Backends.ObjectData.NumbersSection, SectionKind.Data) { Align = 8 });
            return numbers;
        }
        Corsac.Lang.Backends.ObjectData.Place(module, obj, rodata, data, bss, relocatedConstants, ImportConstants, Numbers, RelocKind.Abs64,
            PositionIndependent, name => isExternal is not null && isExternal(name), moduleDefines.Contains, defined, errors, ReleaseBodies);
        Corsac.Lang.Backends.ObjectData.PlaceFloatPool(obj, text, rodata, Corsac.Lang.X86.X86Backend.FloatPoolPrefix, defined);

        Corsac.Lang.Backends.ObjectData.DeclareUndefined(obj, module.Imports, defined);
        if (EmitLinkSummary)
        {
            summary.Attach(obj);
        }
        return obj;
    }

    /// <summary>The names this module defines: its functions, its data, and the tables every object carries.</summary>
    private static HashSet<string> DefinedNames(Module module)
    {
        HashSet<string> definedNames = new(StringComparer.Ordinal);
        foreach (Function f in module.Functions) definedNames.Add(f.Name);
        foreach (DataItem d in module.Data) definedNames.Add(d.Name);
        definedNames.Add(Corsac.Lang.X86.FrameTable.Symbol);
        definedNames.Add(Corsac.Lang.X86.X86Backend.StackMapStart);
        definedNames.Add(Corsac.Lang.X86.X86Backend.StackMapEnd);
        return definedNames;
    }

    /// <summary>Whether a name is another object's to supply, reached through the loader's tables: every name not defined here in position-independent code, an imported one in a program; null where there is none.</summary>
    private Func<string, bool>? External(HashSet<string> definedNames)
        => PositionIndependent ? name => !definedNames.Contains(name)
            : Imported.Count > 0 ? name => Imported.Contains(name) && !definedNames.Contains(name)
            : null;

    /// <summary>The module as assembly to read (--asm): long mode's machine code as the family's listing prints it (AsmText), then its data.</summary>
    public string Assembly(Module module)
    {
        // Long mode, as Generate says it: the selection, the allocation and
        // the listing all read it.
        X86Mode.Long = true;
        FindStreams(module);
        StringBuilder sb = new();
        List<string> errors = new();
        sb.Append("; module ").Append(module.Name).Append('\n');
        sb.Append("bits 64\n\nsection .text\n\n");
        Func<string, bool>? isExternal = External(DefinedNames(module));
        foreach (Function f in module.Functions)
        {
            // As Generate compiles it: the entry realigns its stack, and a
            // name another object supplies is reached as it will be.
            MFunction? m = Compile(f, errors, f.Name == module.Entry, isExternal);
            if (m is null)
            {
                continue;
            }
            if (CountSpills) SpillCount.Run(m, new Corsac.Lang.RegAlloc.SpillCounter(null));
            AsmText.Print(sb, m);
        }
        AsmText.PrintData(sb, module);
        foreach (string e in errors)
        {
            sb.Append("; error: ").Append(e).Append('\n');
        }
        return sb.ToString();
    }

    private MFunction? Compile(Function f, List<string> errors, bool entry = false, Func<string, bool>? isExternal = null)
    {
        int before = errors.Count;
        Prepare?.Invoke(f);
        if (SafepointPolls) Corsac.Lang.Opt.BackEdgePolls.Run(f);
        // The frees before a trap, one copy for every trap that has them (SharedTrapFrees).
        Corsac.Lang.Opt.SharedTrapFrees.Run(f);
        // Frame slots whose lives do not meet share their bytes (SlotShare).
        Corsac.Lang.Opt.SlotShare.Run(f);
        MFunction m = Selector.RunLong(f, errors, isExternal, _streamCopy, _streamFill);
        m.RealignsStack = entry;
        m.StackAtSixteen = Hosted;
        if (errors.Count > before)
        {
            return null;
        }
        // A function that makes a system call saves every callee-saved
        // register: the collector is entered through one, and a caller's
        // reference may be only in one of them (GcRoots.Enter). So does one
        // that says so (Sys.SaveRegisters), which costs no system call.
        // AND SO DOES ONE THAT CATCHES: a throw restores only the stack and
        // frame pointers, so the registers the unwound frames saved and used
        // reach the landing pad as they left them. Saved here, the catching
        // function's epilogue puts back its caller's, whatever happened below;
        // saved only where it uses them, a caller's reference in R15 came back
        // as whatever the thrower's callee had put there.
        m.SavesEverything = m.Blocks.Any(b => b.Source?.IsLandingPad == true
            || b.Instrs.Any(i => i.Op is MOp.Syscall or MOp.SoftInt or MOp.SaveRegs));
        try
        {
            FamilyPasses.Run(m, StackMaps, conditionalMoves: true);
        }
        catch (InvalidOperationException e)
        {
            errors.Add(e.Message);
            return null;
        }
        return m;
    }

    /// <summary>The callee-saved registers a function's prologue saves, as a mask by hardware number (StackMapLayout.X64 names them).</summary>
    private static uint SavedMask(MFunction m)
    {
        uint mask = 0;
        foreach (Gpr g in m.SavedRegs) mask |= 1u << (int)g;
        return mask;
    }

    /// <summary>Every word of a function's IR frame slots, as RBP offsets: what a call's map names whole where their liveness is not known.</summary>
    private static List<int> ObjectWords(MFunction m)
    {
        SortedSet<int> words = new();
        foreach ((int offset, int bytes) in m.Frame.SlotRanges())
            for (int at = offset & ~7; at < offset + bytes; at += 8)
                if (at < 0) words.Add(at);
        return words.ToList();
    }

    /// <summary>
    /// The stack-map table (StackMapTable, version 1 in x86-64's layout),
    /// one an object, as i386's EmitStackMaps makes it. Its base is the
    /// distance from the header's word to the first function with a call
    /// (PC32): an image loaded anywhere describes itself without the
    /// loader, as this target's frame table does, so the section is
    /// read-only in a shared object too.
    /// </summary>
    private static void EmitStackMaps(ObjectFile obj, List<StackMapTable.Function> functions, string? baseFunction, HashSet<string> defined)
    {
        Section s = new(Corsac.Lang.X86.X86Backend.StackMapSection, SectionKind.ReadOnlyData) { Align = 8 };
        obj.Sections.Add(s);
        StackMapTable.Encode(functions, s.Bytes, StackMapLayout.X64);
        if (baseFunction is not null) s.Relocs.Add(new Relocation(StackMapTable.BaseOffset, baseFunction, 0, RelocKind.Rel32));
        // Each independently compiled object owns a complete table, named
        // for the image's directory of them (ManagedDirectory).
        obj.Symbols.Add(new Symbol { Name = Corsac.Lang.X86.X86Backend.StackMapStart, Section = s, Offset = 0, Size = s.Bytes.Count, Global = false });
        obj.Symbols.Add(new Symbol { Name = Corsac.Lang.X86.X86Backend.StackMapEnd, Section = s, Offset = s.Bytes.Count, Size = 0, Global = false });
        defined.Add(Corsac.Lang.X86.X86Backend.StackMapStart);
        defined.Add(Corsac.Lang.X86.X86Backend.StackMapEnd);
    }

    /// <summary>The store sequences' stubs, named as i386's (RuntimeAbi; CardMarks.FuseStores).</summary>
    public const string RefStoreStub = Corsac.Lang.Lto.RuntimeAbi.RefStore, CardStoreStub = Corsac.Lang.Lto.RuntimeAbi.CardStore;
    public const string RefExchangeStub = Corsac.Lang.Lto.RuntimeAbi.RefExchange, RefCompareExchangeStub = Corsac.Lang.Lto.RuntimeAbi.RefCompareExchange;
    private const string MarkingFlag = "s_Runtime_Marking_t$V$I32", CardTable = "s_Runtime_Cards_t$V$NUInt";

    /// <summary>
    /// THE STORE SEQUENCES IN LONG MODE (MOp.CallKeep; i386's in
    /// X86Backend), in the object that defines the runtime's barrier
    /// routines, when its units' stores are made through them: the image's
    /// one copy, laid out between __corsac_store_sequences and its end, a
    /// range its kernel answers no handshake inside. Between the Marking
    /// test and the store a thread so stopped would answer the snapshot and
    /// then store over a reference nobody reported; between the store and
    /// the card, leave the card unset for a minor cycle. Each runs to its RET.
    ///
    /// While a mark is under way the store is the runtime's
    /// (Runtime.WriteBarrierStore and its exchanges), called by the System V
    /// convention: every register the convention lets a callee destroy --
    /// nine general ones and all sixteen XMM -- saved round it, since the
    /// stub keeps every register but the one it answers in, and RSP 16-aligned
    /// at the call. K8 instructions only: CMPXCHG, XCHG, MOVDQU.
    /// </summary>
    private void EmitStoreSequences(Module module, Section text, ObjectFile obj, HashSet<string> defined)
    {
        if (PositionIndependent || !module.RuntimeHelpers.Contains(RefStoreStub)
            || !module.Functions.Any(f => f.Name == Corsac.Lang.Lto.RuntimeAbi.WriteBarrierStore)
            || !module.Functions.Any(f => f.Name == Corsac.Lang.Lto.RuntimeAbi.WriteBarrierExchange)
            || !module.Functions.Any(f => f.Name == Corsac.Lang.Lto.RuntimeAbi.WriteBarrierCompareExchange))
        {
            return;
        }
        Corsac.Lang.Ir.ChunkedBytes b = text.Bytes;
        int gap = (FunctionAlign - b.Count % FunctionAlign) % FunctionAlign;
        Encoder.Nops(b, gap);
        int start = b.Count;
        void Bytes(params byte[] bytes) => b.AddRange(bytes);
        // A RIP-relative disp32 to `symbol`, with `after` more bytes of the
        // instruction past it (an immediate).
        void Rip(string symbol, int after)
        {
            text.Relocs.Add(new Relocation(b.Count, symbol, -4 - after, RelocKind.Rel32));
            b.AddRange(new byte[4]);
        }
        void Call(string routine)
        {
            Bytes(0xE8);
            text.Relocs.Add(new Relocation(b.Count, routine, -4, RelocKind.Plt32));
            b.AddRange(new byte[4]);
        }
        // movdqu [rsp+16k], xmmK (store) or movdqu xmmK, [rsp+16k] (load).
        void Xmms(bool store)
        {
            for (int k = 0; k < 16; k++)
            {
                Bytes(0xF3);
                if (k >= 8) Bytes(0x44);
                Bytes(0x0F, store ? (byte)0x7F : (byte)0x6F, (byte)(0x84 | ((k & 7) << 3)), 0x24);
                b.AddRange(BitConverter.GetBytes(16 * k));
            }
        }
        // While Marking: `routine` does the whole operation. `saves` the
        // registers pushed (RAX among them where the stub answers nothing),
        // `args` the moves into RDI, RSI, RDX; RET with the routine's answer
        // in RAX. Else on past it. Every XMM register is saved, since the
        // routine may use any: 256 bytes, and 72 of pushes, on the stack of
        // a thread stopped mid-store -- only while a mark is under way.
        void Marked(byte[] saves, int pushes, byte[] args, string routine, byte[] restores)
        {
            Bytes(0x83, 0x3D); Rip(MarkingFlag, 1); Bytes(0x00);       // cmp dword [rip+Marking], 0
            Bytes(0x0F, 0x84); int skip = b.Count; Bytes(0, 0, 0, 0);    // je past
            Bytes(saves);
            // RSP is 8 past a 16 at entry; the saves (`pushes` of them) and
            // the XMM area keep it 16 at the call, 8 more where the saves are
            // an even count.
            int frame = 256 + ((8 + 8 * pushes) % 16 == 0 ? 0 : 8);
            Bytes(0x48, 0x81, 0xEC); b.AddRange(BitConverter.GetBytes(frame));   // sub rsp, frame
            Xmms(true);
            Bytes(args);
            Call(routine);
            Xmms(false);
            Bytes(0x48, 0x81, 0xC4); b.AddRange(BitConverter.GetBytes(frame));   // add rsp, frame
            Bytes(restores);
            Bytes(0xC3);                                                          // ret
            int past = b.Count - (skip + 4);
            byte[] rel = BitConverter.GetBytes(past);
            for (int k = 0; k < 4; k++) b[skip + k] = rel[k];
        }
        byte[] saveAll = { 0x50, 0x51, 0x52, 0x56, 0x57, 0x41, 0x50, 0x41, 0x51, 0x41, 0x52, 0x41, 0x53 };     // rax rcx rdx rsi rdi r8-r11
        byte[] restoreAll = { 0x41, 0x5B, 0x41, 0x5A, 0x41, 0x59, 0x41, 0x58, 0x5F, 0x5E, 0x5A, 0x59, 0x58 };
        byte[] saveButRax = { 0x51, 0x52, 0x56, 0x57, 0x41, 0x50, 0x41, 0x51, 0x41, 0x52, 0x41, 0x53 };
        byte[] restoreButRax = { 0x41, 0x5B, 0x41, 0x5A, 0x41, 0x59, 0x41, 0x58, 0x5F, 0x5E, 0x5A, 0x59 };

        // __corsac_refstore: slot RAX, value RDX; every register kept.
        Marked(saveAll, 9, new byte[] { 0x48, 0x89, 0xC7, 0x48, 0x89, 0xD6 }, Corsac.Lang.Lto.RuntimeAbi.WriteBarrierStore, restoreAll); // mov rdi, rax; mov rsi, rdx
        Bytes(0x48, 0x89, 0x10);                                                  // mov [rax], rdx
        CardOfRax();
        // __corsac_cardstore: the same from the store on, for a new block's.
        int cardStore = b.Count;
        Bytes(0x48, 0x89, 0x10);                                                  // mov [rax], rdx
        CardOfRax();
        // __corsac_refxchg: slot RAX, value RDX; the old reference in RAX.
        int exchange = b.Count;
        Marked(saveButRax, 8, new byte[] { 0x48, 0x89, 0xC7, 0x48, 0x89, 0xD6 }, Corsac.Lang.Lto.RuntimeAbi.WriteBarrierExchange, restoreButRax);
        Bytes(0x51, 0x48, 0x89, 0xC1, 0x48, 0x89, 0xD0, 0x48, 0x87, 0x01);      // push rcx; mov rcx, rax; mov rax, rdx; xchg [rcx], rax
        CardOfRcx(popRcx: true);
        // __corsac_refcas: slot RCX, value RDX, expected RAX; what the slot held in RAX.
        int compareExchange = b.Count;
        Marked(saveButRax, 8, new byte[] { 0x48, 0x89, 0xCF, 0x48, 0x89, 0xD6, 0x48, 0x89, 0xC2 },   // mov rdi, rcx; mov rsi, rdx; mov rdx, rax
            Corsac.Lang.Lto.RuntimeAbi.WriteBarrierCompareExchange, restoreButRax);
        Bytes(0xF0, 0x48, 0x0F, 0xB1, 0x11);                                      // lock cmpxchg [rcx], rdx
        CardOfRcx(popRcx: false);
        int end = b.Count;

        // The card of the slot in RAX, every register kept, and RET.
        void CardOfRax()
        {
            Bytes(0x51, 0x48, 0x8B, 0x0D); Rip(CardTable, 0);                     // push rcx; mov rcx, [rip+Cards]
            Bytes(0x48, 0x85, 0xC9, 0x74, 0x0A,                                   // test rcx, rcx; jz done
                  0x50, 0x48, 0xC1, 0xE8, 0x0A, 0xC6, 0x04, 0x01, 0x01, 0x58,     // push rax; shr rax, 10; mov byte [rcx+rax], 1; pop rax
                  0x59, 0xC3);                                                    // done: pop rcx; ret
        }
        // The card of the slot in RCX, RDX kept round it; RCX popped after
        // where the stub pushed it first; RET.
        void CardOfRcx(bool popRcx)
        {
            Bytes(0x52, 0x48, 0x8B, 0x15); Rip(CardTable, 0);                     // push rdx; mov rdx, [rip+Cards]
            Bytes(0x48, 0x85, 0xD2, 0x74, 0x0A,                                   // test rdx, rdx; jz done
                  0x51, 0x48, 0xC1, 0xE9, 0x0A, 0xC6, 0x04, 0x0A, 0x01, 0x59,     // push rcx; shr rcx, 10; mov byte [rdx+rcx], 1; pop rcx
                  0x5A);                                                          // done: pop rdx
            if (popRcx) Bytes(0x59);                                              // pop rcx
            Bytes(0xC3);
        }

        obj.Symbols.Add(new Symbol { Name = Corsac.Lang.Lto.RuntimeAbi.SequencesStart, Section = text, Offset = start, Size = end - start, Global = true });
        obj.Symbols.Add(new Symbol { Name = RefStoreStub, Section = text, Offset = start, Size = cardStore - start, IsFunction = true, Global = true });
        obj.Symbols.Add(new Symbol { Name = CardStoreStub, Section = text, Offset = cardStore, Size = exchange - cardStore, IsFunction = true, Global = true });
        obj.Symbols.Add(new Symbol { Name = RefExchangeStub, Section = text, Offset = exchange, Size = compareExchange - exchange, IsFunction = true, Global = true });
        obj.Symbols.Add(new Symbol { Name = RefCompareExchangeStub, Section = text, Offset = compareExchange, Size = end - compareExchange, IsFunction = true, Global = true });
        obj.Symbols.Add(new Symbol { Name = Corsac.Lang.Lto.RuntimeAbi.SequencesEnd, Section = text, Offset = end, Size = 0, Global = true });
        foreach (string stub in new[] { Corsac.Lang.Lto.RuntimeAbi.SequencesStart, RefStoreStub, CardStoreStub, RefExchangeStub, RefCompareExchangeStub, Corsac.Lang.Lto.RuntimeAbi.SequencesEnd })
            defined.Add(stub);
    }

    private static void Pad(Section s, int align, byte fill) => Corsac.Lang.Backends.ObjectData.Pad(s, align, fill);
}
