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
/// element stored and read back through the frame on every lap. And a
/// packed value's lanes where the variant has no instruction for its
/// operation (Lowering.PackedLanes): each lane stored into its buffer and
/// read back by the next operation, where a 486's own loop keeps it in a
/// register.
///
/// Such a slot becomes one register a FIELD: the parts its loads and stores
/// read and write it in -- a byte, a halfword, a word, or a 64-bit quadword
/// -- each always at the same offset and width; the bytes none of them
/// touches fields of their own, so that a copy of the whole slot misses
/// nothing. A load of a field is its register, extended as the load says
/// where it is narrow; a store a copy to it; a fill a copy of the byte
/// pattern to each; a copy between two such slots of one shape a copy field
/// by field; and a copy of the whole slot from or to other memory a load or
/// a store of each field there. The register allocator then keeps them in
/// registers like any other local. Only slots whose accesses agree on their
/// fields (two that overlap otherwise leave the slot in memory), whose
/// address is never taken except by a copy of it, or that copy plus a
/// constant, used the same ways, and only outside an async or iterator
/// body, whose frame may be a block of the heap.
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

        // Registers that are nothing but a slot's address, or that address
        // and a constant: `r = copy &slot`, `r = add r0 n`, each written once.
        Dictionary<VReg, int> writes = new();
        foreach (Block b in f.Blocks)
            foreach (Instr i in b.Instrs)
                if (i.Dest is { } d) writes[d] = writes.GetValueOrDefault(d) + 1;
        Dictionary<VReg, (FrameSlot Slot, long At)> alias = new();
        for (bool more = true; more;)
        {
            more = false;
            foreach (Block b in f.Blocks)
                foreach (Instr i in b.Instrs)
                {
                    if (i.Dest is not { } d || writes[d] != 1 || f.Params.Contains(d) || alias.ContainsKey(d)) continue;
                    if (i.Op == Opcode.Copy && i.Operands is [SlotOperand { Slot: var s }])
                    { alias[d] = (s, 0); more = true; }
                    else if (i.Op == Opcode.Add && i.Operands is [RegOperand { Reg: var r }, ImmOperand { Value: var n }] && alias.TryGetValue(r, out var at))
                    { alias[d] = (at.Slot, at.At + n); more = true; }
                }
        }

        (FrameSlot Slot, long At)? SlotOf(Operand o) => o switch
        {
            SlotOperand { Slot: var s } => (s, 0),
            RegOperand { Reg: var r } when alias.TryGetValue(r, out var at) => at,
            _ => null,
        };

        HashSet<FrameSlot> candidates = new(f.Slots.Where(s => s.Bytes > 0 && s.Bytes <= 64), ReferenceEqualityComparer.Instance);
        // Each candidate's fields as its accesses make them: offset -> width.
        Dictionary<FrameSlot, SortedDictionary<long, int>> shape = new(ReferenceEqualityComparer.Instance);
        List<(FrameSlot Dest, FrameSlot Source)> copies = new();
        void Refuse(Operand o) { if (SlotOf(o) is { } s) candidates.Remove(s.Slot); }
        void Field(FrameSlot s, long at, int width, IrType type)
        {
            bool fits = width is 1 or 2 or 4 && type == IrType.I32 || width == 8 && type == IrType.I64;
            if (!fits || at < 0 || at + width > s.Bytes || at % width != 0) { candidates.Remove(s); return; }
            if (!shape.TryGetValue(s, out var fields)) shape[s] = fields = new();
            if (fields.TryGetValue(at, out int had)) { if (had != width) candidates.Remove(s); return; }
            foreach ((long start, int w) in fields)
                if (start < at + width && at < start + w) { candidates.Remove(s); return; }
            fields[at] = width;
        }

        foreach (Block b in f.Blocks)
            foreach (Instr i in b.Instrs)
            {
                if (i.Dest is { } d && alias.ContainsKey(d)) continue;
                switch (i.Op)
                {
                    case Opcode.Load when i.Operands.Count == 1 && SlotOf(i.Operands[0]) is { } s && i.Dest is { } loaded:
                        Field(s.Slot, s.At + i.Offset, i.Size, loaded.Type);
                        continue;
                    case Opcode.Store when i.Operands.Count == 2 && SlotOf(i.Operands[0]) is { } s:
                        Field(s.Slot, s.At + i.Offset, i.Size, i.Operands[1].Type);
                        Refuse(i.Operands[1]);  // the address stored somewhere: it escapes
                        continue;
                    case Opcode.MemSet when i.Operands.Count == 3 && SlotOf(i.Operands[0]) is { } s:
                        if (s.At != 0 || i.Offset != 0 || i.Operands[1] is not ImmOperand || i.Operands[2] is not ImmOperand { Value: var n } || n != s.Slot.Bytes)
                            candidates.Remove(s.Slot);
                        continue;
                    case Opcode.MemCopy when i.Operands.Count == 3 && (SlotOf(i.Operands[0]) is not null || SlotOf(i.Operands[1]) is not null):
                    {
                        var to = SlotOf(i.Operands[0]);
                        var from = SlotOf(i.Operands[1]);
                        // WHOLE, at its start: to another slot, or from or to
                        // memory elsewhere, field by field.
                        bool Whole((FrameSlot Slot, long At)? side) => side is not { } x || x.At == 0 && i.Operands[2] is ImmOperand { Value: var length } && length == x.Slot.Bytes;
                        if (i.Offset != 0 || !Whole(to) || !Whole(from) || to is { } t && from is { } fr && (ReferenceEquals(t.Slot, fr.Slot) || t.Slot.Bytes != fr.Slot.Bytes))
                        {
                            if (to is { } x) candidates.Remove(x.Slot);
                            if (from is { } y) candidates.Remove(y.Slot);
                        }
                        else if (to is { } t2 && from is { } f2) copies.Add((t2.Slot, f2.Slot));
                        continue;
                    }
                }
                // Any other appearance of a slot's address: it is taken.
                foreach (Operand o in i.Operands) Refuse(o);
            }

        // EVERY BYTE IN A FIELD: the gaps no access names, in the widest
        // aligned pieces they hold.
        foreach (FrameSlot s in candidates)
        {
            if (!shape.TryGetValue(s, out var fields)) shape[s] = fields = new();
            long at = 0;
            List<(long, int)> fill = new();
            foreach ((long start, int w) in fields.ToList().Append(new KeyValuePair<long, int>(s.Bytes, 0)))
            {
                while (at < start)
                {
                    int piece = at % 4 == 0 && at + 4 <= start ? 4 : at % 2 == 0 && at + 2 <= start ? 2 : 1;
                    fill.Add((at, piece));
                    at += piece;
                }
                at = Math.Max(at, start + w);
            }
            foreach ((long start, int w) in fill) fields[start] = w;
        }
        // A copy between two slots is one only both sides of which stay, and
        // only between two of one shape.
        for (bool changed = true; changed;)
        {
            changed = false;
            foreach ((FrameSlot to, FrameSlot from) in copies)
            {
                bool both = candidates.Contains(to) && candidates.Contains(from);
                bool alike = both && shape[to].SequenceEqual(shape[from]);
                if ((candidates.Contains(to) || candidates.Contains(from)) && !alike)
                {
                    candidates.Remove(to); candidates.Remove(from);
                    changed = true;
                }
            }
        }
        if (candidates.Count == 0) return false;

        Dictionary<FrameSlot, Dictionary<long, (VReg Reg, int Width)>> registers = new(ReferenceEqualityComparer.Instance);
        foreach (FrameSlot s in candidates)
        {
            Dictionary<long, (VReg, int)> fields = new();
            foreach ((long at, int w) in shape[s])
                fields[at] = (f.NewReg(w == 8 ? IrType.I64 : IrType.I32, (s.Name ?? "slot") + "." + at), w);
            registers[s] = fields;
        }
        bool Scalar(Operand o, out Dictionary<long, (VReg Reg, int Width)> fields, out long at)
        {
            fields = null!;
            at = 0;
            if (SlotOf(o) is not { } s || !registers.TryGetValue(s.Slot, out fields!)) return false;
            at = s.At;
            return true;
        }

        foreach (Block b in f.Blocks)
        {
            List<Instr> made = new(b.Instrs.Count);
            foreach (Instr i in b.Instrs)
            {
                if (i.Dest is { } d && alias.TryGetValue(d, out var own) && registers.ContainsKey(own.Slot)) continue;
                switch (i.Op)
                {
                    case Opcode.Load when i.Operands.Count == 1 && Scalar(i.Operands[0], out var fields, out long at):
                    {
                        (VReg reg, int width) = fields[at + i.Offset];
                        // Narrow, extended as the load says; whole, as it is.
                        Opcode? extend = width switch
                        {
                            1 => i.Signed ? Opcode.SExt8 : Opcode.ZExt8,
                            2 => i.Signed ? Opcode.SExt16 : Opcode.ZExt16,
                            _ => null,
                        };
                        made.Add(extend is { } op
                            ? new Instr { Op = op, Dest = i.Dest, Operands = { RegOperand.Of(reg) }, Line = i.Line }
                            : IrInfo.CopyOf(i, RegOperand.Of(reg)));
                        continue;
                    }
                    case Opcode.Store when i.Operands.Count == 2 && Scalar(i.Operands[0], out var fields, out long at):
                        made.Add(new Instr { Op = Opcode.Copy, Dest = fields[at + i.Offset].Reg, Operands = { i.Operands[1] }, Line = i.Line });
                        continue;
                    case Opcode.MemSet when Scalar(i.Operands[0], out var fields, out _):
                    {
                        long b8 = ((ImmOperand)i.Operands[1]).Value & 0xFF;
                        long pattern = b8 * 0x0101010101010101L;
                        foreach ((VReg reg, int width) in fields.Values)
                            made.Add(new Instr { Op = Opcode.Copy, Dest = reg, Line = i.Line,
                                Operands = { width == 8 ? new ImmOperand(pattern, IrType.I64) : new ImmOperand(unchecked((int)pattern), IrType.I32) } });
                        continue;
                    }
                    case Opcode.MemCopy:
                    {
                        bool into = Scalar(i.Operands[0], out var to, out _), outOf = Scalar(i.Operands[1], out var from, out _);
                        if (into && outOf)
                        {
                            foreach ((long at, (VReg reg, _)) in to)
                                made.Add(new Instr { Op = Opcode.Copy, Dest = reg, Operands = { RegOperand.Of(from[at].Reg) }, Line = i.Line });
                            continue;
                        }
                        // From memory elsewhere, each field loaded; to it, stored.
                        if (into)
                        {
                            foreach ((long at, (VReg reg, int width)) in to)
                                made.Add(new Instr { Op = Opcode.Load, Dest = reg, Operands = { i.Operands[1] }, Offset = at, Size = width, Signed = false, Line = i.Line });
                            continue;
                        }
                        if (outOf)
                        {
                            foreach ((long at, (VReg reg, int width)) in from)
                                made.Add(new Instr { Op = Opcode.Store, Operands = { i.Operands[0], RegOperand.Of(reg) }, Offset = at, Size = width, Line = i.Line });
                            continue;
                        }
                        break;
                    }
                }
                made.Add(i);
            }
            b.Instrs.Clear();
            b.Instrs.AddRange(made);
        }
        f.Slots.RemoveAll(registers.ContainsKey);
        return true;
    }
}
