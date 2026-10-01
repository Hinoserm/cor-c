#nullable enable
using Corsac.Lang.Ir;

namespace Corsac.Lang.Opt;

using Block = Corsac.Lang.Ir.Block;

/// <summary>
/// FRAME SLOTS WHOSE LIVES DO NOT MEET SHARE THEIR BYTES. Every temporary the
/// lowering makes -- an interpolated string's builder, a struct passed by
/// reference, an object the lifetime rules placed in the frame -- is a slot of
/// its own, and inlining multiplies them: a function that formats twenty
/// messages had twenty builders and a two-kilobyte frame, and every access to
/// most of it carried a four-byte displacement. Two slots never alive at the
/// same point can be the same bytes.
///
/// A slot is alive at a point that one of its uses can reach and from which
/// another can be reached, over the control flow graph -- loops included, so
/// a slot used at a loop's top and bottom is alive all round it. A use is any
/// instruction naming the slot's address, or writing or reading a register
/// that holds an address inside it (copied, offset, joined). No slot shares
/// whose address could outlive what the graph shows: one stored into memory,
/// returned, thrown, handed to the kernel or used in a way this does not
/// know, or used in a landing pad or the code after one, which the graph does
/// not connect to the calls that unwind to it. A call may be handed the
/// address: nothing a callee is given by reference outlives the call, save
/// the address it answers with, which a struct's result buffer is.
///
/// Nothing depends on a slot starting out as zero: the lowering writes every
/// slot before reading it, so a slot that held another's bytes first is
/// indistinguishable. The collector reads frame slots as they are, and a
/// stale word in a shared one is one more conservative root at most.
/// </summary>
public static class SlotShare
{
    public static void Run(Function f)
    {
        if (f.Async is not null || f.Slots.Count < 2 || f.Blocks.Count == 0) return;
        Dictionary<FrameSlot, int> index = new(ReferenceEqualityComparer.Instance);
        for (int s = 0; s < f.Slots.Count; s++) index[f.Slots[s]] = s;
        int n = f.Slots.Count, words = (n + 63) / 64;

        // Registers holding an address inside a slot, to a fixed point.
        Dictionary<VReg, ulong[]> derived = new();
        bool Carries(Opcode op) => op is Opcode.Copy or Opcode.Add or Opcode.Sub or Opcode.Phi or Opcode.Trunc64 or Opcode.ZExt32 or Opcode.SExt32;
        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (Block b in f.Blocks)
                foreach (Instr i in b.Instrs)
                {
                    // A CALL HANDED AN ADDRESS MAY HAND IT BACK: a struct's
                    // result is written to the caller's buffer and its address
                    // returned, and the result is that slot for as long as it
                    // is used -- shared at the call, the next call's result
                    // was written over it.
                    if (i.Dest is null || !Carries(i.Op) && i.Op is not (Opcode.Call or Opcode.CallIndirect)) continue;
                    ulong[]? into = null;
                    foreach (Operand o in i.Operands)
                    {
                        ulong[]? from = o switch
                        {
                            SlotOperand { Slot: var slot } when index.TryGetValue(slot, out int s) => Single(s, words),
                            RegOperand { Reg: var r } when derived.TryGetValue(r, out ulong[]? bits) => bits,
                            _ => null,
                        };
                        if (from is null) continue;
                        if (!derived.TryGetValue(i.Dest, out into)) derived[i.Dest] = into = new ulong[words];
                        for (int w = 0; w < words; w++)
                            if ((into[w] | from[w]) != into[w]) { into[w] |= from[w]; changed = true; }
                    }
                }
        }

        // Every slot an instruction uses, and the slots that may not share.
        ulong[] poisoned = new ulong[words];
        Dictionary<Instr, ulong[]> uses = new(ReferenceEqualityComparer.Instance);
        foreach (Block b in f.Blocks)
            foreach (Instr i in b.Instrs)
            {
                ulong[]? used = null;
                void Use(ulong[] bits, bool escapes)
                {
                    used ??= new ulong[words];
                    for (int w = 0; w < words; w++) { used[w] |= bits[w]; if (escapes) poisoned[w] |= bits[w]; }
                }
                for (int k = 0; k < i.Operands.Count; k++)
                {
                    ulong[]? bits = i.Operands[k] switch
                    {
                        SlotOperand { Slot: var slot } when index.TryGetValue(slot, out int s) => Single(s, words),
                        RegOperand { Reg: var r } when derived.TryGetValue(r, out ulong[]? d) => d,
                        _ => null,
                    };
                    if (bits is null) continue;
                    Use(bits, !Harmless(i, k));
                }
                if (i.Dest is not null && derived.TryGetValue(i.Dest, out ulong[]? made)) Use(made, false);
                if (used is not null) uses[i] = used;
            }

