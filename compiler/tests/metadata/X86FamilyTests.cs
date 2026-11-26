using Corsac.Lang.Ir;
using Corsac.Lang.X86;

namespace Corsac.Tests.Metadata;

/// <summary>
/// What the x86 family's two modes must agree with themselves about.
///
/// The registers an instruction touches without naming them are one table
/// for both modes (Implicit), each answer its ids in the order the
/// allocation names them and the same as a mask. THE TABLE IS WRITTEN OUT
/// HERE A SECOND TIME, opcode by opcode, in that order: an instruction's own
/// registers, the same in both modes, and what each mode's convention says
/// of its calls, returns and system calls. Every opcode is asked in both
/// modes and must answer what is written here -- none, where nothing is --
/// so an answer that changes, or an opcode that gains one in the mode that
/// does not make it, is seen; and a call is asked with every count of
/// arguments it may carry.
///
/// And the frame slots' liveness reads the machine's code one way for both
/// (X86.FrameSlotLiveness): the same five shapes, built by hand in each
/// mode with that mode's word, must give the same answers -- a word written
/// after a call, a slot whose address is taken, an access under an index,
/// a store of half a word, a store after a branch inside its block.
/// </summary>
public static class X86FamilyTests
{
    public static void Run()
    {
        bool was = X86Mode.Long;
        try
        {
            const int A = (int)Gpr.Eax, C = (int)Gpr.Ecx, D = (int)Gpr.Edx, B = (int)Gpr.Ebx, Si = (int)Gpr.Esi, Di = (int)Gpr.Edi,
                Bp = (int)Gpr.Ebp, R11 = (int)Gpr.R11, Xmm0 = MReg.XmmBase;
            int[] none = new int[0], allXmm = Enumerable.Range(Xmm0, 16).ToArray();
            // An instruction's own, the processor's: the same in both modes.
            Dictionary<MOp, (int[] Uses, int[] Defs)> own = new()
            {
                [MOp.Rdmsr] = (new[] { C }, new[] { A, D }),
                [MOp.Rdtsc] = (none, new[] { A, D }),
                [MOp.Wrmsr] = (new[] { C, A, D }, none),
                [MOp.Cpuid] = (new[] { A, C }, new[] { A, B, C, D }),
                [MOp.Cmpxchg8b] = (new[] { A, B, C, D, Si }, new[] { A, D }),
                [MOp.PackedSaveX87] = (new[] { A }, none),
                [MOp.PackedRestoreX87] = (new[] { A }, none),
                [MOp.PackedSaveFxsr] = (new[] { A }, none),
                [MOp.PackedRestoreFxsr] = (new[] { A }, none),
                [MOp.Cdq] = (new[] { A }, new[] { D }),
                [MOp.Cwd] = (new[] { A }, new[] { D }),
                [MOp.Mul] = (new[] { A }, new[] { A, D }),
                [MOp.MulWide] = (new[] { A }, new[] { A, D }),
                [MOp.ImulWide] = (new[] { A }, new[] { A, D }),
                [MOp.Div] = (new[] { A, D }, new[] { A, D }),
                [MOp.Idiv] = (new[] { A, D }, new[] { A, D }),
                [MOp.Cmpxchg] = (new[] { A }, new[] { A }),
                [MOp.RepMovsb] = (new[] { Si, Di, C }, new[] { Si, Di, C }),
                [MOp.RepMovsd] = (new[] { Si, Di, C }, new[] { Si, Di, C }),
                [MOp.RepMovsq] = (new[] { Si, Di, C }, new[] { Si, Di, C }),
                [MOp.RepStosb] = (new[] { A, Di, C }, new[] { Di, C }),
                [MOp.RepStosd] = (new[] { A, Di, C }, new[] { Di, C }),
                [MOp.RepStosq] = (new[] { A, Di, C }, new[] { Di, C }),
                [MOp.Sahf] = (new[] { A }, none),
                [MOp.Fnstsw] = (none, new[] { A }),
                [MOp.In] = (new[] { D }, new[] { A }),
                [MOp.Out] = (new[] { D, A }, none),
                [MOp.LoadSegments] = (new[] { A }, none),
                [MOp.LoadCs] = (none, new[] { A }),
                [MOp.RepInsw] = (new[] { D, Di, C }, new[] { Di, C }),
                [MOp.RepOutsw] = (new[] { D, Si, C }, new[] { Si, C }),
            };
            // Each mode's convention, asked with no argument, no result and no operand.
            Dictionary<MOp, (int[] Uses, int[] Defs)> cdecl = new()
            {
                [MOp.Call] = (none, new[] { A, C, D }),
                [MOp.CallInd] = (none, new[] { A, C, D }),
                [MOp.CallKeepEax] = (none, new[] { A }),
                [MOp.CallKeepEaxInd] = (none, new[] { A }),
                [MOp.SyscallTrap] = (new[] { A, B, C, D, Si, Di, Bp }, new[] { A }),
                [MOp.SaveRegs] = (none, new[] { B, Si, Di }),
            };
            int[] callWrites = LongAbi.CallerSaved.Concat(allXmm).ToArray();
            Dictionary<MOp, (int[] Uses, int[] Defs)> sysV = new()
            {
                [MOp.Call] = (none, callWrites),
                [MOp.CallInd] = (none, callWrites),
                [MOp.CallKeep] = (new[] { A, D }, none),
                [MOp.CallKeepRax] = (new[] { A, D }, new[] { A }),
                [MOp.CallKeepCas] = (new[] { A, C, D }, new[] { A }),
                [MOp.CallSafe] = (none, allXmm),
                [MOp.Syscall] = (new[] { A }, new[] { A, C, R11 }),
                [MOp.SoftInt] = (new[] { A }, new[] { A, C, R11 }),
                [MOp.JmpInd] = (new[] { A }, none),
            };
            if (callWrites.Length != 25) throw new InvalidOperationException("x86 family: long mode's call writes " + callWrites.Length + " registers, not nine general and sixteen XMM");
            int asked = 0;
            foreach (bool longMode in new[] { false, true })
            {
                X86Mode.Long = longMode;
                Dictionary<MOp, (int[] Uses, int[] Defs)> convention = longMode ? sysV : cdecl;
                foreach (MOp op in Enum.GetValues<MOp>())
                {
                    // An asm block's are its own record's.
                    if (op == MOp.InlineAsm) continue;
                    (int[] uses, int[] defs) = own.TryGetValue(op, out var o) ? o : convention.TryGetValue(op, out var c) ? c : (none, none);
                    MInstr plain = new(op) { Width = 0 };
                    Listed(op + " reads", Implicit.Uses(plain), uses);
                    Listed(op + " writes", Implicit.Defs(plain), defs);
                    // And asked a register at a time (Contains), by the ids
                    // written here.
                    Holds(op, "reads", Implicit.Uses(plain), uses);
                    Holds(op, "writes", Implicit.Defs(plain), defs);
                    asked++;
                }
            }
            Console.WriteLine("x86 family: the unnamed registers of every opcode are the ones written out, in order, in both modes: " + asked + " asked");

            // A call's, a return's and a system call's by what they carry.
            X86Mode.Long = true;
            asked = 0;
            for (int ints = 0; ints <= LongAbi.IntArgRegs.Length; ints++)
                for (int floats = 0; floats <= LongAbi.FloatArgRegs; floats++)
                    foreach (bool al in new[] { false, true })
                    {
                        List<int> reads = new();
                        if (al) reads.Add(A);
                        for (int k = 0; k < ints; k++) reads.Add((int)LongAbi.IntArgRegs[k]);
                        for (int k = 0; k < floats; k++) reads.Add(Xmm0 + k);
                        foreach (MOp op in new[] { MOp.Call, MOp.CallInd })
                        {
                            MInstr call = new(op) { IntArgs = ints, FloatArgs = floats, NativeAl = al };
                            Listed("long mode's call reads", Implicit.Uses(call), reads.ToArray());
                            Listed("long mode's call writes", Implicit.Defs(call), callWrites);
                            asked++;
                        }
                        List<int> returned = new();
                        if (ints > 0) returned.Add(A);
                        if (floats > 0) returned.Add(Xmm0);
                        Listed("long mode's return reads", Implicit.Uses(new MInstr(MOp.Epilogue) { IntArgs = ints, FloatArgs = floats }), returned.ToArray());
                    }
            Listed("long mode's first integer arguments", Implicit.Uses(new MInstr(MOp.Call) { IntArgs = 3 }), (int)Gpr.Rdi, (int)Gpr.Rsi, (int)Gpr.Rdx);
            for (int n = 0; n <= LongAbi.SyscallArgs.Length; n++)
                Listed("long mode's system call reads", Implicit.Uses(new MInstr(MOp.Syscall) { IntArgs = n }), new[] { A }.Concat(LongAbi.SyscallArgs.Take(n)).ToArray());
            Listed("long mode's system call's fourth argument is in R10", Implicit.Uses(new MInstr(MOp.SoftInt) { IntArgs = 4 }),
                A, (int)Gpr.Rdi, (int)Gpr.Rsi, (int)Gpr.Rdx, (int)Gpr.R10);
            X86Mode.Long = false;
            Listed("i386's call reads", Implicit.Uses(new MInstr(MOp.Call) { IntArgs = 2, FloatArgs = 1, NativeAl = true }));
            Listed("i386's return of a long reads", Implicit.Uses(new MInstr(MOp.Epilogue) { Width = 8 }), A, D);
            Listed("i386's return of a word reads", Implicit.Uses(new MInstr(MOp.Epilogue) { Width = 4 }), A);
            Listed("i386's return of nothing reads", Implicit.Uses(new MInstr(MOp.Epilogue) { Width = 0 }));
            int[] trap = { A, B, C, D, Si, Di };
            for (int n = 0; n <= 5; n++)
                Listed("i386's system call of " + n + " arguments reads", Implicit.Uses(new MInstr(MOp.SyscallTrap, new MImm(0), new MImm(n))), trap.Take(n + 1).ToArray());
            Listed("i386's system call of six arguments reads", Implicit.Uses(new MInstr(MOp.SyscallTrap, new MImm(0), new MImm(6))), A, B, C, D, Si, Di, Bp);
            Console.WriteLine("x86 family: each mode's calls, returns and system calls read the registers of what they carry, " + asked + " calls asked");
            foreach (bool longMode in new[] { false, true })
            {
                X86Mode.Long = longMode;
                FrameReading();
            }
            Console.WriteLine("x86 family: the frame's code read alike in both modes -- written after a call, address taken, indexed, half stored, stored after a branch");
        }
        finally
        {
            X86Mode.Long = was;
        }
        DeviceLoadKept();
        Lowered();
        FramelessForms();
        PackedForms();
        ConventionPlaces();
    }

