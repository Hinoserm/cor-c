#nullable enable
using Corsac.Lang.Ir;

namespace Corsac.Lang.X86;

/// <summary>
/// THE MACHINE INTRINSICS THAT ARE ONE INSTRUCTION IN BOTH MODES
/// (MachineIntrinsics), selected once: each was written twice, i386's in
/// Select.cs and long mode's in Select.Long.cs, differing only in how a
/// mode names a word's register. What differs here is said by the mode's
/// own makers of operands (WordReg, Whole) and by the width an instruction
/// takes when none is given, which is the mode's word.
///
/// What stays each mode's own (SelectMachineIntrinsic,
/// SelectMachineIntrinsicL) is what is not the same instruction: the
/// 64-bit values of MSRs and the time stamp, the control registers'
/// probes, the descriptor tables' operands, the thread block, the
/// collector's store sequences, port output and the string ports (whose
/// operands i386 may read from memory), and the packed and streaming
/// routines' instructions.
/// </summary>
internal sealed partial class Selector
{
    /// <summary>A word operand in a register: the mode's maker of one.</summary>
    private MReg WordReg(Operand o) => X86Mode.Long ? RL(o) : R(o);

    /// <summary>The register a word result is written to: long mode's whole register, i386's (only) half.</summary>
    private MReg Whole(VReg d) => X86Mode.Long ? V(d) : Lo(d);

    /// <summary>Selects `name` if it is one of the family's; false for one each mode selects itself.</summary>
    private bool SelectFamilyIntrinsic(string name, Instr i)
    {
        switch (name)
        {
            case MachineIntrinsics.Cli:
                Emit(MOp.Cli);
                return true;
            case MachineIntrinsics.Sti:
                Emit(MOp.Sti);
                return true;
            case MachineIntrinsics.Hlt:
                Emit(MOp.Hlt);
                return true;
            // Every callee-saved register counted as written, no instruction made.
            case MachineIntrinsics.SaveRegisters:
                Emit(MOp.SaveRegs);
                return true;
            case MachineIntrinsics.In8:
            case MachineIntrinsics.In16:
            case MachineIntrinsics.In32:
            {
                int w = name == MachineIntrinsics.In8 ? 1 : name == MachineIntrinsics.In16 ? 2 : 4;
                MReg accumulator = MReg.Of(Gpr.Eax);
                Emit(MOp.Mov, MReg.Of(Gpr.Edx), X86Mode.Long ? RL(i.Operands[0]) : RM(i.Operands[0]));
                // The narrow forms write only AL or AX, so the rest of the
                // register is whatever was there: zero it first and the
                // result is the canonical unsigned byte or word the
                // language promises.
                if (w != 4) EmitW(MOp.Xor, 4, accumulator, accumulator);
                EmitW(MOp.In, w);
                // A read whose value is dropped -- the 400 ns delay of
                // reading a status port -- has no destination to move to.
                if (i.Dest is not null) EmitW(MOp.Mov, 4, Whole(i.Dest), accumulator);
                return true;
            }
            // pushf; pop r: no call to a stub for the one bit a lock or a
            // gate asks before it turns interrupts off.
            case MachineIntrinsics.ReadFlags:
                if (i.Dest is not null) Emit(MOp.ReadFlags, Whole(i.Dest));
                return true;
            // NOTHING WHERE NOTHING READS IT: `Runtime.NativeGs =
            // Sys.GetGs();` in a program that never reads NativeGs lost the
            // store and kept the read, with no register to read into.
            case MachineIntrinsics.GetGs:
                if (i.Dest is not null) Emit(MOp.GetGs, Whole(i.Dest));
                return true;
            case MachineIntrinsics.SetGs:
                Emit(MOp.SetGs, WordReg(i.Operands[0]));
                return true;
            case MachineIntrinsics.ReadCodeSegment:
                if (i.Dest is not null) Emit(MOp.ReadCs, Whole(i.Dest));
                return true;
            case MachineIntrinsics.LoadTaskRegister:
                Emit(MOp.Ltr, WordReg(i.Operands[0]));
                return true;
            case MachineIntrinsics.LoadCodeSegment:
                Emit(MOp.LoadCs, WordReg(i.Operands[0]));
                return true;
            // A use of its operand and nothing else: `test r, r`.
            case MachineIntrinsics.KeepAlive:
            {
                MReg kept = WordReg(i.Operands[0]);
                Emit(MOp.Test, kept, kept);
                return true;
            }
            // Read by nothing -- the note it was for dropped, a program
            // without a collector (Escape.IsThreadNote) -- nothing made.
            case MachineIntrinsics.CallerRegister:
                if (i.Dest is not null) Emit(MOp.CallerReg, Whole(i.Dest), new MImm(((ImmOperand)i.Operands[0]).Value));
                return true;
            // At the process entry, the loader's finaliser in the third
            // register (EDX, RDX). Unused when no C library is started:
            // nothing to read.
            case MachineIntrinsics.LoaderFini:
                if (i.Dest is not null) Emit(MOp.Mov, Whole(i.Dest), MReg.Of(Gpr.Edx));
                return true;
            // One instruction from the 386 on, of the 32-bit value; the
            // source is never zero (its caller asks first), where the
            // result is undefined.
            case MachineIntrinsics.BitScanForward:
            case MachineIntrinsics.BitScanReverse:
                if (i.Dest is not null)
                    EmitW(name == MachineIntrinsics.BitScanForward ? MOp.Bsf : MOp.Bsr, 4, Whole(i.Dest), WordReg(i.Operands[0]));
                return true;
            // One register of the leaf (subleaf 0), straight out of the
            // instruction: nothing stored, so the asker needs no memory to
            // ask with. Probed: its caller asked whether there is a CPUID.
            case MachineIntrinsics.CpuidEax:
            case MachineIntrinsics.CpuidEdx:
            {
                MReg leaf = WordReg(i.Operands[0]);
                EmitW(MOp.Mov, 4, MReg.Of(Gpr.Eax), leaf);
                EmitW(MOp.Mov, 4, MReg.Of(Gpr.Ecx), Imm(0));
                Emit(new MInstr(MOp.Cpuid) { Probed = true });
                if (i.Dest is not null) EmitW(MOp.Mov, 4, Whole(i.Dest), MReg.Of(name == MachineIntrinsics.CpuidEax ? Gpr.Eax : Gpr.Edx));
                return true;
            }
            // A cache line asked for ahead of the streaming copy: Probed,
            // the runtime reaches it only after Processor said it is there.
            case MachineIntrinsics.StreamPrefetch:
                Emit(new MInstr(MOp.PrefetchNta, new MMem(WordReg(i.Operands[0]), 0)) { Probed = true });
                return true;
            default:
                return false;
        }
    }
}
