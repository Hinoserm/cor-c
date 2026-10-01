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
        Dictionary<string, DataItem> items = new(StringComparer.Ordinal);
        foreach (DataItem d in m.Data)
            if (d.ReadOnly && !d.Zero) items[d.Name] = d;
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
        if (!any) return;
        defs = new(f, buildCfg: false);

        // 2. A slot read from read-only data at a relocation.
        foreach (Block b in f.Blocks)
            for (int k = 0; k < b.Instrs.Count; k++)
            {
                Instr i = b.Instrs[k];
                if (i.Op != Opcode.Load || i.Size != word || i.Dest is null || i.Dest.Type != IrTypes.Word) continue;
                if (Symbol(i.Operands[0]) is not { } table || !items.TryGetValue(table.Name, out DataItem? item)) continue;
                long at = table.Offset + i.Offset;
                if (item.Relocs.FirstOrDefault(rel => rel.Offset == at) is not { Symbol: { } named } exact) continue;
                b.Instrs[k] = new Instr { Op = Opcode.Copy, Dest = i.Dest, Line = i.Line, Operands = { new SymOperand(named, exact.Addend) } };
            }
        defs = new(f, buildCfg: false);

        // 3. The call through it.
        foreach (Block b in f.Blocks)
            for (int k = 0; k < b.Instrs.Count; k++)
            {
                Instr i = b.Instrs[k];
                if (i.Op != Opcode.CallIndirect || i.Operands.Count < 1) continue;
                if (Symbol(i.Operands[0]) is not { Offset: 0 } target || !target.Name.StartsWith("m_", StringComparison.Ordinal)) continue;
                Instr call = new() { Op = Opcode.Call, Dest = i.Dest, Line = i.Line, Callee = target.Name };
                for (int a = 1; a < i.Operands.Count; a++) call.Operands.Add(i.Operands[a]);
                b.Instrs[k] = call;
                Resolved++;
            }
    }

    private static bool KeepsFields(string? callee) =>
        callee is not null && (Escape.IsAllocator(callee) || Escape.NeverWritesFields(callee) || Escape.IsCollectorNote(callee)
            || callee == "m_Runtime_InvalidCastTo_2_V$Any_V$String" || callee.StartsWith("__x86.i.", StringComparison.Ordinal));

    private static readonly string? Trace = Environment.GetEnvironmentVariable("CORC_TRACE_FORWARD");

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
                    var touching = stores.Where(st => st.At < at + l.Size && at < st.At + st.Size && Reaches((st.B, st.I), lb, li)).ToList();
                    // THE VTABLE, wherever it is read: the object's one store of
                    // word 0 is its making's, and a load through it anywhere --
                    // a finally's Dispose, which the graph shows no edge into --
                    // reads that.
                    if (at == 0 && l.Size == word)
                    {
                        var all = stores.Where(st => st.At < word && 0 < st.At + st.Size).ToList();
                        if (all.Count == 1 && all[0].At == 0 && all[0].Value is SymOperand vt)
                        {
                            lb.Instrs[li] = new Instr { Op = Opcode.Copy, Dest = l.Dest, Line = l.Line, Operands = { new SymOperand(vt.Name, vt.Offset) } };
                            continue;
                        }
                    }
                    if (touching.Count != 1) { if (trace) Console.Error.WriteLine($"forward {f.Name} {l}: {touching.Count} stores"); continue; }
                    var s0 = touching[0];
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
                    if (!vtableWord && writers.FirstOrDefault(w => Reaches(w, lb, li)) is { B: not null } bad)
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

    internal static Dictionary<string, DataItem> ReadOnlyItems(Module m)
    {
        Dictionary<string, DataItem> items = new(StringComparer.Ordinal);
        foreach (DataItem d in m.Data)
            if (d.ReadOnly && !d.Zero) items[d.Name] = d;
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
        IPass[] after = { new ConstantAndCopyPropagation(), new ConstantFold(), new BranchSimplify(), new DeadCodeElimination() };
        foreach (Function f in m.Functions)
        {
            if (f.Async is not null) continue;
            // Twice round: a test folded to a constant is a register until it
            // is propagated into the branch that reads it, and the branch gone,
            // the enumerator is the one object, whose vtable can then be read.
            for (int round = 0; round < 3; round++)
            {
                devirtualize.Run(f, items);
                foreach (IPass p in after) p.Run(f);
            }
            if (Environment.GetEnvironmentVariable("CORC_DUMP_FUNCTION") == f.Name)
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
                && int.TryParse(escaped.AsSpan(k + 1, 4), System.Globalization.NumberStyles.HexNumber, null, out int c))
            {
                sb.Append((char)c);
                k += 4;
            }
            else sb.Append(escaped[k]);
        }
        return sb.ToString();
    }
}
