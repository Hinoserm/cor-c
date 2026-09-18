#nullable enable
using Corsac.Lang.Ir;

namespace Corsac.Lang.Opt;
using Block = Corsac.Lang.Ir.Block;

/// <summary>Propagate integer predicates along unambiguous dominated edges.
/// Facts describe captured values, not mutable variable names. Staged batch
/// pass: no memory speculation, CFG rewriting, or assumption of SSA.</summary>
public sealed class EdgePredicateSimplify : IPass
{
    public string Name => "edge-predicates";
    private sealed record Fact(Instr Compare, bool Truth, Block Origin, int Index);

    public void Run(Function function)
    {
        if (function.Async is not null) return;
        Cfg cfg = new(function);
        if (cfg.Roots.Count != 1) return;
        Defs defs = new(cfg);
        Dictionary<Block, List<Fact>> outgoing = new();
        foreach (Block block in cfg.ReversePostorder)
        {
            List<Fact> facts = new();
            var predecessors = cfg.Preds(block);
            if (!cfg.IsRoot(block) && predecessors.Count == 1 && cfg.Dominates(predecessors[0], block))
            {
                Block previous = predecessors[0];
                if (outgoing.TryGetValue(previous, out var inherited)) facts.AddRange(inherited);
                Instr? branch = previous.Terminator;
                if (branch?.Op == Opcode.Branch && branch.Targets.Count == 2
                    && branch.Targets[0] != branch.Targets[1]
                    && branch.Operands[0] is RegOperand condition && defs.Site(condition.Reg) is { } site
                    && cfg.Dominates(site.Block, previous))
                {
                    Instr compare = site.Block.Instrs[site.Index];
                    Fact fact = new(compare, branch.Targets[0] == block, site.Block, site.Index);
                    if (IrInfo.IsIntCompare(compare.Op) && compare.Operands.Count == 2
                        && compare.Operands[0].Type.IsInt() && compare.Operands[0].Type == compare.Operands[1].Type
                        && Fresh(fact, previous, previous.Instrs.Count - 1, defs))
                    {
                        if (facts.Count == 64) facts.RemoveAt(0);
                        facts.Add(fact);
                    }
                }
            }
            for (int index = 0; index < block.Instrs.Count; index++)
            {
                Instr instruction = block.Instrs[index];
                if (!IrInfo.IsIntCompare(instruction.Op) || instruction.Dest is null
                    || instruction.Operands.Count != 2 || !instruction.Operands[0].Type.IsInt()
                    || instruction.Operands[0].Type != instruction.Operands[1].Type) continue;
                bool? result = Fold(instruction, facts.Where(f => Fresh(f, block, index, defs)));
                if (result is { } answer)
                    block.Instrs[index] = IrInfo.CopyOf(instruction, new ImmOperand(answer ? 1 : 0, IrType.I32));
            }
            outgoing[block] = facts;
        }
    }

    private static bool Fresh(Fact fact, Block block, int index, Defs defs)
    {
        foreach (Operand operand in fact.Compare.Operands)
            if (operand is RegOperand r && !defs.CanForward(r.Reg, fact.Origin, fact.Index, block, index)) return false;
        return true;
    }

    // Relation sets: less=1, equal=2, greater=4.
    private static int Relations(Opcode op) => op switch
    {
        Opcode.Eq => 2, Opcode.Ne => 5,
        Opcode.LtS or Opcode.LtU => 1, Opcode.LeS or Opcode.LeU => 3,
        Opcode.GtS or Opcode.GtU => 4, Opcode.GeS or Opcode.GeU => 6,
        _ => 7,
    };
    private static int Domain(Opcode op) => op is Opcode.Eq or Opcode.Ne ? 0
        : op is Opcode.LtS or Opcode.LeS or Opcode.GtS or Opcode.GeS ? 1 : 2;
    private static int Swap(int relations) => ((relations & 1) << 2) | (relations & 2) | ((relations & 4) >> 2);
    private static bool Same(Operand a, Operand b) => a.Type == b.Type && (a, b) switch
    {
        (RegOperand x, RegOperand y) => x.Reg == y.Reg,
        (ImmOperand x, ImmOperand y) => (unchecked((ulong)x.Value) & IntegerBitFacts.Mask(x.Type))
            == (unchecked((ulong)y.Value) & IntegerBitFacts.Mask(y.Type)),
        _ => false,
    };

    private static bool? Fold(Instr test, IEnumerable<Fact> facts)
    {
        int wanted = Relations(test.Op), available = 7, domain = Domain(test.Op);
        Operand left = test.Operands[0], right = test.Operands[1];
        bool numeric = RegisterConstant(left, right, out VReg? variable, out ImmOperand? constant, out bool reversed);
        int numericWanted = reversed ? Swap(wanted) : wanted;
        ulong wordMask = IntegerBitFacts.Mask(left.Type), low = 0, high = wordMask;
        bool bounded = false;
        // Equality can use either ordering, but signed and unsigned ordering
        // predicates must never be intersected as if they were the same.
        int numericDomain = domain == 0 ? 2 : domain;
        ulong Ordered(ImmOperand value) => (unchecked((ulong)value.Value) & wordMask)
            ^ (numericDomain == 1 ? 1UL << (left.Type.Bytes() * 8 - 1) : 0);
        foreach (Fact fact in facts)
        {
            Instr known = fact.Compare;
            int relation = Relations(known.Op);
            if (!fact.Truth) relation ^= 7;
            int knownDomain = Domain(known.Op);
            int aligned = relation;
            bool matches = Same(left, known.Operands[0]) && Same(right, known.Operands[1]);
            if (!matches && Same(left, known.Operands[1]) && Same(right, known.Operands[0]))
            { matches = true; aligned = Swap(aligned); }
            if (matches && (domain == 0 || knownDomain == 0 || knownDomain == domain))
            {
                if (domain == 0) aligned = (aligned & 2) == 0 ? 5 : aligned == 2 ? 2 : 7;
                available &= aligned;
            }
            if (!numeric || known.Operands[0].Type != left.Type
                || (knownDomain != 0 && knownDomain != numericDomain)
                || !RegisterConstant(known.Operands[0], known.Operands[1], out VReg? source,
                    out ImmOperand? limit, out bool flip) || source != variable) continue;
            if (flip) relation = Swap(relation);
            ulong value = Ordered(limit!);
            switch (relation)
            {
                case 1: if (value == 0) return null; high = Math.Min(high, value - 1); break;
                case 3: high = Math.Min(high, value); break;
                case 4: if (value == wordMask) return null; low = Math.Max(low, value + 1); break;
                case 6: low = Math.Max(low, value); break;
                case 2: low = Math.Max(low, value); high = Math.Min(high, value); break;
                default: continue;
            }
            bounded = true;
        }
        if (available == 0 || low > high) return null; // unreachable, left to CFG cleanup
        if ((available & wanted) == available) return true;
        if ((available & wanted) == 0) return false;
        if (!bounded) return null;
        ulong boundary = Ordered(constant!);
        int possible = (low < boundary ? 1 : 0) | (low <= boundary && boundary <= high ? 2 : 0)
            | (high > boundary ? 4 : 0);
        return (possible & numericWanted) == possible ? true : (possible & numericWanted) == 0 ? false : null;
    }

    private static bool RegisterConstant(Operand a, Operand b, out VReg? variable, out ImmOperand? constant, out bool reversed)
    {
        reversed = a is ImmOperand;
        if (reversed) (a, b) = (b, a);
        variable = (a as RegOperand)?.Reg; constant = b as ImmOperand;
        return variable is not null && constant is not null;
    }
}
