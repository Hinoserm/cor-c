#nullable enable
using Corsac.Lang.Frames;
using Corsac.Lang.Ir;

namespace Corsac.Lang.X86;

/// <summary>
/// i386's part of the frame slots' liveness (Frames.FrameSlotLiveness, where
/// the rules and their reasons are): this machine's final code read into a
/// FrameCode, and the zeroing the analysis asks for written after the
/// prologue.
///
/// HOW i386's CODE NAMES A FRAME WORD: a displacement off EBP, below it, in
/// an operand that is no spill slot of the allocator's (MMem.IsSpill: those
/// are the map's own, live or not by the allocator's word).
///
///   A plain 32-bit MOV to such a word, from a register or a number, is a
///   whole store. A MOV or MOVF store of any other width or place reads
///   nothing and is no whole store. A 32-bit MOV from the word reads the
///   word; any other operand naming it reads its slot. LEA of it takes its
///   slot's address.
///
///   EBP read as a register (a handler's record of its frame, an intrinsic)
///   is the frame's base escaping. EBP as an index, inline assembly or a
///   native instruction is opaque.
///
/// A function with no frame pointer (MFunction.Frameless) has no map to
/// improve and is not read.
/// </summary>
internal static class FrameSlotLiveness
{
    private const int Ebp = (int)Gpr.Ebp;

    // How it went, for --report-phases: functions read, those left as they
    // were and why, calls, the words their maps name and the words they
    // would have (every slot word at every call), the words zeroed at entry.
    private static long _functions, _left, _calls, _named, _whole, _zeroed;
    private static readonly long[] _why = new long[Frames.FrameSlotLiveness.Reasons + 2];
    private const int LeftFrameless = Frames.FrameSlotLiveness.Reasons, LeftPrologue = Frames.FrameSlotLiveness.Reasons + 1;

    public static string Stats()
        => "frame slot liveness: " + Interlocked.Read(ref _functions) + " functions, " + Interlocked.Read(ref _left) + " left whole ("
           + _why[Frames.FrameSlotLiveness.LeftOpaque] + " opaque, " + _why[Frames.FrameSlotLiveness.LeftOverlap] + " overlapping slots, "
           + _why[Frames.FrameSlotLiveness.LeftEntry] + " entry jumped to, " + _why[Frames.FrameSlotLiveness.LeftUnsettled] + " unsettled, "
           + _why[LeftFrameless] + " frameless, " + _why[LeftPrologue] + " prologue elsewhere), "
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
        if (m.Frameless || m.Blocks.Count == 0) return Left(LeftFrameless);
        // The zeroing goes after the prologue, which is the entry block's
        // first instruction (Select) and nowhere else: before it EBP is
        // still the caller's.
        List<MInstr> entry = m.Blocks[0].Instrs;
        if (entry.Count == 0 || entry[0].Op != MOp.Prologue) return Left(LeftPrologue);
        for (int b = 0; b < m.Blocks.Count; b++)
            for (int k = b == 0 ? 1 : 0; k < m.Blocks[b].Instrs.Count; k++)
                if (m.Blocks[b].Instrs[k].Op == MOp.Prologue) return Left(LeftPrologue);

        Frames.FrameSlotLiveness.Result? result = Frames.FrameSlotLiveness.Run(Read(m), out int why);
        if (result is null) return Left(why);
        if (result.Zero.Length > 0)
        {
            // Plain stores: they change no register and no flag.
            List<MInstr> zeroing = new(result.Zero.Length);
            foreach (int offset in result.Zero) zeroing.Add(new MInstr(MOp.Mov, MMem.Frame(offset), new MImm(0)));
            entry.InsertRange(1, zeroing);
        }
        int slotWords = 0;
        foreach ((int offset, int bytes) in m.Frame.SlotRanges())
            if (offset < 0) slotWords += (offset + bytes - (offset & ~3) + 3) / 4;
        long named = 0;
        foreach (int[] words in result.At.Values) named += words.Length;
        Interlocked.Add(ref _calls, result.At.Count);
        Interlocked.Add(ref _named, named);
        Interlocked.Add(ref _whole, (long)slotWords * result.At.Count);
        Interlocked.Add(ref _zeroed, result.Zero.Length);
        return result;
    }

    /// <summary>The function's final code as the frame analyses read it.</summary>
    private static FrameCode Read(MFunction m)
    {
        FrameCode code = new(4, m.Frame.Size);
        foreach ((int offset, int bytes) in m.Frame.SlotRanges()) code.Slot(offset, bytes);
        Dictionary<MBlock, int> index = new(ReferenceEqualityComparer.Instance);
        foreach (MBlock b in m.Blocks) index[b] = code.NewBlock(b.Source is { IsLandingPad: true });
        List<MBlock> into = new();
        for (int b = 0; b < m.Blocks.Count; b++)
        {
            m.SuccessorsInto(b, into);
            foreach (MBlock to in into)
            {
                // A jump out of the blocks known: not a shape this reads.
                if (index.TryGetValue(to, out int t)) code.Edge(b, t);
                else code.Opaque = true;
            }
            foreach (MInstr i in m.Blocks[b].Instrs)
            {
                bool call = i.Op is MOp.Call or MOp.CallInd or MOp.CallSafe || m.Safepoints.ContainsKey(i);
                code.Instr(b, call ? i : null);
                if (i.Asm is not null || i.Native) code.Opaque = true;
                bool wholeStore = i.Op == MOp.Mov && i.Width == 4 && !i.Lock && i.Operands.Count == 2
                    && i.Operands[0] is MMem { Index: null } stored && (stored.Disp & 3) == 0 && i.Operands[1] is not MMem;
                bool otherStore = !wholeStore && i.Op is MOp.Mov or MOp.MovF && i.Operands.Count == 2
                    && i.Operands[0] is MMem && i.Operands[1] is not MMem && !i.Lock;
                for (int n = 0; n < i.Operands.Count; n++)
                {
                    MOperand o = i.Operands[n];
                    if (o is MReg { Id: Ebp }) code.BaseEscapes = true;
                    if (o is not MMem mem) continue;
                    if (mem.Index?.Id == Ebp) code.Opaque = true;
                    if (mem.Base?.Id != Ebp || mem.IsSpill || mem.Disp >= 0) continue;
                    if (i.Op == MOp.Lea) code.Does(b, FrameCode.Effect.Address, mem.Disp);
                    else if (n == 0 && wholeStore) code.Does(b, FrameCode.Effect.WholeStore, mem.Disp);
                    else if (n == 0 && otherStore) { }
                    else if (i.Op == MOp.Mov && i.Width == 4 && mem.Index is null && (mem.Disp & 3) == 0) code.Does(b, FrameCode.Effect.ReadWord, mem.Disp);
                    else code.Does(b, FrameCode.Effect.ReadSlot, mem.Disp);
                }
            }
        }
        return code;
    }
}
