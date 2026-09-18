using Corsac.Lang.Ir;

namespace Corsac.Lang.X86;

internal sealed partial class Selector
{
    private void SelectFloatToInt(Instr instruction)
    {
        VReg destination = instruction.Dest!;
        bool wide = destination.Type == IrType.I64, unsigned = instruction.Op == Opcode.FToU;
        bool single = instruction.Operands[0].Type == IrType.F32;
        MMem home = FHome(instruction.Operands[0]);
        MReg sign = Temp(), magnitude = Temp();
        Mov(sign, single ? home : Displaced(home, 4));
        Mov(magnitude, sign); Emit(MOp.And, magnitude, Imm(int.MaxValue));

        MBlock anchor = _cur;
        MBlock New(string name) { MBlock block = _m.NewBlock(name + _splits++, anchor); anchor = block; return block; }
        MBlock finite = New("cast-finite"), negative = New("cast-negative"), zero = New("cast-zero"),
            maximum = New("cast-maximum"), minimum = New("cast-minimum"), normal = New("cast-normal"),
            high = New("cast-unsigned-high"), done = New("cast-done");

        // Inspect IEEE bits without executing an operation on NaNs. Current
        // .NET casts map NaN to zero and overflow to the destination limit.
        Emit(MOp.Cmp, magnitude, Imm(single ? 0x7f800000 : 0x7ff00000)); Jcc(Cond.A, zero);
        if (!single)
        {
            Jcc(Cond.B, finite);
            Emit(MOp.Cmp, home, Imm(0)); Jcc(Cond.Ne, zero);
        }
        Jmp(finite); _cur = finite;
        Emit(MOp.Test, sign, Imm(int.MinValue)); Jcc(Cond.Ne, unsigned ? zero : negative);
        int limit = single ? (wide ? (unsigned ? 0x5f800000 : 0x5f000000) : (unsigned ? 0x4f800000 : 0x4f000000))
            : wide ? (unsigned ? 0x43f00000 : 0x43e00000) : (unsigned ? 0x41f00000 : 0x41e00000);
        Emit(MOp.Cmp, magnitude, Imm(limit)); Jcc(Cond.Ae, maximum); Jmp(normal);
        _cur = negative;
        Emit(MOp.Cmp, magnitude, Imm(limit)); Jcc(Cond.Ae, minimum); Jmp(normal);
        _cur = zero;
        Mov(Lo(destination), Imm(0)); if (wide) Mov(Hi(destination), Imm(0)); Jmp(done);
        _cur = maximum;
        Mov(Lo(destination), Imm(wide || unsigned ? -1 : int.MaxValue));
        if (wide) Mov(Hi(destination), Imm(unsigned ? -1 : int.MaxValue)); Jmp(done);
        _cur = minimum;
        Mov(Lo(destination), Imm(wide ? 0 : int.MinValue)); if (wide) Mov(Hi(destination), Imm(int.MinValue)); Jmp(done);
        _cur = normal;
        if (wide && unsigned)
        {
            Emit(MOp.Cmp, magnitude, Imm(single ? 0x5f000000 : 0x43e00000)); Jcc(Cond.Ae, high);
        }
        SelectFloatToIntCore(instruction); Jmp(done);
        _cur = high;
        if (wide && unsigned) SelectFloatToIntCore(instruction, unsignedHighHalf: true);
        Jmp(done); _cur = done;
    }
}
