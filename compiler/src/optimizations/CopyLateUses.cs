#nullable enable
using Corsac.Lang.Ir;

namespace Corsac.Lang.Opt;

using Block = Corsac.Lang.Ir.Block;

/// <summary>
/// A VALUE READ AFTER IT IS COPIED, READ FROM THE COPY. `i++` in a loop's
/// last block is `t = add i 1; i = copy t`, and the loop's test after it
/// reads t (LocalCopies forwards the copy the other way): t is live past the
/// copy, beside i with the same value, so the two cannot share a register --
/// `lea edx,[ecx+1]; mov ecx,edx; cmp edx,...` a lap where `inc ecx; cmp
/// ecx,...` would do. Run last (Run's summary says where): where
/// t is written once, in the copy's block before it, and read nowhere but
/// there, each read of it after the copy -- with i not written again before
/// it -- reads i instead, the copy is t's last read, and the allocator gives
/// the two one register (the coalescing GCC's and LLVM's register
/// coalescers make of a copy whose source dies there).
/// </summary>
public static class CopyLateUses
{
    /// <summary>
    /// Every function of `module`, as the last step on its IR before the
    /// backend but the landing pads' homes (and the allocator fast paths
    /// instruction selection writes out, which add only their own code):
    /// after the late passes, the link's own per-function
    /// cleanup (UnitBackend.Load's SlotScalars and argument folding, which
    /// forward copies the other way) and the link's lifetime rules -- before
    /// the landing pads' homes (LandingPadHomes), which every path runs next.
    /// --skip-passes CopyLateUses leaves it out.
    /// </summary>
    public static void Run(Module module)
    {
        if (Skipped) return;
        foreach (Function function in module.Functions) Run(function);
    }

    private static bool Skipped => _skipped ??= Switches.SkipPasses is { Length: > 0 } skip
        && skip.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Contains("CopyLateUses", StringComparer.Ordinal);

    /// <summary>The switch read once a process: Run is asked once a function at the link.</summary>
    private static bool? _skipped;

    private static void Run(Function function)
    {
        if (function.Async is not null) return;
        // Every register's writes, and whether it is read in more than one
        // block: a temporary read in another block is live out of its own,
        // and left alone.
        Dictionary<VReg, Block> readIn = new();
        HashSet<VReg> elsewhere = new();
        Dictionary<VReg, int> writes = new();
        foreach (Block block in function.Blocks)
            foreach (Instr i in block.Instrs)
            {
                foreach (Operand o in i.Operands)
                    if (o is RegOperand { Reg: var r })
                    {
                        if (readIn.TryGetValue(r, out Block? seen)) { if (!ReferenceEquals(seen, block)) elsewhere.Add(r); }
                        else readIn[r] = block;
                    }
                if (i.Dest is { } d) writes[d] = writes.TryGetValue(d, out int n) ? n + 1 : 1;
            }
        if (readIn.Count == 0) return;
        // ONE WALK A BLOCK: the temporaries whose reads now take a copy (t
        // to x), and for each copy the temporaries read through it, dropped
        // when the copy is written again.
        Dictionary<VReg, VReg> through = new();
        Dictionary<VReg, List<VReg>> by = new();
        HashSet<VReg> written = new();
        foreach (Block block in function.Blocks)
        {
            if (block.IsLandingPad) continue;
            through.Clear(); by.Clear(); written.Clear();
            foreach (Instr i in block.Instrs)
            {
                if (through.Count > 0)
                    for (int o = 0; o < i.Operands.Count; o++)
                        if (i.Operands[o] is RegOperand { Reg: var r } && through.TryGetValue(r, out VReg? copy)) i.Operands[o] = RegOperand.Of(copy);
                if (i.Dest is not { } dest) continue;
                // The copy written again: its temporaries are themselves again.
                if (by.Remove(dest, out List<VReg>? reads)) foreach (VReg t in reads) through.Remove(t);
                if (i.Op == Opcode.Copy && i.Operands is [RegOperand { Reg: var from }] && from != dest && from.Type == dest.Type
                    && written.Contains(from) && !elsewhere.Contains(from) && writes.GetValueOrDefault(from) == 1)
                {
                    through[from] = dest;
                    (by.TryGetValue(dest, out List<VReg>? list) ? list : by[dest] = new()).Add(from);
                }
                written.Add(dest);
            }
        }
    }
}
