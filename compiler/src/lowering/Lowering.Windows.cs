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

    /// <summary>
    /// A DLL'S ENTRY: DllMain(instance, reason, reserved), stdcall, which the
    /// loader calls as a process and its threads come and go and which
    /// answers TRUE. It takes the address of every export, the way a program
    /// hands C a callback, so each is kept and is analysed as what it is: a
    /// function called by code the compiler never sees.
    /// </summary>
    private void EmitDllEntry(List<MethodSymbol> exports)
    {
        Function f = new(DllEntryName, IrTypes.Word) { CalleePops = 3 * Target.Current.WordSize };
        f.Params.Add(f.NewReg(IrTypes.Word, "instance"));
        f.Params.Add(f.NewReg(IrTypes.Word, "reason"));
        f.Params.Add(f.NewReg(IrTypes.Word, "reserved"));
        Builder e = new(f, f.NewBlock("entry"));
        foreach (MethodSymbol m in exports)
        {
            e.Call(MachineIntrinsics.KeepAlive, IrType.Void, RegOperand.Of(e.Address(CallLabel(m))));
        }
        e.Ret(new ImmOperand(1, IrTypes.Word));
        _m.Functions.Add(f);
        _m.Entry = DllEntryName;
    }
}
