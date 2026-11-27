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
/// .bss by their flags (a string object with the image's objects, ObjectData.Place), holding 64-bit absolute relocations where they hold
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

    /// <summary>
    /// A KERNEL MODULE WHOSE TEXT ITS BINDER RELOCATES (--relocated-text),
    /// with PositionIndependent: the image still moves, so every address held
    /// in data is the loader's as above, but nothing goes through the GOT --
    /// a call into the kernel is `call rel32`, the store sequences' stubs
    /// included (straight into the kernel's range), and a kernel name is
    /// reached `[rip + name]`, each a R_X86_64_PC32 the binder fixes
    /// (Linker.LinkModule). The barrier's slow path is the object's own stub,
    /// as a program's. -mcmodel=kernel's reach: sound where the kernel loads
    /// its modules within 2 GB of its image, as corsac86 and Linux do; the
    /// binder refuses a reference that does not fit.
    /// </summary>
    public bool RelocatedText { get; set; }

    /// <summary>A unit of a Windows image (PE32+): X86Mode.Windows for its selection and encoding, and unwind data for every function (Unwind).</summary>
    public bool Windows { get; set; }

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
    /// hosted: a copy or a fill calls them at Selector._streamBytes and over
    /// (three quarters of --cpu's last cache) -- behind a
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
        X86Mode.Windows = Windows;
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
        // A WINDOWS IMAGE'S UNWIND DATA (Unwind): the UNWIND_INFO blobs, one
        // of each shape, and the table naming each function's range and blob,
        // which the PE link turns into .pdata.
        Section? xdata = null, unwindTable = null;
        Dictionary<string, string> xdataBlobs = new(StringComparer.Ordinal);
        if (Windows)
        {
            xdata = new Section(".xdata", SectionKind.ReadOnlyData) { Align = 4 };
            unwindTable = new Section(Corsac.Lang.Elf.Linker.UnwindTableSection, SectionKind.Note) { Align = 8 };
            obj.Sections.Add(xdata);
            obj.Sections.Add(unwindTable);
        }
        List<Corsac.Lang.X86.FrameTable.Entry> frames = new();
        // The stack maps' records as each function is encoded (StackMapRecorder, both backends').
        Corsac.Lang.Backends.StackMapRecorder maps = new(StackMapLayout.X64);
        HashSet<string> eligible = new(StringComparer.Ordinal);
        // Whether a function called the barrier's stub (Selector, MachineIntrinsics.Barrier).
        bool barrierStub = false;
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
            barrierStub |= m.UsesBarrierStub;
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
            if (EmitLinkSummary) FamilyObject.DirectCalls(f, encoder, start, summary, eligible);
            // A WAY IN FROM WINDOWS (Function.NativeEntry): the body under a
            // name of its own, the function's name the stub (WindowsEntry).
            bool viaStub = Windows && f.NativeEntry;
            string name = viaStub ? f.Name + NativeEntryBody : f.Name;
            if (Windows) Unwind(m, name, text, start, size, xdata!, unwindTable!, xdataBlobs, obj, errors);
            FunctionSizes.Add((f.Name, size));
            obj.Symbols.Add(new Symbol
            {
                // (The body as visible as the function: a direct call from
                // another unit is sent to it by the link, Linker.LinkPe.)
                Name = name, Section = text, Offset = start, Size = size, IsFunction = true, Global = f.Exported,
            });
            defined.Add(name);
            if (viaStub) WindowsEntry(f, name, text, obj, defined, xdata!, unwindTable!, xdataBlobs, errors);
            if (EmitLinkSummary)
                Corsac.Lang.Opt.LinkSummary.AddConstantReturns(new[] { f }, obj, summary);

            frames.Add(new Corsac.Lang.X86.FrameTable.Entry(
                name, f.Display ?? f.Name, f.SourceFile ?? "", start, size,
                new List<(int, int)>(encoder.Lines)));

            if (StackMaps)
                maps.Function<MInstr>(name, start, m.Frame.Size, FamilyObject.SavedMask(m), m.FramePointerFree, encoder.CallSites, m.Safepoints,
                    m.Frame.SlotRanges(), m.SlotLiveness);

            // Jump tables: eight-byte absolute addresses of the blocks, which
            // the loader relocates in a shared object.
            Corsac.Lang.Backends.ObjectData.JumpTables<MBlock>(obj, PositionIndependent ? relocatedConstants : rodata, defined, f.Name,
                encoder.Tables, block => block.Offset, 8, RelocKind.Abs64);
            encoder.ReleaseFunction();
            // (Only where nothing reads a body after Generate: Driver sets
            // ReleaseBodies off where the object is to carry the module's IR.)
            if (ReleaseBodies) Corsac.Lang.Backends.ObjectData.Release(f);
        }

        EmitStoreSequences(module, text, obj, defined);
        if (barrierStub) EmitBarrierStub(text, obj, defined, xdata, unwindTable, xdataBlobs, errors);

        // The stack-map table (version 1 in x86-64's layout), one an object.
        // Its base is the distance from the header's word to the first
        // function with a call (PC32): an image loaded anywhere describes
        // itself without the loader, as this target's frame table does, so
        // the section is read-only in a shared object too.
        if (StackMaps)
            maps.Emit(obj, defined, Corsac.Lang.X86.X86Backend.StackMapSection, Corsac.Lang.X86.X86Backend.StackMapStart,
                Corsac.Lang.X86.X86Backend.StackMapEnd, pcRelative: true, RelocKind.Abs64, PositionIndependent);

        // The frame table: the x86 format, whose one address is 32 bits. In
        // long mode it is the distance from the word to the first function
        // (PC32), which a shared object loaded anywhere can hold without the
        // loader's help; the runtime's TableAddress reads it back.
        FamilyObject.PlaceFrameTable(obj, rodata, frames, defined, pcRelative: true);

        spills?.Table(module, pointerBytes: 8, errors);

        // The data, each item into its section, and the float constants the
        // code reads RIP-relative (ObjectData, both backends': the family's
        // pool, X86Backend.FloatPoolPrefix, Select.Sse's FloatConstant names
        // them by their bits). A name is another image's where the loader
        // supplies it (isExternal).
        Corsac.Lang.Backends.ObjectData.Place(module, obj, rodata, data, bss, relocatedConstants, 8, RelocKind.Abs64,
            PositionIndependent, name => isExternal is not null && isExternal(name), defined, errors, ReleaseBodies);
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

    /// <summary>Whether a name is another object's to supply, reached through the loader's tables: every name not defined here in position-independent code, an imported one in a program; null where there is none, and in a module whose text is relocated (RelocatedText), which reaches every name where it is.</summary>
    private Func<string, bool>? External(HashSet<string> definedNames)
        => RelocatedText ? null
            : PositionIndependent ? name => !definedNames.Contains(name)
            : Imported.Count > 0 ? name => Imported.Contains(name) && !definedNames.Contains(name)
            : null;

    /// <summary>The module as assembly to read (--asm): long mode's machine code as the family's listing prints it (AsmText), then its data.</summary>
    public string Assembly(Module module)
    {
        // Long mode, as Generate says it: the selection, the allocation and
        // the listing all read it.
        X86Mode.Long = true;
        X86Mode.Windows = Windows;
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
        MFunction m = Selector.RunLong(f, errors, isExternal, _streamCopy, _streamFill, PositionIndependent && !RelocatedText);
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

    /// <summary>
    /// THE BARRIER'S STUB (MachineIntrinsics.Barrier, CardMarks.Expand): the
    /// snapshot barrier's slow path, slot in RAX and value in RDX, every
    /// register kept -- the nine general registers the convention lets a
    /// callee destroy pushed, the sixteen XMM saved in 256 bytes at sixteen
    /// (RSP is 8 past a 16 at entry; nine pushes make it 16) -- around
    /// Runtime.WriteBarrier(slot, value) called by the System V convention.
    /// The object's own, local, as i386's __corsac_barrier. No frame record
    /// and no stack map: a collection while it runs finds none for the
    /// frames above and reads them whole, as for the store sequences'.
    /// Never in a position-independent object, whose selection calls the
    /// runtime instead -- except a module with relocated text, whose stub's
    /// call is a direct one into the kernel (RelocatedText).
    /// </summary>
    private void EmitBarrierStub(Section text, ObjectFile obj, HashSet<string> defined, Section? xdata = null, Section? unwindTable = null,
                                 Dictionary<string, string>? xdataBlobs = null, List<string>? errors = null)
    {
        ChunkedBytes b = text.Bytes;
        int gap = (FunctionAlign - b.Count % FunctionAlign) % FunctionAlign;
        Encoder.Nops(b, gap);
        int start = b.Count;
        void Bytes(params byte[] bytes) => b.AddRange(bytes);
        void Xmms(bool store)
        {
            // movdqu [rsp+16k], xmmK, or back.
            for (int k = 0; k < 16; k++)
            {
                Bytes(0xF3);
                if (k >= 8) Bytes(0x44);
                Bytes(0x0F, store ? (byte)0x7F : (byte)0x6F, (byte)(0x84 | ((k & 7) << 3)), 0x24);
                b.AddRange(BitConverter.GetBytes(16 * k));
            }
        }
        Bytes(0x50, 0x51, 0x52, 0x56, 0x57, 0x41, 0x50, 0x41, 0x51, 0x41, 0x52, 0x41, 0x53);   // push rax rcx rdx rsi rdi r8-r11
        Bytes(0x48, 0x81, 0xEC); b.AddRange(BitConverter.GetBytes(256));                      // sub rsp, 256
        Xmms(true);
        Bytes(0x48, 0x89, 0xC7, 0x48, 0x89, 0xD6);                                              // mov rdi, rax; mov rsi, rdx
        Bytes(0xE8);                                                                            // call Runtime.WriteBarrier
        text.Relocs.Add(new Relocation(b.Count, Corsac.Lang.Lto.RuntimeAbi.WriteBarrier, -4, RelocKind.Plt32));
        b.AddRange(new byte[4]);
        Xmms(false);
        Bytes(0x48, 0x81, 0xC4); b.AddRange(BitConverter.GetBytes(256));                      // add rsp, 256
        Bytes(0x41, 0x5B, 0x41, 0x5A, 0x41, 0x59, 0x41, 0x58, 0x5F, 0x5E, 0x5A, 0x59, 0x58);   // pop r11-r8 rdi rsi rdx rcx rax
        Bytes(0xC3);                                                                            // ret
        obj.Symbols.Add(new Symbol { Name = Corsac.Lang.X86.X86Backend.BarrierStub, Section = text, Offset = start, Size = b.Count - start, IsFunction = true, Global = false });
        defined.Add(Corsac.Lang.X86.X86Backend.BarrierStub);
        // A Windows image's description of it: nine pushes and 256 bytes,
        // its stack pointer fixed after them (its call's stack is aligned by
        // the 72 + 256 + 8 it holds).
        if (Windows && xdata is not null)
        {
            List<(int End, int Op, int Info)> codes = new();
            int[] regs = { 0, 1, 2, 6, 7, 8, 9, 10, 11 };
            int end = 0;
            foreach (int r in regs) { end += r >= 8 ? 2 : 1; codes.Add((end, 0, r)); }
            codes.Add((end + 7, 1, 256));
            WriteUnwind(Corsac.Lang.X86.X86Backend.BarrierStub, b.Count - start, codes, 0, xdata, unwindTable!, xdataBlobs!, obj, errors!);
        }
    }

    /// <summary>The store sequences' stubs, named as i386's (RuntimeAbi; CardMarks.FuseStores).</summary>
    public const string RefStoreStub = Corsac.Lang.Lto.RuntimeAbi.RefStore, CardStoreStub = Corsac.Lang.Lto.RuntimeAbi.CardStore;
    public const string RefExchangeStub = Corsac.Lang.Lto.RuntimeAbi.RefExchange, RefCompareExchangeStub = Corsac.Lang.Lto.RuntimeAbi.RefCompareExchange;
    /// <summary>
    /// A FUNCTION'S UNWIND DATA FOR WINDOWS (X86Mode.Windows): the
    /// UNWIND_INFO Windows' unwinder reads to find its caller's frame
    /// (Microsoft's "x64 exception handling"), version 1, which every
    /// 64-bit Windows and Wine read. The prologue is read back from the bytes
    /// just written, and only two shapes are taken (any other is an error
    /// here, never data that might mislead):
    ///
    ///   - A FRAME: `push rbp ; mov rbp, rsp` first. PUSH_NONVOL rbp after
    ///     the push and SET_FPREG (RBP, offset 0) after the move, as Go
    ///     describes every function. Whatever follows -- the saves, `and rsp,
    ///     -16`, the probe, `sub rsp`, and the body's own pushes -- needs no
    ///     code: the unwinder recovers RSP from RBP. The registers the body
    ///     saves are not described (their values are the caller's again once
    ///     the walk reaches the way in from Windows, whose stub saved and
    ///     describes every one Windows' convention keeps).
    ///   - A LEAF (MFunction.Frameless): pushes, then an optional probe, then
    ///     an optional `sub rsp, n` -- PUSH_NONVOL for each push and an
    ///     allocation for the `sub`, since a leaf calls nothing and its stack
    ///     pointer stays where its prologue left it. A leaf that does neither
    ///     needs no entry: the unwinder takes a function with none for one,
    ///     the return address at [RSP].
    ///
    /// Identical blobs are written once an object (as Go's linker does); the
    /// table's record is the function's start and end and its blob, three
    /// Abs64 words the PE link turns into a RUNTIME_FUNCTION.
    /// </summary>
    private static void Unwind(Corsac.Lang.X86.MFunction m, string name, Section text, int start, int size, Section xdata, Section table,
                               Dictionary<string, string> blobs, ObjectFile obj, List<string> errors)
    {
        if (m.ColdFrames || m.ReservedFrame)
        {
            errors.Add($"{name}: a frame made after its prologue, which Windows' unwind data cannot describe (FramePointerFree keeps it off in a Windows image)");
            return;
        }
        Corsac.Lang.Ir.ChunkedBytes b = text.Bytes;
        byte At(int k) => k < size ? b[start + k] : (byte)0xCC;
        // (end of the instruction, operation, operand): 0 PUSH_NONVOL, 1 allocation, 3 SET_FPREG.
        List<(int End, int Op, int Info)> codes = new();
        int frameRegister = 0;
        int pos = 0;
        if (!m.Frameless)
        {
            if (At(0) != 0x55 || At(1) != 0x48 || At(2) != 0x89 || At(3) != 0xE5)
            {
                errors.Add($"{name}: its prologue is not `push rbp ; mov rbp, rsp`, the frame Windows' unwind data is written for");
                return;
            }
            codes.Add((1, 0, 5));
            codes.Add((4, 3, 0));
            frameRegister = 5;
        }
        else
        {
            // Exactly its saves, then the probe where its frame is a page or
            // more, then its `sub` where it has a frame: read for what the
            // encoder wrote (PrologueL), and nothing of the body taken for it.
            int frameBytes = Corsac.Lang.X86.Encoder.FramelessBytes(m);
            for (int k = 0; k < m.SavedRegs.Count; k++)
            {
                if (At(pos) is >= 0x50 and <= 0x57) { codes.Add((pos + 1, 0, At(pos) - 0x50)); pos += 1; continue; }
                if (At(pos) == 0x41 && At(pos + 1) is >= 0x50 and <= 0x57) { codes.Add((pos + 2, 0, 8 + At(pos + 1) - 0x50)); pos += 2; continue; }
                break;
            }
            if (frameBytes >= Corsac.Lang.X86.Encoder.ProbePage && At(pos) == 0x49 && At(pos + 1) == 0x89 && At(pos + 2) == 0xE3) pos += Corsac.Lang.X86.Encoder.ProbeBytes;
            if (frameBytes > 0 && At(pos) == 0x48 && At(pos + 1) == 0x83 && At(pos + 2) == 0xEC) { codes.Add((pos + 4, 1, At(pos + 3))); pos += 4; }
            else if (frameBytes > 0 && At(pos) == 0x48 && At(pos + 1) == 0x81 && At(pos + 2) == 0xEC)
            {
                codes.Add((pos + 7, 1, b[start + pos + 3] | b[start + pos + 4] << 8 | b[start + pos + 5] << 16 | b[start + pos + 6] << 24));
                pos += 7;
            }
            if (codes.Count != m.SavedRegs.Count + (frameBytes > 0 ? 1 : 0))
            {
                errors.Add($"{name}: its leaf prologue was not read back as its {m.SavedRegs.Count} pushes and its frame");
                return;
            }
            if (codes.Count == 0) return;
        }
        WriteUnwind(name, size, codes, frameRegister, xdata, table, blobs, obj, errors);
    }

    /// <summary>
    /// One function's UNWIND_INFO from its prologue's operations, in prologue
    /// order -- (end of the instruction, 0 PUSH_NONVOL / 1 allocation / 3
    /// SET_FPREG / 4 SAVE_NONVOL / 8 SAVE_XMM128, operand; for SAVE_NONVOL and
    /// SAVE_XMM128 the register, and the offset from the frame's base in
    /// `extra`, by the operation's index) -- written once an object
    /// (Unwind), and its record in the table.
    /// </summary>
    private static void WriteUnwind(string name, int size, List<(int End, int Op, int Info)> codes, int frameRegister, Section xdata, Section table,
                                    Dictionary<string, string> blobs, ObjectFile obj, List<string> errors, int frameOffset = 0, List<int>? extra = null)
    {
        int prologBytes = codes[^1].End;
        if (prologBytes > 255)
        {
            errors.Add($"{name}: a prologue of {prologBytes} bytes, more than the 255 Windows' unwind data can describe");
            return;
        }
        // THE CODES, the last operation first (descending offset), each one
        // node, or two or three for a large allocation; padded to an even count.
        List<ushort> nodes = new();
        for (int k = codes.Count - 1; k >= 0; k--)
        {
            (int end, int op, int info) = codes[k];
            if (op == 1)
            {
                if (info % 8 != 0 || info <= 0)
                {
                    errors.Add($"{name}: a frame of {info} bytes, not a multiple of eight, which Windows' unwind data cannot describe");
                    return;
                }
                if (info <= 128) nodes.Add((ushort)(end | (2 | ((info - 8) / 8) << 4) << 8));                 // UWOP_ALLOC_SMALL
                else if (info <= 512 * 1024 - 8) { nodes.Add((ushort)(end | 0x01 << 8)); nodes.Add((ushort)(info / 8)); }   // UWOP_ALLOC_LARGE, size/8
                else { nodes.Add((ushort)(end | 0x11 << 8)); nodes.Add((ushort)info); nodes.Add((ushort)(info >> 16)); }     // UWOP_ALLOC_LARGE, size
            }
            else if (op is 4 or 8)
            {
                // SAVE_NONVOL (scaled by 8) or SAVE_XMM128 (scaled by 16): its offset from the frame's base in the next node.
                int offset = extra![k];
                nodes.Add((ushort)(end | (op | info << 4) << 8));
                nodes.Add((ushort)(offset / (op == 8 ? 16 : 8)));
            }
            else nodes.Add((ushort)(end | (op | info << 4) << 8));
        }
        int count = nodes.Count;
        if ((nodes.Count & 1) != 0) nodes.Add(0);
        byte[] blobBytes = new byte[4 + 2 * nodes.Count];
        blobBytes[0] = 1;                              // version 1, no handler
        blobBytes[1] = (byte)prologBytes;
        blobBytes[2] = (byte)count;
        blobBytes[3] = (byte)(frameRegister | frameOffset << 4);   // FrameOffset in sixteens
        for (int k = 0; k < nodes.Count; k++) { blobBytes[4 + 2 * k] = (byte)nodes[k]; blobBytes[5 + 2 * k] = (byte)(nodes[k] >> 8); }
        string key = Convert.ToHexString(blobBytes);
        if (!blobs.TryGetValue(key, out string? blob))
        {
            blob = "__corsac_xdata_" + blobs.Count;
            int at = xdata.Bytes.Count;
            xdata.Bytes.AddRange(blobBytes);
            obj.Symbols.Add(new Symbol { Name = blob, Section = xdata, Offset = at, Size = blobBytes.Length, Global = false });
            blobs[key] = blob;
        }
        int record = table.Bytes.Count;
        table.Bytes.AddRange(new byte[Corsac.Lang.Elf.Linker.UnwindRecordBytes]);
        table.Relocs.Add(new Relocation(record, name, 0, RelocKind.Abs64));
        table.Relocs.Add(new Relocation(record + 8, name, size, RelocKind.Abs64));
        table.Relocs.Add(new Relocation(record + 16, blob, 0, RelocKind.Abs64));
    }

    /// <summary>The name a way in from Windows' body is compiled under (Function.NativeEntry; WindowsEntry).</summary>
    public const string NativeEntryBody = "$sysv";

    /// <summary>
    /// A WAY IN FROM WINDOWS (Function.NativeEntry): under the function's
    /// name, a stub that Windows' x64 convention calls and that calls the
    /// body (compiled by System V's under `body`), as Go's
    /// PUSH_REGS_HOST_TO_ABI0 entries do:
    ///
    ///   push rbp ; mov rbp, rsp
    ///   push rbx, rdi, rsi, r12, r13, r14, r15     what Windows' callee keeps and System V's may change, or
    ///                                              must keep for the unwind walk (RBX, R12-R15: Unwind)
    ///   sub rsp, 168 + out                         a word to align, XMM6-XMM15's 160 bytes, the body's stack arguments
    ///   movaps [rbp - 80 - 16j], xmm(6+j)          j = 0..9
    ///   the four register arguments homed in the caller's shadow space
    ///   ([rbp + 16 + 8k], the k-th's home; the fifth on is at [rbp + 48]
    ///   on, so every argument is then at [rbp + 16 + 8k]), and loaded from
    ///   there into System V's places (Conventions.SystemV), stack ones
    ///   written at [rsp + offset]
    ///   call body
    ///   movaps xmm(6+j), [rbp - 80 - 16j]
    ///   lea rsp, [rbp - 56] ; pop r15 ... rbx ; pop rbp ; ret
    ///
    /// The result is in RAX or XMM0 in both conventions. The epilogue is one
    /// of the forms Windows' unwinder knows (lea rsp,[frame]; pops; ret).
    /// Its unwind data: PUSH_NONVOL rbp; at `mov rbp, rsp` an allocation of
    /// 224 and SET_FPREG with RBP 224 above that base (Cranelift's form for a
    /// frame register set before the saves), so each save is at an offset
    /// from the base: SAVE_NONVOL for the seven pushes, SAVE_XMM128 for the
    /// ten XMM registers; whatever the stack pointer then does needs none.
    /// </summary>
    private void WindowsEntry(Function f, string body, Section text, ObjectFile obj, HashSet<string> defined, Section xdata, Section table,
                              Dictionary<string, string> blobs, List<string> errors)
    {
        ChunkedBytes b = text.Bytes;
        int gap = (FunctionAlign - b.Count % FunctionAlign) % FunctionAlign;
        Encoder.Nops(b, gap);
        int start = b.Count;
        void Bytes(params byte[] bytes) => b.AddRange(bytes);
        void D32(int v) => b.AddRange(BitConverter.GetBytes(v));
        int At() => b.Count - start;
        List<(int End, int Op, int Info)> codes = new();
        List<int> extra = new();
        void Code(int op, int info, int offset = 0) { codes.Add((At(), op, info)); extra.Add(offset); }

        const int saved = 7 * 8, xmmArea = 8 + 160, frame = saved + xmmArea;   // 224: RBP above the base by this
        IReadOnlyList<IrType> types = f.Params.Select(p => p.Type).ToList();
        Corsac.Lang.Calls.CallPlan plan = Corsac.Lang.X86.Conventions.SystemV.Arguments(types);
        int outgoing = (plan.StackBytes + 15) & ~15;

        Bytes(0x55); Code(0, 5);                                                    // push rbp
        Bytes(0x48, 0x89, 0xE5); Code(1, frame); Code(3, 0);                        // mov rbp, rsp: ALLOC(224) + SET_FPREG
        (int Reg, byte[] Push)[] pushes =
        {
            (3, new byte[] { 0x53 }), (7, new byte[] { 0x57 }), (6, new byte[] { 0x56 }),
            (12, new byte[] { 0x41, 0x54 }), (13, new byte[] { 0x41, 0x55 }), (14, new byte[] { 0x41, 0x56 }), (15, new byte[] { 0x41, 0x57 }),
        };
        for (int k = 0; k < pushes.Length; k++) { Bytes(pushes[k].Push); Code(4, pushes[k].Reg, frame - 8 * (k + 1)); }  // SAVE_NONVOL
        Bytes(0x48, 0x81, 0xEC); D32(xmmArea + outgoing);                           // sub rsp, 168 + out
        for (int j = 0; j < 10; j++)
        {
            int x = 6 + j, disp = -80 - 16 * j;
            if (x >= 8) Bytes(0x44);
            Bytes(0x0F, 0x29, (byte)(0x85 | (x & 7) << 3)); D32(disp);             // movaps [rbp + disp], xmmX
            Code(8, x, frame + disp);                                               // SAVE_XMM128, from the base
        }
        // The arguments: the four register ones homed, then each loaded from its home into System V's place.
        for (int k = 0; k < Math.Min(types.Count, 4); k++)
        {
            byte home = (byte)(16 + 8 * k);
            if (types[k].IsFloat()) Bytes(0xF2, 0x0F, 0x11, (byte)(0x45 | k << 3), home);          // movsd [rbp + home], xmmK
            else Bytes(k < 2 ? (byte)0x48 : (byte)0x4C, 0x89, (byte)(0x45 | (k == 0 ? 1 : k == 1 ? 2 : k == 2 ? 0 : 1) << 3), home);   // mov [rbp + home], rcx/rdx/r8/r9
        }
        for (int k = 0; k < types.Count; k++)
        {
            int home = 16 + 8 * k;
            Corsac.Lang.Calls.ArgPlace place = plan.Places[k];
            if (place.Kind == Corsac.Lang.Calls.PlaceKind.Float)
            {
                int x = place.Register;
                Bytes(types[k] == IrType.F32 ? (byte)0xF3 : (byte)0xF2);
                if (x >= 8) Bytes(0x44);
                Bytes(0x0F, 0x10, (byte)(0x85 | (x & 7) << 3)); D32(home);         // movss/movsd xmmX, [rbp + home]
            }
            else if (place.Kind == Corsac.Lang.Calls.PlaceKind.General)
            {
                int r = place.Register;
                Bytes((byte)(0x48 | (r >= 8 ? 4 : 0)), 0x8B, (byte)(0x85 | (r & 7) << 3)); D32(home);   // mov reg, [rbp + home]
            }
            else
            {
                Bytes(0x48, 0x8B, 0x85); D32(home);                                // mov rax, [rbp + home]
                Bytes(0x48, 0x89, 0x84, 0x24); D32(place.Offset);                  // mov [rsp + offset], rax
            }
        }
        Bytes(0xE8);                                                                // call body
        text.Relocs.Add(new Relocation(b.Count, body, -4, RelocKind.Plt32));
        D32(0);
        for (int j = 0; j < 10; j++)
        {
            int x = 6 + j;
            if (x >= 8) Bytes(0x44);
            Bytes(0x0F, 0x28, (byte)(0x85 | (x & 7) << 3)); D32(-80 - 16 * j);     // movaps xmmX, [rbp + disp]
        }
        Bytes(0x48, 0x8D, 0x65, unchecked((byte)-saved));                           // lea rsp, [rbp - 56]
        Bytes(0x41, 0x5F, 0x41, 0x5E, 0x41, 0x5D, 0x41, 0x5C, 0x5E, 0x5F, 0x5B);     // pop r15 r14 r13 r12 rsi rdi rbx
        Bytes(0x5D, 0xC3);                                                          // pop rbp ; ret
        int size = b.Count - start;
        obj.Symbols.Add(new Symbol { Name = f.Name, Section = text, Offset = start, Size = size, IsFunction = true, Global = f.Exported });
        defined.Add(f.Name);
        WriteUnwind(f.Name, size, codes, 5, xdata, table, blobs, obj, errors, frameOffset: frame / 16, extra: extra);
    }

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
    ///
    /// Not in a position-independent object: a kernel's module makes none,
    /// and calls its kernel's -- through their GOT slots
    /// (Selector.SequenceStub), or straight with relocated text
    /// (RelocatedText) -- for the range no thread is stopped inside is the
    /// kernel's.
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
}
