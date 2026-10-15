#nullable enable
using Corsac.Lang.Ir;
using Corsac.Lang.Lto;

namespace Corsac.Lang.Opt;

using Block = Corsac.Lang.Ir.Block;

// ---- whether the program needs a collector, for the link -------------------------------
//
// A WHOLE PROGRAM SAYS IT ITSELF (CollectorSites): from the entry, over every
// call and every address its code and data name, the allocations nothing
// frees, that run more than once and hold no table for the life of the
// program. A unit sees only itself, so it writes down what that walk reads
// of each of its functions once its own lifetimes are decided -- its calls,
// with whether it frees what each hands back; the addresses it names; the
// allocations it left to the collector, with the flags, fields and thrown
// types that could still excuse them -- and the link walks every unit's
// records at once (Lto.HeapDecision). Where they say no collector is needed,
// every unit is made again without one (Module.LinkHeap).

public sealed partial class Escape
{
    /// <summary>The types each function's catches keep what they caught of, "*" for all (KeepCaughtIn).</summary>
    private readonly Dictionary<string, HashSet<string>> _keptCatchesIn = new(StringComparer.Ordinal);

    /// <summary>
    /// The unit's records for the link's walk (Lto.HeapRecords), from the
    /// module as this run leaves its lifetimes: every allocation it owns,
    /// every call whose result it frees, decided.
    /// </summary>
    private HeapRecords HeapRecordsOf(Module m, Dictionary<string, bool[]> summaries)
    {
        HeapRecords records = new() { CallsCollector = m.CallsCollector, NoCollector = m.NoCollector };
        foreach (Function f in m.Functions)
        {
            if (IsStaticInitialiser(f.Name)) continue;
            foreach (Block b in f.Blocks)
                foreach (Instr i in b.Instrs)
                    if (i.Op == Opcode.Store && i.Operands.Count >= 2 && i.Operands[0] is SymOperand { Name: var field } && field.StartsWith("s_", StringComparison.Ordinal))
                        records.StaticWrites.Add(field);
        }
        foreach (Function f in m.Functions) records.Functions.Add(HeapFunctionOf(f, summaries));
        return records;
    }

    private HeapFunction HeapFunctionOf(Function f, Dictionary<string, bool[]> summaries)
    {
        HeapFunction record = new(f.Name) { Async = f.Async is not null };
        if (_keptCatchesIn.TryGetValue(f.Name, out HashSet<string>? kept))
        {
            record.KeptCatchAll = kept.Contains("*");
            record.KeptCatches.AddRange(kept.Where(type => type != "*").Order(StringComparer.Ordinal));
        }

        // The latch flags, as OnceRun reads them over the whole program.
        SortedSet<string> sets = new(StringComparer.Ordinal), breaks = new(StringComparer.Ordinal);
        foreach (Block b in f.Blocks)
            foreach (Instr i in b.Instrs)
            {
                if (i.Op == Opcode.Store && i.Operands.Count == 2 && i.Operands[0] is SymOperand { Name: var flag })
                {
                    if (i.Operands[1] is ImmOperand { Value: not 0 }) sets.Add(flag); else breaks.Add(flag);
                    if (i.Operands[1] is SymOperand { Name: var stored }) breaks.Add(stored);
                }
                else
                    for (int k = 0; k < i.Operands.Count; k++)
                        if (i.Operands[k] is SymOperand { Name: var named } && !(i.Op == Opcode.Load && k == 0)) breaks.Add(named);
            }
        record.LatchSets.AddRange(sets); record.LatchBreaks.AddRange(breaks);

        HashSet<Block> repeating = Repeating(f);
        Dictionary<Block, HashSet<string>> latched = OnceRun.LatchedBy(f);
        Dictionary<Instr, string> permanent = PermanentCandidates(f, repeating);
        Dictionary<Instr, string> held = HeldByFields(f, summaries);
        _freshOrigins.TryGetValue(f.Name, out HashSet<Instr>? origins);
        Dictionary<Instr, string[]>? virtuals = null;
        foreach (Block b in f.Blocks)
        {
            HeapFlags block = (repeating.Contains(b) ? HeapFlags.None : HeapFlags.Once)
                | (b.Terminator is { Op: Opcode.Unreachable } ? HeapFlags.Trapping : HeapFlags.None);
            string[] latches = latched.TryGetValue(b, out HashSet<string>? flags) ? flags.Order(StringComparer.Ordinal).ToArray() : Array.Empty<string>();
            int dying = DyingFrom(b);
            for (int at = 0; at < b.Instrs.Count; at++)
            {
                Instr i = b.Instrs[at];
                if (at == dying) block |= HeapFlags.Dying;
                if (i.Callee is not null)
                {
                    HeapFlags call = block | (i.Op == Opcode.Call ? HeapFlags.Direct : HeapFlags.None)
                        | (origins is not null && origins.Contains(i) ? HeapFlags.Origin : HeapFlags.None)
                        | (_ownedCalls.Contains(i) ? HeapFlags.OwnedResult : HeapFlags.None)
                        | (_owned.Contains(i) ? HeapFlags.OwnedAllocation : HeapFlags.None);
                    bool site = IsAllocator(i.Callee) && !_owned.Contains(i) && !_permanent.Contains(i) && !_fieldOwned.Contains(i);
                    if (site) call |= HeapFlags.Site;
                    record.Events.Add(new(HeapEventKind.Call, i.Callee, call, latches,
                        site ? permanent.GetValueOrDefault(i) : null,
                        site && _thrown.Contains(i) && _thrownType.TryGetValue(i, out string? type) ? type : null) { Held = held.GetValueOrDefault(i) });
                }
                foreach (Operand o in i.Operands)
                    if (o is SymOperand sym) record.Events.Add(new(HeapEventKind.Named, sym.Name, block, latches, null, null));
                if (i.Op != Opcode.CallIndirect) continue;
                // A target the unit names by its virtual slot (VirtualCallees) is the link's to resolve.
                if (_indirect is not null && _indirect.TryGetValue(i, out string[]? targets))
                    foreach (string target in targets)
                        record.Events.Add(new(target.StartsWith(VirtualTargets.Prefix, StringComparison.Ordinal) ? HeapEventKind.Virtual : HeapEventKind.Target,
                            target, block, latches, null, null));
                else if (i.DispatchType is not null)
                {
                    virtuals ??= VirtualCallees(new[] { f });
                    foreach (string name in virtuals.TryGetValue(i, out string[]? named) ? named : new[] { "*" })
                        record.Events.Add(new(HeapEventKind.Virtual, name, block, latches, null, null));
                }
            }
        }
        return record;
    }

