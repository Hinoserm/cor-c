#nullable enable
using Corsac.Lang.Ir;

namespace Corsac.Lang.Opt;

using Block = Corsac.Lang.Ir.Block;

/// <summary>
/// A virtual call on an object whose making is in sight is a direct call.
///
/// An object made here has the type it was made with, and the vtable word
/// lowering stores into it as it is made is never written again. So the load
/// of that word is the vtable's symbol; a slot read from the vtable is read
/// from read-only data, at a relocation that names the method; and the
/// indirect call through it is a call of that method. The iterator a
/// `foreach` asks for its enumerator, a lambda invoked where it was written,
/// an enumerator boxed and walked: each was a call nothing could see into,
/// and an unknown call lets every argument escape -- the object itself
/// first, so not one of them was ever freed.
///
/// Three steps, each needing the last: a load of word 0 of a fresh object
/// (an allocation's register, through copies) whose one store of word 0 is
/// a symbol, made where it dominates the load, becomes that symbol; a load
/// at a constant offset from a read-only item, at a relocation, becomes the
/// symbol the relocation names (as ReadOnlyFold does); and a callindirect
/// whose target is a copy of a function's symbol becomes a call of it.
/// </summary>
public sealed class Devirtualize : IModulePass
{
    public string Name => "devirtualize";

    /// <summary>How many indirect calls became direct.</summary>
    public int Resolved { get; private set; }

    public void Run(Module m)
    {
        // ANOTHER UNIT'S CLASSES TOO, as this one knows them (ShadowData),
        // as LateCleanup and the link read them: a stream made here and
        // disposed, Stream.Dispose() inlined, called Dispose(true) through
        // the MemoryStream descriptor only the library holds. Left indirect
        // here, it was an unknown call in the IR this unit's link hints are
        // taken from (RegionSummary, EscapeHints) -- the stream and what it
        // holds handed to nobody knows what -- though the link's own late
        // passes then made it direct.
        Dictionary<string, DataItem> items = ReadOnlyItems(m);
        if (items.Count == 0) return;
        foreach (Function f in m.Functions) Run(f, items);
    }

    internal void Run(Function f, Dictionary<string, DataItem> items)
    {
        bool any = false;
        foreach (Block b in f.Blocks)
            foreach (Instr i in b.Instrs)
                if (i.Op == Opcode.CallIndirect) { any = true; break; }

        Defs defs = new(f, buildCfg: false);
        Cfg? cfg = null;
        int word = IrTypes.Word.Bytes();

        // The fresh object a register holds the address of, through copies:
        // the allocation that made it, or null.
        Instr? Made(Operand o)
        {
            for (int hops = 0; hops < 8; hops++)
            {
                if (o is not RegOperand r || !defs.IsSingle(r.Reg) || defs.Definition(r.Reg) is not { } d) return null;
                if (d.Op == Opcode.Call && Escape.IsAllocator(d.Callee)) return d;
                if (d.Op is not (Opcode.Copy or Opcode.Trunc64 or Opcode.ZExt32)) return null;
                o = d.Operands[0];
            }
            return null;
        }
        // A register's value as a symbol, through copies.
        SymOperand? Symbol(Operand o)
        {
            for (int hops = 0; hops < 8; hops++)
            {
                if (o is SymOperand s) return s;
                if (o is not RegOperand r || !defs.IsSingle(r.Reg) || defs.Definition(r.Reg) is not { Op: Opcode.Copy } d) return null;
                o = d.Operands[0];
            }
            return null;
        }

        // 1. WHAT A FRESH OBJECT'S WORD HOLDS, where it was stored in sight and
        //    nothing that could write it since can reach the load: the vtable
        //    a virtual call reads, the state an inlined GetEnumerator tests on
        //    the iterator just made. Forwarded only from the one store of that
        //    word, which must dominate the load, and only when no call taking
        //    the object, no copy of it anywhere it could be written through,
        //    and no store of it into memory comes between -- on any path,
        //    round a loop included.
        ForwardFreshFields(f, defs, Made);
        // 1b. A TEST OF THE TYPE OF ONE OF A FEW OBJECTS MADE HERE -- Where's
        //     iterator over a List or over anything else, joined, then asked
        //     by an inlined Sum whether it is a List or an array -- answered
        //     where every object it can be answers alike.
        if (FoldTypeTests(f, items)) defs = new(f, buildCfg: false);
        if (!any) return;
        defs = new(f, buildCfg: false);

        // 2. A slot read from read-only data at a relocation.
        HashSet<VReg>? emptySlots = null;
        foreach (Block b in f.Blocks)
            for (int k = 0; k < b.Instrs.Count; k++)
            {
                Instr i = b.Instrs[k];
                if (i.Op != Opcode.Load || i.Size != word || i.Dest is null || i.Dest.Type != IrTypes.Word) continue;
                if (Symbol(i.Operands[0]) is not { } table || !items.TryGetValue(table.Name, out DataItem? item)) continue;
                long at = table.Offset + i.Offset;
                if (item.Relocs.FirstOrDefault(rel => rel.Offset == at) is not { Symbol: { } named } exact)
                {
                    // A SLOT A TYPE DOES NOT FILL, in its whole descriptor (one
                    // this unit wrote, naming itself): a method its objects do
                    // not have -- an interface's, asked of an iterator that
                    // is no collection, on the path a failed `is` guards. A
                    // call through it is never made.
                    if (table.Name.StartsWith("t_", StringComparison.Ordinal) && at >= Target.Current.DescriptorBytes
                        && item.Relocs.Any(rel => rel.Offset == 5 * word && rel.Symbol == table.Name))
                        (emptySlots ??= new()).Add(i.Dest);
                    // A type's flags word (Lowering's DescFlags): what a
                    // string's ToString, a sequence's walk, tests the
                    // descriptor for. Written by lowering and nothing after
                    // it, unlike words the link fills in.
                    if (table.Name.StartsWith("t_", StringComparison.Ordinal) && at == DescFlagsWord * word && at + word <= item.Bytes.Length
                        && !item.Relocs.Any(rel => rel.Offset == at))
                        b.Instrs[k] = new Instr { Op = Opcode.Copy, Dest = i.Dest, Line = i.Line,
                            Operands = { new ImmOperand(word == 8 ? BitConverter.ToInt64(item.Bytes, (int)at) : BitConverter.ToInt32(item.Bytes, (int)at), i.Dest.Type) } };
                    continue;
                }
                b.Instrs[k] = new Instr { Op = Opcode.Copy, Dest = i.Dest, Line = i.Line, Operands = { new SymOperand(named, exact.Addend) } };
            }
        defs = new(f, buildCfg: false);

        // 3. The call through it.
        foreach (Block b in f.Blocks)
            for (int k = 0; k < b.Instrs.Count; k++)
            {
                Instr i = b.Instrs[k];
                if (i.Op != Opcode.CallIndirect || i.Operands.Count < 1) continue;
                if (emptySlots is not null && i.Operands[0] is RegOperand { Reg: var empty } && emptySlots.Contains(empty) && defs.IsSingle(empty))
                {
                    i.DispatchType = NoTarget;
                    continue;
                }
                if (Symbol(i.Operands[0]) is not { Offset: 0 } target || !target.Name.StartsWith("m_", StringComparison.Ordinal)) continue;
                Instr call = new() { Op = Opcode.Call, Dest = i.Dest, Line = i.Line, Callee = target.Name };
                for (int a = 1; a < i.Operands.Count; a++) call.Operands.Add(i.Operands[a]);
                b.Instrs[k] = call;
                Resolved++;
            }

        // 4. A call on one of a few objects made here: one test of the vtable
        //    for each, a direct call under it.
        Guarded(f, items);
    }

