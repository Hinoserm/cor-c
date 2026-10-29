#nullable enable
using Corsac.Lang.RegAlloc;
using Role = Corsac.Lang.RegAlloc.Role;

namespace Corsac.Lang.X64;

/// <summary>
/// x86-64 AS THE ONE ALLOCATOR SEES IT (Corsac.Lang.RegAlloc): two classes,
/// the fourteen general registers -- the caller-saved nine first, then RBX
/// and R12 to R15 -- and the sixteen XMM registers, every one caller-saved
/// under the System V ABI; RSP and RBP never touched. Each instruction
/// lowered once into the registers it names (Allocator.RegsOf), which
/// operands may be memory (Allocator.Foldable) or a constant, its jumps; and
/// the function rebuilt from what was decided, a spilled value moved by its
/// class's move (MOV, MOVSD) through an eight-byte slot.
/// </summary>
internal sealed class X64Target : ITarget
{
    private const int Gprs = 0, Xmms = 1;

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
            },
        },
    };

    private readonly MFunction _m;

    public X64Target(MFunction m) => _m = m;

    public RegisterFile Registers => File;

    public int ClassOf(int reg) => reg < MReg.FirstVirtual
        ? (reg >= MReg.XmmBase ? Xmms : Gprs)
        : (_m.FloatRegs.Contains(reg) ? Xmms : Gprs);

    /// <summary>Every general register has a byte form under REX.</summary>
    public bool ByteOnly(int vreg) => false;
    public int NewSlot(int vreg) => _m.Frame.Spill();
    public int NewVRegLike(int like) => _m.FloatRegs.Contains(like) ? _m.NewFloat().Id : _m.NewReg().Id;
    public string RegName(int reg) => new MReg(reg).ToString();
    /// <summary>A constant any operand carries: one that fits 32 bits signed (an instruction's immediate, sign-extended).</summary>
    public bool PlainConstant(object constant) => ((MImm)constant).FitsInt32;

    public string Text(AInstr i) => i.Tag is MInstr m ? $"{m.Op} {string.Join(", ", m.Operands)}"
        : i.Made == Made.Copy ? $"copy v{i.CopyDst}, v{i.CopySrc}" : $"jmp {i.Targets![0].Name}";

    /// <summary>The function in the allocator's form.</summary>
    public AFunction Lower()
    {
        AFunction f = new() { Name = _m.Source.Name, Registers = File, Target = this, NextVReg = _m.NextVReg };
        Dictionary<MBlock, ABlock> blocks = new(ReferenceEqualityComparer.Instance);
        foreach (MBlock b in _m.Blocks)
            blocks[b] = new ABlock { Name = b.Name, Tag = b, LandingPad = b.Source?.IsLandingPad == true };
        _homes = Homes();
        foreach (MBlock b in _m.Blocks)
        {
            ABlock ab = blocks[b];
            foreach (MInstr i in b.Instrs)
            {
                ab.Instrs.Add(LowerInstr(i, blocks));
                foreach (MOperand o in i.Operands)
                    if (o is MMem { Label: { } ml } && blocks.TryGetValue(ml, out ABlock? mi)) mi.AddressTaken = true;
            }
            f.Blocks.Add(ab);
        }
        return f;
    }

    private AInstr LowerInstr(MInstr i, Dictionary<MBlock, ABlock> blocks)
    {
        List<Named> named = new();
        foreach ((MReg r, Roles.Role role, int operand, bool inMem) in Allocator.RegsOf(i))
            named.Add(new Named(r.Id, (Role)(byte)role, operand, inMem));
        uint mayMemory = 0, mayMemoryWrite = 0, isMemory = 0, mayImmediate = 0, mayImmediateWide = 0;
        for (int k = 0; k < i.Operands.Count && k < 32; k++)
        {
            if (Allocator.Foldable(i, k))
            {
                mayMemory |= 1u << k;
                // A NARROW WRITE LEAVES THE SLOT'S TOP HALF AS IT WAS, and a
                // reload is the whole eight bytes: a general register written
                // at less than eight bytes is written in a register.
                if (i.Width == 8 || i.Operands[k] is MReg r && (r.IsFloat || _m.FloatRegs.Contains(r.Id))) mayMemoryWrite |= 1u << k;
            }
            if (i.Operands[k] is MMem) isMemory |= 1u << k;
        }
        // A constant in place of a register: a move's or an ALU operation's
        // source, any constant where the operation is 32 bits wide, else one
        // that fits 32 bits signed; a push's, one that fits (PlainConstant).
        // A 64-bit one folded into a move whose destination is later spilled
        // would ask for `mov m64, imm64`, which x86-64 does not have.
        if (i.Op is MOp.Mov or MOp.Add or MOp.Sub or MOp.And or MOp.Or or MOp.Xor or MOp.Adc or MOp.Sbb or MOp.Cmp)
        {
            mayImmediate = 1u << 1;
            if (i.Width == 4) mayImmediateWide = 1u << 1;
        }
        else if (i.Op == MOp.Push) mayImmediate = 1u;
        bool copy = i.Op is MOp.Mov or MOp.MovF && i.Operands.Count == 2 && i.Operands[0] is MReg && i.Operands[1] is MReg;
        object? constant = null;
        // The width is part of the value: a 4-byte move of -1 is 0xFFFFFFFF.
        if (i.Op == MOp.Mov && i.Operands.Count == 2 && i.Operands[0] is MReg { IsFloat: false } && i.Operands[1] is MImm { IsPlain: true } imm)
            constant = i.Width == 4 ? new MImm((uint)imm.Value) : imm;
        List<ABlock>? targets = null;
        if (i.Op is MOp.Jmp or MOp.Jcc && i.Operands[0] is MLabel label) targets = new() { blocks[label.Target] };
        else if (i.Op == MOp.JmpTable) targets = i.Table!.Select(t => blocks[t]).ToList();
        return new AInstr
        {
            Tag = i,
            Regs = named.ToArray(),
            IsCall = i.Op is MOp.Call or MOp.CallInd,
            IsCopy = copy,
            // A 32-bit move onto itself clears the upper half, which is its meaning when written so.
            DropsSelf = copy && (i.Op == MOp.MovF || i.Width == 8),
            WholeCopy = copy && !i.Lock && (i.Op == MOp.MovF || i.Width == 8),
            Constant = constant,
            OperandCount = i.Operands.Count,
            MayMemory = mayMemory,
            MayMemoryWrite = mayMemoryWrite,
            IsMemory = isMemory,
            MayImmediate = mayImmediate,
            MayImmediateWide = mayImmediateWide,
            Targets = targets,
            Unconditional = i.Op is MOp.Jmp or MOp.Ret or MOp.JmpTable or MOp.JmpInd or MOp.Epilogue or MOp.Ud2,
            JumpsAnywhere = i.Op == MOp.JmpInd,
            HomeSlot = _homes.Contains(i) ? ((MMem)i.Operands[1]).Disp : null,
            Line = i.Line,
        };
    }

    private HashSet<MInstr> _homes = new(ReferenceEqualityComparer.Instance);

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
                    // Below the arguments (the saved frame pointer, the return address): only
                    // what reaches past them, sixteen bytes at most, can touch one.
                    if (mem.Disp < 16 || mem.Symbol is not null || mem.Label is not null)
                    {
                        if (mem.Disp > 16 - 16) touched.Add((mem.Disp, mem.Disp + 16));
                        continue;
                    }
                    if (i.Op == MOp.Mov && k == 1 && i.Width == 8 && !i.Lock && i.Operands[0] is MReg { IsPhys: false, IsFloat: false } && (mem.Disp & 7) == 0)
                        load[mem.Disp] = load.ContainsKey(mem.Disp) ? null : i;
                    else touched.Add((mem.Disp, mem.Disp + 16));
                }
        foreach ((int disp, MInstr? i) in load)
            if (i is not null && !touched.Any(t => t.From < disp + 8 && disp < t.To)) homes.Add(i);
        return homes;
    }

    /// <summary>
    /// THE FUNCTION REBUILT from the allocation: each instruction from the
    /// places of the registers it names, in the order it named them; a
    /// spilled value's loads before and stores after by its class's move,
    /// eight bytes; a remade constant moved whole; the copies and jumps the
    /// allocator made, made; the callee-saved registers touched saved, or
    /// all of them where the function enters the collector. No stack maps
    /// (IPlacement.LiveAcross): the runtime has none on x86-64 (gc.cor's
    /// BuildMaps is i386's), and its conservative collector reads a stack as
    /// a range of memory.
    /// </summary>
    public void Rebuild(AFunction f, IPlacement p)
    {
        Dictionary<ABlock, MBlock> blocks = new(ReferenceEqualityComparer.Instance);
        foreach (ABlock ab in f.Blocks) blocks[ab] = ab.Tag as MBlock ?? new MBlock(ab.Name);
        List<MBlock> laid = new();
        int index = 0;
        foreach (ABlock ab in f.Blocks)
        {
            MBlock b = blocks[ab];
            List<MInstr> outList = new();
            foreach (AInstr ai in ab.Instrs)
            {
                // Asked first: a dropped instruction is not built at all.
                MInstr? n = p.Dropped(index) ? null : Build(ai, index, p, blocks);
                foreach ((int reg, int slot, object? constant) in p.Before(index))
                {
                    outList.Add(constant is MImm imm
                        ? new MInstr(MOp.Mov, new MReg(reg), imm) { Width = 8, Line = ai.Line }
                        : new MInstr(ClassOf(reg) == Xmms ? MOp.MovF : MOp.Mov, new MReg(reg), Slot(slot)) { Width = 8, Line = ai.Line });
                }
                if (n is not null) outList.Add(n);
                foreach ((int reg, int slot) in p.After(index))
                    outList.Add(new MInstr(ClassOf(reg) == Xmms ? MOp.MovF : MOp.Mov, Slot(slot), new MReg(reg)) { Width = 8, Line = ai.Line });
                index++;
            }
            b.Instrs.Clear();
            b.Instrs.AddRange(outList);
            laid.Add(b);
        }
        _m.Blocks.Clear();
        _m.Blocks.AddRange(laid);
        _m.NextVReg = Math.Max(_m.NextVReg, f.NextVReg);
        foreach (Gpr g in Allocator.CalleeSaved)
            if (_m.SavesEverything || p.Touched.Contains((int)g)) _m.SavedRegs.Add(g);
    }

    /// <summary>
    /// A value's slot as an operand: the allocator's own below the frame
    /// pointer, or a home above it -- an argument's word, read as the
    /// argument load it is, not counted as a spill slot's traffic.
    /// </summary>
    private static MMem Slot(int offset) => offset > 0 ? MMem.Frame(offset) : MMem.Spill(offset);

    private static MOperand PlaceOf((Place Place, int Reg, int Slot, object? Constant) at, MReg original)
        => at.Place switch
        {
            Place.Slot => Slot(at.Slot),
            Place.Constant => (MImm)at.Constant!,
            _ => original.IsPhys ? original : new MReg(at.Reg),
        };

    private MInstr Build(AInstr ai, int index, IPlacement p, Dictionary<ABlock, MBlock> blocks)
    {
        if (ai.Tag is not MInstr i)
        {
            if (ai.Made == Made.Jump) return new MInstr(MOp.Jmp, new MLabel(blocks[ai.Targets![0]])) { Line = ai.Line };
            int dst = ai.Regs[0].Reg, src = ai.Regs[1].Reg;
            return new MInstr(ClassOf(dst) == Xmms ? MOp.MovF : MOp.Mov, PlaceOf(p.Of(index, 0), new MReg(dst)), PlaceOf(p.Of(index, 1), new MReg(src)))
            {
                Width = 8, Line = ai.Line,
            };
        }
        List<MBlock>? table = i.Table;
        if (i.Op == MOp.JmpTable && ai.Targets is { } targets && targets.Where((t, k) => !ReferenceEquals(blocks[t], i.Table![k])).Any())
            table = targets.Select(t => blocks[t]).ToList();
        MInstr n = new(i.Op)
        {
            Width = i.Width, SourceWidth = i.SourceWidth, Cond = i.Cond, Lock = i.Lock, Table = table, TableSymbol = i.TableSymbol,
            Line = i.Line, IntArgs = i.IntArgs, FloatArgs = i.FloatArgs, NativeAl = i.NativeAl,
        };
        // The registers named, walked in the order Allocator.RegsOf named them.
        int k = 0;
        for (int o = 0; o < i.Operands.Count; o++)
        {
            switch (i.Operands[o])
            {
                case MReg r:
                    n.Operands.Add(PlaceOf(p.Of(index, k++), r));
                    break;
                case MMem m:
                    MReg? baseReg = m.Base is null ? null : (MReg)PlaceOf(p.Of(index, k++), m.Base);
                    MReg? indexReg = m.Index is null ? null : (MReg)PlaceOf(p.Of(index, k++), m.Index);
                    n.Operands.Add(new MMem(baseReg, m.Disp)
                    {
                        Index = indexReg, Scale = m.Scale, Symbol = m.Symbol, Got = m.Got, Label = m.Label, IsSpill = m.IsSpill, Segment = m.Segment,
                    });
                    break;
                case MLabel l when ai.Targets is { Count: > 0 } jump && i.Op is MOp.Jmp or MOp.Jcc:
                    MBlock to = blocks[jump[0]];
                    n.Operands.Add(ReferenceEquals(to, l.Target) ? l : new MLabel(to));
                    break;
                default:
                    n.Operands.Add(i.Operands[o]);
                    break;
            }
        }
        return n;
    }
}
