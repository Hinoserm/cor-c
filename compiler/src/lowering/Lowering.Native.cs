#nullable enable
using Corsac.Lang.Ir;
using Corsac.Lang.X86;

namespace Corsac.Lang.Lower;

/// <summary>
/// CALLS INTO C: a `static extern` method with [DllImport] (or .NET 7's
/// [LibraryImport]) is given a body here that calls the C function it names.
///
///   [DllImport("libc.so.6", EntryPoint = "strlen")]
///   static extern nint Length(string s);
///
/// Both targets' own calling conventions are C's -- cdecl on i386, System V
/// on x86-64 -- so the call itself is an ordinary call to the C symbol
/// through the PLT (Ir.NativeCall marks it, for the few things a C function
/// may assume that this compiler's own functions do not). What the body adds
/// around it is .NET's marshalling for the types that have an obvious C
/// form, and the handshake with the collector:
///
///   - a string goes as a NUL-terminated UTF-8 copy (const char *), an array
///     of numbers as the address of its first element, `ref`/`out` of a
///     number as the variable's address, a bool as a 32-bit 0 or 1 (Win32's
///     BOOL, .NET's default); numbers, pointers, nint and enums as they are;
///   - Runtime.EnterNative before and LeaveNative after: the thread counts as
///     stopped while C runs, so a collection does not wait for it, and on
///     i386 GS holds the C library's thread pointer instead of the runtime's;
///   - every object C was handed a pointer into is used again after the call
///     (the keepalive intrinsic), so it is live across it: in this frame, or
///     in a callee-saved register, which C preserves and which the thread's
///     block holds a copy of (Tls.Spill) while it counts as stopped;
///   - SetLastError = true reads errno before GS goes back, and keeps it
///     where Marshal.GetLastPInvokeError finds it.
///
/// A result comes back as .NET would: a bool from a 32-bit int (or a byte,
/// with [return: MarshalAs(UnmanagedType.I1)] or U1), a narrow integer
/// narrowed again because C leaves the upper bits undefined.
/// </summary>
public sealed partial class Lowering
{
    /// <summary>What a [DllImport] extern names: the library, the C symbol, and its options.</summary>
    internal sealed record NativeImport(string Library, string Symbol, bool SetLastError, bool Transition, bool ByteBool);

    /// <summary>The C function a method stands for, or null when it is not a native import.</summary>
    internal static NativeImport? NativeImportOf(MethodSymbol m)
    {
        if (m.Decl is not MethodDecl d || !d.Mods.HasFlag(Mods.Extern))
        {
            return null;
        }
        foreach (AttributeRef a in d.Attributes)
        {
            if (a.Target.Length == 0 && (a.Is("DllImport") || a.Is("LibraryImport")) && a.Argument is { Length: > 0 } library)
            {
                bool byteBool = d.Attributes.Any(r => r.Target == "return" && r.Is("MarshalAs")
                    && r.Argument is { } kind && (kind.EndsWith("I1", StringComparison.Ordinal) || kind.EndsWith("U1", StringComparison.Ordinal)));
                return new NativeImport(library, a.Named("EntryPoint") ?? m.Name, a.Says("SetLastError"),
                    !d.Attributes.Any(r => r.Target.Length == 0 && r.Is("SuppressGCTransition")), byteBool);
            }
        }
        return null;
    }

