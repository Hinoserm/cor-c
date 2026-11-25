#nullable enable
using Corsac.Lang.X86;

namespace Corsac.Lang.X64;

/// <summary>
/// x86-64 REGISTER ALLOCATION: the function lowered into the one allocator's
/// form (X64Target) and allocated there (Corsac.Lang.RegAlloc.Allocator), with
/// i386's method and everything it has learnt -- holes, short reloads carried
/// along a run, a register kept over the reads before a spill point, a loop's
/// lap cost; this file holds what is x86-64's alone: which registers an
/// instruction names and which of its operands may be memory.
/// </summary>
internal static class Allocator
{
    internal static Gpr[] CalleeSaved => LongAbi.CalleeSaved;

    public static void Run(MFunction m)
    {
        PruneUnusedPoolLoads(m);
        Corsac.Lang.RegAlloc.Allocator.Run(new X64Target(m).Lower());
    }

    /// <summary>
    /// A FLOAT CONSTANT'S OWN LOAD, WHERE EVERY READER TOOK THE POOL'S WORD
    /// ITSELF (Select.Sse's XSource: `addsd xmm, [rip + constant]`): the
    /// register it filled is read by nothing, and the load goes -- a
    /// read-only word read for no one, which no later pass removes where the
    /// register is not written again in its block. i386's allocator drops
    /// these among the unused values it prunes (its PruneUnusedValues, by
    /// i386's own reading of what an instruction names); here only the
    /// pool's loads are taken, judged by long mode's (RegsOf).
    /// </summary>
    private static void PruneUnusedPoolLoads(MFunction m)
    {
        static bool PoolLoad(MInstr i) => i.Op == MOp.MovF && i.Operands.Count == 2 && i.Operands[0] is MReg { IsMachine: false }
            && i.Operands[1] is MMem { Base: null, Index: null, Symbol: { } pool } && pool.StartsWith(X86Backend.FloatPoolPrefix, StringComparison.Ordinal);
        bool any = false;
        foreach (MBlock b in m.Blocks)
            foreach (MInstr i in b.Instrs)
                if (PoolLoad(i)) { any = true; break; }
        if (!any) return;
        HashSet<int> read = new();
        foreach (MBlock b in m.Blocks)
            foreach (MInstr i in b.Instrs)
                foreach ((MReg reg, Roles.Role role, int _, bool _) in RegsOf(i))
                    if (role != Roles.Role.Def) read.Add(reg.Id);
        foreach (MBlock b in m.Blocks)
            b.Instrs.RemoveAll(i => PoolLoad(i) && !read.Contains(((MReg)i.Operands[0]).Id));
    }

    internal static IEnumerable<(MReg Reg, Roles.Role Role, int Operand, bool InMem)> RegsOf(MInstr i)
    {
        // `xor v, v` and `xorps x, x` make zero whatever v held: a definition only.
        bool zeroing = (i.Op is MOp.Xor or MOp.XorF) && i.Operands.Count == 2
            && i.Operands[0] is MReg x && i.Operands[1] is MReg y && x.Id == y.Id;
        for (int k = 0; k < i.Operands.Count; k++)
        {
            switch (i.Operands[k])
            {
                case MReg r:
                    yield return (r, zeroing ? Roles.Role.Def : Roles.Of(i.Op, k), k, false);
                    break;
                case MMem m:
                    if (m.Base is not null)
                    {
                        yield return (m.Base, Roles.Role.Use, k, true);
                    }
                    if (m.Index is not null)
                    {
                        yield return (m.Index, Roles.Role.Use, k, true);
                    }
                    break;
            }
        }
        foreach (int g in Implicit.Uses(i))
        {
            yield return (new MReg(g), Roles.Role.Use, -1, false);
        }
        foreach (int g in Implicit.Defs(i))
        {
            yield return (new MReg(g), Roles.Role.Def, -1, false);
        }
    }

    /// <summary>Whether an operand position may be a memory operand: a spill slot read or written in place.</summary>
    internal static bool Foldable(MFunction m, MInstr i, int operand)
    {
        switch (i.Op)
        {
            case MOp.Mov:
                // Never a memory destination for a 64-bit immediate: there is no such form.
                return operand == 1 || i.Operands[1] is not MImm { FitsInt32: false };
            case MOp.Add:
            case MOp.Sub:
            case MOp.And:
            case MOp.Or:
            case MOp.Xor:
            case MOp.Adc:
            case MOp.Sbb:
            case MOp.Cmp:
                return true;
            case MOp.Push:
            case MOp.Test:
            case MOp.MulWide:
            case MOp.ImulWide:
            case MOp.Div:
            case MOp.Idiv:
            case MOp.Neg:
            case MOp.Not:
            case MOp.Shl:
            case MOp.Shr:
            case MOp.Sar:
            case MOp.Rol:
            case MOp.Ror:
            case MOp.Setcc:
            case MOp.CallInd:
                return operand == 0;
            case MOp.Imul:
            case MOp.Imul3:
            case MOp.Movsx:
            case MOp.Movzx:
            case MOp.AddF:
            case MOp.SubF:
            case MOp.MulF:
            case MOp.DivF:
            case MOp.SqrtF:
            case MOp.UcomiF:
            case MOp.CvtIntToF:
            case MOp.CvtFToInt:
            case MOp.CvtFToF:
                return operand == 1;
            case MOp.MovF:
                return true;
            case MOp.MovGx:
                // The general register's side may be memory; the XMM side may
                // not. (By the function's record of which registers are XMM
                // ones: a renamed operand does not say, MReg.IsFloat.)
                return i.Operands[operand] is MReg r && !r.IsFloat && !m.XmmRegs.Contains(r.Id);
            default:
                return false;
        }
    }

}
