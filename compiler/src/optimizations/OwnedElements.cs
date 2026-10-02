#nullable enable
using System.Text.RegularExpressions;
using Corsac.Lang.Ir;

namespace Corsac.Lang.Opt;

using Block = Corsac.Lang.Ir.Block;

/// <summary>
/// OWNED ELEMENTS: a List or a Dictionary a function makes, fills with
/// objects it makes for it, reads back without letting any go, and drops,
/// gives its elements back as it dies.
///
/// The field rules free a collection's arrays with it; what the arrays HOLD
/// was the collector's -- the per-block maps a pass keeps in a map, the lists
/// a table of lists holds. Two steps. Before inlining, while every use of the
/// collection is still a call of a method this rule knows (MarkOwnedElements),
/// the allocation is marked a candidate and those calls are kept from the
/// inliner. In the lifetime pass, with every function's summary known
/// (Confirm): each value added must be an object made here for it, added once
/// and let go nowhere else; each value read back (an indexer's answer, what
/// TryGetValue wrote) must go nowhere; and every use of either keeps the
/// collection alive (an inserted KeepAlive), so wherever the collection is
/// freed -- at its last use, before it is made again, at a promoted slot's end
/// -- its elements are dead and are freed first (Runtime.FreeOwnedElements).
/// A collection that escapes is never freed, and its elements stay the
/// collector's, as before.
/// </summary>
internal static class OwnedElements
{
    internal static readonly string? Trace = Environment.GetEnvironmentVariable("CORSAC_ELEMENTS_TRACE") is { Length: > 0 } t ? t : null;
    internal static void Say(Function f, string what)
    {
        if (Trace is { } t && f.Name.Contains(t, StringComparison.Ordinal)) Console.Error.WriteLine($"elements {f.Name}: {what}");
    }

    public const string Freer = "m_Runtime_FreeOwnedElements_1_V$Any";

    private static readonly Regex Method = new(@"^m_(List|Dictionary)\$.+?_(set_Item|get_Item|Add|TryAdd|TryGetValue|ContainsKey|get_Count|Remove|RemoveAt|Clear|GetValueOrDefault|Insert)_(\d+)(_|$)", RegexOptions.Compiled);

    /// <summary>What a known call does with its operands: the adder's value operand, the out-slot operand, whether it answers an element.</summary>
    internal readonly record struct Role(bool Known, int Adds = -1, int OutSlot = -1, bool Reads = false);

    internal static Role RoleOf(string kind, string callee)
    {
        // A constructor: the type's own name again, after the type.
        if (callee.StartsWith("m_" + kind + "$", StringComparison.Ordinal) && callee.Contains("_" + kind + "$", StringComparison.Ordinal)
            && !Method.IsMatch(callee))
            return new(true);
        Match match = Method.Match(callee);
        if (!match.Success || match.Groups[1].Value != kind) return new(false);
        string name = match.Groups[2].Value;
        int count = int.Parse(match.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture);
        return (kind, name, count) switch
        {
            ("Dictionary", "set_Item", 2) or ("Dictionary", "Add", 2) or ("Dictionary", "TryAdd", 2) => new(true, Adds: 2),
            ("Dictionary", "TryGetValue", 2) => new(true, OutSlot: 2),
            ("Dictionary", "get_Item", 1) or ("Dictionary", "GetValueOrDefault", _) => new(true, Reads: true),
            ("Dictionary", "ContainsKey", 1) or ("Dictionary", "get_Count", 0) or ("Dictionary", "Remove", 1) or ("Dictionary", "Clear", 0) => new(true),
            ("List", "Add", 1) => new(true, Adds: 1),
            ("List", "set_Item", 2) or ("List", "Insert", 2) => new(true, Adds: 2),
            ("List", "get_Item", 1) => new(true, Reads: true),
            ("List", "get_Count", 0) or ("List", "Clear", 0) or ("List", "RemoveAt", 1) => new(true),
            _ => new(false),
        };
    }

