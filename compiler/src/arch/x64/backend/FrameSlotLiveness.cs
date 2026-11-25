#nullable enable
using Corsac.Lang.Frames;
using Corsac.Lang.Ir;

namespace Corsac.Lang.X64;

/// <summary>
/// x86-64's part of the frame slots' liveness (Frames.FrameSlotLiveness,
/// where the rules and their reasons are): this machine's final code read
/// into a FrameCode, and the zeroing the analysis asks for written after
/// the prologue. The same reading as i386's, a word eight bytes:
///
///   A frame word is a displacement off RBP, below it, in an operand that
///   is no spill slot of the allocator's. A plain 64-bit MOV to it, from a
///   register or a number, is a whole store; a MOV or MOVF store of any
///   other width or place reads nothing and is no whole store; a 64-bit MOV
///   from it reads the word; any other operand naming it reads its slot;
///   LEA of it takes its slot's address. RBP read as a register is the
///   frame's base escaping; RBP as an index, or inline assembly, is opaque.
///
/// A function that gives RBP a value (MFunction.FramePointerFree, a leaf)
/// has no map to improve and is not read.
/// </summary>
internal static class FrameSlotLiveness
{
    private const int Rbp = (int)Gpr.Rbp;

    // How it went, for --report-phases (as i386's).
    private static long _functions, _left, _calls, _named, _whole, _zeroed;
    private static readonly long[] _why = new long[Frames.FrameSlotLiveness.Reasons + 2];
    private const int LeftFrameless = Frames.FrameSlotLiveness.Reasons, LeftPrologue = Frames.FrameSlotLiveness.Reasons + 1;

    public static string Stats()
        => "frame slot liveness: " + Interlocked.Read(ref _functions) + " functions, " + Interlocked.Read(ref _left) + " left whole ("
           + _why[Frames.FrameSlotLiveness.LeftOpaque] + " opaque, " + _why[Frames.FrameSlotLiveness.LeftOverlap] + " overlapping slots, "
           + _why[Frames.FrameSlotLiveness.LeftEntry] + " entry jumped to, " + _why[Frames.FrameSlotLiveness.LeftUnsettled] + " unsettled, "
           + _why[LeftFrameless] + " without a frame pointer, " + _why[LeftPrologue] + " prologue elsewhere), "
           + Interlocked.Read(ref _calls) + " calls naming " + Interlocked.Read(ref _named) + " of " + Interlocked.Read(ref _whole)
           + " slot words, " + Interlocked.Read(ref _zeroed) + " words zeroed at entry";

    private static Frames.FrameSlotLiveness.Result? Left(int why)
    {
        Interlocked.Increment(ref _left);
        Interlocked.Increment(ref _why[why]);
        return null;
    }

    /// <summary>
    /// Which IR slot words each call's map names, with the zeroing written
    /// into the function; null where every slot word stays in every map
    /// (--no-slot-liveness, or a function the analysis leaves as it was).
    /// </summary>
    public static Frames.FrameSlotLiveness.Result? Measured(MFunction m)
    {
        if (Switches.NoSlotLiveness) return null;
        Interlocked.Increment(ref _functions);
        if (m.FramePointerFree || m.Blocks.Count == 0) return Left(LeftFrameless);
        // The zeroing goes after the prologue, the entry block's first
        // instruction and nowhere else: before it RBP is still the caller's.
        List<MInstr> entry = m.Blocks[0].Instrs;
        if (entry.Count == 0 || entry[0].Op != MOp.Prologue) return Left(LeftPrologue);
        for (int b = 0; b < m.Blocks.Count; b++)
            for (int k = b == 0 ? 1 : 0; k < m.Blocks[b].Instrs.Count; k++)
                if (m.Blocks[b].Instrs[k].Op == MOp.Prologue) return Left(LeftPrologue);

        Frames.FrameSlotLiveness.Result? result = Frames.FrameSlotLiveness.Run(Read(m), out int why);
        if (result is null) return Left(why);
        if (result.Zero.Length > 0)
        {
            // Plain stores of a sign-extended zero: no register and no flag changed.
            List<MInstr> zeroing = new(result.Zero.Length);
            foreach (int offset in result.Zero) zeroing.Add(new MInstr(MOp.Mov, MMem.Frame(offset), new MImm(0)) { Width = 8 });
            entry.InsertRange(1, zeroing);
        }
        int slotWords = 0;
        foreach ((int offset, int bytes) in m.Frame.SlotRanges())
            if (offset < 0) slotWords += (offset + bytes - (offset & ~7) + 7) / 8;
        long named = 0;
        foreach (int[] words in result.At.Values) named += words.Length;
        Interlocked.Add(ref _calls, result.At.Count);
        Interlocked.Add(ref _named, named);
        Interlocked.Add(ref _whole, (long)slotWords * result.At.Count);
        Interlocked.Add(ref _zeroed, result.Zero.Length);
        return result;
    }

