#nullable enable
using Corsac.Lang.Ir;
using Corsac.Lang.X86;

namespace Corsac.Lang.Lower;

/// <summary>
/// WINDOWS (docs/WIN32-TARGET.md): what a compile for a Windows image does
/// differently. A [DllImport] calls the DLL's function through the image's
/// import directory, named `dll!export` (Pe.WindowsImports). A DLL has no
/// Main: the loader enters it at DllMain, and C enters it at the functions it
/// exports, each a `[UnmanagedCallersOnly(EntryPoint = "...")]` method, as
/// .NET's native AOT exports them.
/// </summary>
public sealed partial class Lowering
{
    /// <summary>The image is a Windows one: PE32, its imports through the import directory.</summary>
    public static bool Windows { get; set; }

    /// <summary>The image is a Windows DLL: entered at DllMain and at its exports, never at Main.</summary>
    public static bool Dll { get; set; }

    /// <summary>A DLL's entry, which the loader calls: DllMain.</summary>
    public const string DllEntryName = "__corsac_dll_main";

    /// <summary>
    /// The name C calls a method by, when it is one an image exports:
    /// `[UnmanagedCallersOnly(EntryPoint = "glVertex3f")]`.
    /// </summary>
    internal static string? ExportName(MethodSymbol m)
        => m.Decl is MemberDecl d
            ? d.Attributes.FirstOrDefault(a => a.Target.Length == 0 && a.Is("UnmanagedCallersOnly"))?.Named("EntryPoint")
            : null;

    /// <summary>The symbol a call to a DLL's function names: `kernel32.dll!VirtualAlloc`.</summary>
    internal static string WindowsImport(string library, string export) => Corsac.Lang.Pe.WindowsImports.Symbol(library, export);

    /// <summary>
    /// A DLL'S ROOTS: every method it exports, lowered whether or not anything
    /// in it calls one, and recorded for the link (NativeExports). An export
    /// named twice is refused here, where both declarations can be named.
    /// </summary>
    private List<MethodSymbol> RootExports()
    {
        List<MethodSymbol> exports = new();
        Dictionary<string, MethodSymbol> byName = new(StringComparer.Ordinal);
        foreach (TypeSymbol t in _b.Types.Values.ToList())
        {
            if (t.MembersPending) continue;
            foreach (MethodSymbol m in t.Methods)
            {
                if (ExportName(m) is not string export || !Emits(m)) continue;
                Node at = (Node?)m.Decl ?? new Block();
                if (export.Length == 0)
                {
                    Error(at, $"'{m.Name}': an export's EntryPoint names it; an empty one names nothing");
                    continue;
                }
                if (byName.TryGetValue(export, out MethodSymbol? other))
                {
                    Error(at, $"'{m.Owner.Name}.{m.Name}' and '{other.Owner.Name}.{other.Name}' are both exported as '{export}'");
                    continue;
                }
                byName[export] = m;
                Require(m);
                exports.Add(m);
                _m.NativeExports.Add((export, CallLabel(m)));
            }
        }
        if (exports.Count == 0)
        {
            Error(new Block(), "a DLL exports at least one method: [UnmanagedCallersOnly(EntryPoint = \"Name\")]");
        }
        return exports;
    }

    /// <summary>The platform library's class (runtime/src/platforms/win32/win32.cor).</summary>
    private const string Win32Type = "Win32";

    /// <summary>A static method of the Windows platform library, or null when it is not linked.</summary>
    private MethodSymbol? Win32Method(string name, int arity = 0)
        => _b.Types.TryGetValue(Win32Type, out TypeSymbol? t)
            ? t.Methods.FirstOrDefault(m => m.Name == name && m.Static && m.Params.Count == arity)
            : null;

    /// <summary>The SEH registration the method being lowered made on its way in from C, or null.</summary>
    private FrameSlot? _sehRecord;

