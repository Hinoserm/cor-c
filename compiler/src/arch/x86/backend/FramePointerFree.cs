#nullable enable
using Corsac.Lang.Ir;

namespace Corsac.Lang.X86;

/// <summary>
/// A FUNCTION WITHOUT A FRAME POINTER made so, last before it is written,
/// in both modes: the one walk that moves its operands off the frame's base
/// (Readdress), and each mode's layout -- long mode's below (RunLong),
/// i386's here.
///
/// i386: a leaf
/// (MFunction.Frameless), or one whose only calls never return, which makes
/// its frame on the way to them alone (MFunction.ColdFrames, OnlyColdCalls).
/// Every pass until here took an operand based on EBP for the frame, and the value EBP holds in a leaf that gives it one as
/// MReg.EbpData; now the frame is addressed off ESP, and the value is in EBP.
/// The leaf pushes the registers it saves -- EBP first where it gives it a
/// value -- and then reserves its frame, so from the return address down:
/// the saved registers (S of them), then the frame (N bytes), ESP at its
/// bottom, the return address at ESP + N + 4S. Where EBP would have pointed,
/// a word below the return address, an argument at EBP + D (D 8 or more)
/// is at ESP + N + 4S - 4 + D; the frame's own slots, below where EBP would
/// have pointed by D (D negative), are below the saved registers: at ESP + N
/// + D. Nothing moves ESP between the prologue and the epilogue.
///
/// A cold-framed function that saves registers or keeps slots
/// (MFunction.ReservedFrame) is laid out the other way round, as a framed
/// function is: from the return address down, EBP's word (reserved, not
/// written), the frame, then the saves; EBP + D is ESP + N + 4S + D, slot or
/// argument alike.
/// </summary>
internal static class FramePointerFree
{
    public static void Run(MFunction m)
    {
        if (X86Mode.Long)
        {
            RunLong(m);
            return;
        }
        if (!m.Frameless && OnlyColdCalls(m))
        {
            m.Frameless = true;
            m.ColdFrames = true;
            m.ReservedFrame = m.SavedRegs.Count > 0 || m.Frame.Size > 0;
        }
        // A reserved frame costs eight bytes at each such call where the
        // plain one costs three: at most two of them, and a frame and saves
        // a byte's displacement covers (else the frame stays a prologue's).
        if (m.ReservedFrame && (ColdCalls(m) > 2 || m.Frame.Size + 4 * m.SavedRegs.Count > 127))
        {
            m.Frameless = false;
            m.ColdFrames = false;
            m.ReservedFrame = false;
            return;
        }
        if (!m.Frameless) return;
        int frame = m.Frame.Size, saved = 4 * m.SavedRegs.Count;
        // (A reserved frame is a framed function's: where EBP would point is
        // ESP + N + 4S, every slot and argument as far from it.)
        int Address(int disp) => m.ReservedFrame ? frame + saved + disp : disp switch
        {
            >= 8 => frame + saved - 4 + disp,
            < 0 => frame + disp,
            // Where EBP would point, and the return address: no slot of a
            // frameless function's is there.
            _ => throw new InvalidOperationException($"{m.Source.Name}: a frame slot at EBP + {disp} in a function without a frame"),
        };
        Readdress(m, Address, !m.ColdFrames);
    }

    /// <summary>
    /// LONG MODE'S: a leaf the allocator gave RBP a value in
    /// (MFunction.FramePointerFree). The frame is addressed off RSP -- which
    /// nothing moves between the prologue and the epilogue, RSP + K being
    /// where RBP would point, K the frame the prologue makes
    /// (Encoder.FrameBytes) -- and the value is in RBP. The prologue still
    /// sets RBP before anything is written to it, and marks the function so
    /// a fault finds its frame (Encoder.PrologueL). No function is without
    /// a frame there yet, and none makes its frame only on the way to a call.
    /// </summary>
    private static void RunLong(MFunction m)
    {
        if (!m.FramePointerFree) return;
        int k = Encoder.FrameBytes(m);
        Readdress(m, disp => disp + k, true);
    }

