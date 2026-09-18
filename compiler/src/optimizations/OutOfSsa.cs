#nullable enable
using Corsac.Lang.Ir;

namespace Corsac.Lang.Opt;

using Block = Corsac.Lang.Ir.Block;

/// <summary>
/// Takes a function out of SSA form: every phi becomes copies at the end
/// of each predecessor, and the phis go. The copies for one edge are a
/// parallel assignment -- all right-hand sides are read before any
/// left-hand side is written -- and are sequentialised with a temporary
/// where they form a cycle (the classic swap in a loop).
///
/// The copies for an edge from a block with several successors cannot sit
/// in that block: they would run on the other edges too, and would
/// clobber a register the terminator is about to test. Such an edge is
/// split with a new block holding just the copies and a jump. A later
/// <see cref="BranchSimplify"/> merges it back where it can, so the
/// backend sees a normal graph, only with more copies than before, which
/// its allocator's coalescing exists to remove.
/// </summary>
public sealed class OutOfSsa : IPass
{
    public string Name => "unssa";

    public void Run(Function f)
    {
        foreach (Block b in f.Blocks.ToList())
        {
            if (!Phi.HasPhis(b))
            {
                continue;
            }
            List<Instr> phis = Phi.Of(b).ToList();
            HashSet<Block> preds = new(ReferenceEqualityComparer.Instance);
            foreach (Instr phi in phis)
            {
                foreach (Block p in phi.Targets)
                {
                    preds.Add(p);
                }
            }

            foreach (Block p in preds)
            {
                List<(VReg Dest, Operand Src)> moves = new();
                foreach (Instr phi in phis)
                {
                    int k = Phi.IndexOf(phi, p);
                    if (k >= 0 && phi.Dest is not null)
                    {
                        moves.Add((phi.Dest, phi.Operands[k]));
                    }
                }
                List<Instr> copies = Sequentialise(f, moves);
                if (copies.Count == 0)
                {
                    continue;
                }

                Instr t = p.Terminator!;
                bool single = t.Op == Opcode.Jump;
                if (single)
                {
                    p.Instrs.InsertRange(p.Instrs.Count - 1, copies);
                    continue;
                }

                // Split the edge p -> b.
                Block split = f.NewBlock("phi");
                split.Instrs.AddRange(copies);
                split.Instrs.Add(new Instr { Op = Opcode.Jump, Targets = { b }, Line = t.Line });
                for (int k = 0; k < t.Targets.Count; k++)
                {
                    if (ReferenceEquals(t.Targets[k], b))
                    {
                        t.Targets[k] = split;
                    }
                }
                if (ReferenceEquals(t.Default, b))
                {
                    t.Default = split;
                }
            }

            b.Instrs.RemoveRange(0, phis.Count);
        }
    }

    /// <summary>
    /// Orders a parallel assignment into copies. A move whose destination
    /// nobody else still reads can go first; when only cycles remain, one
    /// source is saved to a temporary to break them.
    /// </summary>
    private static List<Instr> Sequentialise(Function f, List<(VReg Dest, Operand Src)> moves)
    {
        List<Instr> outp = new();
        List<(VReg Dest, Operand Src)> pending = moves
            .Where(m => !(m.Src is RegOperand r && ReferenceEquals(r.Reg, m.Dest)))
            .ToList();

        while (pending.Count > 0)
        {
            int free = -1;
            for (int k = 0; k < pending.Count && free < 0; k++)
            {
                VReg d = pending[k].Dest;
                bool read = false;
                for (int j = 0; j < pending.Count; j++)
                {
                    if (j != k && pending[j].Src is RegOperand r && ReferenceEquals(r.Reg, d))
                    {
                        read = true;
                        break;
                    }
                }
                if (!read)
                {
                    free = k;
                }
            }

            if (free >= 0)
            {
                (VReg d, Operand s) = pending[free];
                pending.RemoveAt(free);
                outp.Add(new Instr { Op = Opcode.Copy, Dest = d, Operands = { s } });
                continue;
            }

            // Every destination is still read: a cycle. Park one source.
            (VReg d0, Operand s0) = pending[0];
            VReg src = ((RegOperand)s0).Reg;
            VReg tmp = f.NewReg(src.Type, src.Name);
            outp.Add(new Instr { Op = Opcode.Copy, Dest = tmp, Operands = { new RegOperand(src) } });
            for (int k = 0; k < pending.Count; k++)
            {
                if (pending[k].Src is RegOperand r && ReferenceEquals(r.Reg, src))
                {
                    pending[k] = (pending[k].Dest, new RegOperand(tmp));
                }
            }
            _ = d0;
        }
        return outp;
    }
}