    /// <summary>
    /// A FAULT IN OUR CODE IS AN EXCEPTION, as it is in .NET: a way in from C
    /// on Windows puts an SEH registration -- the chain's link, then the
    /// handler (Win32.Fault) -- in a slot of its own frame and makes it the
    /// head of the thread's chain, fs:[0]. Windows 2000 has no vectored
    /// handlers, so this is how an access violation in the method, or in
    /// anything it calls, reaches the runtime, which sends a null one on as
    /// a NullReferenceException. Taken off again on the way out (SehLeave).
    /// </summary>
    private void SehEnter()
    {
        _sehRecord = null;
        if (!Windows || Win32Method("Fault", 4) is not MethodSymbol fault) return;
        Require(fault);
        int w = _t.WordSize;
        FrameSlot slot = _f.NewSlot(2 * w, w, "seh");
        VReg record = _e.SlotAddress(slot);
        VReg next = _e.Call(MachineIntrinsics.SehChain, IrTypes.Word)!;
        _e.Store(record, next, 0);
        _e.Store(record, _e.Address(CallLabel(fault)), w);
        _e.Call(MachineIntrinsics.SetSehChain, IrType.Void, RegOperand.Of(record));
        _sehRecord = slot;
    }

    /// <summary>The registration SehEnter made taken off the chain: its link the head again.</summary>
    private void SehLeave()
    {
        if (_sehRecord is not FrameSlot slot) return;
        VReg next = _e.Load(IrTypes.Word, RegOperand.Of(_e.SlotAddress(slot)), 0);
        _e.Call(MachineIntrinsics.SetSehChain, IrType.Void, RegOperand.Of(next));
        _sehRecord = null;
    }

    /// <summary>
    /// A DLL'S ENTRY: DllMain(instance, reason, reserved), stdcall, which the
    /// loader calls as a process and its threads come and go. At
    /// DLL_PROCESS_ATTACH the platform library takes a TLS slot and puts this
    /// thread's block in it (Win32.Attach) -- answering FALSE, which fails the
    /// load, when it cannot -- and the thread is the collector's, the module
    /// initialisers run, and what wants the heap is made (Win32.Started);
    /// then the thread goes back to C counted as in C, as an export leaves
    /// it. At DLL_THREAD_DETACH a thread's block goes back, and at
    /// DLL_PROCESS_DETACH the slot. It takes the address
    /// of every export, the way a program hands C a callback, so each is kept
    /// and is analysed as what it is: a function called by code the compiler
    /// never sees.
    /// </summary>
    private void EmitDllEntry(List<MethodSymbol> exports)
    {
        Function f = new(DllEntryName, IrTypes.Word) { CalleePops = 3 * Target.Current.WordSize };
        VReg instance = f.NewReg(IrTypes.Word, "instance");
        f.Params.Add(instance);
        VReg reason = f.NewReg(IrTypes.Word, "reason");
        f.Params.Add(reason);
        f.Params.Add(f.NewReg(IrTypes.Word, "reserved"));
        Builder e = new(f, f.NewBlock("entry"));
        foreach (MethodSymbol m in exports)
        {
            e.Call(MachineIntrinsics.KeepAlive, IrType.Void, RegOperand.Of(e.Address(CallLabel(m))));
        }
        var attach = f.NewBlock("attach");
        var other = f.NewBlock("other");
        var detach = f.NewBlock("detach");
        var yes = f.NewBlock("yes");
        var no = f.NewBlock("no");
        e.Branch(e.Binary(Opcode.Eq, RegOperand.Of(reason), new ImmOperand(1, IrTypes.Word), IrTypes.Word), attach, other);

        e.SetBlock(attach);
        if (Dynamic)
        {
            EmitImageAttach(e, instance);
            e.Jump(yes);
        }
        else if (Win32Method("Attach", 1) is not MethodSymbol take || Win32Method("Started") is not MethodSymbol started)
        {
            Error(new Block(), "a DLL needs the Windows platform library (runtime/src/platforms/win32/win32.cor)");
            e.Jump(no);
        }
        else
        {
            Require(take);
            var run = f.NewBlock("run");
            VReg ok = e.Call(CallLabel(take), IrType.I32, RegOperand.Of(e.Unary(Opcode.ZExt32, instance)))!;
            e.Branch(ok, run, no);
            e.SetBlock(run);
            if (RuntimeKnobs.Count > 0 && RuntimeMethod("UseKnobs", 1) is MethodSymbol knobs)
            {
                Require(knobs);
                e.Call(CallLabel(knobs), IrType.Void, R(e.Address(InternString(string.Join('\0', RuntimeKnobs) + "\0"))));
            }
            if (RuntimeMethod("StartThread", 0) is MethodSymbol join)
            {
                Require(join);
                e.Call(CallLabel(join), IrType.Void);
            }
            // THE RUNTIME DLL introduces itself as a shared object does.
            if (SharedObject) e.Call(SharedInitName, IrType.Void);
            e.Call(Corsac.Lang.Elf.Linker.ProgramInitName, IrType.Void);
            Require(started);
            e.Call(CallLabel(started), IrType.Void);
            if (RuntimeMethod("EnterNative", 0) is MethodSymbol enter)
            {
                Require(enter);
                e.Call(CallLabel(enter), IrType.Void);
            }
            e.Jump(yes);
        }

        e.SetBlock(other);
        var threadGoes = f.NewBlock("thread_detach");
        var notProcess = f.NewBlock("not_process");
        e.Branch(e.Binary(Opcode.Eq, RegOperand.Of(reason), new ImmOperand(0, IrTypes.Word), IrTypes.Word), detach, notProcess);
        e.SetBlock(detach);
        if (!Dynamic && Win32Method("Detach") is MethodSymbol let)
        {
            Require(let);
            e.Call(CallLabel(let), IrType.Void);
        }
        e.Jump(yes);

        // DLL_THREAD_DETACH: a thread given a block lets it go (Win32.ThreadDetach).
        e.SetBlock(notProcess);
        e.Branch(e.Binary(Opcode.Eq, RegOperand.Of(reason), new ImmOperand(3, IrTypes.Word), IrTypes.Word), threadGoes, yes);
        e.SetBlock(threadGoes);
        if (!Dynamic && Win32Method("ThreadDetach") is MethodSymbol leave)
        {
            Require(leave);
            e.Call(CallLabel(leave), IrType.Void);
        }
        e.Jump(yes);

        e.SetBlock(yes);
        e.Ret(new ImmOperand(1, IrTypes.Word));
        e.SetBlock(no);
        e.Ret(new ImmOperand(0, IrTypes.Word));
        _m.Functions.Add(f);
        // The runtime DLL is a library to every pass, which keeps all of it;
        // the link alone is told where the loader enters (Driver).
        if (!_library) _m.Entry = DllEntryName;
    }