    /// <summary>
    /// WHERE EACH CONVENTION PUTS A SIGNATURE'S ARGUMENTS AND ITS RESULT
    /// (Conventions.SystemV, Conventions.I386): the registers in order, each
    /// class counted alone in System V's and the first three words in
    /// i386's whatever stands between them, the stack places' offsets and
    /// sizes, the stack-only form, and what a value comes back in.
    /// </summary>
    private static void ConventionPlaces()
    {
        static void Same(string what, object made, object expected)
        {
            if (!Equals(made, expected)) throw new InvalidOperationException($"conventions: {what}: {made}, not {expected}");
        }
        static string Text(Corsac.Lang.Calls.CallPlan plan)
            => string.Join(" ", plan.Places.Select(p => p.Kind switch
            {
                Corsac.Lang.Calls.PlaceKind.General => "g" + p.Register,
                Corsac.Lang.Calls.PlaceKind.Float => "f" + p.Register,
                _ => "s" + p.Offset,
            } + ":" + p.Bytes)) + $" | stack {plan.StackBytes} g {plan.Generals} f {plan.Floats}";
        IrType i = IrType.I32, l = IrType.I64, f = IrType.F32, d = IrType.F64;

        Corsac.Lang.Calls.Convention v = Conventions.SystemV;
        Same("System V's stack at a call", v.StackAlignment, 16);
        Same("System V's first stack argument", v.StackArgumentsAt, 16);
        // RDI 7, RSI 6, RDX 2, RCX 1, R8, R9; then the stack, a word each, floats and integers in one run.
        Same("System V: seven integers, nine floats, two more",
            Text(v.Arguments(new[] { l, i, l, i, l, l, i, d, f, d, d, d, d, d, d, f, l, d })),
            "g7:8 g6:4 g2:8 g1:4 g8:8 g9:8 s0:4 f0:8 f1:4 f2:8 f3:8 f4:8 f5:8 f6:8 f7:8 s8:4 s16:8 s24:8 | stack 32 g 6 f 8");
        Same("System V has one form", Text(v.Arguments(new[] { i, d }, registers: false)), "g7:4 f0:8 | stack 0 g 1 f 1");
        Same("System V: nothing", Text(v.Arguments(Array.Empty<IrType>())), " | stack 0 g 0 f 0");
        Same("System V: a long comes back in RAX", v.Result(l).Single(), Corsac.Lang.Calls.ArgPlace.InGeneral((int)Gpr.Rax, 8));
        Same("System V: a float comes back in XMM0", v.Result(f).Single(), Corsac.Lang.Calls.ArgPlace.InFloat(0, 4));
        Same("System V: no value, no place", v.Result(IrType.Void).Length, 0);

        Corsac.Lang.Calls.Convention x = Conventions.I386;
        Same("i386's own stack at a call", x.StackAlignment, 4);
        Same("i386's first stack argument", x.StackArgumentsAt, 8);
        // EAX 0, EDX 2, ECX 1: the first three words, a long and a double between them on the stack.
        Same("i386: three words among others",
            Text(x.Arguments(new[] { l, i, d, i, f, i, i, l })),
            "s0:8 g0:4 s8:8 g2:4 s16:4 g1:4 s20:4 s24:8 | stack 32 g 3 f 0");
        Same("i386's stack-only form is cdecl's layout",
            Text(x.Arguments(new[] { i, l, f, d, i }, registers: false)),
            "s0:4 s4:8 s12:4 s16:8 s24:4 | stack 28 g 0 f 0");
        Same("i386: a long comes back in EDX:EAX", string.Join(",", x.Result(l).Select(p => p.Register)), "0,2");
        Same("i386: a double comes back on the x87's stack", x.Result(d).Single().Kind, Corsac.Lang.Calls.PlaceKind.FloatStack);
    }