    /// <summary>
    /// The allocations of a static initialiser stored straight into a static
    /// field, on no cycle (PermanentStatics): each permanent where nothing
    /// but an initialiser stores to that field, which the whole program says.
    /// </summary>
    private static Dictionary<Instr, string> PermanentCandidates(Function f, HashSet<Block> repeating)
    {
        Dictionary<Instr, string> found = new(ReferenceEqualityComparer.Instance);
        if (!IsStaticInitialiser(f.Name)) return found;
        Dictionary<VReg, (Instr Alloc, Block Block)> made = new();
        foreach (Block b in f.Blocks)
            foreach (Instr i in b.Instrs)
                if (i.Op == Opcode.Call && IsAllocator(i.Callee) && i.Dest is not null) made[i.Dest] = (i, b);
        foreach (Block b in f.Blocks)
            foreach (Instr i in b.Instrs)
                if (i.Op == Opcode.Store && i.Operands.Count >= 2 && i.Operands[0] is SymOperand { Name: var field } && field.StartsWith("s_", StringComparison.Ordinal)
                    && i.Operands[1] is RegOperand { Reg: var value } && made.TryGetValue(value, out var alloc) && !repeating.Contains(alloc.Block))
                    found.TryAdd(alloc.Alloc, field);
        return found;
    }

    /// <summary>
    /// THE FRESH OBJECTS `f` STORES INTO A FIELD AND LETS GO OF THERE, as
    /// OwnedFields finds them (its held pairs): an allocation, or a call
    /// handing back a fresh object, whose value goes nowhere but that one
    /// store. Where the whole program finds the field owns what it holds,
    /// its owner frees the object: neither an allocation the collector is
    /// needed for, nor a call that leaves what it hands back to it.
    /// </summary>
    private Dictionary<Instr, string> HeldByFields(Function f, Dictionary<string, bool[]> summaries)
    {
        Dictionary<Instr, string> held = new(ReferenceEqualityComparer.Instance);
        Dictionary<VReg, Instr>? defs = null;
        foreach (Block b in f.Blocks)
            foreach (Instr st in b.Instrs)
            {
                if (st is not { Op: Opcode.Store, Field: { } field } || st.Operands.Count < 2 || st.Operands[1] is not RegOperand { Reg: var value }) continue;
                defs ??= SingleDefs(f);
                Instr? made = null;
                for (int hop = 0; hop < 8 && defs.TryGetValue(value, out Instr? d); hop++)
                {
                    if (d.Op is Opcode.Copy or Opcode.Trunc64 or Opcode.ZExt32 && d.Operands.Count == 1 && d.Operands[0] is RegOperand from) { value = from.Reg; continue; }
                    made = d;
                    break;
                }
                if (made is not { Op: Opcode.Call, Dest: { } dest } || !(IsAllocator(made.Callee) || IsFreshCall(made)) || held.ContainsKey(made)) continue;
                if (Analyse(f, new[] { dest }, summaries, made, new HashSet<Instr>(ReferenceEqualityComparer.Instance) { st }).Escapes) continue;
                held[made] = field;
            }
        return held;
    }