    /// <summary>
    /// GUARDED, WHEN THE RECEIVER IS ONE OF A KNOWN FEW: a register every
    /// write of which is an object made in this function -- an inlined
    /// factory's paths (Select's iterator over an array, a List, anything
    /// else) joined in one result -- has one of those objects' types, each
    /// known from the vtable stored into it. A call through its vtable
    /// becomes a test against each and a direct call under each test: what
    /// each callee does with its arguments is then known, and the objects
    /// are this function's to free. The last type takes no test.
    /// </summary>
    private void Guarded(Function f, Dictionary<string, DataItem> items)
    {
        if (!f.Blocks.Any(b => b.Instrs.Any(i => i.Op == Opcode.CallIndirect))) return;
        int word = IrTypes.Word.Bytes();
        Dictionary<VReg, List<Instr>> writes = new();
        foreach (Block b in f.Blocks)
            foreach (Instr i in b.Instrs)
                if (i.Dest is { } d)
                {
                    if (!writes.TryGetValue(d, out List<Instr>? list)) writes[d] = list = new();
                    list.Add(i);
                }
        foreach (VReg p in f.Params) writes.Remove(p);
        // The vtable stored into each object as it is made.
        Dictionary<Instr, SymOperand> stamped = new(ReferenceEqualityComparer.Instance);
        Instr? MadeBy(VReg r)
        {
            for (int depth = 0; depth < 8 && writes.TryGetValue(r, out List<Instr>? list) && list.Count == 1; depth++)
            {
                Instr d = list[0];
                if (d.Op == Opcode.Call && Escape.IsAllocator(d.Callee)) return d;
                if (d.Op is not (Opcode.Copy or Opcode.Trunc64 or Opcode.ZExt32) || d.Operands[0] is not RegOperand next) return null;
                r = next.Reg;
            }
            return null;
        }
        foreach (Block b in f.Blocks)
            foreach (Instr i in b.Instrs)
                if (i.Op == Opcode.Store && i.Offset == 0 && i.Operands.Count >= 2 && i.Operands[0] is RegOperand at
                    && i.Operands[1] is SymOperand { Name: var t } vt && t.StartsWith("t_", StringComparison.Ordinal)
                    && MadeBy(at.Reg) is { } alloc)
                    stamped.TryAdd(alloc, vt);
        if (stamped.Count == 0) return;
        // The objects a register can hold, when they are all made here.
        bool Origins(VReg r, List<Instr> into, HashSet<VReg> seen, int depth)
        {
            if (depth > 8 || !seen.Add(r)) return depth <= 8;
            if (MadeBy(r) is { } one) { into.Add(one); return true; }
            if (!writes.TryGetValue(r, out List<Instr>? list)) return false;
            foreach (Instr d in list)
            {
                // Null before it is set (a foreach's enumerator): no call is
                // made through it then.
                if (d.Op == Opcode.Copy && d.Operands[0] is ImmOperand { Value: 0 }) continue;
                if (d.Op is not (Opcode.Copy or Opcode.Trunc64 or Opcode.ZExt32) || d.Operands[0] is not RegOperand next
                    || !Origins(next.Reg, into, seen, depth + 1)) return false;
            }
            return true;
        }
        for (int bi = 0; bi < f.Blocks.Count; bi++)
        {
            Block b = f.Blocks[bi];
            for (int k = 0; k < b.Instrs.Count; k++)
            {
                Instr i = b.Instrs[k];
                if (i.Op != Opcode.CallIndirect || i.Operands.Count < 2 || i.Operands[0] is not RegOperand fn
                    || !writes.TryGetValue(fn.Reg, out List<Instr>? fd) || fd.Count != 1 || fd[0] is not { Op: Opcode.Load } slotLoad
                    || slotLoad.Operands[0] is not RegOperand vt || !writes.TryGetValue(vt.Reg, out List<Instr>? vd) || vd.Count != 1
                    || vd[0] is not { Op: Opcode.Load, Offset: 0 } vtLoad || vtLoad.Operands[0] is not RegOperand recv) continue;
                List<Instr> origins = new();
                if (!Origins(recv.Reg, origins, new HashSet<VReg>(), 0) || origins.Count < 2) continue;
                // Each type's method in the slot read. A type with nothing
                // there cannot be the receiver -- the call would fault -- as
                // when a cast to an interface it lacks guards the call.
                List<(SymOperand Vtable, string Target)> cases = new();
                bool known = true;
                foreach (Instr origin in origins)
                {
                    if (!stamped.TryGetValue(origin, out SymOperand? table) || !items.TryGetValue(table.Name, out DataItem? item)) { known = false; break; }
                    long at = table.Offset + slotLoad.Offset;
                    if (at >= item.Bytes.Length || at < 0) { known = false; break; }
                    int found = item.Relocs.FindIndex(rel => rel.Offset == at);
                    if (found < 0) continue;
                    DataReloc there = item.Relocs[found];
                    if (there.Symbol is not { } target || there.Addend != 0 || !target.StartsWith("m_", StringComparison.Ordinal)) { known = false; break; }
                    if (!cases.Any(c => c.Vtable.Name == table.Name && c.Vtable.Offset == table.Offset)) cases.Add((table, target));
                }
                if (!known || cases.Count > 4) continue;
                if (cases.Count == 0)
                {
                    // None of them has it: whatever runs this call, it is not
                    // one of these objects (Escape reads the mark).
                    i.DispatchType = NoTarget;
                    continue;
                }
                Instr Direct(string target, VReg? into)
                {
                    Instr call = new() { Op = Opcode.Call, Dest = into, Line = i.Line, Callee = target };
                    for (int a = 1; a < i.Operands.Count; a++) call.Operands.Add(i.Operands[a]);
                    return call;
                }
                Resolved++;
                if (cases.Select(c => c.Target).Distinct(StringComparer.Ordinal).Count() == 1)
                {
                    b.Instrs[k] = Direct(cases[0].Target, i.Dest);
                    continue;
                }
                // Split: the tests where the call was, the rest after them.
                Block after = f.NewBlock("devirt");
                after.Instrs.AddRange(b.Instrs.Skip(k + 1));
                b.Instrs.RemoveRange(k, b.Instrs.Count - k);
                if (after.Terminator is { } end)
                {
                    foreach (Block s in end.Targets) Phi.Rename(s, b, after);
                    if (end.Default is { } d) Phi.Rename(d, b, after);
                }
                Block test = b;
                for (int c = 0; c < cases.Count; c++)
                {
                    Block each = f.NewBlock("devirt");
                    VReg? got = i.Dest is null ? null : f.NewReg(i.Dest.Type, i.Dest.Name);
                    each.Instrs.Add(Direct(cases[c].Target, got));
                    if (got is not null) each.Instrs.Add(new Instr { Op = Opcode.Copy, Dest = i.Dest, Operands = { new RegOperand(got) }, Line = i.Line });
                    each.Instrs.Add(new Instr { Op = Opcode.Jump, Targets = { after }, Line = i.Line });
                    if (c == cases.Count - 1)
                    {
                        test.Instrs.Add(new Instr { Op = Opcode.Jump, Targets = { each }, Line = i.Line });
                        break;
                    }
                    Block next = f.NewBlock("devirt");
                    VReg same = f.NewReg(IrType.I32, "isType");
                    test.Instrs.Add(new Instr { Op = Opcode.Eq, Dest = same, Operands = { new RegOperand(vt.Reg), new SymOperand(cases[c].Vtable.Name, cases[c].Vtable.Offset) }, Line = i.Line });
                    test.Instrs.Add(new Instr { Op = Opcode.Branch, Operands = { new RegOperand(same) }, Targets = { each, next }, Line = i.Line });
                    test = next;
                }
                break;   // the block was split; the rest of it is `after`, met later
            }
        }
    }

