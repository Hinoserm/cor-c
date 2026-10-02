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

    private static readonly Regex Method = new(@"^m_(List|Dictionary)\$.+?_(set_Item|get_Item|Add|TryAdd|TryGetValue|ContainsKey|get_Count|Remove|RemoveAt|Clear|GetValueOrDefault|Insert|GetEnumerator|get_Values|get_Keys)_(\d+)(_|$)", RegexOptions.Compiled);

    /// <summary>
    /// What a known call does with its operands: the adder's value operand,
    /// the out-slot operand, whether it answers an element, whether it makes
    /// an enumerator of the collection (a foreach) or a view of it (a
    /// Dictionary's Values or Keys, walked by one).
    /// </summary>
    internal readonly record struct Role(bool Known, int Adds = -1, int OutSlot = -1, bool Reads = false, bool Enumerates = false, bool Views = false);

    /// <summary>
    /// The types declared inside List and Dictionary, whose names begin with
    /// the collection's own -- "Dictionary$ValueCollection$int$Tok" -- and
    /// are none of it: a ValueCollection handed back by Values was taken for
    /// a Dictionary, and its Count for the table's.
    /// </summary>
    private static readonly string[] Nested = { "Enumerator$", "KeyCollection$", "KeyEnumerator$", "ValueCollection$", "ValueEnumerator$" };

    private static bool IsNested(string name, int at)
    {
        foreach (string n in Nested)
            if (string.CompareOrdinal(name, at, n, 0, n.Length) == 0) return true;
        return false;
    }

    internal static Role RoleOf(string kind, string callee)
    {
        if (callee.StartsWith("m_" + kind + "$", StringComparison.Ordinal) && IsNested(callee, kind.Length + 3)) return new(false);
        // A constructor: the type's own name again, after the type -- AFTER
        // it: "m_List$" holds "_List$" itself, and searched from the start
        // every method of the type, ToArray included, read as a constructor.
        string prefix = "m_" + kind + "$";
        if (callee.StartsWith(prefix, StringComparison.Ordinal)
            && callee.IndexOf("_" + kind + "$", prefix.Length, StringComparison.Ordinal) >= 0
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
            ("List", "GetEnumerator", 0) or ("Dictionary", "GetEnumerator", 0) => new(true, Enumerates: true),
            ("Dictionary", "get_Values", 0) or ("Dictionary", "get_Keys", 0) => new(true, Views: true),
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
                    if (vt.Name.StartsWith("t_List$0024", StringComparison.Ordinal) && !IsNested(vt.Name.Replace("$0024", "$"), "t_List$".Length)) return "List";
                    if (vt.Name.StartsWith("t_Dictionary$0024", StringComparison.Ordinal) && !IsNested(vt.Name.Replace("$0024", "$"), "t_Dictionary$".Length)) return "Dictionary";
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

    /// <summary>
    /// Whether every use of the collection is one this rule follows; the
    /// calls among them, with their roles. A return of it is one when the
    /// function hands it back (`returned`), and then every return must be.
    /// </summary>
    internal static List<(Block B, Instr Call, Role Role)>? Uses(Function f, Defs defs, string kind, HashSet<VReg> container, Instr alloc, out bool returned)
    {
        List<(Block, Instr, Role)> calls = new();
        returned = false;
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
                    case Opcode.Ret when count == 1 && i.Operands.Count == 1:
                        returned = true;
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
                        if (role.Enumerates && Walker(f, defs, kind, i, calls) is null) { Say(f, $"enumerator not followed: {i}"); return null; }
                        if (role.Views && View(f, defs, i, calls) is null) { Say(f, $"view not followed: {i}"); return null; }
                        continue;
                    default:
                        if (IrInfo.IsIntCompare(i.Op)) continue;
                        Say(f, $"refused use: {i}");
                        return null;
                }
            }
        // HANDED BACK, the function's answer must be this collection on every
        // path: its caller takes what it is given for it (MarkOwnedElements).
        if (returned)
            foreach (Block b in f.Blocks)
                if (b.Terminator is { Op: Opcode.Ret } ret && (ret.Operands.Count != 1 || ret.Operands[0] is not RegOperand back || !container.Contains(back.Reg)))
                { Say(f, $"returns something else: {ret}"); return null; }
        return calls;
    }

    /// <summary>
    /// The enumerator registers of a GetEnumerator call this rule keeps, for
    /// the lifetime pass (Escape.Analyse): the collection is held in the slot,
    /// and every use of the slot is a call of its enumerator, so those
    /// registers are the collection's own -- its uses, and its escape if the
    /// enumerator's methods let them go. Null for any other call.
    /// </summary>
    internal static HashSet<VReg>? WalkerOf(Function f, Instr get, IReadOnlySet<Instr>? ignore)
    {
        if (get.Callee is not { } callee || !callee.EndsWith("_GetEnumerator_0", StringComparison.Ordinal)) return null;
        if (ViewEnumerator(callee) is { } enumerator) return Walker(f, new Defs(f, buildCfg: false), "Dictionary", get, null, enumerator, reads: false, ignore);
        string? kind = callee.StartsWith("m_List$", StringComparison.Ordinal) ? "List"
            : callee.StartsWith("m_Dictionary$", StringComparison.Ordinal) ? "Dictionary" : null;
        if (kind is null || !RoleOf(kind, callee).Enumerates) return null;
        return Walker(f, new Defs(f, buildCfg: false), kind, get, null, ignore: ignore);
    }

    /// <summary>
    /// The view registers of a Values or Keys call this rule keeps, for the
    /// lifetime pass, as WalkerOf; null for any other call. What that pass
    /// has written (`ignore`) is passed over: the view handed back fresh is
    /// owned and freed by it, and that is no use of the table.
    /// </summary>
    internal static HashSet<VReg>? ViewOf(Function f, Instr get, IReadOnlySet<Instr>? ignore)
    {
        if (get.Callee is not { } callee || get.Operands.Count != 1
            || !callee.EndsWith("_get_Values_0", StringComparison.Ordinal) && !callee.EndsWith("_get_Keys_0", StringComparison.Ordinal)
            || !RoleOf("Dictionary", callee).Views) return null;
        return View(f, new Defs(f, buildCfg: false), get, null, ignore);
    }

    /// <summary>The enumerator a Dictionary view's GetEnumerator makes, by its methods' prefix; null for another callee.</summary>
    private static string? ViewEnumerator(string callee)
    {
        if (callee.StartsWith("m_Dictionary$ValueCollection$", StringComparison.Ordinal)) return "m_Dictionary$ValueEnumerator$";
        if (callee.StartsWith("m_Dictionary$KeyCollection$", StringComparison.Ordinal)) return "m_Dictionary$KeyEnumerator$";
        return null;
    }

    /// <summary>
    /// A DICTIONARY'S VALUES OR KEYS: an object holding the table, walked by
    /// an enumerator of its own (`foreach (var v in table.Values)`). Followed
    /// as the table is: the view's registers handed only to its own
    /// GetEnumerator, whose walk is followed as the table's is (Walker), and
    /// its Count. A value its enumerator answers is an element read; a key is
    /// not one. The view's registers, or null.
    /// </summary>
    private static HashSet<VReg>? View(Function f, Defs defs, Instr get, List<(Block B, Instr Call, Role Role)>? calls, IReadOnlySet<Instr>? ignore = null)
    {
        if (get.Dest is null) return null;
        bool values = get.Callee!.EndsWith("_get_Values_0", StringComparison.Ordinal);
        string collection = values ? "m_Dictionary$ValueCollection$" : "m_Dictionary$KeyCollection$";
        HashSet<VReg> view = new() { get.Dest };
        bool grew = true;
        while (grew)
        {
            grew = false;
            foreach (Block b in f.Blocks)
                foreach (Instr i in b.Instrs)
                    if (i.Dest is not null && !view.Contains(i.Dest) && defs.IsSingle(i.Dest) && i.Op is Opcode.Copy or Opcode.Trunc64 or Opcode.ZExt32
                        && i.Operands[0] is RegOperand r && view.Contains(r.Reg))
                    { view.Add(i.Dest); grew = true; }
        }
        foreach (Block b in f.Blocks)
            foreach (Instr i in b.Instrs)
            {
                if (ignore?.Contains(i) == true) continue;
                int at = -1, count = 0;
                for (int k = 0; k < i.Operands.Count; k++)
                    if (i.Operands[k] is RegOperand r && view.Contains(r.Reg)) { at = k; count++; }
                if (count == 0) continue;
                if (i.Op is Opcode.Copy or Opcode.Trunc64 or Opcode.ZExt32 && i.Dest is not null && view.Contains(i.Dest)) continue;
                // Its vtable read, as a call on it checks it is there.
                if (i.Op == Opcode.Load && at == 0 && count == 1 && i.Offset == 0) continue;
                if (i.Op != Opcode.Call || i.Callee is not { } callee || count != 1) return null;
                if (callee == Corsac.Lang.X86.MachineIntrinsics.KeepAlive || Escape.IsCollectorNote(callee)) continue;
                if (at != 0 || !callee.StartsWith(collection, StringComparison.Ordinal)) return null;
                if (callee.EndsWith("_get_Count_0", StringComparison.Ordinal) && i.Operands.Count == 1) calls?.Add((b, i, new(true)));
                else if (callee.EndsWith("_GetEnumerator_0", StringComparison.Ordinal) && ViewEnumerator(callee) is { } enumerator
                    && Walker(f, defs, "Dictionary", i, calls, enumerator, reads: values, ignore) is not null)
                    calls?.Add((b, i, new(true)));
                else return null;
            }
        return view;
    }

    /// <summary>
    /// A FOREACH OVER THE COLLECTION: GetEnumerator writes a struct
    /// enumerator into a frame slot and answers its address, and the loop
    /// moves it on and takes each element from it. The struct holds the
    /// collection and, in its Current, an element -- memory no use of the
    /// collection's registers shows -- so the slot is followed too: only its
    /// address taken, that address (and the answer, the same address) handed
    /// only to the enumerator's MoveNext, Current and Dispose. Current then
    /// answers an element as the indexer does, and those calls are kept from
    /// the inliner with the rest, since inlined MoveNext would copy an element
    /// out of the storage and into the slot unseen. The registers holding
    /// GetEnumerator's answer, or null when a use is not one of those; the
    /// enumerator's calls added to `calls` when it is given.
    /// </summary>
    internal static HashSet<VReg>? Walker(Function f, Defs defs, string kind, Instr get, List<(Block B, Instr Call, Role Role)>? calls,
        string? enumerator = null, bool reads = true, IReadOnlySet<Instr>? ignore = null)
    {
        if (get.Operands.Count != 2 || get.Operands[1] is not RegOperand given) return null;
        // The slot the enumerator lives in, through copies of its address.
        FrameSlot? slot = null;
        Operand o = given;
        for (int hops = 0; hops < 4 && slot is null; hops++)
        {
            if (o is SlotOperand s) { slot = s.Slot; break; }
            if (o is not RegOperand r || !defs.IsSingle(r.Reg) || defs.Definition(r.Reg) is not { Op: Opcode.Copy } d) return null;
            o = d.Operands[0];
        }
        if (slot is null) return null;
        // Every register holding its address, through copies written once:
        // the slot's own, which only GetEnumerator is handed, and the answer's,
        // which only the enumerator's calls are.
        HashSet<VReg> walker = new(), address = new();
        if (get.Dest is not null) walker.Add(get.Dest);
        bool grew = true;
        while (grew)
        {
            grew = false;
            foreach (Block b in f.Blocks)
                foreach (Instr i in b.Instrs)
                {
                    if (i.Dest is null || walker.Contains(i.Dest) || address.Contains(i.Dest) || !defs.IsSingle(i.Dest)
                        || i.Op is not (Opcode.Copy or Opcode.Trunc64 or Opcode.ZExt32)) continue;
                    if (i.Operands[0] is RegOperand r && walker.Contains(r.Reg)) { walker.Add(i.Dest); grew = true; }
                    else if (i.Operands[0] is RegOperand a && address.Contains(a.Reg) || i.Operands[0] is SlotOperand s && s.Slot == slot)
                    { address.Add(i.Dest); grew = true; }
                }
        }
        string prefix = enumerator ?? "m_" + kind + "$Enumerator$";
        foreach (Block b in f.Blocks)
            foreach (Instr i in b.Instrs)
            {
                if (ignore?.Contains(i) == true) continue;
                int at = -1, count = 0, addresses = 0;
                for (int k = 0; k < i.Operands.Count; k++)
                {
                    if (i.Operands[k] is RegOperand r && walker.Contains(r.Reg)) { at = k; count++; }
                    if (i.Operands[k] is RegOperand a && address.Contains(a.Reg) || i.Operands[k] is SlotOperand s && s.Slot == slot) { addresses++; if (!ReferenceEquals(i, get) || k != 1) at = -2; }
                }
                if (count == 0 && addresses == 0) continue;
                if (i.Op is Opcode.Copy or Opcode.Trunc64 or Opcode.ZExt32 && i.Dest is not null && (walker.Contains(i.Dest) || address.Contains(i.Dest))) continue;
                if (ReferenceEquals(i, get) && count == 0 && addresses == 1 && at == -1) continue;
                if (i.Op != Opcode.Call || i.Callee is not { } callee || count != 1 || addresses != 0) return null;
                if (callee == Corsac.Lang.X86.MachineIntrinsics.KeepAlive || Escape.IsCollectorNote(callee)) continue;
                if (at == 0 && i.Operands.Count == 2 && enumerator is null && kind == "Dictionary" && callee.StartsWith(prefix, StringComparison.Ordinal)
                    && callee.EndsWith("_get_Current_0", StringComparison.Ordinal))
                {
                    if (!Pair(f, defs, b, i, calls, ignore)) return null;
                    continue;
                }
                if (at != 0 || i.Operands.Count != 1 || !callee.StartsWith(prefix, StringComparison.Ordinal)) return null;
                if (callee.EndsWith("_MoveNext_0", StringComparison.Ordinal) || callee.EndsWith("_Dispose_0", StringComparison.Ordinal)) calls?.Add((b, i, new(true)));
                else if (callee.EndsWith("_get_Current_0", StringComparison.Ordinal)) calls?.Add((b, i, new(true, Reads: reads)));
                else return null;
            }
        return walker;
    }

    /// <summary>
    /// A DICTIONARY'S PAIR: its enumerator's Current writes a KeyValuePair
    /// into a frame slot of its own and answers the slot's address, and the
    /// loop's `kv` is a block that pair is copied into, read by Key and Value.
    /// Followed as the enumerator is: the slot's address handed only to
    /// Current, the answer only copied into such blocks, each block made here
    /// and given only to the pair's Key and Value -- Value answering an
    /// element, as the indexer does, and both kept from the inliner, since
    /// inlined they read the block unseen.
    /// </summary>
    private static bool Pair(Function f, Defs defs, Block at, Instr current, List<(Block B, Instr Call, Role Role)>? calls, IReadOnlySet<Instr>? ignore)
    {
        if (current.Operands[1] is not RegOperand given) return false;
        FrameSlot? slot = null;
        Operand o = given;
        for (int hops = 0; hops < 4 && slot is null; hops++)
        {
            if (o is SlotOperand s) { slot = s.Slot; break; }
            if (o is not RegOperand r || !defs.IsSingle(r.Reg) || defs.Definition(r.Reg) is not { Op: Opcode.Copy } d) return false;
            o = d.Operands[0];
        }
        if (slot is null) return false;
        // The slot's address, the answer, and the blocks the pair is copied into.
        HashSet<VReg> address = new(), answer = new(), pair = new();
        if (current.Dest is not null) answer.Add(current.Dest);
        HashSet<Instr> made = new(ReferenceEqualityComparer.Instance);
        HashSet<FrameSlot> frames = new();
        bool grew = true;
        while (grew)
        {
            grew = false;
            foreach (Block b in f.Blocks)
                foreach (Instr i in b.Instrs)
                {
                    if (i.Op == Opcode.MemCopy && i.Operands.Count == 3 && i.Operands[1] is RegOperand from && answer.Contains(from.Reg)
                        && i.Operands[0] is RegOperand to && !pair.Contains(to.Reg))
                    {
                        // Into a block made here, through copies written once --
                        // or the frame slot the lifetime pass has put it in.
                        VReg r = to.Reg;
                        for (int hops = 0; hops < 4 && defs.IsSingle(r) && defs.Definition(r) is { Op: Opcode.Copy or Opcode.Trunc64 or Opcode.ZExt32 } c && c.Operands[0] is RegOperand back; hops++)
                            r = back.Reg;
                        if (!defs.IsSingle(r) || defs.Definition(r) is not { } alloc) return false;
                        if (alloc is { Op: Opcode.Copy, Operands: [SlotOperand framed] }) frames.Add(framed.Slot);
                        else if (alloc.Op != Opcode.Call || !Escape.IsAllocator(alloc.Callee)) return false;
                        else made.Add(alloc);
                        if (pair.Add(r)) grew = true;
                        continue;
                    }
                    if (i.Dest is null || answer.Contains(i.Dest) || address.Contains(i.Dest) || pair.Contains(i.Dest) || !defs.IsSingle(i.Dest)
                        || i.Op is not (Opcode.Copy or Opcode.Trunc64 or Opcode.ZExt32)) continue;
                    if (i.Operands[0] is RegOperand r1 && answer.Contains(r1.Reg)) { answer.Add(i.Dest); grew = true; }
                    else if (i.Operands[0] is RegOperand r2 && pair.Contains(r2.Reg) || i.Operands[0] is SlotOperand p && frames.Contains(p.Slot))
                    { pair.Add(i.Dest); grew = true; }
                    else if (i.Operands[0] is RegOperand r3 && address.Contains(r3.Reg) || i.Operands[0] is SlotOperand s && s.Slot == slot)
                    { address.Add(i.Dest); grew = true; }
                }
        }
        foreach (Block b in f.Blocks)
            foreach (Instr i in b.Instrs)
            {
                if (made.Contains(i) || ignore?.Contains(i) == true) continue;
                int answers = 0, addresses = 0, pairs = 0, pairAt = -1;
                for (int k = 0; k < i.Operands.Count; k++)
                {
                    if (i.Operands[k] is SlotOperand s && s.Slot == slot) addresses++;
                    if (i.Operands[k] is SlotOperand p && frames.Contains(p.Slot)) { pairs++; pairAt = k; }
                    if (i.Operands[k] is not RegOperand r) continue;
                    if (answer.Contains(r.Reg)) answers++;
                    if (address.Contains(r.Reg)) addresses++;
                    if (pair.Contains(r.Reg)) { pairs++; pairAt = k; }
                }
                if (answers + addresses + pairs == 0) continue;
                if (i.Op is Opcode.Copy or Opcode.Trunc64 or Opcode.ZExt32 && i.Dest is not null
                    && (answer.Contains(i.Dest) || address.Contains(i.Dest) || pair.Contains(i.Dest))) continue;
                if (ReferenceEquals(i, current) && addresses == 1 && answers + pairs == 0) continue;
                if (i.Op == Opcode.MemCopy && answers == 1 && pairs == 1 && pairAt == 0 && addresses == 0) continue;
                // The frame slot's block zeroed as it is made.
                if (i.Op == Opcode.MemSet && answers + addresses == 0 && pairs == 1 && pairAt == 0 && frames.Count > 0) continue;
                if (i.Op != Opcode.Call || i.Callee is not { } callee || answers + addresses != 0 || pairs != 1) return false;
                if (callee == Corsac.Lang.X86.MachineIntrinsics.KeepAlive || Escape.IsCollectorNote(callee)) continue;
                if (pairAt != 0 || i.Operands.Count != 1 || !callee.StartsWith("m_KeyValuePair$", StringComparison.Ordinal)) return false;
                if (callee.EndsWith("_get_Value_0", StringComparison.Ordinal)) calls?.Add((b, i, new(true, Reads: true)));
                else if (callee.EndsWith("_get_Key_0", StringComparison.Ordinal)) calls?.Add((b, i, new(true)));
                else return false;
            }
        calls?.Add((at, current, new(true)));
        return true;
    }
}

