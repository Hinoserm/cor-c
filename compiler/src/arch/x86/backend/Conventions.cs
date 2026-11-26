#nullable enable
using Corsac.Lang.Calls;
using Corsac.Lang.Ir;

namespace Corsac.Lang.X86;

/// <summary>The x86 family's calling conventions (Corsac.Lang.Calls.Convention), and the one the mode compiles for.</summary>
internal static class Conventions
{
    public static readonly Convention SystemV = new SystemVConvention();
    public static readonly Convention I386 = new I386Convention();

    /// <summary>The convention of the code being selected: System V's in long mode, i386's own otherwise.</summary>
    public static Convention Current => X86Mode.Long ? SystemV : I386;

    /// <summary>
    /// SYSTEM V'S FOR x86-64, as this compiler's values use it: an integer
    /// or a reference in RDI, RSI, RDX, RCX, R8, R9, a float in XMM0 to
    /// XMM7, each class counted alone, and what is left over on the stack
    /// in order, a word each; RAX or XMM0 back. The stack is at sixteen at
    /// a call.
    /// </summary>
    private sealed class SystemVConvention : Convention
    {
        public override string Name => "System V x86-64";
        public override int StackAlignment => 16;
        public override int StackArgumentsAt => 16;
        // A variadic callee reads in AL how many vector registers carry arguments (the psABI, 3.5.7).
        public override bool NativeCountsVectorArguments => true;

        public override CallPlan Arguments(IReadOnlyList<IrType> types, bool registers = true)
        {
            ArgPlace[] places = new ArgPlace[types.Count];
            int generals = 0, floats = 0, stack = 0;
            for (int k = 0; k < types.Count; k++)
            {
                bool isFloat = types[k].IsFloat();
                int bytes = types[k] is IrType.I32 or IrType.F32 ? 4 : 8;
                if (isFloat && floats < LongAbi.FloatArgRegs) places[k] = ArgPlace.InFloat(floats++, bytes);
                else if (!isFloat && generals < LongAbi.IntArgRegs.Length) places[k] = ArgPlace.InGeneral((int)LongAbi.IntArgRegs[generals++], bytes);
                else
                {
                    places[k] = ArgPlace.OnStack(stack, bytes);
                    stack += 8;
                }
            }
            return new CallPlan { Places = places, StackBytes = stack, Generals = generals, Floats = floats };
        }

        // (Made once: a function's every return and call asks.)
        private static readonly ArgPlace[] None = [], Word = [ArgPlace.InGeneral((int)Gpr.Rax, 4)], Quad = [ArgPlace.InGeneral((int)Gpr.Rax, 8)],
            Single = [ArgPlace.InFloat(0, 4)], Double = [ArgPlace.InFloat(0, 8)];

        public override ArgPlace[] Result(IrType type) => type switch
        {
            IrType.Void => None,
            IrType.I32 => Word,
            IrType.F32 => Single,
            IrType.F64 => Double,
            _ => Quad,
        };
    }

    /// <summary>
    /// i386'S OWN: the first three arguments that are words in EAX, EDX
    /// and ECX, whatever stands between them (RegisterArgs, which says
    /// which functions and calls are of this form), every other on the
    /// stack in order -- a word, or two for a 64-bit integer or a double.
    /// Its stack-only form is cdecl's layout: a call into C, a method
    /// marked [StackArguments], a function entered some other way. A word
    /// comes back in EAX, two in EDX:EAX, a float on the x87's stack. This
    /// backend's own calls keep the stack at four; a call into C aligns it
    /// to sixteen itself (NativeStackAlignment, Selector.CallSequence).
    /// </summary>
    private sealed class I386Convention : Convention
    {
        public override string Name => "i386";
        public override int StackAlignment => 4;
        public override int StackArgumentsAt => 8;
        // C's i386 System V on Linux keeps sixteen at a call: the psABI's later
        // revision, and what GCC's code has long assumed (its SSE spills by MOVDQA).
        public override int NativeStackAlignment => 16;

        public override CallPlan Arguments(IReadOnlyList<IrType> types, bool registers = true)
        {
            ArgPlace[] places = new ArgPlace[types.Count];
            int generals = 0, stack = 0;
            for (int k = 0; k < types.Count; k++)
            {
                if (registers && types[k] == IrType.I32 && generals < RegisterArgs.Most)
                {
                    places[k] = ArgPlace.InGeneral((int)RegisterArgs.Order[generals++], 4);
                    continue;
                }
                int bytes = types[k] is IrType.I64 or IrType.F64 ? 8 : 4;
                places[k] = ArgPlace.OnStack(stack, bytes);
                stack += bytes;
            }
            return new CallPlan { Places = places, StackBytes = stack, Generals = generals, Floats = 0 };
        }

        private static readonly ArgPlace[] None = [], Word = [ArgPlace.InGeneral((int)Gpr.Eax, 4)],
            Pair = [ArgPlace.InGeneral((int)Gpr.Eax, 4), ArgPlace.InGeneral((int)Gpr.Edx, 4)],
            Single = [new ArgPlace(PlaceKind.FloatStack, 0, 0, 4)], Double = [new ArgPlace(PlaceKind.FloatStack, 0, 0, 8)];

        public override ArgPlace[] Result(IrType type) => type switch
        {
            IrType.Void => None,
            IrType.I32 => Word,
            IrType.I64 => Pair,
            IrType.F32 => Single,
            _ => Double,
        };
    }
}
