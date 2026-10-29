#nullable enable

namespace Corsac.Lang.RegAlloc;

/// <summary>What an instruction does to a register it names.</summary>
[Flags]
public enum Role : byte
{
    None = 0,
    Use = 1,
    Def = 2,
    UseDef = 3,
}

/// <summary>
/// A register an instruction names: physical (below the register file's
/// FirstVirtual, a fixed constraint) or virtual; what the instruction does
/// with it; the operand it is in, -1 for one the instruction reads or
/// writes unnamed; and whether it is a memory operand's base or index,
/// which only ever reads it.
/// </summary>
public readonly record struct Named(int Reg, Role Role, int Operand, bool InMem);

/// <summary>
/// A class of interchangeable registers: the allocatable ones in the order
/// they are tried (caller-saved first: one that crosses no call costs
/// nothing there), the callee-saved ones among them, and those a value
/// limited to byte operations may take (null: all).
/// </summary>
public sealed class RegClass
{
    public required string Name { get; init; }
    public required int[] Preference { get; init; }
    public required int[] CalleeSaved { get; init; }
    public int[]? ByteCapable { get; init; }
}

/// <summary>
/// A machine's registers as the allocator sees them: physical numbers
/// 0 .. PhysCount-1, virtual ones from FirstVirtual; each class's; which
/// physical ones it tracks at all (not the stack and frame pointers).
/// </summary>
public sealed class RegisterFile
{
    public required int PhysCount { get; init; }
    public required int FirstVirtual { get; init; }
    public required RegClass[] Classes { get; init; }
    /// <summary>The physical registers the allocator never touches nor tracks.</summary>
    public required int[] Untracked { get; init; }

    public bool Tracked(int reg) => Array.IndexOf(Untracked, reg) < 0;
}

/// <summary>
/// AN INSTRUCTION AS THE ALLOCATOR SEES IT, the target's own (Tag) or one
/// the allocator made (Tag null: a copy, a jump -- made by the target when
/// the function is rebuilt, from Made). Everything the scan asks of an
/// instruction is here, worked out once by the target.
/// </summary>
public sealed class AInstr
{
    /// <summary>The target's instruction, or null for one the allocator made.</summary>
    public object? Tag { get; init; }
    /// <summary>The registers it names, its explicit operands' first, in the order the target rebuilds them.</summary>
    public required Named[] Regs { get; set; }
    public bool IsCall { get; init; }
    /// <summary>
    /// A register moved to another, operand 1 to operand 0: its destination
    /// and source are its first two named registers, read from them, so a
    /// pass that renames them (CopyFold, LoopSplit) renames the copy too.
    /// </summary>
    public bool IsCopy { get; init; }
    public int CopyDst => IsCopy ? Regs[0].Reg : -1;
    public int CopySrc => IsCopy ? Regs[1].Reg : -1;
    /// <summary>Whether, its two registers given one, the copy goes (not x86-64's 32-bit move, which zero-extends: that is its meaning).</summary>
    public bool DropsSelf { get; init; }
    /// <summary>A copy of a whole register, its full width and nothing else (not locked): one the passes may fold or split by.</summary>
    public bool WholeCopy { get; init; }
    /// <summary>A jump to a computed address: it leaves for blocks no edge names.</summary>
    public bool JumpsAnywhere { get; init; }
    /// <summary>
    /// A HOME: the frame slot a whole-register load into operand 0 reads --
    /// an incoming argument's -- where nothing else in the function reads,
    /// writes or takes the address of that slot. The value loaded lives
    /// there when it is spilled: no store back and no slot of its own, the
    /// load kept (its register may serve the reads after it) -- Wimmer and
    /// Mossenbock's spill-store elimination for parameters. Null for none.
    /// </summary>
    public int? HomeSlot { get; init; }
    /// <summary>The constant a whole-width definition moves into operand 0 (a value remade where it is wanted), else null.</summary>
    public object? Constant { get; init; }
    /// <summary>How many operands it has (the positions the masks below are over).</summary>
    public int OperandCount { get; init; }
    /// <summary>The operand positions that may be a memory operand (a spill slot read or written in place).</summary>
    public uint MayMemory { get; init; }
    /// <summary>
    /// Of those, the positions a write may go to memory: all of the slot
    /// written. x86-64's 32-bit or byte write of a general register leaves
    /// the slot's top half as it was, and a reload is the whole eight bytes.
    /// </summary>
    public uint MayMemoryWrite { get; init; }
    /// <summary>The operand positions that are memory now.</summary>
    public uint IsMemory { get; init; }
    /// <summary>The operand positions that may be an immediate (a constant remade in place): any, or a plain number only.</summary>
    public uint MayImmediate { get; init; }
    public uint MayImmediateWide { get; init; }
    /// <summary>Where it jumps: its labels' blocks in operand order, then its table's; retargeted by the passes, applied by the target.</summary>
    public List<ABlock>? Targets { get; set; }
    /// <summary>Whether control never falls out of it to the next block.</summary>
    public bool Unconditional { get; init; }
    public int Line { get; init; }
    /// <summary>What the allocator made it as, where Tag is null.</summary>
    public Made Made { get; init; }
}

