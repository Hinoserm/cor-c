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
/// EncodeLong answers false for an opcode whose family case is right in
/// long mode as it stands (FamilyForm: the ones read against the mode, one
/// by one), and the family's case writes it. Any other opcode is refused:
/// i386's forms of the segment registers, the x87 and MMX sets, CMPXCHG8B,
/// its stubs' conventions and the rest put raw register numbers and
/// absolute addresses in their bytes, and would be other instructions here.
/// </summary>
internal sealed partial class Encoder
{
    /// <summary>Long mode: what the prologue takes off the stack pointer for this function (FrameBytes): a framed function's slots and saves, sixteen-aligned; a function's without a frame, its slots.</summary>
    private int _frameBytes;

    /// <summary>
    /// What the prologue takes off the stack pointer: a framed function's
    /// slots and its saved registers' slots, to a multiple of sixteen; a
    /// function's without a frame, its slots alone (FramelessBytes).
    /// </summary>
    public static int FrameBytes(MFunction m) => m.Frameless ? FramelessBytes(m) : (m.Frame.Size + m.SavedRegs.Count * 8 + 15) & ~15;

    /// <summary>
    /// What a function without a frame reserves for its slots
    /// (MFunction.Frameless): the frame's size, whole words as it is made
    /// (Frame.Size). No sixteen to keep in a leaf, which calls nothing; a
    /// reserved frame keeps one by ReservedPad.
    /// </summary>
    public static int FramelessBytes(MFunction m) => m.Frame.Size;

    /// <summary>
    /// The word a reserved frame (MFunction.ReservedFrame) pushes below its
    /// saves in long mode where its slots and its saves are an odd number of
    /// words, so that the stack is a multiple of sixteen at the calls it
    /// makes its frame for: the return address left it eight short, the
    /// base register's word made that up, and the rest must be whole
    /// sixteens. None on i386. (Answered for any function, as if its frame
    /// were reserved: FramePointerFree.Run asks before it decides, and
    /// counts it only where it is.)
    /// </summary>
    public static int ReservedPad(MFunction m)
        => X86Mode.Long && (FramelessBytes(m) + 8 * m.SavedRegs.Count) % 16 != 0 ? 8 : 0;

    /// <summary>A register operand's whole hardware number (0 to 15), general or XMM; 0 for anything else. Never a register left unallocated.</summary>
    private static int Hw(MOperand o) => o is not MReg r ? 0 : r.IsMachine ? r.Hw
        : throw new InvalidOperationException($"register {r} left unallocated (the base register's value is made the register by FramePointerFree)");

    /// <summary>
    /// An integer instruction with a ModRM whose reg field is a REGISTER, of
    /// the width given: the operand-size prefix for sixteen bits, REX.W for
    /// sixty-four, REX.R for a register of eight or above (`reg` is its
    /// whole number), and the REX a byte register SPL to DIL needs.
    /// </summary>
    private void OpL(int width, int reg, MOperand rm, params byte[] opcode)
    {
        if ((reg & 8) != 0) _rex |= RexR;
        if (width == 1 && reg is >= 4 and <= 7) _rexEmpty = true;
        OpD(width, reg & 7, rm, opcode);
    }

