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

    /// <summary>The runtime's mark on a collection whose elements go with its storage (Runtime.OwnElements).</summary>
    public const string Marker = "m_Runtime_OwnElements_1_V$Any";

    /// <summary>
    /// WHERE A COLLECTION IS HANDED TO A FIELD: a store of it into an
    /// object's field, or a call whose parameter is stored into one and put
    /// to no other use (a constructor's `_t = tokens`, Stores answering which
    /// field for a callee and an operand). One at most: Uses records it here,
    /// and refuses either use when it is not given one.
    /// </summary>
    internal sealed class HandOff
    {
        public Func<string, int, string?>? Stores;
        public Instr? At;
        public string? Field;
        public int Operand;
    }

    /// <summary>
    /// The parameters a function stores into a field and puts to no other
    /// use, by index, with the field: `Parser(List&lt;Token&gt; tokens) { _t = tokens; }`.
    /// Or hands, once and to nothing else, to a call that does (`through`,
    /// answering for a callee and an operand): a constructor that wraps the
    /// list -- `Parser(List&lt;Token&gt; t) : this(new ParserTokens(t))` --
    /// stores it in the wrapper's field.
    /// </summary>
    internal static Dictionary<int, string> StoredParameters(Function g, Func<string, int, string?>? through = null)
    {
        Dictionary<int, string> stored = new();
        if (g.Async is not null || g.Params.Count < 2) return stored;
        Defs defs = new(g, buildCfg: false);
        for (int p = 1; p < g.Params.Count; p++)
        {
            HashSet<VReg> regs = Container(g, defs, g.Params[p]);
            string? field = null;
            bool ok = true;
            foreach (Block b in g.Blocks)
            {
                foreach (Instr i in b.Instrs)
                {
                    int count = 0;
                    foreach (Operand o in i.Operands) if (o is RegOperand r && regs.Contains(r.Reg)) count++;
                    if (count == 0) continue;
                    if (i.Op is Opcode.Copy or Opcode.Trunc64 or Opcode.ZExt32 && i.Dest is not null && regs.Contains(i.Dest)) continue;
                    // The store's note to the collector, which keeps nothing.
                    if (i.Op == Opcode.Call && Escape.IsCollectorNote(i.Callee)) continue;
                    if (i.Op == Opcode.Store && count == 1 && field is null && i.Field is not null && i.Operands.Count >= 2
                        && i.Operands[0] is RegOperand && i.Operands[1] is RegOperand v && regs.Contains(v.Reg))
                    { field = i.Field; continue; }
                    if (through is not null && i.Op == Opcode.Call && count == 1 && field is null && i.Callee is { } callee)
                    {
                        int at = i.Operands.FindIndex(o => o is RegOperand r && regs.Contains(r.Reg));
                        if (at > 0 && through(callee, at) is { } passed) { field = passed; continue; }
                    }
                    Say(g, $"parameter {p}: not only stored: {i}");
                    ok = false;
                    break;
                }
                if (!ok) break;
            }
            if (ok && field is not null) stored[p] = field;
        }
        return stored;
    }

    /// <summary>
    /// A UNIT'S PARAMETERS STORED INTO A FIELD (StoredParameters): for the
    /// link, those of the functions another unit can call; each function
    /// looked at only when it stores one of its parameters into a field at all.
    /// </summary>
    internal static IEnumerable<(string Function, int Argument, string Field)> StoredParametersOf(Module m, bool exportedOnly = true)
    {
        foreach (Function g in m.Functions)
        {
            if (exportedOnly && !g.Exported || g.Async is not null || g.Params.Count < 2) continue;
            HashSet<VReg> parameters = new(g.Params.Skip(1));
            // And their copies, one step on: lowering may give a parameter a
            // register of its own before it is stored.
            foreach (Block b in g.Blocks)
                foreach (Instr i in b.Instrs)
                    if (i.Op == Opcode.Copy && i.Dest is not null && i.Operands.Count == 1 && i.Operands[0] is RegOperand from && g.Params.IndexOf(from.Reg) > 0)
                        parameters.Add(i.Dest);
            bool any = false;
            foreach (Block b in g.Blocks)
            {
                foreach (Instr i in b.Instrs)
                    if (i.Op == Opcode.Store && i.Field is not null && i.Operands.Count >= 2 && i.Operands[1] is RegOperand v && parameters.Contains(v.Reg))
                    { any = true; break; }
                if (any) break;
            }
            if (!any) continue;
            foreach ((int p, string field) in StoredParameters(g)) yield return (g.Name, p, field);
        }
    }

    /// <summary>
    /// A HAND-OFF TO ANOTHER UNIT'S FUNCTION, in a unit's compile: what
    /// field its parameter goes to is that unit's to say, and the link's to
    /// match (Lto.OwnedFieldSolver). Named here as a field no instruction
    /// has, read nowhere.
    /// </summary>
    internal static string CallField(string callee, int argument) => "\u0002" + argument.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + callee;

    /// <summary>The callee and argument a CallField names, or null for a field.</summary>
    internal static (string Callee, int Argument)? CalleeOf(string field)
    {
        if (field.Length < 3 || field[0] != '\u0002') return null;
        int colon = field.IndexOf(':');
        return colon > 1 && int.TryParse(field.AsSpan(1, colon - 1), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int argument)
            ? (field[(colon + 1)..], argument) : null;
    }

    /// <summary>
    /// Which of `fields` some read hands straight to a List's or a
    /// Dictionary's method, and so which kind; one pass over the module,
    /// before the far dearer FieldReads is asked of any of them. A field read
    /// both ways is neither.
    /// </summary>
    internal static Dictionary<string, string> CollectionFields(Module m, IReadOnlySet<string> fields)
    {
        Dictionary<string, string?> kinds = new(StringComparer.Ordinal);
        foreach (Function g in m.Functions)
        {
            Dictionary<VReg, string>? loaded = null;
            foreach (Block b in g.Blocks)
                foreach (Instr i in b.Instrs)
                {
                    if (i.Op == Opcode.Load && i.Field is { } field && i.Dest is not null && fields.Contains(field)) (loaded ??= new())[i.Dest] = field;
                    else if (loaded is not null && i.Op == Opcode.Call && i.Callee is { } callee && i.Operands.Count > 0 && i.Operands[0] is RegOperand r
                        && loaded.TryGetValue(r.Reg, out string? read))
                    {
                        string? kind = RoleOf("List", callee).Known ? "List" : RoleOf("Dictionary", callee).Known ? "Dictionary" : null;
                        if (kind is null) continue;
                        kinds[read] = kinds.TryGetValue(read, out string? known) && known != kind ? null : kind;
                    }
                }
        }
        Dictionary<string, string> found = new(StringComparer.Ordinal);
        foreach ((string field, string? kind) in kinds) if (kind is not null) found[field] = kind;
        return found;
    }

    /// <summary>
    /// EVERY READ OF A FIELD in the program, each a load whose value goes
    /// only to the collection's known methods (Uses) -- never handed back,
    /// stored or passed on -- with those calls; null when one is not, or the
    /// field's address is taken. What is stored into the field is the
    /// owned-field rules' to judge (Escape.OwnedFields).
    /// </summary>
    internal static List<(Function F, Instr Load, List<(Block B, Instr Call, Role Role)> Calls)>? FieldReads(Module m, string field, string kind)
    {
        List<(Function, Instr, List<(Block, Instr, Role)>)> reads = new();
        foreach (Function g in m.Functions)
        {
            Defs? defs = null;
            foreach (Block b in g.Blocks)
                foreach (Instr i in b.Instrs)
                {
                    if (i.Field != field || i.Op == Opcode.Store) continue;
                    if (i.Op != Opcode.Load || i.Dest is null || g.Async is not null || i.Operands.Count < 1 || i.Operands[0] is not RegOperand)
                    { Say(g, $"field {field}: not followed: {i}"); return null; }
                    defs ??= new Defs(g, buildCfg: false);
                    HashSet<VReg> container = Container(g, defs, i);
                    if (Uses(g, defs, kind, container, i, out bool returned) is not { } calls || returned)
                    { Say(g, $"field {field}: a read not followed: {i}"); return null; }
                    reads.Add((g, i, calls));
                }
        }
        return reads;
    }

    // ---- reached through accessors ------------------------------------------------------
    //
    // THE COMPILER'S OWN PARSER keeps its tokens one step further away: the
    // list is a field of a wrapper (ParserTokens.mutable) that is a field of
    // the parser (Parser._t), read by an indexer that hands an element back,
    // through Cur and Ahead that hand it back again, and written through a
    // getter that fills the field when it is empty (`mutable ??= new(...)`).
    // The values below are what a function holds of the field's collection:
    // its reads, the calls of a getter that hands the field's value back, and
    // the variables of a `??=` that hold one of those or a list made there.

    /// <summary>What a function holds of a field's collection (FieldValues).</summary>
    internal sealed class FieldValueSet
    {
        /// <summary>Every register holding the field's value, or a list a `??=` made to fill it.</summary>
        public readonly HashSet<VReg> Values = new();
        /// <summary>The reads of the field, and the calls of getters handing it back.</summary>
        public readonly List<Instr> Seeds = new();
        /// <summary>The stores into the field of one of those values: a `??=` putting back what it read, or what it made.</summary>
        public readonly HashSet<Instr> Stores = new(ReferenceEqualityComparer.Instance);
    }

    /// <summary>A register that holds an object made here: an allocation, through copies written once.</summary>
    internal static bool FreshHere(Defs defs, VReg r)
    {
        for (int hops = 0; hops < 6; hops++)
        {
            if (!defs.IsSingle(r) || defs.Definition(r) is not { } d) return false;
            if (d.Op == Opcode.Call && Escape.IsAllocator(d.Callee)) return true;
            if (d.Op is not (Opcode.Copy or Opcode.Trunc64 or Opcode.ZExt32) || d.Operands[0] is not RegOperand from) return false;
            r = from.Reg;
        }
        return false;
    }

    /// <summary>Whether a function reads the field or calls one of its getters: what FieldValues need look at.</summary>
    internal static bool Touches(Function g, string field, IReadOnlySet<string> getters)
    {
        foreach (Block b in g.Blocks)
            foreach (Instr i in b.Instrs)
                if (i.Field == field || i.Op == Opcode.Call && i.Callee is { } c && getters.Contains(c)) return true;
        return false;
    }

    /// <summary>
    /// The registers of a function that hold a field's collection: each read
    /// of it, each call of a getter in `getters`, copies of those -- and a
    /// variable written more than once when every write is one of them, null,
    /// another such variable, or a list made here (the `nc` of `x ??= new()`).
    /// Null when the field is used any other way (its address taken).
    /// </summary>
    internal static FieldValueSet? FieldValues(Function g, Defs defs, string field, IReadOnlySet<string> getters)
    {
        FieldValueSet set = new();
        foreach (Block b in g.Blocks)
            foreach (Instr i in b.Instrs)
            {
                if (i.Field == field)
                {
                    if (i.Op == Opcode.Store) continue;
                    if (i.Op != Opcode.Load || i.Dest is null || i.Operands.Count < 1 || i.Operands[0] is not RegOperand) return null;
                    set.Seeds.Add(i);
                    set.Values.Add(i.Dest);
                }
                else if (i.Op == Opcode.Call && i.Callee is { } c && i.Dest is not null && getters.Contains(c))
                {
                    set.Seeds.Add(i);
                    set.Values.Add(i.Dest);
                }
            }
        if (set.Seeds.Count == 0) return set;
        RegisterWrites writes = new(g);
        for (bool grew = true; grew;)
        {
            grew = false;
            foreach (Block b in g.Blocks)
                foreach (Instr i in b.Instrs)
                    if (i.Dest is not null && !set.Values.Contains(i.Dest) && defs.IsSingle(i.Dest) && i.Op is Opcode.Copy or Opcode.Trunc64 or Opcode.ZExt32
                        && i.Operands[0] is RegOperand r && set.Values.Contains(r.Reg))
                    { set.Values.Add(i.Dest); grew = true; }
            // The variables: a group of them, each written only with a value,
            // null, a list made here or another of the group.
            HashSet<VReg> group = new();
            foreach (Block b in g.Blocks)
                foreach (Instr i in b.Instrs)
                    if (i.Dest is not null && !set.Values.Contains(i.Dest) && !defs.IsSingle(i.Dest) && i.Op == Opcode.Copy
                        && i.Operands[0] is RegOperand r && set.Values.Contains(r.Reg) && !g.Params.Contains(i.Dest))
                        group.Add(i.Dest);
            for (bool shrank = true; shrank && group.Count > 0;)
            {
                shrank = false;
                foreach (VReg d in group.ToList())
                {
                    bool closed = writes.TryGetValue(d, out WriteList all) && all.All(w => w.Op == Opcode.Copy && w.Operands.Count == 1
                        && (w.Operands[0] is ImmOperand { Value: 0 }
                            || w.Operands[0] is RegOperand { Reg: var from } && (set.Values.Contains(from) || group.Contains(from) || FreshHere(defs, from))));
                    if (!closed) { group.Remove(d); shrank = true; }
                }
            }
            foreach (VReg d in group) if (set.Values.Add(d)) grew = true;
        }
        foreach (Block b in g.Blocks)
            foreach (Instr i in b.Instrs)
                if (i.Op == Opcode.Store && i.Field == field && i.Operands.Count >= 2 && i.Operands[1] is RegOperand v && set.Values.Contains(v.Reg))
                    set.Stores.Add(i);
        return set;
    }

    /// <summary>Functions whose address is taken -- named by code or by a descriptor -- and may be called where no call shows.</summary>
    internal static HashSet<string> AddressTaken(Module m)
    {
        HashSet<string> addressed = new(StringComparer.Ordinal);
        foreach (DataItem d in m.Data) foreach (DataReloc r in d.Relocs) addressed.Add(r.Symbol);
        foreach (Function f in m.Functions) foreach (Block b in f.Blocks) foreach (Instr i in b.Instrs)
            foreach (Operand o in i.Operands) if (o is SymOperand sym) addressed.Add(sym.Name);
        return addressed;
    }

    /// <summary>
    /// THE GETTERS OF A FIELD'S COLLECTION: functions every return of which
    /// hands back the field's value (FieldValues) -- `Writable() =&gt; mutable
    /// ??= new List&lt;Token&gt;(snapshot)` -- called only where a call shows.
    /// A call of one is a read of the field.
    /// </summary>
    internal static HashSet<string> CollectionGetters(Module m, string field, IReadOnlySet<string> addressed)
    {
        HashSet<string> getters = new(StringComparer.Ordinal);
        for (int round = 0; round < 6; round++)
        {
            int before = getters.Count;
            foreach (Function g in m.Functions)
            {
                if (g.Async is not null || getters.Contains(g.Name) || addressed.Contains(g.Name) || g.Name == m.Entry || !Touches(g, field, getters)) continue;
                Defs defs = new(g, buildCfg: false);
                if (FieldValues(g, defs, field, getters) is not { Seeds.Count: > 0 } values) continue;
                bool any = false, all = true;
                foreach (Block b in g.Blocks)
                    if (b.Terminator is { Op: Opcode.Ret } ret)
                    {
                        any = true;
                        if (ret.Operands.Count != 1 || ret.Operands[0] is not RegOperand back || !values.Values.Contains(back.Reg)) all = false;
                    }
                if (any && all) getters.Add(g.Name);
            }
            if (getters.Count == before) break;
        }
        return getters;
    }

    /// <summary>
    /// Before inlining, every use of a field's collection, as FieldReads, but
    /// through its getters and `??=` too: each function holding one, with the
    /// collection's calls there. Null when one is not followed. Whether what
    /// those calls answer goes anywhere is judged after inlining.
    /// </summary>
    internal static List<(Function F, List<(Block B, Instr Call, Role Role)> Calls)>? FieldUses(Module m, string field, string kind, IReadOnlySet<string> addressed)
    {
        HashSet<string> getters = CollectionGetters(m, field, addressed);
        List<(Function, List<(Block, Instr, Role)>)> uses = new();
        foreach (Function g in m.Functions)
        {
            if (!Touches(g, field, getters)) continue;
            Defs defs = new(g, buildCfg: false);
            if (FieldValues(g, defs, field, getters) is not { } values) { Say(g, $"field {field}: not followed"); return null; }
            if (values.Seeds.Count == 0) continue;
            if (g.Async is not null) { Say(g, $"field {field}: read in an async body"); return null; }
            if (Uses(g, defs, kind, values.Values, null, out bool returned, allowed: values.Stores) is not { } calls
                || returned && !getters.Contains(g.Name))
            { Say(g, $"field {field}: a read not followed"); return null; }
            uses.Add((g, calls));
        }
        return uses;
    }

    /// <summary>
    /// THE PARAMETER A VALUE IS REACHED FROM, through copies written once and
    /// reads of reference fields -- `this._t`, then its `mutable` -- with the
    /// fields read on the way: -1 when it is not (a static, a call's result,
    /// an object made here). An object reached from a parameter is no
    /// function's own: it is held by the heap, and a store frees what it
    /// replaces only in an object its function made and kept to itself
    /// (Escape.PrivateOwner).
    /// </summary>
    internal static int Root(Function g, Defs defs, Operand o, List<string>? fields = null)
    {
        for (int hops = 0; hops < 12; hops++)
        {
            if (o is not RegOperand { Reg: var r }) return -1;
            int p = g.Params.IndexOf(r);
            if (p >= 0) return defs.Count(r) == 1 ? p : -1;
            if (!defs.IsSingle(r) || defs.Definition(r) is not { } d) return -1;
            if (d.Op is Opcode.Copy or Opcode.Trunc64 or Opcode.ZExt32) { o = d.Operands[0]; continue; }
            if (d.Op == Opcode.Load && d.Field is { } field && d.Operands.Count == 1 && d.Operands[0] is RegOperand)
            {
                fields?.Add(field);
                o = d.Operands[0];
                continue;
            }
            return -1;
        }
        return -1;
    }

    /// <summary>The registers on the way to a function's returns, through copies and joins.</summary>
    internal static HashSet<VReg> Returned(Function f)
    {
        HashSet<VReg> chain = new();
        RegisterWrites writes = new(f);
        Stack<VReg> work = new();
        foreach (Block rb in f.Blocks)
            if (rb.Terminator is { Op: Opcode.Ret, Operands: [RegOperand back] }) work.Push(back.Reg);
        while (work.TryPop(out VReg? r))
        {
            if (!chain.Add(r) || !writes.TryGetValue(r, out WriteList ws)) continue;
            foreach (Instr w in ws)
                if (w.Op is Opcode.Copy or Opcode.Trunc64 or Opcode.ZExt32 or Opcode.Phi)
                    foreach (Operand o in w.Operands) if (o is RegOperand from) work.Push(from.Reg);
        }
        return chain;
    }

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
    internal static HashSet<VReg> Container(Function f, Defs defs, Instr alloc) => Container(f, defs, alloc.Dest!);

    /// <summary>The registers that hold what `root` holds, through copies of registers written once.</summary>
    internal static HashSet<VReg> Container(Function f, Defs defs, VReg root)
    {
        HashSet<VReg> set = new() { root };
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
    internal static bool BarrierOnly(Function f, VReg address)
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
    internal static List<(Block B, Instr Call, Role Role)>? Uses(Function f, Defs defs, string kind, HashSet<VReg> container, Instr? alloc, out bool returned,
        HandOff? handOff = null, IReadOnlySet<Instr>? allowed = null, List<(Block B, Instr Call, Role Role)>? into = null)
    {
        // The calls found are gathered in `into` when given, a refusal
        // included: what was kept of them can be let go (ReleaseFieldReads).
        List<(Block, Instr, Role)> calls = into ?? new();
        returned = false;
        foreach (Block b in f.Blocks)
            foreach (Instr i in b.Instrs)
            {
                if (ReferenceEquals(i, alloc) || allowed?.Contains(i) == true) continue;
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
                    // HANDED TO A FIELD, once (HandOff): stored into an
                    // object's field, or passed to a function that does that
                    // and nothing else with it.
                    case Opcode.Store when handOff is not null && handOff.At is null && at == 1 && count == 1 && i.Field is not null
                        && i.Operands[0] is RegOperand:
                        handOff.At = i; handOff.Field = i.Field; handOff.Operand = 1;
                        continue;
                    // The store's note to the collector, which keeps nothing:
                    // the store itself is what is judged.
                    case Opcode.Call when (handOff is not null || allowed is not null) && at > 0 && Escape.IsCollectorNote(i.Callee):
                        continue;
                    case Opcode.Call when handOff?.Stores is not null && handOff.At is null && at > 0 && count == 1 && i.Callee is not null
                        && handOff.Stores(i.Callee, at) is { } stored:
                        handOff.At = i; handOff.Field = stored; handOff.Operand = at;
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
        // THROUGH A FIELD in a whole program, where every read of the field
        // is in sight (OwnedElements.FieldReads) -- and in a unit the link
        // will finish, which follows the reads it has and leaves the rest of
        // the program to the link: a collection handed to another unit's
        // function goes to whatever field that unit says it stores it into
        // (CallField).
        bool whole = !m.PreserveExports && m.Entry is not null;
        bool unit = m.PreserveExports && m.LeavesLinkHints && !m.AtLink;
        Func<string, int, string?>? stores = null;
        bool inner = false;
        Dictionary<string, List<(Function F, List<(Block B, Instr Call, OwnedElements.Role Role)> Calls)>?> readsOf = new(StringComparer.Ordinal);
        HashSet<string>? addressed = null;
        if (whole || unit)
        {
            Dictionary<string, Function> byName = new(StringComparer.Ordinal);
            foreach (Function g in m.Functions) byName[g.Name] = g;
            Dictionary<string, Dictionary<int, string>> stored = new(StringComparer.Ordinal);
            stores = (callee, at) =>
            {
                // Another unit's function, in a unit: the field its parameter
                // goes to is that unit's to say (CallField). Only at the
                // hand-off itself: a parameter passed on to another unit's
                // function is stored by nothing this unit can name.
                if (!byName.TryGetValue(callee, out Function? g)) return unit && !inner ? OwnedElements.CallField(callee, at) : null;
                if (!stored.TryGetValue(callee, out Dictionary<int, string>? map))
                {
                    // Empty while it is judged: a cycle of calls stores nothing.
                    stored[callee] = new();
                    bool outer = inner;
                    inner = true;
                    stored[callee] = map = OwnedElements.StoredParameters(g, stores);
                    inner = outer;
                }
                return map.GetValueOrDefault(at);
            };
        }
        // A candidate handed to a field: every read of the field followed,
        // and its calls kept too.
        bool ThroughField(Function f, OwnedElements.HandOff? handOff, string kind, bool returned)
        {
            if (handOff?.Field is not { } field) return true;
            if (returned) return false;
            if (OwnedElements.CalleeOf(field) is not null) return true;
            addressed ??= OwnedElements.AddressTaken(m);
            if (!readsOf.TryGetValue(field, out var reads))
            {
                // In a unit, plain reads alone: what the field's accessors do
                // in other units is no hint's to say (FieldElementsProved).
                if (unit) readsOf[field] = reads = OwnedElements.FieldReads(m, field, kind)?.Select(r => (r.F, r.Calls)).ToList();
                else
                {
                    readsOf[field] = reads = OwnedElements.FieldUses(m, field, kind, addressed);
                    if (reads is not null && kind == "List") KeepStashes(m, field, reads, addressed);
                }
            }
            if (reads is null) return false;
            foreach (var read in reads) foreach (var c in read.Calls) m.KeepCalls.Add(c.Call);
            if (unit) m.ElementFields.TryAdd(field, kind);
            OwnedElements.Say(f, $"handed to {field}, read {reads.Count} time(s)");
            return true;
        }
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
                    OwnedElements.HandOff? handOff = stores is null ? null : new() { Stores = stores };
                    if (OwnedElements.Uses(f, defs, kind, container, i, out bool returned, handOff) is not { } calls) { OwnedElements.Say(f, $"{kind} at {i.Line}: a use not followed"); continue; }
                    if (!returned && !calls.Any(c => c.Role.Adds >= 0)) continue;
                    if (!ThroughField(f, handOff, kind, returned)) { OwnedElements.Say(f, $"{kind} at {i.Line}: handed to a field not followed"); continue; }
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
                        OwnedElements.HandOff? handOff = stores is null ? null : new() { Stores = stores };
                        if (OwnedElements.Uses(f, defs, kind, container, i, out bool returned, handOff) is not { } calls) { OwnedElements.Say(f, $"{kind} from {i.Callee}: a use not followed"); continue; }
                        if (!ThroughField(f, handOff, kind, returned)) { OwnedElements.Say(f, $"{kind} from {i.Callee}: handed to a field not followed"); continue; }
                        OwnedElements.Say(f, $"{kind} from {i.Callee}: candidate{(returned ? ", handed back" : "")}");
                        i.Field = Instr.OwnsCandidate;
                        foreach (var c in calls) m.KeepCalls.Add(c.Call);
                        if (returned && handsBack.TryAdd(f.Name, kind)) grew = true;
                    }
            }
        }
        // IN A UNIT, the reads of every field one of its functions stores a
        // parameter into and puts to no other use (`_t = tokens`): another
        // unit may hand that function a collection to keep there, and only
        // reads still calls when the lifetime pass judges them can be proved
        // for the link. A field some read hands straight to a collection's
        // method, every read followed.
        if (!unit) return;
        HashSet<string> parameterFields = new(StringComparer.Ordinal);
        foreach ((string _, int _, string field) in OwnedElements.StoredParametersOf(m, exportedOnly: false))
            if (!readsOf.ContainsKey(field)) parameterFields.Add(field);
        if (parameterFields.Count == 0) return;
        foreach ((string field, string kind) in OwnedElements.CollectionFields(m, parameterFields).OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (OwnedElements.FieldReads(m, field, kind) is not { } reads) continue;
            foreach (var read in reads) foreach (var c in read.Calls) m.KeepCalls.Add(c.Call);
            m.ElementFields.TryAdd(field, kind);
        }
    }

    /// <summary>
    /// A STASH: another list, in a field, that what is read of the field's
    /// list is put into to be put back later -- the parser's `_splits`,
    /// keeping the token a split replaced. Its calls are kept from the
    /// inliner too, so that after inlining what goes in and comes out of it
    /// is still a call's operand and answer (Escape.StashProved). Found where
    /// the field's collection is used, or a function using it is called: a
    /// list's Add, Insert or set_Item handed one of those calls' answers, or
    /// a block made here holding one (a tuple).
    /// </summary>
    private static void KeepStashes(Module m, string field, List<(Function F, List<(Block B, Instr Call, OwnedElements.Role Role)> Calls)> reads,
        IReadOnlySet<string> addressed)
    {
        HashSet<string> touching = new(reads.Select(r => r.F.Name), StringComparer.Ordinal);
        Dictionary<Function, HashSet<Instr>> answers = new();
        foreach (var (g, calls) in reads)
        {
            if (!answers.TryGetValue(g, out var mine)) answers[g] = mine = new(ReferenceEqualityComparer.Instance);
            foreach (var c in calls) if (c.Role.Reads) mine.Add(c.Call);
        }
        HashSet<string> stashes = new(StringComparer.Ordinal);
        foreach (Function h in m.Functions)
        {
            if (h.Async is not null) continue;
            answers.TryGetValue(h, out HashSet<Instr>? read);
            // What may be an element here: a read's answer, or what a function
            // using the field hands back.
            HashSet<VReg> elements = new();
            foreach (Block b in h.Blocks)
                foreach (Instr i in b.Instrs)
                    if (i.Op == Opcode.Call && i.Dest is not null && (read?.Contains(i) == true || i.Callee is { } c && touching.Contains(c)))
                        elements.Add(i.Dest);
            if (elements.Count == 0) continue;
            Defs defs = new(h, buildCfg: false);
            for (bool grew = true; grew;)
            {
                grew = false;
                foreach (Block b in h.Blocks)
                    foreach (Instr i in b.Instrs)
                        if (i.Dest is not null && !elements.Contains(i.Dest) && i.Op is Opcode.Copy or Opcode.Trunc64 or Opcode.ZExt32
                            && i.Operands[0] is RegOperand r && elements.Contains(r.Reg))
                        { elements.Add(i.Dest); grew = true; }
            }
            // Blocks made here an element is stored into.
            HashSet<VReg> holders = new();
            foreach (Block b in h.Blocks)
                foreach (Instr i in b.Instrs)
                    if (i.Op == Opcode.Store && i.Operands.Count >= 2 && i.Operands[1] is RegOperand v && elements.Contains(v.Reg)
                        && i.Operands[0] is RegOperand at && OwnedElements.FreshHere(defs, at.Reg))
                        holders.UnionWith(OwnedElements.Container(h, defs, at.Reg));
            foreach (Block b in h.Blocks)
                foreach (Instr i in b.Instrs)
                {
                    if (i.Op != Opcode.Call || i.Callee is not { } callee || i.Operands.Count < 2 || i.Operands[0] is not RegOperand list) continue;
                    OwnedElements.Role role = OwnedElements.RoleOf("List", callee);
                    if (role.Adds < 0 || role.Adds >= i.Operands.Count || i.Operands[role.Adds] is not RegOperand value
                        || !elements.Contains(value.Reg) && !holders.Contains(value.Reg)) continue;
                    // The list a field holds, other than the field itself.
                    VReg r = list.Reg;
                    for (int hops = 0; hops < 4 && defs.IsSingle(r) && defs.Definition(r) is { Op: Opcode.Copy, Operands: [RegOperand back] }; hops++) r = back.Reg;
                    if (defs.Definition(r) is { Op: Opcode.Load, Field: { } stash } && stash != field) stashes.Add(stash);
                }
        }
        foreach (string stash in stashes)
        {
            if (OwnedElements.FieldUses(m, stash, "List", addressed) is not { } uses) continue;
            foreach (var use in uses) foreach (var c in use.Calls) m.KeepCalls.Add(c.Call);
            if (uses.Count > 0) OwnedElements.Say(uses[0].F, $"{field}: stash {stash} kept");
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
        _handedBackIf.Clear();
        foreach (Function f in m.Functions) ConfirmOwnedElements(f, summaries, handedBack, calls: false);
        for (int round = 0; round < 8; round++)
        {
            int before = handedBack.Count + _handedBackIf.Count;
            foreach (Function f in m.Functions) ConfirmOwnedElements(f, summaries, handedBack, calls: true);
            if (handedBack.Count + _handedBackIf.Count == before) break;
        }
        // A call whose callee was never proved: nothing it was given is known.
        foreach (Function f in m.Functions)
            foreach (Block b in f.Blocks)
                foreach (Instr i in b.Instrs)
                    if (i.Op == Opcode.Call && i.Field == Instr.OwnsCandidate) i.Field = null;
        FieldsInUnit(m, summaries);
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
                    && (calls ? i.Callee is not null && (handedBack.ContainsKey(i.Callee) || _handedBackIf.ContainsKey(i.Callee)) : IsAllocator(i.Callee)))
                    candidates.Add(i);
        if (candidates.Count == 0) return;
        foreach (Instr made in candidates)
        {
            made.Field = null;
            if (f.Async is not null || made.Dest is null) continue;
            Defs defs = new(f);
            HashSet<VReg> container = OwnedElements.Container(f, defs, made);
            // Handed back only if other units do what a unit's compile could
            // not see (_handedBackIf): good for a hand-off to a field alone.
            (string Kind, Corsac.Lang.Lto.LifetimeCondition Needs)? handedIf = calls && !handedBack.ContainsKey(made.Callee!) ? _handedBackIf[made.Callee!] : null;
            if ((calls ? handedIf?.Kind ?? handedBack[made.Callee!] : OwnedElements.KindOf(f, made, container)) is not { } kind) continue;
            OwnedElements.HandOff? handOff = _elementMode != ElementMode.Off ? new() { Stores = StoredParameter } : null;
            if (OwnedElements.Uses(f, defs, kind, container, made, out bool returned, handOff) is not { } uses) continue;
            bool throughField = handOff?.At is not null;
            List<HashSet<VReg>>? held = throughField ? new() : null;
            if (throughField && returned) _elementWhy = "handed to a field and back";
            // IN A UNIT'S COMPILE, a collection handed to a field or handed
            // back is judged on what the calls it meets in other units do, as
            // a condition for the link (Needs): a field's elements are the
            // link's to decide in any case, and a collection handed back on a
            // condition can go only to one.
            Needs? needs = _elementMode == ElementMode.Hints && (throughField || returned) ? new(this) : null;
            if (handedIf is { } inherited && (needs is null || !needs.Condition.Add(inherited.Needs))) { _elementWhy = "handed back on a condition"; needs = null; returned = throughField = true; }
            _elementNeeds = needs;
            List<(Block B, Instr After)>? keepAlive = null;
            List<Instr> added = new();
            bool proved = !(throughField && returned)
                && (keepAlive = ProveOwnedElements(f, defs, made, container, uses, summaries, filled: calls || returned, held)) is not null
                && (added = _lastAdded) is not null
                && !(throughField && (HeldAcross(f, made, held!) && Why("rule 13") || !FieldElementsProved(handOff!.Field!, kind, summaries)));
            _elementNeeds = null;
            if (!proved)
            {
                OwnedElements.Say(f, $"at {made.Line}: not proved ({_elementWhy})");
                // Its calls the late inliner's again: nothing here needs them kept.
                foreach (var use in uses) _module?.KeepCalls.Remove(use.Call);
                continue;
            }
            if (throughField && _elementMode == ElementMode.Hints)
            {
                // A unit's compile: the hand-off, for the link to match with
                // every read of the field; nothing is marked or kept alive
                // here, and the calls are the late inliner's again -- the IR
                // the link runs these passes over again is the archive's.
                OwnedElements.Say(f, $"at {made.Line}: handed to {handOff!.Field} for the link");
                Corsac.Lang.Lto.OwnedElementRecord record = new(kind);
                record.Needs.Add(needs!.Condition);
                if (OwnedElements.CalleeOf(handOff.Field!) is { } callee) Hint(_elementCallHints, callee, record);
                else Hint(_elementHandOffHints, handOff.Field!, record);
                foreach (var use in uses) _module?.KeepCalls.Remove(use.Call);
                continue;
            }
            // Its elements made where it is, with the link's answer: only where
            // it is owned, never handed back -- a caller that does not own it
            // may keep one.
            if (_elementMode == ElementMode.Linked && !returned && added.Count > 0) _elementSites.Add((f, made, added));
            if (returned && needs is { Condition.IsTrue: false })
            {
                OwnedElements.Say(f, $"at {made.Line}: hands back owned elements if other units do as they need");
                foreach (var use in uses) _module?.KeepCalls.Remove(use.Call);
                if (!handedBack.ContainsKey(f.Name)) _handedBackIf.TryAdd(f.Name, (kind, needs.Condition));
                continue;
            }
            // The collection kept alive past every use of what it holds.
            KeepAlives(made.Dest, keepAlive);
            if (throughField)
            {
                // Marked where it is handed over (MarkElementsThroughFields),
                // once the lifetime rules have judged the field.
                OwnedElements.Say(f, $"at {made.Line}: OWNS ELEMENTS THROUGH {handOff!.Field}");
                _elementMarks.Add((f, handOff.At!, handOff.Operand));
                ElementsOwned++;
                continue;
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

    private bool Why(string why)
    {
        _elementWhy = why;
        return true;
    }

    /// <summary>A KeepAlive of `holder` after each instruction named.</summary>
    private static void KeepAlives(VReg holder, List<(Block B, Instr After)> keepAlive)
    {
        foreach ((Block b, Instr after) in keepAlive)
        {
            int at = b.Instrs.IndexOf(after);
            if (at < 0) continue;
            Instr keep = new() { Op = Opcode.Call, Callee = Corsac.Lang.X86.MachineIntrinsics.KeepAlive, Operands = { new RegOperand(holder) }, Line = after.Line };
            if (ReferenceEquals(after, b.Terminator)) b.Instrs.Insert(at, keep);
            else b.Instrs.Insert(at + 1, keep);
        }
    }

    // ---- elements owned through a field -------------------------------------------------
    //
    // A COLLECTION HANDED TO A FIELD -- `new Parser(Lexer.Tokenize(text))`,
    // the parser keeping the tokens in `_t` and reading them through it --
    // owns its elements as one dropped where it was made does, when every
    // read of the field anywhere in the program is followed as this rule
    // follows the collection's own uses: what it answers going nowhere, and
    // the field's value kept alive (a KeepAlive of the read) past every use
    // of it. Then wherever an element is in use, so is the collection, and
    // whatever proves the collection dead -- the owned-field rules freeing it
    // with the object that holds it (Runtime.FreeField, FreeOwnedFields), or
    // when the field is given another (FreeOwnedReplaced) -- proves its
    // elements dead too, each of those judged on the liveness of what was
    // read from the field. Nothing here frees it: the collection is
    // marked where it is handed over (Runtime.OwnElements), and its storage,
    // whenever it goes, takes its elements with it (IOwnsElements). An
    // element in use across the read that answered it -- a loop reading the
    // field again while one from the last lap is still held -- would have
    // the read's register no longer the collection it came from, and is
    // refused (HeldAcross).

    /// <summary>
    /// How this run takes the rule: not at all; over a whole program; in a
    /// unit's compile, as hints for the link (Lto.OwnedFieldHints); or in the
    /// link's run of a unit's late passes, with its answer (Lto.OwnedFieldFacts.Elements).
    /// </summary>
    private enum ElementMode { Off, Whole, Hints, Linked }

    private ElementMode _elementMode;

    /// <summary>A unit compile's proofs of what is added and read back, made on what other units' calls do: the condition, for the link.</summary>
    private Needs? _elementNeeds;

    /// <summary>In a unit's compile, the functions handing back a collection whose elements are its own if the condition holds.</summary>
    private readonly Dictionary<string, (string Kind, Corsac.Lang.Lto.LifetimeCondition Needs)> _handedBackIf = new(StringComparer.Ordinal);

    /// <summary>One more of a unit's findings for a key: of one kind, every condition together, or none if past the bound or of two kinds.</summary>
    private static void Hint<K>(SortedDictionary<K, Corsac.Lang.Lto.OwnedElementRecord?> hints, K key, Corsac.Lang.Lto.OwnedElementRecord record) where K : notnull
    {
        if (!hints.TryGetValue(key, out Corsac.Lang.Lto.OwnedElementRecord? known)) { hints[key] = record; return; }
        if (known is null) return;
        if (known.Kind != record.Kind || !known.Needs.Add(record.Needs)) hints[key] = null;
    }

    /// <summary>The link's answer, in its run of a unit's late passes.</summary>
    private Corsac.Lang.Lto.OwnedFieldFacts? _elementFacts;

    /// <summary>A unit compile's findings, for the link (ElementHints): the fields every read here is proved of, and the hand-offs.</summary>
    private readonly SortedDictionary<string, Corsac.Lang.Lto.OwnedElementRecord?> _elementReadHints = new(StringComparer.Ordinal);
    private readonly SortedDictionary<string, Corsac.Lang.Lto.OwnedElementRecord?> _elementHandOffHints = new(StringComparer.Ordinal);
    private readonly SortedDictionary<(string Callee, int Argument), Corsac.Lang.Lto.OwnedElementRecord?> _elementCallHints = new(Corsac.Lang.Lto.OwnedFieldHints.PairOrder.Instance);

    /// <summary>Each collection proved to own its elements through a field: where it is handed over, and which operand it is there.</summary>
    private readonly List<(Function F, Instr At, int Operand)> _elementMarks = new();

    /// <summary>Each field's verdict, its reads judged once for every collection handed to it.</summary>
    private readonly Dictionary<string, bool> _fieldElements = new(StringComparer.Ordinal);

    private Dictionary<string, Dictionary<int, string>>? _storedParameters;

    /// <summary>
    /// The field a callee stores its parameter at `at` into, and nothing more
    /// (OwnedElements.StoredParameters). Another unit's function: in its
    /// compile, the hand-off is named for the link (CallField); with the
    /// link's answer, the field it proved that function stores it into.
    /// </summary>
    private string? StoredParameter(string callee, int at)
    {
        _storedParameters ??= new(StringComparer.Ordinal);
        if (!_storedParameters.TryGetValue(callee, out Dictionary<int, string>? map))
        {
            Function? g = _module?.Functions.FirstOrDefault(x => x.Name == callee);
            if (g is null)
                return _elementMode switch
                {
                    ElementMode.Hints => OwnedElements.CallField(callee, at),
                    ElementMode.Linked => _elementFacts!.ElementCallees.GetValueOrDefault((callee, at)),
                    _ => null,
                };
            // Empty while it is judged: a cycle of calls stores nothing.
            _storedParameters[callee] = new();
            _storedParameters[callee] = map = OwnedElements.StoredParameters(g, StoredParameter);
        }
        return map.GetValueOrDefault(at);
    }

    /// <summary>
    /// EVERY READ OF THE FIELD, judged as the collection's own uses are
    /// (ProveOwnedElements, as if each read made it, filled): each read's
    /// register written once and kept alive past every use of what it
    /// answers, nothing it answered still held where it reads the field
    /// again. A list's field read through accessors -- getters handing back
    /// its value or an element, setters adding to it, a stash -- is judged
    /// as one (AccessorsProved). Judged once per field; the KeepAlives placed
    /// only when every read is proved.
    /// </summary>
    private bool FieldElementsProved(string field, string kind, Dictionary<string, bool[]> summaries)
    {
        if (_fieldElements.TryGetValue(field, out bool known)) { if (!known) _elementWhy = $"a read of {field}"; return known; }
        _fieldElements[field] = false;
        _elementWhy = $"a read of {field}";
        // Another unit's function's parameter, in a unit's compile: its field,
        // and every read of it, are the link's to judge.
        if (_elementMode == ElementMode.Hints && OwnedElements.CalleeOf(field) is not null) return _fieldElements[field] = true;
        // With the link's answer, a field it did not prove for this kind is not.
        if (_elementMode == ElementMode.Linked && _elementFacts!.Elements.GetValueOrDefault(field) != kind)
        {
            ReleaseFieldReads(field);
            return false;
        }
        List<(VReg Holder, List<(Block B, Instr After)> Keep)> keeps = new();
        var reads = OwnedElements.FieldReads(_module!, field, kind);
        // In a unit's compile, on what other units' calls do: the link's to check.
        Needs? outer = _elementNeeds;
        Needs? needs = _elementNeeds = _elementMode == ElementMode.Hints ? new(this) : null;
        bool proved = reads is not null && ReadsProved(field, reads, summaries, keeps);
        _elementNeeds = outer;
        // Or through the field's accessors (AccessorsProved), a list's only --
        // in a whole program alone. A unit sees its own getters and setters,
        // not the calls of them other units make, each of which is a read of
        // the field there: it hints plain reads only, and the link proves no
        // field another way, so its answer is never one the accessors made.
        HashSet<Instr> kept = new(ReferenceEqualityComparer.Instance);
        if (!proved && kind == "List" && _elementMode == ElementMode.Whole)
        {
            keeps.Clear();
            proved = AccessorsProved(field, summaries, keeps, kept);
        }
        if (!proved)
        {
            Unproved(field, reads is null ? "a read is not followed" : $"a read is not ({_elementWhy})");
            if (reads is not null) foreach (var read in reads) foreach (var c in read.Calls) _module?.KeepCalls.Remove(c.Call);
            foreach (Instr c in kept) _module?.KeepCalls.Remove(c);
            if (_elementMode is ElementMode.Hints or ElementMode.Linked) ReleaseFieldReads(field);
            _elementWhy = $"a read of {field}";
            return false;
        }
        if (_elementMode == ElementMode.Hints)
        {
            // In a unit's compile the reads' calls are the late inliner's
            // again: what is kept from it is the archive's, for the link's run
            // of these passes. Nothing is kept alive here.
            foreach (var read in reads!) foreach (var c in read.Calls) _module?.KeepCalls.Remove(c.Call);
            if (reads.Count > 0)
            {
                Corsac.Lang.Lto.OwnedElementRecord record = new(kind);
                record.Needs.Add(needs!.Condition);
                Hint(_elementReadHints, field, record);
            }
            return _fieldElements[field] = true;
        }
        foreach ((VReg holder, var keep) in keeps) KeepAlives(holder, keep);
        return _fieldElements[field] = true;
    }

    /// <summary>Every read of the field a load whose collection is used only here (FieldReads), each judged alone.</summary>
    private bool ReadsProved(string field, List<(Function F, Instr Load, List<(Block B, Instr Call, OwnedElements.Role Role)> Calls)> reads,
        Dictionary<string, bool[]> summaries, List<(VReg Holder, List<(Block B, Instr After)> Keep)> keeps)
    {
        foreach ((Function g, Instr load, var calls) in reads)
        {
            Defs defs = new(g);
            HashSet<VReg> container = OwnedElements.Container(g, defs, load);
            List<HashSet<VReg>> held = new();
            if (!defs.IsSingle(load.Dest!) || ProveOwnedElements(g, defs, load, container, calls, summaries, filled: true, held) is not { } keep
                || HeldAcross(g, load, held) && Why("rule 13"))
            {
                OwnedElements.Say(g, $"read of {field} at {load.Line}: not proved ({_elementWhy})");
                return false;
            }
            keeps.Add((load.Dest!, keep));
        }
        return true;
    }

    /// <summary>
    /// A field the link proved whose reads here are not: this unit's compile
    /// proved every one of them over the same IR, or the link would not have,
    /// and a collection another unit marks is freed with its elements while
    /// a read here may still be using them. Never a build that frees one.
    /// </summary>
    private void Unproved(string field, string why)
    {
        if (_elementMode == ElementMode.Linked)
            throw new InvalidOperationException($"owned elements: the link proved {field}, and {why} in {_module?.Name}");
    }

    /// <summary>
    /// The calls of a field's reads kept from the inliner for this rule, let
    /// go: whatever of each read could be followed, of either kind.
    /// </summary>
    private void ReleaseFieldReads(string field)
    {
        if (_module is not { KeepCalls.Count: > 0 } m) return;
        foreach (Function g in m.Functions)
        {
            Defs? defs = null;
            foreach (Block b in g.Blocks)
                foreach (Instr i in b.Instrs)
                {
                    if (i.Op != Opcode.Load || i.Field != field || i.Dest is null) continue;
                    defs ??= new Defs(g, buildCfg: false);
                    HashSet<VReg> container = OwnedElements.Container(g, defs, i);
                    foreach (string kind in new[] { "List", "Dictionary" })
                    {
                        List<(Block B, Instr Call, OwnedElements.Role Role)> found = new();
                        OwnedElements.Uses(g, defs, kind, container, i, out _, into: found);
                        foreach (var c in found) m.KeepCalls.Remove(c.Call);
                    }
                }
        }
    }

    /// <summary>
    /// A UNIT'S FIELDS, after its candidates: in its compile, every field
    /// whose reads MarkOwnedElements kept is judged for the link; with the
    /// link's answer, every field it proved that this unit reads has its
    /// reads kept alive as a whole program's are, and the reads of every one
    /// it did not are the inliner's again.
    /// </summary>
    private void FieldsInUnit(Module m, Dictionary<string, bool[]> summaries)
    {
        if (_elementMode == ElementMode.Hints)
        {
            foreach ((string field, string kind) in m.ElementFields) FieldElementsProved(field, kind, summaries);
            return;
        }
        if (_elementMode != ElementMode.Linked) return;
        HashSet<string> loaded = LoadedFields(m);
        foreach ((string field, string kind) in _elementFacts!.Elements.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            if (loaded.Contains(field)) FieldElementsProved(field, kind, summaries);
        foreach (string field in _elementFacts.ElementKept.Order(StringComparer.Ordinal))
            if (loaded.Contains(field) && !_elementFacts.Elements.ContainsKey(field)) ReleaseFieldReads(field);
    }

    /// <summary>Every field the module reads or takes the address of: anything but a store, and no call's own tag.</summary>
    private static HashSet<string> LoadedFields(Module m)
    {
        HashSet<string> loaded = new(StringComparer.Ordinal);
        foreach (Function g in m.Functions)
            foreach (Block b in g.Blocks)
                foreach (Instr i in b.Instrs)
                    if (i.Field is { Length: > 0 } field && field[0] != '\u0001' && i.Op != Opcode.Store && i.Op != Opcode.Call) loaded.Add(field);
        return loaded;
    }

    /// <summary>
    /// THE UNIT'S HINTS FOR ELEMENTS OWNED THROUGH A FIELD (Lto.OwnedFieldHints):
    /// what it reads, the fields every read of which it proved, the
    /// collections it proved handed to a field or to another unit's
    /// parameter, the parameters its functions store into a field, and the
    /// fields whose reads it kept from the inliner.
    /// </summary>
    private void ElementHints(Module m, Corsac.Lang.Lto.OwnedFieldHints hints)
    {
        hints.Loaded.UnionWith(LoadedFields(m));
        foreach ((string field, Corsac.Lang.Lto.OwnedElementRecord? record) in _elementReadHints) if (record is not null) hints.ElementReads[field] = record;
        foreach ((string field, Corsac.Lang.Lto.OwnedElementRecord? record) in _elementHandOffHints)
            if (record is not null) hints.ElementHandOffs[field] = record;
            // Handed collections of two kinds: neither, which the link must hear of.
            else hints.ElementHandOffs[field] = new("?");
        foreach (((string callee, int argument), Corsac.Lang.Lto.OwnedElementRecord? record) in _elementCallHints)
            hints.ElementCalls[(callee, argument)] = record ?? new("?");
        foreach ((string function, int argument, string field) in OwnedElements.StoredParametersOf(m)) hints.StoredParameters[(function, argument)] = field;
        hints.ElementKept.UnionWith(m.ElementFields.Keys);
    }

    /// <summary>Whether anything in `held` is live just before `at` runs: held over from before it, or round a loop.</summary>
    private static bool HeldAcross(Function f, Instr at, List<HashSet<VReg>> held)
    {
        HashSet<VReg> all = new();
        foreach (HashSet<VReg> h in held) all.UnionWith(h);
        if (all.Count == 0) return false;
        Block? b = f.Blocks.FirstOrDefault(x => x.Instrs.Contains(at));
        if (b is null) return true;
        Liveness live = new(f);
        HashSet<VReg> now = new();
        foreach (VReg r in all)
        {
            if (!live.Tracks(r)) return true;
            if (live.IsLiveOut(b, r)) now.Add(r);
        }
        for (int k = b.Instrs.Count - 1; k >= 0; k--)
        {
            Instr i = b.Instrs[k];
            if (i.Dest is not null) now.Remove(i.Dest);
            foreach (Operand o in i.Operands) if (o is RegOperand r && all.Contains(r.Reg)) now.Add(r.Reg);
            if (ReferenceEquals(i, at)) return now.Count > 0;
        }
        return true;
    }

    /// <summary>What the last proof found added to the collection: the makings of its elements.</summary>
    private List<Instr> _lastAdded = new();

    /// <summary>Each collection proved, with the link's answer, to own what it adds: its making, and the makings of what it adds.</summary>
    private readonly List<(Function F, Instr Made, List<Instr> Added)> _elementSites = new();

    /// <summary>
    /// WHAT IS ADDED TO A COLLECTION THAT OWNS IT, made where the collection
    /// is, once every rule here has had it. The link chose its region sites
    /// from what the unit's compile found, where nothing gave the elements
    /// back, and an element in a region is freed by no free -- given back
    /// with the whole region however long its collection lives, and one the
    /// region does not take is the collector's whatever frees its collection.
    /// So: on the heap where the collection is in a frame (no longer the
    /// allocation it was) or on the heap, where its storage takes them;
    /// beside it where the link made it a region's (AllocNear), each that the
    /// collection's making comes before -- in its region, given back with it,
    /// the elements being dead whenever it is. One made before the collection
    /// is left as the link chose. The collection itself stays where the link
    /// put it: on the heap, its storage made in a region all the same, it
    /// would read arrays gone with the region when it is freed.
    /// </summary>
    private void ElementSites()
    {
        foreach ((Function f, Instr made, List<Instr> added) in _elementSites)
        {
            Block? home = f.Blocks.FirstOrDefault(b => b.Instrs.Contains(made));
            if (!made.RegionSite || home is null || made.Dest is null)
            {
                foreach (Instr origin in added) origin.RegionSite = false;
                continue;
            }
            Cfg cfg = new(f);
            foreach (Instr origin in added)
            {
                if (origin.Op != Opcode.Call || !RegionPointsTo.IsRewritable(origin.Callee) || origin.Dest is null) continue;
                Block? at = f.Blocks.FirstOrDefault(b => b.Instrs.Contains(origin));
                if (at is null) continue;
                bool after = ReferenceEquals(at, home) ? home.Instrs.IndexOf(made) < at.Instrs.IndexOf(origin) : cfg.Dominates(home, at);
                if (!after) continue;
                int k = at.Instrs.IndexOf(origin);
                at.Instrs.RemoveAt(k);
                at.Instrs.InsertRange(k, RegionPointsTo.Beside(f, origin, made.Dest));
            }
        }
        _elementSites.Clear();
    }

    /// <summary>
    /// The marks, placed after every lifetime rule has judged the field: a
    /// call of the runtime's marker just before each collection is handed
    /// over (Runtime.OwnElements), bookkeeping no analysis need follow.
    /// </summary>
    private void MarkElementsThroughFields()
    {
        foreach ((Function f, Instr at, int operand) in _elementMarks)
        {
            Block? b = f.Blocks.FirstOrDefault(x => x.Instrs.Contains(at));
            if (b is null || operand >= at.Operands.Count || at.Operands[operand] is not RegOperand collection) continue;
            List<Instr> mark = new();
            VReg word = Word(f, mark, collection.Reg, at.Line, "elementsOf");
            mark.Add(new Instr { Op = Opcode.Call, Callee = OwnedElements.Marker, Operands = { new RegOperand(word) }, Line = at.Line });
            b.Instrs.InsertRange(b.Instrs.IndexOf(at), mark);
            _bookkeeping.UnionWith(mark);
        }
        _elementMarks.Clear();
    }

    private List<(Block B, Instr After)>? ProveOwnedElements(Function f, Defs defs, Instr alloc, HashSet<VReg> container,
        List<(Block B, Instr Call, OwnedElements.Role Role)> calls, Dictionary<string, bool[]> summaries, bool filled = false,
        List<HashSet<VReg>>? heldOut = null)
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
                joinable: joins.Count > 0 ? joins : null, needs: _elementNeeds, consumers: new(ReferenceEqualityComparer.Instance) { call });
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
                Flow flow = Analyse(f, new[] { got }, summaries, null, needs: _elementNeeds);
                if (flow.Escapes) { _elementWhy = "rule 9"; return null; }
                held.Add(flow.Derived);
            }
        foreach ((Block b, Instr load) in slotLoads)
        {
            if (load.Dest is null) continue;
            Flow flow = Analyse(f, new[] { load.Dest }, summaries, null, needs: _elementNeeds);
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
        heldOut?.AddRange(held);
        // What was added, for the link's answer to make where the collection
        // is (ElementSites).
        _lastAdded = adderOf.Keys.ToList();
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

    // ---- elements owned through a field's accessors -------------------------------------
    //
    // THE PARSER'S SHAPE: the list in a wrapper's field (ParserTokens.mutable),
    // the wrapper in the parser's (Parser._t), the elements read by an indexer
    // that hands one back and by Cur and Ahead that hand it back again, the
    // list written through a getter that fills the field when it is empty
    // (`mutable ??= new(...)`) and by setters whose parameter is only added
    // to it, and a token a split replaced kept in another list (`_splits`)
    // to be put back. Each piece is followed as a read of the field is:
    //
    // - A GETTER of the field's value (OwnedElements.CollectionGetters) is a
    //   read of the field wherever it is called; a `??=` variable holding the
    //   field's value or the list made to fill it is one of its values.
    // - AN ELEMENT HANDED BACK makes its function an element getter, and each
    //   call of it is an element read in the caller. What reads an element
    //   through a getter has no register of the collection to keep alive: it
    //   keeps alive the PARAMETER the collection is reached from instead
    //   (OwnedElements.Root: `this`, through `_t` and `mutable`). An object
    //   reached from a parameter is held by the heap, so no function made it
    //   for itself, and a store frees what it replaces only in an object its
    //   function made and kept (Escape.PrivateOwner): while the parameter
    //   lives, so does everything owned through it.
    // - A SETTER (an adder) adds its parameter to the field's list and does
    //   nothing else with it; each call of it is an add in the caller, judged
    //   there: an object made for it, as any add.
    // - PUT BACK: an element added again to the list it came from -- reached
    //   from the same parameter by the same fields (ListPath), every store
    //   of which fills the object as it is made or fills an empty field
    //   (FieldsFixed), so it is the same list -- is no new owner. The same
    //   element twice in the list is given back once (List.FreeStorage:
    //   OwnedElements.ReleaseDistinct).
    // - A STASH (StashProved): another list in a field of the same object,
    //   into which an element is put, alone or in a tuple, to be put back.
    //   Whatever is read out of it at a place that goes back into the list
    //   was put there as an element of that list, and goes nowhere else.
    //
    // An element answered by an array the wrapper keeps instead -- the
    // indexer's `mutable is null ? snapshot[i] : mutable[i]` -- is followed
    // with the element it is joined with when it is read only where the
    // field was just found empty (NullGuarded): put back, it goes into a
    // list that was filled from empty, which owns nothing.

    /// <summary>What one function does with the field's collection (AccessorsProved).</summary>
    private sealed class AccessScan
    {
        public required Function F;
        public required Defs Defs;
        public required OwnedElements.FieldValueSet Values;
        public required RegisterWrites Writes;
        public List<(Block B, Instr Call, OwnedElements.Role Role)> Calls = new();
    }

    /// <summary>
    /// WHERE A LIST IS REACHED FROM: a parameter of the function, and the
    /// fields read on the way from it, outermost first ("Parser::_t/
    /// ParserTokens::mutable"). Two values with one path in one call of a
    /// function are one object, when every field on it is filled once
    /// (FieldsFixed).
    /// </summary>
    private readonly record struct ListPath(int Param, string Chain)
    {
        public ListPath Then(string more) => new(Param, Chain.Length == 0 ? more : more.Length == 0 ? Chain : Chain + "/" + more);
    }

    /// <summary>The path of an operand, through copies and reads of reference fields to a parameter; null when it reaches none.</summary>
    private static ListPath? RootPath(Function g, Defs defs, Operand o)
    {
        List<string> fields = new();
        int p = OwnedElements.Root(g, defs, o, fields);
        if (p < 0) return null;
        fields.Reverse();
        return new ListPath(p, string.Join("/", fields));
    }

    /// <summary>An element read: a collection call's answer, or an element getter's; with the path of the list it is read from.</summary>
    private readonly record struct ElementSource(Instr At, VReg Dest, VReg? Container, ListPath? Path);

    /// <summary>An element parked in a stash: where, the stash's path and the element's list's.</summary>
    private readonly record struct Parking(Function F, Instr Add, string Stash, ListPath StashPath, ListPath ListPath, HashSet<VReg> Derived);

    /// <summary>
    /// A FIELD'S LIST READ THROUGH ITS ACCESSORS, over the whole program: every
    /// value of it followed (FieldValues, Uses), every element read -- by the
    /// collection's calls or an element getter's -- going nowhere but back
    /// (handed back by a getter, put back into the list, put into a stash),
    /// every add an object made for it, a setter's parameter or an element
    /// put back. The KeepAlives each needs are added to `keeps`; the calls
    /// kept from the inliner that nothing needs if it fails, to `kept`.
    /// </summary>
    private bool AccessorsProved(string field, Dictionary<string, bool[]> summaries, List<(VReg Holder, List<(Block B, Instr After)> Keep)> keeps,
        HashSet<Instr> kept)
    {
        Module m = _module!;
        HashSet<string> addressed = OwnedElements.AddressTaken(m);
        HashSet<string> collectionGetters = OwnedElements.CollectionGetters(m, field, addressed);
        Dictionary<string, AccessScan> scans = new(StringComparer.Ordinal);
        bool Refuse(Function g, string why)
        {
            _elementWhy = $"a read of {field}: {why}";
            OwnedElements.Say(g, $"through accessors of {field}: {why}");
            return false;
        }
        foreach (Function g in m.Functions)
        {
            if (!OwnedElements.Touches(g, field, collectionGetters)) continue;
            Defs defs = new(g);
            if (OwnedElements.FieldValues(g, defs, field, collectionGetters) is not { } values) return Refuse(g, "not followed");
            if (values.Seeds.Count == 0) continue;
            if (g.Async is not null) return Refuse(g, "read in an async body");
            if (OwnedElements.Uses(g, defs, "List", values.Values, null, out bool returned, allowed: values.Stores) is not { } calls)
                return Refuse(g, "a use not followed");
            if (returned && !collectionGetters.Contains(g.Name)) return Refuse(g, "handed back");
            foreach (var c in calls) kept.Add(c.Call);
            if (calls.Any(c => c.Role.Enumerates || c.Role.Views || c.Role.OutSlot >= 0))
                return Refuse(g, "walked through an accessor");
            scans[g.Name] = new AccessScan { F = g, Defs = defs, Values = values, Writes = new RegisterWrites(g), Calls = calls };
        }

        // Where each getter of the collection reaches it from, by its parameters.
        Dictionary<string, ListPath> getterPath = new(StringComparer.Ordinal);
        ListPath? CollectionPath(AccessScan s, VReg v)
        {
            ListPath? path = null;
            HashSet<VReg> seen = new();
            Stack<VReg> work = new();
            work.Push(v);
            while (work.TryPop(out VReg? r))
            {
                if (!seen.Add(r)) continue;
                if (s.F.Params.Contains(r)) return null;
                ListPath? here;
                if (s.Defs.IsSingle(r) && s.Defs.Definition(r) is { } d)
                {
                    if (d.Op is Opcode.Copy or Opcode.Trunc64 or Opcode.ZExt32 && d.Operands[0] is RegOperand from) { work.Push(from.Reg); continue; }
                    if (OwnedElements.FreshHere(s.Defs, r)) continue;
                    if (d.Op == Opcode.Load && d.Field == field) here = RootPath(s.F, s.Defs, d.Operands[0])?.Then(field);
                    else if (d.Op == Opcode.Call && d.Callee is { } c && getterPath.TryGetValue(c, out ListPath inner) && inner.Param < d.Operands.Count)
                        here = RootPath(s.F, s.Defs, d.Operands[inner.Param])?.Then(inner.Chain);
                    else return null;
                }
                else if (s.Writes.TryGetValue(r, out WriteList ws))
                {
                    foreach (Instr w in ws)
                    {
                        if (w.Op != Opcode.Copy || w.Operands.Count != 1) return null;
                        if (w.Operands[0] is RegOperand from) work.Push(from.Reg);
                        else if (w.Operands[0] is not ImmOperand { Value: 0 }) return null;
                    }
                    continue;
                }
                else return null;
                if (here is null || path is not null && path != here) return null;
                path = here;
            }
            return path;
        }
        for (int round = 0; round < 6; round++)
        {
            bool changed = false;
            foreach (string name in collectionGetters)
            {
                if (getterPath.ContainsKey(name) || !scans.TryGetValue(name, out AccessScan? s)) continue;
                ListPath? path = null;
                bool agree = true;
                foreach (Block b in s.F.Blocks)
                    if (b.Terminator is { Op: Opcode.Ret, Operands: [RegOperand back] })
                    {
                        ListPath? here = CollectionPath(s, back.Reg);
                        if (here is null || path is not null && path != here) agree = false;
                        path = here;
                    }
                if (!agree || path is not { } found) continue;
                getterPath[name] = found;
                changed = true;
            }
            if (!changed) break;
        }

        // THE ADDERS: a parameter added to the collection and put to no
        // other use, with the path of the list it is added to.
        Dictionary<string, (ListPath Target, HashSet<int> Values)> adders = new(StringComparer.Ordinal);
        for (int round = 0; round < 4; round++)
        {
            bool changed = false;
            foreach (AccessScan s in scans.Values)
            {
                Function g = s.F;
                if (addressed.Contains(g.Name) || g.Name == m.Entry) continue;
                for (int p = 1; p < g.Params.Count; p++)
                {
                    if (adders.TryGetValue(g.Name, out var known) && known.Values.Contains(p)) continue;
                    HashSet<VReg> regs = OwnedElements.Container(g, s.Defs, g.Params[p]);
                    ListPath? target = null;
                    bool ok = true, any = false;
                    foreach (Block b in g.Blocks)
                    {
                        foreach (Instr i in b.Instrs)
                        {
                            int at = -1, count = 0;
                            for (int k = 0; k < i.Operands.Count; k++)
                                if (i.Operands[k] is RegOperand r && regs.Contains(r.Reg)) { at = k; count++; }
                            if (count == 0) continue;
                            if (i.Op is Opcode.Copy or Opcode.Trunc64 or Opcode.ZExt32 && i.Dest is not null && regs.Contains(i.Dest)) continue;
                            if (i.Op == Opcode.Call && at > 0 && IsCollectorNote(i.Callee)) continue;
                            ListPath? here = null;
                            if (count == 1 && s.Calls.FirstOrDefault(c => ReferenceEquals(c.Call, i)) is { Call: not null } call && call.Role.Adds == at
                                && i.Operands[0] is RegOperand list && s.Values.Values.Contains(list.Reg))
                                here = CollectionPath(s, list.Reg);
                            else if (count == 1 && i.Op == Opcode.Call && i.Callee is { } c && adders.TryGetValue(c, out var adder) && adder.Values.Contains(at)
                                && adder.Target.Param < i.Operands.Count)
                                here = RootPath(g, s.Defs, i.Operands[adder.Target.Param])?.Then(adder.Target.Chain);
                            if (here is null || target is not null && target != here) { ok = false; break; }
                            target = here;
                            any = true;
                        }
                        if (!ok) break;
                    }
                    if (!ok || !any || target is not { } found) continue;
                    if (!adders.TryGetValue(g.Name, out var entry)) adders[g.Name] = entry = (found, new());
                    if (entry.Target != found) continue;
                    entry.Values.Add(p);
                    changed = true;
                }
            }
            if (!changed) break;
        }
        _adders = adders.ToDictionary(a => a.Key, a => (a.Value.Target.Param, a.Value.Values), StringComparer.Ordinal);
        _adderPaths = adders.ToDictionary(a => a.Key, a => a.Value.Target, StringComparer.Ordinal);

        // The element getters, found below, and the functions calling an accessor.
        Dictionary<string, ListPath> elementGetters = new(StringComparer.Ordinal);
        AccessScan ScanOf(Function g)
        {
            if (scans.TryGetValue(g.Name, out AccessScan? s)) return s;
            return scans[g.Name] = new AccessScan { F = g, Defs = new Defs(g), Values = new(), Writes = new RegisterWrites(g) };
        }
        bool CallsAccessor(Function g) => g.Blocks.Any(b => b.Instrs.Any(i => i.Op == Opcode.Call && i.Callee is { } c
            && (elementGetters.ContainsKey(c) || adders.ContainsKey(c))));

        List<ElementSource> Sources(AccessScan s)
        {
            List<ElementSource> sources = new();
            foreach ((Block _, Instr call, OwnedElements.Role role) in s.Calls)
                if (role.Reads && call.Dest is not null && call.Operands[0] is RegOperand c)
                    sources.Add(new(call, call.Dest, c.Reg, CollectionPath(s, c.Reg)));
            foreach (Block b in s.F.Blocks)
                foreach (Instr i in b.Instrs)
                    if (i.Op == Opcode.Call && i.Dest is not null && i.Callee is { } callee && elementGetters.TryGetValue(callee, out ListPath inner)
                        && inner.Param < i.Operands.Count)
                        sources.Add(new(i, i.Dest, null, RootPath(s.F, s.Defs, i.Operands[inner.Param])?.Then(inner.Chain)));
            return sources;
        }

        // What may take an element back in a function: the list's own adds
        // and the adders, each with the path of the list it adds to; and the
        // adds of a stash, a list read from another field.
        List<(Instr Call, ListPath? Target)> PutsOf(AccessScan s)
        {
            List<(Instr, ListPath?)> puts = new();
            foreach ((Block _, Instr call, OwnedElements.Role role) in s.Calls)
                if (role.Adds > 0 && call.Operands[0] is RegOperand list)
                    puts.Add((call, CollectionPath(s, list.Reg)));
            foreach (Block b in s.F.Blocks)
                foreach (Instr i in b.Instrs)
                    if (i.Op == Opcode.Call && i.Callee is { } callee && adders.TryGetValue(callee, out var adder) && adder.Target.Param < i.Operands.Count)
                        puts.Add((i, RootPath(s.F, s.Defs, i.Operands[adder.Target.Param])?.Then(adder.Target.Chain)));
            return puts;
        }
        List<(Instr Call, string Stash, ListPath? StashPath)> StashAddsOf(AccessScan s)
        {
            List<(Instr, string, ListPath?)> adds = new();
            foreach (Block b in s.F.Blocks)
                foreach (Instr i in b.Instrs)
                    if (i.Op == Opcode.Call && i.Callee is { } callee && !adders.ContainsKey(callee)
                        && OwnedElements.RoleOf("List", callee) is { Adds: > 0 } role && i.Operands.Count > role.Adds
                        && StashOf(s.Defs, i.Operands[0], field) is ({ } stash, var load))
                        adds.Add((i, stash, RootPath(s.F, s.Defs, load.Operands[0])?.Then(stash)));
            return adds;
        }

        // One element read judged: where it goes, given what may take it back.
        Dictionary<Function, HashSet<VReg>> returnedOf = new();
        HashSet<VReg> ReturnedOf(Function g) => returnedOf.TryGetValue(g, out var r) ? r : returnedOf[g] = OwnedElements.Returned(g);
        // Every read of one list in a function together, kept by its
        // parameter; one reached from none alone, kept by its own read.
        List<List<ElementSource>> Groups(AccessScan s)
        {
            List<List<ElementSource>> groups = new();
            foreach (var byPath in Sources(s).GroupBy(x => x.Path))
            {
                if (byPath.Key is null) foreach (ElementSource alone in byPath) groups.Add(new() { alone });
                else groups.Add(byPath.ToList());
            }
            return groups;
        }
        (Flow Flow, bool Returns, List<Instr> Puts, List<(Instr Add, string Stash, ListPath StashPath)> Parks) Follow(AccessScan s, List<ElementSource> group)
        {
            Function g = s.F;
            HashSet<Instr> consumers = new(ReferenceEqualityComparer.Instance);
            HashSet<Instr> holderConsumers = new(ReferenceEqualityComparer.Instance);
            var stashAdds = StashAddsOf(s);
            if (group[0].Path is { } path)
            {
                foreach (var put in PutsOf(s)) if (put.Target == path) consumers.Add(put.Call);
                foreach (var add in stashAdds)
                    if (add.StashPath is { } at && at.Param == path.Param) { holderConsumers.Add(add.Call); consumers.Add(add.Call); }
            }
            HashSet<VReg> back = ReturnedOf(g);
            Flow flow = Analyse(g, group.SelectMany(x => ElementRoots(s, x, field)).Distinct().ToArray(), summaries, null, returnable: back.Count > 0 ? back : null,
                consumers: consumers, holderConsumers: holderConsumers);
            bool returns = flow.Derived.Overlaps(back);
            // Handed back joined with what is not the element (a borrow):
            // its callers could not tell, and might put that back.
            if (returns && flow.Borrowed.Overlaps(back)) flow.Escapes = true;
            List<Instr> puts = consumers.Where(c => !holderConsumers.Contains(c) && c.Operands.Any(o => o is RegOperand r && flow.Derived.Contains(r.Reg))).ToList();
            List<(Instr, string, ListPath)> parks = stashAdds.Where(a => holderConsumers.Contains(a.Call) && Parks(g, s.Defs, a.Call, flow.Derived))
                .Select(a => (a.Call, a.Stash, a.StashPath!.Value)).ToList();
            return (flow, returns, puts, parks);
        }

        // THE ELEMENT GETTERS: what hands back an element of the list, by
        // the path it reaches the list from; found until no more are.
        for (int round = 0; round < 8; round++)
        {
            bool changed = false;
            foreach (Function g in m.Functions)
            {
                if (g.Async is not null || elementGetters.ContainsKey(g.Name) || ReturnedOf(g).Count == 0) continue;
                if (!scans.ContainsKey(g.Name) && !CallsAccessor(g)) continue;
                AccessScan s = ScanOf(g);
                ListPath? path = null;
                bool agree = true, any = false;
                foreach (List<ElementSource> group in Groups(s))
                {
                    if (!Follow(s, group).Returns) continue;
                    any = true;
                    if (group[0].Path is not { } here || path is not null && path != here) agree = false;
                    path = group[0].Path;
                }
                if (!any) continue;
                if (!agree || path is not { } found || addressed.Contains(g.Name) || g.Name == m.Entry)
                    return Refuse(g, "an element handed back from no one list, or by a function called unseen");
                elementGetters[g.Name] = found;
                changed = true;
            }
            if (!changed) break;
        }

        // EACH FUNCTION, judged.
        HashSet<string> fixedFields = new(StringComparer.Ordinal) { field };
        void Fixed(ListPath path) { foreach (string f in path.Chain.Split('/', StringSplitOptions.RemoveEmptyEntries)) fixedFields.Add(f); }
        bool putBack = false;
        List<Parking> parked = new();
        HashSet<string> stashes = new(StringComparer.Ordinal);
        foreach (Function g in m.Functions)
        {
            if (!scans.ContainsKey(g.Name) && !CallsAccessor(g)) continue;
            if (g.Async is not null) return Refuse(g, "an accessor called in an async body");
            AccessScan s = ScanOf(g);
            Defs defs = s.Defs;
            Cfg cfg = defs.Cfg;
            List<HashSet<VReg>> derivedAll = new();
            foreach (List<ElementSource> group in Groups(s))
            {
                ElementSource source = group[0];
                (Flow flow, bool returns, List<Instr> puts, var parks) = Follow(s, group);
                if (flow.Escapes) return Refuse(g, $"an element read at {source.At.Line} escapes via {flow.Why?.Op} {flow.Why?.Callee}");
                if (returns && (!elementGetters.TryGetValue(g.Name, out ListPath handed) || handed != source.Path))
                    return Refuse(g, $"an element handed back at {source.At.Line} from another list");
                // Taken back or put into a stash: through fields that never change.
                if (puts.Count > 0 || parks.Count > 0)
                {
                    putBack = true;
                    Fixed(source.Path!.Value);
                    foreach (var park in parks)
                    {
                        if (!ParkedOnly(g, defs, park.Add, flow.Derived)) return Refuse(g, $"a stash's tuple at {park.Add.Line} used otherwise");
                        stashes.Add(park.Stash);
                        Fixed(park.StashPath);
                        parked.Add(new Parking(g, park.Add, park.Stash, park.StashPath, source.Path!.Value, flow.Derived));
                    }
                }
                // What keeps the list alive while the element is used: the
                // register the list was read into, when that is all; else
                // the parameter it is reached from.
                VReg keeper;
                Instr? anchor = null;
                if (source.Path is null && source.Container is { } local && defs.IsSingle(local)
                    && FieldLoadOf(defs, local, field) is { } loaded)
                {
                    keeper = local;
                    anchor = defs.Definition(local);
                    if (HeldAcross(g, loaded, new() { flow.Derived })) return Refuse(g, $"an element read at {source.At.Line} held across another read");
                }
                else if (source.Path is { } path) keeper = g.Params[path.Param];
                else return Refuse(g, $"an element read at {source.At.Line} through no parameter");
                if (KeepAfterUses(g, cfg, anchor, flow.Derived) is not { } keep) return Refuse(g, $"an element read at {source.At.Line} used where its list is not");
                keeps.Add((keeper, keep));
                derivedAll.Add(flow.Derived);
            }

            // THE ADDS: an object made for it, a setter's own parameter, or
            // an element put back (judged above, or read out of a stash).
            var unparked = Unparked(s, field);
            Dictionary<Instr, Instr> adderOf = new(ReferenceEqualityComparer.Instance);
            List<(Instr Call, int Value, VReg? Container, ListPath? Target)> adds = new();
            foreach ((Block _, Instr call, OwnedElements.Role role) in s.Calls)
                if (role.Adds > 0 && call.Operands[0] is RegOperand list)
                    adds.Add((call, role.Adds, list.Reg, CollectionPath(s, list.Reg)));
            foreach (Block b in g.Blocks)
                foreach (Instr i in b.Instrs)
                    if (i.Op == Opcode.Call && i.Callee is { } callee && adders.TryGetValue(callee, out var adder) && adder.Target.Param < i.Operands.Count)
                        foreach (int v in adder.Values)
                            if (v < i.Operands.Count) adds.Add((i, v, null, RootPath(g, defs, i.Operands[adder.Target.Param])?.Then(adder.Target.Chain)));
            foreach ((Instr call, int value, VReg? container, ListPath? target) in adds)
            {
                Operand given = call.Operands[value];
                if (given is RegOperand pr && g.Params.IndexOf(pr.Reg) is int p && p > 0 && adders.TryGetValue(g.Name, out var mine) && mine.Values.Contains(p)) continue;
                if (given is RegOperand er && derivedAll.Any(d => d.Contains(er.Reg))) continue;
                if (given is RegOperand ur && unparked.TryGetValue(ur.Reg, out var fromStash))
                {
                    // Judged with the stash: what it read out goes back where it came from.
                    if (target is null) return Refuse(g, $"put back at {call.Line} through no parameter");
                    putBack = true;
                    stashes.Add(fromStash.Stash);
                    continue;
                }
                Block at = g.Blocks.First(x => x.Instrs.Contains(call));
                List<Instr> origins = new();
                HashSet<VReg> joins = new();
                if (Origin(g, defs, at, call, given, new()) is { } single) origins.Add(single);
                else if (VariableOrigins(g, defs, given, joins) is { } several) origins.AddRange(several);
                else return Refuse(g, $"adds at {call.Line} what was not made for it");
                foreach (Instr origin in origins)
                    if (!adderOf.TryAdd(origin, call)) return Refuse(g, $"adds at {call.Line} what another add takes");
                HashSet<Block> renewing = new(ReferenceEqualityComparer.Instance);
                foreach (Instr origin in origins) renewing.Add(g.Blocks.First(x => x.Instrs.Contains(origin)));
                if (ReachesAvoiding(cfg, at, at, renewing)) return Refuse(g, $"adds at {call.Line} the same object round a loop");
                Flow flow = Analyse(g, origins.Select(o => o.Dest!).ToArray(), summaries, origins.Count == 1 ? origins[0] : null, null,
                    joinable: joins.Count > 0 ? joins : null, consumers: new(ReferenceEqualityComparer.Instance) { call });
                if (flow.Escapes) return Refuse(g, $"what is added at {call.Line} escapes");
                // Its uses once it is in the list: made, filled and read
                // before, it was nobody's.
                HashSet<Instr> later = new(ReferenceEqualityComparer.Instance);
                int callAt = at.Instrs.IndexOf(call);
                foreach (Block x in g.Blocks)
                {
                    bool loops = cfg.Reaches(at, at);
                    if (!ReferenceEquals(x, at) && !cfg.Reaches(at, x)) continue;
                    for (int k = ReferenceEquals(x, at) && !loops ? callAt + 1 : 0; k < x.Instrs.Count; k++)
                        if (x.Instrs[k].Operands.Any(o => o is RegOperand r && flow.Derived.Contains(r.Reg))) later.Add(x.Instrs[k]);
                }
                if (later.Count == 0) continue;
                VReg keeper;
                Instr? anchor = null;
                if (container is { } local && defs.IsSingle(local) && FieldLoadOf(defs, local, field) is { } loaded)
                {
                    keeper = local;
                    anchor = defs.Definition(local);
                    if (HeldAcross(g, loaded, new() { flow.Derived })) return Refuse(g, $"what is added at {call.Line} held across another read");
                }
                else if (target is { } path) keeper = g.Params[path.Param];
                else return Refuse(g, $"adds at {call.Line} through no parameter");
                if (KeepAfterUses(g, cfg, anchor, flow.Derived, later) is not { } keep) return Refuse(g, $"what is added at {call.Line} used where its list is not");
                keeps.Add((keeper, keep));
            }
        }

        // What is put back is the element of the same list: every field on
        // the way to it, and every stash, filled once as its object is made,
        // or filled when empty.
        if (stashes.Count > 0 && !StashProved(field, stashes, parked, scans, addressed, fixedFields, kept, CollectionPath)) return false;
        if (putBack && !FieldsFixed(fixedFields, field, scans, addressed)) return false;
        OwnedElements.Say(scans.Values.First().F, $"through accessors of {field}: proved{(putBack ? ", put back" : "")}");
        return true;
    }

    /// <summary>The adders' list paths (AccessorsProved), for Unparking.</summary>
    private Dictionary<string, ListPath>? _adderPaths;

    /// <summary>The read of `field` a register holds, through copies written once; null for anything else.</summary>
    private static Instr? FieldLoadOf(Defs defs, VReg r, string field)
    {
        for (int hops = 0; hops < 6; hops++)
        {
            if (!defs.IsSingle(r) || defs.Definition(r) is not { } d) return null;
            if (d.Op == Opcode.Load && d.Field == field) return d;
            if (d.Op is not (Opcode.Copy or Opcode.Trunc64 or Opcode.ZExt32) || d.Operands[0] is not RegOperand from) return null;
            r = from.Reg;
        }
        return null;
    }

    /// <summary>The stash a call's receiver is -- a list read from another field -- with that read; (null, null) for anything else.</summary>
    private static (string? Field, Instr Load) StashOf(Defs defs, Operand receiver, string field)
    {
        if (receiver is not RegOperand { Reg: var r }) return (null, null!);
        for (int hops = 0; hops < 6; hops++)
        {
            if (!defs.IsSingle(r) || defs.Definition(r) is not { } d) return (null, null!);
            if (d.Op == Opcode.Load && d.Field is { } stash && stash != field && d.Operands.Count == 1 && d.Operands[0] is RegOperand) return (stash, d);
            if (d.Op is not (Opcode.Copy or Opcode.Trunc64 or Opcode.ZExt32) || d.Operands[0] is not RegOperand from) return (null, null!);
            r = from.Reg;
        }
        return (null, null!);
    }

    /// <summary>
    /// The registers an element read starts from: the answer, and what it is
    /// joined with that was read only where the field was found empty
    /// (NullGuarded) -- the wrapper's array, read instead of its list.
    /// </summary>
    private static VReg[] ElementRoots(AccessScan s, ElementSource source, string field)
    {
        List<VReg> roots = new() { source.Dest };
        if (source.Container is not { } c || FieldLoadOf(s.Defs, c, field) is not { } load || OwnerOf(s.Defs, load.Operands[0]) is not { } owner) return roots.ToArray();
        // Copies of the answer written once, then the variables they are joined into.
        HashSet<VReg> answer = OwnedElements.Container(s.F, s.Defs, source.Dest);
        foreach (Block b in s.F.Blocks)
            foreach (Instr i in b.Instrs)
            {
                if (i.Op != Opcode.Copy || i.Dest is not { } join || s.Defs.IsSingle(join) || i.Operands[0] is not RegOperand from || !answer.Contains(from.Reg)) continue;
                if (!s.Writes.TryGetValue(join, out WriteList ws)) continue;
                List<VReg> others = new();
                bool guarded = true;
                foreach (Instr w in ws)
                {
                    if (ReferenceEquals(w, i)) continue;
                    if (w.Op != Opcode.Copy || w.Operands is not [RegOperand other]) { guarded = false; break; }
                    if (answer.Contains(other.Reg)) continue;
                    Block at = s.F.Blocks.First(x => x.Instrs.Contains(w));
                    if (!NullGuarded(s, at, owner, field)) { guarded = false; break; }
                    others.Add(other.Reg);
                }
                if (guarded) roots.AddRange(others);
            }
        return roots.Distinct().ToArray();
    }

    /// <summary>
    /// Whether a block runs only where `owner`'s field was just read and found
    /// null: dominated by the null edge of a test of that read, an edge into a
    /// block nothing else enters. That edge's block, or null.
    /// </summary>
    internal static Block? NullGuard(Function f, Defs defs, Block at, object owner, string field)
    {
        Cfg cfg = defs.Cfg;
        foreach (Block b in f.Blocks)
        {
            if (b.Terminator is not { Op: Opcode.Branch, Operands: [RegOperand tested] } branch || branch.Targets.Count != 2) continue;
            Block? nullEdge = null;
            if (IsOwnersRead(defs, tested.Reg, owner, field)) nullEdge = branch.Targets[1];
            else if (defs.IsSingle(tested.Reg) && defs.Definition(tested.Reg) is { Op: Opcode.Eq or Opcode.Ne } cmp && cmp.Operands.Count == 2)
            {
                RegOperand? read = cmp.Operands[0] is ImmOperand { Value: 0 } ? cmp.Operands[1] as RegOperand
                    : cmp.Operands[1] is ImmOperand { Value: 0 } ? cmp.Operands[0] as RegOperand : null;
                if (read is not null && IsOwnersRead(defs, read.Reg, owner, field)) nullEdge = cmp.Op == Opcode.Eq ? branch.Targets[0] : branch.Targets[1];
            }
            if (nullEdge is null || ReferenceEquals(branch.Targets[0], branch.Targets[1]) || cfg.Preds(nullEdge).Count != 1) continue;
            if (cfg.Dominates(nullEdge, at)) return nullEdge;
        }
        return null;
    }

    private static bool NullGuarded(AccessScan s, Block at, object owner, string field) => NullGuard(s.F, s.Defs, at, owner, field) is not null;

    private static bool IsOwnersRead(Defs defs, VReg r, object owner, string field)
        => FieldLoadOf(defs, r, field) is { Operands: [var from] } && ReferenceEquals(OwnerOf(defs, from), owner);

    /// <summary>
    /// The object an operand addresses, for telling two of its uses are of
    /// one object: its frame slot (an object promoted to the frame), or the
    /// register its copies written once start from.
    /// </summary>
    internal static object? OwnerOf(Defs defs, Operand o)
    {
        for (int hops = 0; hops < 8; hops++)
        {
            if (o is SlotOperand { Slot: var slot }) return slot;
            if (o is not RegOperand { Reg: var r }) return null;
            if (!defs.IsSingle(r) || defs.Definition(r) is not { Op: Opcode.Copy or Opcode.Trunc64 or Opcode.ZExt32 } d) return r;
            o = d.Operands[0];
        }
        return null;
    }

    /// <summary>
    /// Where a KeepAlive goes for every use of `derived`: after each, each
    /// dominated by `anchor` (the keeper's definition; a parameter has none).
    /// Null when one is not.
    /// </summary>
    private static List<(Block B, Instr After)>? KeepAfterUses(Function f, Cfg cfg, Instr? anchor, HashSet<VReg> derived, IReadOnlySet<Instr>? only = null)
    {
        Block? anchorBlock = anchor is null ? null : f.Blocks.First(b => b.Instrs.Contains(anchor));
        List<(Block, Instr)> keep = new();
        foreach (Block b in f.Blocks)
            foreach (Instr i in b.Instrs)
            {
                bool uses = false;
                foreach (Operand o in i.Operands) if (o is RegOperand r && derived.Contains(r.Reg)) { uses = true; break; }
                if (!uses || only is not null && !only.Contains(i)) continue;
                if (anchorBlock is not null)
                {
                    bool dominated = ReferenceEquals(b, anchorBlock) ? b.Instrs.IndexOf(anchor!) < b.Instrs.IndexOf(i) : cfg.Dominates(anchorBlock, b);
                    if (!dominated) return null;
                }
                keep.Add((b, i));
            }
        return keep;
    }

    /// <summary>The memory a stash's add is handed -- a frame slot, or a block made here -- as its operands name it.</summary>
    private static (FrameSlot? Slot, HashSet<VReg>? Block) HolderOf(Function g, Defs defs, Operand value)
    {
        if (SlotOf(defs, value) is { } slot) return (slot, null);
        if (value is RegOperand { Reg: var r } && OwnedElements.FreshHere(defs, r))
        {
            VReg root = r;
            for (int hops = 0; hops < 6 && defs.Definition(root) is { Op: Opcode.Copy or Opcode.Trunc64 or Opcode.ZExt32, Operands: [RegOperand back] }; hops++) root = back.Reg;
            return (null, OwnedElements.Container(g, defs, root));
        }
        return (null, null);
    }

    /// <summary>Whether `i` addresses the holder: its slot, or a register of its block.</summary>
    private static bool Addresses(Defs defs, Operand o, FrameSlot? slot, HashSet<VReg>? block)
        => slot is not null && SlotOf(defs, o) == slot || block is not null && o is RegOperand { Reg: var r } && block.Contains(r);

    /// <summary>Whether a stash's add is handed an element: the element itself, or memory it was stored into.</summary>
    private static bool Parks(Function g, Defs defs, Instr add, HashSet<VReg> derived)
    {
        int value = OwnedElements.RoleOf("List", add.Callee!).Adds;
        if (add.Operands[value] is RegOperand v && derived.Contains(v.Reg)) return true;
        (FrameSlot? slot, HashSet<VReg>? block) = HolderOf(g, defs, add.Operands[value]);
        if (slot is null && block is null) return false;
        foreach (Block b in g.Blocks)
            foreach (Instr i in b.Instrs)
                if (i.Op == Opcode.Store && i.Operands.Count >= 2 && i.Operands[1] is RegOperand s && derived.Contains(s.Reg) && Addresses(defs, i.Operands[0], slot, block))
                    return true;
        return false;
    }

    /// <summary>
    /// Whether the memory an element was stored into for a stash is used for
    /// nothing else: written, read, zeroed, told to the collector, and handed
    /// to the stash's add (Analyse follows the element in it; this keeps an
    /// address of it from going anywhere Analyse does not look).
    /// </summary>
    private static bool ParkedOnly(Function g, Defs defs, Instr add, HashSet<VReg> derived)
    {
        int value = OwnedElements.RoleOf("List", add.Callee!).Adds;
        if (add.Operands[value] is RegOperand v && derived.Contains(v.Reg)) return true;
        (FrameSlot? slot, HashSet<VReg>? block) = HolderOf(g, defs, add.Operands[value]);
        HashSet<VReg> addresses = new();
        foreach (Block b in g.Blocks)
            foreach (Instr i in b.Instrs)
                if (i.Dest is not null && i.Op is Opcode.Copy && i.Operands is [var o] && (Addresses(defs, o, slot, block) || o is RegOperand { Reg: var a } && addresses.Contains(a)))
                    addresses.Add(i.Dest);
        foreach (Block b in g.Blocks)
            foreach (Instr i in b.Instrs)
                for (int k = 0; k < i.Operands.Count; k++)
                {
                    Operand o = i.Operands[k];
                    if (!Addresses(defs, o, slot, block) && !(o is RegOperand { Reg: var a } && addresses.Contains(a))) continue;
                    if (i.Op is Opcode.Copy && i.Dest is not null && addresses.Contains(i.Dest)) continue;
                    if (k == 0 && i.Op is Opcode.Store or Opcode.Load or Opcode.MemSet) continue;
                    if (k == 0 && i.Op == Opcode.Add && i.Operands[1] is ImmOperand && i.Dest is not null && OwnedElements.BarrierOnly(g, i.Dest)) continue;
                    if (i.Op == Opcode.Call && (IsCollectorNote(i.Callee) || i.Callee == Corsac.Lang.X86.MachineIntrinsics.KeepAlive)) continue;
                    if (ReferenceEquals(i, add) && k == value) continue;
                    // The block made for the tuple: its allocation.
                    if (block is not null && i.Op is Opcode.Trunc64 or Opcode.ZExt32 && i.Dest is not null && block.Contains(i.Dest)) continue;
                    return false;
                }
        return true;
    }

    /// <summary>
    /// WHAT IS READ OUT OF A STASH in a function, by register: the stash, the
    /// parameter it is reached from, and the offset in the tuple the value
    /// was loaded from (0 for a stash of elements). Its get_Item answers a
    /// tuple in the frame (the out operand, and the address it hands back),
    /// copied whole into more frame memory, and read word by word.
    /// </summary>
    private static Dictionary<VReg, (string Stash, ListPath? StashPath, long Offset, int Size, Instr Read)> Unparked(AccessScan s, string field)
    {
        Dictionary<VReg, (string, ListPath?, long, int, Instr)> found = new();
        Function g = s.F;
        Defs defs = s.Defs;
        foreach (Block b in g.Blocks)
            foreach (Instr call in b.Instrs)
            {
                if (call.Op != Opcode.Call || call.Callee is not { } callee || !OwnedElements.RoleOf("List", callee).Reads || call.Operands.Count < 2) continue;
                if (StashOf(defs, call.Operands[0], field) is not ({ } stash, var load)) continue;
                ListPath? root = RootPath(g, defs, load.Operands[0])?.Then(stash);
                if (call.Operands.Count == 2)
                {
                    if (call.Dest is not null)
                        foreach (VReg r in OwnedElements.Container(g, defs, call.Dest)) found[r] = (stash, root, 0, 0, call);
                    continue;
                }
                foreach ((Instr loaded, long offset, int size) in TupleReadsOrNull(g, defs, call) ?? new())
                    foreach (VReg r in OwnedElements.Container(g, defs, loaded.Dest!)) found[r] = (stash, root, offset, size, call);
            }
        return found;
    }

    /// <summary>
    /// The frame memory a tuple read out of a stash lands in (its out slot,
    /// and wherever it is copied whole), with every load from it; null when
    /// that memory is used any other way.
    /// </summary>
    private static List<(Instr Load, long Offset, int Size)>? TupleReadsOrNull(Function g, Defs defs, Instr call)
    {
        List<(FrameSlot? Slot, HashSet<VReg>? Block)> holders = new();
        HashSet<VReg> addresses = new();
        if (call.Operands.Count != 3) return null;
        var first = HolderOf(g, defs, call.Operands[2]);
        if (first.Slot is null && first.Block is null) return null;
        holders.Add(first);
        if (call.Dest is not null) addresses.Add(call.Dest);
        bool Held(Operand o) => o is RegOperand { Reg: var r } && addresses.Contains(r) || holders.Any(h => Addresses(defs, o, h.Slot, h.Block));
        for (bool grew = true; grew;)
        {
            grew = false;
            foreach (Block b in g.Blocks)
                foreach (Instr i in b.Instrs)
                {
                    if (i.Op is Opcode.Copy or Opcode.Trunc64 or Opcode.ZExt32 && i.Dest is not null && i.Operands is [var o] && Held(o) && addresses.Add(i.Dest)) grew = true;
                    if (i.Op == Opcode.MemCopy && i.Operands.Count == 3 && Held(i.Operands[1]) && !Held(i.Operands[0]))
                    {
                        var to = HolderOf(g, defs, i.Operands[0]);
                        if (to.Slot is null && to.Block is null) return null;
                        holders.Add(to);
                        grew = true;
                    }
                }
        }
        List<(Instr, long, int)> loads = new();
        foreach (Block b in g.Blocks)
            foreach (Instr i in b.Instrs)
                for (int k = 0; k < i.Operands.Count; k++)
                {
                    if (!Held(i.Operands[k])) continue;
                    if (ReferenceEquals(i, call)) continue;
                    if (i.Op is Opcode.Copy or Opcode.Trunc64 or Opcode.ZExt32 && i.Dest is not null && addresses.Contains(i.Dest)) continue;
                    if (k == 0 && i.Op == Opcode.Load && i.Dest is not null) { loads.Add((i, i.Offset, i.Size)); continue; }
                    if (i.Op == Opcode.MemCopy && k <= 1) continue;
                    if (k == 0 && i.Op is Opcode.Store or Opcode.MemSet) continue;
                    if (i.Op == Opcode.Call && (IsCollectorNote(i.Callee) || i.Callee == Corsac.Lang.X86.MachineIntrinsics.KeepAlive)) continue;
                    return null;
                }
        return loads;
    }

    /// <summary>
    /// THE STASHES: lists in fields, into which an element of the field's list
    /// is put, alone or in a tuple, to be put back into it. Each filled once
    /// as its object is made (FieldsFixed), used only by the list's own calls
    /// (FieldUses), and what is read out of it put back only into the list it
    /// was read from -- the stash and the list reached from one parameter by
    /// the same fields wherever it is put in or taken out -- or else used as
    /// a number: compared, counted with, handed to an index operand of a
    /// list's call. Every word read out and put back was put in, wherever
    /// anything is put into the stash, as an element read from that list
    /// (`parked`, judged by AccessorsProved), or the memory holding it zeroed.
    /// </summary>
    private bool StashProved(string field, HashSet<string> stashes, List<Parking> parked,
        Dictionary<string, AccessScan> scans, HashSet<string> addressed, HashSet<string> fixedFields, HashSet<Instr> kept,
        Func<AccessScan, VReg, ListPath?> collectionPath)
    {
        Module m = _module!;
        bool Refuse(Function g, string why)
        {
            _elementWhy = $"a read of {field}: {why}";
            OwnedElements.Say(g, $"through accessors of {field}: {why}");
            return false;
        }
        foreach (string stash in stashes)
        {
            fixedFields.Add(stash);
            if (OwnedElements.FieldUses(m, stash, "List", addressed) is not { } uses) return Refuse(m.Functions[0], $"stash {stash} used otherwise");
            // One place for the stash and one for the list, from the one parameter.
            (string Stash, string List)? pair = null;
            bool Same(ListPath stashPath, ListPath listPath)
            {
                if (stashPath.Param != listPath.Param) return false;
                if (pair is { } known) return known == (stashPath.Chain, listPath.Chain);
                pair = (stashPath.Chain, listPath.Chain);
                return true;
            }
            foreach (Parking p in parked)
                if (p.Stash == stash && !Same(p.StashPath, p.ListPath)) return Refuse(p.F, $"stash {stash} given at {p.Add.Line} an element of another list");
            // What is read out, and which words of it are put back.
            HashSet<(long Offset, int Size)> putBack = new();
            foreach ((Function g, var calls) in uses)
            {
                AccessScan s = scans.TryGetValue(g.Name, out AccessScan? sc) ? sc
                    : new AccessScan { F = g, Defs = new Defs(g), Values = new(), Writes = new RegisterWrites(g) };
                foreach ((Block _, Instr call, OwnedElements.Role role) in calls)
                {
                    kept.Add(call);
                    if (role.Enumerates || role.Views || role.OutSlot >= 0) return Refuse(g, $"stash {stash} walked");
                    if (!role.Reads) continue;
                    if (call.Operands.Count > 3 || call.Operands.Count == 3 && TupleReadsOrNull(g, s.Defs, call) is null)
                        return Refuse(g, $"stash {stash}'s tuple used otherwise at {call.Line}");
                }
                foreach ((VReg value, var (from, stashPath, offset, size, read)) in Unparked(s, field))
                {
                    if (from != stash) continue;
                    List<ListPath?> targets = new();
                    switch (Unparking(s, value, field, targets, collectionPath, 0))
                    {
                        case null: return Refuse(g, $"what is read out of stash {stash} at {read.Line} is used otherwise");
                        case true:
                            foreach (ListPath? target in targets)
                            {
                                if (stashPath is not { } sp || target is not { } t || !Same(sp, t))
                                    return Refuse(g, $"what is read out of stash {stash} at {read.Line} is put into another list");
                                foreach (string f in t.Chain.Split('/', StringSplitOptions.RemoveEmptyEntries)) fixedFields.Add(f);
                                foreach (string f in sp.Chain.Split('/', StringSplitOptions.RemoveEmptyEntries)) fixedFields.Add(f);
                            }
                            putBack.Add((offset, size));
                            break;
                    }
                }
            }
            if (putBack.Count == 0) continue;
            // What is put in, wherever it is.
            foreach ((Function g, var calls) in uses)
            {
                Defs defs = scans.TryGetValue(g.Name, out AccessScan? sc) ? sc.Defs : new Defs(g);
                foreach ((Block _, Instr call, OwnedElements.Role role) in calls)
                {
                    if (role.Adds <= 0) continue;
                    HashSet<VReg> mine = new();
                    foreach (Parking p in parked) if (ReferenceEquals(p.Add, call)) mine.UnionWith(p.Derived);
                    Operand value = call.Operands[role.Adds];
                    (FrameSlot? slot, HashSet<VReg>? block) = HolderOf(g, defs, value);
                    if (slot is null && block is null)
                    {
                        if (value is not RegOperand v || !mine.Contains(v.Reg)) return Refuse(g, $"stash {stash} given at {call.Line} what its list did not hold");
                        continue;
                    }
                    bool zeroed = false;
                    HashSet<long> words = new();
                    foreach (Block b in g.Blocks)
                        foreach (Instr i in b.Instrs)
                        {
                            if (i.Op == Opcode.MemSet && i.Operands.Count >= 1 && Addresses(defs, i.Operands[0], slot, block)) { zeroed = true; continue; }
                            if (i.Op == Opcode.MemCopy && i.Operands.Count >= 1 && Addresses(defs, i.Operands[0], slot, block)) return Refuse(g, $"stash {stash}'s tuple copied into at {i.Line}");
                            if (i.Op != Opcode.Store || i.Operands.Count < 2 || !Addresses(defs, i.Operands[0], slot, block)) continue;
                            foreach ((long offset, int size) in putBack)
                            {
                                long to = i.Offset + Math.Max(i.Size, 1), end = offset + Math.Max(size, 1);
                                if (to <= offset || i.Offset >= end) continue;
                                if (i.Offset != offset || i.Operands[1] is not RegOperand v || !mine.Contains(v.Reg))
                                    return Refuse(g, $"stash {stash} given at {i.Line} what its list did not hold");
                                words.Add(offset);
                            }
                        }
                    if (!zeroed && putBack.Any(w => !words.Contains(w.Offset))) return Refuse(g, $"stash {stash}'s tuple at {call.Line} not all written");
                }
            }
        }
        return true;
    }

    /// <summary>
    /// How a value read out of a stash is used: true when some use puts it
    /// back into a list (whose path is added to `targets`), false when it is
    /// only a number, null for anything else.
    /// </summary>
    private bool? Unparking(AccessScan s, VReg value, string field, List<ListPath?> targets, Func<AccessScan, VReg, ListPath?> collectionPath, int depth)
    {
        if (depth > 4) return null;
        bool put = false;
        Function g = s.F;
        foreach (Block b in g.Blocks)
            foreach (Instr i in b.Instrs)
                for (int k = 0; k < i.Operands.Count; k++)
                {
                    if (i.Operands[k] is not RegOperand { Reg: var r } || r != value) continue;
                    if (IrInfo.IsIntCompare(i.Op) || i.Op is Opcode.Branch or Opcode.Switch) continue;
                    if (i.Op is Opcode.Copy or Opcode.Trunc64 or Opcode.ZExt32 or Opcode.SExt32 && i.Dest is not null && s.Defs.IsSingle(i.Dest)
                        || i.Op is Opcode.Add or Opcode.Sub && i.Dest is not null && s.Defs.IsSingle(i.Dest) && i.Operands.Count == 2 && i.Operands[1 - k] is ImmOperand)
                    {
                        switch (Unparking(s, i.Dest!, field, targets, collectionPath, depth + 1))
                        {
                            case null: return null;
                            case true: put = true; break;
                        }
                        continue;
                    }
                    if (i.Op == Opcode.Call && i.Callee is { } callee && k > 0)
                    {
                        // Into the list: by its own call, or a setter.
                        if (s.Calls.FirstOrDefault(c => ReferenceEquals(c.Call, i)) is { Call: not null } known
                            && i.Operands[0] is RegOperand list && s.Values.Values.Contains(list.Reg))
                        {
                            if (k != known.Role.Adds) continue;
                            targets.Add(collectionPath(s, list.Reg));
                            put = true;
                            continue;
                        }
                        if (_adders?.TryGetValue(callee, out var adder) == true && _adderPaths?.TryGetValue(callee, out ListPath into) == true && k != adder.Receiver)
                        {
                            if (!adder.Values.Contains(k)) continue;
                            targets.Add(adder.Receiver < i.Operands.Count ? RootPath(g, s.Defs, i.Operands[adder.Receiver])?.Then(into.Chain) : null);
                            put = true;
                            continue;
                        }
                        // An index of any list's own call.
                        OwnedElements.Role role = OwnedElements.RoleOf("List", callee);
                        if (role.Known && k != role.Adds) continue;
                    }
                    return null;
                }
        return put;
    }

    /// <summary>The adders of the field being judged (AccessorsProved), for Unparking.</summary>
    private Dictionary<string, (int Receiver, HashSet<int> Values)>? _adders;

    /// <summary>
    /// WHETHER EVERY FIELD ON THE WAY TO THE LIST IS FILLED ONCE: each store
    /// into it, anywhere, is into an object as it is made -- one its function
    /// made, before that object is used any other way, once and not round a
    /// loop; or a constructor's own, every call of which is handed an object
    /// just made -- or, for the list's own field, a `??=` putting back what
    /// it read of the same object or filling it, from empty, with a list made
    /// there. Then an object reached from a parameter holds the same list as
    /// long as it lives, and what was read from it and is put back goes home.
    /// </summary>
    private bool FieldsFixed(HashSet<string> fields, string field, Dictionary<string, AccessScan> scans, HashSet<string> addressed)
    {
        Module m = _module!;
        Dictionary<string, Function> byName = new(StringComparer.Ordinal);
        foreach (Function g in m.Functions) byName[g.Name] = g;
        HashSet<string> constructors = ConstructorLike(m, addressed);
        foreach (Function g in m.Functions)
        {
            Defs? defs = null;
            foreach (Block b in g.Blocks)
                foreach (Instr st in b.Instrs)
                {
                    if (st.Field is not { } x || !fields.Contains(x)) continue;
                    if (st.Op == Opcode.Load) continue;
                    if (st.Op != Opcode.Store)
                    {
                        // Its address taken: written where no store shows.
                        _elementWhy = $"a read of {field}: {x}'s address taken at {st.Line}";
                        OwnedElements.Say(g, $"through accessors of {field}: {x}'s address taken at {st.Line}");
                        return false;
                    }
                    defs ??= scans.TryGetValue(g.Name, out AccessScan? sc) ? sc.Defs : new Defs(g);
                    if (x == field && scans.TryGetValue(g.Name, out AccessScan? s) && s.Values.Stores.Contains(st) && FillStore(s, st, field)) continue;
                    if (Initializing(g, defs, b, st, x, constructors, byName)) continue;
                    _elementWhy = $"a read of {field}: {x} stored at {st.Line} after its object was made";
                    OwnedElements.Say(g, $"through accessors of {field}: {x} stored at {st.Line} after its object was made");
                    return false;
                }
        }
        return true;
    }

    /// <summary>A `??=` of the list's field (FillStoreShape).</summary>
    private static bool FillStore(AccessScan s, Instr st, string field) => FillStoreShape(s.F, s.Defs, s.Writes, st) is not null;

    /// <summary>What a `??=` of a field stores (FillStoreShape).</summary>
    internal sealed class Fill
    {
        /// <summary>The reads of the field, of the same object, whose value is stored back.</summary>
        public readonly List<Instr> Restores = new();
        /// <summary>The objects made to fill it, each where the field was found null.</summary>
        public readonly List<Instr> Made = new();
        /// <summary>The variables on the way to the store.</summary>
        public readonly HashSet<VReg> Joins = new();
        /// <summary>The null edges the objects are made behind.</summary>
        public readonly HashSet<Block> Guards = new(ReferenceEqualityComparer.Instance);
    }

    /// <summary>
    /// A `??=` OF A FIELD -- `f ??= new T()`, lowered `nc = o.f; if (nc ==
    /// null) nc = new T(); o.f = nc` -- what is stored is, on every path, the
    /// field's own value read from the same object (put back as it was), or
    /// an object made where that read was found null. Null for any other store.
    /// </summary>
    internal static Fill? FillStoreShape(Function f, Defs defs, RegisterWrites writes, Instr st)
    {
        if (st.Op != Opcode.Store || st.Field is not { } field || st.Operands.Count < 2
            || OwnerOf(defs, st.Operands[0]) is not { } owner || st.Operands[1] is not RegOperand value) return null;
        Fill fill = new();
        HashSet<VReg> seen = new();
        Stack<(VReg Reg, Instr? Write)> work = new();
        work.Push((value.Reg, null));
        while (work.TryPop(out var item))
        {
            (VReg r, Instr? write) = item;
            if (!seen.Add(r) || seen.Count > 32) { if (seen.Count > 32) return null; continue; }
            if (f.Params.Contains(r)) return null;
            if (defs.IsSingle(r) && defs.Definition(r) is { } d)
            {
                if (d.Op == Opcode.Load && d.Field == field)
                {
                    if (!ReferenceEquals(OwnerOf(defs, d.Operands[0]), owner)) return null;
                    fill.Restores.Add(d);
                    continue;
                }
                if (d.Op == Opcode.Call && Escape.IsAllocator(d.Callee))
                {
                    // Copied into the variable where the field was found empty.
                    if (write is null) return null;
                    Block where = f.Blocks.First(x => x.Instrs.Contains(write));
                    if (NullGuard(f, defs, where, owner, field) is not { } guard) return null;
                    fill.Guards.Add(guard);
                    fill.Made.Add(d);
                    continue;
                }
                if (d.Op is Opcode.Copy or Opcode.Trunc64 or Opcode.ZExt32 && d.Operands[0] is RegOperand back) { work.Push((back.Reg, write)); continue; }
                return null;
            }
            if (!writes.TryGetValue(r, out WriteList ws)) return null;
            fill.Joins.Add(r);
            foreach (Instr w in ws)
            {
                if (w.Op != Opcode.Copy || w.Operands is not [RegOperand from]) return null;
                work.Push((from.Reg, w));
            }
        }
        return fill.Restores.Count > 0 ? fill : null;
    }

    /// <summary>A store into an object as it is made (FieldsFixed).</summary>
    private static bool Initializing(Function g, Defs defs, Block at, Instr st, string x, HashSet<string> constructors, Dictionary<string, Function> byName)
    {
        Operand baseOperand = st.Operands[0];
        // A constructor's own `this`: the only store of the field in it, not
        // round a loop, and no constructor it calls on `this` storing the
        // field again.
        if (baseOperand is RegOperand { Reg: var self } && g.Params.Count > 0 && self == g.Params[0] && defs.Count(self) == 1)
        {
            if (!constructors.Contains(g.Name)) return false;
            int stores = 0;
            foreach (Block b in g.Blocks)
                foreach (Instr i in b.Instrs)
                {
                    if (i.Op == Opcode.Store && i.Field == x) stores++;
                    if (i.Op == Opcode.Call && i.Operands.Count > 0 && i.Operands[0] is RegOperand { Reg: var passed } && passed == self && i.Callee is { } c
                        && StoresOnThis(c, x, byName, 0)) return false;
                }
            return stores == 1 && !defs.Cfg.Reaches(at, at);
        }
        // An object this function made: an allocation, or one promoted to the frame.
        object? origin = null;
        Block? made = null;
        if (SlotOf(defs, baseOperand) is { } slot)
        {
            origin = slot;
            made = g.Blocks.FirstOrDefault(b => b.Instrs.Any(i => i.Op == Opcode.MemSet && i.Operands.Count > 0 && SlotOf(defs, i.Operands[0]) == slot));
        }
        else if (baseOperand is RegOperand { Reg: var r } && OwnedElements.FreshHere(defs, r))
        {
            VReg root = r;
            for (int hops = 0; hops < 6 && defs.Definition(root) is { Op: Opcode.Copy or Opcode.Trunc64 or Opcode.ZExt32, Operands: [RegOperand back] }; hops++) root = back.Reg;
            origin = root;
            made = g.Blocks.First(b => b.Instrs.Contains(defs.Definition(root)!));
        }
        if (origin is null || made is null) return false;
        HashSet<VReg>? names = origin is VReg v0 ? OwnedElements.Container(g, defs, v0) : null;
        bool Same(Operand o) => origin is FrameSlot fs ? SlotOf(defs, o) == fs : o is RegOperand { Reg: var q } && names!.Contains(q);
        int count = 0;
        foreach (Block b in g.Blocks)
            foreach (Instr i in b.Instrs)
                if (i.Op == Opcode.Store && i.Field == x && Same(i.Operands[0])) count++;
        if (count != 1 || ReachesAvoiding(defs.Cfg, at, at, made)) return false;
        // Nothing done with the object before it is filled but filling it.
        int stIndex = at.Instrs.IndexOf(st);
        bool loops = defs.Cfg.Reaches(at, at);
        foreach (Block b in g.Blocks)
        {
            if (!ReferenceEquals(b, at) && !defs.Cfg.Reaches(b, at)) continue;
            int end = ReferenceEquals(b, at) && !loops ? stIndex : b.Instrs.Count;
            for (int k = 0; k < end; k++)
            {
                Instr i = b.Instrs[k];
                for (int o = 0; o < i.Operands.Count; o++)
                {
                    if (!Same(i.Operands[o])) continue;
                    if (o == 0 && i.Op is Opcode.Store or Opcode.Load or Opcode.MemSet) continue;
                    if (i.Op is Opcode.Copy or Opcode.Trunc64 or Opcode.ZExt32 && i.Dest is not null) continue;
                    if (o == 0 && i.Op == Opcode.Add && i.Operands[1] is ImmOperand && i.Dest is not null && OwnedElements.BarrierOnly(g, i.Dest)) continue;
                    if (i.Op == Opcode.Call && (IsCollectorNote(i.Callee) || i.Callee == Corsac.Lang.X86.MachineIntrinsics.KeepAlive)) continue;
                    return false;
                }
            }
        }
        return true;
    }

    /// <summary>Whether a constructor, or one it calls on its `this`, stores the field there.</summary>
    private static bool StoresOnThis(string name, string x, Dictionary<string, Function> byName, int depth)
    {
        if (depth > 4 || !byName.TryGetValue(name, out Function? g)) return true;
        foreach (Block b in g.Blocks)
            foreach (Instr i in b.Instrs)
            {
                if (i.Op == Opcode.Store && i.Field == x) return true;
                if (i.Op == Opcode.Call && i.Callee is { } c && i.Operands.Count > 0 && i.Operands[0] is RegOperand { Reg: var r } && g.Params.Count > 0 && r == g.Params[0]
                    && StoresOnThis(c, x, byName, depth + 1)) return true;
            }
        return false;
    }

    /// <summary>
    /// CONSTRUCTORS, as the owned-field rules find them: functions every call
    /// of which passes as its first argument an object just made -- nothing
    /// between but its descriptor stamped -- or the caller's own first
    /// argument, the caller being one.
    /// </summary>
    private static HashSet<string> ConstructorLike(Module m, HashSet<string> addressed)
    {
        Dictionary<string, List<(Function G, Block B, int At)>> sites = new(StringComparer.Ordinal);
        foreach (Function g in m.Functions)
            foreach (Block b in g.Blocks)
                for (int k = 0; k < b.Instrs.Count; k++)
                    if (b.Instrs[k] is { Op: Opcode.Call, Callee: string callee })
                    {
                        if (!sites.TryGetValue(callee, out var list)) sites[callee] = list = new();
                        list.Add((g, b, k));
                    }
        HashSet<string> made = new(StringComparer.Ordinal);
        bool FreshFirst(Function g, Block at, int index)
        {
            if (at.Instrs[index].Operands is not [RegOperand r, ..]) return false;
            if (g.Params.Count > 0 && r.Reg == g.Params[0]) return made.Contains(g.Name);
            int from = -1;
            HashSet<VReg> names = new() { r.Reg };
            for (int k = index - 1; k >= 0; k--)
            {
                Instr i = at.Instrs[k];
                if (i.Dest is null || !names.Contains(i.Dest)) continue;
                if (i.Op == Opcode.Call && IsAllocator(i.Callee)) { from = k; break; }
                if (i.Op is Opcode.Copy or Opcode.Trunc64 or Opcode.ZExt32 && i.Operands[0] is RegOperand back) { names.Add(back.Reg); continue; }
                return false;
            }
            if (from < 0) return false;
            for (int k = from + 1; k < index; k++)
            {
                Instr i = at.Instrs[k];
                if (!i.Operands.Any(o => o is RegOperand u && names.Contains(u.Reg))) continue;
                if (i.Op is Opcode.Copy or Opcode.Trunc64 or Opcode.ZExt32 && i.Dest is not null) { names.Add(i.Dest); continue; }
                if (i.Op == Opcode.Store && i.Operands[1] is SymOperand) continue;
                return false;
            }
            return true;
        }
        for (bool grew = true; grew;)
        {
            grew = false;
            foreach (Function g in m.Functions)
            {
                if (made.Contains(g.Name) || g.Params.Count == 0 || addressed.Contains(g.Name) || !sites.TryGetValue(g.Name, out var calls) || calls.Count == 0) continue;
                if (calls.All(c => FreshFirst(c.G, c.B, c.At))) { made.Add(g.Name); grew = true; }
            }
        }
        return made;
    }

    /// <summary>The call that gives back an owning collection's elements, before whatever frees the collection.</summary>
    private static void AppendElementFree(Function f, List<Instr> output, Instr made, VReg pointer, int line)
    {
        if (made.Field != Instr.OwnsElements) return;
        VReg argument = Word(f, output, pointer, line, "elementsOf");
        output.Add(new Instr { Op = Opcode.Call, Callee = OwnedElements.Freer, Operands = { new RegOperand(argument) }, Line = line });
    }
}