    /// <summary>
    /// EVERY OPERAND MOVED OFF THE FRAME'S BASE, for both modes: one based on
    /// the base register becomes one off the stack pointer at `address` of
    /// its displacement, and the value the base register holds
    /// (MReg.EbpData), as an operand or in one, becomes the register itself.
    /// Refused, not miscompiled, where something moves the stack pointer --
    /// a call (but a cold-framed function's own, `calls` false, which make
    /// their frame on the way), a store sequence's stub's, a push or a pop
    /// -- or writes it: the allocator's leaf test and OnlyColdCalls let no
    /// such function here.
    /// </summary>
    private static void Readdress(MFunction m, Func<int, int> address, bool calls)
    {
        List<(MReg Reg, Roles.Role Role, int Operand, bool InMem)> regs = new();
        foreach (MBlock b in m.Blocks)
        {
            foreach (MInstr i in b.Instrs)
            {
                if ((i.Op is MOp.Call or MOp.CallInd && calls) || i.Op is MOp.CallKeep or MOp.CallKeepInd or MOp.CallKeepEax or MOp.CallKeepEaxInd
                    or MOp.CallKeepRax or MOp.CallKeepCas or MOp.CallSafe or MOp.Push or MOp.Pop or MOp.Pushf or MOp.Popf)
                    throw new InvalidOperationException($"{m.Source.Name}: {i.Op} in a function without a frame pointer");
                foreach ((MReg r, Roles.Role role, int operand, bool inMem) in Roles.RegsOf(i, regs))
                    if (r.Id == (int)Gpr.Esp && (role & Roles.Role.Def) != 0)
                        throw new InvalidOperationException($"{m.Source.Name}: the stack pointer written in a function without a frame pointer");
                for (int o = 0; o < i.Operands.Count; o++)
                {
                    switch (i.Operands[o])
                    {
                        case MReg { Id: MReg.EbpDataId }:
                            i.Operands[o] = MReg.Of(Gpr.Ebp);
                            break;
                        // A new operand, not this one changed: one shared by two
                        // instructions, met again, would be taken for the frame.
                        case MMem mem when mem.Base?.Id == (int)Gpr.Ebp:
                            i.Operands[o] = mem.WithRegisters(MReg.Of(Gpr.Esp), mem.Index?.Id == MReg.EbpDataId ? MReg.Of(Gpr.Ebp) : mem.Index)
                                .WithDisp(address(mem.Disp) - mem.Disp);
                            break;
                        case MMem mem when mem.Base?.Id == MReg.EbpDataId || mem.Index?.Id == MReg.EbpDataId:
                            i.Operands[o] = mem.WithRegisters(mem.Base?.Id == MReg.EbpDataId ? MReg.Of(Gpr.Ebp) : mem.Base,
                                mem.Index?.Id == MReg.EbpDataId ? MReg.Of(Gpr.Ebp) : mem.Index);
                            break;
                    }
                }
            }
        }
    }

    /// <summary>How many calls a function makes.</summary>
    private static int ColdCalls(MFunction m) => m.Blocks.Sum(b => b.Instrs.Count(i => i.Op is MOp.Call or MOp.CallInd));

    /// <summary>
    /// Whether a function with a frame may have none but on the way to its
    /// calls (MFunction.ColdFrames): every call one that never returns (the
    /// selector's unreachable INT3 after it, straight or after a trap), none
    /// through memory, no aligned slots, no landing pad, no asm,
    /// nothing that names EBP but as the base of a slot or an argument (not
    /// its own word or the return address's), and
    /// nothing that pushes, pops or writes ESP.
    /// </summary>
    private static bool OnlyColdCalls(MFunction m)
    {
        if (m.AlignedArea != 0 || m.FramePointerFree
            || m.Source.KeepsCallerRegisters || m.Source.Blocks.Any(b => b.IsLandingPad)) return false;
        bool calls = false;
        List<(MReg Reg, Roles.Role Role, int Operand, bool InMem)> regs = new();
        foreach (MBlock b in m.Blocks)
        {
            for (int k = 0; k < b.Instrs.Count; k++)
            {
                MInstr i = b.Instrs[k];
                if (i.Op is MOp.Call or MOp.CallInd)
                {
                    // (Not through memory: an argument's word above the frame
                    // is ESP-based after this, and the frame's two pushes,
                    // before the call reads it, would move it.)
                    // Never returning: the selector's unreachable (INT3, no
                    // operand) after it, straight or after a trap -- a trap
                    // the program may run on from (INT3 1) alone is a call
                    // that comes back, past a frame never undone.
                    int after = k + 1;
                    if (after < b.Instrs.Count && b.Instrs[after] is { Op: MOp.Int3, Operands.Count: > 0 }) after++;
                    if (after >= b.Instrs.Count || b.Instrs[after] is not { Op: MOp.Int3, Operands.Count: 0 } || i.Operands.Count > 0 && i.Operands[0] is MMem) return false;
                    calls = true;
                }
                if (i.Op is MOp.CallKeep or MOp.CallKeepInd or MOp.CallKeepEax or MOp.CallKeepEaxInd or MOp.CallSafe or MOp.Push or MOp.Pop
                    or MOp.Pushf or MOp.Popf or MOp.CallerReg or MOp.InlineAsm or MOp.SaveRegs) return false;
                foreach (MOperand o in i.Operands)
                {
                    if (o is MReg { Id: (int)Gpr.Ebp or MReg.EbpDataId }) return false;
                    if (o is MMem mem && (mem.Index?.Id is (int)Gpr.Ebp or MReg.EbpDataId || mem.Base?.Id == MReg.EbpDataId
                        || mem.Base?.Id == (int)Gpr.Ebp && mem.Disp is >= 0 and < 8)) return false;
                }
                foreach ((MReg r, Roles.Role role, int operand, bool inMem) in Roles.RegsOf(i, regs))
                    if (r.Id == (int)Gpr.Esp && (role & Roles.Role.Def) != 0) return false;
            }
        }
        return calls;
    }
}