    /// <summary>
    /// LONG MODE'S PACKED OPERATIONS ON THE REGISTERS A REX NAMES, byte for
    /// byte: the family's cases for a shift, a word's extraction and its
    /// insertion put the register into a ModRM by hand and knew no XMM8 to
    /// XMM15 nor R8 to R15; MOVNTI is long mode's own; a sixteen-byte move of
    /// an aligned slot is MOVAPS.
    /// </summary>
    private static void PackedForms()
    {
        bool was = X86Mode.Long;
        X86Mode.Long = true;
        try
        {
            static void Same(string what, MInstr i, params byte[] expected)
            {
                MFunction m = new(new Function("packed", IrType.Void)) { Frameless = true };
                m.NewBlock("entry").Instrs.Add(i);
                Section text = new(".text", SectionKind.Code);
                new Encoder(text).Encode(m);
                byte[] made = text.Bytes.ToArray();
                if (!made.SequenceEqual(expected))
                    throw new InvalidOperationException($"x86 family (long mode, packed): {what}: {Convert.ToHexString(made)}, not {Convert.ToHexString(expected)}");
            }
            MImm N(int n) => new(n);
            Same("psrldq xmm9, 8", new MInstr(MOp.XmmShiftV, MReg.Xmm(9), N(0x73), N(3), N(8)) { Probed = true }, 0x66, 0x41, 0x0F, 0x73, 0xD9, 0x08);
            Same("psllw xmm2, 5", new MInstr(MOp.XmmShiftV, MReg.Xmm(2), N(0x71), N(6), N(5)) { Probed = true }, 0x66, 0x0F, 0x71, 0xF2, 0x05);
            Same("pextrw r10d, xmm11, 3", new MInstr(MOp.XmmExtractV, MReg.Of(Gpr.R10), MReg.Xmm(11), N(3)) { Probed = true }, 0x66, 0x45, 0x0F, 0xC5, 0xD3, 0x03);
            Same("pextrw eax, xmm1, 0", new MInstr(MOp.XmmExtractV, MReg.Of(Gpr.Rax), MReg.Xmm(1), N(0)) { Probed = true }, 0x66, 0x0F, 0xC5, 0xC1, 0x00);
            Same("pinsrw xmm12, r9d, 2", new MInstr(MOp.XmmInsertV, MReg.Xmm(12), MReg.Of(Gpr.R9), N(2)) { Probed = true }, 0x66, 0x45, 0x0F, 0xC4, 0xE1, 0x02);
            Same("pinsrw xmm3, edx, 7", new MInstr(MOp.XmmInsertV, MReg.Xmm(3), MReg.Of(Gpr.Rdx), N(7)) { Probed = true }, 0x66, 0x0F, 0xC4, 0xDA, 0x07);
            Same("paddb xmm8, xmm15", new MInstr(MOp.XmmOpV, N(0x66), N(0xFC), MReg.Xmm(8), MReg.Xmm(15)) { Probed = true }, 0x66, 0x45, 0x0F, 0xFC, 0xC7);
            Same("pshufd xmm1, xmm10, 0xEE", new MInstr(MOp.XmmOpVDef, N(0x66), N(0x70), MReg.Xmm(1), MReg.Xmm(10), N(0xEE)) { Probed = true }, 0x66, 0x41, 0x0F, 0x70, 0xCA, 0xEE);
            Same("movq xmm9, xmm2", new MInstr(MOp.XmmOpVDef, N(0xF3), N(0x7E), MReg.Xmm(9), MReg.Xmm(2)) { Probed = true }, 0xF3, 0x44, 0x0F, 0x7E, 0xCA);
            Same("movntdq [r8], xmm13", new MInstr(MOp.XmmStoreNtV, new MMem(MReg.Of(Gpr.R8), 0), MReg.Xmm(13)) { Probed = true }, 0x66, 0x45, 0x0F, 0xE7, 0x28);
            Same("movnti [rdi], rsi", new MInstr(MOp.Movnti, new MMem(MReg.Of(Gpr.Rdi), 0), MReg.Of(Gpr.Rsi)) { Width = 8, Probed = true }, 0x48, 0x0F, 0xC3, 0x37);
            Same("movnti [r9 + 8], r14", new MInstr(MOp.Movnti, new MMem(MReg.Of(Gpr.R9), 8), MReg.Of(Gpr.R14)) { Width = 8, Probed = true }, 0x4D, 0x0F, 0xC3, 0x71, 0x08);
            Same("movq xmm8, rax", new MInstr(MOp.MovGx, MReg.Xmm(8), MReg.Of(Gpr.Rax)) { Width = 8, Probed = true }, 0x66, 0x4C, 0x0F, 0x6E, 0xC0);
            Same("movq r11, xmm1", new MInstr(MOp.MovGx, MReg.Of(Gpr.R11), MReg.Xmm(1)) { Width = 8, Probed = true }, 0x66, 0x49, 0x0F, 0x7E, 0xCB);
            Same("prefetchnta [rdi]", new MInstr(MOp.PrefetchNta, new MMem(MReg.Of(Gpr.Rdi), 0)) { Probed = true }, 0x0F, 0x18, 0x07);
            Same("prefetchw [r9]", new MInstr(MOp.PrefetchWrite, new MMem(MReg.Of(Gpr.R9), 0)) { Probed = true }, 0x41, 0x0F, 0x0D, 0x09);
            Same("sfence", new MInstr(MOp.Sfence) { Probed = true }, 0x0F, 0xAE, 0xF8);
            Same("movaps xmm9, [rbp - 32], the slot aligned",
                new MInstr(MOp.MovF, MReg.Xmm(9), new MMem(MReg.Of(Gpr.Rbp), -32) { IsSpill = true }.AsAligned()) { Width = 16, Probed = true }, 0x44, 0x0F, 0x28, 0x4D, 0xE0);
            Same("movups [rbp - 24], xmm9, the slot not",
                new MInstr(MOp.MovF, new MMem(MReg.Of(Gpr.Rbp), -24) { IsSpill = true }, MReg.Xmm(9)) { Width = 16, Probed = true }, 0x44, 0x0F, 0x11, 0x4D, 0xE8);
        }
        finally { X86Mode.Long = was; }
    }

