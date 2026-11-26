using Corsac.Lang.Ir;

namespace Corsac.Lang.X86;

using Block = Corsac.Lang.Ir.Block;

/// <summary>
/// PACKED VALUES IN THE XMM CLASS (a Packed128; in long mode a Packed64
/// too, at its own eight bytes: the last paragraph), where the code owns
/// the SSE state and --cpu has SSE: a Packed128 the IR keeps in a sixteen-byte frame slot,
/// named through registers that hold the slot's address, is a value the
/// allocator keeps in an XMM register -- across instructions, blocks and a
/// loop's laps -- where the packed registers (PackedIn, PackedOut) hold one
/// for a block at most and write it back to its slot after every operation.
/// A ChaCha20 block's four rows were stored and loaded every quarter round.
///
/// THE IR'S FORM. Lowering gives every packed operation a slot of its own for
/// its result and a register holding its address; a Packed128 variable is a
/// register holding the address of the slot its last value is in, set again
/// at every assignment (`a = copy %result`). Taken as values: a slot is a
/// value; a register set once to a slot's address is that slot (an alias);
/// a register set more than once -- a variable -- is a value of its own, the
/// value it is copied from at each assignment, as C#'s copy of a struct is.
///
/// WHICH ARE TAKEN: a slot of sixteen bytes and every register naming it,
/// linked by their copies, where each is named only by an XMM packed
/// operation's address, a sixteen-byte MemCopy (Load128, Store, a copy), a
/// read of some of its bytes (a field), or a copy into another of them; and
/// every write -- an operation's result, a MemCopy's destination -- is to a
/// slot or an alias, never through a variable. Any other use of any of them
/// leaves all of them in memory, as they were. An XMM packed operation
/// touching none of them -- a Packed64's floats (f2) among them -- goes
/// through the packed registers as before, in XMM4 to XMM7, the class keeping
/// XMM0 to XMM3 (MFunction.XmmShared).
///
/// LONG MODE has no packed registers at all -- no MMX state, as GCC's and
/// Clang's 64-bit code has none: every packed operation is made here, on
/// the class's registers, an operand that is no value kept loaded into one
/// and a result that is none stored from one. A PACKED64 is the low half of
/// an XMM register there (the "q" operations, SSE2's on eight bytes), so a
/// slot of eight bytes is taken as one of sixteen is, by the same rules at
/// its own width; and a whole 64-bit store into one (Packed64.FromBits) or
/// load from one is a MOVQ between the files.
/// </summary>
internal sealed partial class Selector
{
    /// <summary>The XMM-class register of each value taken: a slot's, a variable's (and an alias's, its slot's).</summary>
    private Dictionary<object, MReg>? _packedValues;

    /// <summary>The slot each alias names.</summary>
    private Dictionary<VReg, FrameSlot>? _packedAliases;

    private bool PackedValueMode => _packedValues is not null;

    /// <summary>Whether a packed operation's name is one on the XMM registers: their sixteen bytes, or long mode's eight of a Packed64 ("q").</summary>
    private static bool XmmValueOp(string callee, out string[] part)
    {
        part = callee[MachineIntrinsics.PackedOpPrefix.Length..].Split('.');
        return part[0] is "x" or "xshift" or "xshuffle" or "xextract" or "xinsert" or "xstorent" or "xf"
            or "q" or "qshift" or "qshuffle" or "qextract" or "qinsert" or "qf";
    }

    /// <summary>The bytes of the values such an operation is on: eight for a "q" one.</summary>
    private static int PackedWidthOf(string kind) => kind[0] == 'q' ? 8 : 16;

    /// <summary>Whether a slot can be a value of the class: sixteen bytes, or in long mode a Packed64's eight.</summary>
    private static bool PackedSlot(FrameSlot s) => s.Bytes == 16 || s.Bytes == 8 && X86Mode.Long;

    /// <summary>The bytes of a value kept (its slot's; a variable's, those of what it copies).</summary>
    private Dictionary<object, int>? _packedWidths;

    private int PackedWidth(Operand o) => o switch
    {
        SlotOperand { Slot: var s } when _packedWidths!.TryGetValue(s, out int w) => w,
        RegOperand { Reg: var r } when _packedAliases!.TryGetValue(r, out FrameSlot? s) && _packedWidths!.TryGetValue(s, out int w) => w,
        RegOperand { Reg: var r } when _packedWidths!.TryGetValue(r, out int w) => w,
        _ => 16,
    };

