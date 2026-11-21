#nullable enable
using Corsac.Lang.Ir;

namespace Corsac.Lang.Opt;

using Block = Corsac.Lang.Ir.Block;

/// <summary>
/// A SUM OF A LOOP'S COUNTER AND WHAT THE LOOP NEVER CHANGES, KEPT AS IT
/// GOES: `d = i + a` in a natural loop, where i changes in the loop only by
/// `i = i + c` (c a number) and a only outside it. A register j is made
/// `i + a` in the loop's preheader and stepped by c just after i is, so it
/// is `i + a` wherever the loop reads it -- 32-bit sums wrap alike -- and d
/// is j copied. A span's element at `from + i`, read every lap, was `mov
/// t, from ; add t, i`: now one `inc j` beside `inc i`.
///
/// Only an int sum, in a loop with one way in from outside (a block made on
/// that edge where it is a guard's branch), no handler in the function, no
/// async body; one such register a loop, held round it.
/// </summary>
public sealed class StrengthReduce : IPass
{
    public string Name => "strength-reduce";

    public void Run(Function function)
    {
        if (function.Async is not null || function.Blocks.Count == 0 || function.Blocks.Any(b => b.IsLandingPad)) return;
        Cfg cfg = PipelineAnalyses.CfgOf(function);
        if (cfg.Roots.Count != 1) return;
        bool changed = false;
        foreach (Block header in function.Blocks.ToArray())
        {
            // (A way in made for a loop before changes the graph: read anew.)
            cfg = PipelineAnalyses.CfgOf(function);
            Stack<Block>? todo = null;
            foreach (Block p in cfg.Preds(header))
                if (cfg.Dominates(header, p)) (todo ??= new()).Push(p);
            if (todo is null) continue;
            HashSet<Block> loop = new() { header };
            while (todo.TryPop(out Block? block))
                if (loop.Add(block)) foreach (Block pred in cfg.Preds(block)) todo.Push(pred);
            if (loop.Any(b => !cfg.Dominates(header, b))) continue;
            Block? preheader = null;
            int outside = 0;
            foreach (Block p in cfg.Preds(header))
                if (!loop.Contains(p) && outside++ == 0) preheader = p;
            if (outside != 1 || preheader!.Terminator is not { } leaving) continue;
            bool own = leaving.Op == Opcode.Jump && cfg.Succs(preheader).Count == 1;

            // Every register the loop writes, and how often.
            Dictionary<VReg, int> writes = new();
            foreach (Block b in loop)
                foreach (Instr i in b.Instrs)
                    if (i.Dest is { } d) writes[d] = writes.GetValueOrDefault(d) + 1;

            // The counters: written once in the loop, by a number added --
            // `i = i + c`, or as lowering writes `i++`, `t = i + c ; i = t`
            // (t written once in the loop): stepped where i is written.
            Dictionary<VReg, Instr> made1 = new();
            foreach (Block b in loop)
                foreach (Instr i in b.Instrs)
                    if (i.Dest is { } d && writes[d] == 1) made1[d] = i;
            Dictionary<VReg, (Instr Step, Block At, long By)> counters = new();
            foreach (Block b in loop)
                foreach (Instr i in b.Instrs)
                {
                    if (i.Dest is not { Type: IrType.I32 } v || writes[v] != 1) continue;
                    if (Step(i, v) is long by && by != 0) counters[v] = (i, b, by);
                    // (t made just before the copy, in its block: i is t
                    // as made this lap, and no way reaches the copy past it.)
                    else if (i.Op == Opcode.Copy && i.Operands is [RegOperand { Reg: var t }] && made1.TryGetValue(t, out Instr? sum)
                        && Step(sum, v) is long by2 && by2 != 0 && b.Instrs.IndexOf(i) is int at && at > 0 && ReferenceEquals(b.Instrs[at - 1], sum))
                        counters[v] = (i, b, by2);
                }
            if (counters.Count == 0) continue;

            // A NUMBER THE LOOP READS AND NEVER CHANGES: a load written once in
            // it, through a struct the function was given by value (its own
            // copy, never null: Function.ValueParams) or a frame slot, in a
            // loop that stores nothing and calls nothing -- read once where
            // the loop is entered instead, as often as the loop would have,
            // which cannot fault. A span's `from`, read every lap.
            bool still = loop.All(b => b.Instrs.All(i => i.Op is Opcode.Load or Opcode.ArrayLength or Opcode.Branch or Opcode.Jump or Opcode.Switch
                || IrInfo.IsPure(i)));
            Dictionary<VReg, Instr> steady = new();
            if (still)
                foreach (Block b in loop)
                    foreach (Instr i in b.Instrs)
                        if (i.Op == Opcode.Load && !i.Device && i.Dest is { Type: IrType.I32 } read && writes[read] == 1 && i.Operands.Count == 1
                            && (i.Operands[0] is SlotOperand
                                || i.Operands[0] is RegOperand { Reg: var by } && !writes.ContainsKey(by) && function.ValueParams?.ContainsKey(by) == true && function.Params.Contains(by)))
                            steady[read] = i;
            // NOT A SUM ONLY AN ADDRESS IS MADE OF: x86 adds a base and an index
            // in the address itself, and a register held round the loop for
            // one (`items + from + i`) is a register for nothing. A sum
            // something else reads too -- a bounds check's compare -- is one.
            Dictionary<VReg, List<Instr>>? uses = null;
            bool AddressOnly(VReg r, int depth)
            {
                if (uses is null)
                {
                    uses = new();
                    foreach (Block b in function.Blocks)
                        foreach (Instr i in b.Instrs)
                            foreach (Operand o in i.Operands)
                                if (o is RegOperand { Reg: var reg }) (uses.TryGetValue(reg, out var l) ? l : uses[reg] = new()).Add(i);
                }
                if (!uses.TryGetValue(r, out var readers)) return true;
                foreach (Instr u in readers)
                {
                    if (u.Op is Opcode.Load && u.Operands[0] is RegOperand { Reg: var at } && at == r) continue;
                    if (u.Op is Opcode.Store && u.Operands[0] is RegOperand { Reg: var to } && to == r && !(u.Operands.Count > 1 && u.Operands[1] is RegOperand { Reg: var v } && v == r)) continue;
                    if (depth > 0 && u.Op == Opcode.Add && u.Dest is { } next && next != r && AddressOnly(next, depth - 1)) continue;
                    return false;
                }
                return true;
            }
            bool Fixed(VReg r) => r.Type == IrType.I32 && (!writes.ContainsKey(r) || steady.ContainsKey(r));
            // A way in of the loop's own, made where the one there is a
            // branch (a guard: `i < n` before the first lap): a block on that
            // edge, jumping to the header.
            Block? entry = own ? preheader : null;
            Block Entry()
            {
                if (entry is not null) return entry;
                Block made = function.NewBlock("pre");
                function.Blocks.Remove(made);
                function.Blocks.Insert(function.Blocks.IndexOf(header), made);
                made.Instrs.Add(new Instr { Op = Opcode.Jump, InitialTargets = new[] { header } });
                for (int t = 0; t < leaving.TargetCount; t++)
                    if (ReferenceEquals(leaving.Targets[t], header)) leaving.SetTarget(t, made);
                if (ReferenceEquals(leaving.Default, header)) leaving.Default = made;
                return entry = made;
            }

            // The sums, each pair once: `d = i + a`, a written nowhere in the loop.
            Dictionary<(VReg Counter, VReg Fixed), VReg> made = new();
            foreach (Block b in loop.OrderBy(b => function.Blocks.IndexOf(b)).ToArray())
                for (int k = 0; k < b.Instrs.Count; k++)
                {
                    Instr i = b.Instrs[k];
                    if (i.Op != Opcode.Add || i.Dest is not { Type: IrType.I32 } sum || i.Operands.Count != 2
                        || i.Operands[0] is not RegOperand { Reg: var x } || i.Operands[1] is not RegOperand { Reg: var y }) continue;
                    (VReg Counter, VReg Fixed)? pair = counters.ContainsKey(x) && Fixed(y) ? (x, y)
                        : counters.ContainsKey(y) && Fixed(x) ? (y, x) : null;
                    if (pair is not { } p || ReferenceEquals(i, counters[p.Counter].Step) || AddressOnly(sum, 3)) continue;
                    if (!made.TryGetValue(p, out VReg? j))
                    {
                        // One a loop: a register held round it, on a machine
                        // of six.
                        if (made.Count == 1) continue;
                        j = function.NewReg(IrType.I32);
                        made[p] = j;
                        // j = i + a where the loop is entered (a read there,
                        // where the loop reads it: a load of its own).
                        List<Instr> pre = Entry().Instrs;
                        VReg a = p.Fixed;
                        if (steady.TryGetValue(a, out Instr? load))
                        {
                            a = function.NewReg(IrType.I32);
                            Instr again = load.Clone();
                            again.Dest = a;
                            pre.Insert(pre.Count - 1, again);
                        }
                        pre.Insert(pre.Count - 1, new Instr { Op = Opcode.Add, Dest = j, Operands = { RegOperand.Of(p.Counter), RegOperand.Of(a) }, Line = i.Line });
                        // And stepped as i is, just after it.
                        (Instr step, Block at, long by) = counters[p.Counter];
                        int s = at.Instrs.IndexOf(step);
                        at.Instrs.Insert(s + 1, new Instr { Op = Opcode.Add, Dest = j, Operands = { RegOperand.Of(j), new ImmOperand(by, IrType.I32) }, Line = step.Line });
                        if (ReferenceEquals(at, b) && s < k) k++;
                    }
                    b.Instrs[k] = new Instr { Op = Opcode.Copy, Dest = sum, Operands = { RegOperand.Of(j) }, Line = i.Line };
                    changed = true;
                }
        }
        if (changed) function.Edited();
    }


    /// <summary>`v = v + c` or `v = v - c` (c a number): c, negated for the subtraction; null for anything else.</summary>
    private static long? Step(Instr i, VReg v)
    {
        if (i.Operands.Count != 2) return null;
        if (i.Op == Opcode.Add && i.Operands[0] is RegOperand { Reg: var a } && a == v && i.Operands[1] is ImmOperand { Value: var c }) return c;
        if (i.Op == Opcode.Add && i.Operands[1] is RegOperand { Reg: var b } && b == v && i.Operands[0] is ImmOperand { Value: var d }) return d;
        if (i.Op == Opcode.Sub && i.Operands[0] is RegOperand { Reg: var e } && e == v && i.Operands[1] is ImmOperand { Value: var f } && f != long.MinValue) return -f;
        return null;
    }
}