    /// <summary>
    /// LONG MODE'S WAYS IN AND OUT OF A FUNCTION WITHOUT A FRAME, byte for
    /// byte, as the runtime reads them (Runtime.FrameAt, Prologue): a leaf's
    /// pushes then its slots, RBP first where it holds a value; and a
    /// reserved frame's the other way round, with the word that keeps the
    /// stack a multiple of sixteen at its call pushed below the saves where
    /// its slots and saves are an odd number of words -- one save and no
    /// slot wants it, two saves want none.
    /// </summary>
    private static void FramelessForms()
    {
        bool was = X86Mode.Long;
        X86Mode.Long = true;
        try
        {
            static byte[] Encoded(MFunction m)
            {
                Section text = new(".text", SectionKind.Code);
                new Encoder(text).Encode(m);
                return text.Bytes.ToArray();
            }
            static void Same(string what, byte[] made, params byte[] expected)
            {
                if (!made.SequenceEqual(expected))
                    throw new InvalidOperationException($"x86 family (long mode): {what}: {Convert.ToHexString(made)}, not {Convert.ToHexString(expected)}");
            }

            // A leaf that saves nothing and keeps nothing: no way in at all.
            MFunction bare = new(new Function("bare", IrType.Void)) { Frameless = true };
            bare.NewBlock("entry").Instrs.AddRange(new[] { new MInstr(MOp.Prologue), new MInstr(MOp.Epilogue) { Width = 0 } });
            Same("a leaf with nothing to save is its return", Encoded(bare), 0xC3);

            // A leaf that gives RBP a value and uses RBX and R12, with a slot:
            // RBP pushed first, then the others, then the slot's word.
            MFunction leaf = new(new Function("leaf", IrType.Void)) { Frameless = true, FramePointerFree = true };
            leaf.SavedRegs.AddRange(new[] { Gpr.Rbp, Gpr.Rbx, Gpr.R12 });
            leaf.Frame.Allocate(8, 8);
            leaf.NewBlock("entry").Instrs.AddRange(new[]
            {
                new MInstr(MOp.Prologue),
                new MInstr(MOp.Mov, MReg.Of(Gpr.Rbp), MReg.Of(Gpr.Rsp)) { Width = 8 },
                new MInstr(MOp.Epilogue) { Width = 0 },
            });
            Same("a leaf's pushes, its slot, its own `mov rbp, rsp` written 8B EC, and the way out",
                Encoded(leaf), 0x55, 0x53, 0x41, 0x54, 0x48, 0x83, 0xEC, 0x08, 0x48, 0x8B, 0xEC, 0x48, 0x83, 0xC4, 0x08, 0x41, 0x5C, 0x5B, 0x5D, 0xC3);

            // A reserved frame: one save, no slot -- RBP's word (push rax),
            // the save, and the word that makes the stack a sixteen; the
            // frame made at the call sixteen bytes up.
            foreach (int saves in new[] { 1, 2 })
            {
                MFunction cold = new(new Function("cold" + saves, IrType.Void)) { Frameless = true, ColdFrames = true, ReservedFrame = true };
                cold.SavedRegs.Add(Gpr.Rbx);
                if (saves == 2) cold.SavedRegs.Add(Gpr.R12);
                cold.NewBlock("entry").Instrs.AddRange(new[]
                {
                    new MInstr(MOp.Prologue), new MInstr(MOp.Epilogue) { Width = 0 },
                    new MInstr(MOp.CallInd, MReg.Of(Gpr.R8)), new MInstr(MOp.Ud2),
                });
                if (Encoder.ReservedPad(cold) != (saves == 1 ? 8 : 0))
                    throw new InvalidOperationException($"x86 family (long mode): a reserved frame of {saves} saves and no slot pads {Encoder.ReservedPad(cold)} bytes");
                if (saves == 1)
                    Same("a reserved frame of one save: the frame pointer's word, the save, a word of padding; the frame made 16 up before a call through R8",
                        Encoded(cold), 0x50, 0x53, 0x50, 0x59, 0x5B, 0x59, 0xC3,
                        0x48, 0x89, 0x6C, 0x24, 0x10, 0x48, 0x8D, 0x6C, 0x24, 0x10, 0x41, 0xFF, 0xD0, 0x0F, 0x0B);
                else
                    Same("a reserved frame of two saves: no padding; the frame made 16 up",
                        Encoded(cold), 0x50, 0x53, 0x41, 0x54, 0x41, 0x5C, 0x5B, 0x59, 0xC3,
                        0x48, 0x89, 0x6C, 0x24, 0x10, 0x48, 0x8D, 0x6C, 0x24, 0x10, 0x41, 0xFF, 0xD0, 0x0F, 0x0B);
            }

            // The plain cold frame: `push rbp ; mov rbp, rsp` before the call.
            MFunction plain = new(new Function("plain", IrType.Void)) { Frameless = true, ColdFrames = true };
            plain.NewBlock("entry").Instrs.AddRange(new[]
            {
                new MInstr(MOp.Prologue), new MInstr(MOp.Epilogue) { Width = 0 },
                new MInstr(MOp.CallInd, MReg.Of(Gpr.Rax)), new MInstr(MOp.Ud2),
            });
            Same("a plain cold frame: the push that also makes the stack a sixteen, then the call", Encoded(plain), 0xC3, 0x55, 0x48, 0x89, 0xE5, 0xFF, 0xD0, 0x0F, 0x0B);
            Console.WriteLine("x86 family: long mode's frameless ways in and out are the bytes the runtime reads -- a leaf's, a reserved frame's with and without its padding word, a plain cold frame's");
        }
        finally
        {
            X86Mode.Long = was;
        }
    }