    /// <summary>The values taken (the class above), or none: null where nothing is taken.</summary>
    private void PackedFindValues()
    {
        Dictionary<VReg, FrameSlot> aliases = new(ReferenceEqualityComparer.Instance);
        HashSet<VReg> variables = new(ReferenceEqualityComparer.Instance);
        // Aliases: one definition, the address of a slot of sixteen bytes.
        foreach ((VReg v, Instr? d) in _definitions)
            if (d is { Op: Opcode.Copy, Operands: [SlotOperand { Slot: var s }] } && PackedSlot(s) && !_rangeDefinitions.ContainsKey(v))
                aliases[v] = s;
        // Variables: every definition a copy of a slot's address, an alias or a variable.
        foreach ((VReg v, List<Instr> defs) in _rangeDefinitions)
            if (!_f.Params.Contains(v) && defs.All(d => d.Op == Opcode.Copy && d.Operands.Count == 1)) variables.Add(v);
        bool changed;
        do
        {
            changed = false;
            foreach (VReg v in variables.ToList())
                if (!_rangeDefinitions[v].All(d => d.Operands[0] is SlotOperand { Slot: var named } && PackedSlot(named)
                        || d.Operands[0] is RegOperand { Reg: var q } && (aliases.ContainsKey(q) || variables.Contains(q))))
                {
                    variables.Remove(v);
                    changed = true;
                }
        } while (changed);

        // The node an operand names: a slot (directly or by its alias), a variable, or none.
        object? NodeOf(Operand o) => o switch
        {
            SlotOperand { Slot: var s } when PackedSlot(s) => s,
            RegOperand { Reg: var r } when aliases.TryGetValue(r, out FrameSlot? s) => s,
            RegOperand { Reg: var r } when variables.Contains(r) => r,
            _ => null,
        };
        Dictionary<object, object> parent = new(ReferenceEqualityComparer.Instance);
        object Find(object x)
        {
            while (parent.TryGetValue(x, out object? up) && !ReferenceEquals(up, x)) x = up;
            return x;
        }
        void Union(object a, object b)
        {
            object ra = Find(a), rb = Find(b);
            if (!ReferenceEquals(ra, rb)) parent[ra] = rb;
        }
        HashSet<object> escaped = new(ReferenceEqualityComparer.Instance);
        // What a packed operation names: only their kin is taken, never a
        // struct of sixteen bytes no packed operation touches.
        List<object> operated = new();
        // Every slot named, directly or by an alias: each in a group kept is given its register.
        List<FrameSlot> slots = new();
        // The bytes each use takes its node to be of -- exactly (an
        // operation's, a copy's), or at least (a read of some of them): a
        // group whose slots are of another width is left in memory.
        List<(object Node, int Bytes, bool Exact)> widths = new();
        // The whole copies between two nodes (a MemCopy of both).
        List<(object To, object From)> copies = new();
        bool anyOperation = false;
        foreach (Block b in _f.Blocks)
            foreach (Instr i in b.Instrs)
            {
                bool valueOp = i.Op == Opcode.Call && i.Callee is { } c && c.StartsWith(MachineIntrinsics.PackedOpPrefix, StringComparison.Ordinal)
                    && XmmValueOp(c, out string[] part);
                string kind = valueOp ? i.Callee![MachineIntrinsics.PackedOpPrefix.Length..].Split('.')[0] : "";
                if (valueOp) anyOperation = true;
                // A variable's assignment links it to what it copies.
                if (i.Op == Opcode.Copy && i.Dest is { } dest && variables.Contains(dest) && NodeOf(i.Operands[0]) is { } from)
                {
                    Union(dest, from);
                    if (from is FrameSlot copied) slots.Add(copied);
                    continue;
                }
                // An alias's one definition: it is its slot.
                if (i.Op == Opcode.Copy && i.Dest is { } alias && aliases.ContainsKey(alias)) continue;
                if (i.Op == Opcode.MemCopy && i.Operands[2] is ImmOperand { Value: 16 or 8 } && NodeOf(i.Operands[0]) is { } copyTo && NodeOf(i.Operands[1]) is { } copyFrom)
                    copies.Add((copyTo, copyFrom));
                for (int k = 0; k < i.Operands.Count; k++)
                {
                    if (NodeOf(i.Operands[k]) is not { } node) continue;
                    if (valueOp) operated.Add(node);
                    if (node is FrameSlot named) slots.Add(named);
                    bool slotItself = node is FrameSlot;
                    bool fine;
                    if (valueOp)
                    {
                        // (A shift's count in a register, an "x" or "q" operation's third operand, is read at the operation's width as any other.)
                        fine = !(kind is "xinsert" or "qinsert" && k == 2) && !(kind == "xstorent" && k == 0)
                            && (k != 0 || kind is "xextract" or "qextract" || slotItself);
                        widths.Add((node, PackedWidthOf(kind), true));
                    }
                    else if (i.Op == Opcode.MemCopy && k < 2 && i.Operands[2] is ImmOperand { Value: 16 or 8 } copied && (k == 1 || slotItself))
                    {
                        fine = true;
                        widths.Add((node, (int)copied.Value, true));
                    }
                    // Zeroed whole: a fresh block escape analysis put in the frame (`b = a`'s copy).
                    else if (i.Op == Opcode.MemSet && k == 0 && slotItself && i.Operands[1] is ImmOperand { Value: 0 } && i.Operands[2] is ImmOperand { Value: 16 or 8 } cleared)
                    {
                        fine = true;
                        widths.Add((node, (int)cleared.Value, true));
                    }
                    else if (i.Op == Opcode.Load && k == 0 && i.Offset >= 0 && i.Offset + i.Size <= 16 && !i.Device)
                    {
                        fine = true;
                        widths.Add((node, (int)(i.Offset + i.Size), false));
                    }
                    // Long mode: a Packed64 written whole from a general register (FromBits).
                    else if (X86Mode.Long && i.Op == Opcode.Store && k == 0 && slotItself && i.Offset == 0 && i.Size == 8 && !i.Device
                        && NodeOf(i.Operands[1]) is null && i.Operands[1] is not RegOperand { Reg.Type: IrType.F32 or IrType.F64 })
                    {
                        fine = true;
                        widths.Add((node, 8, true));
                    }
                    else fine = false;
                    if (!fine) escaped.Add(node);
                }
            }
        if (!anyOperation) return;
        HashSet<object> lost = new(ReferenceEqualityComparer.Instance), kept = new(ReferenceEqualityComparer.Instance);
        foreach (object e in escaped) lost.Add(Find(e));
        // A group is of its slots' width, all of one; every use of it at that width.
        Dictionary<object, int> groupWidth = new(ReferenceEqualityComparer.Instance);
        foreach (FrameSlot s in slots.Concat(aliases.Values))
        {
            object root = Find(s);
            if (groupWidth.TryGetValue(root, out int was) && was != s.Bytes) lost.Add(root);
            groupWidth[root] = s.Bytes;
        }
        foreach ((object node, int bytes, bool exact) in widths)
        {
            object root = Find(node);
            if (!groupWidth.TryGetValue(root, out int of) || (exact ? of != bytes : of < bytes)) lost.Add(root);
        }
        foreach (object o in operated) if (!lost.Contains(Find(o))) kept.Add(Find(o));
        // A VALUE COPIED WHOLE INTO OR OUT OF ONE KEPT is kept too, where
        // nothing else loses it: an operand built in a buffer of its own
        // and copied into the variable an operation names (FromBits'
        // result, a temporary's) would be stored to memory only to be
        // loaded from it at once.
        bool grew = true;
        while (grew)
        {
            grew = false;
            foreach ((object a, object b) in copies)
            {
                object ra = Find(a), rb = Find(b);
                if (lost.Contains(ra) || lost.Contains(rb) || kept.Contains(ra) == kept.Contains(rb)) continue;
                grew |= kept.Add(ra) | kept.Add(rb);
            }
        }
        Dictionary<object, MReg> values = new(ReferenceEqualityComparer.Instance);
        void Take(object node)
        {
            if (!kept.Contains(Find(node)) || values.ContainsKey(node)) return;
            values[node] = XTemp();
        }
        foreach (FrameSlot s in aliases.Values) Take(s);
        foreach (VReg v in variables) Take(v);
        foreach (object o in operated) Take(o);
        foreach (FrameSlot s in slots) Take(s);
        foreach ((object a, object b) in copies) { Take(a); Take(b); }
        if (values.Count == 0) return;

        // A RESULT MADE ONLY TO BE A VARIABLE'S NEXT VALUE -- `x = x + y` is
        // an operation into a slot of its own, then `x = copy` of it -- made
        // in the variable's register: no second value, no copy, no second
        // slot stored. Where the slot is named by the operation that writes
        // it and the one copy into the variable after it in its block, and
        // nothing between names the variable or may throw.
        Dictionary<FrameSlot, (Block b, int at)> madeAt = new(ReferenceEqualityComparer.Instance);
        Dictionary<FrameSlot, (VReg x, Block b, int at)> copiedAt = new(ReferenceEqualityComparer.Instance);
        Dictionary<FrameSlot, int> namings = new(ReferenceEqualityComparer.Instance);
        foreach (Block b in _f.Blocks)
            for (int at = 0; at < b.Instrs.Count; at++)
            {
                Instr i = b.Instrs[at];
                if (i.Op == Opcode.Copy && i.Dest is { } alias && aliases.ContainsKey(alias)) continue;
                bool result = i.Op == Opcode.Call && i.Callee is { } c && c.StartsWith(MachineIntrinsics.PackedOpPrefix, StringComparison.Ordinal)
                    && XmmValueOp(c, out string[] part) && part[0] is "x" or "xshift" or "xshuffle" or "xinsert" or "xf"
                        or "q" or "qshift" or "qshuffle" or "qinsert" or "qf";
                for (int k = 0; k < i.Operands.Count; k++)
                {
                    if (NodeOf(i.Operands[k]) is not FrameSlot s) continue;
                    namings[s] = namings.GetValueOrDefault(s) + 1;
                    if (result && k == 0) madeAt[s] = (b, at);
                    else if (i.Op == Opcode.Copy && i.Dest is { } x && variables.Contains(x)) copiedAt[s] = (x, b, at);
                }
            }
        foreach ((FrameSlot s, (Block b, int at) made) in madeAt)
        {
            if (namings[s] != 2 || !copiedAt.TryGetValue(s, out var copy) || !ReferenceEquals(copy.b, made.b) || copy.at < made.at
                || !values.ContainsKey(s) || !values.TryGetValue(copy.x, out MReg? into))
                continue;
            bool clear = true;
            for (int k = made.at + 1; k < copy.at && clear; k++)
            {
                Instr between = made.b.Instrs[k];
                // Nothing between that may throw -- a handler would read the
                // variable's old value: only copies, and packed operations on
                // values kept in registers, which touch no memory (MXCSR keeps
                // the float exceptions masked).
                clear = !ReferenceEquals(between.Dest, copy.x) && (between.Op == Opcode.Copy || OnRegistersOnly(between));
                foreach (Operand o in between.Operands) clear &= !ReferenceEquals(NodeOf(o), copy.x);
            }
            if (clear) values[s] = into;
        }

        // NodeOf's node kept means PackedValue finds its register: both take
        // an alias to its slot, so SelectPackedValueOp reads no memory for it.
        bool OnRegistersOnly(Instr i)
        {
            if (i.Op != Opcode.Call || i.Callee is not { } c || !c.StartsWith(MachineIntrinsics.PackedOpPrefix, StringComparison.Ordinal)
                || !XmmValueOp(c, out string[] part) || part[0] == "xstorent")
                return false;
            for (int k = 0; k < i.Operands.Count; k++)
                if (!(part[0] is "xinsert" or "qinsert" && k == 2) && !(NodeOf(i.Operands[k]) is { } n && values.ContainsKey(n))) return false;
            return true;
        }
        _packedValues = values;
        _packedAliases = aliases;
        _packedWidths = new(ReferenceEqualityComparer.Instance);
        foreach (object node in values.Keys) _packedWidths[node] = groupWidth[Find(node)];
    }

