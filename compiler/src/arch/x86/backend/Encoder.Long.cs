#nullable enable
using Corsac.Lang.Ir;

namespace Corsac.Lang.X86;

/// <summary>
/// LONG MODE'S FORMS, where they are not i386's with a REX prefix (which the
/// family's cases write as they stand: Encoder.EndInstr puts the prefix in).
/// What is here is what differs in kind: sixty-four-bit immediates and the
/// moves that carry them, MOVSXD, the stack operations (always the word, no
/// REX.W), calls and what a poll keeps, the frame (callee-saved registers
/// stored in slots below it, not pushed), jump tables read from the
/// instruction, and the opcodes only long mode has.
/// EncodeLong answers false for an opcode it has no form of its own for,
/// and the family's case writes it.
/// </summary>
internal sealed partial class Encoder
{
    /// <summary>Long mode: the bytes the prologue takes below the saved base, frame and saved registers, sixteen-aligned.</summary>
    private int _frameBytes;

    /// <summary>The bytes a long-mode function's prologue takes below its saved base: the frame and the callee-saved registers' slots, to a multiple of sixteen.</summary>
    public static int FrameBytes(MFunction m) => (m.Frame.Size + m.SavedRegs.Count * 8 + 15) & ~15;

    /// <summary>A register operand's whole hardware number (0 to 15), general or XMM; 0 for anything else.</summary>
    private static int Hw(MOperand o) => o is MReg r ? r.Hw : 0;

    /// <summary>
    /// An integer instruction with a ModRM, of the width given: the operand-
    /// size prefix for sixteen bits, REX.W for sixty-four, REX.R for a
    /// register field of eight or above (`reg` is a whole number, or an
    /// opcode's digit), and the REX a byte register SPL to DIL needs.
    /// </summary>
    private void OpL(int width, int reg, MOperand rm, params byte[] opcode)
    {
        if (width == 2) B(0x66);
        if (width == 8) _rex |= RexW8;
        if ((reg & 8) != 0) _rex |= RexR;
        if (width == 1 && (reg is >= 4 and <= 7 || rm is MReg { IsFloat: false, Hw: >= 4 and <= 7 })) _rexEmpty = true;
        B(opcode);
        XmmRM(reg & 7, rm);
    }

    /// <summary>An SSE instruction: its mandatory prefix, REX (W where asked), 0F and the opcode.</summary>
    private void SseL(byte prefix, bool wide, int reg, MOperand rm, byte opcode)
    {
        if (prefix != 0) B(prefix);
        if (wide) _rex |= RexW8;
        if ((reg & 8) != 0) _rex |= RexR;
        B(0x0F, opcode);
        XmmRM(reg & 7, rm);
    }

    private static byte ScalarL(int width) => width == 4 ? (byte)0xF3 : (byte)0xF2;

    /// <summary>A register in the opcode's own byte (`50+r`, `B8+r`): its low bits, REX.B for its fourth.</summary>
    private void OpReg(byte opcode, MReg r)
    {
        if ((r.Hw & 8) != 0) _rex |= RexB;
        _out.Add((byte)(opcode + (r.Hw & 7)));
    }

    private void BreakMergeL(MInstr i, int width)
    {
        if (i.Operands[0] is not MReg to || !CpuModel.BreaksMerge(width)) return;
        if (i.Op != MOp.CvtIntToF && i.Operands[1] is MReg from && from.Hw == to.Hw) return;
        if (CpuModel.ZeroByPxor) SseL(0x66, false, to.Hw, to, 0xEF);
        else SseL(0, false, to.Hw, to, 0x57);
        NextInstr();
    }

    private static readonly Gpr[] SafeSaved = { Gpr.Rax, Gpr.Rcx, Gpr.Rdx, Gpr.Rsi, Gpr.Rdi, Gpr.R8, Gpr.R9, Gpr.R10, Gpr.R11 };

    private void CallL(MImm target)
    {
        _out.Add(0xE8);
        _pending.Add(new Relocation(_out.Count, target.Symbol!, target.Value - 4, RelocKind.Plt32));
        D32(0);
    }