        // Landing pads and what follows them are not joined to the calls that
        // unwind there: anything used in them keeps its own bytes.
        Cfg cfg = new(f);
        HashSet<Block> afterPad = new(ReferenceEqualityComparer.Instance);
        Stack<Block> pending = new();
        foreach (Block b in f.Blocks) if (b.IsLandingPad && afterPad.Add(b)) pending.Push(b);
        while (pending.Count > 0)
            foreach (Block s in cfg.Succs(pending.Pop()))
                if (afterPad.Add(s)) pending.Push(s);
        foreach (Block b in afterPad)
            foreach (Instr i in b.Instrs)
                if (uses.TryGetValue(i, out ulong[]? bits))
                    for (int w = 0; w < words; w++) poisoned[w] |= bits[w];

        // Used before (a use can reach here) and after (a use is reachable).
        Dictionary<Block, ulong[]> blockUses = new(ReferenceEqualityComparer.Instance);
        foreach (Block b in f.Blocks)
        {
            ulong[] all = new ulong[words];
            foreach (Instr i in b.Instrs)
                if (uses.TryGetValue(i, out ulong[]? bits)) Or(all, bits);
            blockUses[b] = all;
        }
        Dictionary<Block, ulong[]> beforeIn = new(ReferenceEqualityComparer.Instance), afterOut = new(ReferenceEqualityComparer.Instance);
        foreach (Block b in f.Blocks) { beforeIn[b] = new ulong[words]; afterOut[b] = new ulong[words]; }
        for (changed = true; changed;)
        {
            changed = false;
            foreach (Block b in f.Blocks)
            {
                ulong[] into = beforeIn[b];
                foreach (Block p in cfg.Preds(b))
                {
                    ulong[] pin = beforeIn[p], pu = blockUses[p];
                    for (int w = 0; w < words; w++)
                    {
                        ulong v = pin[w] | pu[w];
                        if ((into[w] | v) != into[w]) { into[w] |= v; changed = true; }
                    }
                }
            }
        }
        for (changed = true; changed;)
        {
            changed = false;
            for (int k = f.Blocks.Count - 1; k >= 0; k--)
            {
                Block b = f.Blocks[k];
                ulong[] into = afterOut[b];
                foreach (Block s in cfg.Succs(b))
                {
                    ulong[] sout = afterOut[s], su = blockUses[s];
                    for (int w = 0; w < words; w++)
                    {
                        ulong v = sout[w] | su[w];
                        if ((into[w] | v) != into[w]) { into[w] |= v; changed = true; }
                    }
                }
            }
        }

        // WHICH SLOTS ARE ALIVE TOGETHER, block by block, as intervals: in a
        // block a slot is alive from its first use there (or the block's top,
        // if a use before can reach it) to its last (or the block's end, if a
        // use after is reachable); one with no use in the block is alive
        // through it exactly when both hold. Two slots meet where their
        // intervals do, found by a sweep -- no set per instruction.
        ulong[][] conflicts = new ulong[n][];
        for (int s = 0; s < n; s++) conflicts[s] = new ulong[words];
        ulong[] everUsed = new ulong[words];
        Dictionary<int, (int First, int Last)> spans = new();
        List<(int From, int To, int Slot)> intervals = new();
        foreach (Block b in f.Blocks)
        {
            int count = b.Instrs.Count;
            spans.Clear();
            for (int k = 0; k < count; k++)
            {
                if (!uses.TryGetValue(b.Instrs[k], out ulong[]? here)) continue;
                Or(everUsed, here);
                for (int w = 0; w < words; w++)
                    for (ulong bits = here[w]; bits != 0; bits &= bits - 1)
                    {
                        int slot = w * 64 + System.Numerics.BitOperations.TrailingZeroCount(bits);
                        spans[slot] = spans.TryGetValue(slot, out var had) ? (had.First, k) : (k, k);
                    }
            }
            intervals.Clear();
            ulong[] into = beforeIn[b], outOf = afterOut[b];
            foreach ((int slot, (int first, int last)) in spans)
                intervals.Add((Test(into, slot) ? 0 : first, Test(outOf, slot) ? count : last, slot));
            for (int w = 0; w < words; w++)
                for (ulong bits = into[w] & outOf[w]; bits != 0; bits &= bits - 1)
                {
                    int slot = w * 64 + System.Numerics.BitOperations.TrailingZeroCount(bits);
                    if (!spans.ContainsKey(slot)) intervals.Add((0, count, slot));
                }
            intervals.Sort((x, y) => x.From.CompareTo(y.From));
            for (int i = 0; i < intervals.Count; i++)
                for (int j = i + 1; j < intervals.Count && intervals[j].From <= intervals[i].To; j++)
                {
                    int x = intervals[i].Slot, y = intervals[j].Slot;
                    conflicts[x][y / 64] |= 1UL << (y % 64);
                    conflicts[y][x / 64] |= 1UL << (x % 64);
                }
        }