/// <summary>
/// Before inlining: the candidates, and their calls kept from the inliner
/// (OwnedElements). A function that hands its collection back makes the
/// calls of it candidates too, in callers that use what they are given only
/// as this rule follows: `var toks = Lex(text); foreach (var t in toks)`.
/// </summary>
public sealed class MarkOwnedElements : IModulePass
{
    public string Name => "mark-owned-elements";

    public void Run(Module m)
    {
        // What each function handing back a candidate hands back.
        Dictionary<string, string> handsBack = new(StringComparer.Ordinal);
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
                    if (OwnedElements.Uses(f, defs, kind, container, i, out bool returned) is not { } calls) { OwnedElements.Say(f, $"{kind} at {i.Line}: a use not followed"); continue; }
                    if (!returned && !calls.Any(c => c.Role.Adds >= 0)) continue;
                    OwnedElements.Say(f, $"{kind} at {i.Line}: candidate{(returned ? ", handed back" : "")}");
                    i.Field = Instr.OwnsCandidate;
                    foreach (var c in calls) m.KeepCalls.Add(c.Call);
                    if (returned) handsBack[f.Name] = kind;
                }
        }
        // The callers, until no more hand back what they were handed.
        bool grew = handsBack.Count > 0;
        for (int round = 0; grew && round < 8; round++)
        {
            grew = false;
            foreach (Function f in m.Functions)
            {
                if (f.Async is not null) continue;
                Defs? defs = null;
                foreach (Block b in f.Blocks)
                    foreach (Instr i in b.Instrs)
                    {
                        if (i.Op != Opcode.Call || i.Callee is null || i.Dest is null || i.Field is not null
                            || !handsBack.TryGetValue(i.Callee, out string? kind)) continue;
                        defs ??= new Defs(f, buildCfg: false);
                        HashSet<VReg> container = OwnedElements.Container(f, defs, i);
                        if (OwnedElements.Uses(f, defs, kind, container, i, out bool returned) is not { } calls) { OwnedElements.Say(f, $"{kind} from {i.Callee}: a use not followed"); continue; }
                        OwnedElements.Say(f, $"{kind} from {i.Callee}: candidate{(returned ? ", handed back" : "")}");
                        i.Field = Instr.OwnsCandidate;
                        foreach (var c in calls) m.KeepCalls.Add(c.Call);
                        if (returned && handsBack.TryAdd(f.Name, kind)) grew = true;
                    }
            }
        }
    }
}