    /// <summary>
    /// ONE INSTRUCTION AS THE ALLOCATOR IS TOLD IT (FamilyTarget.LowerInstr),
    /// where the modes differ and where they must not: a register moved onto
    /// itself is nothing at any width but long mode's 4-byte move, which
    /// clears the upper half; a copy is whole at the mode's word and as an
    /// XMM move; a call in an i386 function with the XMM class writes all
    /// eight XMM registers, named after its own; long mode writes a general
    /// register in a slot only at eight bytes; a push takes a wide constant
    /// only where the operation is 32 bits wide.
    /// </summary>
    private static void Lowered()
    {
        bool was = X86Mode.Long;
        try
        {
            foreach (bool longMode in new[] { false, true })
            {
                X86Mode.Long = longMode;
                string mode = longMode ? "long mode" : "i386";
                int word = X86Mode.Word;
                MFunction m = new(new Function("lowered", IrType.I32));
                MBlock b = m.NewBlock("entry");
                MReg a = m.NewReg(), c = m.NewReg(), x = m.NewFloat(), y = m.NewFloat();
                m.XmmRegs.Add(x.Id);
                m.XmmRegs.Add(y.Id);
                int[] widths = longMode ? new[] { 1, 2, 4, 8 } : new[] { 1, 2, 4 };
                b.Instrs.Add(new MInstr(MOp.Prologue));
                foreach (int width in widths) b.Instrs.Add(new MInstr(MOp.Mov, a, c) { Width = width });
                b.Instrs.Add(new MInstr(MOp.MovF, x, y) { Width = 8 });
                b.Instrs.Add(new MInstr(MOp.Call, new MImm(0)));
                b.Instrs.Add(new MInstr(MOp.Push, a));
                b.Instrs.Add(new MInstr(MOp.Add, a, c) { Width = 4 });
                b.Instrs.Add(new MInstr(MOp.Add, a, c) { Width = word });
                b.Instrs.Add(new MInstr(MOp.Mov, a, c) { Width = 4, Lock = true });
                MMem pool = longMode ? MMem.Rip(X86Backend.FloatPoolName(0x3FF0000000000000, 8), 0) : MMem.Abs(X86Backend.FloatPoolName(0x3FF0000000000000, 8), 0);
                b.Instrs.Add(new MInstr(MOp.MovF, x, pool) { Width = 8 });
                b.Instrs.Add(new MInstr(MOp.Mov, a, new MImm(-1)) { Width = 4 });
                b.Instrs.Add(new MInstr(MOp.Lea, a, MMem.Frame(-16)) { Width = word });
                b.Instrs.Add(new MInstr(MOp.Lea, a, new MMem(MReg.Of(Gpr.Esp), 8)) { Width = word });
                b.Instrs.Add(new MInstr(MOp.Mov, a, new MMem(c, 0)) { Width = word });
                // Two arguments passed on the stack, each loaded once: a word
                // and a double. A third word loaded twice is no home.
                MReg h = m.NewReg(), twice = m.NewReg(), hx = m.NewFloat();
                b.Instrs.Add(new MInstr(MOp.Mov, h, MMem.Frame(2 * word)) { Width = word });
                b.Instrs.Add(new MInstr(MOp.MovF, hx, MMem.Frame(4 * word)) { Width = 8 });
                b.Instrs.Add(new MInstr(MOp.Mov, twice, MMem.Frame(8 * word)) { Width = word });
                b.Instrs.Add(new MInstr(MOp.Mov, twice, MMem.Frame(8 * word)) { Width = word });
                MReg hf = m.NewFloat();
                b.Instrs.Add(new MInstr(MOp.MovF, hf, MMem.Frame(10 * word)) { Width = 4 });
                b.Instrs.Add(new MInstr(MOp.MovF, x, y) { Width = 4 });
                b.Instrs.Add(new MInstr(MOp.Epilogue) { Width = 0 });
                b.Instrs.Add(new MInstr(MOp.Ret));
                Corsac.Lang.RegAlloc.AFunction f = longMode ? new LongTarget(m).Lower() : new X86Target(m).Lower();
                List<Corsac.Lang.RegAlloc.AInstr> lowered = f.Blocks[0].Instrs;
                int at = 1;
                foreach (int width in widths)
                {
                    Corsac.Lang.RegAlloc.AInstr copy = lowered[at++];
                    Expect(copy.IsCopy, $"{mode}: a {width}-byte register move is a copy");
                    Expect(copy.WholeCopy == (width == word), $"{mode}: a {width}-byte move is a whole copy only at the mode's word");
                    Expect(copy.DropsSelf == !(longMode && width == 4), $"{mode}: a {width}-byte move onto itself is dropped, but long mode's 4-byte one");
                    Expect(copy.MayMemory == 3u && copy.MayMemoryWrite == (longMode && width != 8 ? 0u : 3u), $"{mode}: a {width}-byte move may be written in a slot only where the slot holds what the register would");
                }
                Corsac.Lang.RegAlloc.AInstr xmm = lowered[at++];
                Expect(xmm.IsCopy && xmm.WholeCopy && xmm.DropsSelf, $"{mode}: an XMM move is a whole copy, dropped onto itself");
                Expect(xmm.MayMemoryWrite == 3u, $"{mode}: an XMM move may be written in a slot");
                Corsac.Lang.RegAlloc.AInstr call = lowered[at++];
                Expect(call.IsCall, $"{mode}: a call is one");
                int[] written = call.Regs.Where(r => r.Operand == -1 && (r.Role & Corsac.Lang.RegAlloc.Role.Def) != 0).Select(r => r.Reg).ToArray();
                int[] expected = longMode
                    ? LongAbi.CallerSaved.Concat(Enumerable.Range(MReg.XmmBase, 16)).ToArray()
                    : new[] { (int)Gpr.Eax, (int)Gpr.Ecx, (int)Gpr.Edx }.Concat(Enumerable.Range(MReg.XmmBase, 8)).ToArray();
                Expect(written.SequenceEqual(expected), $"{mode}: a call writes [{string.Join(", ", written)}], not [{string.Join(", ", expected)}]");
                Corsac.Lang.RegAlloc.AInstr push = lowered[at++];
                Expect(push.MayImmediate == 1u, $"{mode}: a push's operand may be a constant");
                Expect(push.MayImmediateWide == (longMode ? 0u : 1u), $"{mode}: a push takes any constant only where it is 32 bits wide");
                Corsac.Lang.RegAlloc.AInstr add4 = lowered[at++], addWord = lowered[at++];
                Expect(add4.MayImmediate == 2u && add4.MayImmediateWide == 2u, $"{mode}: a 4-byte add's source may be any constant");
                Expect(addWord.MayImmediateWide == (longMode ? 0u : 2u), $"{mode}: a word's add takes any constant only where the word is 32 bits");
                Expect(add4.MayMemory == 3u && add4.MayMemoryWrite == (longMode ? 0u : 3u), $"{mode}: a 4-byte add may be written in a slot only on i386");
                Expect(addWord.MayMemoryWrite == 3u, $"{mode}: a word's add may be written in a slot");
                Expect(!add4.IsCopy, $"{mode}: an add is no copy");
                Corsac.Lang.RegAlloc.AInstr locked = lowered[at++];
                Expect(locked.IsCopy && !locked.WholeCopy, $"{mode}: a locked move is no whole copy");
                // What may be made again where it is wanted, and what may not.
                object? remadePool = lowered[at++].Constant, remadeNumber = lowered[at++].Constant, remadeFrame = lowered[at++].Constant,
                    offStack = lowered[at++].Constant, loaded = lowered[at++].Constant;
                Expect(remadePool is FamilyTarget.FloatPoolLoad { Width: 8 } made && ReferenceEquals(made.Pool, pool), $"{mode}: a float constant's load from the pool is made again from the pool");
                Expect(remadeNumber is MImm number && number.Value == (longMode ? 0xFFFFFFFFL : -1L), $"{mode}: a 4-byte move of -1 is made again as the value the register held");
                Expect(remadeFrame is MMem { Disp: -16 }, $"{mode}: an address off the frame's base is made again by its LEA");
                Expect(offStack is null, $"{mode}: an address off the stack pointer, which every push moves, is not made again");
                Expect(loaded is null, $"{mode}: a load is no constant");
                // (The LEA of a local below the frame's base, above, takes no
                // argument's address: the homes stand.)
                Corsac.Lang.RegAlloc.AInstr home = lowered[at++], floatHome = lowered[at++], first = lowered[at++], second = lowered[at++];
                Expect(home.HomeSlot == 2 * word, $"{mode}: a stack-passed word loaded once is its value's home");
                Expect(floatHome.HomeSlot == 4 * word, $"{mode}: a stack-passed double loaded once into the XMM class is its value's home");
                Expect(first.HomeSlot is null && second.HomeSlot is null, $"{mode}: a word loaded twice is no home");
                Expect(lowered[at++].HomeSlot == 10 * word, $"{mode}: a stack-passed float loaded once at four bytes is its value's home");
                Expect(lowered[at++].MayMemoryWrite == 3u, $"{mode}: a 4-byte XMM move may be written in a slot, an XMM register's by its class");

                // THE REGISTERS (FamilyTarget.Files): this function calls, so
                // the frame's base is no register of it.
                int sp = (int)Gpr.Esp, bp = (int)Gpr.Ebp;
                int[] callerSaved = longMode ? LongAbi.CallerSaved : new[] { (int)Gpr.Eax, (int)Gpr.Ecx, (int)Gpr.Edx };
                int[] calleeSaved = longMode
                    ? new[] { (int)Gpr.Rbx, (int)Gpr.R12, (int)Gpr.R13, (int)Gpr.R14, (int)Gpr.R15 }
                    : new[] { (int)Gpr.Ebx, (int)Gpr.Esi, (int)Gpr.Edi };
                int[]? bytes = longMode ? null : new[] { (int)Gpr.Eax, (int)Gpr.Ecx, (int)Gpr.Edx, (int)Gpr.Ebx };
                int xmms = longMode ? 16 : 8;
                void File(string which, Corsac.Lang.RegAlloc.RegisterFile file, int[] preference, int[] saved, int[] untracked, int[]? byteCapable)
                {
                    Expect(file.PhysCount == MReg.XmmBase + xmms && file.FirstVirtual == MReg.FirstVirtual, $"{mode}, {which}: the machine's registers end where its XMM ones do");
                    Expect(file.Untracked.SequenceEqual(untracked), $"{mode}, {which}: untracked [{string.Join(", ", file.Untracked)}]");
                    Expect(file.Classes.Length == 2, $"{mode}, {which}: a general class and an XMM one");
                    Expect(file.Classes[0].Preference.SequenceEqual(preference), $"{mode}, {which}: general registers tried in the order [{string.Join(", ", file.Classes[0].Preference)}]");
                    Expect(file.Classes[0].CalleeSaved.SequenceEqual(saved), $"{mode}, {which}: callee-saved [{string.Join(", ", file.Classes[0].CalleeSaved)}]");
                    Expect(byteCapable is null ? file.Classes[0].ByteCapable is null : file.Classes[0].ByteCapable is { } capable && capable.SequenceEqual(byteCapable),
                        $"{mode}, {which}: the registers with byte forms");
                    Expect(file.Classes[1].Preference.SequenceEqual(Enumerable.Range(MReg.XmmBase, xmms)) && file.Classes[1].CalleeSaved.Length == 0
                        && !file.Classes[1].HoldsReferences, $"{mode}, {which}: every XMM register caller-saved, none a reference's");
                }
                File("a function that calls", f.Registers, callerSaved.Concat(calleeSaved).ToArray(), calleeSaved, new[] { sp, bp }, bytes);

                // A leaf has the base register besides, callee-saved; i386's
                // leaf that reads a caller's register (off the frame pointer)
                // is none, long mode's (from its save slot) is.
                foreach (bool callerRegister in new[] { false, true })
                {
                    MFunction leaf = new(new Function("leaf", IrType.I32));
                    MBlock lb = leaf.NewBlock("entry");
                    MReg v = leaf.NewReg();
                    Expect(v.Id != MReg.EbpDataId, $"{mode}: no virtual register has the number the base register is known by while it holds a value");
                    lb.Instrs.Add(new MInstr(MOp.Prologue));
                    lb.Instrs.Add(callerRegister ? new MInstr(MOp.CallerReg, v, new MImm(0)) : new MInstr(MOp.Mov, v, new MImm(1)) { Width = word });
                    lb.Instrs.Add(new MInstr(MOp.Epilogue) { Width = 0 });
                    lb.Instrs.Add(new MInstr(MOp.Ret));
                    Corsac.Lang.RegAlloc.RegisterFile got = (longMode ? new LongTarget(leaf).Lower() : new X86Target(leaf).Lower()).Registers;
                    if (callerRegister && !longMode) File("a leaf reading a caller's register", got, callerSaved.Concat(calleeSaved).ToArray(), calleeSaved, new[] { sp, bp }, bytes);
                    else File("a leaf", got, callerSaved.Concat(calleeSaved).Append(bp).ToArray(), calleeSaved.Append(bp).ToArray(), new[] { sp }, bytes);
                }

                // A thunk C enters keeps the callee-saved registers as its caller left them.
                MFunction thunk = new(new Function("thunk", IrType.I32) { KeepsCallerRegisters = true });
                thunk.NewBlock("entry").Instrs.AddRange(new[] { new MInstr(MOp.Prologue), new MInstr(MOp.Epilogue) { Width = 0 } });
                File("a thunk", (longMode ? new LongTarget(thunk).Lower() : new X86Target(thunk).Lower()).Registers, callerSaved, new int[0], new[] { sp, bp },
                    longMode ? null : new[] { (int)Gpr.Eax, (int)Gpr.Ecx, (int)Gpr.Edx });

                // VALUES NOTHING READS ARE GONE BEFORE THE ALLOCATOR SEES THEM
                // (Allocator.PruneUnusedValues), in both modes: a constant, and
                // arithmetic at the word's width whose flags the next
                // instruction writes over. A machine XMM register's write stays.
                MFunction pruned = new(new Function("pruned", IrType.I32));
                MBlock pb = pruned.NewBlock("entry");
                MReg kept = pruned.NewReg(), dead = pruned.NewReg(), sum = pruned.NewReg(), fx = pruned.NewFloat();
                pb.Instrs.AddRange(new[]
                {
                    new MInstr(MOp.Prologue),
                    new MInstr(MOp.Mov, kept, new MImm(7)) { Width = word },
                    new MInstr(MOp.Mov, dead, new MImm(5)) { Width = word },
                    new MInstr(MOp.Mov, sum, kept) { Width = word },
                    new MInstr(MOp.Add, sum, kept) { Width = word },
                    new MInstr(MOp.Test, kept, kept) { Width = word },
                    new MInstr(MOp.MovF, fx, MMem.Frame(-8)) { Width = 8 },
                    new MInstr(MOp.MovF, MReg.Of(MReg.XmmBase + 5), fx) { Width = 8 },
                    new MInstr(MOp.Mov, MReg.Of(Gpr.Eax), kept) { Width = word },
                    new MInstr(MOp.Epilogue) { Width = word, IntArgs = 1 },
                    new MInstr(MOp.Ret),
                });
                pruned.Frame.Allocate(8, 8);
                Allocator.Run(pruned);
                List<MInstr> left = pruned.Blocks.SelectMany(block => block.Instrs).ToList();
                Expect(!left.Any(i => i.Operands.Any(o => o is MImm { Value: 5 })), $"{mode}: a constant nothing reads is gone");
                Expect(!left.Any(i => i.Op == MOp.Add), $"{mode}: arithmetic nothing reads, its flags written over, is gone");
                Expect(left.Any(i => i.Operands.Any(o => o is MImm { Value: 7 })), $"{mode}: a value read stays");
                // (XMM5: no register the allocator's first choice for the
                // value moved, so only the write itself puts it there.)
                Expect(left.Any(i => i.Op == MOp.MovF && i.Operands[0] is MReg { Id: MReg.XmmBase + 5 }), $"{mode}: a machine XMM register's write stays, whoever reads it");
            }
            Console.WriteLine("x86 family: an instruction is told the allocator alike in both modes but where a mode's registers differ -- self-moves, whole copies, a call's writes, slots written, wide constants, what is made again, the argument homes, the register files, a leaf's, the values pruned");
        }
        finally
        {
            X86Mode.Long = was;
        }
    }

