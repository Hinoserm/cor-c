#nullable enable
using Corsac.Lang.RegAlloc;
using Role = Corsac.Lang.RegAlloc.Role;

using Corsac.Lang.X86;

namespace Corsac.Lang.X64;

/// <summary>
/// x86-64 AS THE ONE ALLOCATOR SEES IT (Corsac.Lang.RegAlloc): two classes,
/// the fourteen general registers -- the caller-saved nine first, then RBX
/// and R12 to R15 -- and the sixteen XMM registers, every one caller-saved
/// under the System V ABI; RSP and RBP never touched. Each instruction
/// lowered once into the registers it names (Roles.RegsOf), which
/// operands may be memory (Roles.Foldable) or a constant, its jumps; and
/// the function rebuilt from what was decided (FamilyTarget.Rebuild), a spilled
/// value moved by its class's move (MOV, MOVSD) through an eight-byte slot.
/// </summary>
internal sealed class X64Target : FamilyTarget, ITarget
{
    /// <summary>
    /// Long mode's registers: the caller-saved nine first, then RBX and R12
    /// to R15, every one with a byte form under REX; sixteen XMM registers,
    /// every one caller-saved under the System V ABI. A leaf's file has RBP
    /// besides (the prologue still pushes it); a fault in such a function
    /// finds its frame by the prologue's mark (Encoder.PrologueL).
    /// </summary>
    private static readonly (RegisterFile Whole, RegisterFile Leaf, RegisterFile CallerOnly) Of = Files(
        LongAbi.CallerSaved, Allocator.CalleeSaved.Select(g => (int)g).ToArray(), null, 16);

    public X64Target(MFunction m) : base(m)
    {
    }

    public override RegisterFile Registers => _m.Source.KeepsCallerRegisters ? Of.CallerOnly : _leaf ? Of.Leaf : Of.Whole;

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

    /// <summary>
    /// The callee-saved registers touched, or all of them where the function
    /// enters the collector. RBP given a value is saved by the prologue's own
    /// push, not among these.
    /// </summary>
    protected override void Saved(AFunction f, IPlacement p)
    {
        foreach (Gpr g in Allocator.CalleeSaved)
            if (_m.SavesEverything || p.Touched.Contains((int)g)) _m.SavedRegs.Add(g);
        _m.FramePointerFree = _leaf && p.Touched.Contains((int)Gpr.Rbp);
    }
}
