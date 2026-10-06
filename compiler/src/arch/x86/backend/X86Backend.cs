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
    /// <summary>
    /// Whether a function's IR is let go once its code is placed, for a
    /// caller that reads nothing of the module's bodies after Generate
    /// (Driver.Compile). The whole module's IR lived through code generation
    /// beside the machine code and the link's copy of it, and that was the
    /// largest a compile ever got: a unit of this compiler peaked there.
    /// </summary>
    public bool ReleaseBodies { get; set; }
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

    /// <summary>Writable data with no references in it: outside the collector's roots.</summary>
    public const string NumbersSection = ".data.corsac.numbers";
    public const string StackMapStart = "__corsac_stackmaps";

    /// <summary>Each object's card-marking stub, and the table it reads (Runtime.Cards).</summary>
    public const string CardStub = "__corsac_cardmark", CardTable = "s_Runtime_Cards";

    /// <summary>Each object's barrier stub, and the runtime routine it calls (Runtime.WriteBarrier).</summary>
    public const string BarrierStub = "__corsac_barrier", BarrierRoutine = Corsac.Lang.Lto.RuntimeAbi.WriteBarrier;
    public const string StackMapEnd = "__corsac_stackmaps_end";

    /// <summary>The store sequences' stubs and the range they lie in (RuntimeAbi.RefStore; CardMarks.FuseStores).</summary>
    public const string RefStoreStub = Corsac.Lang.Lto.RuntimeAbi.RefStore, CardStoreStub = Corsac.Lang.Lto.RuntimeAbi.CardStore;
    public const string RefExchangeStub = Corsac.Lang.Lto.RuntimeAbi.RefExchange, RefCompareExchangeStub = Corsac.Lang.Lto.RuntimeAbi.RefCompareExchange;
    public const string MarkingFlag = "s_Runtime_Marking";
    /// <summary>What the store sequences call while Marking is set: the report, the store and the card in one (Runtime.WriteBarrierStore).</summary>
    public const string BarrierStoreRoutine = Corsac.Lang.Lto.RuntimeAbi.WriteBarrierStore,
        BarrierExchangeRoutine = Corsac.Lang.Lto.RuntimeAbi.WriteBarrierExchange,
        BarrierCompareExchangeRoutine = Corsac.Lang.Lto.RuntimeAbi.WriteBarrierCompareExchange;

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
        // Constants naming another image, which the loader writes at every
        // start: kept together by the linker (Layout.RelocatedImports).
        Section? importConstants = null;
        Section ImportConstants()
        {
            if (importConstants is null)
            {
                importConstants = new Section(".data.rel.ro.import", SectionKind.Data);
                obj.Sections.Add(importConstants);
            }
            return importConstants;
        }
        Section bss = new(".bss", SectionKind.Uninitialised);
        // Writable data that holds no reference (DataItem.NoReferences), in a
        // section the linker places outside the statics read as roots.
        Section? numbers = null;
        Section Numbers()
        {
            if (numbers is null)
            {
                numbers = new Section(NumbersSection, SectionKind.Data);
                obj.Sections.Add(numbers);
            }
            return numbers;
        }
        obj.Sections.Add(text);
        obj.Sections.Add(rodata);
        obj.Sections.Add(data);
        obj.Sections.Add(bss);
        obj.Sections.Add(relocatedConstants);

        HashSet<string> defined = new(StringComparer.Ordinal);
        Encoder encoder = new(text);
        List<FrameTable.Entry> frames = new();
        FunctionSizes.Clear();
        // THE STACK MAPS AS THE TABLE WILL HOLD THEM, a record a function and
        // a site per call, made as each function is encoded. They were kept
        // as the allocator left them -- per site a tuple, the Safepoint and
        // its list of offsets -- for every call of the unit, then turned into
        // these records only once the last function was placed, with both
        // alive together: two copies of the unit's largest table after its
        // code. Now a site is its return, its registers and its words, and
        // the allocator's objects go with the function.
        List<StackMapTable.Function> mapFunctions = new();
        string? mapBase = null;
        int mapBaseAt = 0;
        bool cardStub = false, barrierStub = false;

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
        // What this module defines: a constant naming anything else names
        // another image, or another unit of this one (ImportConstants).
        HashSet<string> moduleDefines = new(StringComparer.Ordinal);
        foreach (Function f in module.Functions) moduleDefines.Add(f.Name);
        foreach (DataItem d in module.Data) moduleDefines.Add(d.Name);

        // THE SYMBOL QUESTIONS, AS DELEGATES MADE ONCE: written at each call
        // below, a local function became a new delegate for every function
        // compiled.
        Func<string, bool>? askPrivate = PositionIndependent ? IsPrivate : null;
        Func<string, bool> askDefined = IsDefined;
        Func<string, bool>? askImported = imported.Count == 0 ? null : IsImported;
        // The calls a link summary may name, refilled for each function.
        HashSet<string> eligible = new(StringComparer.Ordinal);

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
                m = Compile(f, errors, askPrivate, askDefined, askImported);
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
                            // Bigger than the whole budget: compiled alone, as
                            // slowly as it must be, never refused. The batch
                            // sizes the work, not the code.
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
                                compiled[item] = Compile(FunctionLoader?.Invoke(first + item) ?? module.Functions[first + item], localErrors,
                                    askPrivate, askDefined, askImported);
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
            cardStub |= m.UsesCardStub;
            barrierStub |= m.UsesBarrierStub;
            f = m.Source;
            // The gap before a function is never executed, but it is filled
            // with real no-ops rather than zeros so a disassembly reads cleanly.
            int gap = (FunctionAlign - text.Bytes.Count % FunctionAlign) % FunctionAlign;
            Encoder.Nops(text.Bytes, gap);
            int start = text.Bytes.Count;
            int size = encoder.Encode(m);
            if (EmitLinkSummary && !PositionIndependent)
            {
                eligible.Clear();
                foreach (Corsac.Lang.Ir.Block b in f.Blocks)
                foreach (Instr i in b.Instrs)
                    if (i.Op == Opcode.Call && i.Operands.Count == 0 && i.Dest?.Type == IrType.I32 && i.Callee is not null)
                        eligible.Add(i.Callee!);
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
                Corsac.Lang.Opt.LinkSummary.AddConstantReturns(f, obj, summary);

            // WHAT THIS FUNCTION IS, for a fault to read back. Recorded per
            // function as it is encoded, because this is the one moment the
            // name, the source and the final byte offsets are all in hand.
            frames.Add(new FrameTable.Entry(
                f.Name, f.Display ?? f.Name, f.SourceFile ?? "", start, size,
                new List<(int, int)>(encoder.Lines)));

            if (StackMaps)
            {
                // A record only for a function with a call, from the first
                // such function's start (the table's base), as EmitStackMaps
                // made them from the sites.
                StackMapTable.Function? record = null;
                foreach ((MInstr call, int ret) in encoder.CallSites)
                {
                    if (record is null)
                    {
                        if (m.Frame.Size > 0xFFFFFF) throw new InvalidOperationException("a frame over 16 MB has no stack map");
                        if (mapBase is null) { mapBase = f.Name; mapBaseAt = start; }
                        record = new StackMapTable.Function
                        {
                            Start = start - mapBaseAt, FrameSize = m.Frame.Size, Saved = SavedMask(m), Objects = StackMapTable.Words(ObjectWords(m)),
                        };
                        mapFunctions.Add(record);
                    }
                    if (ret > 0x7FFFFF) throw new InvalidOperationException("a function over 8 MB has no stack map");
                    Safepoint? map = m.Safepoints.GetValueOrDefault(call);
                    // No live spill slot is one shared empty list of words.
                    record.Sites.Add(new StackMapTable.Site(ret, map is null, map?.Registers ?? 0,
                        map is null || map.SlotOffsets.Count == 0 ? Array.Empty<int>() : StackMapTable.Words(map.SlotOffsets)));
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
                    for (int z = 0; z < 4; z++) tables.Bytes.Add(0);
                }
            }
            encoder.ReleaseFunction();
            if (ReleaseBodies)
            {
                f.Blocks.Clear();
                f.Blocks.TrimExcess();
                // AND WHAT HANGS OFF IT BESIDE ITS BLOCKS: its parameters and
                // frame slots, its state machine's frame, and the index an
                // analysis kept (Escape.StampIndex), which holds registers of
                // the body just let go. Nothing past this point reads a
                // placed function but its name.
                f.Params.Clear();
                f.Params.TrimExcess();
                f.Slots.Clear();
                f.Slots.TrimExcess();
                f.Async = null;
                f.AnalysisIndex = null;
            }
        }

        // THE FRAME TABLE, once every function's bytes are placed. It goes in
        // .rodata, where it is mapped and readable, rather than a section of
        // its own that every linker script would have to learn about -- and so
        // a flat freestanding image carries it exactly as an ELF does.
        if (frames.Count > 0)
        {
            // It holds one address, so in a shared object it holds a
            // relocation, and a relocation may not land in a page the loader
            // has to keep read-only. Jump tables move for the same reason.
            Section frameSection = PositionIndependent ? relocatedConstants : rodata;
            Pad(frameSection, 4, 0);
            int at = frameSection.Bytes.Count;
            // Built straight into the section, never one array of its own.
            FrameTable.Build(frameSection.Bytes, frames, out int fixup, out string baseSymbol);
            obj.Symbols.Add(new Symbol
            {
                Name = FrameTable.Symbol, Section = frameSection, Offset = at, Size = frameSection.Bytes.Count - at, Global = false,
            });
            defined.Add(FrameTable.Symbol);
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
            bool namesImport = loaderWrites && d.Relocs.Any(r => !moduleDefines.Contains(r.Symbol));
            Section s = d.Zero ? bss : d.ReadOnly ? (loaderWrites ? (namesImport && PositionIndependent ? ImportConstants() : relocatedConstants) : rodata) : d.NoReferences ? Numbers() : data;
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
            // A PLACED ITEM'S BYTES ARE THE SECTION'S NOW, and were held twice
            // over to the end of the compile: the module's copy and the
            // section's. A zero-filled one held an array of zeros as large as
            // the storage it stands for. Let go when the caller reads nothing
            // of the module after this (ReleaseBodies), as a function is.
            if (ReleaseBodies)
            {
                d.Bytes = Array.Empty<byte>();
                d.Relocs.Clear();
                d.Relocs.TrimExcess();
            }
        }

        if (cardStub)
        {
            // THE CARD STUB (MachineIntrinsics.CardMark): the slot in EAX, every
            // register kept. No stack map and no frame entry: it calls nothing,
            // so no walk ever finds it on a stack.
            int gap = (FunctionAlign - text.Bytes.Count % FunctionAlign) % FunctionAlign;
            Encoder.Nops(text.Bytes, gap);
            int at = text.Bytes.Count;
            text.Bytes.AddRange(new byte[] { 0x51, 0x8B, 0x0D });                  // push ecx; mov ecx, [Cards]
            text.Relocs.Add(new Relocation(text.Bytes.Count, CardTable, 0, RelocKind.Abs32));
            text.Bytes.AddRange(new byte[4]);
            text.Bytes.AddRange(new byte[]
            {
                0x85, 0xC9,                 // test ecx, ecx
                0x74, 0x09,                 // jz done
                0x50,                       // push eax
                0xC1, 0xE8, 0x0A,           // shr eax, 10
                0xC6, 0x04, 0x01, 0x01,     // mov byte [ecx+eax], 1
                0x58,                       // pop eax
                0x59,                       // done: pop ecx
                0xC3,                       // ret
            });
            obj.Symbols.Add(new Symbol { Name = CardStub, Section = text, Offset = at, Size = text.Bytes.Count - at, IsFunction = true, Global = false });
            defined.Add(CardStub);
        }

        if (barrierStub)
        {
            // THE BARRIER STUB (MachineIntrinsics.Barrier): slot in EAX, value
            // in EDX, every register kept; Runtime.WriteBarrier takes both as
            // machine words. A collection while it runs finds no map for the frames
            // above it and reads them whole, which is correct and conservative.
            int gap = (FunctionAlign - text.Bytes.Count % FunctionAlign) % FunctionAlign;
            Encoder.Nops(text.Bytes, gap);
            int at = text.Bytes.Count;
            text.Bytes.AddRange(new byte[]
            {
                0x51, 0x52, 0x50,           // push ecx; push edx; push eax
                0x52,                       // push edx              (value)
                0x50,                       // push eax              (slot)
                0xE8,                       // call Runtime.WriteBarrier
            });
            text.Relocs.Add(new Relocation(text.Bytes.Count, BarrierRoutine, -4, RelocKind.Rel32));
            text.Bytes.AddRange(new byte[4]);
            text.Bytes.AddRange(new byte[]
            {
                0x83, 0xC4, 0x08,           // add esp, 8
                0x58, 0x5A, 0x59,           // pop eax; pop edx; pop ecx
                0xC3,                       // ret
            });
            obj.Symbols.Add(new Symbol { Name = BarrierStub, Section = text, Offset = at, Size = text.Bytes.Count - at, IsFunction = true, Global = false });
            defined.Add(BarrierStub);
        }

        // THE STORE SEQUENCES (MachineIntrinsics.RefStore, CardStore,
        // RefExchange, RefCompareExchange), in the object that defines the
        // runtime's barriers, when its units' stores are made through them:
        // the image's one copy, global, which its other units and its modules
        // call, laid out together between two symbols its kernel reads
        // (CORSAC's ring-1 header). A thread of a ring-1 kernel is sent to its
        // collector's handshake at whatever instruction a trap finds it on
        // (Ring1Kernel.SendToAnswer), and ring 0 sends none whose EIP lies in
        // here: between the Marking test and the store a thread so sent would
        // answer the snapshot and then store over a reference nobody
        // reported, and between the store and the card leave the card unset
        // for a minor cycle. Each runs to its RET first.
        // WHILE MARKING, THE STORE IS THE RUNTIME'S: Runtime.WriteBarrierStore
        // (and its exchange and compare-exchange) reports, stores and marks
        // the card inside the thread's InAlloc, which no handshake is sent
        // into either, and the stub returns what it answers. A report made in
        // a call that came back here to store left the call's epilogue -- after
        // InAlloc was let go, outside this range -- where a thread could be
        // sent to the next cycle's snapshot between its report to the last
        // one and its store.
        if (!PositionIndependent && module.RuntimeHelpers.Contains(RefStoreStub)
            && module.Functions.Any(f => f.Name == BarrierRoutine)
            && module.Functions.Any(f => f.Name == BarrierStoreRoutine)
            && module.Functions.Any(f => f.Name == BarrierExchangeRoutine)
            && module.Functions.Any(f => f.Name == BarrierCompareExchangeRoutine))
        {
            int gap = (FunctionAlign - text.Bytes.Count % FunctionAlign) % FunctionAlign;
            Encoder.Nops(text.Bytes, gap);
            int start = text.Bytes.Count;
            void Bytes(params byte[] bytes) => text.Bytes.AddRange(bytes);
            void Address(string symbol, RelocKind kind, int addend)
            {
                text.Relocs.Add(new Relocation(text.Bytes.Count, symbol, addend, kind));
                text.Bytes.AddRange(new byte[4]);
            }
            // While a mark is under way, `routine` does the whole operation:
            // `pushes` puts its arguments (first last), `cleanup` is how many
            // bytes they took, and `restore` the registers popped after; it
            // RETs with the routine's answer in EAX. Else on past it.
            void Marked(byte[] saves, byte[] pushes, string routine, byte cleanup, byte[] restore)
            {
                Bytes(0x83, 0x3D); Address(MarkingFlag, RelocKind.Abs32, 0); Bytes(0x00);  // cmp dword [Marking], 0
                int skip = saves.Length + pushes.Length + 5 + 3 + restore.Length + 1;
                Bytes(0x74, (byte)skip);                                                   // je past the call
                Bytes(saves); Bytes(pushes); Bytes(0xE8);                                  // push ...; call routine
                Address(routine, RelocKind.Rel32, -4);
                Bytes(0x83, 0xC4, cleanup); Bytes(restore); Bytes(0xC3);                   // add esp, n; pop ...; ret
            }
            // The card of the slot in EAX, every register kept, and RET.
            void CardOfEax()
            {
                Bytes(0x51, 0x8B, 0x0D); Address(CardTable, RelocKind.Abs32, 0);           // push ecx; mov ecx, [Cards]
                Bytes(0x85, 0xC9, 0x74, 0x09,                                              // test ecx, ecx; jz done
                      0x50, 0xC1, 0xE8, 0x0A, 0xC6, 0x04, 0x01, 0x01, 0x58,                // push eax; shr eax, 10; mov byte [ecx+eax], 1; pop eax
                      0x59, 0xC3);                                                         // done: pop ecx; ret
            }
            // __corsac_refstore: slot EAX, value EDX; every register kept.
            // Marking: WriteBarrierStore(slot, value) with EAX, ECX and EDX
            // saved round it.
            Marked([0x51, 0x52, 0x50], [0x52, 0x50], BarrierStoreRoutine, 0x08, [0x58, 0x5A, 0x59]);
            Bytes(0x89, 0x10);                                                             // mov [eax], edx
            CardOfEax();
            // __corsac_cardstore: the same from the store on, for a store
            // that overwrites nothing (a new block's, Lowering.StoreNew).
            int cardStore = text.Bytes.Count;
            Bytes(0x89, 0x10);                                                             // mov [eax], edx
            CardOfEax();
            // __corsac_refxchg: slot EAX, value EDX; the old reference in EAX,
            // every other register kept. Marking: WriteBarrierExchange(slot,
            // value), its answer the old reference, ECX and EDX saved round it.
            int exchange = text.Bytes.Count;
            Marked([0x51, 0x52], [0x52, 0x50], BarrierExchangeRoutine, 0x08, [0x5A, 0x59]);
            Bytes(0x51, 0x89, 0xC1, 0x89, 0xD0, 0x87, 0x01);                               // push ecx; mov ecx, eax; mov eax, edx; xchg [ecx], eax
            Bytes(0x52, 0x8B, 0x15); Address(CardTable, RelocKind.Abs32, 0);               // push edx; mov edx, [Cards]
            Bytes(0x85, 0xD2, 0x74, 0x09,                                                  // test edx, edx; jz done
                  0x51, 0xC1, 0xE9, 0x0A, 0xC6, 0x04, 0x0A, 0x01, 0x59,                    // push ecx; shr ecx, 10; mov byte [edx+ecx], 1; pop ecx
                  0x5A, 0x59, 0xC3);                                                       // done: pop edx; pop ecx; ret
            // __corsac_refcas: slot ECX, value EDX, expected EAX; what the
            // slot held in EAX, every other register kept. Marking:
            // WriteBarrierCompareExchange(slot, value, expected), ECX and EDX
            // saved round it.
            int compareExchange = text.Bytes.Count;
            Marked([0x51, 0x52], [0x50, 0x52, 0x51], BarrierCompareExchangeRoutine, 0x0C, [0x5A, 0x59]);
            Bytes(0xF0, 0x0F, 0xB1, 0x11);                                                 // lock cmpxchg [ecx], edx
            Bytes(0x52, 0x8B, 0x15); Address(CardTable, RelocKind.Abs32, 0);               // push edx; mov edx, [Cards]
            Bytes(0x85, 0xD2, 0x74, 0x09,                                                  // test edx, edx; jz done
                  0x51, 0xC1, 0xE9, 0x0A, 0xC6, 0x04, 0x0A, 0x01, 0x59,                    // push ecx; shr ecx, 10; mov byte [edx+ecx], 1; pop ecx
                  0x5A, 0xC3);                                                             // done: pop edx; ret
            int end = text.Bytes.Count;
            obj.Symbols.Add(new Symbol { Name = Corsac.Lang.Lto.RuntimeAbi.SequencesStart, Section = text, Offset = start, Size = end - start, Global = true });
            obj.Symbols.Add(new Symbol { Name = RefStoreStub, Section = text, Offset = start, Size = cardStore - start, IsFunction = true, Global = true });
            obj.Symbols.Add(new Symbol { Name = CardStoreStub, Section = text, Offset = cardStore, Size = exchange - cardStore, IsFunction = true, Global = true });
            obj.Symbols.Add(new Symbol { Name = RefExchangeStub, Section = text, Offset = exchange, Size = compareExchange - exchange, IsFunction = true, Global = true });
            obj.Symbols.Add(new Symbol { Name = RefCompareExchangeStub, Section = text, Offset = compareExchange, Size = end - compareExchange, IsFunction = true, Global = true });
            obj.Symbols.Add(new Symbol { Name = Corsac.Lang.Lto.RuntimeAbi.SequencesEnd, Section = text, Offset = end, Size = 0, Global = true });
            foreach (string stub in new[] { Corsac.Lang.Lto.RuntimeAbi.SequencesStart, RefStoreStub, CardStoreStub, RefExchangeStub, RefCompareExchangeStub, Corsac.Lang.Lto.RuntimeAbi.SequencesEnd })
                defined.Add(stub);
        }

        if (StackMaps)
        {
            EmitStackMaps(obj, mapFunctions, mapBase, defined, PositionIndependent);
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
    private MFunction? Compile(Function f, List<string> errors, Func<string, bool>? isPrivate = null, Func<string, bool>? isDefined = null, Func<string, bool>? isImported = null)
    {
        int before = errors.Count;
        // Frame slots whose lives do not meet share their bytes (SlotShare).
        Corsac.Lang.Opt.SlotShare.Run(f);
        MFunction m = Selector.Run(f, errors, AutomaticPacked, PositionIndependent);
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
            AddressFold.Run(m);
            Allocator.Run(m);
            Layout.Run(m);
            Peephole.Run(m);
            FrameCompact.Run(m);
        }
        catch (InvalidOperationException e)
        {
            errors.Add(e.Message);
            return null;
        }
        return m;
    }

    /// <summary>
    /// The callee-saved registers a function's prologue pushes, by hardware
    /// number: after `push ebp; mov ebp, esp; sub esp, FrameSize` they are
    /// pushed in the order EBX, ESI, EDI, so the k-th of them present is at
    /// EBP - FrameSize - 4(k+1). The collector reads them to find where a
    /// caller's register value was kept, and so which saved words are values
    /// some frame still holds live and which are stale copies.
    /// </summary>
    private static uint SavedMask(MFunction m)
    {
        uint mask = 0;
        foreach (Gpr g in m.SavedRegs)
        {
            mask |= 1u << (int)g;
        }
        return mask;
    }

    /// <summary>
    /// Every word of a function's IR frame slots, as EBP offsets: what the
    /// collector reads as it is, beside the live spill slots a map names.
    /// </summary>
    private static List<int> ObjectWords(MFunction m)
    {
        // Gathered, sorted and made unique in one list: what a SortedSet
        // copied out to a list gave, without the set's node per word.
        List<int> words = new();
        foreach ((int offset, int bytes) in m.Frame.SlotRanges())
        {
            for (int at = offset & ~3; at < offset + bytes; at += 4)
            {
                if (at < 0) words.Add(at);
            }
        }
        words.Sort();
        int unique = 0;
        for (int k = 0; k < words.Count; k++)
        {
            if (unique == 0 || words[k] != words[unique - 1]) words[unique++] = words[k];
        }
        words.RemoveRange(unique, words.Count - unique);
        return words;
    }

    /// <summary>
    /// The stack-map table: one per object, read by the collector to know,
    /// at each call a frame is stopped in, which of its words and saved
    /// registers hold live references.
    ///
    /// VERSION 6, VARINTS AND SHARED BITMAPS: the layout is StackMapTable's,
    /// in the linker's object model, where it is written down in full; the
    /// link decodes and re-encodes it with the same code when it cuts
    /// duplicate bodies, and runtime/src/core/gc.cor (MapSite) reads it. In
    /// short: a header, a checkpoint every thirty-two call sites for the
    /// binary search, then per function its start, frame size, saved
    /// registers and frame-slot bitmap, and per call site its return address
    /// from the one before and its live registers and spill-slot bitmap, all
    /// as varints; a bitmap of up to sixteen words is held inline, a larger
    /// one once in a pool, the most used first.
    ///
    /// ONE RELOCATION, NOT ONE PER ENTRY. Every function of this object is in
    /// its one text section, so the distance from the first function with a
    /// call site to any call is known here, and is what is written; a reader
    /// adds the base, exactly as the frame table's reader does. A shared
    /// object then has one loader-written word in the table, not one a call.
    /// </summary>
    private static void EmitStackMaps(ObjectFile obj, List<StackMapTable.Function> functions, string? baseFunction, HashSet<string> defined, bool pic)
    {
        // The base is a relocation, so in a shared object the page holding
        // the header is written by the loader; writable in that mode, as the
        // frame table and the jump tables are.
        Section s = new(pic ? ".data.rel.ro" + StackMapSection : StackMapSection,
            pic ? SectionKind.Data : SectionKind.ReadOnlyData) { Align = 4 };
        obj.Sections.Add(s);

        // The records were made in code order as each function was encoded
        // (Generate), a function's sites together, starts from the first
        // function with a call: the base the relocation names.
        StackMapTable.Encode(functions, s.Bytes);
        if (baseFunction is not null) s.Relocs.Add(new Relocation(StackMapTable.BaseOffset, baseFunction, 0, RelocKind.Abs32));

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
