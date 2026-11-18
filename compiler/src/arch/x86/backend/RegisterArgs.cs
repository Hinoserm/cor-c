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
/// into the registers and jumps to the body: what reaches a function by its
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

    private static bool Managed(string name)
        => name.StartsWith("m_", StringComparison.Ordinal) || name.StartsWith("mx_", StringComparison.Ordinal);

    /// <summary>How many of its arguments a function takes in registers: all of them, or none (0).</summary>
    public static int Of(Function f)
    {
        if (!Managed(f.Name) || f.Params.Count is < 1 or > 3 || f.CalleePops != 0 || f.KeepsCallerRegisters) return 0;
        foreach (VReg p in f.Params) if (p.Type != IrType.I32) return 0;
        return f.Params.Count;
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
    /// stack into its register, and a jump to the body. Four bytes a load,
    /// five the jump; returned with the offset of the jump's field.
    /// </summary>
    public static (byte[] Code, int Field) Stub(int arguments)
    {
        List<byte> code = new();
        for (int k = 0; k < arguments; k++)
        {
            // mov reg, [esp + 4 + 4k]: 8B /r, ModRM mod 01 r/m 100 (a SIB), SIB [esp], disp8.
            code.Add(0x8B);
            code.Add((byte)(0x44 | ((int)Order[k] << 3)));
            code.Add(0x24);
            code.Add((byte)(4 + 4 * k));
        }
        code.Add(0xE9);
        int field = code.Count;
        code.AddRange(new byte[4]);
        return (code.ToArray(), field);
    }
}
