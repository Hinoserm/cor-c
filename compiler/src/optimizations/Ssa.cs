#nullable enable
using Corsac.Lang.Ir;

namespace Corsac.Lang.Opt;

using Block = Corsac.Lang.Ir.Block;

/// <summary>
/// Puts a function into SSA form: every register gets exactly one
/// definition, and where two definitions meet a <see cref="Opcode.Phi"/>
/// chooses between them. The classic construction -- phis at the iterated
/// dominance frontier of each register's definition blocks, then a walk
/// down the dominator tree renaming definitions and uses -- pruned by
/// liveness so a register dead at a join gets no phi there.
///
/// Every root of the graph (entry, landing pads) is the top of its own
/// dominator tree and the renaming starts there with nothing defined,
/// which encodes the IR's rule that every register is dead on entry to a
/// landing pad: a use in a pad of something defined outside it keeps the
/// original register, which after renaming has no definition at all, and
/// reads whatever the backend gives it -- as it did before. Parameters
/// are the one exception: they are defined on entry and so are pushed at
/// the entry root only.
///
/// Register names survive (a renamed copy keeps <see cref="VReg.Name"/>),
/// so a dump after this pass still reads like the source.
/// </summary>
public sealed class Ssa : IPass
{
    public string Name => "ssa";

    public void Run(Function f)
    {
        Cfg cfg = new(f);
        Liveness live = new(cfg);

        // Where each register is defined, counting a parameter's implicit
        // definition on entry.
        Dictionary<VReg, HashSet<Block>> defBlocks = new();
        foreach (VReg p in f.Params)
        {
            defBlocks[p] = new HashSet<Block>(ReferenceEqualityComparer.Instance) { f.Entry };
        }
        foreach (Block b in f.Blocks)
        {
            foreach (Instr i in b.Instrs)
            {
                if (i.Dest is not null)
                {
                    if (!defBlocks.TryGetValue(i.Dest, out HashSet<Block>? set))
                    {
                        set = new HashSet<Block>(ReferenceEqualityComparer.Instance);
                        defBlocks[i.Dest] = set;
                    }
                    set.Add(b);
                }
            }
        }

        // Phi placement: iterated dominance frontier, pruned by liveness.
        Dictionary<Block, List<Instr>> phis = new(ReferenceEqualityComparer.Instance);
        foreach ((VReg v, HashSet<Block> defs) in defBlocks)
        {
            // Even a register with one definition needs a phi when it is
            // read around a loop before that definition: the header joins
            // the previous trip's value with none at all.
            HashSet<Block> placed = new(ReferenceEqualityComparer.Instance);
            Stack<Block> work = new(defs);
            while (work.Count > 0)
            {
                Block d = work.Pop();
                foreach (Block y in cfg.Frontier(d))
                {
                    if (!placed.Add(y) || !live.IsLiveIn(y, v))
                    {
                        continue;
                    }
                    Instr phi = new() { Op = Opcode.Phi, Dest = v };
                    foreach (Block p in cfg.Preds(y))
                    {
                        phi.Operands.Add(new RegOperand(v));
                        phi.Targets.Add(p);
                    }
                    if (!phis.TryGetValue(y, out List<Instr>? list))
                    {
                        list = new List<Instr>();
                        phis[y] = list;
                    }
                    list.Add(phi);
                    if (!defs.Contains(y))
                    {
                        work.Push(y);
                    }
                }
            }
        }
        foreach ((Block y, List<Instr> list) in phis)
        {
            y.Instrs.InsertRange(0, list);
        }

        // Renaming, down each dominator tree. The stack per register holds
        // the current name; an empty stack means "not defined on this path".
        Dictionary<VReg, Stack<VReg>> names = new();
        Stack<VReg> StackOf(VReg v)
        {
            if (!names.TryGetValue(v, out Stack<VReg>? s))
            {
                s = new Stack<VReg>();
                names[v] = s;
            }
            return s;
        }

        foreach (Block root in f.Blocks.Where(cfg.IsRoot))
        {
            if (ReferenceEquals(root, f.Entry))
            {
                foreach (VReg p in f.Params)
                {
                    StackOf(p).Push(p);
                }
            }

            // Explicit stack: a long straight-line function is a dominator
            // chain as deep as it is long.
            Stack<(Block Block, List<VReg> Pushed, bool Done)> walk = new();
            walk.Push((root, new List<VReg>(), false));
            while (walk.Count > 0)
            {
                (Block b, List<VReg> pushed, bool done) = walk.Pop();
                if (done)
                {
                    foreach (VReg v in pushed)
                    {
                        names[v].Pop();
                    }
                    continue;
                }

                foreach (Instr i in b.Instrs)
                {
                    if (i.Op != Opcode.Phi)
                    {
                        for (int k = 0; k < i.Operands.Count; k++)
                        {
                            if (i.Operands[k] is RegOperand r && names.TryGetValue(r.Reg, out Stack<VReg>? s) && s.Count > 0)
                            {
                                VReg top = s.Peek();
                                if (!ReferenceEquals(top, r.Reg))
                                {
                                    i.Operands[k] = new RegOperand(top);
                                }
                            }
                        }
                    }
                    if (i.Dest is not null)
                    {
                        // Every definition gets a fresh register, so that
                        // the original name ends up defined nowhere and a
                        // use nothing reaches stays visibly undefined
                        // rather than aliasing some unrelated definition.
                        VReg old = i.Dest;
                        VReg fresh = f.NewReg(old.Type, old.Name);
                        i.Dest = fresh;
                        StackOf(old).Push(fresh);
                        pushed.Add(old);
                    }
                }

                foreach (Block succ in cfg.Succs(b))
                {
                    foreach (Instr phi in Phi.Of(succ))
                    {
                        int k = Phi.IndexOf(phi, b);
                        if (k < 0 || phi.Operands[k] is not RegOperand r)
                        {
                            continue;
                        }
                        if (names.TryGetValue(r.Reg, out Stack<VReg>? s) && s.Count > 0)
                        {
                            phi.Operands[k] = new RegOperand(s.Peek());
                        }
                    }
                }

                walk.Push((b, pushed, true));
                foreach (Block child in cfg.DomChildren(b))
                {
                    walk.Push((child, new List<VReg>(), false));
                }
            }

            if (ReferenceEquals(root, f.Entry))
            {
                foreach (VReg p in f.Params)
                {
                    names[p].Pop();
                }
            }
        }

        // A phi operand still naming an original register is a path on
        // which nothing defined it. Its value is anybody's; make it the
        // value the other paths bring, which spares a copy of garbage.
        HashSet<VReg> defined = new(f.Params);
        foreach (Block b in f.Blocks)
        {
            foreach (Instr i in b.Instrs)
            {
                if (i.Dest is not null)
                {
                    defined.Add(i.Dest);
                }
            }
        }
        foreach (Block b in f.Blocks)
        {
            foreach (Instr phi in Phi.Of(b))
            {
                Operand? known = phi.Operands.FirstOrDefault(o => o is not RegOperand r || defined.Contains(r.Reg));
                if (known is null)
                {
                    continue;
                }
                for (int k = 0; k < phi.Operands.Count; k++)
                {
                    if (phi.Operands[k] is RegOperand r && !defined.Contains(r.Reg))
                    {
                        phi.Operands[k] = known;
                    }
                }
            }
        }
    }
}
