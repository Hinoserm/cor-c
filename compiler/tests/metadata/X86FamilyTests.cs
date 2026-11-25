using Corsac.Lang.Ir;
using Corsac.Lang.X86;

namespace Corsac.Tests.Metadata;

/// <summary>
/// What the x86 family's two modes must agree with themselves about.
///
/// Long mode's unnamed registers are told two ways -- as lists, in the order
/// the allocation names them (Implicit.Uses, Implicit.Defs), and as masks,
/// for the passes that ask of every instruction (Implicit.Masks). Every
/// opcode, with every count of arguments a call may carry, must give the
/// same registers both ways: a register in one and not the other is a value
/// a pass takes for dead that the allocation kept, or the reverse.
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
        X86Mode.Long = true;
        try
        {
            int asked = 0;
            foreach (MOp op in Enum.GetValues<MOp>())
            {
                // An asm block's are its own record's, read by both from it.
                if (op == MOp.InlineAsm) continue;
                for (int ints = 0; ints <= LongAbi.IntArgRegs.Length; ints++)
                    for (int floats = 0; floats <= LongAbi.FloatArgRegs; floats++)
                        foreach (bool al in new[] { false, true })
                        {
                            MInstr i = new(op) { IntArgs = ints, FloatArgs = floats, NativeAl = al };
                            Implicit.Masks(i, out long uses, out long defs);
                            Same(op, "reads", Mask(Implicit.Uses(i)), uses);
                            Same(op, "writes", Mask(Implicit.Defs(i)), defs);
                            asked++;
                        }
            }
            Console.WriteLine("x86 family: long mode's unnamed registers are the same as lists and as masks, " + asked + " instructions asked");
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

    private static void Same(MOp op, string what, long listed, long masked)
    {
        if (listed != masked)
            throw new InvalidOperationException($"x86 family: {op} {what} {listed:X} as a list and {masked:X} as a mask");
    }
}