    /// <summary>
    /// The function's final code as the frame analyses read it. A block
    /// with a branch before its last instruction is read as the blocks it
    /// really is -- what follows the branch runs only where it was not
    /// taken -- so a store after a branch is never taken as made on the
    /// branch's path.
    /// </summary>
    private static FrameCode Read(MFunction m)
    {
        FrameCode code = new(8, m.Frame.Size);
        foreach ((int offset, int bytes) in m.Frame.SlotRanges()) code.Slot(offset, bytes);
        Dictionary<MBlock, int> index = new(ReferenceEqualityComparer.Instance);
        foreach (MBlock b in m.Blocks) index[b] = code.NewBlock(b.Source is { IsLandingPad: true });
        void Edge(int from, MBlock to)
        {
            // A jump out of the blocks known: not a shape this reads.
            if (index.TryGetValue(to, out int t)) code.Edge(from, t);
            else code.Opaque = true;
        }
        for (int b = 0; b < m.Blocks.Count; b++)
        {
            List<MInstr> instrs = m.Blocks[b].Instrs;
            int at = index[m.Blocks[b]];
            // Whether control can run off the end of the part being read.
            bool falls = true;
            for (int k = 0; k < instrs.Count; k++)
            {
                MInstr i = instrs[k];
                bool call = i.Op is MOp.Call or MOp.CallInd or MOp.CallSafe || m.Safepoints.ContainsKey(i);
                code.Instr(at, call ? i : null);
                if (i.Asm is not null) code.Opaque = true;
                bool wholeStore = i.Op == MOp.Mov && i.Width == 8 && !i.Lock && i.Operands.Count == 2
                    && i.Operands[0] is MMem { Index: null } stored && (stored.Disp & 7) == 0 && i.Operands[1] is not MMem;
                bool otherStore = !wholeStore && i.Op is MOp.Mov or MOp.MovF && i.Operands.Count == 2
                    && i.Operands[0] is MMem && i.Operands[1] is not MMem && !i.Lock;
                for (int n = 0; n < i.Operands.Count; n++)
                {
                    MOperand o = i.Operands[n];
                    if (o is MReg { Id: Rbp }) code.BaseEscapes = true;
                    if (o is not MMem mem) continue;
                    if (mem.Index?.Id == Rbp) code.Opaque = true;
                    if (mem.Base?.Id != Rbp || mem.IsSpill) continue;
                    // AN INDEXED ACCESS reaches wherever its index takes it,
                    // whatever slot its displacement happens to lie in (a
                    // constant folded into it may put it below its array's
                    // first element, or in the slot beside): every slot, as
                    // the base escaping.
                    if (mem.Index is not null) { code.BaseEscapes = true; continue; }
                    // An address above the base, or the base's own, is a
                    // pointer into the frame all the same (Effect.Address).
                    if (i.Op == MOp.Lea) { code.Does(at, FrameCode.Effect.Address, mem.Disp); continue; }
                    if (mem.Disp >= 0) continue;
                    if (n == 0 && wholeStore) code.Does(at, FrameCode.Effect.WholeStore, mem.Disp);
                    else if (n == 0 && otherStore) { }
                    else if (i.Op == MOp.Mov && i.Width == 8 && (mem.Disp & 7) == 0) code.Does(at, FrameCode.Effect.ReadWord, mem.Disp);
                    else code.Does(at, FrameCode.Effect.ReadSlot, mem.Disp);
                }
                bool jump = i.Op is MOp.Jmp or MOp.Jcc;
                if (jump) Edge(at, ((MLabel)i.Operands[0]).Target);
                else if (i.Op == MOp.JmpTable) foreach (MBlock to in i.Table!) Edge(at, to);
                bool ends = i.Op is MOp.Jmp or MOp.JmpTable or MOp.JmpInd or MOp.Ret or MOp.Epilogue;
                if ((jump || ends || i.Op == MOp.JmpTable) && k + 1 < instrs.Count)
                {
                    // What follows a branch inside a block: a part of its own.
                    int next = code.NewBlock(false);
                    if (!ends) code.Edge(at, next);
                    at = next;
                }
                falls = !ends;
            }
            if (falls && !m.Blocks[b].EndsUnconditionally && b + 1 < m.Blocks.Count) code.Edge(at, index[m.Blocks[b + 1]]);
        }
        return code;
    }
}