    /// <summary>The collection kind of an allocation, from the vtable stored into it, or null.</summary>
    internal static string? KindOf(Function f, Instr alloc, HashSet<VReg> container)
    {
        foreach (Block b in f.Blocks)
            foreach (Instr i in b.Instrs)
                if (i.Op == Opcode.Store && i.Offset == 0 && i.Operands.Count >= 2 && i.Operands[0] is RegOperand bas
                    && container.Contains(bas.Reg) && i.Operands[1] is SymOperand vt)
                {
                    if (vt.Name.StartsWith("t_List$0024", StringComparison.Ordinal)) return "List";
                    if (vt.Name.StartsWith("t_Dictionary$0024", StringComparison.Ordinal)) return "Dictionary";
                    return null;
                }
        return null;
    }

    /// <summary>The registers that hold the allocation's object, through copies of registers written once.</summary>
    internal static HashSet<VReg> Container(Function f, Defs defs, Instr alloc)
    {
        HashSet<VReg> set = new() { alloc.Dest! };
        bool grew = true;
        while (grew)
        {
            grew = false;
            foreach (Block b in f.Blocks)
                foreach (Instr i in b.Instrs)
                    if (i.Dest is not null && !set.Contains(i.Dest) && defs.IsSingle(i.Dest) && i.Op is Opcode.Copy or Opcode.Trunc64 or Opcode.ZExt32
                        && i.Operands[0] is RegOperand r && set.Contains(r.Reg))
                    { set.Add(i.Dest); grew = true; }
        }
        return set;
    }

    /// <summary>A field's address that only a write barrier or a card mark is given.</summary>
    private static bool BarrierOnly(Function f, VReg address)
    {
        foreach (Block b in f.Blocks)
            foreach (Instr i in b.Instrs)
                for (int k = 0; k < i.Operands.Count; k++)
                    if (i.Operands[k] is RegOperand r && r.Reg == address
                        && !(k == 0 && i.Op == Opcode.Call && i.Callee is { } c
                             && (c.StartsWith("m_Runtime_WriteBarrier_", StringComparison.Ordinal)
                                 || c.StartsWith("m_Runtime_CardMark_", StringComparison.Ordinal))))
                        return false;
        return true;
    }

    /// <summary>Whether every use of the collection is one this rule follows; the calls among them, with their roles.</summary>
    internal static List<(Block B, Instr Call, Role Role)>? Uses(Function f, string kind, HashSet<VReg> container, Instr alloc)
    {
        List<(Block, Instr, Role)> calls = new();
        foreach (Block b in f.Blocks)
            foreach (Instr i in b.Instrs)
            {
                if (ReferenceEquals(i, alloc)) continue;
                int at = -1, count = 0;
                for (int k = 0; k < i.Operands.Count; k++)
                    if (i.Operands[k] is RegOperand r && container.Contains(r.Reg)) { at = k; count++; }
                if (count == 0) continue;
                switch (i.Op)
                {
                    case Opcode.Copy or Opcode.Trunc64 or Opcode.ZExt32 when i.Dest is not null && container.Contains(i.Dest):
                        continue;
                    // The vtable, and the collection's own counters as an inlined
                    // Count reads them; never its storage, where an inlined
                    // getter would take an element out unseen. Lowering tags
                    // every load of a field that holds a reference (and the
                    // inliner keeps the tag), so an untagged one reads a number.
                    case Opcode.Load when at == 0 && (i.Offset == 0 || i.Field is null):
                    case Opcode.Branch:
                        continue;
                    case Opcode.Store when at == 0 && count == 1 && i.Offset == 0 && i.Operands[1] is SymOperand:
                        continue;
                    // An inlined constructor filling the collection's own fields
                    // (fresh storage, counters): the value stored is not the
                    // collection, and an element stored anywhere is the
                    // element's escape, which its own flow refuses.
                    case Opcode.Store when at == 0 && count == 1 && i.Offset != 0:
                        continue;
                    case Opcode.Add when at == 0 && count == 1 && i.Operands[1] is ImmOperand && i.Dest is not null
                        && BarrierOnly(f, i.Dest):
                        continue;
                    case Opcode.Call when i.Callee is not null && at == 0 && count == 1:
                        if (i.Callee == Corsac.Lang.X86.MachineIntrinsics.KeepAlive) continue;
                        Role role = RoleOf(kind, i.Callee);
                        if (!role.Known) { Say(f, $"unknown call: {i.Callee}"); return null; }
                        calls.Add((b, i, role));
                        continue;
                    default:
                        if (IrInfo.IsIntCompare(i.Op)) continue;
                        Say(f, $"refused use: {i}");
                        return null;
                }
            }
        return calls;
    }
}