        // Greedy by size: each slot joins the first group it meets no member of.
        List<int> order = Enumerable.Range(0, n).Where(s => Test(everUsed, s) && !Test(poisoned, s))
            .OrderByDescending(s => f.Slots[s].Bytes).ThenBy(s => s).ToList();
        List<(List<int> Members, ulong[] Busy)> groups = new();
        foreach (int s in order)
        {
            (List<int> Members, ulong[] Busy)? home = null;
            foreach (var g in groups)
                if (!Test(g.Busy, s)) { home = g; break; }
            if (home is null) groups.Add((new List<int> { s }, (ulong[])conflicts[s].Clone()));
            else { home.Value.Members.Add(s); Or(home.Value.Busy, conflicts[s]); }
        }
        Dictionary<FrameSlot, FrameSlot> into2 = new(ReferenceEqualityComparer.Instance);
        foreach (var (members, _) in groups)
        {
            if (members.Count < 2) continue;
            int bytes = members.Max(s => f.Slots[s].Bytes), align = members.Max(s => f.Slots[s].Align);
            FrameSlot first = f.Slots[members[0]];
            FrameSlot shared = first.Bytes >= bytes && first.Align >= align ? first : f.NewSlot(bytes, align, first.Name);
            foreach (int s in members) if (!ReferenceEquals(f.Slots[s], shared)) into2[f.Slots[s]] = shared;
        }
        // Slots nothing uses are dropped too.
        HashSet<FrameSlot> unused = new(ReferenceEqualityComparer.Instance);
        for (int s = 0; s < n; s++) if (!Test(everUsed, s)) unused.Add(f.Slots[s]);
        if (into2.Count == 0 && unused.Count == 0) return;
        foreach (Block b in f.Blocks)
            foreach (Instr i in b.Instrs)
                for (int k = 0; k < i.Operands.Count; k++)
                    if (i.Operands[k] is SlotOperand { Slot: var slot } && into2.TryGetValue(slot, out FrameSlot? to))
                        i.Operands[k] = new SlotOperand(to);
        f.Slots.RemoveAll(slot => into2.ContainsKey(slot) || unused.Contains(slot));
    }

    /// <summary>
    /// Whether an address as operand <paramref name="k"/> of <paramref name="i"/>
    /// is only used there -- read or written through, compared, carried on,
    /// handed to a call -- and not kept anywhere the graph does not follow.
    /// </summary>
    private static bool Harmless(Instr i, int k) => i.Op switch
    {
        Opcode.Copy or Opcode.Add or Opcode.Sub or Opcode.Phi or Opcode.Trunc64 or Opcode.ZExt32 or Opcode.SExt32 => true,
        Opcode.Load or Opcode.ArrayLength or Opcode.InitArrayLength => true,
        Opcode.Store => k == 0,
        Opcode.MemCopy or Opcode.MemSet => true,
        Opcode.AtomicSwap or Opcode.AtomicAdd or Opcode.AtomicAnd or Opcode.AtomicOr or Opcode.AtomicXor or Opcode.AtomicCas => k == 0,
        Opcode.Eq or Opcode.Ne or Opcode.LtS or Opcode.LeS or Opcode.GtS or Opcode.GeS
            or Opcode.LtU or Opcode.LeU or Opcode.GtU or Opcode.GeU => true,
        // What the backend answers itself (`__x86.*`, handler frames) may
        // keep the address past the call; a method may not.
        Opcode.Call => i.Callee is not null && !i.Callee.StartsWith("__", StringComparison.Ordinal),
        Opcode.CallIndirect => k > 0,
        Opcode.Branch => true,
        _ => false,
    };

    private static ulong[] Single(int s, int words)
    {
        ulong[] bits = new ulong[words];
        bits[s / 64] |= 1UL << (s % 64);
        return bits;
    }

    private static bool Test(ulong[] bits, int s) => (bits[s / 64] & (1UL << (s % 64))) != 0;

    private static void Or(ulong[] into, ulong[] from)
    {
        for (int w = 0; w < into.Length; w++) into[w] |= from[w];
    }
}