    /// <summary>
    /// A DEVICE LOAD STAYS ONE THROUGH LONG MODE'S ALLOCATOR AND PEEPHOLE: a
    /// word stored to an address and read back from it marked as device
    /// memory -- the read that flushes a posted write -- is still a load of
    /// that address, still marked, where the allocator once rebuilt the
    /// operand without its mark and the peephole then took the load for the
    /// register just stored.
    /// </summary>
    private static void DeviceLoadKept()
    {
        bool was = X86Mode.Long;
        X86Mode.Long = true;
        try
        {
            foreach (bool device in new[] { true, false })
            {
                MFunction m = new(new Function(device ? "device-read-back" : "memory-read-back", IrType.I64));
                MBlock b = m.NewBlock("entry");
                MReg p = m.NewReg(), v = m.NewReg(), r = m.NewReg();
                MMem read = new(p, 0);
                b.Instrs.AddRange(new[]
                {
                    new MInstr(MOp.Prologue),
                    new MInstr(MOp.Mov, p, MReg.Of(Gpr.Rdi)) { Width = 8 },
                    new MInstr(MOp.Mov, v, MReg.Of(Gpr.Rsi)) { Width = 8 },
                    new MInstr(MOp.Mov, new MMem(p, 0), v) { Width = 8 },
                    new MInstr(MOp.Mov, r, device ? read.AsDevice() : read) { Width = 8 },
                    new MInstr(MOp.Mov, MReg.Of(Gpr.Rax), r) { Width = 8 },
                    new MInstr(MOp.Epilogue),
                    new MInstr(MOp.Ret),
                });
                Allocator.Run(m);
                Peephole.Run(m);
                bool marked = false, loads = false;
                foreach (MBlock block in m.Blocks)
                    foreach (MInstr i in block.Instrs)
                        if (i.Op == MOp.Mov && i.Operands.Count == 2 && i.Operands[0] is MReg && i.Operands[1] is MMem read2)
                        {
                            loads = true;
                            marked |= read2.Device;
                        }
                // THE PLAIN READ-BACK IS FORWARDED from the register stored --
                // no load of it is left -- which shows the rule reaches this
                // code, and that the device one is kept by its mark and not
                // by chance.
                if (device) Expect(marked, "long mode: a device load read back after a store to its address is still a marked load");
                else Expect(!loads, "long mode: a plain load read back after a store to its address is the register stored");
            }
            Console.WriteLine("x86 family: a device load keeps its mark, and stays a load, through long mode's allocator and peephole");
        }
        finally
        {
            X86Mode.Long = was;
        }
    }