/// <summary>Before inlining: the candidates, and their calls kept from the inliner (OwnedElements).</summary>
public sealed class MarkOwnedElements : IModulePass
{
    public string Name => "mark-owned-elements";

    public void Run(Module m)
    {
        foreach (Function f in m.Functions)
        {
            if (f.Async is not null) continue;
            Defs? defs = null;
            foreach (Block b in f.Blocks)
                foreach (Instr i in b.Instrs)
                {
                    if (i.Op != Opcode.Call || i.Callee != Escape.ObjectAllocator || i.Dest is null || i.Field is not null) continue;
                    defs ??= new Defs(f, buildCfg: false);
                    HashSet<VReg> container = OwnedElements.Container(f, defs, i);
                    if (OwnedElements.KindOf(f, i, container) is not { } kind) continue;
                    if (OwnedElements.Uses(f, kind, container, i) is not { } calls) { OwnedElements.Say(f, $"{kind} at {i.Line}: a use not followed"); continue; }
                    if (!calls.Any(c => c.Role.Adds >= 0)) continue;
                    OwnedElements.Say(f, $"{kind} at {i.Line}: candidate");
                    i.Field = Instr.OwnsCandidate;
                    foreach (var c in calls) m.KeepCalls.Add(c.Call);
                }
        }
    }
}

public sealed partial class Escape
{
    /// <summary>How many collections were proved to own their elements.</summary>
    public int ElementsOwned { get; private set; }

    /// <summary>
    /// The candidates of one function judged with every summary known
    /// (OwnedElements): proved ones marked OwnsElements, with a KeepAlive of
    /// the collection after every use of what it holds; the rest unmarked.
    /// </summary>
    private void ConfirmOwnedElements(Function f, Dictionary<string, bool[]> summaries)
    {
        List<Instr> candidates = new();
        foreach (Block b in f.Blocks)
            foreach (Instr i in b.Instrs)
                if (i.Op == Opcode.Call && i.Field == Instr.OwnsCandidate) candidates.Add(i);
        if (candidates.Count == 0) return;
        foreach (Instr alloc in candidates)
        {
            alloc.Field = null;
            if (f.Async is not null || alloc.Dest is null) continue;
            Defs defs = new(f);
            HashSet<VReg> container = OwnedElements.Container(f, defs, alloc);
            if (OwnedElements.KindOf(f, alloc, container) is not { } kind) continue;
            if (OwnedElements.Uses(f, kind, container, alloc) is not { } calls) continue;
            if (ProveOwnedElements(f, defs, alloc, container, calls, summaries) is not { } keepAlive) { OwnedElements.Say(f, $"at {alloc.Line}: not proved ({_elementWhy})"); continue; }
            OwnedElements.Say(f, $"at {alloc.Line}: OWNS ELEMENTS");
            // The collection kept alive past every use of what it holds.
            VReg holder = alloc.Dest;
            foreach ((Block b, Instr after) in keepAlive)
            {
                int at = b.Instrs.IndexOf(after);
                if (at < 0) continue;
                Instr keep = new() { Op = Opcode.Call, Callee = Corsac.Lang.X86.MachineIntrinsics.KeepAlive, Operands = { new RegOperand(holder) }, Line = after.Line };
                if (ReferenceEquals(after, b.Terminator)) b.Instrs.Insert(at, keep);
                else b.Instrs.Insert(at + 1, keep);
            }
            alloc.Field = Instr.OwnsElements;
            ElementsOwned++;
        }
    }

