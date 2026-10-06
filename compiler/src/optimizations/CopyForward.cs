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
                        || ReferenceEquals(to, from)) continue;
                    if (Forward(f, b, k, dst, to, src, from, slotOf))
                    {
                        changed = true;
                        break;
                    }
                }
                if (changed) break;
            }
        }
    }

    private static bool Forward(Function f, Block home, int at, VReg dst, FrameSlot to, VReg src, FrameSlot from,
        Dictionary<VReg, FrameSlot> slotOf)
    {
        bool Names(Instr i, FrameSlot slot) =>
            i.Operands.Any(o => o is SlotOperand s && ReferenceEquals(s.Slot, slot)
                || o is RegOperand r && slotOf.TryGetValue(r.Reg, out FrameSlot? of) && ReferenceEquals(of, slot));
        List<Instr> zeroings = new();
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
                if (Names(i, from))
                {
                    bool definesAddress = i.Op == Opcode.Copy && i.Dest is not null && slotOf.TryGetValue(i.Dest, out FrameSlot? d) && ReferenceEquals(d, from);
                    if (!before && !definesAddress) return false;
                }

                // THE COPY: made, zeroed, and then only read -- by loads
                // through it or as a call's argument -- after the copy.
                if (Names(i, to))
                {
                    if (i.Op == Opcode.Copy && i.Dest is not null && slotOf.TryGetValue(i.Dest, out FrameSlot? d) && ReferenceEquals(d, to)) continue;
                    if (before && i.Op == Opcode.MemSet && i.Operands[1] is ImmOperand { Value: 0 }) { zeroings.Add(i); continue; }
                    if (!after) return false;
                    bool reads = i.Op == Opcode.Load && i.Operands.Count == 1
                        || i.Op is Opcode.Call or Opcode.CallIndirect
                            && !i.Operands.Any(o => o is SlotOperand s && ReferenceEquals(s.Slot, to));
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
        foreach (Instr z in zeroings) home.Instrs.Remove(z);
        return true;
    }
}