    /// <summary>
    /// A comparison whose every possible answer is the same, where one side
    /// is read from the descriptor of an object made here: the register
    /// read holds one of a few objects, all made in this function with their
    /// vtables stored as they are made (or null, through which nothing is
    /// read), so its first word is one of those vtables -- the word lowering
    /// writes once and nothing writes again. Words read on from a vtable at
    /// constant offsets are read-only data: a relocation's symbol, or the
    /// number lowering wrote (a type's depth). `x is T[]`, `x is List&lt;T&gt;`
    /// and their like, asked of Where's iterator, become constants, and the
    /// paths they guard go with BranchSimplify and dead-code elimination.
    /// </summary>
    private static bool FoldTypeTests(Function f, Dictionary<string, DataItem> items)
    {
        int word = IrTypes.Word.Bytes();
        Dictionary<VReg, List<Instr>> writes = new();
        foreach (Block b in f.Blocks)
            foreach (Instr i in b.Instrs)
                if (i.Dest is { } d)
                {
                    if (!writes.TryGetValue(d, out List<Instr>? list)) writes[d] = list = new();
                    list.Add(i);
                }
        foreach (VReg p in f.Params) writes.Remove(p);
        bool anyAlloc = false;
        foreach (List<Instr> ws in writes.Values) if (ws.Count == 1 && ws[0] is { Op: Opcode.Call } c && Escape.IsAllocator(c.Callee)) { anyAlloc = true; break; }
        if (!anyAlloc) return false;
        // The vtable stored into each object as it is made: its one store of
        // word 0, a symbol, through the allocation's own register chain.
        Dictionary<Instr, SymOperand?> stamped = new(ReferenceEqualityComparer.Instance);
        Instr? MadeBy(VReg r)
        {
            for (int depth = 0; depth < 8 && writes.TryGetValue(r, out List<Instr>? list) && list.Count == 1; depth++)
            {
                Instr d = list[0];
                if (d.Op == Opcode.Call && Escape.IsAllocator(d.Callee)) return d;
                if (d.Op is not (Opcode.Copy or Opcode.Trunc64 or Opcode.ZExt32) || d.Operands[0] is not RegOperand next) return null;
                r = next.Reg;
            }
            return null;
        }
        foreach (Block b in f.Blocks)
            foreach (Instr i in b.Instrs)
                if (i.Op == Opcode.Store && i.Offset == 0 && i.Operands.Count >= 2 && i.Operands[0] is RegOperand at && MadeBy(at.Reg) is { } alloc)
                {
                    // More than one store of word 0, or one not of a vtable:
                    // the word is not known.
                    // A vtable is a descriptor at its methods (DescriptorBytes on), never a
                    // type's identity (typeof, the descriptor itself) kept as data.
                    SymOperand? vt = i.Operands[1] is SymOperand { Name: var t, Offset: var o } s && o == Target.Current.DescriptorBytes
                        && t.Length > 2 && t[1] == '_' && t[0] is 't' or 'q' or 'b' or 'v' ? s : null;
                    stamped[alloc] = stamped.ContainsKey(alloc) ? null : vt;
                }
        if (stamped.Count == 0) return false;
        bool Origins(VReg r, List<Instr> into, HashSet<VReg> seen, int depth)
        {
            if (depth > 8 || !seen.Add(r)) return depth <= 8;
            if (MadeBy(r) is { } one) { into.Add(one); return true; }
            if (!writes.TryGetValue(r, out List<Instr>? list)) return false;
            foreach (Instr d in list)
            {
                if (d.Op == Opcode.Copy && d.Operands[0] is ImmOperand { Value: 0 }) continue;
                if (d.Op is not (Opcode.Copy or Opcode.Trunc64 or Opcode.ZExt32) || d.Operands[0] is not RegOperand next
                    || !Origins(next.Reg, into, seen, depth + 1)) return false;
            }
            return true;
        }
        // Every value an operand can have, as symbols with offsets or
        // numbers; null if not known.
        List<(string? Sym, long Value)>? Values(Operand o, int depth)
        {
            if (depth > 10) return null;
            if (o is ImmOperand imm) return new() { (null, imm.Value) };
            if (o is SymOperand sym) return new() { (sym.Name, sym.Offset) };
            if (o is not RegOperand { Reg: var r } || !writes.TryGetValue(r, out List<Instr>? ws) || ws.Count != 1) return null;
            Instr d = ws[0];
            switch (d.Op)
            {
                case Opcode.Copy or Opcode.Trunc64 or Opcode.ZExt32 when d.Operands.Count == 1:
                    return Values(d.Operands[0], depth + 1);
                case Opcode.Add or Opcode.Sub when d.Operands.Count == 2 && d.Operands[1] is ImmOperand plus:
                    return Values(d.Operands[0], depth + 1)?.Select(v => (v.Sym, d.Op == Opcode.Add ? v.Value + plus.Value : v.Value - plus.Value)).ToList();
                case Opcode.Load when d.Operands.Count == 1 && d.Operands[0] is RegOperand { Reg: var from }:
                {
                    if (d.Offset == 0 && d.Size == word)
                    {
                        List<Instr> origins = new();
                        if (Origins(from, origins, new HashSet<VReg>(), 0) && origins.Count is > 0 and <= 8)
                        {
                            List<(string?, long)> tables = new();
                            foreach (Instr origin in origins)
                            {
                                if (!stamped.TryGetValue(origin, out SymOperand? vt) || vt is null) return null;
                                if (!tables.Contains((vt.Name, vt.Offset))) tables.Add((vt.Name, vt.Offset));
                            }
                            return tables;
                        }
                    }
                    if (Values(d.Operands[0], depth + 1) is not { } bases) return null;
                    List<(string?, long)> read = new();
                    foreach ((string? table, long into) in bases)
                    {
                        if (table is null || !items.TryGetValue(table, out DataItem? item)) return null;
                        long w = into + d.Offset;
                        int found = item.Relocs.FindIndex(rel => rel.Offset == w);
                        if (found >= 0)
                        {
                            if (d.Size != word) return null;
                            read.Add((item.Relocs[found].Symbol, item.Relocs[found].Addend));
                            continue;
                        }
                        // A number lowering wrote -- not in another unit's class
                        // as this one shadows it, which leaves its relocations
                        // out: a word there may be one.
                        if (Shadow(table, item) || item.Relocs.Any(rel => rel.Offset < w + d.Size && rel.Offset + word > w)) return null;
                        if (w < 0 || w + d.Size > item.Bytes.Length || d.Size is not (4 or 8)) return null;
                        read.Add((null, d.Size == 8 ? BitConverter.ToInt64(item.Bytes, (int)w) : (d.Signed ? BitConverter.ToInt32(item.Bytes, (int)w) : BitConverter.ToUInt32(item.Bytes, (int)w))));
                    }
                    return read;
                }
                default:
                    return null;
            }
        }
        bool folded = false;
        foreach (Block b in f.Blocks)
            for (int k = 0; k < b.Instrs.Count; k++)
            {
                Instr i = b.Instrs[k];
                if (!IrInfo.IsIntCompare(i.Op) || i.Dest is null || i.Operands.Count != 2) continue;
                // Only where one side is read from an object made here.
                if (Values(i.Operands[0], 0) is not { } xs || Values(i.Operands[1], 0) is not { } ys) continue;
                bool? answer = null;
                foreach (var x in xs)
                    foreach (var y in ys)
                    {
                        bool? one;
                        if (x.Sym is not null || y.Sym is not null)
                            one = i.Op is Opcode.Eq or Opcode.Ne && x.Sym is not null && y.Sym is not null
                                ? (i.Op == Opcode.Eq) == (x.Sym == y.Sym && x.Value == y.Value) : null;
                        else one = i.Op switch
                        {
                            Opcode.Eq => x.Value == y.Value, Opcode.Ne => x.Value != y.Value,
                            Opcode.LtS => x.Value < y.Value, Opcode.LeS => x.Value <= y.Value,
                            Opcode.GtS => x.Value > y.Value, Opcode.GeS => x.Value >= y.Value,
                            _ => null,
                        };
                        if (one is null || answer is not null && answer != one) { answer = null; goto next; }
                        answer = one;
                    }
                next:
                if (answer is not bool known) continue;
                b.Instrs[k] = new Instr { Op = Opcode.Copy, Dest = i.Dest, Line = i.Line, Operands = { new ImmOperand(known ? 1 : 0, i.Dest.Type) } };
                folded = true;
            }
        return folded;

        // A class of another unit as this one knows it (Lowering.ShadowDescriptor):
        // no name, no self, only some of its methods.
        bool Shadow(string name, DataItem item)
            => name.StartsWith("t_", StringComparison.Ordinal) && !item.Relocs.Any(rel => rel.Offset == 5 * word && rel.Symbol == name);
    }

