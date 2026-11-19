#nullable enable
using Corsac.Lang.Ir;

namespace Corsac.Lang.X86;

/// <summary>
/// A FUNCTION'S ARGUMENTS IN REGISTERS, where it takes one to three words:
/// the first in EAX, the second in EDX, the third in ECX (GCC's regparm(3),
/// the order Linux's i386 kernel calls its own functions in). Pushed, each
/// argument was a store by the caller and a load by the callee, and every
/// call an `add esp, n` after it: 52,569 of those and 16,803 argument loads
/// in a K8 kernel, about 2.6% of its instructions, and the stores of every
/// push on top -- on a processor whose loads and stores are the most of what
/// it waits for (the Geode LX, the 486).
///
/// Every function this backend compiles that takes such arguments has two
/// entries. Its body is named EntryName(name) and finds its arguments in
/// the registers; a direct call -- the most of all calls -- goes there,
/// pushing nothing. Its own name is a stub that loads them from the stack
/// into the registers and runs on into the body, which begins where the
/// stub ends (no jump between them: Stub): what reaches a function by its
/// address, not by a call this backend made -- a virtual or an interface
/// call through a descriptor, a delegate, a hook the runtime or the kernel
/// calls through a word (Sys.AddressOf), C, assembly, another image -- calls
/// it as it always has and finds it as it always was. Both are decided from
/// what each side has in hand and nothing more -- the callee from its
/// parameters, the caller from the call's operands, which say the same: so
/// units compiled apart, and a module against its kernel, agree with no
/// table between them, and a call to a function without the entry is an
/// undefined symbol at the link, never a function entered wrongly.
///
/// Not a function that pops its own arguments (stdcall: CalleePops) nor one
/// that keeps its caller's registers (C enters it, Function
/// .KeepsCallerRegisters): C calls them, by their own names. Not a call into
/// C, nor a machine intrinsic, nor a callee without a managed name (`m_`,
/// `mx_`): those are no functions of this backend's.
/// </summary>
internal static class RegisterArgs
{
    /// <summary>What the body of a function taking its arguments in registers is named after.</summary>
    public const string EntrySuffix = Corsac.Lang.Lto.RuntimeAbi.RegisterEntrySuffix;

    /// <summary>Where the arguments go, the first first.</summary>
    public static readonly Gpr[] Order = { Gpr.Eax, Gpr.Edx, Gpr.Ecx };

    public static string EntryName(string name) => name + EntrySuffix;

    /// <summary>The local name a reverse stub calls its own body by (ReverseStub).</summary>
    public static string StackBodyName(string name) => name + "$s";

    private static bool Managed(string name)
        => name.StartsWith("m_", StringComparison.Ordinal) || name.StartsWith("mx_", StringComparison.Ordinal);

    /// <summary>How many of its arguments a function takes in registers: all of them, or none (0).</summary>
    public static int Of(Function f)
    {
        if (!Managed(f.Name) || f.Params.Count is < 1 or > 3 || f.CalleePops != 0 || f.KeepsCallerRegisters) return 0;
        foreach (VReg p in f.Params) if (p.Type != IrType.I32) return 0;
        return f.Params.Count;
    }

    /// <summary>
    /// Whether the function's own body takes its arguments in registers (Of
    /// it more than none) -- or, holding an asm block whose text names the
    /// stack (EBP or ESP: it may read them there with no operand to show
    /// it, or a push or a pop), keeps them on the stack: its register entry
    /// is then a stub the other way (ReverseStub), which pushes them and
    /// calls the body. Asked once a function, the answer kept on it
    /// (Function.RegisterBody).
    /// </summary>
    public static bool BodyInRegisters(Function f)
    {
        if (Of(f) == 0) return false;
        if (f.RegisterBody != 0) return f.RegisterBody == 1;
        bool inRegisters = true;
        foreach (Corsac.Lang.Ir.Block b in f.Blocks)
            foreach (Instr i in b.Instrs)
                if (i.Op == Opcode.Call && i.Callee is { } c && c.StartsWith(MachineIntrinsics.Asm, StringComparison.Ordinal) && NamesStack(c))
                    inRegisters = false;
        // (A word written whole: two workers asking at once write one answer.)
        f.RegisterBody = inRegisters ? (byte)1 : (byte)2;
        return inRegisters;
    }

    /// <summary>Whether an asm block's text names EBP or ESP (InlineAsm.Analysis.UsesStack); one it cannot read, taken as naming them.</summary>
    private static bool NamesStack(string callee)
    {
        try
        {
            Corsac.Asm.InlineAsm.Spec spec = Corsac.Asm.InlineAsm.Decode(callee[MachineIntrinsics.Asm.Length..]);
            return Corsac.Asm.InlineAsm.Analyze(spec.Text, spec.Bits, spec.Locals).UsesStack;
        }
        catch (Exception)
        {
            return true;
        }
    }

    /// <summary>
    /// The register entry of a function whose body keeps its arguments on the
    /// stack (BodyInRegisters false), a function of its own with a frame, so
    /// that a walk of the frames finds it (its frame-table entry the
    /// function's display name): `push ebp ; mov ebp, esp`, `push ecx ; push
    /// edx ; push eax` (as many as it takes, the last first), `call body`,
    /// `leave`, `ret`; returned with the offset of the call's field.
    /// </summary>
    public static (byte[] Code, int Field) ReverseStub(int arguments)
    {
        List<byte> code = new() { 0x55, 0x89, 0xE5 };
        for (int k = arguments - 1; k >= 0; k--) code.Add((byte)(0x50 + (int)Order[k]));
        code.Add(0xE8);
        int field = code.Count;
        code.AddRange(new byte[4]);
        code.Add(0xC9);
        code.Add(0xC3);
        return (code.ToArray(), field);
    }

    /// <summary>How many of its arguments a call passes in registers: all of them, or none (0).</summary>
    public static int Of(Instr call)
    {
        if (call.Op != Opcode.Call || call.Callee is not { } callee || !Managed(callee) || call.Operands.Count is < 1 or > 3) return 0;
        foreach (Operand o in call.Operands) if (o.Type != IrType.I32) return 0;
        return call.Operands.Count;
    }

    /// <summary>
    /// The stub under a function's own name: each argument loaded from the
    /// stack into its register, four bytes a load, and nothing after them --
    /// the body is laid down where the stub ends (X86Backend.Compile), so a
    /// call by the function's address runs the loads and is in the body. A
    /// jump there was five bytes more and, the stub a function of its own,
    /// the alignment gap after it: 16 or 32 bytes a function at the K8's
    /// alignment, where the loads alone lie in the gap the body's alignment
    /// leaves before it as often as not.
    /// </summary>
    public static byte[] Stub(int arguments)
    {
        byte[] code = new byte[4 * arguments];
        for (int k = 0; k < arguments; k++)
        {
            // mov reg, [esp + 4 + 4k]: 8B /r, ModRM mod 01 r/m 100 (a SIB), SIB [esp], disp8.
            code[4 * k] = 0x8B;
            code[4 * k + 1] = (byte)(0x44 | ((int)Order[k] << 3));
            code[4 * k + 2] = 0x24;
            code[4 * k + 3] = (byte)(4 + 4 * k);
        }
        return code;
    }
}
