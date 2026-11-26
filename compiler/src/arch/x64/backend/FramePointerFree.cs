#nullable enable
using Corsac.Lang.X86;

namespace Corsac.Lang.X64;

/// <summary>
/// A FUNCTION WITHOUT A FRAME POINTER made so, last before it is written: a
/// leaf the allocator gave RBP a value in (MFunction.FramePointerFree). Every
/// pass until here took an operand based on RBP for the frame and the value
/// in RBP as MReg.RbpData; now the frame is addressed off RSP -- which
/// nothing moves between the prologue and the epilogue, RSP + K being where
/// RBP would point, K the frame the prologue makes (Encoder.FrameBytes) --
/// and the value is in RBP. The prologue still sets RBP before anything is
/// written to it, and marks the function so a fault finds its frame
/// (Encoder.PrologueL).
/// </summary>
internal static class FramePointerFree
{
    public static void Run(MFunction m)
    {
        if (!m.FramePointerFree) return;
        int k = Encoder.FrameBytes(m);
        List<(MReg Reg, Roles.Role Role, int Operand, bool InMem)> regs = new();
        foreach (MBlock b in m.Blocks)
        {
            foreach (MInstr i in b.Instrs)
            {
                if (i.Op is MOp.Call or MOp.CallInd or MOp.CallSafe or MOp.CallKeep or MOp.CallKeepRax or MOp.CallKeepCas
                    or MOp.Push or MOp.Pop or MOp.Pushf or MOp.Popf)
                    throw new InvalidOperationException($"{m.Source.Name}: {i.Op} in a function without a frame pointer");
                foreach ((MReg r, Roles.Role role, int operand, bool inMem) in Roles.RegsOf(i, regs))
                    if (r.Id == (int)Gpr.Rsp && (role & Roles.Role.Def) != 0)
                        throw new InvalidOperationException($"{m.Source.Name}: RSP written in a function without a frame pointer");
                for (int o = 0; o < i.Operands.Count; o++)
                {
                    switch (i.Operands[o])
                    {
                        case MReg { Id: MReg.RbpDataId }:
                            i.Operands[o] = MReg.Of(Gpr.Rbp);
                            break;
                        case MMem mem when mem.Base?.Id == (int)Gpr.Rbp:
                            i.Operands[o] = mem.WithRegisters(MReg.Of(Gpr.Rsp), mem.Index?.Id == MReg.RbpDataId ? MReg.Of(Gpr.Rbp) : mem.Index).WithDisp(k);
                            break;
                        // A new operand, not this one changed: one shared by two
                        // instructions, met again, would be taken for the frame.
                        case MMem mem when mem.Base?.Id == MReg.RbpDataId || mem.Index?.Id == MReg.RbpDataId:
                            i.Operands[o] = mem.WithRegisters(mem.Base?.Id == MReg.RbpDataId ? MReg.Of(Gpr.Rbp) : mem.Base,
                                mem.Index?.Id == MReg.RbpDataId ? MReg.Of(Gpr.Rbp) : mem.Index);
                            break;
                    }
                }
            }
        }
    }
}
