#nullable enable
using Corsac.Lang.Ir;

namespace Corsac.Lang.Opt;

using Block = Corsac.Lang.Ir.Block;

/// <summary>
/// Tidies the control-flow graph after the other passes have decided
/// branches and emptied blocks: a branch to one place becomes a jump, a
/// block that only jumps is bypassed, blocks nothing reaches go, and a
/// block with one predecessor that is its predecessor's only successor is
/// appended to it. Lowering emits a block per source construct and never
/// looks back, so a function typically loses a third of its blocks here.
///
/// Landing pads and other roots (see <see cref="Cfg.IsRoot"/>) are never
/// bypassed, merged away or removed: control reaches them by a route the
/// graph does not show. The entry block keeps its place at index zero.
/// </summary>
public sealed class BranchSimplify : IPass
{
    public string Name => "branches";

    public void Run(Function f)
    {
        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (Block b in f.Blocks)
            {
                if (CollapseTerminator(b))
                {
                    changed = true;
                }
            }
            if (ThreadJumps(f))
            {
                changed = true;
            }
            if (ThreadConstantConditions(f))
            {
                changed = true;
            }
            if (Cfg.RemoveUnreachable(f))
            {
                changed = true;
            }
            if (MergeBlocks(f))
            {
                changed = true;
            }
        }
    }

    /// <summary>A branch or switch with only one place to go becomes a jump.</summary>
    private static bool CollapseTerminator(Block b)
    {
        Instr? t = b.Terminator;
        if (t is null)
        {
            return false;
        }
        Block? only = t.Op switch
        {
            Opcode.Branch when ReferenceEquals(t.Targets[0], t.Targets[1]) => t.Targets[0],
            Opcode.Switch when t.Default is not null && t.Targets.All(x => ReferenceEquals(x, t.Default)) => t.Default,
            _ => null,
        };
        if (only is null)
        {
            return false;
        }
        b.Instrs[^1] = new Instr { Op = Opcode.Jump, InitialTargets = new[] { only }, Line = t.Line };
        return true;
    }

    /// <summary>
    /// A block that sets a register to a constant and jumps to a block that
    /// does nothing but branch on that register goes straight to the side
    /// the constant picks. `a &amp;&amp; b`, `a || b` and every inlined method
    /// returning bool come out of lowering as a 0 or 1 written on each arm
    /// and tested again where the arms meet; this takes the test away from
    /// every arm that knows the answer. The copy stays for any later reader.
    /// </summary>
    private static bool ThreadConstantConditions(Function f)
    {
        Cfg cfg = PipelineAnalyses.CfgOf(f);
        bool changed = false;
        foreach (Block p in f.Blocks)
        {
            Instr? jump = p.Terminator;
            if (jump is null || jump.Op != Opcode.Jump)
            {
                continue;
            }
            Block b = jump.Targets[0];
            if (ReferenceEquals(b, p) || cfg.IsRoot(b) || b.Instrs.Count != 1)
            {
                continue;
            }
            Instr branch = b.Instrs[0];
            if (branch.Op != Opcode.Branch || branch.Operands[0] is not RegOperand tested)
            {
                continue;
            }
            // The value the register holds as this block leaves: its last write here.
            long? known = null;
            for (int k = p.Instrs.Count - 2; k >= 0; k--)
            {
                Instr i = p.Instrs[k];
                if (i.Dest != tested.Reg)
                {
                    continue;
                }
                if (i.Op == Opcode.Copy && i.Operands[0] is ImmOperand imm)
                {
                    known = imm.Value;
                }
                break;
            }
            if (known is null)
            {
                continue;
            }
            Block to = known.Value != 0 ? branch.Targets[0] : branch.Targets[1];
            if (ReferenceEquals(to, b) || !Phi.CanAddIncoming(to, b, p))
            {
                continue;
            }
            Phi.AddIncoming(to, b, p);
            jump.SetTarget(0, to);
            Phi.DropEdgeIfGone(p, b);
            changed = true;
        }
        return changed;
    }

    /// <summary>
    /// Every edge into a block that only jumps on is redirected to where it
    /// jumps. A chain of such blocks is followed to its end; a cycle of
    /// them is an infinite loop the program wrote, and is left alone.
    /// </summary>
    private static bool ThreadJumps(Function f)
    {
        Cfg cfg = PipelineAnalyses.CfgOf(f);
        Dictionary<Block, Block> next = new(ReferenceEqualityComparer.Instance);
        foreach (Block b in f.Blocks)
        {
            if (b.Instrs.Count == 1 && b.Instrs[0].Op == Opcode.Jump && !cfg.IsRoot(b)
                && !ReferenceEquals(b.Instrs[0].Targets[0], b))
            {
                next[b] = b.Instrs[0].Targets[0];
            }
        }
        if (next.Count == 0)
        {
            return false;
        }

        // Follows the chain to its end, also returning the last hop: the
        // end's phis know the value by that name. One set of the blocks
        // passed, emptied for each walk: a set each was one for every edge
        // of every block, every round.
        HashSet<Block> seen = new(ReferenceEqualityComparer.Instance);
        (Block End, Block LastHop) Final(Block b)
        {
            seen.Clear();
            Block last = b;
            while (next.TryGetValue(b, out Block? n) && seen.Add(b))
            {
                last = b;
                b = n;
            }
            return (b, last);
        }

        // The end's phis must be able to take this block as a predecessor
        // in place of the hop; in SSA form a block already reaching the end
        // directly with a different value cannot be redirected.
        Block? Redirect(Block from, Block target)
        {
            // Not a block that only jumps on: its own end, so nothing to do.
            if (!next.ContainsKey(target))
            {
                return null;
            }
            (Block end, Block hop) = Final(target);
            if (ReferenceEquals(end, target))
            {
                return null;
            }
            if (!Phi.CanAddIncoming(end, hop, from))
            {
                return null;
            }
            Phi.AddIncoming(end, hop, from);
            return end;
        }

        bool changed = false;
        foreach (Block b in f.Blocks)
        {
            Instr? t = b.Terminator;
            if (t is null || t.Op is not (Opcode.Jump or Opcode.Branch or Opcode.Switch))
            {
                continue;
            }
            for (int k = 0; k < t.Targets.Count; k++)
            {
                Block? to = Redirect(b, t.Targets[k]);
                if (to is not null)
                {
                    Block old = t.Targets[k];
                    t.SetTarget(k, to);
                    Phi.DropEdgeIfGone(b, old);
                    changed = true;
                }
            }
            if (t.Default is not null)
            {
                Block? to = Redirect(b, t.Default);
                if (to is not null)
                {
                    Block old = t.Default;
                    t.Default = to;
                    Phi.DropEdgeIfGone(b, old);
                    changed = true;
                }
            }
        }
        return changed;
    }

    /// <summary>
    /// Appends a block to its only predecessor when it is that block's
    /// only successor: the jump between them was the only reason they
    /// were two. Done in one sweep with the predecessor sets kept current,
    /// so a chain of a hundred single-successor blocks costs one pass.
    /// </summary>
    private static bool MergeBlocks(Function f)
    {
        // BY POSITION: how many blocks come into each, and which one when
        // it is one -- all a merge asks. A set of predecessors a block was
        // the pass's own largest allocation, a fifth of it ever given back.
        Cfg cfg = PipelineAnalyses.CfgOf(f);
        int n = f.Blocks.Count;
        int[] into = new int[n];
        Block?[] only = new Block?[n];
        foreach (Block b in f.Blocks)
        {
            Edges preds = cfg.Preds(b);
            into[b.Order] = preds.Count;
            if (preds.Count == 1) only[b.Order] = preds[0];
        }

        bool[] gone = new bool[n];
        bool any = false;
        foreach (Block a in f.Blocks)
        {
            if (gone[a.Order])
            {
                continue;
            }
            while (true)
            {
                Instr? t = a.Terminator;
                if (t is null || t.Op != Opcode.Jump)
                {
                    break;
                }
                Block b = t.Targets[0];
                if (ReferenceEquals(a, b) || cfg.IsRoot(b) || into[b.Order] != 1 || !ReferenceEquals(only[b.Order], a))
                {
                    break;
                }
                // b's phis have one entry, from a: they are copies now.
                Phi.LowerSingleEntry(b);
                a.Instrs.RemoveAt(a.Instrs.Count - 1);
                a.Instrs.AddRange(b.Instrs);
                gone[b.Order] = true;
                any = true;
                // b's successors are a's now: one that had b alone has a
                // alone; one that had others as well has at least as many
                // (a may have been one of them -- then a later round sees it).
                if (a.Terminator is { } end)
                {
                    for (int k = 0; k <= end.Targets.Count; k++)
                    {
                        Block? s = k < end.Targets.Count ? end.Targets[k] : end.Default;
                        if (s is null || s.Order >= n) continue;
                        if (into[s.Order] == 1 && ReferenceEquals(only[s.Order], b)) only[s.Order] = a;
                        Phi.Rename(s, b, a);
                    }
                }
            }
        }
        if (!any)
        {
            return false;
        }
        int kept = 0;
        for (int k = 0; k < f.Blocks.Count; k++)
            if (!gone[f.Blocks[k].Order]) f.Blocks[kept++] = f.Blocks[k];
        f.Blocks.RemoveRange(kept, f.Blocks.Count - kept);
        return true;
    }
}
