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
                    Same(op, "reads", Mask(uses), Implicit.Uses(plain).Mask);
                    Same(op, "writes", Mask(defs), Implicit.Defs(plain).Mask);
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
                Corsac.Lang.X64.Allocator.Run(m);
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
        if (!held) throw new InvalidOperationException("x86 family, frame reading (" + (X86Mode.Long ? "long mode" : "i386") + "): " + what);
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

    private static long Mask(IEnumerable<int> ids)
    {
        long mask = 0;
        foreach (int id in ids) mask |= 1L << id;
        return mask;
    }

    private static void Listed(string what, RegSet set, params int[] expected)
    {
        if (!set.Ids.SequenceEqual(expected))
            throw new InvalidOperationException($"x86 family: {what} [{string.Join(", ", set.Ids)}], not [{string.Join(", ", expected)}]");
    }

    private static void Same(MOp op, string what, long listed, long masked)
    {
        if (listed != masked)
            throw new InvalidOperationException($"x86 family: {op} {what} {listed:X} as a list and {masked:X} as a mask");
    }
}
