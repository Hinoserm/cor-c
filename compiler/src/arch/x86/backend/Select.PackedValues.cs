using Corsac.Lang.Ir;

namespace Corsac.Lang.X86;

using Block = Corsac.Lang.Ir.Block;

/// <summary>
/// PACKED128 VALUES IN THE XMM CLASS, where the code owns the SSE state and
/// --cpu has SSE: a Packed128 the IR keeps in a sixteen-byte frame slot,
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
/// </summary>
internal sealed partial class Selector
{
    /// <summary>The XMM-class register of each value taken: a slot's, a variable's (and an alias's, its slot's).</summary>
    private Dictionary<object, MReg>? _packedValues;

    /// <summary>The slot each alias names.</summary>
    private Dictionary<VReg, FrameSlot>? _packedAliases;

    private bool PackedValueMode => _packedValues is not null;

    /// <summary>Whether a packed operation's name is one on the XMM registers' sixteen bytes.</summary>
    private static bool XmmValueOp(string callee, out string[] part)
    {
        part = callee[MachineIntrinsics.PackedOpPrefix.Length..].Split('.');
        return part[0] is "x" or "xshift" or "xshuffle" or "xextract" or "xinsert" or "xstorent" or "xf";
    }

    /// <summary>The values taken (the class above), or none: null where nothing is taken.</summary>
    private void PackedFindValues()
    {
        Dictionary<VReg, FrameSlot> aliases = new(ReferenceEqualityComparer.Instance);
        HashSet<VReg> variables = new(ReferenceEqualityComparer.Instance);
        // Aliases: one definition, the address of a slot of sixteen bytes.
        foreach ((VReg v, Instr? d) in _definitions)
            if (d is { Op: Opcode.Copy, Operands: [SlotOperand { Slot: { Bytes: 16 } s }] } && !_rangeDefinitions.ContainsKey(v))
                aliases[v] = s;
        // Variables: every definition a copy of a slot's address, an alias or a variable.
        foreach ((VReg v, List<Instr> defs) in _rangeDefinitions)
            if (!_f.Params.Contains(v) && defs.All(d => d.Op == Opcode.Copy && d.Operands.Count == 1)) variables.Add(v);
        bool changed;
        do
        {
            changed = false;
            foreach (VReg v in variables.ToList())
                if (!_rangeDefinitions[v].All(d => d.Operands[0] is SlotOperand { Slot.Bytes: 16 }
                        || d.Operands[0] is RegOperand { Reg: var q } && (aliases.ContainsKey(q) || variables.Contains(q))))
                {
                    variables.Remove(v);
                    changed = true;
                }
        } while (changed);

        // The node an operand names: a slot (directly or by its alias), a variable, or none.
        object? NodeOf(Operand o) => o switch
        {
            SlotOperand { Slot: { Bytes: 16 } s } => s,
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
                for (int k = 0; k < i.Operands.Count; k++)
                {
                    if (NodeOf(i.Operands[k]) is not { } node) continue;
                    if (valueOp) operated.Add(node);
                    if (node is FrameSlot named) slots.Add(named);
                    bool slotItself = node is FrameSlot;
                    bool fine = valueOp
                        ? !(kind == "xinsert" && k == 2) && !(kind == "xstorent" && k == 0)
                          && (k != 0 || kind == "xextract" || slotItself)
                        : i.Op == Opcode.MemCopy && k < 2 && i.Operands[2] is ImmOperand { Value: 16 } && (k == 1 || slotItself)
                          // Zeroed whole: a fresh block escape analysis put in the frame (`b = a`'s copy).
                          || i.Op == Opcode.MemSet && k == 0 && slotItself && i.Operands[1] is ImmOperand { Value: 0 } && i.Operands[2] is ImmOperand { Value: 16 }
                          || i.Op == Opcode.Load && k == 0 && i.Offset >= 0 && i.Offset + i.Size <= 16;
                    if (!fine) escaped.Add(node);
                }
            }
        if (!anyOperation) return;
        HashSet<object> lost = new(ReferenceEqualityComparer.Instance), kept = new(ReferenceEqualityComparer.Instance);
        foreach (object e in escaped) lost.Add(Find(e));
        foreach (object o in operated) if (!lost.Contains(Find(o))) kept.Add(Find(o));
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
                    && XmmValueOp(c, out string[] part) && part[0] is "x" or "xshift" or "xshuffle" or "xinsert" or "xf";
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
                // Nothing between that may throw: a handler would read the variable's old value.
                clear = !ReferenceEquals(between.Dest, copy.x) && (between.Op == Opcode.Copy
                    || between.Op == Opcode.Call && between.Callee is { } c && c.StartsWith(MachineIntrinsics.PackedOpPrefix, StringComparison.Ordinal));
                foreach (Operand o in between.Operands) clear &= !ReferenceEquals(NodeOf(o), copy.x);
            }
            if (clear) values[s] = into;
        }
        _packedValues = values;
        _packedAliases = aliases;
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

    /// <summary>A sixteen-byte MemCopy from or to a value this mode keeps: a load, a store, or a register's copy.</summary>
    private bool PackedValueMemCopy(Instr i)
    {
        if (_packedValues is null || i.Operands[2] is not ImmOperand { Value: 16 }) return false;
        MReg? to = PackedValue(i.Operands[0]), from = PackedValue(i.Operands[1]);
        if (to is null && from is null) return false;
        if (to is not null && from is not null) MovF(to, from, 16);
        else if (to is not null)
        {
            // Straight into the value's register: bytes the MMX registers hold newer written back first.
            PackedLetGo(PackedPlaceOf(i.Operands[1]), 16, replaced: false);
            MovF(to, PackedAt(i.Operands[1]), 16);
        }
        else PackedValueOut(i.Operands[0], from!);
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
    /// A field of a value this mode keeps read: the value stored to a
    /// sixteen-byte scratch of the frame, the bytes read from there.
    /// </summary>
    private MMem? PackedValueBytes(Operand address, long offset)
    {
        if (PackedValue(address) is not { } v) return null;
        MMem scratch = MMem.Frame(_m.Frame.PackedScratch);
        MovF(scratch, v, 16);
        return Displaced(scratch, checked((int)offset));
    }

    /// <summary>An operand's value in an XMM register: its own, or one it is loaded into (MOVUPS: no alignment asked).</summary>
    private MReg PackedValueIn(Operand o)
    {
        if (PackedValue(o) is { } v) return v;
        // Bytes the MMX registers hold newer than memory, written back first.
        PackedLetGo(PackedPlaceOf(o), 16, replaced: false);
        MReg t = XTemp();
        MovF(t, PackedAt(o), 16);
        return t;
    }

    /// <summary>A result into the value an operand names, or stored to the memory it addresses.</summary>
    private void PackedValueOut(Operand o, MReg value)
    {
        if (PackedValue(o) is { } v)
        {
            MovF(v, value, 16);
            return;
        }
        PackedStoredTo(o, 16);
        MovF(PackedAt(o), value, 16);
    }

    /// <summary>The register a result is made in: its value's, unless that is `avoid`'s (a source read after the result begins), then a new one.</summary>
    private MReg PackedValueResult(Operand o, MReg? avoid) => PackedValue(o) is { } v && (avoid is null || v.Id != avoid.Id) ? v : XTemp();

    /// <summary>An XMM packed operation on values in the XMM class (PackedValueMode): SelectPackedOp's XMM cases, on allocated registers.</summary>
    private void SelectPackedValueOp(string[] part, Instr i)
    {
        MInstr P(MOp op, params MOperand[] operands) => Emit(new MInstr(op, operands) { Probed = true });
        Operand Address(int k) => i.Operands[k];
        switch (part[0])
        {
            case "x":
            {
                MReg a = PackedValueIn(Address(1)), b = PackedValueIn(Address(2));
                MReg made = PackedValueResult(Address(0), a.Id == b.Id ? null : b);
                MovF(made, a, 16);
                P(MOp.XmmOpV, Imm(Convert.ToByte(part[1], 16)), Imm(Convert.ToByte(part[2], 16)), made, b);
                PackedValueOut(Address(0), made);
                return;
            }
            case "xshift":
            {
                MReg a = PackedValueIn(Address(1));
                MReg made = PackedValueResult(Address(0), null);
                MovF(made, a, 16);
                P(MOp.XmmShiftV, made, Imm(Convert.ToByte(part[1], 16)), Imm(int.Parse(part[2])), Imm(int.Parse(part[3])));
                PackedValueOut(Address(0), made);
                return;
            }
            case "xshuffle":
            {
                MReg a = PackedValueIn(Address(1));
                MReg made = PackedValueResult(Address(0), null);
                P(MOp.XmmOpVDef, Imm(Convert.ToByte(part[1], 16)), Imm(0x70), made, a, Imm(int.Parse(part[2])));
                PackedValueOut(Address(0), made);
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
            {
                MReg a = PackedValueIn(Address(0));
                P(MOp.XmmExtractV, i.Dest is not null ? Lo(i.Dest) : Temp(), a, Imm(int.Parse(part[1])));
                return;
            }
            case "xinsert":
            {
                MReg a = PackedValueIn(Address(1));
                MReg made = PackedValueResult(Address(0), null);
                MovF(made, a, 16);
                P(MOp.XmmInsertV, made, R(i.Operands[2]), Imm(int.Parse(part[1])));
                PackedValueOut(Address(0), made);
                return;
            }
            case "xf":
            {
                byte prefix = Convert.ToByte(part[1], 16), op = Convert.ToByte(part[2], 16);
                int immediate = int.Parse(part[3]);
                bool unary = part[4] == "1";
                MReg made;
                if (unary)
                {
                    MReg a = PackedValueIn(Address(1));
                    made = PackedValueResult(Address(0), null);
                    if (immediate >= 0) P(MOp.XmmOpVDef, Imm(prefix), Imm(op), made, a, Imm(immediate));
                    else P(MOp.XmmOpVDef, Imm(prefix), Imm(op), made, a);
                }
                else
                {
                    MReg a = PackedValueIn(Address(1)), b = PackedValueIn(Address(2));
                    made = PackedValueResult(Address(0), a.Id == b.Id ? null : b);
                    MovF(made, a, 16);
                    if (immediate >= 0) P(MOp.XmmOpV, Imm(prefix), Imm(op), made, b, Imm(immediate));
                    else P(MOp.XmmOpV, Imm(prefix), Imm(op), made, b);
                }
                PackedValueOut(Address(0), made);
                return;
            }
        }
        Error("unknown packed operation " + i.Callee);
    }
}