    /// <summary>Whether a packed operation names a value this mode keeps: then it is made on registers (SelectPackedValueOp).</summary>
    private bool TouchesPackedValue(Instr i)
    {
        foreach (Operand o in i.Operands)
            if (PackedValue(o) is not null) return true;
        return false;
    }

    /// <summary>Whether any XMM packed operation of the function names no value this mode keeps: the packed registers' then, beside the class.</summary>
    private bool PackedOperationsOutside()
    {
        foreach (Block b in _f.Blocks)
            foreach (Instr i in b.Instrs)
                if (i.Op == Opcode.Call && i.Callee is { } c && c.StartsWith(MachineIntrinsics.PackedOpPrefix, StringComparison.Ordinal)
                    && (c.StartsWith(MachineIntrinsics.PackedOpPrefix + "x", StringComparison.Ordinal) || c.StartsWith(MachineIntrinsics.PackedOpPrefix + "f2.", StringComparison.Ordinal))
                    && !TouchesPackedValue(i))
                    return true;
        return false;
    }

    /// <summary>The XMM register of the value an operand names, or null where it names none taken.</summary>
    private MReg? PackedValue(Operand o) => _packedValues is null ? null : o switch
    {
        SlotOperand { Slot: var s } when _packedValues.TryGetValue(s, out MReg? v) => v,
        RegOperand { Reg: var r } when _packedAliases!.TryGetValue(r, out FrameSlot? s) && _packedValues.TryGetValue(s, out MReg? v) => v,
        RegOperand { Reg: var r } when _packedValues.TryGetValue(r, out MReg? v) => v,
        _ => null,
    };

