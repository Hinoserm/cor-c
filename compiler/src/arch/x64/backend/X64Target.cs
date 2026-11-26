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
    private const int Xmms = 1;

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
    /// THE LOADS THAT ARE HOMES (AInstr.HomeSlot): `mov v, [rbp+d]` of a
    /// stack-passed argument's eight bytes, the only thing in the function
    /// that touches any byte of them, nothing taking an address off RBP --
    /// no LEA of it, no indexed access above it (RBP as a value is read
    /// through only: Sys.FramePointer's contract) -- and
    /// any other access taken as sixteen bytes wide. x86-64's collector
    /// reads a stack as memory: no map to name it in.
    /// </summary>
    protected override HashSet<MInstr> Homes()
    {
        HashSet<MInstr> homes = new(ReferenceEqualityComparer.Instance);
        Dictionary<int, MInstr?> load = new();
        List<(int From, int To)> touched = new();
        foreach (MBlock b in _m.Blocks)
            foreach (MInstr i in b.Instrs)
                for (int k = 0; k < i.Operands.Count; k++)
                {
                    if (i.Operands[k] is not MMem mem) continue;
                    if (mem.Index?.Id == (int)Gpr.Rbp) return homes;
                    if (mem.Base?.Id != (int)Gpr.Rbp) continue;
                    if (i.Op == MOp.Lea || mem.Index is not null && mem.Disp >= 0) return homes;
                    // The saved frame pointer and the return address, between the locals
                    // and the arguments: an access there, sixteen bytes at most, may reach
                    // one. A local below the frame pointer reaches none without writing
                    // over the return address first.
                    if (mem.Disp < 16 || mem.Symbol is not null || mem.Label is not null)
                    {
                        if (mem.Disp >= 0) touched.Add((mem.Disp, mem.Disp + 16));
                        continue;
                    }
                    if (i.Op == MOp.Mov && k == 1 && i.Width == 8 && !i.Lock && i.Operands[0] is MReg { IsMachine: false, IsFloat: false } && (mem.Disp & 7) == 0)
                        load[mem.Disp] = load.ContainsKey(mem.Disp) ? null : i;
                    else touched.Add((mem.Disp, mem.Disp + 16));
                }
        foreach ((int disp, MInstr? i) in load)
            if (i is not null && !touched.Any(t => t.From < disp + 8 && disp < t.To)) homes.Add(i);
        return homes;
    }

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

    /// <summary>A spilled value moved by its class's move (MOV, MOVSD) through an eight-byte slot: the class the register operand is of.</summary>
    protected override MInstr Move(MOperand to, MOperand from, int line)
        => new MInstr(to is MReg r && ClassOf(r.Id) == Xmms || from is MReg s && ClassOf(s.Id) == Xmms ? MOp.MovF : MOp.Mov, to, from) { Width = 8, Line = line };

    /// <summary>The allocator's copy, by the class of the register copied into.</summary>
    protected override MInstr Copy(int destination, MOperand to, MOperand from, int line)
        => new MInstr(ClassOf(destination) == Xmms ? MOp.MovF : MOp.Mov, to, from) { Width = 8, Line = line };
}