    /// <summary>Long mode's form of the instruction, where it has one of its own; false where the family's case is right.</summary>
    private bool EncodeLong(MInstr i)
    {
        // Each form says its own width (OpL): nothing is taken from the instruction's.
        _rex = 0;
        _byteOp = false;
        int w = i.Width;
        switch (i.Op)
        {
            case MOp.Mov: Lock(i); MovL(i, w); return true;
            case MOp.Movsx:
                // Width is the source's; the destination is 64 bits.
                if (w == 4) OpL(8, Hw(i.Operands[0]), i.Operands[1], 0x63);
                else
                {
                    if (w == 1 && i.Operands[1] is MReg { IsFloat: false, Hw: >= 4 and <= 7 }) _rexEmpty = true;
                    OpL(8, Hw(i.Operands[0]), i.Operands[1], 0x0F, w == 1 ? (byte)0xBE : (byte)0xBF);
                }
                return true;
            case MOp.Movzx:
                // Width is the source's; the destination is 32 bits, which zero-extends to 64.
                if (w == 1 && (Hw(i.Operands[0]) is >= 4 and <= 7 || i.Operands[1] is MReg { IsFloat: false, Hw: >= 4 and <= 7 })) _rexEmpty = true;
                OpL(4, Hw(i.Operands[0]), i.Operands[1], 0x0F, w == 1 ? (byte)0xB6 : (byte)0xB7);
                return true;
            case MOp.Lea: OpL(8, Hw(i.Operands[0]), i.Operands[1], 0x8D); return true;
            case MOp.Push: PushL(i.Operands[0]); return true;
            case MOp.Pop:
                if (i.Operands[0] is MReg pr) OpReg(0x58, pr);
                else OpL(4, 0, i.Operands[0], 0x8F);
                return true;
            case MOp.Add: Lock(i); AluL(0, i.Operands[0], i.Operands[1], w); return true;
            case MOp.Or: Lock(i); AluL(1, i.Operands[0], i.Operands[1], w); return true;
            case MOp.Adc: Lock(i); AluL(2, i.Operands[0], i.Operands[1], w); return true;
            case MOp.Sbb: Lock(i); AluL(3, i.Operands[0], i.Operands[1], w); return true;
            case MOp.And: Lock(i); AluL(4, i.Operands[0], i.Operands[1], w); return true;
            case MOp.Sub: Lock(i); AluL(5, i.Operands[0], i.Operands[1], w); return true;
            case MOp.Xor: Lock(i); AluL(6, i.Operands[0], i.Operands[1], w); return true;
            case MOp.Cmp: Lock(i); AluL(7, i.Operands[0], i.Operands[1], w); return true;
            case MOp.Test:
                if (i.Operands[1] is MImm ti)
                {
                    OpL(w, 0, i.Operands[0], w == 1 ? (byte)0xF6 : (byte)0xF7);
                    if (w == 1) _out.Add((byte)ti.Value); else if (w == 2) D16(ti.Value); else D32(ti.Value);
                }
                else OpL(w, Hw(i.Operands[1]), i.Operands[0], w == 1 ? (byte)0x84 : (byte)0x85);
                return true;
            case MOp.Imul: OpL(w, Hw(i.Operands[0]), i.Operands[1], 0x0F, 0xAF); return true;
            case MOp.Bsf: OpL(w, Hw(i.Operands[0]), i.Operands[1], 0x0F, 0xBC); return true;
            case MOp.Bsr: OpL(w, Hw(i.Operands[0]), i.Operands[1], 0x0F, 0xBD); return true;
            case MOp.Imul3:
            {
                MImm k = (MImm)i.Operands[2];
                if (k.FitsSbyte) { OpL(w, Hw(i.Operands[0]), i.Operands[1], 0x6B); _out.Add((byte)k.Value); }
                else { OpL(w, Hw(i.Operands[0]), i.Operands[1], 0x69); D32(k.Value); }
                return true;
            }
            case MOp.MulWide: Lock(i); OpL(w, 4, i.Operands[0], 0xF7); return true;
            case MOp.ImulWide: Lock(i); OpL(w, 5, i.Operands[0], 0xF7); return true;
            case MOp.Div: OpL(w, 6, i.Operands[0], 0xF7); return true;
            case MOp.Idiv: OpL(w, 7, i.Operands[0], 0xF7); return true;
            case MOp.Neg: Lock(i); OpL(w, 3, i.Operands[0], w == 1 ? (byte)0xF6 : (byte)0xF7); return true;
            // (FE/FF /0 and /1: long mode has no one-byte forms, their bytes its REX prefixes.)
            case MOp.Inc: Lock(i); OpL(w, 0, i.Operands[0], w == 1 ? (byte)0xFE : (byte)0xFF); return true;
            case MOp.Dec: Lock(i); OpL(w, 1, i.Operands[0], w == 1 ? (byte)0xFE : (byte)0xFF); return true;
            case MOp.Not: Lock(i); OpL(w, 2, i.Operands[0], w == 1 ? (byte)0xF6 : (byte)0xF7); return true;
            case MOp.Cwd:
                if (w == 8) _rex |= RexW8;
                _out.Add(0x99);
                return true;
            case MOp.Bswap:
                if (w == 8) _rex |= RexW8;
                _out.Add(0x0F);
                OpReg(0xC8, i.Reg(0));
                return true;
            case MOp.Rol: ShiftL(0, i, w); return true;
            case MOp.Ror: ShiftL(1, i, w); return true;
            case MOp.Shl: ShiftL(4, i, w); return true;
            case MOp.Shr: ShiftL(5, i, w); return true;
            case MOp.Sar: ShiftL(7, i, w); return true;
            case MOp.Setcc: OpL(1, 0, i.Operands[0], 0x0F, (byte)(0x90 + (int)i.Cond)); return true;
            case MOp.Cmov: OpL(w, Hw(i.Operands[0]), i.Operands[1], 0x0F, (byte)(0x40 + (int)i.Cond)); return true;
            case MOp.JmpTable:
                // jmp [table + index*8]: the table's address is in a register (the selector's LEA).
                OpL(4, 4, new MMem(i.Reg(1), 0) { Index = i.Reg(0), Scale = 8 }, 0xFF);
                Tables.Add((i.TableSymbol!, i.Table!));
                return true;
            case MOp.JmpInd: OpL(4, 4, i.Operands[0], 0xFF); return true;
            case MOp.Call: CallL((MImm)i.Operands[0]); return true;
            case MOp.CallInd: OpL(4, 2, i.Operands[0], 0xFF); return true;
            case MOp.CallSafe:
            {
                // The nine caller-saved general registers, and eight bytes more
                // so RSP is sixteen-aligned at the call as it was at the site.
                foreach (Gpr r in SafeSaved) { OpReg(0x50, MReg.Of(r)); NextInstr(); }
                B(0x48, 0x83, 0xEC, 0x08);                                   // sub rsp, 8
                NextInstr();
                CallL((MImm)i.Operands[0]);
                NextInstr();
                CallSites.Add((i, Here));
                B(0x48, 0x83, 0xC4, 0x08);                                   // add rsp, 8
                for (int k = SafeSaved.Length - 1; k >= 0; k--) { NextInstr(); OpReg(0x58, MReg.Of(SafeSaved[k])); }
                return true;
            }
            // Not a call site: the stub is never a safepoint (nothing in it
            // waits or collects), as i386's.
            case MOp.CallKeep or MOp.CallKeepRax or MOp.CallKeepCas: CallL((MImm)i.Operands[0]); return true;
            case MOp.Ret: _out.Add(0xC3); return true;
            case MOp.Xchg:
            {
                // xchg r/m, r: the memory side first as the selector writes it.
                MOperand rm = i.Operands[0] is MMem ? i.Operands[0] : i.Operands[1];
                MOperand reg = i.Operands[0] is MMem ? i.Operands[1] : i.Operands[0];
                Lock(i);
                OpL(w, Hw(reg), rm, w == 1 ? (byte)0x86 : (byte)0x87);
                return true;
            }
            case MOp.Xadd: Lock(i); OpL(w, Hw(i.Operands[1]), i.Operands[0], 0x0F, w == 1 ? (byte)0xC0 : (byte)0xC1); return true;
            case MOp.Cmpxchg: Lock(i); OpL(w, Hw(i.Operands[1]), i.Operands[0], 0x0F, w == 1 ? (byte)0xB0 : (byte)0xB1); return true;
            case MOp.Mfence: B(0x0F, 0xAE, 0xF0); return true;
            case MOp.RepMovsb: B(0xF3, 0xA4); return true;
            case MOp.RepMovsq: B(0xF3, 0x48, 0xA5); return true;
            case MOp.RepStosb: B(0xF3, 0xAA); return true;
            case MOp.RepStosq: B(0xF3, 0x48, 0xAB); return true;
            // ---- SSE2 ----
            case MOp.MovF:
                if (i.Operands[0] is MMem) SseL(ScalarL(w), false, Hw(i.Operands[1]), i.Operands[0], 0x11);
                else SseL(ScalarL(w), false, Hw(i.Operands[0]), i.Operands[1], 0x10);
                return true;
            case MOp.MovGx:
                // movd/movq between a general register or memory and an XMM register.
                if (i.Operands[0] is MReg { IsFloat: true } xd) SseL(0x66, w == 8, xd.Hw, i.Operands[1], 0x6E);
                else SseL(0x66, w == 8, Hw(i.Operands[1]), i.Operands[0], 0x7E);
                return true;
            case MOp.AddF: SseL(ScalarL(w), false, Hw(i.Operands[0]), i.Operands[1], 0x58); return true;
            case MOp.MulF: SseL(ScalarL(w), false, Hw(i.Operands[0]), i.Operands[1], 0x59); return true;
            case MOp.SubF: SseL(ScalarL(w), false, Hw(i.Operands[0]), i.Operands[1], 0x5C); return true;
            case MOp.DivF: SseL(ScalarL(w), false, Hw(i.Operands[0]), i.Operands[1], 0x5E); return true;
            case MOp.SqrtF: BreakMergeL(i, w); SseL(ScalarL(w), false, Hw(i.Operands[0]), i.Operands[1], 0x51); return true;
            case MOp.UcomiF: SseL(w == 8 ? (byte)0x66 : (byte)0, false, Hw(i.Operands[0]), i.Operands[1], 0x2E); return true;
            case MOp.XorF: SseL(0, false, Hw(i.Operands[0]), i.Operands[1], 0x57); return true;
            case MOp.CvtIntToF: BreakMergeL(i, w); SseL(ScalarL(w), i.SourceWidth == 8, Hw(i.Operands[0]), i.Operands[1], 0x2A); return true;
            case MOp.CvtFToInt: SseL(ScalarL(i.SourceWidth), w == 8, Hw(i.Operands[0]), i.Operands[1], 0x2C); return true;
            case MOp.CvtFToF:
                BreakMergeL(i, w);
                // To double from single is F3 5A; to single from double is F2 5A.
                SseL(w == 8 ? (byte)0xF3 : (byte)0xF2, false, Hw(i.Operands[0]), i.Operands[1], 0x5A);
                return true;
            // ---- odds and ends ----
            case MOp.Int3: _out.Add(0xCC); return true;
            case MOp.Ud2: B(0x0F, 0x0B); return true;
            case MOp.Nop: _out.Add(0x90); return true;
            case MOp.CompilerFence or MOp.SaveRegs: return true;
            case MOp.CallerReg:
            {
                Gpr kept = ((MImm)i.Operands[1]).Value switch { 0 => Gpr.Rbx, 1 => Gpr.R12, 2 => Gpr.R13, 3 => Gpr.R14, _ => Gpr.R15 };
                MReg to = (MReg)i.Operands[0];
                int saved = _m.SavedRegs.IndexOf(kept);
                if (saved >= 0) OpL(8, to.Hw, SaveSlot(saved), 0x8B);            // mov r, [save slot]
                else if (to.Hw != (int)kept) OpL(8, (int)kept, to, 0x89);        // mov r, reg
                return true;
            }
            case MOp.Pause: B(0xF3, 0x90); return true;
            case MOp.Syscall: B(0x0F, 0x05); return true;
            case MOp.In: if (w == 2) B(0x66); _out.Add(w == 1 ? (byte)0xEC : (byte)0xED); return true;
            case MOp.Out: if (w == 2) B(0x66); _out.Add(w == 1 ? (byte)0xEE : (byte)0xEF); return true;
            case MOp.Cli: _out.Add(0xFA); return true;
            case MOp.Sti: _out.Add(0xFB); return true;
            case MOp.Hlt: _out.Add(0xF4); return true;
            case MOp.RepInsw: B(0x66, 0xF3, 0x6D); return true;
            case MOp.RepOutsw: B(0x66, 0xF3, 0x6F); return true;
            case MOp.Lgdt: OpL(4, 2, new MMem((MReg)i.Operands[0], 0), 0x0F, 0x01); return true;
            case MOp.Lidt: OpL(4, 3, new MMem((MReg)i.Operands[0], 0), 0x0F, 0x01); return true;
            case MOp.Invlpg: OpL(4, 7, new MMem((MReg)i.Operands[0], 0), 0x0F, 0x01); return true;
            case MOp.Ltr: OpL(4, 3, i.Operands[0], 0x0F, 0x00); return true;
            // 0F 20 /r and 0F 22 /r: the control register in the reg field
            // (REX.R for CR8), the general register in r/m; always 64 bits here.
            case MOp.MovFromCr: OpL(4, (int)((MImm)i.Operands[1]).Value, i.Operands[0], 0x0F, 0x20); return true;
            case MOp.MovToCr: OpL(4, (int)((MImm)i.Operands[0]).Value, i.Operands[1], 0x0F, 0x22); return true;
            case MOp.LoadSegments:
                // mov ds, es, fs, gs, ss <- ax. In long mode loading FS or GS
                // clears its base; a kernel sets GS's through its MSR after.
                B(0x8E, 0xD8, 0x8E, 0xC0, 0x8E, 0xE0, 0x8E, 0xE8, 0x8E, 0xD0);
                return true;
            case MOp.LoadCs:
                // push sel; lea rax, [rip + 3]; push rax; retfq -- a far
                // return to the instruction after it, under the new CS.
                OpL(4, 6, i.Operands[0], 0xFF);                               // push r/m64
                NextInstr();
                B(0x48, 0x8D, 0x05); D32(3);                                  // lea rax, [rip+3]
                B(0x50);                                                      // push rax
                B(0x48, 0xCB);                                                // retfq
                return true;
            case MOp.Rdmsr: B(0x0F, 0x32); return true;
            case MOp.Wrmsr: B(0x0F, 0x30); return true;
            case MOp.Cpuid: B(0x0F, 0xA2); return true;
            case MOp.Rdtsc: B(0x0F, 0x31); return true;
            // An asm block: its bytes, assembled with its slots where they
            // are now (InlineAsmCode.Assembled) -- the same wherever it lands.
            case MOp.InlineAsm: B(i.Asm!.Assembled(i).Bytes); return true;
            case MOp.ReadCs:
                // 8C /r, mov r32, cs: in long mode the upper half is nought.
                _out.Add(0x8C);
                OpReg(0xC8, (MReg)i.Operands[0]);
                return true;
            case MOp.ReadFlags:
                // 9C pushfq (64 bits in long mode), then pop r64.
                _out.Add(0x9C);
                NextInstr();
                OpReg(0x58, (MReg)i.Operands[0]);
                return true;
            case MOp.Swapgs: B(0x0F, 0x01, 0xF8); return true;
            case MOp.Pushf: _out.Add(0x9C); return true;
            case MOp.Popf: _out.Add(0x9D); return true;
            case MOp.SoftInt: B(0xCD, (byte)((MImm)i.Operands[0]).Value); return true;
            case MOp.Prologue: PrologueL(); return true;
            case MOp.Epilogue: EpilogueL(); return true;
            default:
                // The family's case: by the instruction's own width again.
                _rex = RexW(i) ? RexW8 : 0;
                _byteOp = i.Width == 1 || i.Op == MOp.Setcc;
                return false;
        }
    }