    /// <summary>
    /// AN IMAGE OVER THE RUNTIME DLL STARTS (--windows --dynamic): the runtime
    /// DLL's DllMain has already run -- the loader starts an image's imports
    /// first -- and owns the TLS slot, the heap and the threads. This image
    /// has the words its imports' addresses go in filled first, before any
    /// of its code reads one (Win32.FillImports, Linker DataImports); then
    /// introduces its statics and frames to the runtime, as a shared object
    /// does (Runtime.BeginImage); runs its module initialisers; and stays
    /// loaded, as the runtime does, since the runtime now holds its statics.
    /// </summary>
    private void EmitImageAttach(Builder e, VReg instance)
    {
        if (Win32Method("FillImports", 3) is not MethodSymbol fill || Win32Method("PinImage", 1) is not MethodSymbol pin)
        {
            Error(new Block(), "an image over the runtime DLL needs Win32.FillImports and Win32.PinImage (runtime/src/platforms/win32/win32.cor)");
            return;
        }
        VReg image = e.Unary(Opcode.ZExt32, instance);
        Require(fill);
        e.Call(CallLabel(fill), IrType.Void, RegOperand.Of(image),
            RegOperand.Of(WordAddress(e, Corsac.Lang.Elf.Linker.DataImportsStart)), RegOperand.Of(WordAddress(e, Corsac.Lang.Elf.Linker.DataImportsEnd)));
        EmitBeginImage(e);
        e.Call(Corsac.Lang.Elf.Linker.ProgramInitName, IrType.Void);
        Require(pin);
        e.Call(CallLabel(pin), IrType.Void, RegOperand.Of(image));
    }
}
