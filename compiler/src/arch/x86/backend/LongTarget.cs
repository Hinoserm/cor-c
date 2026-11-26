#nullable enable
using Corsac.Lang.RegAlloc;

namespace Corsac.Lang.X86;

/// <summary>
/// LONG MODE AS THE ONE ALLOCATOR SEES IT (Corsac.Lang.RegAlloc), what is
/// its own beside the family's (FamilyTarget): two classes, the fourteen
/// general registers -- the caller-saved nine first, then RBX and R12 to
/// R15 -- and the sixteen XMM registers, every one caller-saved under the
/// System V ABI; RSP and RBP never touched. A spilled value goes through
/// an eight-byte slot.
/// </summary>
internal sealed class LongTarget : FamilyTarget, ITarget
{
    /// <summary>
    /// Long mode's registers: the caller-saved nine first, then RBX and R12
    /// to R15, every one with a byte form under REX; sixteen XMM registers,
    /// every one caller-saved under the System V ABI. A leaf's file has RBP
    /// besides, pushed first of those it saves (MFunction.FramePointerFree),
    /// in a function with no frame of its own (MFunction.Frameless); a fault
    /// in such a function finds its caller by its first bytes
    /// (Runtime.FrameAt).
    /// </summary>
    private static readonly (RegisterFile Whole, RegisterFile Leaf, RegisterFile CallerOnly) Of = Files(
        LongAbi.CallerSaved, LongAbi.CalleeSaved.Select(g => (int)g).ToArray(), null, 16);

    public LongTarget(MFunction m) : base(m)
    {
    }

    protected override (RegisterFile Whole, RegisterFile Leaf, RegisterFile CallerOnly) ModeFiles => Of;

    protected override string InstrText(MInstr i) => $"{i.Op} {string.Join(", ", i.Operands)}";

    /// <summary>
    /// A NARROW WRITE LEAVES THE SLOT'S TOP HALF AS IT WAS, and a reload is
    /// the whole eight bytes: a general register written at less than eight
    /// bytes is written in a register.
    /// </summary>
    protected override bool MayWriteMemory(MInstr i, int operand)
        => i.Width == 8 || i.Operands[operand] is MReg r && (r.IsFloat || _m.XmmRegs.Contains(r.Id));

    /// <summary>
    /// A plain constant moved into a general register, the width part of
    /// the value: a 4-byte move of -1 is 0xFFFFFFFF. (A 64-bit constant
    /// folded into a move whose destination is later spilled would ask for
    /// `mov m64, imm64`, which x86-64 does not have: Roles.Foldable.)
    /// </summary>
    protected override MImm? ConstantMoved(MInstr i)
        => i.Op == MOp.Mov && i.Operands.Count == 2 && i.Operands[0] is MReg { IsFloat: false } && i.Operands[1] is MImm { IsPlain: true } imm
            ? (i.Width == 4 ? new MImm((uint)imm.Value) : imm) : null;
}