    /// <summary>
    /// The names `f` refers to that it would not without a collector: its
    /// code under Runtime.CollectorLinked() answered no, the bookkeeping the
    /// manual heap drops, and the collecting allocators and frees, which
    /// become the manual heap's -- as the end of Run makes it, where a
    /// program needs no collector.
    /// </summary>
    /// <summary>
    /// Every function's names gone without a collector (GoneWithoutCollector),
    /// as the module stands: taken where the IR archive is, before the late
    /// passes, so they are the edges of the archive's references (HeapRecords.Gone).
    /// </summary>
    public static Dictionary<string, string[]> GoneOf(Module m)
    {
        Dictionary<string, string[]> gone = new(StringComparer.Ordinal);
        foreach (Function f in m.Functions)
            if (GoneWithoutCollector(f) is { Count: > 0 } names) gone[f.Name] = names.ToArray();
        return gone;
    }

    private static List<string> GoneWithoutCollector(Function f)
    {
        HashSet<string> all = new(StringComparer.Ordinal), kept = new(StringComparer.Ordinal);
        Dictionary<VReg, int> defs = new();
        foreach (Block b in f.Blocks)
        {
            foreach (Instr i in b.Instrs)
            {
                if (i.Callee is not null) all.Add(i.Callee);
                foreach (Operand o in i.Operands) if (o is SymOperand sym) all.Add(sym.Name);
                if (i.Dest is not null) defs[i.Dest] = defs.GetValueOrDefault(i.Dest) + 1;
            }
            if (b.Terminator is Instr t) foreach (Operand o in t.Operands) if (o is SymOperand sym) all.Add(sym.Name);
        }
        if (all.Count == 0) return new List<string>();
        bool asks = all.Contains(CollectorQuery);
        // What runs with the question answered no: a branch on its answer,
        // or on that answer compared with zero, goes one way only.
        Dictionary<VReg, long> known = new();
        if (asks)
            foreach (Block b in f.Blocks)
                foreach (Instr i in b.Instrs)
                    if (i is { Op: Opcode.Call, Callee: CollectorQuery, Dest: { } answer } && defs[answer] == 1) known[answer] = 0;
        bool grew = asks;
        while (grew)
        {
            grew = false;
            foreach (Block b in f.Blocks)
                foreach (Instr i in b.Instrs)
                {
                    if (i.Dest is null || known.ContainsKey(i.Dest)) continue;
                    long? value = i switch
                    {
                        { Op: Opcode.Copy or Opcode.ZExt32 or Opcode.SExt32 or Opcode.Trunc64, Operands: [RegOperand { Reg: var from }] }
                            when known.TryGetValue(from, out long v) => v,
                        { Op: Opcode.Eq, Operands: [RegOperand { Reg: var x }, ImmOperand { Value: var c }] } when known.TryGetValue(x, out long v) => v == c ? 1 : 0,
                        { Op: Opcode.Ne, Operands: [RegOperand { Reg: var x }, ImmOperand { Value: var c }] } when known.TryGetValue(x, out long v) => v != c ? 1 : 0,
                        _ => null,
                    };
                    if (value is long found && defs[i.Dest] == 1) { known[i.Dest] = found; grew = true; }
                }
        }
        HashSet<Block> live = new(ReferenceEqualityComparer.Instance);
        Stack<Block> work = new();
        if (f.Blocks.Count > 0) { live.Add(f.Blocks[0]); work.Push(f.Blocks[0]); }
        while (work.TryPop(out Block? b))
        {
            IEnumerable<Block> next = b.Terminator is { Op: Opcode.Branch, Operands: [RegOperand { Reg: var cond }] } branch && branch.Targets.Count == 2
                && known.TryGetValue(cond, out long taken)
                ? new[] { taken != 0 ? branch.Targets[0] : branch.Targets[1] }
                : b.Terminator?.Targets ?? (IEnumerable<Block>)Array.Empty<Block>();
            foreach (Instr i in b.Instrs) if (i.Op == Opcode.LabelAddr) next = next.Concat(i.Targets);
            foreach (Block n in next) if (live.Add(n)) work.Push(n);
        }
        foreach (Block b in f.Blocks)
        {
            if (!live.Contains(b)) continue;
            foreach (Instr i in b.Instrs)
            {
                if (i.Callee is string callee)
                {
                    bool dropped = callee == CollectorQuery
                        || i.Dest is null && (IsCollectorNote(callee) || callee is ThreadBlocking or ThreadUnblocking or ThreadSafePoint or ThreadRegister);
                    if (!dropped)
                        kept.Add(callee switch
                        {
                            Allocator or LeafAllocator => ManualAllocator,
                            ObjectAllocator => ManualObjectAllocator,
                            CollectorFreer => ManualFreer,
                            CollectorLive => ManualLive,
                            _ => callee,
                        });
                }
                foreach (Operand o in i.Operands) if (o is SymOperand sym) kept.Add(sym.Name);
            }
            if (b.Terminator is Instr t) foreach (Operand o in t.Operands) if (o is SymOperand sym) kept.Add(sym.Name);
        }
        return all.Where(name => !kept.Contains(name)).Order(StringComparer.Ordinal).ToList();
    }
}