    private string _elementWhy = "";

    private List<(Block B, Instr After)>? ProveOwnedElements(Function f, Defs defs, Instr alloc, HashSet<VReg> container,
        List<(Block B, Instr Call, OwnedElements.Role Role)> calls, Dictionary<string, bool[]> summaries)
    {
        Cfg cfg = new(f);
        Block allocBlock = f.Blocks.First(b => b.Instrs.Contains(alloc));
        // The out-slots TryGetValue writes elements into.
        HashSet<FrameSlot> slots = new();
        foreach ((Block _, Instr call, OwnedElements.Role role) in calls)
        {
            if (role.OutSlot < 0) continue;
            if (SlotOf(defs, call.Operands[role.OutSlot]) is not { } slot) { _elementWhy = "rule 1"; return null; }
            slots.Add(slot);
        }
        // Every use of those slots: loads (elements), stores, the calls' own addresses, the collector's notes.
        List<(Block B, Instr I)> slotLoads = new();
        List<(Block B, int I, Instr Store)> slotStores = new();
        foreach (Block b in f.Blocks)
            for (int k = 0; k < b.Instrs.Count; k++)
            {
                Instr i = b.Instrs[k];
                for (int o = 0; o < i.Operands.Count; o++)
                {
                    if (SlotOf(defs, i.Operands[o]) is not { } slot || !slots.Contains(slot)) continue;
                    if (i.Op == Opcode.Load && o == 0) { slotLoads.Add((b, i)); continue; }
                    if (i.Op == Opcode.Store && o == 0) { slotStores.Add((b, k, i)); continue; }
                    if (i.Op is Opcode.Copy && i.Dest is not null) continue;
                    if (i.Op == Opcode.Call && (IsCollectorNote(i.Callee) || i.Callee is not null && i.Callee.StartsWith("m_Runtime_WriteBarrier", StringComparison.Ordinal)
                        || i.Callee is not null && i.Callee.StartsWith("m_Runtime_CardMark", StringComparison.Ordinal))) continue;
                    if (i.Op == Opcode.Call && calls.Any(c => ReferenceEquals(c.Call, i) && c.Role.OutSlot == o)) continue;
                    { _elementWhy = "rule 2"; return null; }
                }
            }

        // THE VALUES ADDED: each an object made here, added once, gone nowhere else.
        Dictionary<Instr, Instr> adderOf = new(ReferenceEqualityComparer.Instance);
        List<HashSet<VReg>> held = new();
        foreach ((Block b, Instr call, OwnedElements.Role role) in calls)
        {
            if (role.Adds < 0) continue;
            List<Instr> origins = new();
            HashSet<VReg> joins = new();
            if (Origin(f, defs, b, call, call.Operands[role.Adds], slots) is { } single) origins.Add(single);
            else if (VariableOrigins(f, defs, call.Operands[role.Adds], joins) is { } several) origins.AddRange(several);
            else { _elementWhy = "rule 3"; return null; }
            foreach (Instr origin in origins)
                if (!adderOf.TryAdd(origin, call)) { _elementWhy = "rule 4"; return null; }
            // Added again round a loop without being made again: the same object twice.
            HashSet<Block> renewing = new(ReferenceEqualityComparer.Instance);
            foreach (Instr origin in origins) renewing.Add(f.Blocks.First(x => x.Instrs.Contains(origin)));
            if (ReachesAvoiding(cfg, b, b, renewing)) { _elementWhy = "rule 5"; return null; }
            HashSet<Instr> stores = new(ReferenceEqualityComparer.Instance);
            foreach (var st in slotStores) stores.Add(st.Store);
            Flow flow = Analyse(f, origins.Select(o => o.Dest!).ToArray(), summaries, origins.Count == 1 ? origins[0] : null, stores,
                joinable: joins.Count > 0 ? joins : null, consumers: new(ReferenceEqualityComparer.Instance) { call });
            if (flow.Escapes) { _elementWhy = "rule 6"; return null; }
            held.Add(flow.Derived);
        }
        if (adderOf.Count == 0) { _elementWhy = "rule 7"; return null; }
        // Every slot store puts one of those objects there.
        foreach (var st in slotStores)
            if (st.Store.Operands[1] is not RegOperand v || !held.Any(h => h.Contains(v.Reg))) { _elementWhy = "rule 8"; return null; }

        // THE VALUES READ BACK: going nowhere.
        foreach ((Block b, Instr call, OwnedElements.Role role) in calls)
            if (role.Reads && call.Dest is { } got)
            {
                Flow flow = Analyse(f, new[] { got }, summaries, null);
                if (flow.Escapes) { _elementWhy = "rule 9"; return null; }
                held.Add(flow.Derived);
            }
        foreach ((Block b, Instr load) in slotLoads)
        {
            if (load.Dest is null) continue;
            Flow flow = Analyse(f, new[] { load.Dest }, summaries, null);
            if (flow.Escapes) { _elementWhy = "rule 10"; return null; }
            held.Add(flow.Derived);
        }

        // Every use of anything held keeps the collection alive: the
        // collection's register must reach each such use.
        List<(Block B, Instr After)> keep = new();
        foreach (Block b in f.Blocks)
            foreach (Instr i in b.Instrs)
            {
                bool uses = slotLoads.Any(l => ReferenceEquals(l.I, i));
                if (!uses)
                    foreach (Operand o in i.Operands)
                        if (o is RegOperand r && held.Any(h => h.Contains(r.Reg))) { uses = true; break; }
                if (!uses) continue;
                bool dominated = ReferenceEquals(b, allocBlock) ? b.Instrs.IndexOf(alloc) < b.Instrs.IndexOf(i) : cfg.Dominates(allocBlock, b);
                if (!dominated) { _elementWhy = "rule 11"; return null; }
                keep.Add((b, i));
            }
        return keep;
    }