public sealed partial class Escape
{
    /// <summary>How many collections were proved to own their elements.</summary>
    public int ElementsOwned { get; private set; }

    /// <summary>
    /// Every candidate judged with every summary known (OwnedElements):
    /// first the collections made, then the calls of functions proved to hand
    /// back one whose elements it owns, callees before callers.
    /// </summary>
    private void ConfirmOwnedElements(Module m, Dictionary<string, bool[]> summaries)
    {
        summaries = WithLinkEscapes(summaries, m.LinkEscapes);
        Dictionary<string, string> handedBack = new(StringComparer.Ordinal);
        foreach (Function f in m.Functions) ConfirmOwnedElements(f, summaries, handedBack, calls: false);
        for (int round = 0; round < 8; round++)
        {
            int before = handedBack.Count;
            foreach (Function f in m.Functions) ConfirmOwnedElements(f, summaries, handedBack, calls: true);
            if (handedBack.Count == before) break;
        }
        // A call whose callee was never proved: nothing it was given is known.
        foreach (Function f in m.Functions)
            foreach (Block b in f.Blocks)
                foreach (Instr i in b.Instrs)
                    if (i.Op == Opcode.Call && i.Field == Instr.OwnsCandidate) i.Field = null;
    }

    /// <summary>
    /// AT THE LINK, the whole program's answers beside the unit's own: a
    /// parameter stays put if either says it does. The unit's answer for a
    /// function another unit defines is "escapes", whatever it does; the
    /// link's is that function's, solved over every unit, as the lifetime
    /// pass the link runs after this one reads it (RunAtLink). Both hold of
    /// the function whatever was inlined into it, so either is enough.
    /// </summary>
    private static Dictionary<string, bool[]> WithLinkEscapes(Dictionary<string, bool[]> summaries, Dictionary<string, bool[]>? link)
    {
        if (link is null || link.Count == 0) return summaries;
        Dictionary<string, bool[]> both = new(summaries, StringComparer.Ordinal);
        foreach ((string name, bool[] escapes) in link)
        {
            if (!both.TryGetValue(name, out bool[]? own)) { both[name] = escapes; continue; }
            if (own.Length != escapes.Length) continue;
            bool[] joined = new bool[own.Length];
            for (int p = 0; p < own.Length; p++) joined[p] = own[p] && escapes[p];
            both[name] = joined;
        }
        return both;
    }