    // ---- the frame slots' liveness, over machine code made by hand -----------------

    private static int _slotIds;

    private static MFunction Function(string name, out int a, out int b)
    {
        MFunction m = new(new Function(name, IrType.I32));
        int word = X86Mode.Word;
        a = m.Frame.SlotOffset(new FrameSlot(++_slotIds, word, word));
        b = m.Frame.SlotOffset(new FrameSlot(++_slotIds, word, word));
        return m;
    }

    private static MInstr Call() => new(MOp.Call, MImm.Sym("callee", 0));
    private static MInstr Store(int slot, int width) => new(MOp.Mov, MMem.Frame(slot), MReg.Of(Gpr.Eax)) { Width = width };
    private static MInstr Load(int slot) => new(MOp.Mov, MReg.Of(Gpr.Eax), MMem.Frame(slot));

    private static void Expect(bool held, string what)
    {
        if (!held) throw new InvalidOperationException("x86 family (" + (X86Mode.Long ? "long mode" : "i386") + "): " + what);
    }

    private static Corsac.Lang.Frames.FrameSlotLiveness.Result Measure(MFunction m, string what)
    {
        Corsac.Lang.Frames.FrameSlotLiveness.Result? result = FrameSlotLiveness.Measured(m);
        Expect(result is not null, what + " was left unread");
        return result!;
    }

