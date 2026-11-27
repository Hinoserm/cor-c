#nullable enable
using System.Text;
using System.Threading.Tasks;
using Corsac.Lang.Frames;
using Corsac.Lang.Ir;
using Corsac.Lang.Lto;

namespace Corsac.Lang.X86;

/// <summary>
/// The 486 code generator: IR module in, relocatable object out.
///
/// Per function: instruction selection (Select.cs), register allocation
/// (RegAlloc.cs), encoding (Encoder.cs). The module's data items are placed
/// in .rodata, .data or .bss by their flags (a string object with the image's
/// objects, ObjectData.Place), and every name the code or data
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
    /// The least alignment of each function's first byte: four by default,
    /// which --cpu may raise (CpuModel.FunctionAlign: sixteen on the K6, as
    /// AMD 21924 asks; Intel's 486 and Pentium manuals ask for none, and
    /// padding there is bytes that could have been code). Loop heads are not
    /// aligned: padded to their section offsets, they needed the section
    /// aligned beyond its functions, and a copy of a generic body the link
    /// takes out (DuplicateCutter) must leave the section's alignment -- with
    /// the section at 16 and functions at 4, it nearly never could, and the
    /// kernel kept 16,800 copies, a fifth more code.
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

    /// <summary>
    /// Done to each function just before it is selected, on whichever worker
    /// selects it (the allocation put in place, AllocatorFastPaths.AtCodegen).
    /// </summary>
    public Action<Function>? Prepare { get; set; }
    /// <summary>The thread owns its x87/MMX state (Lowering.OwnsFpuState: Linux, Windows); bare-metal code must not borrow it implicitly.</summary>
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
    /// A Windows image (docs/WIN32-TARGET.md): the thread block is read from
    /// the TLS slot the runtime took, in the TEB, never through GS.
    /// </summary>
    public bool WindowsTls { get; set; }

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
    /// A safepoint poll on the back edges of every loop that may not end by
    /// itself (BackEdgePolls), put in the function's IR before it is
    /// selected: an image with a collector and an operating system to run
    /// threads, where a thread that neither allocates nor blocks would never
    /// answer a round. A call is already a safepoint by virtue of having a map.
    /// </summary>
    public bool SafepointPolls { get; set; }

    /// <summary>The section stack maps go in, and the symbols the runtime finds them by.</summary>
    public const string StackMapSection = ".corsac.stackmaps";

    /// <summary>Writable data with no references in it: outside the collector's roots.</summary>
    public const string NumbersSection = Corsac.Lang.Backends.ObjectData.NumbersSection;
    public const string StackMapStart = "__corsac_stackmaps";

    /// <summary>Each object's card-marking stub, and the table it reads (Runtime.Cards).</summary>
    public const string CardStub = "__corsac_cardmark", CardTable = "s_Runtime_Cards_t$V$NUInt";

    /// <summary>Each object's barrier stub, and the runtime routine it calls (Runtime.WriteBarrier): i386's written here, long mode's by X64Backend.EmitBarrierStub, under the one name.</summary>
    public const string BarrierStub = "__corsac_barrier", BarrierRoutine = Corsac.Lang.Lto.RuntimeAbi.WriteBarrier;
    public const string StackMapEnd = "__corsac_stackmaps_end";

    /// <summary>The store sequences' stubs and the range they lie in (RuntimeAbi.RefStore; CardMarks.FuseStores).</summary>
    public const string RefStoreStub = Corsac.Lang.Lto.RuntimeAbi.RefStore, CardStoreStub = Corsac.Lang.Lto.RuntimeAbi.CardStore;
    public const string RefExchangeStub = Corsac.Lang.Lto.RuntimeAbi.RefExchange, RefCompareExchangeStub = Corsac.Lang.Lto.RuntimeAbi.RefCompareExchange;
    public const string MarkingFlag = "s_Runtime_Marking_t$V$I32";
    /// <summary>The runtime's 386 lock word (Runtime.Atomics386), which the 386 form of __corsac_refcas takes where it may not turn interrupts off.</summary>
    public const string Atomics386Lock = "s_Runtime_Atomics386_t$V$I32";
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

    private string? _streamCopy, _streamFill;

    /// <summary>--opt-size: the layout's own padding left out (Encoder.LoopAlign).</summary>
    public bool OptimizeSize { get; set; }

    /// <summary>--count-spills: each block counts its spill slots' reads and writes as it runs (SpillCount).</summary>
    public bool CountSpills { get; set; }

    /// <summary>--count-spills-map: where each counted block is listed (SpillCounter), or null.</summary>
    public string? SpillMap { get; set; }

    /// <summary>
    /// On by default on bare metal (Driver: --x87-outside-blocks takes it
    /// away, --x87-in-blocks-only says it again): bare-metal code of the
    /// program's own -- not the system library's (Function.SystemCode),
    /// whose float routines run where their callers keep the state, nor the
    /// compiler's own helpers -- may use the x87 only where the state is
    /// kept: the compiler's own block around a method that needs it
    /// (Binder.PackedStateOf), which saves the interrupted thread's state
    /// first, or under its callers' only (an $inside copy); outside every
    /// one -- a lambda's, an async body's -- it is an error the link makes, which alone sees every caller (Lto.X87Notes:
    /// each function's facts, Selector.X87Fact, in the object). Off
    /// (--x87-outside-blocks), the unit's own float code is judged nowhere,
    /// and its calls still are: what it calls outside a block runs outside.
    /// </summary>
    public bool X87InBlocksOnly { get; set; }

    /// <summary>
    /// Whether the object carries those facts (Lto.X87Notes): every
    /// bare-metal unit's, but not a unit the link makes again from its IR
    /// (UnitBackend), which judged the units' own facts before.
    /// </summary>
    public bool KeepsX87Facts { get; set; } = true;

    /// <summary>
    /// The facts Selector.X87Fact makes, from every worker at once: each
    /// function's, and every symbol whose address the unit's code takes.
    /// `judged`: whether the unit keeps the rule (X87InBlocksOnly); where it
    /// does not, no float code of its own is said, and its calls are.
    /// </summary>
    internal sealed class X87Facts(bool judged)
    {
        private readonly List<Corsac.Lang.Lto.X87Notes.Fact> _facts = new();
        private readonly HashSet<string> _taken = new(StringComparer.Ordinal);

        public void Add(Function f, bool floats)
        {
            floats &= judged;
            HashSet<string> taken = new(StringComparer.Ordinal);
            Corsac.Lang.Lto.X87Notes.Fact fact = Selector.X87Fact(f, floats, taken);
            lock (_facts)
            {
                _facts.Add(fact);
                _taken.UnionWith(taken);
            }
        }

        /// <summary>
        /// Into the object: every function's facts, and a fact of its address
        /// taken for every function a symbol names that the code or the data
        /// (a delegate's target, a type's slots) refers to otherwise than by a
        /// direct call -- the unit's data items' names left out, which are no
        /// function's.
        /// </summary>
        public void Attach(ObjectFile obj, Module module)
        {
            HashSet<string> data = new(module.Data.Select(d => d.Name), StringComparer.Ordinal);
            foreach (DataItem d in module.Data)
                foreach (DataReloc r in d.Relocs) _taken.Add(r.Symbol);
            List<Corsac.Lang.Lto.X87Notes.Fact> all = new(_facts);
            foreach (string name in _taken.Where(name => !data.Contains(name)).OrderBy(name => name, StringComparer.Ordinal))
                all.Add(new(name, "", "", -1, true, Array.Empty<Corsac.Lang.Lto.X87Notes.Call>()));
            Corsac.Lang.Lto.X87Notes.Attach(obj, all);
        }
    }

    private X87Facts? _x87;

    /// <summary>
    /// THE FLOAT CONSTANTS' POOL: a float's or a double's bits, read-only, one
    /// copy an object, named by its width and its bits and local to the object
    /// (Select.Sse's FloatConstant reads them). Made after the code, for every
    /// name of it the code's relocations hold.
    /// </summary>
    public const string FloatPoolPrefix = "__corsac_fpool";

    public static string FloatPoolName(long bits, int width)
        => width == 4 ? $"{FloatPoolPrefix}4_{(uint)bits:x8}" : $"{FloatPoolPrefix}8_{(ulong)bits:x16}";

    public ObjectFile Generate(Module module, List<string> errors)
    {
        if (Workers < 1 || Workers > 64) throw new ArgumentOutOfRangeException(nameof(Workers));
        // i386, for the machine IR the two backends share (X86Mode): said
        // by whichever backend generates, as Target.Current is said once a
        // process -- one target a process, both of them.
        X86Mode.Long = false;
        // The runtime's streaming copy and fill, where the module has them to
        // call (Module.RuntimeHelpers: lowering kept them) and its code owns
        // the x87 state (Selector.StreamedOr).
        FindStreams(module);
        ObjectFile obj = new();
        (Target.X86Profile.Contract with { AutomaticPacked = AutomaticPacked }).Attach(obj);
        // FACTS FROM EVERY FREESTANDING UNIT, the rule kept or not
        // (--x87-outside-blocks): a unit that lets its own float code run
        // anywhere still says whom it calls, and where, so that what it calls
        // is judged and an image of it can be linked against.
        _x87 = KeepsX87Facts && !AutomaticPacked ? new X87Facts(X87InBlocksOnly) : null;
        OptimizationSummary summary = new();
        // THE SECTION AS ALIGNED AS ITS FUNCTIONS: a duplicate the link cuts
        // out of it must be a whole number of the section's alignment.
        int functionAlign = CpuModel.FunctionAlign(FunctionAlign);
        Section text = new(".text", SectionKind.Code) { Align = Math.Max(4, functionAlign) };
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
        Encoder encoder = new(text, OptimizeSize);
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
        Corsac.Lang.Backends.StackMapRecorder maps = new(StackMapLayout.I386);
        bool cardStub = false, barrierStub = false;
        // Each function's arguments as it takes them, and the first call of
        // each name passing them in registers and the first on the stack.
        Dictionary<string, string> takes = new(StringComparer.Ordinal);
        Dictionary<(string Callee, string Shape), (string Caller, int Line)> passes = new();

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
        bool IsPrivate(string name) => privateNames.Contains(name) || name.StartsWith(FloatPoolPrefix, StringComparison.Ordinal);

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
        bool IsDefined(string name) => definedNames.Contains(name) || name.StartsWith(FloatPoolPrefix, StringComparison.Ordinal);

        // What the loader will supply. Only a non-position-independent
        // object asks: a shared one reaches everything it does not define
        // through its GOT already.
        HashSet<string> imported = PositionIndependent || Imported.Count == 0
            ? new HashSet<string>(StringComparer.Ordinal)
            : Imported;
        bool IsImported(string name) => imported.Contains(name);

        // THE SYMBOL QUESTIONS, AS DELEGATES MADE ONCE: written at each call
        // below, a local function became a new delegate for every function
        // compiled.
        Func<string, bool>? askPrivate = PositionIndependent ? IsPrivate : null;
        Func<string, bool> askDefined = IsDefined;
        Func<string, bool>? askImported = imported.Count == 0 ? null : IsImported;
        // The calls a link summary may name, refilled for each function.
        HashSet<string> eligible = new(StringComparer.Ordinal);

        // A bounded window avoids retaining a whole module of machine IR
        // (CompileWindow, both backends'). Workers only read the symbol sets
        // and each owns disjoint functions, results and diagnostics.
        // Encoding and layout below remain serial.
        Corsac.Lang.RegAlloc.SpillCounter? spills = CountSpills ? new(SpillMap) : null;
        PeakBatchBytes = 0; PeakBatchFunctions = 0;
        // THE FUNCTIONS THAT NEVER TOUCH THE FPU'S STATE (FpuFree): a call to
        // one keeps the MMX registers and needs no x87 save in a packed block.
        _fpuFree = FpuFree.Compute(module, SafepointPolls, out _fpuDefined);
        // BODIES LOADED AS THEY ARE COMPILED (FunctionLoader: the link's
        // regeneration) are not there to judge: a function of this module is
        // judged when a call to it is first asked about (DeferredFacts).
        // Only where NONE is there: a module that holds its bodies (a unit
        // whose late passes run at the link) was judged whole just above,
        // and its functions are being rewritten by the workers from here on
        // -- nothing reads them again for this. That verdict stands through
        // the rewriting: the late passes (the escape and region rules,
        // inlining, the allocator's fast paths) bring in callees' bodies
        // and fold and move what is there, and a function is free only
        // where every callee of it was; none makes x87 or MMX code of its own.
        _fpuDeferred = FunctionLoader is null || module.Functions.Any(f => f.Blocks.Count != 0)
            ? null : new DeferredFacts(module, FunctionLoader, SafepointPolls, _fpuDefined);
        Corsac.Lang.Backends.CompileWindow<MFunction> window = new(module.Functions, Workers, FunctionLoader, FunctionLoadBytes, FunctionMemoryBudget,
            (body, said) =>
            {
                Prepare?.Invoke(body);
                return Compile(body, said, askPrivate, askDefined, askImported);
            });
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
            cardStub |= m.UsesCardStub;
            barrierStub |= m.UsesBarrierStub;
            Function f = m.Source;
            // WHERE IT TAKES ITS ARGUMENTS, and where each call it makes by
            // name passes them (RegisterArgs.Agree), for the check below
            // once every function of the unit is in hand.
            takes[f.Name] = RegisterArgs.Shape(f);
            foreach (Corsac.Lang.Ir.Block b in f.Blocks)
                foreach (Instr i in b.Instrs)
                    if (i.Op == Opcode.Call && i.Callee is { } named && !NativeCall.Is(i) && !named.StartsWith(MachineIntrinsics.Prefix, StringComparison.Ordinal))
                    {
                        (string, string) key = (named, RegisterArgs.Shape(i));
                        if (!passes.ContainsKey(key)) passes.Add(key, (f.Display ?? f.Name, i.Line));
                    }
            // UNDER ITS OWN NAME, where its arguments come as its parameters
            // say (RegisterArgs: in registers or on the stack, the one way
            // every caller of it has) -- but for the function whose body
            // keeps on the stack what comes in registers (an asm block that
            // names the stack, RegisterArgs.BodyInRegisters): its body is
            // under a name of this object's own, and the function's name is
            // on the few instructions after it that push them and call it.
            bool stackBody = RegisterArgs.StackBody(f);
            string bodyName = stackBody ? RegisterArgs.StackBodyName(f.Name) : f.Name;
            // ITS WAY IN WITH ITS COMMONEST NUMBERS (Function.ConstantEntry,
            // the link's ArgumentFacts.Entries), in front of it under a name
            // of its own: the numbers set, and the function run on into --
            // it begins where the way in ends, the bytes of the gap its
            // alignment leaves anyway where they are enough. (A jump instead
            // to a function whose own name is on its stack entry, after it.)
            byte[]? wayIn = null;
            int wayInField = -1;
            FrameTable.Entry? wayInFrame = null;
            if (f.ConstantEntry is { } way) (wayIn, wayInField) = RegisterArgs.ConstantEntry(f, way.Given, stackBody);
            // The gap before a function (before its way in, where it has
            // one) is never executed, but it is filled with real no-ops
            // rather than zeros so a disassembly reads cleanly.
            int gap = (functionAlign - (text.Bytes.Count + (wayIn?.Length ?? 0)) % functionAlign) % functionAlign;
            Encoder.Nops(text.Bytes, gap);
            if (wayIn is not null)
            {
                string wayName = f.ConstantEntry!.Value.Name;
                obj.Symbols.Add(new Symbol { Name = wayName, Section = text, Offset = text.Bytes.Count, Size = wayIn.Length, IsFunction = true, Global = f.Exported });
                if (wayInField >= 0) text.Relocs.Add(new Relocation(text.Bytes.Count + wayInField, f.Name, -4, RelocKind.Rel32));
                // A frame-table record of its own, as the stack entry's below:
                // a thread stopped in it is found in its own few bytes, not
                // in the function before it -- under the function's name and
                // file, the function it is (no call returns into it, so only
                // a thread stopped there meets it), which the link's pool of
                // names holds once for both.
                wayInFrame = new FrameTable.Entry(wayName, f.Display ?? f.Name, f.SourceFile ?? "", text.Bytes.Count, wayIn.Length, new List<(int, int)>());
                text.Bytes.AddRange(wayIn);
                defined.Add(wayName);
            }
            int start = text.Bytes.Count;
            int size;
            // A function the encoder refuses -- the emitted-code audit's
            // finding above all, an instruction --cpu does not permit -- is
            // an error of this compile, reported as selection's are, and not
            // a crash of the compiler.
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
            if (EmitLinkSummary && !PositionIndependent) FamilyObject.DirectCalls(f, encoder, start, summary, eligible);
            FunctionSizes.Add((f.Name, size));
            // (A body under its own local name is this object's alone: copies
            // of one function in two units may have one this form and the
            // other the plain one, an asm block inlined into one of them.)
            obj.Symbols.Add(new Symbol
            {
                Name = bodyName, Section = text, Offset = start, Size = size, IsFunction = true, Global = f.Exported && !stackBody,
            });
            defined.Add(bodyName);
            // Its FpuFree marker, local: what the project build hands the
            // other units of this image (ProjectCompile's facts), never a
            // name two objects could both define.
            if (f.Exported && _fpuFree?.Contains(f.Name) == true)
                obj.Symbols.Add(new Symbol { Name = FpuFree.Marker(f.Name), Section = text, Offset = start, Size = 0, Global = false });
            // THE WAY IN TO A BODY THAT KEEPS ITS ARGUMENTS ON THE STACK
            // (RegisterArgs.StackEntry), after the body, with a frame and a
            // frame-table entry of its own so a walk of the frames goes
            // through it.
            FrameTable.Entry? entryFrame = null;
            if (stackBody)
            {
                int pad = (functionAlign - text.Bytes.Count % functionAlign) % functionAlign;
                Encoder.Nops(text.Bytes, pad);
                (byte[] entry, int at) = RegisterArgs.StackEntry(f);
                int entryStart = text.Bytes.Count;
                text.Bytes.AddRange(entry);
                text.Relocs.Add(new Relocation(entryStart + at, bodyName, -4, RelocKind.Rel32));
                obj.Symbols.Add(new Symbol
                {
                    Name = f.Name, Section = text, Offset = entryStart, Size = entry.Length, IsFunction = true, Global = f.Exported,
                });
                defined.Add(f.Name);
                // Named by a symbol of its own, not the function's display,
                // and with no file: a trace lists only frames a program would
                // name (with a dot, Runtime.TraceOf), and the function is not
                // listed twice -- its body's frame and this one's.
                entryFrame = new FrameTable.Entry(f.Name, f.Name, "", entryStart, entry.Length, new List<(int, int)>());
            }
            if (EmitLinkSummary && !PositionIndependent)
                Corsac.Lang.Opt.LinkSummary.AddConstantReturns(f, obj, summary);

            // WHAT THIS FUNCTION IS, for a fault to read back. Recorded per
            // function as it is encoded, because this is the one moment the
            // name, the source and the final byte offsets are all in hand.
            // (Its label the body's: the table's offsets count from the first
            // entry's symbol, and the body is what the start is of.) The way
            // in with numbers, before it in the code, before it here too.
            if (wayInFrame is { } numbersFrame) frames.Add(numbersFrame);
            frames.Add(new FrameTable.Entry(
                bodyName, f.Display ?? f.Name, f.SourceFile ?? "", start, size,
                new List<(int, int)>(encoder.Lines)));
            // The way in's after the body's: the table is in the order of the code.
            if (entryFrame is { } stackEntryFrame) frames.Add(stackEntryFrame);

            if (StackMaps)
                maps.Function<MInstr>(bodyName, start, m.Frame.Size, FamilyObject.SavedMask(m), m.FramePointerFree, encoder.CallSites, m.Safepoints,
                    m.Frame.SlotRanges(), m.SlotLiveness);

            Corsac.Lang.Backends.ObjectData.JumpTables<MBlock>(obj, PositionIndependent ? relocatedConstants : rodata, defined, bodyName,
                encoder.Tables, block => block.Offset, 4, RelocKind.Abs32);
            encoder.ReleaseFunction();
            if (ReleaseBodies) Corsac.Lang.Backends.ObjectData.Release(f);
        }

        // THE FRAME TABLE (FamilyObject). It holds one address, so in a
        // shared object it holds a relocation, and a relocation may not land
        // in a page the loader has to keep read-only: the relocated
        // constants then. Jump tables move for the same reason.
        FamilyObject.PlaceFrameTable(obj, PositionIndependent ? relocatedConstants : rodata, frames, defined, pcRelative: false);

        spills?.Table(module, pointerBytes: 4, errors);

        // The data, each item into its section, and the float constants the
        // code reads (ObjectData, both backends').
        Corsac.Lang.Backends.ObjectData.Place(module, obj, rodata, data, bss, relocatedConstants, 4, RelocKind.Abs32,
            PositionIndependent, imported.Contains, defined, errors, ReleaseBodies);
        Corsac.Lang.Backends.ObjectData.PlaceFloatPool(obj, text, rodata, FloatPoolPrefix, defined);

        // A CALL AND ITS FUNCTION DISAGREEING ON WHERE THE ARGUMENTS ARE
        // (RegisterArgs.Agree) is an error of this compile: the function has
        // the one name, and nothing later would tell.
        foreach (var ((callee, shape), call) in passes.OrderBy(pair => pair.Key.Callee, StringComparer.Ordinal).ThenBy(pair => pair.Key.Shape, StringComparer.Ordinal))
            if (takes.TryGetValue(callee, out string? taken) && !RegisterArgs.Agree(shape, taken))
                errors.Add("'" + call.Caller + "' (line " + call.Line + ") calls '" + callee + "' with " + RegisterArgs.Said(shape)
                    + ", which takes " + RegisterArgs.Said(taken));
        if (cardStub)
        {
            // THE CARD STUB (MachineIntrinsics.CardMark): the slot in EAX, every
            // register kept. No stack map and no frame entry: it calls nothing,
            // so no walk ever finds it on a stack.
            int gap = (functionAlign - text.Bytes.Count % functionAlign) % functionAlign;
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
            // machine words, in those two registers (RegisterArgs), so they
            // are saved and it is called. A collection while it runs finds no
            // map for the frames above it and reads them whole, which is
            // correct and conservative.
            int gap = (functionAlign - text.Bytes.Count % functionAlign) % functionAlign;
            Encoder.Nops(text.Bytes, gap);
            int at = text.Bytes.Count;
            text.Bytes.AddRange(new byte[]
            {
                0x51, 0x52, 0x50,           // push ecx; push edx; push eax
                0xE8,                       // call Runtime.WriteBarrier (slot EAX, value EDX)
            });
            text.Relocs.Add(new Relocation(text.Bytes.Count, BarrierRoutine, -4, RelocKind.Rel32));
            text.Bytes.AddRange(new byte[4]);
            text.Bytes.AddRange(new byte[]
            {
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
        // (__corsac_store_sequences). A kernel's processor answers its
        // collector's handshake at whatever instruction an interrupt finds it
        // on, and the kernel answers none whose EIP lies in here: between the Marking test and the store a thread so sent would
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
            int gap = (functionAlign - text.Bytes.Count % functionAlign) % functionAlign;
            Encoder.Nops(text.Bytes, gap);
            int start = text.Bytes.Count;
            void Bytes(params byte[] bytes) => text.Bytes.AddRange(bytes);
            void Address(string symbol, RelocKind kind, int addend)
            {
                text.Relocs.Add(new Relocation(text.Bytes.Count, symbol, addend, kind));
                text.Bytes.AddRange(new byte[4]);
            }
            // While a mark is under way, `routine` does the whole operation:
            // `places` puts its arguments where it takes them -- EAX, EDX and
            // ECX, the first three words (RegisterArgs) -- `saves` and
            // `restore` are the registers kept round it; it RETs with the
            // routine's answer in EAX. Else on past it.
            void Marked(byte[] saves, byte[] places, string routine, byte[] restore)
            {
                Bytes(0x83, 0x3D); Address(MarkingFlag, RelocKind.Abs32, 0); Bytes(0x00);  // cmp dword [Marking], 0
                int skip = saves.Length + places.Length + 5 + restore.Length + 1;
                Bytes(0x74, (byte)skip);                                                   // je past the call
                Bytes(saves); Bytes(places); Bytes(0xE8);                                  // push ...; call routine
                Address(routine, RelocKind.Rel32, -4);
                Bytes(restore); Bytes(0xC3);                                               // pop ...; ret
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
            // Marking: WriteBarrierStore(slot, value), which takes them where
            // they are, with EAX, ECX and EDX saved round it.
            Marked([0x51, 0x52, 0x50], [], BarrierStoreRoutine, [0x58, 0x5A, 0x59]);
            Bytes(0x89, 0x10);                                                             // mov [eax], edx
            CardOfEax();
            // __corsac_cardstore: the same from the store on, for a store
            // that overwrites nothing (a new block's, Lowering.StoreNew).
            int cardStore = text.Bytes.Count;
            Bytes(0x89, 0x10);                                                             // mov [eax], edx
            CardOfEax();
            // __corsac_refxchg: slot EAX, value EDX; the old reference in EAX,
            // every other register kept. Marking: WriteBarrierExchange(slot,
            // value), which takes them where they are, its answer the old
            // reference, ECX and EDX saved round it.
            int exchange = text.Bytes.Count;
            Marked([0x51, 0x52], [], BarrierExchangeRoutine, [0x5A, 0x59]);
            Bytes(0x51, 0x89, 0xC1, 0x89, 0xD0);                                           // push ecx; mov ecx, eax; mov eax, edx
            // ON A 386 THE EXCHANGE TAKES WHAT THE COMPARE-EXCHANGE TAKES:
            // __corsac_refcas below compares and stores under the 386 lock
            // where it may not turn interrupts off, and a bare XCHG landing
            // between its read and its write was lost under its store -- as
            // the ints' were before AtomicSwap386. With interrupts off where
            // this code may (CPL at most IOPL), else under the lock.
            if (Target.X86Profile.Has(X86Feature.I486))
            {
                Bytes(0x87, 0x01);                                                         // xchg [ecx], eax
            }
            else
            {
                Bytes(0x9C, 0x53,                                                          // pushfd; push ebx
                      0x31, 0xDB, 0x66, 0x8C, 0xCB, 0x83, 0xE3, 0x03,                      // xor ebx, ebx; mov bx, cs; and ebx, 3
                      0x51, 0x8B, 0x4C, 0x24, 0x08, 0xC1, 0xE9, 0x0C, 0x83, 0xE1, 0x03,    // push ecx; mov ecx, [esp+8]; shr ecx, 12; and ecx, 3
                      0x39, 0xCB, 0x59, 0x77, 0x07,                                        // cmp ebx, ecx; pop ecx; ja locked
                      0xFA,                                                                // cli
                      0x87, 0x01,                                                          // xchg [ecx], eax
                      0x5B, 0x9D, 0xEB, 0x1B);                                             // pop ebx; popfd; jmp past
                Bytes(0xBB, 0x01, 0x00, 0x00, 0x00, 0x87, 0x1D);                           // locked: mov ebx, 1; xchg [lock], ebx
                Address(Atomics386Lock, RelocKind.Abs32, 0);
                Bytes(0x85, 0xDB, 0x75, 0xF1,                                              // test ebx, ebx; jnz locked
                      0x87, 0x01,                                                          // xchg [ecx], eax
                      0x31, 0xDB, 0x87, 0x1D);                                             // xor ebx, ebx; xchg [lock], ebx
                Address(Atomics386Lock, RelocKind.Abs32, 0);
                Bytes(0x5B, 0x9D);                                                         // pop ebx; popfd; past:
            }
            Bytes(0x52, 0x8B, 0x15); Address(CardTable, RelocKind.Abs32, 0);               // push edx; mov edx, [Cards]
            Bytes(0x85, 0xD2, 0x74, 0x09,                                                  // test edx, edx; jz done
                  0x51, 0xC1, 0xE9, 0x0A, 0xC6, 0x04, 0x0A, 0x01, 0x59,                    // push ecx; shr ecx, 10; mov byte [edx+ecx], 1; pop ecx
                  0x5A, 0x59, 0xC3);                                                       // done: pop edx; pop ecx; ret
            // __corsac_refcas: slot ECX, value EDX, expected EAX; what the
            // slot held in EAX, every other register kept. Marking:
            // WriteBarrierCompareExchange(slot, value, expected) -- the slot
            // to EAX and the expected to ECX, one exchange -- ECX and EDX
            // saved round it.
            int compareExchange = text.Bytes.Count;
            // These sequences are bytes, which the emitted-code audit never
            // walks: their one instruction past the 386, CMPXCHG, is the
            // 486's. On a 386 -- one processor -- the compare and the store
            // are done with interrupts off where this code may turn them off
            // (its ring, CS's low bits, at most EFLAGS.IOPL: a kernel's ring 0,
            // a ring-1 service with IOPL 1), and else under the runtime's 386
            // lock (Runtime.Atomics386), as Platform.AtomicsEnter does.
            Marked([0x51, 0x52], [0x91], BarrierCompareExchangeRoutine, [0x5A, 0x59]);     // xchg eax, ecx
            if (Target.X86Profile.Has(X86Feature.I486))
            {
                Bytes(0xF0, 0x0F, 0xB1, 0x11);                                             // lock cmpxchg [ecx], edx
            }
            else
            {
                Bytes(0x9C, 0x53,                                                          // pushfd; push ebx
                      0x31, 0xDB, 0x66, 0x8C, 0xCB, 0x83, 0xE3, 0x03,                      // xor ebx, ebx; mov bx, cs; and ebx, 3
                      0x51, 0x8B, 0x4C, 0x24, 0x08, 0xC1, 0xE9, 0x0C, 0x83, 0xE1, 0x03,    // push ecx; mov ecx, [esp+8]; shr ecx, 12; and ecx, 3
                      0x39, 0xCB, 0x59, 0x77, 0x0F,                                        // cmp ebx, ecx; pop ecx; ja locked
                      0xFA,                                                                // cli
                      0x39, 0x01, 0x74, 0x04, 0x8B, 0x01, 0xEB, 0x02, 0x89, 0x11,          // cmp [ecx], eax; je +4; mov eax, [ecx]; jmp +2; mov [ecx], edx
                      0x5B, 0x9D, 0xEB, 0x23);                                             // pop ebx; popfd; jmp past
                Bytes(0xBB, 0x01, 0x00, 0x00, 0x00, 0x87, 0x1D);                           // locked: mov ebx, 1; xchg [lock], ebx
                Address(Atomics386Lock, RelocKind.Abs32, 0);
                Bytes(0x85, 0xDB, 0x75, 0xF1,                                              // test ebx, ebx; jnz locked
                      0x39, 0x01, 0x74, 0x04, 0x8B, 0x01, 0xEB, 0x02, 0x89, 0x11,          // the compare and the store
                      0x31, 0xDB, 0x87, 0x1D);                                             // xor ebx, ebx; xchg [lock], ebx
                Address(Atomics386Lock, RelocKind.Abs32, 0);
                Bytes(0x5B, 0x9D);                                                         // pop ebx; popfd; past:
            }
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

        // The stack-map table, one an object: read by the collector to know,
        // at each call a frame is stopped in, which of its words and saved
        // registers hold live references (StackMapTable has the layout,
        // runtime/src/core/gc.cor's MapSite reads it). Its base is the first
        // function with a call's address, a word the loader writes in a
        // shared object.
        if (StackMaps) maps.Emit(obj, defined, StackMapSection, StackMapStart, StackMapEnd, pcRelative: false, RelocKind.Abs32, PositionIndependent);

        Corsac.Lang.Backends.ObjectData.DeclareUndefined(obj, module.Imports, defined);
        if (EmitLinkSummary && !PositionIndependent)
        {
            summary.Attach(obj);
        }
        _x87?.Attach(obj, module);
        return obj;
    }

    /// <summary>The runtime's streaming copy and fill, where the module has them to call (Module.RuntimeHelpers: lowering kept them) and its code owns the x87 state (Selector.StreamedOr).</summary>
    private void FindStreams(Module module)
    {
        _streamCopy = AutomaticPacked && module.RuntimeHelpers.Contains(Corsac.Lang.Lto.RuntimeAbi.StreamCopy) ? Corsac.Lang.Lto.RuntimeAbi.StreamCopy : null;
        _streamFill = AutomaticPacked && module.RuntimeHelpers.Contains(Corsac.Lang.Lto.RuntimeAbi.StreamFill) ? Corsac.Lang.Lto.RuntimeAbi.StreamFill : null;
    }

    public string Assembly(Module module)
    {
        X86Mode.Long = false;
        // As Generate compiles it: the streaming routines where the module has them.
        FindStreams(module);
        _fpuFree = FpuFree.Compute(module, SafepointPolls, out _fpuDefined);
        _fpuDeferred = null;
        StringBuilder sb = new();
        List<string> errors = new();
        sb.Append("; module ").Append(module.Name).Append('\n');
        sb.Append("bits 32\n\n");
        sb.Append("section .text\n\n");
        foreach (Function f in module.Functions)
        {
            Prepare?.Invoke(f);
            MFunction? m = Compile(f, errors);
            if (m is not null)
            {
                if (CountSpills) SpillCount.Run(m, new Corsac.Lang.RegAlloc.SpillCounter(null));
                AsmText.Print(sb, m);
            }
        }
        AsmText.PrintData(sb, module);
        foreach (string e in errors)
        {
            sb.Append("; error: ").Append(e).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>This module's functions that never touch the FPU's state, and the names it defines (FpuFree.Compute), for its selection.</summary>
    private HashSet<string>? _fpuFree, _fpuDefined;
    private DeferredFacts? _fpuDeferred;

    /// <summary>
    /// THE SAME VERDICT WHERE THE BODIES COME ONE AT A TIME (FunctionLoader):
    /// FpuFree.Compute finds none of the module's functions free there --
    /// every one is a header -- and a unit regenerated at the link knew none
    /// of its own, so every call inside it from a packed block emptied the
    /// MMX registers and saved the x87's state as if into the unknown. Here
    /// a function is judged when a call to it is first asked about: its
    /// body loaded, and every function of the module it reaches, each read
    /// for what it says of itself (FpuFree.Summarise) and let go; then the
    /// same fixed point over those (FpuFree.Settle), which is the whole
    /// module's answer for them -- the set is closed under their calls. The
    /// answers are kept; a module with no packed state at a call asks
    /// nothing and loads nothing twice. What it does load a second time is
    /// loaded as the link loads it, its rules run again and its reports
    /// made again: the price of a module that came without its bodies, paid
    /// only for the functions packed code calls.
    ///
    /// The loads run on a thread of their own (the loader keeps its
    /// analyses' state by thread, and the asker is in the middle of a
    /// function's selection), one asker at a time. Never made for a module
    /// that holds its bodies (Generate).
    /// </summary>
    internal sealed class DeferredFacts
    {
        private readonly Dictionary<string, int> _index = new(StringComparer.Ordinal);
        private readonly Func<int, Function> _load;
        private readonly bool _polls;
        private readonly HashSet<string> _defined;
        private readonly Dictionary<string, List<string>?> _said = new(StringComparer.Ordinal);
        // Read by every asker, with no lock where the answer is there: an
        // answer once written never changes.
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _free = new(StringComparer.Ordinal);

        public DeferredFacts(Module module, Func<int, Function> load, bool polls, HashSet<string> defined)
        {
            for (int k = 0; k < module.Functions.Count; k++) _index.TryAdd(module.Functions[k].Name, k);
            _load = load;
            _polls = polls;
            _defined = defined;
        }

        public bool Free(string name)
        {
            if (_free.TryGetValue(name, out bool known)) return known;
            lock (this)
            {
                if (_free.TryGetValue(name, out known)) return known;
                // A thread of its own, not the pool's: a task waited on from
                // a pool thread may be run by that very thread, and the
                // loader forgets its thread's analyses as it starts. The
                // asker's unit goes with it (UnitScope.OnThread): the load
                // runs the unit's passes, under its settings and counted to it.
                Exception? failed = null;
                Thread judge = Corsac.UnitScope.OnThread(() => { try { Judge(name); } catch (Exception e) { failed = e; } }, 16 * 1024 * 1024);
                judge.Start();
                judge.Join();
                if (failed is not null) throw new InvalidOperationException("judging " + name + " for FpuFree: " + failed.Message, failed);
                return _free[name];
            }
        }

        private void Judge(string asked)
        {
            // Every function of the module the asked one reaches, read once.
            List<string> reached = new();
            Stack<string> work = new();
            work.Push(asked);
            HashSet<string> seen = new(StringComparer.Ordinal) { asked };
            while (work.Count > 0)
            {
                string name = work.Pop();
                reached.Add(name);
                if (!_said.TryGetValue(name, out List<string>? callees))
                    _said[name] = callees = _index.TryGetValue(name, out int at) ? FpuFree.Summarise(_load(at), _polls) : null;
                if (callees is null) continue;
                foreach (string callee in callees)
                    if (_defined.Contains(callee) && !_free.ContainsKey(callee) && seen.Add(callee)) work.Push(callee);
            }
            // Those judged before are settled already: free or not, as they said.
            Dictionary<string, List<string>> calls = new(StringComparer.Ordinal);
            foreach (string name in reached)
                if (_said[name] is { } callees && !callees.Any(c => _free.TryGetValue(c, out bool was) && !was))
                    calls[name] = callees.Where(c => !(_free.TryGetValue(c, out bool was) && was)).ToList();
            HashSet<string> free = FpuFree.Settle(calls, _defined);
            foreach (string name in reached) _free[name] = free.Contains(name);
        }
    }

    /// <summary>Select and allocate one function; null, with errors reported, if it cannot be compiled.</summary>
    private MFunction? Compile(Function f, List<string> errors, Func<string, bool>? isPrivate = null, Func<string, bool>? isDefined = null, Func<string, bool>? isImported = null)
    {
        int before = errors.Count;
        if (SafepointPolls) Corsac.Lang.Opt.BackEdgePolls.Run(f);
        // The frees before a trap, one copy for every trap that has them (SharedTrapFrees).
        Corsac.Lang.Opt.SharedTrapFrees.Run(f);
        // Frame slots whose lives do not meet share their bytes (SlotShare).
        Corsac.Lang.Opt.SlotShare.Run(f);
        MFunction m;
        FpuFree.Use(_fpuFree, _fpuDefined, _fpuDeferred is { } deferred ? deferred.Free : null);
        try
        {
            m = Selector.Run(f, errors, AutomaticPacked, PositionIndependent, WindowsTls, _streamCopy, _streamFill, _x87);
        }
        finally
        {
            // Asked by the selection alone: let go, however it ended, so
            // that a pool's idle thread holds no module by them.
            FpuFree.Use(null, null);
        }
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
            FamilyPasses.Run(m, StackMaps, Target.X86Profile.Has(X86Feature.Cmov));
        }
        catch (InvalidOperationException e)
        {
            errors.Add(e.Message);
            return null;
        }
        return m;
    }
}