    private static bool KeepsFields(string? callee) =>
        callee is not null && (Escape.IsAllocator(callee) || Escape.NeverWritesFields(callee) || Escape.IsCollectorNote(callee)
            || callee == "m_Runtime_InvalidCastTo_2_V$Any_V$String" || callee.StartsWith("__x86.i.", StringComparison.Ordinal));

    private static readonly string? Trace = Switches.TraceForward;

    private static void ForwardFreshFields(Function f, Defs defs, Func<Operand, Instr?> made)
    {
        int word = IrTypes.Word.Bytes();
        // Every register derived from each fresh object, with its offset into
        // it; -1 for an address into it at an offset not known.
        Dictionary<Instr, Dictionary<VReg, long>> objects = new(ReferenceEqualityComparer.Instance);
        foreach (Block b in f.Blocks)
            foreach (Instr i in b.Instrs)
                if (i.Op == Opcode.Call && Escape.IsAllocator(i.Callee) && i.Dest is not null)
                    objects[i] = new() { [i.Dest] = 0 };
        if (objects.Count == 0) return;
        bool grew = true;
        while (grew)
        {
            grew = false;
            foreach (Block b in f.Blocks)
                foreach (Instr i in b.Instrs)
                {
                    // A register with two definitions holds something else at
                    // times: it is not followed, and a copy into it counts as a
                    // write where it happens (below), since what is stored
                    // through it later is not known to be another object.
                    if (i.Dest is null || i.Operands.Count == 0 || i.Operands[0] is not RegOperand r || !defs.IsSingle(i.Dest)) continue;
                    foreach (Dictionary<VReg, long> d in objects.Values)
                    {
                        if (!d.TryGetValue(r.Reg, out long at) || d.ContainsKey(i.Dest)) continue;
                        long? to = i.Op switch
                        {
                            Opcode.Copy or Opcode.Trunc64 or Opcode.ZExt32 => at,
                            Opcode.Add when i.Operands[1] is ImmOperand k && at >= 0 => at + k.Value,
                            Opcode.Add => -1,
                            _ => null,
                        };
                        if (to is long t) { d[i.Dest] = t; grew = true; }
                    }
                }
        }

        Cfg? cfg = null;
        Cfg G() => cfg ??= new Cfg(f);
        foreach ((Instr alloc, Dictionary<VReg, long> derived) in objects)
        {
            // Registers that may hold the object among other things (a copy
            // into a register with two definitions), and their copies: a
            // store or a call through one may write it.
            HashSet<VReg> aliases = new();
            bool more = true;
            while (more)
            {
                more = false;
                foreach (Block b in f.Blocks)
                    foreach (Instr i in b.Instrs)
                        if (i.Dest is not null && i.Op is Opcode.Copy or Opcode.Trunc64 or Opcode.ZExt32 or Opcode.Add
                            && i.Operands[0] is RegOperand from && (derived.ContainsKey(from.Reg) || aliases.Contains(from.Reg))
                            && !derived.ContainsKey(i.Dest) && aliases.Add(i.Dest))
                            more = true;
            }
            // A register given only null and this object (a foreach's enumerator,
            // zeroed first): a load through it reads this object -- through
            // null it would not return -- though a store through it is still
            // a write where it happens, as for any alias.
            Dictionary<VReg, List<Instr>> writes = new();
            foreach (Block b in f.Blocks)
                foreach (Instr i in b.Instrs)
                    if (i.Dest is not null && aliases.Contains(i.Dest))
                    {
                        if (!writes.TryGetValue(i.Dest, out List<Instr>? list)) writes[i.Dest] = list = new();
                        list.Add(i);
                    }
            HashSet<VReg> nullOrThis = new();
            foreach (VReg a in aliases)
            {
                if (defs.IsSingle(a) || !writes.TryGetValue(a, out List<Instr>? ws)) continue;
                int all = 0;
                foreach (Block b in f.Blocks) foreach (Instr i in b.Instrs) if (i.Dest == a) all++;
                if (all == ws.Count && ws.All(w => w.Op == Opcode.Copy && (w.Operands[0] is ImmOperand { Value: 0 }
                        || w.Operands[0] is RegOperand { Reg: var from } && derived.TryGetValue(from, out long o) && o == 0)))
                    nullOrThis.Add(a);
                else if (Trace is { } tt && f.Name.Contains(tt, StringComparison.Ordinal))
                    Console.Error.WriteLine($"forward {f.Name} alias {a}: writes {all}/{ws.Count}: {string.Join("; ", ws.Select(w => w.ToString()))}");
            }
            List<(Block B, int I, long At, int Size, Operand Value)> stores = new();
            List<(Block B, int I)> writers = new();
            foreach (Block b in f.Blocks)
                for (int k = 0; k < b.Instrs.Count; k++)
                {
                    Instr i = b.Instrs[k];
                    if (ReferenceEquals(i, alloc)) continue;
                    bool uses = false, viaAlias = false;
                    foreach (Operand o in i.Operands)
                    {
                        if (o is not RegOperand r) continue;
                        if (derived.ContainsKey(r.Reg)) uses = true;
                        else if (aliases.Contains(r.Reg)) viaAlias = true;
                    }
                    if (viaAlias && !uses)
                    {
                        // Through an alias: only what can write memory counts.
                        if (i.Op is Opcode.Store or Opcode.MemCopy or Opcode.MemSet or Opcode.CallIndirect
                            || i.Op == Opcode.Call && !KeepsFields(i.Callee))
                            writers.Add((b, k));
                        continue;
                    }
                    if (!uses) continue;
                    switch (i.Op)
                    {
                        case Opcode.Store:
                            if (i.Operands.Count >= 2 && i.Operands[1] is RegOperand v && derived.ContainsKey(v.Reg)) { writers.Add((b, k)); break; }
                            if (i.Operands[0] is RegOperand bas && derived.TryGetValue(bas.Reg, out long at) && at >= 0)
                                stores.Add((b, k, at + i.Offset, i.Size, i.Operands[1]));
                            else writers.Add((b, k));
                            break;
                        case Opcode.Load:
                        case Opcode.ArrayLength:
                        case Opcode.Branch:
                            break;
                        case Opcode.Copy or Opcode.Trunc64 or Opcode.ZExt32 or Opcode.Add when i.Dest is not null
                            && (derived.ContainsKey(i.Dest) || aliases.Contains(i.Dest)):
                            break;
                        case Opcode.Call when KeepsFields(i.Callee):
                            break;
                        default:
                            if (IrInfo.IsIntCompare(i.Op)) break;
                            writers.Add((b, k));
                            break;
                    }
                }
            if (stores.Count == 0) continue;

            // Whether a write can be followed by the load on the SAME object:
            // a path from it to the load that does not go back through the
            // allocation. Round a loop that makes the object again, the
            // registers name the new one, and the write was to the old.
            Block allocBlock = f.Blocks.First(b => b.Instrs.Contains(alloc));
            int allocAt = allocBlock.Instrs.IndexOf(alloc);
            bool Reaches((Block B, int I) w, Block lb, int li)
            {
                if (ReferenceEquals(w.B, lb) && w.I < li && !(ReferenceEquals(lb, allocBlock) && w.I < allocAt && allocAt < li)) return true;
                if (ReferenceEquals(w.B, allocBlock) && w.I < allocAt) return false;
                HashSet<Block> seen = new(ReferenceEqualityComparer.Instance);
                Stack<Block> work = new(G().Succs(w.B));
                while (work.Count > 0)
                {
                    Block b = work.Pop();
                    if (!seen.Add(b)) continue;
                    if (ReferenceEquals(b, lb) && !(ReferenceEquals(b, allocBlock) && allocAt < li)) return true;
                    if (ReferenceEquals(b, allocBlock)) continue;
                    foreach (Block n in G().Succs(b)) work.Push(n);
                }
                return false;
            }

            (Block B, int I) FirstReaching(List<(Block B, int I)> from, Block lb, int li)
            {
                foreach (var w in from) if (Reaches(w, lb, li)) return w;
                return default;
            }

            foreach (Block lb in f.Blocks)
                for (int li = 0; li < lb.Instrs.Count; li++)
                {
                    Instr l = lb.Instrs[li];
                    if (l.Op != Opcode.Load || l.Dest is null || l.Dest.Type.IsFloat() || l.Operands[0] is not RegOperand lr) continue;
                    long lat;
                    if (nullOrThis.Contains(lr.Reg)) lat = 0;
                    else if (!derived.TryGetValue(lr.Reg, out lat) || lat < 0) continue;
                    long at = lat + l.Offset;
                    bool trace = Trace is { } t && f.Name.Contains(t, StringComparison.Ordinal);
                    // Only the stores that can come before this load on the same
                    // object: one after it (the `state = 0` of the branch the
                    // test chooses) is no business of the load's.
                    // THE VTABLE, wherever it is read: the object's one store of
                    // word 0 is its making's, and a load through it anywhere --
                    // a finally's Dispose, which the graph shows no edge into --
                    // reads that.
                    if (at == 0 && l.Size == word)
                    {
                        int all = 0;
                        (Block B, int I, long At, int Size, Operand Value) only = default;
                        foreach (var st in stores)
                            if (st.At < word && 0 < st.At + st.Size && all++ == 0) only = st;
                        if (all == 1 && only.At == 0 && only.Value is SymOperand vt)
                        {
                            lb.Instrs[li] = new Instr { Op = Opcode.Copy, Dest = l.Dest, Line = l.Line, Operands = { new SymOperand(vt.Name, vt.Offset) } };
                            continue;
                        }
                    }
                    // Counted in a loop, to two: a lambda over `at` made a cell,
                    // an iterator and a list for every load in the function.
                    int touching = 0;
                    (Block B, int I, long At, int Size, Operand Value) s0 = default;
                    foreach (var st in stores)
                        if (st.At < at + l.Size && at < st.At + st.Size && Reaches((st.B, st.I), lb, li) && touching++ == 0)
                        {
                            s0 = st;
                        }
                        else if (touching > 1) break;
                    if (touching != 1) { if (trace) Console.Error.WriteLine($"forward {f.Name} {l}: {touching} stores"); continue; }
                    if (s0.At != at || s0.Size != l.Size || l.Size > word) continue;
                    bool first = ReferenceEquals(s0.B, lb) ? s0.I < li : G().Dominates(s0.B, lb);
                    if (!first)
                    { if (trace) Console.Error.WriteLine($"forward {f.Name} {l}: store not first"); continue; }
                    // The vtable word is the object's type, which nothing but its
                    // making writes: a call cannot change it, only a store could.
                    // Only where word 0 IS a vtable -- a symbol stored there: a
                    // captured variable's cell keeps its value in word 0, and
                    // the lambda writes it.
                    bool vtableWord = at == 0 && s0.Value is SymOperand;
                    if (!vtableWord && FirstReaching(writers, lb, li) is { B: not null } bad)
                    { if (trace) Console.Error.WriteLine($"forward {f.Name} {l}: writer {bad.B.Instrs[bad.I]}"); continue; }
                    Operand value = s0.Value switch
                    {
                        ImmOperand im => new ImmOperand(im.Value, l.Dest.Type),
                        SymOperand sy when l.Size == word => new SymOperand(sy.Name, sy.Offset),
                        RegOperand rv when defs.IsSingle(rv.Reg) && rv.Reg.Type == l.Dest.Type && l.Size == word => new RegOperand(rv.Reg),
                        _ => null!,
                    };
                    if (value is null) continue;
                    // A narrower load reads a narrower value: only an
                    // immediate is narrowed here, as the load would read it.
                    if (value is ImmOperand narrow && l.Size < 8)
                    {
                        long bits = l.Size == 4 ? (l.Signed ? (int)narrow.Value : (uint)narrow.Value)
                                  : l.Size == 2 ? (l.Signed ? (short)narrow.Value : (ushort)narrow.Value)
                                  : (l.Signed ? (sbyte)narrow.Value : (byte)narrow.Value);
                        value = new ImmOperand(bits, l.Dest.Type);
                    }
                    else if (l.Size < word) continue;
                    if (trace) Console.Error.WriteLine($"forward {f.Name} {l} in {lb.Label}: FORWARDED {value} from {s0.B.Instrs[s0.I]} in {s0.B.Label} alloc in {allocBlock.Label} writers [{string.Join("; ", writers.Select(w => w.B.Label + ":" + w.B.Instrs[w.I]))}]");
                    lb.Instrs[li] = new Instr { Op = Opcode.Copy, Dest = l.Dest, Line = l.Line, Operands = { value } };
                }
        }
    }