    /// <summary>
    /// A copy into a register this mode keeps: an alias's definition, which is
    /// its slot and makes nothing; a variable's assignment, the value copied.
    /// </summary>
    private bool PackedValueCopy(Instr i)
    {
        if (_packedValues is null || i.Dest is not { } d) return false;
        if (_packedAliases!.TryGetValue(d, out FrameSlot? s) && _packedValues.ContainsKey(s)) return true;
        if (!_packedValues.TryGetValue(d, out MReg? to)) return false;
        MovF(to, PackedValue(i.Operands[0])!, 16);
        return true;
    }

    /// <summary>A MemCopy of a value this mode keeps, whole (sixteen bytes, or a Packed64's eight), from or to it: a load, a store, or a register's copy.</summary>
    private bool PackedValueMemCopy(Instr i)
    {
        if (_packedValues is null || i.Operands[2] is not ImmOperand { Value: 16 or 8 } size) return false;
        int bytes = (int)size.Value;
        MReg? to = PackedValue(i.Operands[0]), from = PackedValue(i.Operands[1]);
        if (to is null && from is null) return false;
        if (to is not null && from is not null) MovF(to, from, 16);
        else if (to is not null)
        {
            // Straight into the value's register: bytes the MMX registers hold newer written back first.
            PackedLetGo(PackedPlaceOf(i.Operands[1]), bytes, replaced: false);
            MovF(to, PackedAt(i.Operands[1]), bytes);
        }
        else PackedValueOut(i.Operands[0], from!, bytes);
        return true;
    }

