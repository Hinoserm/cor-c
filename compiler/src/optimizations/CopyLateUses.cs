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
/// ecx,...` would do. Run last, after every pass that forwards copies: where
/// t is written once, in the copy's block before it, and read nowhere but
/// there, each read of it after the copy -- with i not written again before
/// it -- reads i instead, the copy is t's last read, and the allocator gives
/// the two one register (the coalescing GCC's and LLVM's register
/// coalescers make of a copy whose source dies there).
/// </summary>
public sealed class CopyLateUses : IModulePass
{
    public string Name => "copy-late-uses";

    public void Run(Module module)
    {
        foreach (Function function in module.Functions) Run(function);
    }

    private static void Run(Function function)
    {
        if (function.Async is not null) return;
        // Every read of each register, by the block it is in: a temporary
        // read in another block is live out of its own, and left alone.
        Dictionary<VReg, Block>? readIn = null;
        HashSet<VReg>? elsewhere = null;
        foreach (Block block in function.Blocks)
            foreach (Instr i in block.Instrs)
                foreach (Operand o in i.Operands)
                    if (o is RegOperand { Reg: var r })
                    {
                        readIn ??= new();
                        elsewhere ??= new();
                        if (readIn.TryGetValue(r, out Block? seen) && !ReferenceEquals(seen, block)) elsewhere.Add(r);
                        else readIn[r] = block;
                    }
        if (readIn is null) return;
        Dictionary<VReg, int> writes = new();
        foreach (Block block in function.Blocks)
            foreach (Instr i in block.Instrs)
                if (i.Dest is { } d) writes[d] = writes.TryGetValue(d, out int n) ? n + 1 : 1;

        foreach (Block block in function.Blocks)
        {
            if (block.IsLandingPad) continue;
            for (int k = 0; k < block.Instrs.Count; k++)
            {
                Instr copy = block.Instrs[k];
                if (copy.Op != Opcode.Copy || copy.Dest is not { } into || copy.Operands is not [RegOperand { Reg: var from }]
                    || from == into || from.Type != into.Type || elsewhere!.Contains(from) || writes.GetValueOrDefault(from) != 1) continue;
                // Written in this block, before the copy.
                bool here = false;
                for (int j = 0; j < k && !here; j++) here = block.Instrs[j].Dest == from;
                if (!here) continue;
                for (int j = k + 1; j < block.Instrs.Count; j++)
                {
                    Instr use = block.Instrs[j];
                    for (int o = 0; o < use.Operands.Count; o++)
                        if (use.Operands[o] is RegOperand { Reg: var r } && r == from) use.Operands[o] = RegOperand.Of(into);
                    if (use.Dest == into) break;
                }
            }
        }
    }
}
