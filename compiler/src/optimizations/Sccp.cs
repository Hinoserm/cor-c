#nullable enable
using Corsac.Lang.Ir;

namespace Corsac.Lang.Opt;

using Block = Corsac.Lang.Ir.Block;

/// <summary>
/// Sparse conditional constant propagation, on SSA form only. Wegman and
/// Zadeck: every register starts as "not yet known", constants flow
/// forward through instructions and phis, and a branch whose condition is
/// known sends flow down one edge only -- so a phi never sees a value
/// from a path that cannot be taken, which is what lets it find constants
/// that plain folding cannot: after inlining, a callee's `if (flag)` with
/// a constant argument folds and everything behind the dead arm goes.
///
/// The rewrite is deliberately minimal. Uses of constant registers become
/// immediates and decided branches become jumps; the now-dead definitions
/// and the blocks flow never reached are left to dead-code elimination
/// and <see cref="BranchSimplify"/>, which already know how to remove
/// them while keeping the phis right.
/// </summary>
public sealed class Sccp : IPass
{
    public string Name => "sccp";

    private enum Kind : byte
    {
        Top,        // nothing reaching yet
        Const,
        Bottom,     // varies
    }

    private readonly record struct Lattice(Kind Kind, long Value)
    {
        public static readonly Lattice TopValue = new(Kind.Top, 0);
        public static readonly Lattice BottomValue = new(Kind.Bottom, 0);
        public static Lattice Of(long v) => new(Kind.Const, v);
    }

    private Function _f = null!;
    private Cfg _cfg = null!;
    private readonly Dictionary<VReg, Lattice> _values = new();
    private readonly Dictionary<VReg, List<(Block Block, Instr Instr)>> _users = new();
    private readonly HashSet<Block> _executable = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<(Block, Block)> _edges = new();
    private readonly Queue<Block> _blockWork = new();
    private readonly Queue<(Block Block, Instr Instr)> _instrWork = new();

    public void Run(Function f)
    {
        _f = f;
        _cfg = new Cfg(f);
        _values.Clear();
        _users.Clear();
        _executable.Clear();
        _edges.Clear();
        _blockWork.Clear();
        _instrWork.Clear();

        HashSet<VReg> defined = new(f.Params);
        foreach (Block b in f.Blocks)
        {
            foreach (Instr i in b.Instrs)
            {
                if (i.Dest is not null)
                {
                    defined.Add(i.Dest);
                }
                foreach (VReg r in IrInfo.Uses(i))
                {
                    if (!_users.TryGetValue(r, out List<(Block, Instr)>? list))
                    {
                        list = new List<(Block, Instr)>();
                        _users[r] = list;
                    }
                    list.Add((b, i));
                }
            }
        }
        // Parameters vary; so does a register nothing defines, whose value
        // is whatever it is.
        foreach (VReg p in f.Params)
        {
            _values[p] = Lattice.BottomValue;
        }
        foreach (VReg r in _users.Keys)
        {
            if (!defined.Contains(r))
            {
                _values[r] = Lattice.BottomValue;
            }
        }

        foreach (Block root in f.Blocks.Where(_cfg.IsRoot))
        {
            MarkBlock(root);
        }

        while (_blockWork.Count > 0 || _instrWork.Count > 0)
        {
            while (_blockWork.Count > 0)
            {
                Block b = _blockWork.Dequeue();
                foreach (Instr i in b.Instrs)
                {
                    Visit(b, i);
                }
            }
            while (_instrWork.Count > 0)
            {
                (Block b, Instr i) = _instrWork.Dequeue();
                if (_executable.Contains(b))
                {
                    Visit(b, i);
                }
            }
        }

        Rewrite();
    }

    private Lattice ValueOf(Operand o) => o switch
    {
        ImmOperand imm => Lattice.Of(IrInfo.Normalise(imm.Value, imm.Type)),
        RegOperand r => _values.TryGetValue(r.Reg, out Lattice v) ? v : Lattice.TopValue,
        _ => Lattice.BottomValue,       // an address is a value, but not one we fold
    };

    private void MarkBlock(Block b)
    {
        if (_executable.Add(b))
        {
            _blockWork.Enqueue(b);
        }
    }

    private void MarkEdge(Block from, Block to)
    {
        if (!_edges.Add((from, to)))
        {
            return;
        }
        if (_executable.Contains(to))
        {
            // Already running: only its phis can learn something from a new edge.
            foreach (Instr phi in Phi.Of(to))
            {
                Visit(to, phi);
            }
        }
        else
        {
            MarkBlock(to);
        }
    }

