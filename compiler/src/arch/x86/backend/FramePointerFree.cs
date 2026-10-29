#nullable enable
using Corsac.Lang.Ir;

namespace Corsac.Lang.X86;

/// <summary>
/// A FUNCTION WITHOUT A FRAME POINTER made so, last before it is written: a
/// leaf the allocator gave EBP a value in (MFunction.FramePointerFree). Every
/// pass until here took an operand based on EBP for the frame and the value
/// in EBP as MReg.EbpData; now the frame is addressed off ESP -- which
/// nothing moves between the prologue and the epilogue, ESP + K being where
/// EBP would point, K the frame's size and the saved registers' pushes -- and
/// the value is in EBP. The prologue still sets EBP before anything is
/// written to it, and marks the function so a fault finds its frame
/// (Encoder.EncodePrologue).
/// </summary>
internal static class FramePointerFree
{
    public static void Run(MFunction m)
    {
        if (!m.FramePointerFree) return;
        int k = m.Frame.Size + 4 * m.SavedRegs.Count;
        List<(MReg Reg, Roles.Role Role, int Operand, bool InMem)> regs = new();
        foreach (MBlock b in m.Blocks)
        {
            foreach (MInstr i in b.Instrs)
            {
                if (i.Op is MOp.Call or MOp.CallInd or MOp.Push or MOp.Pop or MOp.Pushfd or MOp.Popfd)
                    throw new InvalidOperationException($"{m.Source.Name}: {i.Op} in a function without a frame pointer");
                foreach ((MReg r, Roles.Role role, int operand, bool inMem) in Allocator.RegsOf(i, regs))
                    if (r.Id == (int)Gpr.Esp && (role & Roles.Role.Def) != 0)
                        throw new InvalidOperationException($"{m.Source.Name}: ESP written in a function without a frame pointer");
                for (int o = 0; o < i.Operands.Count; o++)
                {
                    switch (i.Operands[o])
                    {
                        case MReg { Id: MReg.EbpDataId }:
                            i.Operands[o] = MReg.Of(Gpr.Ebp);
                            break;
                        case MMem mem when mem.Base?.Id == (int)Gpr.Ebp:
                            i.Operands[o] = new MMem(MReg.Of(Gpr.Esp), mem.Disp + k)
                            {
                                Index = mem.Index?.Id == MReg.EbpDataId ? MReg.Of(Gpr.Ebp) : mem.Index, Scale = mem.Scale,
                                Symbol = mem.Symbol, Reloc = mem.Reloc, Label = mem.Label, IsSpill = mem.IsSpill, Device = mem.Device,
                            };
                            break;
                        case MMem mem when mem.Base?.Id == MReg.EbpDataId || mem.Index?.Id == MReg.EbpDataId:
                            if (mem.Base?.Id == MReg.EbpDataId) mem.Base = MReg.Of(Gpr.Ebp);
                            if (mem.Index?.Id == MReg.EbpDataId) mem.Index = MReg.Of(Gpr.Ebp);
                            break;
                    }
                }
            }
        }
    }
}