    /// <summary>A call through a vtable no object this function made can be the receiver of (Guarded).</summary>
    public const string NoTarget = "\u0001no-target";

    /// <summary>Lowering's DescFlags: the descriptor word a type's kind is in.</summary>
    private const int DescFlagsWord = 6;

    internal static Dictionary<string, DataItem> ReadOnlyItems(Module m)
    {
        Dictionary<string, DataItem> items = new(StringComparer.Ordinal);
        foreach (DataItem d in m.Data)
            if (d.ReadOnly && !d.Zero) items[d.Name] = d;
        foreach ((string name, DataItem shadow) in m.ShadowData) items.TryAdd(name, shadow);
        return items;
    }
}

/// <summary>
/// AFTER THE LATE INLINER, BEFORE THE LIFETIME RULES: what inlining a call
/// Devirtualize made direct exposed, folded. An iterator's inlined
/// GetEnumerator tests the state its constructor stored a few instructions
/// before: forwarded, the test is a constant, the branch that would make a
/// second iterator goes, and the enumerator is the one object the rules can
/// follow -- which a register holding either of two never was.
/// </summary>
public sealed class LateCleanup : IModulePass
{
    public string Name => "late-cleanup";

    public void Run(Module m)
    {
        Dictionary<string, DataItem> items = Devirtualize.ReadOnlyItems(m);
        Devirtualize devirtualize = new();
        IPass[] after = { new ConstantAndCopyPropagation(), new ConstantFold { AcrossFunction = true }, new BranchSimplify(), new DeadCodeElimination() };
        foreach (Function f in m.Functions)
        {
            // Twice round: a test folded to a constant is a register until it
            // is propagated into the branch that reads it, and the branch gone,
            // the enumerator is the one object, whose vtable can then be read.
            for (int round = 0; round < 3; round++)
            {
                devirtualize.Run(f, items);
                foreach (IPass p in after) p.Run(f);
            }
            if (Switches.DumpFunction == f.Name)
            {
                System.Text.StringBuilder text = new();
                f.Dump(text);
                Console.Error.WriteLine("== after late-cleanup " + f.Name + "\n" + text);
            }
        }
    }
}

