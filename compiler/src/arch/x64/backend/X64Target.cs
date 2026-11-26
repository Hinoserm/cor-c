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

    private static readonly RegisterFile File = new()
    {
        PhysCount = MReg.FirstVirtual,
        FirstVirtual = MReg.FirstVirtual,
        Untracked = new[] { (int)Gpr.Rsp, (int)Gpr.Rbp },
        Classes = new[]
        {
            new RegClass
            {
                Name = "gpr",
                // Caller-saved first: free when the value does not cross a call.
                Preference = new[]
                {
                    (int)Gpr.Rax, (int)Gpr.Rcx, (int)Gpr.Rdx, (int)Gpr.Rsi, (int)Gpr.Rdi,
                    (int)Gpr.R8, (int)Gpr.R9, (int)Gpr.R10, (int)Gpr.R11,
                    (int)Gpr.Rbx, (int)Gpr.R12, (int)Gpr.R13, (int)Gpr.R14, (int)Gpr.R15,
                },
                CalleeSaved = Allocator.CalleeSaved.Select(g => (int)g).ToArray(),
            },
            new RegClass
            {
                Name = "xmm",
                Preference = Enumerable.Range(MReg.XmmBase, 16).ToArray(),
                CalleeSaved = Array.Empty<int>(),
                HoldsReferences = false,
            },
        },
    };

    /// <summary>
    /// A LEAF'S REGISTERS: RBP besides, callee-saved (the prologue still
    /// pushes it), where nothing needs the frame pointer -- no call (a
    /// callee's prologue pushes RBP as the link to this frame; the collector
    /// and every unwinder walk that chain), no landing pad, no system call or
    /// software interrupt (SavesEverything), no realigned stack, nothing that
    /// names RBP but as the frame's base and nothing that moves RSP after the
    /// prologue, off which the frame is addressed instead (FramePointerFree).
    /// A fault in such a function finds its frame by the prologue's mark
    /// (Encoder.PrologueL).
    /// </summary>
    private static readonly RegisterFile LeafFile = new()
    {
        PhysCount = MReg.FirstVirtual,
        FirstVirtual = MReg.FirstVirtual,
        Untracked = new[] { (int)Gpr.Rsp },
        Classes = new[]
        {
            new RegClass
            {
                Name = "gpr",
                Preference = new[]
                {
                    (int)Gpr.Rax, (int)Gpr.Rcx, (int)Gpr.Rdx, (int)Gpr.Rsi, (int)Gpr.Rdi,
                    (int)Gpr.R8, (int)Gpr.R9, (int)Gpr.R10, (int)Gpr.R11,
                    (int)Gpr.Rbx, (int)Gpr.R12, (int)Gpr.R13, (int)Gpr.R14, (int)Gpr.R15, (int)Gpr.Rbp,
                },
                CalleeSaved = Allocator.CalleeSaved.Select(g => (int)g).Append((int)Gpr.Rbp).ToArray(),
            },
            new RegClass
            {
                Name = "xmm",
                Preference = Enumerable.Range(MReg.XmmBase, 16).ToArray(),
                CalleeSaved = Array.Empty<int>(),
                HoldsReferences = false,
            },
        },
    };

    /// <summary>
    /// A THUNK'S REGISTERS (Function.KeepsCallerRegisters): the caller-saved
    /// nine alone, so RBX and R12 to R15 keep what its caller left in them,
    /// and a value it holds across a call is held in its frame.
    /// </summary>
    private static readonly RegisterFile CallerOnlyFile = new()
    {
        PhysCount = MReg.FirstVirtual,
        FirstVirtual = MReg.FirstVirtual,
        Untracked = new[] { (int)Gpr.Rsp, (int)Gpr.Rbp },
        Classes = new[]
        {
            new RegClass
            {
                Name = "gpr",
                Preference = new[]
                {
                    (int)Gpr.Rax, (int)Gpr.Rcx, (int)Gpr.Rdx, (int)Gpr.Rsi, (int)Gpr.Rdi,
                    (int)Gpr.R8, (int)Gpr.R9, (int)Gpr.R10, (int)Gpr.R11,
                },
                CalleeSaved = Array.Empty<int>(),
            },
            new RegClass
            {
                Name = "xmm",
                Preference = Enumerable.Range(MReg.XmmBase, 16).ToArray(),
                CalleeSaved = Array.Empty<int>(),
                HoldsReferences = false,
            },
        },
    };

    public X64Target(MFunction m) : base(m, Leaf(m))
    {
    }

    /// <summary>Whether the function may run without a frame pointer (LeafFile).</summary>
    private static bool Leaf(MFunction m)
    {
        if (m.SavesEverything || m.RealignsStack) return false;
        List<(MReg Reg, Roles.Role Role, int Operand, bool InMem)> regs = new();
        foreach (MBlock b in m.Blocks)
        {
            if (b.Source?.IsLandingPad == true) return false;
            foreach (MInstr i in b.Instrs)
            {
                // The store sequences' stubs are calls too (CallKeep*): frameless,
                // so the barrier they call links to this function's RBP.
                // An asm block that pushes, names RSP or RBP or a slot off RBP.
                if (i.Op is MOp.Call or MOp.CallInd or MOp.CallSafe or MOp.CallKeep or MOp.CallKeepRax or MOp.CallKeepCas
                    or MOp.Push or MOp.Pop or MOp.Pushf or MOp.Popf) return false;
                if (i.Op == MOp.InlineAsm && i.Asm!.NeedsFrame) return false;
                foreach (MOperand o in i.Operands)
                    if (o is MMem { Index: { Id: (int)Gpr.Rbp } }) return false;
                foreach ((MReg r, Roles.Role role, int operand, bool inMem) in Roles.RegsOf(i, regs))
                {
                    if (r.Id == (int)Gpr.Rbp && !inMem) return false;
                    if (r.Id == (int)Gpr.Rsp && (role & Roles.Role.Def) != 0) return false;
                }
            }
        }
        return true;
    }

    public override RegisterFile Registers => _m.Source.KeepsCallerRegisters ? CallerOnlyFile : _leaf ? LeafFile : File;

    /// <summary>Every general register has a byte form under REX.</summary>
    public bool ByteOnly(int vreg) => false;
    public int NewSlot(int vreg) => _m.Frame.Spill();
    public int NewVRegLike(int like) => _m.XmmRegs.Contains(like) ? _m.NewFloat().Id : _m.NewReg().Id;
    public string RegName(int reg) => new MReg(reg).ToString();
    /// <summary>A constant any operand carries: one that fits 32 bits signed (an instruction's immediate, sign-extended).</summary>
    public bool PlainConstant(object constant) => constant is MImm { FitsInt32: true };

    public string Text(AInstr i) => i.Tag is MInstr m ? $"{m.Op} {string.Join(", ", m.Operands)}"
        : i.Made == Made.Copy ? $"copy v{i.CopyDst}, v{i.CopySrc}" : $"jmp {i.Targets![0].Name}";

    /// <summary>
    /// A NARROW WRITE LEAVES THE SLOT'S TOP HALF AS IT WAS, and a reload is
    /// the whole eight bytes: a general register written at less than eight
    /// bytes is written in a register.
    /// </summary>
    protected override bool MayWriteMemory(MInstr i, int operand)
        => i.Width == 8 || i.Operands[operand] is MReg r && (r.IsFloat || _m.XmmRegs.Contains(r.Id));

    /// <summary>
    /// A constant moved into a general register -- the width part of the
    /// value: a 4-byte move of -1 is 0xFFFFFFFF -- or an address an LEA
    /// makes from nothing the allocator places: off RBP, which holds still,
    /// or a symbol RIP-relative; not off RSP, which every push moves, nor
    /// through the GOT. (A 64-bit constant folded into a move whose
    /// destination is later spilled would ask for `mov m64, imm64`, which
    /// x86-64 does not have: Roles.Foldable.)
    /// </summary>
    protected override object? ConstantOf(MInstr i)
    {
        if (i.Op == MOp.Mov && i.Operands.Count == 2 && i.Operands[0] is MReg { IsFloat: false } && i.Operands[1] is MImm { IsPlain: true } imm)
            return i.Width == 4 ? new MImm((uint)imm.Value) : imm;
        if (i.Op == MOp.Lea && i.Width == 8 && i.Operands.Count == 2 && i.Operands[0] is MReg { IsMachine: false, IsFloat: false }
            && i.Operands[1] is MMem { Index: null, Label: null, IsSpill: false, Got: false, Segment: 0 } m
            && (m.Base is null ? m.Symbol is not null : m.Base.Id == (int)Gpr.Rbp && m.Symbol is null))
            return m;
        return null;
    }

    protected override int? HomeOf(MInstr i) => _homes.Contains(i) ? ((MMem)i.Operands[1]).Disp : null;

    private HashSet<MInstr> _homes = new(ReferenceEqualityComparer.Instance);

    protected override void BeforeLowering() => _homes = Homes();

    /// <summary>
    /// THE LOADS THAT ARE HOMES (AInstr.HomeSlot): `mov v, [rbp+d]` of a
    /// stack-passed argument's eight bytes, the only thing in the function
    /// that touches any byte of them, nothing taking an address off RBP --
    /// no LEA of it, no indexed access above it (RBP as a value is read
    /// through only: Sys.FramePointer's contract) -- and
    /// any other access taken as sixteen bytes wide. x86-64's collector
    /// reads a stack as memory: no map to name it in.
    /// </summary>
    private HashSet<MInstr> Homes()
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

    /// <summary>A remade constant moved whole, an address by LEA, any other value loaded from its slot.</summary>
    protected override MInstr Remade(int reg, int slot, object? constant, int line)
        => constant is MMem address
            ? new MInstr(MOp.Lea, Out(reg), new MMem(address.Base, address.Disp) { Symbol = address.Symbol }) { Width = 8, Line = line }
            : constant is MImm imm
            ? new MInstr(MOp.Mov, Out(reg), imm) { Width = 8, Line = line }
            : Move(Out(reg), Slot(slot), line);

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

    protected override bool Fixed(MReg original) => original.IsMachine;

    /// <summary>A spilled value moved by its class's move (MOV, MOVSD) through an eight-byte slot: the class the register operand is of.</summary>
    protected override MInstr Move(MOperand to, MOperand from, int line)
        => new MInstr(to is MReg r && ClassOf(r.Id) == Xmms || from is MReg s && ClassOf(s.Id) == Xmms ? MOp.MovF : MOp.Mov, to, from) { Width = 8, Line = line };

    /// <summary>The allocator's copy, by the class of the register copied into.</summary>
    protected override MInstr Copy(int destination, MOperand to, MOperand from, int line)
        => new MInstr(ClassOf(destination) == Xmms ? MOp.MovF : MOp.Mov, to, from) { Width = 8, Line = line };

    /// <summary>A register the allocator placed a value in: RBP as the value it holds (MReg.RbpData), no frame's base.</summary>
    protected override MReg Out(int reg) => reg == (int)Gpr.Rbp ? MReg.RbpData : new MReg(reg);
}