    /// <summary>
    /// Long mode: a Packed64 this mode keeps written whole from a general
    /// register or a number (Packed64.FromBits) -- MOVQ xmm, r64, which
    /// clears the register's upper half.
    /// </summary>
    private bool PackedValueStore(Instr i)
    {
        if (_packedValues is null || i.Offset != 0 || i.Size != 8 || PackedValue(i.Operands[0]) is not { } v) return false;
        EmitW(MOp.MovGx, 8, v, RL(i.Operands[1]));
        return true;
    }

    /// <summary>A value this mode keeps zeroed whole (a fresh block's MemSet): XORPS with itself.</summary>
    private bool PackedValueMemSet(Instr i)
    {
        if (_packedValues is null || PackedValue(i.Operands[0]) is not { } v) return false;
        Emit(MOp.XorPs, v, v);
        return true;
    }

    /// <summary>
    /// A WORD OF A VALUE this mode keeps, read where --cpu has SSE2: the low
    /// one by MOVD, another first brought down by PSHUFD -- no store of the
    /// whole value to read four bytes of it back.
    /// </summary>
    private bool PackedValueLane(Instr i)
    {
        if (X86Mode.Long) return PackedValueLaneL(i);
        if (PackedValue(i.Operands[0]) is not { } v || i.Size != 4 || i.Dest is not { Type: IrType.I32 } d
            || (i.Offset & 3) != 0 || i.Offset is < 0 or > 12 || !Target.Current.X86Profile.Has(X86Feature.Sse2)) return false;
        MReg lane = v;
        if (i.Offset != 0)
        {
            lane = XTemp();
            Emit(new MInstr(MOp.XmmOpVDef, Imm(0x66), Imm(0x70), lane, v, Imm((int)(i.Offset / 4))) { Probed = true });
        }
        Emit(MOp.MovdFromX, Lo(d), lane);
        return true;
    }

