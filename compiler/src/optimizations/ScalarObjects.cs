#nullable enable
using Corsac.Lang.Ir;
namespace Corsac.Lang.Opt;
using Block = Corsac.Lang.Ir.Block;

/// <summary>Scalar replacement of direct object fields with dominated lifetimes.</summary>
public sealed class ScalarObjects : IParallelModulePass
{
    public string Name => "scalar-objects";
    public int Replaced { get; private set; }
    public int Workers { get; set; } = 1;

    /// <summary>Whether the module has Runtime.WriteBarrierValues, which a barrier on a replaced object becomes.</summary>
    private bool _valueBarrier;

    public void Run(Module module)
    {
        if (Workers < 1 || Workers > 64) throw new ArgumentOutOfRangeException(nameof(Workers));
        _valueBarrier = module.Functions.Any(fn => fn.Name == Escape.ValueBarrier);
        if (Workers > 1 && module.Functions.Count > 1)
            RunParallel(module);
        else
            foreach (Function f in module.Functions) Replaced += Fold(f, _valueBarrier);
    }

    private void RunParallel(Module module)
    {
        int[] counts = new int[module.Functions.Count];
        FunctionWorkers.Run(module, Workers, (function, index) => counts[index] = Fold(function, _valueBarrier));
        foreach (int count in counts) Replaced += count;
    }

    private static int Fold(Function f, bool valueBarrier)
    {
            if (f.Async is not null) return 0;
            int replaced = 0;
            // ONE ANALYSIS OF THE FUNCTION FOR ALL ITS CANDIDATES, made again
            // only after a replacement has changed the function. Made afresh
            // for every candidate, a function with thousands of small
            // allocations -- what inlining a whole kernel into one module
            // leaves -- cost thousands of whole-function control-flow graphs
            // and liveness solutions: most of a whole-kernel compile.
            Analysis cache = new(f);
            foreach (Block block in f.Blocks)
                foreach (Instr alloc in block.Instrs.ToArray())
                    if (alloc.Op == Opcode.Call && Escape.IsAllocator(alloc.Callee)
                        && alloc.Dest is not null && alloc.Operands.Count == 1
                        && alloc.Operands[0] is ImmOperand size && size.Value > 0 && size.Value <= 1024)
                        if (Replace(f, block, alloc, size.Value, cache, valueBarrier)) { replaced++; cache = new(f); }
            if (replaced != 0)
            {
                // Expose child references before the following escape pass:
                // scalar fields need not hide behind dead mutable copies.
                new LocalCopies().Run(f);
                new DeadCodeElimination().Run(f);
            }
            return replaced;
    }

    /// <summary>The definitions and liveness of a function as it stands, each made when first asked for.</summary>
    private sealed class Analysis
    {
        private readonly Function _f;
        private Defs? _defs;
        private Liveness? _liveness;
        public Analysis(Function f) { _f = f; }
        public Defs Defs => _defs ??= new Defs(_f);
        public Liveness Liveness => _liveness ??= new Liveness(Defs.Cfg);
        private Dictionary<VReg, List<(Block Block, int Index)>>? _uses;
        /// <summary>Every instruction that reads each register, in block and instruction order.</summary>
        public Dictionary<VReg, List<(Block Block, int Index)>> Uses
        {
            get
            {
                if (_uses is not null) return _uses;
                _uses = new();
                foreach (Block b in _f.Blocks)
                    for (int k = 0; k < b.Instrs.Count; k++)
                        foreach (VReg r in IrInfo.Uses(b.Instrs[k]).Distinct())
                        {
                            if (!_uses.TryGetValue(r, out var list)) _uses[r] = list = new();
                            list.Add((b, k));
                        }
                return _uses;
            }
        }
    }