    /// <summary>A frame slot an operand names, directly or through a copy of its address.</summary>
    private static FrameSlot? SlotOf(Defs defs, Operand o)
    {
        for (int hops = 0; hops < 4; hops++)
        {
            if (o is SlotOperand s) return s.Slot;
            if (o is not RegOperand r || !defs.IsSingle(r.Reg) || defs.Definition(r.Reg) is not { Op: Opcode.Copy } d) return null;
            o = d.Operands[0];
        }
        return null;
    }

    /// <summary>
    /// The allocation an added value is: through copies, or a load of an
    /// out-slot whose last write in this block, before the load, stored one.
    /// </summary>
    private static Instr? Origin(Function f, Defs defs, Block b, Instr call, Operand value, HashSet<FrameSlot> slots)
    {
        for (int hops = 0; hops < 8; hops++)
        {
            if (value is not RegOperand r || !defs.IsSingle(r.Reg) || defs.Definition(r.Reg) is not { } d) return null;
            if (d.Op == Opcode.Call && IsAllocator(d.Callee)) return d;
            if (d.Op is Opcode.Copy or Opcode.Trunc64 or Opcode.ZExt32 && d.Operands[0] is RegOperand) { value = d.Operands[0]; continue; }
            if (d.Op == Opcode.Load && d.Offset == 0 && SlotOf(defs, d.Operands[0]) is { } slot && slots.Contains(slot))
            {
                int at = b.Instrs.IndexOf(d);
                if (at < 0) return null;
                for (int k = at - 1; k >= 0; k--)
                {
                    Instr w = b.Instrs[k];
                    bool writes = w.Op == Opcode.Store && w.Offset == 0 && SlotOf(defs, w.Operands[0]) == slot
                        || w.Op is Opcode.Call or Opcode.CallIndirect && w.Operands.Any(o => SlotOf(defs, o) == slot)
                           && !(w.Callee is { } c && (IsCollectorNote(c) || c.StartsWith("m_Runtime_WriteBarrier", StringComparison.Ordinal)
                                || c.StartsWith("m_Runtime_CardMark", StringComparison.Ordinal)));
                    if (!writes) continue;
                    if (w.Op != Opcode.Store) return null;
                    value = w.Operands[1];
                    goto next;
                }
                return null;
            }
            return null;
        next:;
        }
        return null;
    }

