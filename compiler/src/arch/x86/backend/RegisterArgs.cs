#nullable enable
using Corsac.Lang.Ir;

namespace Corsac.Lang.X86;

/// <summary>
/// A FUNCTION'S ARGUMENTS IN REGISTERS: its first three machine words, in
/// the order of its parameters, in EAX, EDX and ECX (as GCC's regparm(3)
/// has a function of three, the order Linux's i386 kernel calls its own
/// functions in). Every other argument -- a fourth word and those after
/// it, and any that is no word, a 64-bit or a floating one, wherever it
/// stands -- is pushed, the last first, and taken off by the caller after.
/// Pushed, each argument was a store by the caller and a load by the
/// callee, and every call an `add esp, n` after it: on a processor whose
/// loads and stores are the most of what it waits for (the 486, the Geode
/// LX), and whose cache holds eight kilobytes of code.
///
/// IT IS THE ONE WAY A FUNCTION OF THIS LANGUAGE IS CALLED, by its name or
/// by its address: a direct call, a virtual or an interface call through a
/// descriptor, a delegate, a call through a word (Sys.Call). The function
/// has one entry, under its name, and nothing between a caller and its
/// body (as Linux's kernel built -mregparm=3, CoreCLR's managed convention
/// on x86 and Go's internal ABI have it: the register convention for every
/// call of the language's own, and the machine's C convention only where
/// foreign code is met).
///
/// Both sides decide from what each has in hand and nothing more -- the
/// callee from its parameters, the caller from the call's operands, which
/// say the same: so units compiled apart, and a module against its kernel,
/// agree with no table between them. A direct call whose operands do not
/// say what its callee's parameters do is refused where both are in hand
/// (X86Backend.Compile, a unit; Shape, Agree).
///
/// WHAT FOREIGN CODE ENTERS IS DECLARED SO. Not a function that pops its
/// own arguments (stdcall: CalleePops) nor one that keeps its caller's
/// registers (C enters it -- [UnmanagedCallersOnly]'s way in,
/// Function.KeepsCallerRegisters): those take every argument from the
/// stack, as C passes them, and so does the one marked [StackArguments],
/// by its name (`mc_`). Not a call into C (Sys.CallNative, NativeCall),
/// nor a machine intrinsic. Every other function this backend compiles is
/// called so, the ones the compiler makes among them (Managed). Assembly
/// that calls a function of this language by its address gives it its
/// first three words in EAX, EDX and ECX and pushes the rest.
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

    /// <summary>How many arguments go in registers at the most.</summary>
    public const int Most = 3;

    /// <summary>Whether a function takes any argument in a register at all: one this convention is for (Managed), entered by none of C's ways.</summary>
    private static bool Takes(Function f) => Managed(f.Name) && f.CalleePops == 0 && !f.KeepsCallerRegisters;

    /// <summary>How many of its arguments a function takes in registers: its first three words, or as many as it has.</summary>
    public static int Of(Function f)
    {
        if (!Takes(f)) return 0;
        int words = 0;
        foreach (VReg p in f.Params) if (p.Type == IrType.I32 && ++words == Most) break;
        return words;
    }

    /// <summary>
    /// Which register a function's parameter comes in -- 0, 1 or 2, the
    /// place in Order -- or -1, on the stack: the first three that are
    /// words, whatever stands between them.
    /// </summary>
    public static int Register(Function f, int parameter)
    {
        if (!Takes(f) || f.Params[parameter].Type != IrType.I32) return -1;
        int before = 0;
        for (int k = 0; k < parameter; k++) if (f.Params[k].Type == IrType.I32 && ++before == Most) return -1;
        return before;
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
    /// esp`, then every argument pushed again as C would have pushed them
    /// all, the last first -- one that came in a register from it, one
    /// that came on the stack from where it lies above this frame -- `call
    /// body`, `leave`, `ret`; returned with the offset of the call's field.
    /// </summary>
    public static (byte[] Code, int Field) StackEntry(Function f)
    {
        List<byte> code = new() { 0x55, 0x89, 0xE5 };
        // Where each argument that came on the stack lies: from [ebp+8] up,
        // in the order of the parameters.
        int[] at = new int[f.Params.Count];
        int offset = 8;
        for (int k = 0; k < f.Params.Count; k++)
        {
            if (Register(f, k) >= 0) continue;
            at[k] = offset;
            offset += Bytes(f.Params[k].Type);
        }
        void PushFrame(int displacement)
        {
            // push dword [ebp + d]: FF /6, a byte's displacement or four.
            if (displacement <= 127) { code.Add(0xFF); code.Add(0x75); code.Add((byte)displacement); }
            else { code.Add(0xFF); code.Add(0xB5); code.AddRange(BitConverter.GetBytes(displacement)); }
        }
        for (int k = f.Params.Count - 1; k >= 0; k--)
        {
            int register = Register(f, k);
            if (register >= 0) { code.Add((byte)(0x50 + (int)Order[register])); continue; }
            if (Bytes(f.Params[k].Type) == 8) PushFrame(at[k] + 4);
            PushFrame(at[k]);
        }
        code.Add(0xE8);
        int field = code.Count;
        code.AddRange(new byte[4]);
        code.Add(0xC9);
        code.Add(0xC3);
        return (code.ToArray(), field);
    }

    /// <summary>The bytes an argument of a type is on the stack.</summary>
    private static int Bytes(IrType type) => type is IrType.I64 or IrType.F64 ? 8 : 4;

    /// <summary>Where a call's arguments begin among its operands, or -1 for one this convention is not for (into C, an intrinsic, a [StackArguments] method by name).</summary>
    private static int First(Instr call)
    {
        if (call.Op == Opcode.Call) return call.Callee is { } callee && Managed(callee) ? 0 : -1;
        if (call.Op == Opcode.CallIndirect) return NativeCall.Is(call) ? -1 : 1;
        return -1;
    }

    /// <summary>
    /// How many of its arguments a call passes in registers: its first
    /// three words, or as many as it has. A call through an address as a
    /// call by name -- its operands after the address -- unless it is one
    /// into C (NativeCall).
    /// </summary>
    public static int Of(Instr call)
    {
        int first = First(call);
        if (first < 0) return 0;
        int words = 0;
        for (int k = first; k < call.Operands.Count; k++) if (call.Operands[k].Type == IrType.I32 && ++words == Most) break;
        return words;
    }

    /// <summary>
    /// The arguments a call finds set already, a bit each by its place among
    /// the call's own: one by the name of a function's way in with its
    /// commonest numbers (Function.ConstantEntry, the link's ArgumentFacts.
    /// Entries), which loads them in front of the function. Each still has
    /// its register and its place -- the call simply loads none of them.
    /// </summary>
    public static int Given(Instr call)
        => call.Op == Opcode.Call && call.Callee is { } callee ? Corsac.Lang.Lto.ArgumentFacts.EntryGiven(callee) : 0;

    /// <summary>
    /// The way in itself (Function.ConstantEntry): each parameter it sets
    /// given its number in its register -- `xor reg, reg` for a zero (the
    /// flags are nobody's at a call), `mov reg, imm32` for any other -- and
    /// nothing after, where the function begins where it ends; where the
    /// function's own name is on other code (`jump`: a body that keeps its
    /// arguments on the stack, StackEntry), a jump there, returned with the
    /// offset of its field (or -1).
    /// </summary>
    public static (byte[] Code, int Field) ConstantEntry(Function f, (int Parameter, long Value)[] given, bool jump)
    {
        List<byte> code = new();
        foreach ((int parameter, long value) in given)
        {
            int register = Register(f, parameter);
            if (register < 0) throw new InvalidOperationException($"{f.Name}: a way in sets parameter {parameter}, which comes in no register");
            int reg = (int)Order[register];
            if (value == 0) { code.Add(0x31); code.Add((byte)(0xC0 | reg << 3 | reg)); }
            else { code.Add((byte)(0xB8 + reg)); code.AddRange(BitConverter.GetBytes(unchecked((int)value))); }
        }
        int field = -1;
        if (jump) { code.Add(0xE9); field = code.Count; code.AddRange(new byte[4]); }
        return (code.ToArray(), field);
    }

    /// <summary>Which register a call's operand goes in (the place in Order), or -1: pushed. As Register(Function, int) has its parameters.</summary>
    public static int Register(Instr call, int operand)
    {
        int first = First(call);
        if (first < 0 || operand < first || call.Operands[operand].Type != IrType.I32) return -1;
        int before = 0;
        for (int k = first; k < operand; k++) if (call.Operands[k].Type == IrType.I32 && ++before == Most) return -1;
        return before;
    }

    /// <summary>
    /// A function's arguments as a caller must have them, in a word: `S`
    /// first where every one is on the stack whatever it is (Takes not),
    /// then a letter a parameter -- `w` a word, `q` eight bytes, `f` a
    /// four-byte float. Where each lies follows from the letters before it.
    /// </summary>
    public static string Shape(Function f)
    {
        System.Text.StringBuilder shape = new(f.Params.Count + 1);
        if (!Takes(f)) shape.Append('S');
        foreach (VReg p in f.Params) shape.Append(Letter(p.Type));
        return shape.ToString();
    }

    /// <summary>A direct call's arguments as it passes them, as Shape(Function) says a function's.</summary>
    public static string Shape(Instr call)
    {
        System.Text.StringBuilder shape = new(call.Operands.Count + 1);
        if (First(call) < 0) shape.Append('S');
        foreach (Operand o in call.Operands) shape.Append(Letter(o.Type));
        return shape.ToString();
    }

    private static char Letter(IrType type) => type switch { IrType.I32 => 'w', IrType.I64 or IrType.F64 => 'q', _ => 'f' };

    /// <summary>
    /// Whether a direct call and the function it names agree on where the
    /// arguments are (their Shapes): each parameter the call has an operand
    /// for is of the operand's kind, so that both count the same words into
    /// the same registers and the same bytes onto the stack -- and both
    /// take the registers, or neither. (More operands than parameters is no
    /// harm: a word a function does not read it does not miss, and Sys.Call
    /// passes three to whatever it is given. Fewer is the two's to settle
    /// where what is missing would have come in a register -- and refused
    /// where the function would read it from a stack nothing was pushed
    /// on. Who looks in a register and who on the stack is not theirs to
    /// settle, and nothing at the link would say so: the function has one
    /// name whichever it is.)
    /// </summary>
    public static bool Agree(string passes, string takes)
    {
        bool passesStack = passes.StartsWith('S'), takesStack = takes.StartsWith('S');
        ReadOnlySpan<char> operands = passes.AsSpan(passesStack ? 1 : 0), parameters = takes.AsSpan(takesStack ? 1 : 0);
        if (parameters.Length == 0) return true;
        if (passesStack != takesStack && operands.Length > 0) return false;
        int both = Math.Min(operands.Length, parameters.Length);
        if (!operands[..both].SequenceEqual(parameters[..both])) return false;
        // The parameters past the call's operands: each a word among the
        // function's first three, or it lies on the stack.
        int words = 0;
        for (int k = 0; k < parameters.Length; k++)
        {
            bool register = !takesStack && parameters[k] == 'w' && words++ < Most;
            if (k >= both && !register) return false;
        }
        return true;
    }

    /// <summary>The words of a refusal: a Shape, read out.</summary>
    public static string Said(string shape)
    {
        bool stack = shape.StartsWith('S');
        string kinds = string.Join(", ", shape.Skip(stack ? 1 : 0).Select(letter => letter switch { 'w' => "a word", 'q' => "eight bytes", _ => "a float" }));
        return (kinds.Length == 0 ? "no arguments" : kinds) + (stack ? ", all on the stack" : "");
    }
}