    private static bool Replace(Function f, Block block, Instr alloc, long bytes, Analysis cache, bool valueBarrier)
    {
        Defs defs = cache.Defs;
        if (!defs.IsSingle(alloc.Dest!) || defs.Cfg.Roots.Count != 1) return false;
        Dictionary<VReg, long> addresses = new() { [alloc.Dest!] = 0 };
        HashSet<Instr> aliases = new();
        int start = block.Instrs.IndexOf(alloc);
        // The object's addresses, followed from its registers' readers only.
        var uses = cache.Uses;
        Queue<VReg> pending = new();
        pending.Enqueue(alloc.Dest!);
        while (pending.TryDequeue(out VReg? from))
        {
            if (!uses.TryGetValue(from, out var readers)) continue;
            foreach ((Block rb, int ri) in readers)
            {
            Instr i = rb.Instrs[ri];
            if (i.Dest is null || !defs.IsSingle(i.Dest) || i.Operands.Count == 0
                || addresses.ContainsKey(i.Dest)
                || i.Operands[0] is not RegOperand r || !addresses.TryGetValue(r.Reg, out long at)) continue;
            if (i.Op is Opcode.Copy or Opcode.Trunc64 or Opcode.ZExt32)
            { addresses[i.Dest] = at; aliases.Add(i); pending.Enqueue(i.Dest); }
            else if (i.Op == Opcode.Add && i.Operands.Count == 2 && i.Operands[1] is ImmOperand delta
                && delta.Value >= 0 && delta.Value <= bytes && at <= bytes - delta.Value)
            { addresses[i.Dest] = at + delta.Value; aliases.Add(i); pending.Enqueue(i.Dest); }
            }
        }

        Dictionary<long, (int Size, IrType Type)> fields = new();
        Dictionary<Instr, long> accesses = new();
        // THE WRITE BARRIER ON ONE OF ITS FIELDS. Compiled code tells the
        // collector of a reference it is about to overwrite, by the slot's
        // address -- which a replaced object no longer has. What the barrier
        // reads there is the field's value, so it becomes the barrier told
        // that value (Runtime.WriteBarrierValues): the same two references
        // reported, and nothing about the object left in memory to point at.
        // Without this an out-of-line barrier kept every object whose field
        // it guarded in memory, and whether it was out of line was the
        // inliner's decision about the barrier's size that day.
        Dictionary<Instr, long> barriers = new();
        // AND THE CARD MARK AFTER ONE: it names the kilobyte of the heap an
        // object's field is in, for the next minor collection to read. A
        // replaced object has no field in the heap, so its marks go with it.
        HashSet<Instr> cardMarks = new();
        // Only the instructions that read one of its addresses, each once.
        HashSet<(Block, int)> seen = new();
        List<(Block Block, int Index)> touching = new();
        foreach (VReg a in addresses.Keys)
            if (uses.TryGetValue(a, out var readers))
                foreach (var site in readers)
                    if (seen.Add(site)) touching.Add(site);
        foreach ((Block b, int k) in touching)
        {
            Instr i = b.Instrs[k];
            // No loop-carried references, calls, address escapes,
            // identity tests, unknown offsets, overlapping fields or partial loads.
            if (!defs.Cfg.Dominates(block, b) || (b == block && k <= start)) return false;
            foreach (VReg r in IrInfo.Uses(i).Where(addresses.ContainsKey))
            {
                var site = defs.Site(r);
                if (site is null || !defs.Cfg.Dominates(site.Value.Block, b)
                    || (site.Value.Block == b && site.Value.Index >= k)) return false;
            }
            if (aliases.Contains(i)) continue;
            if (valueBarrier && i.Op == Opcode.Call && i.Callee == Escape.Barrier && i.Dest is null && i.Operands.Count == 2
                && i.Operands[0] is RegOperand slot && addresses.TryGetValue(slot.Reg, out long slotAt)
                && !(i.Operands[1] is RegOperand passed && addresses.ContainsKey(passed.Reg)))
            {
                barriers[i] = slotAt;
                continue;
            }
            if (i.Op == Opcode.Call && i.Callee == CardMarks.CardMark && i.Dest is null && i.Operands.Count == 1
                && i.Operands[0] is RegOperand marked && addresses.ContainsKey(marked.Reg))
            {
                cardMarks.Add(i);
                continue;
            }
            if (i.Op is not (Opcode.Load or Opcode.Store) || i.Size is not (1 or 2 or 4 or 8)
                || i.Operands[0] is not RegOperand root || !addresses.TryGetValue(root.Reg, out long offset)) return false;
            if (i.Op == Opcode.Store && i.Operands[1] is RegOperand value && addresses.ContainsKey(value.Reg)) return false;
            if (i.Offset < 0 || i.Offset > bytes || offset > bytes - i.Offset) return false;
            offset += i.Offset;
            if (offset > bytes - i.Size) return false;
            IrType type = i.Op == Opcode.Load ? i.Dest!.Type : i.Operands[1].Type;
            if (!((type == IrType.I32 && i.Size <= 4) || (type == IrType.I64 && i.Size == 8))) return false;
            foreach (var field in fields)
                if (offset < field.Key + field.Value.Size && field.Key < offset + i.Size
                    && (field.Key != offset || field.Value.Size != i.Size || field.Value.Type != type)) return false;
            fields[offset] = (i.Size, type);
            accesses[i] = offset;
        }
        if (fields.Count == 0) return false;
        // A barrier guards a field that is stored, a whole word of it.
        foreach (long slotAt in barriers.Values)
            if (!fields.TryGetValue(slotAt, out var guarded) || guarded.Size != 4 && guarded.Size != 8) return false;
        // Last, being the dearest: nothing loop-carried still holds it.
        if (Escape.LiveAtSelf(cache.Liveness, block, alloc, addresses.Keys.ToHashSet())) return false;
        Dictionary<long, VReg> locals = fields.ToDictionary(p => p.Key, p => f.NewReg(p.Value.Type, "scalarfield"));
        foreach (Block b in f.Blocks)
        {
        List<Instr> result = new();
        foreach (Instr i in b.Instrs)
        {
            if (i == alloc)
            {
                foreach (var field in locals)
                    result.Add(new Instr { Op = Opcode.Copy, Dest = field.Value,
                        Operands = { new ImmOperand(0, field.Value.Type) }, Line = i.Line });
            }
            else if (aliases.Contains(i) || cardMarks.Contains(i)) { }
            else if (barriers.TryGetValue(i, out long slotAt))
            {
                VReg field = locals[slotAt];
                Operand old = new RegOperand(Escape.Word(f, result, field, i.Line, "barrierold"));
                result.Add(new Instr { Op = Opcode.Call, Callee = Escape.ValueBarrier, Operands = { old, i.Operands[1] }, Line = i.Line });
            }
            else if (accesses.TryGetValue(i, out long offset))
            {
                if (i.Size < 4)
                {
                    // A store truncates even when its input is a full-width
                    // register. Signedness belongs to each load, not the slot.
                    bool signed = i.Op == Opcode.Load && i.Signed;
                    Opcode extend = (i.Size, signed) switch
                    {
                        (1, true) => Opcode.SExt8, (1, false) => Opcode.ZExt8,
                        (2, true) => Opcode.SExt16, _ => Opcode.ZExt16,
                    };
                    result.Add(new Instr { Op = extend,
                        Dest = i.Op == Opcode.Load ? i.Dest : locals[offset],
                        Operands = { i.Op == Opcode.Load ? new RegOperand(locals[offset]) : i.Operands[1] },
                        Line = i.Line });
                }
                else
                result.Add(i.Op == Opcode.Load ? IrInfo.CopyOf(i, new RegOperand(locals[offset]))
                    : new Instr { Op = Opcode.Copy, Dest = locals[offset], Operands = { i.Operands[1] }, Line = i.Line });
            }
            else result.Add(i);
        }
        b.Instrs.Clear(); b.Instrs.AddRange(result);
        }
        return true;
    }
}