    /// <summary>
    /// The allocations a variable is given, when every value it is ever given
    /// is one (`memory = new(); if (...) memory = new(inherited);`): each a
    /// fresh object, the variable joining them.
    /// </summary>
    private static List<Instr>? VariableOrigins(Function f, Defs defs, Operand value, HashSet<VReg> joins)
    {
        if (value is not RegOperand r || defs.IsSingle(r.Reg)) return null;
        List<Instr> origins = new();
        foreach (Block b in f.Blocks)
            foreach (Instr i in b.Instrs)
            {
                if (i.Dest != r.Reg) continue;
                if (i.Op != Opcode.Copy || i.Operands[0] is not RegOperand from) return null;
                Operand o = new RegOperand(from.Reg);
                Instr? made = null;
                for (int hops = 0; hops < 8 && made is null; hops++)
                {
                    if (o is not RegOperand q || !defs.IsSingle(q.Reg) || defs.Definition(q.Reg) is not { } d) return null;
                    if (d.Op == Opcode.Call && IsAllocator(d.Callee)) made = d;
                    else if (d.Op is Opcode.Copy or Opcode.Trunc64 or Opcode.ZExt32) o = d.Operands[0];
                    else return null;
                }
                if (made is null) return null;
                origins.Add(made);
            }
        joins.Add(r.Reg);
        return origins.Count > 0 ? origins : null;
    }

    private static bool ReachesAvoiding(Cfg cfg, Block from, Block to, HashSet<Block> avoid)
    {
        HashSet<Block> seen = new(ReferenceEqualityComparer.Instance);
        Stack<Block> work = new(cfg.Succs(from));
        while (work.Count > 0)
        {
            Block b = work.Pop();
            if (!seen.Add(b)) continue;
            if (avoid.Contains(b)) continue;
            if (ReferenceEquals(b, to)) return true;
            foreach (Block n in cfg.Succs(b)) work.Push(n);
        }
        return false;
    }

    /// <summary>Whether `from` reaches `to` (one edge at least) without passing through `avoid`.</summary>
    private static bool ReachesAvoiding(Cfg cfg, Block from, Block to, Block avoid)
    {
        HashSet<Block> seen = new(ReferenceEqualityComparer.Instance);
        Stack<Block> work = new(cfg.Succs(from));
        while (work.Count > 0)
        {
            Block b = work.Pop();
            if (!seen.Add(b)) continue;
            if (ReferenceEquals(b, to)) return true;
            if (ReferenceEquals(b, avoid)) continue;
            foreach (Block n in cfg.Succs(b)) work.Push(n);
        }
        return false;
    }

    /// <summary>The call that gives back an owning collection's elements, before whatever frees the collection.</summary>
    private static void AppendElementFree(Function f, List<Instr> output, Instr made, VReg pointer, int line)
    {
        if (made.Field != Instr.OwnsElements) return;
        VReg argument = Word(f, output, pointer, line, "elementsOf");
        output.Add(new Instr { Op = Opcode.Call, Callee = OwnedElements.Freer, Operands = { new RegOperand(argument) }, Line = line });
    }
}
