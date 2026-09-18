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
        b.Instrs[^1] = new Instr { Op = Opcode.Jump, Targets = { only }, Line = t.Line };
        return true;
    }

    /// <summary>
    /// Every edge into a block that only jumps on is redirected to where it
    /// jumps. A chain of such blocks is followed to its end; a cycle of
    /// them is an infinite loop the program wrote, and is left alone.
    /// </summary>
    private static bool ThreadJumps(Function f)
    {
        Cfg cfg = new(f);
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
        // end's phis know the value by that name.
        (Block End, Block LastHop) Final(Block b)
        {
            HashSet<Block> seen = new(ReferenceEqualityComparer.Instance);
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
                    t.Targets[k] = to;
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
        Cfg cfg = new(f);
        Dictionary<Block, HashSet<Block>> preds = new(ReferenceEqualityComparer.Instance);
        foreach (Block b in f.Blocks)
        {
            preds[b] = new HashSet<Block>(cfg.Preds(b), ReferenceEqualityComparer.Instance);
        }

        HashSet<Block> gone = new(ReferenceEqualityComparer.Instance);
        foreach (Block a in f.Blocks)
        {
            if (gone.Contains(a))
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
                if (ReferenceEquals(a, b) || cfg.IsRoot(b) || preds[b].Count != 1 || !preds[b].Contains(a))
                {
                    break;
                }
                // b's phis have one entry, from a: they are copies now.
                Phi.LowerSingleEntry(b);
                a.Instrs.RemoveAt(a.Instrs.Count - 1);
                a.Instrs.AddRange(b.Instrs);
                gone.Add(b);
                foreach (Block s in b.Successors)
                {
                    preds[s].Remove(b);
                    preds[s].Add(a);
                    Phi.Rename(s, b, a);
                }
            }
        }
        if (gone.Count == 0)
        {
            return false;
        }
        f.Blocks.RemoveAll(gone.Contains);
        return true;
    }
}