    /// <summary>
    /// The candidates of one function: proved ones marked OwnsElements, with a
    /// KeepAlive of the collection after every use of what it holds; the rest
    /// unmarked. One handed back is not freed here: the function is noted as
    /// handing back a collection that owns its elements, and the call in its
    /// caller is the candidate there -- the collection arrives holding objects
    /// nothing else holds, as if the caller had made and filled it.
    /// </summary>
    private void ConfirmOwnedElements(Function f, Dictionary<string, bool[]> summaries, Dictionary<string, string> handedBack, bool calls)
    {
        List<Instr> candidates = new();
        foreach (Block b in f.Blocks)
            foreach (Instr i in b.Instrs)
                if (i.Op == Opcode.Call && i.Field == Instr.OwnsCandidate
                    && (calls ? i.Callee is not null && handedBack.ContainsKey(i.Callee) : IsAllocator(i.Callee)))
                    candidates.Add(i);
        if (candidates.Count == 0) return;
        foreach (Instr made in candidates)
        {
            made.Field = null;
            if (f.Async is not null || made.Dest is null) continue;
            Defs defs = new(f);
            HashSet<VReg> container = OwnedElements.Container(f, defs, made);
            if ((calls ? handedBack[made.Callee!] : OwnedElements.KindOf(f, made, container)) is not { } kind) continue;
            if (OwnedElements.Uses(f, defs, kind, container, made, out bool returned) is not { } uses) continue;
            if (ProveOwnedElements(f, defs, made, container, uses, summaries, filled: calls || returned) is not { } keepAlive)
            {
                OwnedElements.Say(f, $"at {made.Line}: not proved ({_elementWhy})");
                // Its calls the late inliner's again: nothing here needs them kept.
                foreach (var use in uses) _module?.KeepCalls.Remove(use.Call);
                continue;
            }
            // The collection kept alive past every use of what it holds.
            VReg holder = made.Dest;
            foreach ((Block b, Instr after) in keepAlive)
            {
                int at = b.Instrs.IndexOf(after);
                if (at < 0) continue;
                Instr keep = new() { Op = Opcode.Call, Callee = Corsac.Lang.X86.MachineIntrinsics.KeepAlive, Operands = { new RegOperand(holder) }, Line = after.Line };
                if (ReferenceEquals(after, b.Terminator)) b.Instrs.Insert(at, keep);
                else b.Instrs.Insert(at + 1, keep);
            }
            if (returned)
            {
                OwnedElements.Say(f, $"at {made.Line}: HANDS BACK OWNED ELEMENTS");
                handedBack.TryAdd(f.Name, kind);
                continue;
            }
            OwnedElements.Say(f, $"at {made.Line}: OWNS ELEMENTS");
            made.Field = Instr.OwnsElements;
            ElementsOwned++;
        }
    }

