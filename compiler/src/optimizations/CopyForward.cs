#nullable enable
using Corsac.Lang.Ir;

namespace Corsac.Lang.Opt;

using Block = Corsac.Lang.Ir.Block;

/// <summary>
/// A COPY OF A STRUCT INTO A SLOT NOBODY NEEDED. A value passed by value is
/// copied into a slot of its own for the call -- the callee may write its
/// parameter, and the caller's must not change -- and the copy is the whole
/// cost of passing a span: the slot zeroed, the three words moved, where C
/// would push the address it had. When the slot copied FROM is never touched
/// again, in this block or any other, nothing can tell the two apart: the
/// copy's readers read the original instead, and the copy and the zeroing of
/// its slot go.
///
/// Both ends must be frame slots whose address is only ever taken by a copy
/// of the slot; the copy's slot must be read only after the copy, in the same
/// block, and written by nothing but its zeroing before it. Anything else is
/// left as it is.
/// </summary>
public sealed class CopyForward : IPass
{
    public string Name => "copy-forward";

    public void Run(Function f)
    {
        // Which slot each register is the address of, where a copy made it.
        Dictionary<VReg, FrameSlot> slotOf = new();
        // Counted by register number: a dictionary of every register here,
        // on every run, was the pass's largest allocation.
        int[] writes = new int[f.RegCount];
        foreach (Block b in f.Blocks)
            foreach (Instr i in b.Instrs)
                if (i.Dest is not null)
                {
                    writes[i.Dest.Id]++;
                    if (i.Op == Opcode.Copy && i.Operands is [SlotOperand s]) slotOf[i.Dest] = s.Slot;
                }
        foreach (VReg p in f.Params) writes[p.Id]++;
        List<VReg>? twice = null;
        foreach (VReg r in slotOf.Keys) if (writes[r.Id] != 1) (twice ??= new()).Add(r);
        if (twice is not null) foreach (VReg r in twice) slotOf.Remove(r);
        if (slotOf.Count == 0) return;

        // A COPY FOUND NOT TO GO is not asked again until a copy that does go
        // touches one of its two slots. The walk starts over after every
        // copy it forwards, and asked every copy before it again each time,
        // each question a walk of the whole function: copies times copies
        // times instructions, in a function of many struct arguments. What a
        // copy's answer reads is only what names its own two slots and where
        // that stands against the copy; forwarding another copy changes only
        // what names that copy's slots (its readers now name the original,
        // the copy and its zeroing go). So an answer stands while neither of
        // its slots is one of those, and the first copy that goes is the one
        // the walk from the start would have found.
        Dictionary<Instr, (FrameSlot To, FrameSlot From)> refused = new(ReferenceEqualityComparer.Instance);
        List<Instr> stale = new();
        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (Block b in f.Blocks)
            {
                for (int k = 0; k < b.Instrs.Count; k++)
                {
                    Instr copy = b.Instrs[k];
                    if (copy.Op != Opcode.MemCopy || copy.Operands.Count != 3
                        || copy.Operands[0] is not RegOperand { Reg: var dst } || copy.Operands[1] is not RegOperand { Reg: var src }
                        || !slotOf.TryGetValue(dst, out FrameSlot? to) || !slotOf.TryGetValue(src, out FrameSlot? from)
                        || ReferenceEquals(to, from) || refused.ContainsKey(copy)) continue;
                    if (Forward(f, b, k, dst, to, src, from, slotOf))
                    {
                        changed = true;
                        foreach (var (asked, slots) in refused)
                            if (ReferenceEquals(slots.To, to) || ReferenceEquals(slots.To, from)
                                || ReferenceEquals(slots.From, to) || ReferenceEquals(slots.From, from)) stale.Add(asked);
                        foreach (Instr asked in stale) refused.Remove(asked);
                        stale.Clear();
                        break;
                    }
                    refused[copy] = (to, from);
                }
                if (changed) break;
            }
        }
    }

    /// <summary>Whether an instruction names a slot: the slot itself, or a register holding its address (slotOf).</summary>
    private static bool Names(Instr i, FrameSlot slot, Dictionary<VReg, FrameSlot> slotOf)
    {
        // A loop, not LINQ: asked of every instruction for every copy, the
        // closure and the operand list's boxed enumerator each time were
        // most of what the whole optimiser allocated.
        for (int n = 0; n < i.Operands.Count; n++)
        {
            Operand o = i.Operands[n];
            if (o is SlotOperand s && ReferenceEquals(s.Slot, slot)
                || o is RegOperand r && slotOf.TryGetValue(r.Reg, out FrameSlot? of) && ReferenceEquals(of, slot)) return true;
        }
        return false;
    }

    /// <summary>Whether an instruction has the slot itself among its operands.</summary>
    private static bool HasSlot(Instr i, FrameSlot slot)
    {
        for (int n = 0; n < i.Operands.Count; n++)
            if (i.Operands[n] is SlotOperand s && ReferenceEquals(s.Slot, slot)) return true;
        return false;
    }

    private static bool Forward(Function f, Block home, int at, VReg dst, FrameSlot to, VReg src, FrameSlot from,
        Dictionary<VReg, FrameSlot> slotOf)
    {
        List<Instr>? zeroings = null;
        foreach (Block b in f.Blocks)
        {
            for (int k = 0; k < b.Instrs.Count; k++)
            {
                Instr i = b.Instrs[k];
                bool after = ReferenceEquals(b, home) && k > at;
                bool before = ReferenceEquals(b, home) && k < at;
                if (ReferenceEquals(b, home) && k == at) continue;

                // THE ORIGINAL: nothing may name it after the copy, anywhere
                // -- in this block after it, or in any other block at all.
                if (Names(i, from, slotOf))
                {
                    bool definesAddress = i.Op == Opcode.Copy && i.Dest is not null && slotOf.TryGetValue(i.Dest, out FrameSlot? d) && ReferenceEquals(d, from);
                    if (!before && !definesAddress) return false;
                }

                // THE COPY: made, zeroed, and then only read -- by loads
                // through it or as a call's argument -- after the copy.
                if (Names(i, to, slotOf))
                {
                    if (i.Op == Opcode.Copy && i.Dest is not null && slotOf.TryGetValue(i.Dest, out FrameSlot? d) && ReferenceEquals(d, to)) continue;
                    if (before && i.Op == Opcode.MemSet && i.Operands[1] is ImmOperand { Value: 0 }) { (zeroings ??= new()).Add(i); continue; }
                    if (!after) return false;
                    bool reads = i.Op == Opcode.Load && i.Operands.Count == 1
                        || i.Op is Opcode.Call or Opcode.CallIndirect
                            && !HasSlot(i, to);
                    if (!reads) return false;
                }
            }
        }

        // The copy's readers read the original.
        for (int k = at + 1; k < home.Instrs.Count; k++)
        {
            Instr i = home.Instrs[k];
            for (int n = 0; n < i.Operands.Count; n++)
                if (i.Operands[n] is RegOperand { Reg: var r } && slotOf.TryGetValue(r, out FrameSlot? of) && ReferenceEquals(of, to))
                    i.Operands[n] = RegOperand.Of(src);
                else if (i.Operands[n] is SlotOperand s && ReferenceEquals(s.Slot, to))
                    i.Operands[n] = new SlotOperand(from);
        }
        home.Instrs.RemoveAt(at);
        if (zeroings is not null) foreach (Instr z in zeroings) home.Instrs.Remove(z);
        return true;
    }
}