    /// <summary>
    /// The same with an opcode's DIGIT in the reg field (`/0` to `/7`): no
    /// register there to ask a REX for. (Taken for one, a byte operation
    /// under digit 4 to 7 -- AND, SUB, XOR and CMP with an immediate, the
    /// shifts, the wide multiplies and divides -- had a REX it did not
    /// need: a byte on every `cmp byte [m], n`.)
    /// </summary>
    private void OpD(int width, int digit, MOperand rm, params byte[] opcode)
    {
        if (width == 2) B(0x66);
        if (width == 8) _rex |= RexW8;
        if (width == 1 && rm is MReg { IsFloat: false, Hw: >= 4 and <= 7 }) _rexEmpty = true;
        B(opcode);
        XmmRM(digit, rm);
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

    /// <summary>
    /// Long mode's form of the instruction, where it has one of its own;
    /// false where the family's case is right (FamilyForm, and the shapes a
    /// case here hands on with Family: MOVF between registers or of sixteen
    /// bytes); and for any other opcode the compile is stopped, with the
    /// function's name.
    /// </summary>
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
                // Width is the source's; the destination is 64 bits, or the
                // low thirty-two where the instruction says (MInstr.To32).
                if (w == 4) OpL(8, Hw(i.Operands[0]), i.Operands[1], 0x63);
                else
                {
                    if (w == 1 && i.Operands[1] is MReg { IsFloat: false, Hw: >= 4 and <= 7 }) _rexEmpty = true;
                    OpL(i.To32 ? 4 : 8, Hw(i.Operands[0]), i.Operands[1], 0x0F, w == 1 ? (byte)0xBE : (byte)0xBF);
                }
                return true;
            case MOp.Movzx:
                // Width is the source's; the destination is 32 bits, which zero-extends to 64.
                // (The empty REX for a byte SOURCE in SPL to DIL alone: the destination is a whole register.)
                if (w == 1 && i.Operands[1] is MReg { IsFloat: false, Hw: >= 4 and <= 7 }) _rexEmpty = true;
                OpL(4, Hw(i.Operands[0]), i.Operands[1], 0x0F, w == 1 ? (byte)0xB6 : (byte)0xB7);
                return true;
            // An address is the register's whole eight bytes; an I32's sum made
            // by LEA (a multiplication's step) is four, and clears the rest.
            case MOp.Lea: OpL(i.Width == 4 ? 4 : 8, Hw(i.Operands[0]), i.Operands[1], 0x8D); return true;
            case MOp.Push: PushL(i.Operands[0]); return true;
            case MOp.Pop:
                if (i.Operands[0] is MReg pr) OpReg(0x58, pr);
                else OpD(4, 0, i.Operands[0], 0x8F);
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
                    Imm32Only(ti, w);
                    OpD(w, 0, i.Operands[0], w == 1 ? (byte)0xF6 : (byte)0xF7);
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
                Imm32Only(k, w);
                if (k.FitsSbyte) { OpL(w, Hw(i.Operands[0]), i.Operands[1], 0x6B); _out.Add((byte)k.Value); }
                else
                {
                    OpL(w, Hw(i.Operands[0]), i.Operands[1], 0x69);
                    // (Under the operand-size prefix the immediate is sixteen bits.)
                    if (w == 2) D16(k.Value); else D32(k.Value);
                }
                return true;
            }
            case MOp.MulWide: Lock(i); OpD(w, 4, i.Operands[0], w == 1 ? (byte)0xF6 : (byte)0xF7); return true;
            case MOp.ImulWide: Lock(i); OpD(w, 5, i.Operands[0], w == 1 ? (byte)0xF6 : (byte)0xF7); return true;
            case MOp.Div: OpD(w, 6, i.Operands[0], w == 1 ? (byte)0xF6 : (byte)0xF7); return true;
            case MOp.Idiv: OpD(w, 7, i.Operands[0], w == 1 ? (byte)0xF6 : (byte)0xF7); return true;
            case MOp.Neg: Lock(i); OpD(w, 3, i.Operands[0], w == 1 ? (byte)0xF6 : (byte)0xF7); return true;
            // (FE/FF /0 and /1: long mode has no one-byte forms, their bytes its REX prefixes.)
            case MOp.Inc: Lock(i); OpD(w, 0, i.Operands[0], w == 1 ? (byte)0xFE : (byte)0xFF); return true;
            case MOp.Dec: Lock(i); OpD(w, 1, i.Operands[0], w == 1 ? (byte)0xFE : (byte)0xFF); return true;
            case MOp.Not: Lock(i); OpD(w, 2, i.Operands[0], w == 1 ? (byte)0xF6 : (byte)0xF7); return true;
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
            case MOp.Setcc: OpD(1, 0, i.Operands[0], 0x0F, (byte)(0x90 + (int)i.Cond)); return true;
            case MOp.Cmov: OpL(w, Hw(i.Operands[0]), i.Operands[1], 0x0F, (byte)(0x40 + (int)i.Cond)); return true;
            case MOp.JmpTable:
                // jmp [table + index*8]: the table's address is in a register (the selector's LEA).
                OpD(4, 4, new MMem(i.Reg(1), 0) { Index = i.Reg(0), Scale = 8 }, 0xFF);
                Tables.Add((i.TableSymbol!, i.Table!));
                return true;
            case MOp.JmpInd: OpD(4, 4, i.Operands[0], 0xFF); return true;
            case MOp.Call: ColdFrameL(); CallL((MImm)i.Operands[0]); return true;
            case MOp.CallInd: ColdFrameL(); OpD(4, 2, i.Operands[0], 0xFF); return true;
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
            // waits or collects), as i386's. By name, or through its GOT slot
            // in a position-independent object (`call [rip + stub@GOTPCREL]`,
            // Selector.SequenceStub).
            case MOp.CallKeep or MOp.CallKeepRax or MOp.CallKeepCas:
                if (i.Operands[0] is MMem { Got: true } slot) OpD(4, 2, slot, 0xFF);
                else CallL((MImm)i.Operands[0]);
                return true;
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
            // A move between two registers, or of a whole sixteen bytes, is
            // the family's (MOVAPS: no wait on what the destination held;
            // MOVUPS or MOVAPS of all of it): MOVSS/MOVSD here kept the
            // destination's upper lanes, and of sixteen bytes moved eight.
            case MOp.MovF when w == 16 || i.Operands[0] is MReg && i.Operands[1] is MReg: return Family(i);
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
            // ---- packed values in the XMM class (Select.PackedValues) ----
            // The family's cases put a shift's, an extraction's and an
            // insertion's registers into a ModRM byte by hand, with no REX
            // for XMM8 to XMM15 or R8 to R15.
            case MOp.XmmOpV:
            case MOp.XmmOpVDef:
                // [prefix] 0F op /r [ib]: no REX.W, which the family's way in would give it.
                SseL((byte)((MImm)i.Operands[0]).Value, false, Hw(i.Operands[2]), i.Operands[3], (byte)((MImm)i.Operands[1]).Value);
                if (i.Operands.Count > 4) _out.Add((byte)((MImm)i.Operands[4]).Value);
                return true;
            case MOp.XmmStoreNtV:
                // movntdq m128, xmm: 66 0F E7 /r.
                SseL(0x66, false, Hw(i.Operands[1]), i.Operands[0], 0xE7);
                return true;
            case MOp.XmmShiftV:
                // 66 0F op /digit ib: the register is the r/m field.
                SseL(0x66, false, (int)((MImm)i.Operands[2]).Value, i.Operands[0], (byte)((MImm)i.Operands[1]).Value);
                _out.Add((byte)((MImm)i.Operands[3]).Value);
                return true;
            case MOp.XmmExtractV:
                // pextrw r32, xmm, ib: 66 0F C5 /r, the general register the reg field.
                SseL(0x66, false, Hw(i.Operands[0]), i.Operands[1], 0xC5);
                _out.Add((byte)((MImm)i.Operands[2]).Value);
                return true;
            case MOp.XmmMaskV:
                // pmovmskb r32, xmm: 66 0F D7 /r, the general register the reg field.
                SseL(0x66, false, Hw(i.Operands[0]), i.Operands[1], 0xD7);
                return true;
            case MOp.XmmInsertV:
                // pinsrw xmm, r32, ib: 66 0F C4 /r, the general register the r/m field.
                B(0x66);
                OpL(4, Hw(i.Operands[0]), i.Operands[1], 0x0F, 0xC4);
                _out.Add((byte)((MImm)i.Operands[2]).Value);
                return true;
            case MOp.Movnti:
                // movnti m64, r64 (m32, r32): 0F C3 /r, around the caches.
                OpL(w, Hw(i.Operands[1]), i.Operands[0], 0x0F, 0xC3);
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
            case MOp.Lgdt: OpD(4, 2, new MMem((MReg)i.Operands[0], 0), 0x0F, 0x01); return true;
            case MOp.Lidt: OpD(4, 3, new MMem((MReg)i.Operands[0], 0), 0x0F, 0x01); return true;
            case MOp.Invlpg: OpD(4, 7, new MMem((MReg)i.Operands[0], 0), 0x0F, 0x01); return true;
            case MOp.Ltr: OpD(4, 3, i.Operands[0], 0x0F, 0x00); return true;
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
                OpD(4, 6, i.Operands[0], 0xFF);                               // push r/m64
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
            case MOp.GetGs:
                // The same with GS, sreg 5.
                _out.Add(0x8C);
                OpReg(0xE8, (MReg)i.Operands[0]);
                return true;
            case MOp.SetGs:
                // 8E /5, mov gs, r32: the selector's descriptor read from
                // the table, its 32-bit base GS's (the MSR's base is lost).
                _out.Add(0x8E);
                OpReg(0xE8, (MReg)i.Operands[0]);
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
                if (!FamilyForm(i.Op))
                    throw new InvalidOperationException($"{_m.Source.Name}: {i.Op} has no form in long mode");
                return Family(i);
        }
    }

    /// <summary>The family's case writes the instruction: by its own width again, as the family's cases expect.</summary>
    private bool Family(MInstr i)
    {
        _rex = RexW(i) ? RexW8 : 0;
        _byteOp = i.Width == 1 || i.Op == MOp.Setcc;
        return false;
    }

    /// <summary>
    /// The opcodes long mode has no form of its own for and whose family
    /// case is right there as it stands: their registers go through the
    /// fields that note a REX (RegR, RegB, Xmm, XmmRM, ModRM), they name no
    /// absolute address, and they mean in long mode what they mean on i386.
    /// Each was read against the mode before it was put here; an opcode is
    /// added the same way, with what selects it. (A register-to-register
    /// or sixteen-byte MovF goes there by its own case above, not by this
    /// list.)
    /// </summary>
    private static bool FamilyForm(MOp op) => op is MOp.Shld or MOp.Shrd or MOp.XorPs or MOp.Cvttss2si or MOp.Cvttsd2si
        or MOp.LockOrEsp or MOp.Int or MOp.PrefetchNta or MOp.PrefetchWrite or MOp.Sfence;

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
                            OpD(8, 0, r, 0xC7);
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
                    OpD(1, 0, dst, 0xC6);
                    _out.Add((byte)imm.Value);
                    return;
                case 2:
                    OpD(2, 0, dst, 0xC7);
                    D16(imm.Value);
                    return;
                default:
                    if (w == 8 && !imm.FitsInt32) throw new InvalidOperationException($"{_m.Source.Name}: a 64-bit immediate into memory");
                    OpD(w, 0, dst, 0xC7);
                    D32(imm.Value);
                    return;
            }
        }
        if (dst is MMem) OpL(w, Hw(src), dst, w == 1 ? (byte)0x88 : (byte)0x89);
        else if (src is MMem) OpL(w, Hw(dst), src, w == 1 ? (byte)0x8A : (byte)0x8B);
        // `mov rbp, rsp` in a function without a frame written 8B EC: its
        // first bytes after its `push rbp` are then never a framed
        // function's 55 48 89 E5 (Runtime.FrameAt).
        else if (_m.Frameless && w == 8 && dst is MReg { Id: (int)Gpr.Rbp } && src is MReg { Id: (int)Gpr.Rsp }) OpL(w, Hw(dst), src, 0x8B);
        else OpL(w, Hw(src), dst, w == 1 ? (byte)0x88 : (byte)0x89);
    }

    private void PushL(MOperand o)
    {
        switch (o)
        {
            case MReg r: OpReg(0x50, r); break;
            case MImm imm when imm.FitsSbyte: B(0x6A, (byte)imm.Value); break;
            // PUSH imm32 pushes it sign-extended to a whole word, as a
            // sixty-four-bit operation's immediate is (Imm32Only).
            case MImm imm: Imm32Only(imm, 8); _out.Add(0x68); D32(imm.Value); break;
            default: OpD(4, 6, o, 0xFF); break;
        }
    }

    private void AluL(int digit, MOperand dst, MOperand src, int w)
    {
        if (src is MImm imm)
        {
            Imm32Only(imm, w);
            if (w == 1) { OpD(1, digit, dst, 0x80); _out.Add((byte)imm.Value); }
            else if (imm.FitsSbyte) { OpD(w, digit, dst, 0x83); _out.Add((byte)imm.Value); }
            else
            {
                OpD(w, digit, dst, 0x81);
                if (w == 2) D16(imm.Value); else D32(imm.Value);
            }
            return;
        }
        int basis = digit * 8;
        if (src is MMem) OpL(w, Hw(dst), src, (byte)(basis + (w == 1 ? 2 : 3)));
        else OpL(w, Hw(src), dst, (byte)(basis + (w == 1 ? 0 : 1)));
    }

    /// <summary>
    /// An immediate of a sixty-four-bit operation is thirty-two bits, sign-
    /// extended: one that does not fit is refused, never written short, and
    /// so is a symbol's address, which no thirty-two-bit field holds in a
    /// position-independent image. (What chooses immediates asks first --
    /// the allocator's FoldableImm, the selection, IfConvert, AddressFold;
    /// this is for the pass that forgets.)
    /// </summary>
    private void Imm32Only(MImm imm, int w)
    {
        if (w != 8) return;
        if (!imm.IsPlain) throw new InvalidOperationException($"{_m.Source.Name}: a symbol's address as the immediate of a 64-bit operation");
        if (!imm.FitsInt32) throw new InvalidOperationException($"{_m.Source.Name}: an immediate of a 64-bit operation that is not a sign-extended 32 bits");
    }

    private void ShiftL(int digit, MInstr i, int w)
    {
        if (i.Operands[1] is MImm imm)
        {
            OpD(w, digit, i.Operands[0], w == 1 ? (byte)0xC0 : (byte)0xC1);
            _out.Add((byte)imm.Value);
        }
        else OpD(w, digit, i.Operands[0], w == 1 ? (byte)0xD2 : (byte)0xD3);
    }

    // ---- the frame ---------------------------------------------------------------------

    /// <summary>
    /// `push rbp ; mov rbp, rsp ; sub rsp, n`, then each callee-saved register
    /// the function uses stored in its slot below the frame.
    ///
    /// A FUNCTION WITHOUT A FRAME (MFunction.Frameless, a leaf): the
    /// registers it saves pushed -- RBP first where it gives it a value --
    /// then its slots reserved, `sub rsp, n`, and nothing at all where it
    /// saves none and keeps none. Never the framed function's first four
    /// bytes, 55 48 89 E5, which is how a fault's walk tells the two apart
    /// (Runtime.FrameAt; MovL writes a `mov rbp, rsp` of its own the other
    /// way).
    /// </summary>
    private void PrologueL()
    {
        // A RESERVED FRAME (MFunction.ReservedFrame): RBP's word and the
        // slots, then the saves, a framed function's layout with RBP not
        // written -- and a word more where that leaves the stack eight off
        // a sixteen (ReservedPad). RBP's word alone: a push of anything, RAX.
        if (_m.ReservedFrame)
        {
            int reserve = _frameBytes + 8;
            if (reserve == 8) _out.Add(0x50);
            else AluL(5, MReg.Of(Gpr.Rsp), new MImm(reserve), 8);
            foreach (Gpr g in _m.SavedRegs)
            {
                NextInstr();
                OpReg(0x50, MReg.Of(g));
            }
            if (ReservedPad(_m) != 0)
            {
                NextInstr();
                _out.Add(0x50);
            }
            return;
        }
        if (_m.Frameless)
        {
            bool first = true;
            foreach (Gpr g in _m.SavedRegs)
            {
                if (!first) NextInstr();
                first = false;
                OpReg(0x50, MReg.Of(g));
            }
            if (_frameBytes > 0)
            {
                if (!first) NextInstr();
                if (ProbeStack()) NextInstr();
                AluL(5, MReg.Of(Gpr.Rsp), new MImm(_frameBytes), 8);
            }
            return;
        }
        _out.Add(0x55);
        B(0x48, 0x89, 0xE5);
        if (_m.RealignsStack) B(0x48, 0x83, 0xE4, 0xF0);                       // and rsp, -16
        if (_frameBytes > 0)
        {
            NextInstr();
            if (ProbeStack()) NextInstr();
            AluL(5, MReg.Of(Gpr.Rsp), new MImm(_frameBytes), 8);
        }
        for (int k = 0; k < _m.SavedRegs.Count; k++)
        {
            NextInstr();
            OpL(8, (int)_m.SavedRegs[k], SaveSlot(k), 0x89);
        }
    }

    /// <summary>
    /// A WINDOWS FRAME OF A PAGE OR MORE TOUCHES ITS PAGES IN ORDER before
    /// the stack pointer goes below them (X86Mode.Windows). Windows commits a
    /// thread's stack as it grows through the one guard page below what is
    /// committed, so a first touch two pages down faults: Microsoft's
    /// __chkstk, MinGW's ___chkstk_ms and LLVM's Windows prologues probe
    /// every 4 KiB first. Inline here, with no CRT to call: the stack
    /// pointer does not move until the `sub` that follows, so the unwind
    /// data needs no code for the loop (X64Backend.Unwind), and it uses R10
    /// and R11 alone, which carry no argument in at a function's entry
    /// (System V's arguments are RDI RSI RDX RCX R8 R9; the static chain,
    /// R10, is not this compiler's). A read is the touch, as __chkstk's
    /// `test [r11], r11b`. Writes the probe and answers true, or nothing.
    ///
    ///   mov r11, rsp ; mov r10d, pages
    ///   L: sub r11, 4096 ; test [r11], r11b ; dec r10d ; jnz L
    /// </summary>
    private bool ProbeStack()
    {
        if (!X86Mode.Windows || _frameBytes < ProbePage) return false;
        int pages = _frameBytes / ProbePage;
        B(0x49, 0x89, 0xE3);                                                    // mov r11, rsp
        B(0x41, 0xBA); D32(pages);                                              // mov r10d, pages
        B(0x49, 0x81, 0xEB); D32(ProbePage);                                    // L: sub r11, 4096
        B(0x45, 0x84, 0x1B);                                                    // test [r11], r11b
        B(0x41, 0xFF, 0xCA);                                                    // dec r10d
        B(0x75, unchecked((byte)-(7 + 3 + 3 + 2)));                             // jnz L
        return true;
    }

    /// <summary>A page, as Windows' stack grows: the size a frame is probed by (ProbeStack).</summary>
    public const int ProbePage = 4096;

    /// <summary>The probe's bytes (ProbeStack), which X64Backend.Unwind steps over in a prologue.</summary>
    public const int ProbeBytes = 3 + 6 + 7 + 3 + 3 + 2;

    /// <summary>
    /// Where the function's k-th saved register is kept: its slot below the
    /// frame, or, in a function without a frame, where it was pushed --
    /// above the slots, the first pushed highest.
    /// </summary>
    private MMem SaveSlot(int k) => _m.Frameless
        ? new MMem(MReg.Of(Gpr.Rsp), _frameBytes + 8 * (_m.SavedRegs.Count - 1 - k))
        : MMem.Frame(-_m.Frame.Size - 8 * (k + 1));

    /// <summary>
    /// The frame a function with none makes on its way to a call that never
    /// returns (MFunction.ColdFrames): `push rbp ; mov rbp, rsp` where
    /// nothing lies below its return address -- the push also what makes the
    /// stack a multiple of sixteen for the call -- else, the word for its
    /// RBP kept (ReservedFrame), `mov [rsp + X], rbp ; lea rbp, [rsp + X]`.
    /// The call is an instruction of its own after them.
    /// </summary>
    private void ColdFrameL()
    {
        if (!_m.ColdFrames) return;
        if (!_m.ReservedFrame) B(0x55, 0x48, 0x89, 0xE5);
        else
        {
            // (Within a byte's displacement, FramePointerFree.Run sees to
            // it: the only form Runtime.FrameAt reads.)
            int x = _frameBytes + 8 * _m.SavedRegs.Count + ReservedPad(_m);
            if (x > 127) throw new InvalidOperationException($"{_m.Source.Name}: a reserved frame of {x} bytes, more than a byte's displacement");
            B(0x48, 0x89, 0x6C, 0x24, (byte)x);
            B(0x48, 0x8D, 0x6C, 0x24, (byte)x);
        }
        NextInstr();
    }

    private void EpilogueL()
    {
        if (_m.ReservedFrame)
        {
            // (The padding word and RBP's word alone: popped into RCX, which
            // no return value is in.)
            if (ReservedPad(_m) != 0)
            {
                _out.Add(0x59);
                NextInstr();
            }
            for (int k = _m.SavedRegs.Count - 1; k >= 0; k--)
            {
                OpReg(0x58, MReg.Of(_m.SavedRegs[k]));
                NextInstr();
            }
            int reserve = _frameBytes + 8;
            if (reserve == 8) _out.Add(0x59);
            else AluL(0, MReg.Of(Gpr.Rsp), new MImm(reserve), 8);
            NextInstr();
            _out.Add(0xC3);
            return;
        }
        if (_m.Frameless)
        {
            if (_frameBytes > 0)
            {
                AluL(0, MReg.Of(Gpr.Rsp), new MImm(_frameBytes), 8);
                NextInstr();
            }
            for (int k = _m.SavedRegs.Count - 1; k >= 0; k--)
            {
                OpReg(0x58, MReg.Of(_m.SavedRegs[k]));
                NextInstr();
            }
            _out.Add(0xC3);
            return;
        }
        for (int k = 0; k < _m.SavedRegs.Count; k++)
        {
            OpL(8, (int)_m.SavedRegs[k], SaveSlot(k), 0x8B);
            NextInstr();
        }
        _out.Add(0xC9);   // leave: mov rsp, rbp; pop rbp
        _out.Add(0xC3);
    }
}