/// <summary>What the allocator made: a copy of operand 1 into operand 0, or a jump to Targets[0].</summary>
public enum Made : byte { None, Copy, Jump }

public sealed class ABlock
{
    public required string Name { get; init; }
    /// <summary>The target's block, or null for one the allocator made.</summary>
    public object? Tag { get; init; }
    public List<AInstr> Instrs { get; } = new();
    /// <summary>Entered from anywhere in its try, by no edge.</summary>
    public bool LandingPad { get; init; }
    /// <summary>Whether some instruction takes this block's address, so it is entered by that, not only by edges.</summary>
    public bool AddressTaken { get; set; }

    public bool EndsUnconditionally => Instrs.Count > 0 && Instrs[^1].Unconditional;
    public override string ToString() => Name;
}

/// <summary>
/// A FUNCTION AS THE ALLOCATOR SEES IT: its blocks in layout order, as the
/// target lowered them (ITarget.Lower), with what the allocator decides
/// written back by the target (ITarget.Rebuild).
/// </summary>
public sealed class AFunction
{
    public required string Name { get; init; }
    public required RegisterFile Registers { get; init; }
    public required ITarget Target { get; init; }
    public List<ABlock> Blocks { get; } = new();
    public int NextVReg { get; set; }

    /// <summary>Every successor of the block at `index`: its jumps' targets, then the next block where it falls through.</summary>
    public void SuccessorsInto(int index, List<ABlock> into)
    {
        into.Clear();
        ABlock b = Blocks[index];
        foreach (AInstr i in b.Instrs)
            if (i.Targets is { } targets) into.AddRange(targets);
        if (!b.EndsUnconditionally && index + 1 < Blocks.Count) into.Add(Blocks[index + 1]);
    }
}

/// <summary>
/// WHAT A MACHINE TELLS THE ALLOCATOR, and does for it: the one allocator
/// for every backend (i386, x86-64) knows nothing of an instruction set. A
/// target lowers its function into the allocator's form, answers for its
/// values' classes and constraints, makes the values and instructions the
/// allocator adds, and rebuilds its function from what was decided.
/// </summary>
public interface ITarget
{
    RegisterFile Registers { get; }

    /// <summary>The class of a register, physical or virtual (an index into Registers.Classes).</summary>
    int ClassOf(int reg);
    /// <summary>Whether a value is limited to its class's byte-capable registers.</summary>
    bool ByteOnly(int vreg);
    /// <summary>A new virtual register of `like`'s class and constraints.</summary>
    int NewVRegLike(int like);
    /// <summary>A new spill slot for a value of `vreg`'s class: its frame offset.</summary>
    int NewSlot(int vreg);

    /// <summary>
    /// The function rebuilt from the allocation: each instruction from its
    /// placements (IPlacement), made instructions made, the loads, stores
    /// and constants placed around it, safepoints and saved registers set.
    /// </summary>
    void Rebuild(AFunction f, IPlacement placement);

    /// <summary>One instruction as text, for the allocator's trace.</summary>
    string Text(AInstr i);
    /// <summary>A physical register's name, for the trace.</summary>
    string RegName(int reg);
    /// <summary>Whether a constant (AInstr.Constant) is a plain number, which fits an operand narrower than a whole register.</summary>
    bool PlainConstant(object constant);
}

/// <summary>Where the allocator put something an instruction names.</summary>
public enum Place : byte { Register, Slot, Constant }

/// <summary>
/// WHAT THE ALLOCATOR DECIDED, asked by the target as it rebuilds: for an
/// instruction (by its index in layout order) every register it names, in
/// the order it named them, its place -- a physical register, a spill slot
/// (the operand made memory) or a constant (made an immediate) -- and the
/// loads, stores and constants before and after it, whether it goes, and
/// the values live across it if it is a call.
/// </summary>
public interface IPlacement
{
    /// <summary>The place of the `k`-th register instruction `index` names (AInstr.Regs[k]).</summary>
    (Place Place, int Reg, int Slot, object? Constant) Of(int index, int k);
    /// <summary>What goes before the instruction: a register loaded from a slot, or set to a constant.</summary>
    IReadOnlyList<(int Reg, int Slot, object? Constant)> Before(int index);
    /// <summary>What goes after it: a register stored to its slot.</summary>
    IReadOnlyList<(int Reg, int Slot)> After(int index);
    /// <summary>Whether the instruction goes altogether: a spilled constant's definition, a copy whose value is already where it is copied to.</summary>
    bool Dropped(int index);
    /// <summary>At a call: the spill slots and the callee-saved registers holding values live across it.</summary>
    (List<int> Slots, List<int> Registers)? LiveAcross(int index);
    /// <summary>The physical registers the function writes or reads at all, its callee-saved ones to be saved.</summary>
    IReadOnlyCollection<int> Touched { get; }
}