/// <summary>
/// A closure's `$this` that its body never reads is not stored.
///
/// Every lambda written in an instance method is given the instance, used or
/// not, so that a lambda inside a lambda can reach it; a lambda that reads
/// only its own locals then carries `this` into whatever it is handed to --
/// a Select, a Where -- and `this` escapes there. The register allocator's
/// `s => index[s]` kept the whole Allocator, and every array it owns, from
/// being freed. A closure class is the unit's own, so every read of its
/// fields is in the unit: a `$this` field nothing loads is never needed, and
/// its stores go.
/// </summary>
public sealed class DeadClosureThis : IModulePass
{
    public string Name => "dead-closure-this";

    public int Removed { get; private set; }

    public void Run(Module m)
    {
        // The `$this` stores of closures, by field key, with the offset each writes.
        Dictionary<string, HashSet<long>> candidates = new(StringComparer.Ordinal);
        foreach (Function f in m.Functions)
            foreach (Block b in f.Blocks)
                foreach (Instr i in b.Instrs)
                    if (i.Op == Opcode.Store && i.Field is { } field && field.EndsWith("::$this", StringComparison.Ordinal)
                        && field.Contains("Lambda$", StringComparison.Ordinal))
                    {
                        if (!candidates.TryGetValue(field, out HashSet<long>? at)) candidates[field] = at = new();
                        at.Add(i.Offset);
                    }
        if (candidates.Count == 0) return;
        HashSet<string> dead = new(StringComparer.Ordinal);
        foreach ((string field, HashSet<long> offsets) in candidates)
        {
            if (offsets.Count != 1) continue;
            long at = offsets.First();
            // The closure's own methods, by their labels: only they are handed
            // the closure as `this`, and only through `this` is `$this` read --
            // by its Invoke, or to copy into a closure made inside it. None
            // found, it is kept: nothing is known.
            string prefix = "m_" + Decode(field[..field.IndexOf("::", StringComparison.Ordinal)]).Replace('.', '$') + "_";
            List<Function> methods = m.Functions.Where(f => f.Name.StartsWith(prefix, StringComparison.Ordinal) && f.Params.Count > 0).ToList();
            if (methods.Count == 0 || methods.Any(f => ReadsAt(f, at))) continue;
            dead.Add(field);
        }
        if (dead.Count == 0) return;
        foreach (Function f in m.Functions)
            foreach (Block b in f.Blocks)
                b.Instrs.RemoveAll(i =>
                {
                    bool gone = i.Op == Opcode.Store && i.Field is { } field && dead.Contains(field);
                    if (gone) Removed++;
                    return gone;
                });
    }

