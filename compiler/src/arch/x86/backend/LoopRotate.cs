#nullable enable

namespace Corsac.Lang.X86;

/// <summary>
/// THE LOOP'S TEST AT THE BOTTOM AS WELL. Lowering puts a `for` or `while`
/// test in a block of its own at the top, and the body ends with a jump back
/// to it: every lap ran the body's `jmp`, then the test's compare and its
/// conditional jump out -- two branches a lap where one does.
///
///     test:  cmp i, n ; jge exit          test:  cmp i, n ; jge exit
///     body:  ...                    =>    body:  ...
///            jmp test                            cmp i, n ; jl body
///     exit:                               exit:
///
/// After layout, a block that ends with `jmp T`, where T is laid out before
/// it and is a short test -- a few instructions that only move, compute and
/// compare, ending in a conditional jump and falling through or jumping on
/// -- takes a copy of T in place of the jump, its fall-through made an
/// explicit jump. The entry still runs the test once at the top. Peephole
/// then turns `jcc exit ; jmp body` with the exit next into `j!cc body`.
///
/// Registers are allocated by now, so the copy reads and writes exactly what
/// the original does, with the same values: control reaches it with the
/// machine in the state it would have had arriving at T.
/// </summary>
internal static class LoopRotate
{
    /// <summary>The most instructions a test may have, besides its jumps, to be copied.</summary>
    private const int Short = 4;

    public static void Run(MFunction m)
    {
        Dictionary<MBlock, int> index = new(ReferenceEqualityComparer.Instance);
        for (int b = 0; b < m.Blocks.Count; b++) index[m.Blocks[b]] = b;
        for (int b = 0; b < m.Blocks.Count; b++)
        {
            MBlock latch = m.Blocks[b];
            if (latch.Instrs.Count == 0 || latch.Instrs[^1] is not { Op: MOp.Jmp } back
                || back.Operands[0] is not MLabel { Target: var test } || !index.TryGetValue(test, out int t)
                || t >= b || ReferenceEquals(test, latch))
                continue;
            if (Copy(m, test, t) is not { } copy || !LoopShaped(m, copy, index, test, latch)) continue;
            latch.Instrs.RemoveAt(latch.Instrs.Count - 1);
            latch.Instrs.AddRange(copy);
        }
    }

    /// <summary>The test block's instructions copied, ending in explicit jumps; null when it is not a short test.</summary>
    private static List<MInstr>? Copy(MFunction m, MBlock test, int at)
    {
        List<MInstr> instrs = test.Instrs;
        int body = 0, jcc = -1;
        for (int k = 0; k < instrs.Count; k++)
        {
            MInstr i = instrs[k];
            if (i.Op == MOp.Jcc)
            {
                // The conditional jump, then nothing but an optional jmp.
                if (jcc >= 0) return null;
                jcc = k;
                continue;
            }
            if (i.Op == MOp.Jmp)
            {
                if (jcc < 0 || k != instrs.Count - 1) return null;
                continue;
            }
            if (jcc >= 0) return null;
            if (i.Op is not (MOp.Mov or MOp.Movzx or MOp.Movsx or MOp.Lea or MOp.Add or MOp.Sub or MOp.And or MOp.Or
                    or MOp.Xor or MOp.Cmp or MOp.Test or MOp.Shl or MOp.Shr or MOp.Sar or MOp.Imul or MOp.Imul3 or MOp.Neg or MOp.Setcc)
                || i.Lock || ++body > Short)
                return null;
        }
        if (jcc < 0) return null;
        List<MInstr> copy = new(instrs.Count + 1);
        foreach (MInstr i in instrs) copy.Add(Clone(i));
        if (instrs[^1].Op != MOp.Jmp)
        {
            // It fell through into the block after it: say so.
            if (at + 1 >= m.Blocks.Count) return null;
            copy.Add(new MInstr(MOp.Jmp, new MLabel(m.Blocks[at + 1])) { Line = instrs[^1].Line });
        }
        return copy;
    }

    /// <summary>
    /// Whether the test is a loop's: one way on reaches the latch again
    /// without passing the test -- the body -- and the other does not. A jump
    /// back to a join of an if's arms, which ends in a test too, is no loop,
    /// and copying such tests everywhere grew the code by a tenth.
    /// </summary>
    private static bool LoopShaped(MFunction m, List<MInstr> copy, Dictionary<MBlock, int> index, MBlock test, MBlock latch)
    {
        int inside = 0, outside = 0;
        foreach (MInstr i in copy)
        {
            if (i.Op is not (MOp.Jcc or MOp.Jmp)) continue;
            if (i.Operands[0] is not MLabel { Target: var to }) return false;
            if (Reaches(m, index, to, latch, test)) inside++;
            else outside++;
        }
        return inside == 1 && outside == 1;
    }

    /// <summary>Whether `target` is reached from `from` (itself included) without passing `avoid`.</summary>
    private static bool Reaches(MFunction m, Dictionary<MBlock, int> index, MBlock from, MBlock target, MBlock avoid)
    {
        HashSet<MBlock> seen = new(ReferenceEqualityComparer.Instance);
        Stack<MBlock> work = new();
        work.Push(from);
        while (work.TryPop(out MBlock? b))
        {
            if (ReferenceEquals(b, target)) return true;
            if (ReferenceEquals(b, avoid) || !seen.Add(b)) continue;
            foreach (MInstr i in b.Instrs)
            {
                if (i.Op is MOp.Jcc or MOp.Jmp && i.Operands.Count > 0 && i.Operands[0] is MLabel { Target: var to }) work.Push(to);
                if (i.Op == MOp.JmpTable && i.Table is { } table) foreach (MBlock t in table) work.Push(t);
            }
            if (!b.EndsUnconditionally && index.TryGetValue(b, out int at) && at + 1 < m.Blocks.Count) work.Push(m.Blocks[at + 1]);
        }
        return false;
    }

    private static MInstr Clone(MInstr i)
    {
        MInstr made = new(i.Op) { Width = i.Width, Cond = i.Cond, Native = i.Native, Lock = i.Lock, Table = i.Table, CallReloc = i.CallReloc, Line = i.Line };
        foreach (MOperand o in i.Operands)
            made.Operands.Add(o is MMem mem
                ? new MMem(mem.Base, mem.Disp) { Index = mem.Index, Scale = mem.Scale, Symbol = mem.Symbol, Reloc = mem.Reloc, Label = mem.Label, IsSpill = mem.IsSpill }
                : o);
        return made;
    }
}
