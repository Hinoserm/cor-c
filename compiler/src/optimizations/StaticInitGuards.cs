#nullable enable
using Corsac.Lang.Ir;

namespace Corsac.Lang.Opt;

using Block = Corsac.Lang.Ir.Block;

/// <summary>
/// A TYPE'S INITIALISER GUARD BEHIND ANOTHER FOR THE SAME TYPE GOES. Lowering
/// tests the ready word at every touch of another type (Lowering.TouchType):
///
///     %v = load @s_T_StaticReady$
///     compilerfence
///     %c = eq %v 1
///     branch %c ->done ->run
///   run:
///     call T.StaticInit$
///     jump ->done
///
/// and inlining puts the touches of two bodies side by side: String's twice
/// in a row in a function that made a list and then read string.Empty. Once
/// control is past one guard's join, the initialiser has finished -- and
/// initialisation never comes undone -- or this thread is the one running it
/// and is somewhere inside it, when a second call would return at once just
/// as the first did. A failed initialiser threw out of the first call and
/// never reached the join. So a guard whose block that join dominates does
/// nothing the first did not, and its branch becomes a jump to its own join.
///
/// Only a join entered from its guard and its call alone counts: one that
/// other code also jumps into proves nothing about the paths through it.
/// Landing pads are roots of the graph (Cfg.IsRoot), dominated by nothing
/// else, so a guard in a handler is always kept.
/// </summary>
public sealed class StaticInitGuards : IPass
{
    public string Name => "static-init-guards";

    private sealed record Guard(Block Block, string Ready, Block Done, Block Run, Instr Load, Instr Fence, Instr Test);

    public void Run(Function f)
    {
        // Cheap first: two guards for one type, or nothing to do.
        List<Guard>? guards = null;
        foreach (Block b in f.Blocks)
            if (Match(b) is { } g) (guards ??= new()).Add(g);
        if (guards is null || guards.Count < 2) return;
        if (!guards.GroupBy(g => g.Ready).Any(group => group.Count() > 1)) return;

        Cfg cfg = new(f);
        // The join entered from the guard and its call alone, the call from the guard alone.
        bool Sealed(Guard g)
        {
            if (cfg.IsRoot(g.Done) || cfg.IsRoot(g.Run)) return false;
            if (cfg.Preds(g.Run).Count != 1) return false;
            int fromGuard = 0;
            foreach (Block p in cfg.Preds(g.Done))
            {
                if (ReferenceEquals(p, g.Block) || ReferenceEquals(p, g.Run)) fromGuard++;
                else return false;
            }
            return fromGuard == 2;
        }
        List<Guard> covering = guards.Where(Sealed).ToList();
        if (covering.Count == 0) return;

        // A COUNT OF EVERY READ, so a test the branch alone used goes with it,
        // and the fence that kept its load in place.
        Dictionary<VReg, int> reads = new();
        foreach (Block b in f.Blocks)
            foreach (Instr i in b.Instrs)
                foreach (Operand o in i.Operands)
                    if (o is RegOperand { Reg: var r }) reads[r] = reads.GetValueOrDefault(r) + 1;

        foreach (Guard g in guards)
        {
            bool covered = false;
            foreach (Guard by in covering)
            {
                if (ReferenceEquals(by, g) || by.Ready != g.Ready) continue;
                if (cfg.Dominates(by.Done, g.Block)) { covered = true; break; }
            }
            if (!covered) continue;
            Instr branch = g.Block.Instrs[^1];
            g.Block.Instrs[^1] = new Instr { Op = Opcode.Jump, WritableTargets = { g.Done }, Line = branch.Line };
            // Chained coverage stays sound with this guard gone: its join
            // still has the one entry it had from the guard's block, and
            // the guard that covered this one covers that block too.
            if (reads.GetValueOrDefault(g.Test.Dest!) == 1 && reads.GetValueOrDefault(g.Load.Dest!) == 1)
            {
                g.Block.Instrs.Remove(g.Test);
                g.Block.Instrs.Remove(g.Fence);
                g.Block.Instrs.Remove(g.Load);
            }
        }
        Cfg.RemoveUnreachable(f);
    }

    /// <summary>The guard ending the block, exactly as Lowering.TouchType makes it, or null.</summary>
    private static Guard? Match(Block b)
    {
        int n = b.Instrs.Count;
        if (n < 4) return null;
        if (b.Instrs[n - 1] is not { Op: Opcode.Branch, Operands: [RegOperand { Reg: var c }], Targets: [var done, var run] }) return null;
        if (ReferenceEquals(done, run) || ReferenceEquals(run, b) || ReferenceEquals(done, b)) return null;
        Instr test = b.Instrs[n - 2], fence = b.Instrs[n - 3], load = b.Instrs[n - 4];
        if (test is not { Op: Opcode.Eq, Operands: [RegOperand { Reg: var v }, ImmOperand { Value: 1 }] } || !ReferenceEquals(test.Dest, c)) return null;
        if (fence.Op != Opcode.CompilerFence) return null;
        if (load is not { Op: Opcode.Load, Offset: 0, Operands: [SymOperand { Name: var ready, Offset: 0 }] } || !ReferenceEquals(load.Dest, v)) return null;
        if (load.Size != IrTypes.Word.Bytes() || !ready.EndsWith("_" + Corsac.Lang.BindResult.ReadyField, StringComparison.Ordinal)) return null;
        // The arm that is not ready: the initialiser's call and back.
        if (run.Instrs is not [{ Op: Opcode.Call, Dest: null, Operands.Count: 0, Callee: { } callee }, { Op: Opcode.Jump, Targets: [var back] }]
            || !ReferenceEquals(back, done)) return null;
        // And the initialiser of the type whose word it read: s_T_StaticReady$ and m_T_StaticInit$.
        if (!ready.StartsWith("s_", StringComparison.Ordinal)) return null;
        string type = ready[2..^(Corsac.Lang.BindResult.ReadyField.Length + 1)];
        if (!callee.StartsWith("m_" + type + "_StaticInit$", StringComparison.Ordinal)) return null;
        // No join to fix up where the guard's own arms meet.
        if (done.Instrs.Count > 0 && done.Instrs[0].Op == Opcode.Phi) return null;
        return new Guard(b, ready, done, run, load, fence, test);
    }
}