    /// <summary>Whether anything reads the word at `at` of the method's `this`, or reads it in a way not followed.</summary>
    private static bool ReadsAt(Function f, long at)
    {
        Dictionary<VReg, long> derived = new() { [f.Params[0]] = 0 };
        bool grew = true;
        while (grew)
        {
            grew = false;
            foreach (Block b in f.Blocks)
                foreach (Instr i in b.Instrs)
                {
                    if (i.Dest is null || derived.ContainsKey(i.Dest) || i.Operands.Count == 0 || i.Operands[0] is not RegOperand r
                        || !derived.TryGetValue(r.Reg, out long o)) continue;
                    long? to = i.Op switch
                    {
                        Opcode.Copy or Opcode.Trunc64 or Opcode.ZExt32 => o,
                        Opcode.Add when i.Operands[1] is ImmOperand k && o >= 0 => o + k.Value,
                        Opcode.Add => -1,
                        _ => null,
                    };
                    if (to is long t) { derived[i.Dest] = t; grew = true; }
                }
        }
        foreach (Block b in f.Blocks)
            foreach (Instr i in b.Instrs)
            {
                if (i.Op == Opcode.Load && i.Operands[0] is RegOperand r && derived.TryGetValue(r.Reg, out long o)
                    && (o < 0 || o + i.Offset <= at && at < o + i.Offset + i.Size))
                    return true;
                // Copied whole, or handed on: whatever reads it then is not seen.
                if (i.Op is Opcode.MemCopy or Opcode.Call or Opcode.CallIndirect or Opcode.Store)
                    foreach (Operand op in i.Operands)
                        if (op is RegOperand q && derived.ContainsKey(q.Reg)
                            && !(i.Op == Opcode.Store && ReferenceEquals(op, i.Operands[0])))
                            return true;
            }
        return false;
    }

    /// <summary>A type key back from Lowering.TypeKey's escaped form: `$` and four hex digits a character.</summary>
    private static string Decode(string escaped)
    {
        System.Text.StringBuilder sb = new();
        for (int k = 0; k < escaped.Length; k++)
        {
            if (escaped[k] == '$' && k + 4 < escaped.Length + 0 && k + 5 <= escaped.Length
                && int.TryParse(escaped.Substring(k + 1, 4), System.Globalization.NumberStyles.HexNumber, null, out int c))
            {
                sb.Append((char)c);
                k += 4;
            }
            else sb.Append(escaped[k]);
        }
        return sb.ToString();
    }
}
