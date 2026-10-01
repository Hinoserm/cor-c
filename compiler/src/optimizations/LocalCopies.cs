#nullable enable
using Corsac.Lang.Ir;
namespace Corsac.Lang.Opt;

/// <summary>Forward current copies within blocks and unambiguous dominated edges.</summary>
public sealed class LocalCopies : IPass
{
    public string Name => "local-copies";

    public void Run(Function f)
    {
        Cfg cfg = new(f);
        // A BLOCK'S TABLE ONLY ONCE IT HAS SOMETHING IN IT, and kept only for
        // a block that has one: most blocks copy nothing, and a dictionary
        // made for each of them, on every round, was this pass's whole cost.
        Dictionary<Corsac.Lang.Ir.Block, Dictionary<VReg, Operand>> atEnd = new();
        // One delegate for the function, reading whichever table the block
        // has: made in the loop, it was a closure for every instruction.
        Dictionary<VReg, Operand>? copies = null;
        Func<VReg, Operand?> current = r => copies!.GetValueOrDefault(r);
        List<VReg> stale = new();
        foreach (var block in cfg.ReversePostorder)
        {
            copies = null;
            // ONE PREDECESSOR, AND NOT A ROOT, IS A DOMINATOR: every way in is
            // through it. Asked of the dominator tree, every graph this pass
            // made built one.
            var predecessors = cfg.Preds(block);
            if (!cfg.IsRoot(block) && predecessors.Count == 1
                && atEnd.TryGetValue(predecessors[0], out var inherited))
                copies = new(inherited);
            foreach (Instr i in block.Instrs)
            {
                // Phi operands refer to predecessor edges, not this position.
                if (i.Op == Opcode.Phi) { copies?.Clear(); continue; }
                if (copies is { Count: > 0 }) IrInfo.ReplaceUses(i, current);
                if (i.Dest is not { } dest) continue;
                // Entries are canonical one-hop values. Redefining their
                // source invalidates every captured alias before new facts.
                if (copies is { Count: > 0 })
                {
                    copies.Remove(dest);
                    foreach (var pair in copies)
                        if (pair.Value is RegOperand held && held.Reg == dest) stale.Add(pair.Key);
                    foreach (VReg alias in stale) copies.Remove(alias);
                    stale.Clear();
                }
                if (i.Op == Opcode.Copy && i.Operands.Count == 1 && i.Operands[0].Type == dest.Type
                    && (i.Operands[0] is ImmOperand || i.Operands[0] is RegOperand r && r.Reg != dest))
                    (copies ??= new())[dest] = i.Operands[0];
            }
            if (copies is { Count: > 0 }) atEnd[block] = copies;
        }
    }
}