    private static void FrameReading()
    {
        int word = X86Mode.Word;

        // A WORD WRITTEN AFTER A CALL and read after the next: no word of the
        // first call's map, a word of the second's, and never zeroed.
        {
            MFunction m = Function("written-after", out int a, out _);
            MInstr first = Call(), second = Call();
            MBlock b = m.NewBlock("entry");
            b.Instrs.AddRange(new[] { new MInstr(MOp.Prologue), first, Store(a, word), second, Load(a), new MInstr(MOp.Epilogue), new MInstr(MOp.Ret) });
            var r = Measure(m, "written-after");
            Expect(!r.At[first].Contains(a), "a word written after a call is named at it");
            Expect(r.At[second].Contains(a), "a word read after a call is not named at it");
            Expect(r.Zero.Length == 0, "a word no map names unwritten is zeroed");
        }

        // A SLOT WHOSE ADDRESS IS TAKEN is named at every call from then on,
        // and zeroed at entry: the call finds it before anything was stored.
        {
            MFunction m = Function("address-taken", out int a, out int other);
            MInstr call = Call();
            MBlock b = m.NewBlock("entry");
            b.Instrs.AddRange(new[] { new MInstr(MOp.Prologue), new MInstr(MOp.Lea, MReg.Of(Gpr.Ecx), MMem.Frame(a)), call, new MInstr(MOp.Epilogue), new MInstr(MOp.Ret) });
            var r = Measure(m, "address-taken");
            Expect(r.At[call].Contains(a), "a slot whose address is taken is not named");
            Expect(!r.At[call].Contains(other), "a slot nothing touches is named");
            Expect(r.Zero.Contains(a), "an exposed slot a call may find unwritten is not zeroed");
            Expect(b.Instrs[1] is { Op: MOp.Mov, Operands: [MMem { Disp: var d }, MImm { Value: 0 }] } zero && d == a && zero.Width == word,
                "the zeroing is not a whole word's store after the prologue");
        }

        // AN ACCESS UNDER AN INDEX reaches wherever the index takes it: every
        // slot is named at every call.
        {
            MFunction m = Function("indexed", out int a, out int other);
            MInstr call = Call();
            MBlock b = m.NewBlock("entry");
            MMem indexed = new(MReg.Of(Gpr.Ebp), a) { Index = MReg.Of(Gpr.Ecx), Scale = word };
            b.Instrs.AddRange(new[] { new MInstr(MOp.Prologue), call, new MInstr(MOp.Mov, MReg.Of(Gpr.Eax), indexed), new MInstr(MOp.Epilogue), new MInstr(MOp.Ret) });
            var r = Measure(m, "indexed");
            Expect(r.At[call].Contains(a) && r.At[call].Contains(other), "an indexed frame access leaves a slot out of a map");
        }

        // HALF A WORD STORED is no whole store: the word read after the call
        // is named at it, and zeroed, having never been written whole.
        {
            MFunction m = Function("half-stored", out int a, out _);
            MInstr call = Call();
            MBlock b = m.NewBlock("entry");
            b.Instrs.AddRange(new[] { new MInstr(MOp.Prologue), Store(a, word / 2), call, Load(a), new MInstr(MOp.Epilogue), new MInstr(MOp.Ret) });
            var r = Measure(m, "half-stored");
            Expect(r.At[call].Contains(a), "a word read after a call is not named at it");
            Expect(r.Zero.Contains(a), "a word stored only in part is taken for written");
        }

        // A STORE AFTER A BRANCH INSIDE ITS BLOCK is made only where the
        // branch was not taken: the way that jumped reads the word unwritten.
        {
            MFunction m = Function("stored-after-branch", out int a, out _);
            MInstr first = Call(), second = Call();
            MBlock entry = m.NewBlock("entry"), join = m.NewBlock("join");
            entry.Instrs.AddRange(new[] { new MInstr(MOp.Prologue), first, new MInstr(MOp.Jcc, new MLabel(join)) { Cond = Cond.E }, Store(a, word) });
            join.Instrs.AddRange(new[] { second, Load(a), new MInstr(MOp.Epilogue), new MInstr(MOp.Ret) });
            var r = Measure(m, "stored-after-branch");
            Expect(r.At[first].Contains(a), "a store after a branch was taken as made on the branch's way");
            Expect(r.At[second].Contains(a), "a word read after a call is not named at it");
            Expect(r.Zero.Contains(a), "a word one way leaves unwritten is not zeroed");
        }
    }

    private static void Holds(MOp op, string what, RegSet set, int[] expected)
    {
        for (int reg = 0; reg < 64; reg++)
            if (set.Contains(reg) != expected.Contains(reg))
                throw new InvalidOperationException($"x86 family: {op} {what} register {reg}: the set says {set.Contains(reg)}");
        if (set.Contains(64) || set.Contains(-1)) throw new InvalidOperationException($"x86 family: {op} {what} a register no mask has");
    }

    private static void Listed(string what, RegSet set, params int[] expected)
    {
        if (!set.Ids.SequenceEqual(expected))
            throw new InvalidOperationException($"x86 family: {what} [{string.Join(", ", set.Ids)}], not [{string.Join(", ", expected)}]");
    }
}
