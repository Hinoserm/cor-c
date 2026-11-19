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
/// IT IS THE ONE WAY A FUNCTION OF THIS LANGUAGE IS CALLED, by its name or
/// by its address: a direct call, a virtual or an interface call through a
/// descriptor, a delegate, a call through a word (Sys.Call) -- each puts the
/// arguments in the registers and the function, under its one name, finds
/// them there. There is no second entry that takes them from the stack and
/// no stub between a caller and a body (as Linux's kernel built -mregparm=3,
/// CoreCLR's managed convention on x86 and Go's internal ABI have it: the
/// register convention for every call of the language's own, and the
/// machine's C convention only where foreign code is met).
///
/// Both sides decide from what each has in hand and nothing more -- the
/// callee from its parameters, the caller from the call's operands, which
/// say the same: so units compiled apart, and a module against its kernel,
/// agree with no table between them. A direct call whose operands do not
/// say what its callee's parameters do is refused where both are in hand
/// (X86Backend.Compile, a unit; RegisterArgs.Agree).
///
/// WHAT FOREIGN CODE ENTERS IS DECLARED SO. Not a function that pops its
/// own arguments (stdcall: CalleePops) nor one that keeps its caller's
/// registers (C enters it -- [UnmanagedCallersOnly]'s way in,
/// Function.KeepsCallerRegisters): those take theirs from the stack, as C
/// passes them, and so does the one marked [StackArguments], by its name
/// (`mc_`). Not a call into C (Sys.CallNative, NativeCall), nor a machine
/// intrinsic. Every other function this backend compiles is called so,
/// the ones the compiler makes among them (Managed). Assembly that calls a function of this language by its
/// address gives it its first three words in EAX, EDX and ECX; a function
/// of four words or more, or with a 64-bit or a floating one among them,
/// has them all on the stack, the caller taking them off, as ever.
///
/// A word too many is no harm (Sys.Call passes three to a function of one
/// or two): the registers a function does not read it does not miss.
/// </summary>
internal static class RegisterArgs
{
    /// <summary>Where the arguments go, the first first.</summary>
    public static readonly Gpr[] Order = { Gpr.Eax, Gpr.Edx, Gpr.Ecx };

    /// <summary>The local name the entry of a function whose body keeps its arguments on the stack calls that body by (StackEntry).</summary>
    public static string StackBodyName(string name) => name + "$s";

    /// <summary>
    /// Whether a name is a function's that this convention is for: every
    /// one this backend compiles -- a method's (`m_`, `mx_`) and those the
    /// compiler makes beside them, which stand in descriptors' slots as
    /// methods do and are called through them as methods are (a box's way
    /// to its struct's member, `__box_call_`; an array's and a delegate's
    /// own, `__array`, `__delegate`) -- but the one marked to take its
    /// arguments from the stack ([StackArguments], `mc_`), a call into C
    /// (NativeCall) and a machine intrinsic.
    /// </summary>
    private static bool Managed(string name)
        => !name.StartsWith(StackArgumentsPrefix, StringComparison.Ordinal) && !name.StartsWith(NativeCall.Prefix, StringComparison.Ordinal)
            && !name.StartsWith(MachineIntrinsics.Prefix, StringComparison.Ordinal);

    /// <summary>What begins the label of a method marked [StackArguments] (Lowering.StackArgumentsPrefix).</summary>
    public const string StackArgumentsPrefix = "mc_";

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
    /// it, or a push or a pop), keeps them on the stack: what is under the
    /// function's name is then a few instructions that push them and call
    /// the body (StackEntry). Asked once a function, the answer kept on it
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
    /// What is under the name of a function whose body keeps its arguments
    /// on the stack (BodyInRegisters false), a function of its own with a
    /// frame, so that a walk of the frames finds it: `push ebp ; mov ebp,
    /// esp`, `push ecx ; push edx ; push eax` (as many as it takes, the last
    /// first), `call body`, `leave`, `ret`; returned with the offset of the
    /// call's field.
    /// </summary>
    public static (byte[] Code, int Field) StackEntry(int arguments)
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

    /// <summary>
    /// How many of its arguments a call passes in registers: all of them, or
    /// none (0). A call through an address as a call by name -- its operands
    /// after the address -- unless it is one into C (NativeCall).
    /// </summary>
    public static int Of(Instr call)
    {
        int first;
        if (call.Op == Opcode.Call)
        {
            if (call.Callee is not { } callee || !Managed(callee)) return 0;
            first = 0;
        }
        else if (call.Op == Opcode.CallIndirect)
        {
            if (NativeCall.Is(call)) return 0;
            first = 1;
        }
        else return 0;
        int count = call.Operands.Count - first;
        if (count is < 1 or > 3) return 0;
        for (int k = first; k < call.Operands.Count; k++) if (call.Operands[k].Type != IrType.I32) return 0;
        return count;
    }

    /// <summary>
    /// Whether a direct call and the function it names agree on where the
    /// arguments are: the function reads none where the call did not put
    /// them -- not in registers what was pushed, nor on the stack what went
    /// in registers. (Fewer or more operands than parameters is for the two
    /// to settle: a word a function does not read it does not miss, and
    /// Sys.Call passes three to whatever it is given. Who looks in a
    /// register and who on the stack is not, and nothing at the link would
    /// say so: the function has one name whichever it is.)
    /// </summary>
    public static bool Agree(int passes, int operands, int takes, int parameters)
        => takes > 0 ? passes > 0 || operands == 0 : parameters == 0 || passes == 0;

    /// <summary>The words of a refusal: where a call or a function has its arguments.</summary>
    public static string Where(int inRegisters, int all) => inRegisters > 0 ? inRegisters + " in registers" : all + " on the stack";
}
