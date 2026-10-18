#nullable enable
using Corsac.Lang.Ir;

namespace Corsac.Lang.Opt;

using Block = Corsac.Lang.Ir.Block;

/// <summary>
/// THE VTABLE SLOTS A FUNCTION READS, for the sweep that keeps a method a
/// descriptor holds only while something can call it through its slot
/// (Inline.RemoveDeadFunctions). A slot is read by a load at a constant
/// offset from a method table: an object's first word (a load at offset 0),
/// a descriptor's symbol at its methods, or either moved by a constant or
/// masked, through copies -- however the passes have moved the virtual call
/// lowering wrote (hoisted, shared, merged into one call). Each read is
/// named by the class it was made through, where it was made for virtual
/// calls of a class and nothing else (Instr.DispatchType), and by object --
/// every descriptor -- otherwise. A load at offset 0 of anything is taken
/// for a method table, so a field read through a reference read from memory
/// counts as a slot's read too: more is kept, never less.
/// </summary>
internal static class VirtualSlotUses
{
    /// <summary>A read of every descriptor's slot, whatever its class.</summary>
    public const string Any = Lower.Lowering.ObjectDispatch;

    /// <summary>
    /// Each slot `f` reads, as the class the read is made through (or
    /// <see cref="Any"/>) and the slot's offset from the method table's start.
    /// </summary>
    public static void Of(Function f, Action<string, long> use)
    {
        int w = Target.Current.WordSize;
        Dictionary<VReg, List<Instr>> defs = new();
        Dictionary<VReg, List<Instr>> uses = new();
        foreach (Block b in f.Blocks)
            foreach (Instr i in b.Instrs)
            {
                if (i.Dest is { } d) (defs.TryGetValue(d, out List<Instr>? l) ? l : defs[d] = new()).Add(i);
                foreach (Operand o in i.Operands)
                    if (o is RegOperand { Reg: var r }) (uses.TryGetValue(r, out List<Instr>? l) ? l : uses[r] = new()).Add(i);
            }
        Dictionary<VReg, List<(string? Table, long At)>?> tables = new();
        foreach (Block b in f.Blocks)
            foreach (Instr i in b.Instrs)
            {
                if (i.Op != Opcode.Load || i.Size != w || i.Operands.Count < 1 || i.Dest is null) continue;
                List<(string? Table, long At)>? from = Table(i.Operands[0]);
                if (from is null) continue;
                string? through = null;
                foreach (var (table, at) in from)
                {
                    long slot = at + i.Offset;
                    if (slot < 0 || slot % w != 0) continue;
                    use(table ?? (through ??= Through(i.Dest)), slot);
                }
            }

        // Where an operand points in a method table: each (descriptor or
        // null for an object's own, offset from the methods' start) it may
        // be, or null when it is none.
        List<(string? Table, long At)>? Table(Operand o)
        {
            if (o is SymOperand { Name: var name, Offset: var addend })
                return Escape.IsDescriptor(name) ? new() { (name, addend - Target.Current.DescriptorBytes) } : null;
            return o is RegOperand { Reg: var r } ? Register(r) : null;
        }

        List<(string? Table, long At)>? Register(VReg r)
        {
            if (tables.TryGetValue(r, out List<(string?, long)>? known)) return known;
            tables[r] = null;     // a cycle through phis or copies adds nothing
            if (!defs.TryGetValue(r, out List<Instr>? written)) return null;
            List<(string? Table, long At)>? all = null;
            foreach (Instr d in written)
            {
                IEnumerable<(string? Table, long At)>? some = d.Op switch
                {
                    Opcode.Load when d.Offset == 0 && d.Size == w && d.Operands.Count == 1 && d.Operands[0] is RegOperand => new[] { ((string?)null, 0L) },
                    Opcode.Copy or Opcode.Phi => d.Operands.SelectMany(o => Table(o) ?? new()).ToList(),
                    Opcode.And when d.Operands is [var x, ImmOperand] => Table(x),
                    Opcode.And when d.Operands is [ImmOperand, var x] => Table(x),
                    Opcode.Add when d.Operands is [var x, ImmOperand { Value: var k }] => Table(x)?.Select(t => (t.Table, t.At + k)),
                    Opcode.Add when d.Operands is [ImmOperand { Value: var k }, var x] => Table(x)?.Select(t => (t.Table, t.At + k)),
                    Opcode.Sub when d.Operands is [var x, ImmOperand { Value: var k }] => Table(x)?.Select(t => (t.Table, t.At - k)),
                    _ => null,
                };
                if (some is not null) (all ??= new()).AddRange(some);
            }
            return tables[r] = all is { Count: > 0 } ? all.Distinct().ToList() : null;
        }

        // The class a method loaded into `r` is called through: the one class
        // every call of it names (Lowering's DispatchType), when it is used
        // for nothing but calls; any other use, or an interface's, is any.
        string Through(VReg r)
        {
            string? only = null;
            HashSet<VReg> seen = new() { r };
            Stack<VReg> work = new();
            work.Push(r);
            while (work.Count > 0)
            {
                if (!uses.TryGetValue(work.Pop(), out List<Instr>? used)) continue;
                foreach (Instr u in used)
                {
                    if (u.Op == Opcode.Copy || u.Op == Opcode.Phi)
                    {
                        if (u.Dest is { } next && seen.Add(next)) work.Push(next);
                        continue;
                    }
                    if (u.Op != Opcode.CallIndirect || u.Operands[0] is not RegOperand { Reg: var callee } || !seen.Contains(callee)
                        || u.Operands.Skip(1).Any(a => a is RegOperand { Reg: var arg } && seen.Contains(arg))
                        || u.DispatchType is not string named || !named.StartsWith("t_", StringComparison.Ordinal)
                        || only is not null && only != named)
                        return Any;
                    only = named;
                }
            }
            return only ?? Any;
        }
    }
}