    private void Set(VReg r, Lattice v)
    {
        _values.TryGetValue(r, out Lattice old);
        if (old == v)
        {
            return;
        }
        // The lattice only descends: Top -> Const -> Bottom.
        if (old.Kind == Kind.Bottom || (old.Kind == Kind.Const && v.Kind == Kind.Top))
        {
            return;
        }
        _values[r] = v;
        if (_users.TryGetValue(r, out List<(Block Block, Instr Instr)>? list))
        {
            foreach ((Block b, Instr i) in list)
            {
                _instrWork.Enqueue((b, i));
            }
        }
    }

    private void Visit(Block b, Instr i)
    {
        switch (i.Op)
        {
            case Opcode.Phi:
                {
                    Lattice acc = Lattice.TopValue;
                    for (int k = 0; k < i.Operands.Count; k++)
                    {
                        if (!_edges.Contains((i.Targets[k], b)))
                        {
                            continue;
                        }
                        acc = Meet(acc, ValueOf(i.Operands[k]));
                    }
                    Set(i.Dest!, acc);
                    return;
                }

            case Opcode.Jump:
                MarkEdge(b, i.Targets[0]);
                return;

            case Opcode.Branch:
                {
                    Lattice c = ValueOf(i.Operands[0]);
                    if (c.Kind == Kind.Const)
                    {
                        MarkEdge(b, (int)c.Value != 0 ? i.Targets[0] : i.Targets[1]);
                    }
                    else if (c.Kind == Kind.Bottom)
                    {
                        MarkEdge(b, i.Targets[0]);
                        MarkEdge(b, i.Targets[1]);
                    }
                    return;
                }

            case Opcode.Switch:
                {
                    Lattice c = ValueOf(i.Operands[0]);
                    if (c.Kind == Kind.Const)
                    {
                        long n = (int)c.Value;
                        MarkEdge(b, n >= 0 && n < i.Targets.Count ? i.Targets[(int)n] : i.Default!);
                    }
                    else if (c.Kind == Kind.Bottom)
                    {
                        foreach (Block t in i.Targets)
                        {
                            MarkEdge(b, t);
                        }
                        MarkEdge(b, i.Default!);
                    }
                    return;
                }
        }

        if (i.Dest is null)
        {
            return;
        }
        if (!IrInfo.IsPure(i) || i.Op is Opcode.Load or Opcode.LabelAddr or Opcode.StackPointer or Opcode.FramePointer)
        {
            Set(i.Dest, Lattice.BottomValue);
            return;
        }

        // Pure arithmetic: constant if every operand is, unknown while any
        // is still unknown, varying otherwise.
        bool anyTop = false;
        bool anyBottom = false;
        Instr probe = new() { Op = i.Op, Dest = i.Dest };
        foreach (Operand o in i.Operands)
        {
            Lattice v = ValueOf(o);
            anyTop |= v.Kind == Kind.Top;
            anyBottom |= v.Kind == Kind.Bottom;
            probe.Operands.Add(v.Kind == Kind.Const ? new ImmOperand(v.Value, o.Type) : o);
        }
        if (anyBottom)
        {
            Set(i.Dest, Lattice.BottomValue);
            return;
        }
        if (anyTop)
        {
            return;
        }
        if (i.Op == Opcode.Copy)
        {
            Set(i.Dest, ValueOf(probe.Operands[0]));
            return;
        }
        Instr? folded = ConstantFold.Fold(probe);
        if (folded is not null && folded.Operands[0] is ImmOperand result)
        {
            Set(i.Dest, Lattice.Of(IrInfo.Normalise(result.Value, i.Dest.Type)));
        }
        else
        {
            Set(i.Dest, Lattice.BottomValue);   // e.g. a division by zero: left to trap
        }
    }

    private static Lattice Meet(Lattice a, Lattice b)
    {
        if (a.Kind == Kind.Top)
        {
            return b;
        }
        if (b.Kind == Kind.Top)
        {
            return a;
        }
        if (a.Kind == Kind.Const && b.Kind == Kind.Const && a.Value == b.Value)
        {
            return a;
        }
        return Lattice.BottomValue;
    }

    private void Rewrite()
    {
        foreach (Block b in _f.Blocks)
        {
            if (!_executable.Contains(b))
            {
                continue;       // unreachable once the branches are decided; BranchSimplify removes it
            }
            foreach (Instr i in b.Instrs)
            {
                IrInfo.ReplaceUses(i, r => _values.TryGetValue(r, out Lattice v) && v.Kind == Kind.Const
                    ? new ImmOperand(v.Value, r.Type)
                    : null);
            }

            Instr t = b.Terminator!;
            if (t.Op is Opcode.Branch or Opcode.Switch)
            {
                Instr? folded = ConstantFold.Fold(t);
                if (folded is not null)
                {
                    List<Block> before = t.Targets.ToList();
                    if (t.Default is not null)
                    {
                        before.Add(t.Default);
                    }
                    b.Instrs[^1] = folded;
                    foreach (Block old in before.Distinct())
                    {
                        Phi.DropEdgeIfGone(b, old);
                    }
                }
            }
        }
    }
}