    private void Lock(MInstr i)
    {
        if (i.Lock) _out.Add(0xF0);
    }

    private void MovL(MInstr i, int w)
    {
        MOperand dst = i.Operands[0], src = i.Operands[1];
        if (src is MImm imm)
        {
            if (!imm.IsPlain) throw new InvalidOperationException($"{_m.Source.Name}: a symbol's address as an immediate; it is made with LEA");
            if (dst is MReg r)
            {
                long v = imm.Value;
                if (w == 8 && v is >= 0 and <= uint.MaxValue)
                {
                    w = 4;     // the 32-bit form zero-extends
                    v = (uint)v;
                }
                switch (w)
                {
                    case 1:
                        if (r.Hw is >= 4 and <= 7) _rexEmpty = true;
                        OpReg(0xB0, r);
                        _out.Add((byte)v);
                        return;
                    case 2:
                        B(0x66);
                        OpReg(0xB8, r);
                        D16(v);
                        return;
                    case 4:
                        OpReg(0xB8, r);
                        D32(v);
                        return;
                    default:
                        if (v is >= int.MinValue and <= int.MaxValue)
                        {
                            OpL(8, 0, r, 0xC7);
                            D32(v);
                        }
                        else
                        {
                            _rex |= RexW8;
                            OpReg(0xB8, r);
                            D64(v);
                        }
                        return;
                }
            }
            switch (w)
            {
                case 1:
                    OpL(1, 0, dst, 0xC6);
                    _out.Add((byte)imm.Value);
                    return;
                case 2:
                    OpL(2, 0, dst, 0xC7);
                    D16(imm.Value);
                    return;
                default:
                    if (w == 8 && !imm.FitsInt32) throw new InvalidOperationException($"{_m.Source.Name}: a 64-bit immediate into memory");
                    OpL(w, 0, dst, 0xC7);
                    D32(imm.Value);
                    return;
            }
        }
        if (dst is MMem) OpL(w, Hw(src), dst, w == 1 ? (byte)0x88 : (byte)0x89);
        else if (src is MMem) OpL(w, Hw(dst), src, w == 1 ? (byte)0x8A : (byte)0x8B);
        else OpL(w, Hw(src), dst, w == 1 ? (byte)0x88 : (byte)0x89);
    }

