#nullable enable
using Corsac.Lang.Calls;
using Corsac.Lang.Ir;

namespace Corsac.Lang.X86;

/// <summary>The x86 family's calling conventions (Corsac.Lang.Calls.Convention), and the one the mode compiles for.</summary>
internal static class Conventions
{
    public static readonly Convention SystemV = new SystemVConvention();
    public static readonly Convention I386 = new I386Convention();

    /// <summary>Windows' x64 convention: a 64-bit Windows image's calls into Windows (X86Mode.Windows; Selector.CallSequence).</summary>
    public static readonly Convention Windows64 = new Windows64Convention();

    /// <summary>The general registers of Windows' four argument positions: RCX, RDX, R8, R9.</summary>
    public static readonly Gpr[] Win64Positions = { Gpr.Rcx, Gpr.Rdx, Gpr.R8, Gpr.R9 };

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
    /// WINDOWS' x64 CONVENTION (Microsoft's "x64 calling convention"), as a
    /// 64-bit Windows image's calls into Windows use it -- its own code keeps
    /// System V's, and a way in from Windows is a stub between the two
    /// (X64Backend's, as Go's): the first four arguments by POSITION, the
    /// k-th in RCX, RDX, R8 or R9 if a word and in XMMk if a float (the
    /// caller copies a float into its general register too, for a variadic
    /// callee: Selector.CallSequence); the rest on the stack in order, a
    /// word each, above the 32 bytes of shadow space the caller leaves for
    /// the callee to home the four registers in. RAX or XMM0 back. The stack
    /// at sixteen at the call.
    /// </summary>
    private sealed class Windows64Convention : Convention
    {
        public override string Name => "Windows x64";
        public override int StackAlignment => 16;
        // (A callee's first stack argument, above its saved base, return
        // address and shadow space; CallSequence takes the shadow's size
        // from it. A function's own way in is System V's: a stub makes the move.)
        public override int StackArgumentsAt => 16 + ShadowBytes;

        /// <summary>The caller's shadow space: a home for each of the four register arguments.</summary>
        public const int ShadowBytes = 32;

        /// <summary>The general registers of the four positions.</summary>
        public static readonly Gpr[] Positions = { Gpr.Rcx, Gpr.Rdx, Gpr.R8, Gpr.R9 };

        public override CallPlan Arguments(IReadOnlyList<IrType> types, bool registers = true)
        {
            ArgPlace[] places = new ArgPlace[types.Count];
            int stack = 0, floats = 0, mask = 0;
            for (int k = 0; k < types.Count; k++)
            {
                int bytes = types[k] is IrType.I32 or IrType.F32 ? 4 : 8;
                if (k < Positions.Length)
                {
                    if (types[k].IsFloat())
                    {
                        places[k] = ArgPlace.InFloat(k, bytes);
                        floats = k + 1;
                        mask |= 1 << k;
                    }
                    else places[k] = ArgPlace.InGeneral((int)Positions[k], bytes);
                }
                else
                {
                    places[k] = ArgPlace.OnStack(ShadowBytes + stack, bytes);
                    stack += 8;
                }
            }
            // Generals: every position up to the last register argument, a
            // float's too (its copy); Floats: XMM0 up to the last float's;
            // FloatMask: the float positions alone, the XMM registers a call
            // really fills (an integer's position leaves its XMM undefined).
            return new CallPlan { Places = places, StackBytes = stack, Generals = Math.Min(types.Count, Positions.Length), Floats = floats, FloatMask = mask };
        }

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
