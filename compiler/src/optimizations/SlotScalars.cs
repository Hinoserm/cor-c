#nullable enable
using Corsac.Lang.Ir;

namespace Corsac.Lang.Opt;

using Block = Corsac.Lang.Ir.Block;

/// <summary>
/// A STRUCT IN THE FRAME WHOSE ADDRESS GOES NOWHERE, KEPT IN REGISTERS. A
/// struct a call returns, or one a constructor fills, lives in a frame slot,
/// and once its methods are inlined every use of it is a load or a store at
/// a constant offset of that slot, a fill of it, or a copy of it to another
/// such slot. List's enumerator in every foreach was that: built in one
/// slot, copied whole to another with `rep movsd`, and its index and current
/// element stored and read back through the frame on every lap.
///
/// Such a slot becomes one register a word: a load of a word is a copy from
/// its register, a store a copy to it, a fill a copy of the byte pattern to
/// each, a copy between two such slots a copy word by word. The register
/// allocator then keeps them in registers like any other local. Only slots
/// read and written as whole aligned 32-bit words (no narrow field, no
/// 64-bit one), whose address is never taken except by a copy of it used
/// the same ways, and only outside an async or iterator body, whose frame may
/// be a block of the heap.
///
/// Run last (Pipeline.RunLate, after the final cleanup), when the lifetime
/// passes have finished reading slots and buffers as they were made.
/// </summary>
public static class SlotScalars
{
    /// <summary>Whether any slot of the function was made registers.</summary>
    public static bool Apply(Function f)
    {
        if (f.Async is not null || f.Slots.Count == 0 || IrTypes.Word != IrType.I32) return false;

        // Registers that are nothing but a slot's address: `r = copy &slot`, written once.
        Dictionary<VReg, int> writes = new();
        foreach (Block b in f.Blocks)
            foreach (Instr i in b.Instrs)
                if (i.Dest is { } d) writes[d] = writes.GetValueOrDefault(d) + 1;
        Dictionary<VReg, FrameSlot> alias = new();
        foreach (Block b in f.Blocks)
            foreach (Instr i in b.Instrs)
                if (i.Op == Opcode.Copy && i.Dest is { } d && writes[d] == 1 && i.Operands.Count == 1
                    && i.Operands[0] is SlotOperand { Slot: var s } && !f.Params.Contains(d))
                    alias[d] = s;

        FrameSlot? SlotOf(Operand o) => o switch
        {
            SlotOperand { Slot: var s } => s,
            RegOperand { Reg: var r } when alias.TryGetValue(r, out FrameSlot? s) => s,
            _ => null,
        };

        HashSet<FrameSlot> candidates = new(f.Slots.Where(s => s.Bytes > 0 && s.Bytes % 4 == 0 && s.Bytes <= 64));
        List<(FrameSlot Dest, FrameSlot Source)> copies = new();
        void Refuse(Operand o) { if (SlotOf(o) is { } s) candidates.Remove(s); }

        foreach (Block b in f.Blocks)
            foreach (Instr i in b.Instrs)
            {
                if (i.Op == Opcode.Copy && i.Dest is { } d && alias.ContainsKey(d)) continue;
                switch (i.Op)
                {
                    case Opcode.Load when i.Operands.Count == 1 && SlotOf(i.Operands[0]) is { } s:
                        if (i.Size != 4 || i.Dest?.Type != IrType.I32 || !Word(s, i.Offset)) candidates.Remove(s);
                        continue;
                    case Opcode.Store when i.Operands.Count == 2 && SlotOf(i.Operands[0]) is { } s:
                        if (i.Size != 4 || i.Operands[1].Type != IrType.I32 || !Word(s, i.Offset)) candidates.Remove(s);
                        Refuse(i.Operands[1]);  // the address stored somewhere: it escapes
                        continue;
                    case Opcode.MemSet when i.Operands.Count == 3 && SlotOf(i.Operands[0]) is { } s:
                        if (i.Offset != 0 || i.Operands[1] is not ImmOperand || i.Operands[2] is not ImmOperand { Value: var n } || n != s.Bytes)
                            candidates.Remove(s);
                        continue;
                    case Opcode.MemCopy when i.Operands.Count == 3 && (SlotOf(i.Operands[0]) is not null || SlotOf(i.Operands[1]) is not null):
                    {
                        FrameSlot? to = SlotOf(i.Operands[0]), from = SlotOf(i.Operands[1]);
                        if (to is null || from is null || ReferenceEquals(to, from) || i.Offset != 0 || to.Bytes != from.Bytes
                            || i.Operands[2] is not ImmOperand { Value: var length } || length != to.Bytes)
                        {
                            if (to is not null) candidates.Remove(to);
                            if (from is not null) candidates.Remove(from);
                        }
                        else copies.Add((to, from));
                        continue;
                    }
                }
                // Any other appearance of a slot's address: it is taken.
                foreach (Operand o in i.Operands) Refuse(o);
            }
        // A copy between two slots is one only both sides of which stay.
        for (bool changed = true; changed;)
        {
            changed = false;
            foreach ((FrameSlot to, FrameSlot from) in copies)
                if (candidates.Contains(to) != candidates.Contains(from))
                {
                    candidates.Remove(to); candidates.Remove(from);
                    changed = true;
                }
        }
        if (candidates.Count == 0) return false;

        Dictionary<FrameSlot, VReg[]> words = new();
        foreach (FrameSlot s in candidates)
        {
            VReg[] fields = new VReg[s.Bytes / 4];
            for (int k = 0; k < fields.Length; k++) fields[k] = f.NewReg(IrType.I32, (s.Name ?? "slot") + "." + (k * 4));
            words[s] = fields;
        }
        bool Scalar(Operand o, out VReg[] fields)
        {
            fields = null!;
            return SlotOf(o) is { } s && words.TryGetValue(s, out fields!);
        }

        foreach (Block b in f.Blocks)
        {
            List<Instr> made = new(b.Instrs.Count);
            foreach (Instr i in b.Instrs)
            {
                if (i.Op == Opcode.Copy && i.Dest is { } d && alias.TryGetValue(d, out FrameSlot? own) && words.ContainsKey(own)) continue;
                switch (i.Op)
                {
                    case Opcode.Load when i.Operands.Count == 1 && Scalar(i.Operands[0], out VReg[] fields):
                        made.Add(IrInfo.CopyOf(i, RegOperand.Of(fields[i.Offset / 4])));
                        continue;
                    case Opcode.Store when i.Operands.Count == 2 && Scalar(i.Operands[0], out VReg[] fields):
                        made.Add(new Instr { Op = Opcode.Copy, Dest = fields[i.Offset / 4], Operands = { i.Operands[1] }, Line = i.Line });
                        continue;
                    case Opcode.MemSet when Scalar(i.Operands[0], out VReg[] fields):
                    {
                        long b8 = ((ImmOperand)i.Operands[1]).Value & 0xFF;
                        long pattern = b8 | b8 << 8 | b8 << 16 | b8 << 24;
                        foreach (VReg field in fields)
                            made.Add(new Instr { Op = Opcode.Copy, Dest = field, Operands = { new ImmOperand(unchecked((int)pattern), IrType.I32) }, Line = i.Line });
                        continue;
                    }
                    case Opcode.MemCopy when Scalar(i.Operands[0], out VReg[] to) && Scalar(i.Operands[1], out VReg[] from):
                        for (int k = 0; k < to.Length; k++)
                            made.Add(new Instr { Op = Opcode.Copy, Dest = to[k], Operands = { RegOperand.Of(from[k]) }, Line = i.Line });
                        continue;
                }
                made.Add(i);
            }
            b.Instrs.Clear();
            b.Instrs.AddRange(made);
        }
        f.Slots.RemoveAll(words.ContainsKey);
        return true;
    }

    /// <summary>Whether [offset, offset + 4) is one aligned word of the slot.</summary>
    private static bool Word(FrameSlot s, long offset) => offset >= 0 && offset % 4 == 0 && offset + 4 <= s.Bytes;
}