    /// <summary>
    /// Long mode's: a doubleword or a quadword of a value this mode keeps,
    /// at its own boundary, by MOVD or MOVQ -- the low one straight from the
    /// register, another brought down first (PSHUFD: a doubleword by its
    /// number, the upper quadword by 0xEE).
    /// </summary>
    private bool PackedValueLaneL(Instr i)
    {
        if (PackedValue(i.Operands[0]) is not { } v || i.Dest is not { } d || i.Dest.Type is not (IrType.I32 or IrType.I64)
            || i.Size is not (4 or 8) || i.Size == 4 && i.Dest.Type != IrType.I32 && i.Signed
            || i.Offset < 0 || i.Offset % i.Size != 0 || i.Offset + i.Size > PackedWidth(i.Operands[0])) return false;
        MReg lane = v;
        if (i.Offset != 0)
        {
            lane = XTemp();
            Emit(new MInstr(MOp.XmmOpVDef, Imm(0x66), Imm(0x70), lane, v, Imm(i.Size == 8 ? 0xEE : (int)(i.Offset / 4))) { Probed = true });
        }
        // (A doubleword into a 64-bit value unsigned: the 32-bit move clears the upper half.)
        EmitW(MOp.MovGx, i.Size, V(d), lane);
        return true;
    }

    /// <summary>
    /// A field of a value this mode keeps read: the value stored to a
    /// sixteen-byte scratch of the frame, the bytes read from there.
    /// </summary>
    private MMem? PackedValueBytes(Operand address, long offset)
    {
        if (PackedValue(address) is not { } v) return null;
        MMem scratch = MMem.Frame(_m.Frame.PackedScratch);
        MovF(scratch, v, PackedWidth(address));
        return Displaced(scratch, checked((int)offset));
    }

    /// <summary>An operand's value in an XMM register: its own, or one it is loaded into (MOVUPS: no alignment asked; a Packed64's eight bytes by MOVSD, the upper half cleared).</summary>
    private MReg PackedValueIn(Operand o, int bytes = 16)
    {
        if (PackedValue(o) is { } v) return v;
        // Bytes the MMX registers hold newer than memory, written back first.
        PackedLetGo(PackedPlaceOf(o), bytes, replaced: false);
        MReg t = XTemp();
        MovF(t, PackedAt(o), bytes);
        return t;
    }

    /// <summary>A result into the value an operand names, or stored to the memory it addresses.</summary>
    private void PackedValueOut(Operand o, MReg value, int bytes = 16)
    {
        if (PackedValue(o) is { } v)
        {
            MovF(v, value, 16);
            return;
        }
        PackedStoredTo(o, bytes);
        MovF(PackedAt(o), value, bytes);
    }

    /// <summary>The register a result is made in: its value's, unless that is `avoid`'s (a source read after the result begins), then a new one.</summary>
    private MReg PackedValueResult(Operand o, MReg? avoid) => PackedValue(o) is { } v && (avoid is null || v.Id != avoid.Id) ? v : XTemp();

