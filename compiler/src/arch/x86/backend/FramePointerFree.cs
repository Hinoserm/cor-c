#nullable enable
using Corsac.Lang.Ir;

namespace Corsac.Lang.X86;

/// <summary>
/// A FUNCTION WITHOUT A FRAME made so, last before it is written: a leaf
/// (MFunction.Frameless). Every pass until here took an operand based on EBP
/// for the frame, and the value EBP holds in a leaf that gives it one as
/// MReg.EbpData; now the frame is addressed off ESP, and the value is in EBP.
/// The leaf pushes the registers it saves -- EBP first where it gives it a
/// value -- and then reserves its frame, so from the return address down:
/// the saved registers (S of them), then the frame (N bytes), ESP at its
/// bottom, the return address at ESP + N + 4S. Where EBP would have pointed,
/// a word below the return address, an argument at EBP + D (D 8 or more)
/// is at ESP + N + 4S - 4 + D; the frame's own slots, below where EBP would
/// have pointed by D (D negative), are below the saved registers: at ESP + N
/// + D. Nothing moves ESP between the prologue and the epilogue.
/// </summary>
internal static class FramePointerFree
{
    public static void Run(MFunction m)
    {
        if (!m.Frameless) return;
        int frame = m.Frame.Size, saved = 4 * m.SavedRegs.Count;
        int Address(int disp) => disp switch
        {
            >= 8 => frame + saved - 4 + disp,
            < 0 => frame + disp,
            // Where EBP would point, and the return address: no slot of a
            // frameless function's is there.
            _ => throw new InvalidOperationException($"{m.Source.Name}: a frame slot at EBP + {disp} in a function without a frame"),
        };
        List<(MReg Reg, Roles.Role Role, int Operand, bool InMem)> regs = new();
        foreach (MBlock b in m.Blocks)
        {
            foreach (MInstr i in b.Instrs)
            {
                if (i.Op is MOp.Call or MOp.CallInd or MOp.CallKeep or MOp.CallKeepInd or MOp.CallKeepEax or MOp.CallKeepEaxInd
                    or MOp.Push or MOp.Pop or MOp.Pushfd or MOp.Popfd)
                    throw new InvalidOperationException($"{m.Source.Name}: {i.Op} in a function without a frame");
                foreach ((MReg r, Roles.Role role, int operand, bool inMem) in Allocator.RegsOf(i, regs))
                    if (r.Id == (int)Gpr.Esp && (role & Roles.Role.Def) != 0)
                        throw new InvalidOperationException($"{m.Source.Name}: ESP written in a function without a frame");
                for (int o = 0; o < i.Operands.Count; o++)
                {
                    switch (i.Operands[o])
                    {
                        case MReg { Id: MReg.EbpDataId }:
                            i.Operands[o] = MReg.Of(Gpr.Ebp);
                            break;
                        case MMem mem when mem.Base?.Id == (int)Gpr.Ebp:
                            i.Operands[o] = new MMem(MReg.Of(Gpr.Esp), Address(mem.Disp))
                            {
                                Index = mem.Index?.Id == MReg.EbpDataId ? MReg.Of(Gpr.Ebp) : mem.Index, Scale = mem.Scale,
                                Symbol = mem.Symbol, Reloc = mem.Reloc, Label = mem.Label, IsSpill = mem.IsSpill, Device = mem.Device,
                            };
                            break;
                        // A new operand, not this one changed: one shared by two
                        // instructions, met again, would be taken for the frame.
                        case MMem mem when mem.Base?.Id == MReg.EbpDataId || mem.Index?.Id == MReg.EbpDataId:
                            i.Operands[o] = new MMem(mem.Base?.Id == MReg.EbpDataId ? MReg.Of(Gpr.Ebp) : mem.Base, mem.Disp)
                            {
                                Index = mem.Index?.Id == MReg.EbpDataId ? MReg.Of(Gpr.Ebp) : mem.Index, Scale = mem.Scale,
                                Symbol = mem.Symbol, Reloc = mem.Reloc, Label = mem.Label, IsSpill = mem.IsSpill, Device = mem.Device,
                            };
                            break;
                    }
                }
            }
        }
    }
}