    private void PushL(MOperand o)
    {
        switch (o)
        {
            case MReg r: OpReg(0x50, r); break;
            case MImm imm when imm.FitsSbyte: B(0x6A, (byte)imm.Value); break;
            case MImm imm: _out.Add(0x68); D32(imm.Value); break;
            default: OpL(4, 6, o, 0xFF); break;
        }
    }

    private void AluL(int digit, MOperand dst, MOperand src, int w)
    {
        if (src is MImm imm)
        {
            if (w == 1) { OpL(1, digit, dst, 0x80); _out.Add((byte)imm.Value); }
            else if (imm.FitsSbyte) { OpL(w, digit, dst, 0x83); _out.Add((byte)imm.Value); }
            else
            {
                OpL(w, digit, dst, 0x81);
                if (w == 2) D16(imm.Value); else D32(imm.Value);
            }
            return;
        }
        int basis = digit * 8;
        if (src is MMem) OpL(w, Hw(dst), src, (byte)(basis + (w == 1 ? 2 : 3)));
        else OpL(w, Hw(src), dst, (byte)(basis + (w == 1 ? 0 : 1)));
    }

    private void ShiftL(int digit, MInstr i, int w)
    {
        if (i.Operands[1] is MImm imm)
        {
            OpL(w, digit, i.Operands[0], w == 1 ? (byte)0xC0 : (byte)0xC1);
            _out.Add((byte)imm.Value);
        }
        else OpL(w, digit, i.Operands[0], w == 1 ? (byte)0xD2 : (byte)0xD3);
    }