    /// <summary>Whether anything this compilation emits calls into C, so the program must be linked dynamically.</summary>
    private bool AnyNativeImports()
    {
        foreach (TypeSymbol t in _b.Types.Values)
        {
            foreach (MethodSymbol m in t.Methods)
            {
                if (NativeImportOf(m) is not null && Emits(m))
                {
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>The body of a native import: marshal, call, unmarshal. Leaves the builder closed.</summary>
    private void EmitNativeBody(MethodSymbol m, NativeImport import)
    {
        _m.NativeLibraries.Add(import.Library);
        Node at = (Node?)m.Decl ?? new Block();

        if (!m.Static)
        {
            Error(at, $"'{m.Name}': a [DllImport] method is static");
        }

        List<Operand> args = new();
        List<VReg> keep = new();
        for (int i = 0; i < m.Params.Count; i++)
        {
            ParamSymbol p = m.Params[i];
            VReg v = _params[i];
            Type t = p.Type;

            if (p.ByRef)
            {
                // The variable's address, which is what C's pointer is.
                if (!Blittable(t))
                {
                    Error(at, $"'{m.Name}': a ref or out {t} cannot be passed to C; only numbers, pointers and nint can");
                }
                args.Add(new RegOperand(v));
                continue;
            }

            if (t.Prim == Prim.String && !t.IsArray)
            {
                VReg? bytes = CallRuntime(at, "NativeString", IrTypes.Word, v);
                if (bytes is null) return;
                keep.Add(bytes);
                args.Add(new RegOperand(CallRuntime(at, "NativeData", IrTypes.Word, bytes)!));
                continue;
            }

            if (t.IsArray)
            {
                if (t.ArrayRank != 1 || t.Element is null || !Blittable(t.Element))
                {
                    Error(at, $"'{m.Name}': a {t} cannot be passed to C; an array of numbers can");
                }
                keep.Add(v);
                VReg? data = CallRuntime(at, "NativeData", IrTypes.Word, v);
                if (data is null) return;
                args.Add(new RegOperand(data));
                continue;
            }

            if (t.Prim == Prim.Bool || Blittable(t))
            {
                args.Add(new RegOperand(v));
                continue;
            }

            Error(at, $"'{m.Name}': a {t} cannot be passed to C; pass a number, a pointer, nint, a string, or an array of numbers");
        }

        if (import.Transition && RequireRuntime(at, "EnterNative", 0, "a call into C") is MethodSymbol enter)
        {
            _e.Call(CallLabel(enter), IrType.Void);
        }

        Type ret = m.Returns;
        IrType returns = ret.IsVoid ? IrType.Void
            : ret.Prim == Prim.Bool ? IrType.I32
            : IrTypes.Of(ret);
        if (!ret.IsVoid && ret.Prim != Prim.Bool && !Blittable(ret))
        {
            Error(at, $"'{m.Name}': C cannot return a {ret}; return nint (IntPtr) and read it with Marshal");
        }
        VReg? result = _e.Call(NativeCall.Of(import.Symbol), returns, args.ToArray());

        // errno is the C library's, per thread, behind its thread pointer:
        // read while GS is still the C library's on i386.
        VReg? errno = null;
        if (import.SetLastError)
        {
            _m.NativeLibraries.Add("libc.so.6");
            VReg? location = _e.Call(NativeCall.Of("__errno_location"), IrTypes.Word);
            errno = Numbered(_e, _e.Load(IrType.I32, new RegOperand(location!), 0));
        }

        if (import.Transition && RequireRuntime(at, "LeaveNative", 0, "a call into C") is MethodSymbol leave)
        {
            _e.Call(CallLabel(leave), IrType.Void);
        }

        if (errno is not null && RequireRuntime(at, "SetNativeError", 1, "SetLastError") is MethodSymbol setError)
        {
            _e.Call(CallLabel(setError), IrType.Void, new RegOperand(errno));
        }

        foreach (VReg alive in keep)
        {
            _e.Call(MachineIntrinsics.KeepAlive, IrType.Void, new RegOperand(alive));
        }

        if (result is not null && _returnValue is not null)
        {
            VReg shaped;
            if (ret.Prim == Prim.Bool)
            {
                VReg raw = import.ByteBool ? _e.Unary(Opcode.ZExt8, result) : result;
                shaped = _e.Binary(Opcode.Ne, new RegOperand(raw), new ImmOperand(0, IrType.I32), IrType.I32);
            }
            else if (ret.Prim is Prim.I8 or Prim.U8 or Prim.I16 or Prim.U16 or Prim.Char)
            {
                // C leaves the bits above a narrow result undefined.
                shaped = Narrow(_e, result, ret, ret);
            }
            else
            {
                shaped = result;
            }
            _e.CopyTo(_returnValue, new RegOperand(shaped));
        }
        _e.Jump(_returnBlock);
    }

    /// <summary>
    /// A call through a function pointer: the arguments as its parameters
    /// say, and for an unmanaged one (C's) the same handshake a [DllImport]
    /// makes. Function pointers carry no marshalling, in .NET or here: what
    /// goes through one is already what C takes.
    /// </summary>
    private VReg EmitPointerCall(CallExpr call, FunctionPointer pointer)
    {
        VReg target = Eval(call.Target);
        List<Operand> args = new();
        for (int i = 0; i < call.Args.Count; i++)
        {
            args.Add(new RegOperand(EvalAs(call.Args[i], pointer.Params[i])));
        }
        Type ret = pointer.Returns;
        IrType returns = ret.IsVoid ? IrType.Void : ret.Prim == Prim.Bool && pointer.Unmanaged ? IrType.I32 : IrTypes.Of(ret);
        if (pointer.Unmanaged && RequireRuntime(call, "EnterNative", 0, "a call into C") is MethodSymbol enter)
        {
            _e.Call(CallLabel(enter), IrType.Void);
        }
        VReg? result = _e.CallIndirect(new RegOperand(target), returns, args, pointer.Unmanaged ? NativeCall.Indirect : null);
        if (pointer.Unmanaged && RequireRuntime(call, "LeaveNative", 0, "a call into C") is MethodSymbol leave)
        {
            _e.Call(CallLabel(leave), IrType.Void);
        }
        if (result is null)
        {
            return Void();
        }
        if (pointer.Unmanaged && ret.Prim == Prim.Bool)
        {
            return _e.Binary(Opcode.Ne, new RegOperand(result), new ImmOperand(0, IrType.I32), IrType.I32);
        }
        if (pointer.Unmanaged && ret.Prim is Prim.I8 or Prim.U8 or Prim.I16 or Prim.U16 or Prim.Char)
        {
            return Narrow(_e, result, ret, ret);
        }
        return result;
    }

    /// <summary>Whether C calls this method: [UnmanagedCallersOnly].</summary>
    internal static bool CalledByC(MethodSymbol m)
        => m.Decl is MemberDecl d && d.Attributes.Any(a => a.Target.Length == 0 && a.Is("UnmanagedCallersOnly"));

    /// <summary>
    /// The way in to a method C calls: GS back to the runtime's on i386 and
    /// running again (Runtime.ReturnFromC), and the thread's handler chain
    /// emptied for the length of the call, so an exception that escapes it
    /// meets no handler -- the process ends, as .NET's does -- rather than
    /// being thrown past the C frames between here and the last `try`.
    /// Answers the chain to put back on the way out.
    /// </summary>
    private VReg? EnterFromC(Node at)
    {
        if (RequireRuntime(at, "ReturnFromC", 0, "a method C calls") is MethodSymbol back)
        {
            _e.Call(CallLabel(back), IrType.Void);
        }
        int w = _t.WordSize;
        VReg chain = _e.Load(IrTypes.Word, ThreadBlockNow(), TlsHandler / 4 * w);
        _e.Store(new RegOperand(ThreadBlockNow()), new ImmOperand(0, IrTypes.Word), TlsHandler / 4 * w);
        return chain;
    }

    /// <summary>The way back out to C: the handler chain as it was, and the thread counted as in C again.</summary>
    private void LeaveToC(Node at, VReg chain)
    {
        int w = _t.WordSize;
        _e.Store(new RegOperand(ThreadBlockNow()), new RegOperand(chain), TlsHandler / 4 * w);
        if (RequireRuntime(at, "EnterNative", 0, "a method C calls") is MethodSymbol enter)
        {
            _e.Call(CallLabel(enter), IrType.Void);
        }
    }

    /// <summary>A value C can take as it is: a number, a char, an enum, a pointer or nint.</summary>
    private static bool Blittable(Type t)
        => !t.IsArray && (t.IsNumeric || t.IsNative || t.IsPointer || t.Prim == Prim.Char
            || t.Symbol is { Kind: TypeKind.Enum });

    /// <summary>A call to a runtime hook with one word argument, or null (with the error said) when it is missing.</summary>
    private VReg? CallRuntime(Node at, string name, IrType returns, VReg argument)
    {
        if (RequireRuntime(at, name, 1, "a call into C") is not MethodSymbol hook)
        {
            return null;
        }
        return _e.Call(CallLabel(hook), returns, new RegOperand(argument));
    }
}