    /// <summary>An XMM packed operation on values in the XMM class (PackedValueMode): SelectPackedOp's XMM cases, on allocated registers.</summary>
    private void SelectPackedValueOp(string[] part, Instr i)
    {
        MInstr P(MOp op, params MOperand[] operands) => Emit(new MInstr(op, operands) { Probed = true });
        Operand Address(int k) => i.Operands[k];
        // A Packed64's operation ("q"): the same instruction on the
        // registers' low halves, eight bytes in and out. What its upper
        // half holds is whatever the instruction left there, read by no one.
        int bytes = PackedWidthOf(part[0]);
        switch (part[0])
        {
            case "x":
            case "q":
            {
                byte op = Convert.ToByte(part[2], 16);
                MReg a = PackedValueIn(Address(1), bytes), b = PackedValueIn(Address(2), bytes);
                MReg made = PackedValueResult(Address(0), a.Id == b.Id ? null : b);
                MovF(made, a, 16);
                // A PACKED64'S OPERATIONS WHOSE SIXTEEN-BYTE FORM IS NOT THE
                // EIGHT-BYTE ONE ON THE LOW HALF (as GCC makes them where it
                // keeps a 64-bit vector in an XMM register): an unpack of
                // the high halves is the low one's -- which interleaves all
                // eight bytes of each -- and its upper eight bytes brought
                // down (PSRLDQ 8); a pack leaves the second operand's four
                // in the upper half's low doubleword, brought beside the
                // first's (PSHUFD 0x08).
                bool high = bytes == 8 && op is 0x68 or 0x69 or 0x6A, pack = bytes == 8 && op is 0x63 or 0x67 or 0x6B;
                P(MOp.XmmOpV, Imm(Convert.ToByte(part[1], 16)), Imm(high ? op - 8 : op), made, b);
                if (high) P(MOp.XmmShiftV, made, Imm(0x73), Imm(3), Imm(8));
                if (pack) P(MOp.XmmOpVDef, Imm(0x66), Imm(0x70), made, made, Imm(0x08));
                PackedValueOut(Address(0), made, bytes);
                return;
            }
            case "xshift":
            case "qshift":
            {
                byte group = Convert.ToByte(part[1], 16);
                int digit = int.Parse(part[2]);
                MReg a = PackedValueIn(Address(1), bytes);
                MReg made = PackedValueResult(Address(0), null);
                // (No byte shift of a Packed64 comes here: there is none, Lowering.PackedIntrinsic.)
                MovF(made, a, 16);
                P(MOp.XmmShiftV, made, Imm(group), Imm(digit), Imm(int.Parse(part[3])));
                PackedValueOut(Address(0), made, bytes);
                return;
            }
            case "xshuffle":
            case "qshuffle":
            {
                MReg a = PackedValueIn(Address(1), bytes);
                MReg made = PackedValueResult(Address(0), null);
                P(MOp.XmmOpVDef, Imm(Convert.ToByte(part[1], 16)), Imm(0x70), made, a, Imm(int.Parse(part[2])));
                PackedValueOut(Address(0), made, bytes);
                return;
            }
            case "xstorent":
            {
                MReg value = PackedValueIn(Address(1));
                PackedStoredTo(Address(0), 16);
                P(MOp.XmmStoreNtV, PackedAt(Address(0)), value);
                return;
            }
            case "xextract":
            case "qextract":
            {
                MReg a = PackedValueIn(Address(0), bytes);
                P(MOp.XmmExtractV, i.Dest is not null ? (X86Mode.Long ? V(i.Dest) : Lo(i.Dest)) : Temp(), a, Imm(int.Parse(part[1])));
                return;
            }
            case "xinsert":
            case "qinsert":
            {
                MReg a = PackedValueIn(Address(1), bytes);
                MReg made = PackedValueResult(Address(0), null);
                MovF(made, a, 16);
                P(MOp.XmmInsertV, made, X86Mode.Long ? RL(i.Operands[2]) : R(i.Operands[2]), Imm(int.Parse(part[1])));
                PackedValueOut(Address(0), made, bytes);
                return;
            }
            case "xf":
            case "qf":
            {
                byte prefix = Convert.ToByte(part[1], 16), op = Convert.ToByte(part[2], 16);
                int immediate = int.Parse(part[3]);
                bool unary = part[4] == "1";
                MReg made;
                if (unary)
                {
                    MReg a = PackedValueIn(Address(1), bytes);
                    made = PackedValueResult(Address(0), null);
                    if (immediate >= 0) P(MOp.XmmOpVDef, Imm(prefix), Imm(op), made, a, Imm(immediate));
                    else P(MOp.XmmOpVDef, Imm(prefix), Imm(op), made, a);
                }
                else
                {
                    MReg a = PackedValueIn(Address(1), bytes), b = PackedValueIn(Address(2), bytes);
                    made = PackedValueResult(Address(0), a.Id == b.Id ? null : b);
                    MovF(made, a, 16);
                    if (immediate >= 0) P(MOp.XmmOpV, Imm(prefix), Imm(op), made, b, Imm(immediate));
                    else P(MOp.XmmOpV, Imm(prefix), Imm(op), made, b);
                }
                PackedValueOut(Address(0), made, bytes);
                return;
            }
        }
        Error("unknown packed operation " + i.Callee);
    }
}