    // ---- the frame ---------------------------------------------------------------------

    /// <summary>
    /// `push rbp ; mov rbp, rsp ; sub rsp, n`, then each callee-saved register
    /// the function uses stored in its slot below the frame. In a function
    /// that gives RBP a value after (FramePointerFree) the move is written
    /// 8B EC, so its first bytes are never a framed function's.
    /// </summary>
    private void PrologueL()
    {
        _out.Add(0x55);
        if (_m.FramePointerFree) B(0x48, 0x8B, 0xEC);
        else B(0x48, 0x89, 0xE5);
        if (_m.RealignsStack) B(0x48, 0x83, 0xE4, 0xF0);                       // and rsp, -16
        if (_frameBytes > 0)
        {
            NextInstr();
            AluL(5, MReg.Of(Gpr.Rsp), new MImm(_frameBytes), 8);
        }
        for (int k = 0; k < _m.SavedRegs.Count; k++)
        {
            NextInstr();
            OpL(8, (int)_m.SavedRegs[k], SaveSlot(k), 0x89);
        }
    }

    private MMem SaveSlot(int k) => _m.FramePointerFree
        ? new MMem(MReg.Of(Gpr.Rsp), _frameBytes - _m.Frame.Size - 8 * (k + 1))
        : MMem.Frame(-_m.Frame.Size - 8 * (k + 1));

    private void EpilogueL()
    {
        for (int k = 0; k < _m.SavedRegs.Count; k++)
        {
            OpL(8, (int)_m.SavedRegs[k], SaveSlot(k), 0x8B);
            NextInstr();
        }
        if (_m.FramePointerFree)
        {
            // RBP holds a value, not the frame: RSP past the frame, then RBP back.
            if (_frameBytes > 0)
            {
                AluL(0, MReg.Of(Gpr.Rsp), new MImm(_frameBytes), 8);
                NextInstr();
            }
            _out.Add(0x5D);    // pop rbp
        }
        else _out.Add(0xC9);   // leave: mov rsp, rbp; pop rbp
        _out.Add(0xC3);
    }
}