    private string _elementWhy = "";

    private List<(Block B, Instr After)>? ProveOwnedElements(Function f, Defs defs, Instr alloc, HashSet<VReg> container,
        List<(Block B, Instr Call, OwnedElements.Role Role)> calls, Dictionary<string, bool[]> summaries, bool filled = false)
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
        // Nothing added here is nothing gained -- unless it arrived filled, or
        // is handed back to be.
        if (adderOf.Count == 0 && !filled) { _elementWhy = "rule 7"; return null; }
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
        // And every move of an enumerator over it, which reads its storage
        // through the struct rather than the collection's registers.
        foreach ((Block b, Instr call, OwnedElements.Role _) in calls)
        {
            if (call.Operands.Count == 0 || call.Operands[0] is RegOperand r && container.Contains(r.Reg)) continue;
            bool dominated = ReferenceEquals(b, allocBlock) ? b.Instrs.IndexOf(alloc) < b.Instrs.IndexOf(call) : cfg.Dominates(allocBlock, b);
            if (!dominated) { _elementWhy = "rule 12"; return null; }
            if (!keep.Any(k => ReferenceEquals(k.After, call))) keep.Add((b, call));
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
        if (value is not RegOperand start) return null;
        // Copies written once, back to the join they read.
        VReg r = start.Reg;
        for (int hops = 0; hops < 8 && defs.IsSingle(r); hops++)
        {
            if (defs.Definition(r) is not { Op: Opcode.Copy or Opcode.Trunc64 or Opcode.ZExt32 } d || d.Operands[0] is not RegOperand from) return null;
            r = from.Reg;
        }
        if (defs.IsSingle(r)) return null;
        Dictionary<VReg, List<Instr>> writes = new();
        foreach (Block b in f.Blocks)
            foreach (Instr i in b.Instrs)
                if (i.Dest is { } d && !defs.IsSingle(d))
                {
                    if (!writes.TryGetValue(d, out List<Instr>? list)) writes[d] = list = new();
                    list.Add(i);
                }
        List<Instr> origins = new();
        // Through joins of joins as well -- a conditional's own result, then
        // the variable it is assigned to (`m = c ? new(x) : new()`).
        bool Collect(VReg join, int depth)
        {
            if (!joins.Add(join)) return true;
            if (depth > 4 || !writes.TryGetValue(join, out List<Instr>? list)) return false;
            foreach (Instr i in list)
            {
                if (i.Op != Opcode.Copy || i.Operands[0] is not RegOperand from) return false;
                Operand o = new RegOperand(from.Reg);
                Instr? made = null;
                for (int hops = 0; hops < 8 && made is null; hops++)
                {
                    if (o is not RegOperand q) return false;
                    if (!defs.IsSingle(q.Reg))
                    {
                        if (!Collect(q.Reg, depth + 1)) return false;
                        break;
                    }
                    if (defs.Definition(q.Reg) is not { } d) return false;
                    if (d.Op == Opcode.Call && IsAllocator(d.Callee)) made = d;
                    else if (d.Op is Opcode.Copy or Opcode.Trunc64 or Opcode.ZExt32) o = d.Operands[0];
                    else return false;
                }
                if (made is not null) origins.Add(made);
            }
            return true;
        }
        if (!Collect(r, 0)) return null;
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
