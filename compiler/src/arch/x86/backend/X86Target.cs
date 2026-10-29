#nullable enable
using Corsac.Lang.Ir;
using Corsac.Lang.RegAlloc;

namespace Corsac.Lang.X86;

/// <summary>
/// i386 AS THE ONE ALLOCATOR SEES IT (Corsac.Lang.RegAlloc): one class of
/// six registers -- EAX, ECX, EDX first, free across no call, then the
/// callee-saved EBX, ESI, EDI -- of which EAX to EBX have byte halves; ESP
/// and EBP never touched. Each instruction lowered once into the registers
/// it names (Allocator.RegsOf), which operands may be memory or an
/// immediate (Allocator.Foldable, FoldableImm), its jumps; and the function
/// rebuilt from what was decided, the loads, stores and constants around
/// each instruction made here.
/// </summary>
internal sealed class X86Target : ITarget
{
    private static readonly RegisterFile File = new()
    {
        PhysCount = 8,
        FirstVirtual = 8,
        Untracked = new[] { (int)Gpr.Esp, (int)Gpr.Ebp },
        Classes = new[]
        {
            new RegClass
            {
                Name = "gpr",
                // Caller-saved first: an interval that does not cross a call costs
                // nothing in one of these, while a callee-saved register costs a
                // push and a pop in the prologue and epilogue.
                Preference = new[] { (int)Gpr.Eax, (int)Gpr.Ecx, (int)Gpr.Edx, (int)Gpr.Ebx, (int)Gpr.Esi, (int)Gpr.Edi },
                CalleeSaved = new[] { (int)Gpr.Ebx, (int)Gpr.Esi, (int)Gpr.Edi },
                ByteCapable = new[] { (int)Gpr.Eax, (int)Gpr.Ecx, (int)Gpr.Edx, (int)Gpr.Ebx },
            },
        },
    };

    private readonly MFunction _m;
    private readonly List<(MReg Reg, Roles.Role Role, int Operand, bool InMem)> _regs = new();

    public X86Target(MFunction m)
    {
        _m = m;
        _leaf = Leaf(m);
    }

    /// <summary>
    /// A LEAF'S REGISTERS: EBP besides, callee-saved (the prologue still
    /// pushes it), where nothing needs the frame pointer -- no call, a store
    /// sequence's stub's included (a callee's prologue pushes EBP as the link
    /// to this frame; the collector and every unwinder walk that chain), no
    /// landing pad, nothing that names
    /// EBP but as the frame's base (Sys.FramePointer, a six-argument system
    /// call) and nothing that moves ESP after the prologue, off which the frame
    /// is addressed instead (FramePointerFree). A fault in such a function
    /// finds its frame by the prologue's mark (Encoder.EncodePrologue).
    /// </summary>
    private static readonly RegisterFile LeafFile = new()
    {
        PhysCount = 8,
        FirstVirtual = 8,
        Untracked = new[] { (int)Gpr.Esp },
        Classes = new[]
        {
            new RegClass
            {
                Name = "gpr",
                Preference = new[] { (int)Gpr.Eax, (int)Gpr.Ecx, (int)Gpr.Edx, (int)Gpr.Ebx, (int)Gpr.Esi, (int)Gpr.Edi, (int)Gpr.Ebp },
                CalleeSaved = new[] { (int)Gpr.Ebx, (int)Gpr.Esi, (int)Gpr.Edi, (int)Gpr.Ebp },
                ByteCapable = new[] { (int)Gpr.Eax, (int)Gpr.Ecx, (int)Gpr.Edx, (int)Gpr.Ebx },
            },
        },
    };

    private readonly bool _leaf;

    /// <summary>Whether the function may run without a frame pointer (LeafFile).</summary>
    private static bool Leaf(MFunction m)
    {
        List<(MReg Reg, Roles.Role Role, int Operand, bool InMem)> regs = new();
        foreach (MBlock b in m.Blocks)
        {
            if (b.Source?.IsLandingPad == true) return false;
            foreach (MInstr i in b.Instrs)
            {
                // The store sequences' stubs are calls too (CallKeep*): frameless,
                // so the barrier they call links to this function's EBP.
                if (i.Op is MOp.Call or MOp.CallInd or MOp.CallKeep or MOp.CallKeepInd or MOp.CallKeepEax or MOp.CallKeepEaxInd
                    or MOp.Push or MOp.Pop or MOp.Pushfd or MOp.Popfd) return false;
                foreach (MOperand o in i.Operands)
                    if (o is MMem { Index: { Id: (int)Gpr.Ebp } }) return false;
                foreach ((MReg r, Roles.Role role, int operand, bool inMem) in Allocator.RegsOf(i, regs))
                {
                    if (r.Id == (int)Gpr.Ebp && !inMem) return false;
                    if (r.Id == (int)Gpr.Esp && (role & Roles.Role.Def) != 0) return false;
                }
            }
        }
        return true;
    }

    public RegisterFile Registers => _leaf ? LeafFile : File;
    public int ClassOf(int reg) => 0;
    public bool ByteOnly(int vreg) => _m.ByteRegs.Contains(vreg);
    public int NewSlot(int vreg) => _m.Frame.Spill();
    public string RegName(int reg) => ((Gpr)reg).ToString();
    public bool PlainConstant(object constant) => constant is MImm { IsPlain: true };
    public bool Immediate(object constant) => constant is MImm;

    /// <summary>
    /// An address an LEA makes from nothing the allocator places -- off EBP,
    /// which holds still through the function, or a symbol -- remade where it
    /// is wanted (AInstr.Constant); not off ESP, which every push moves.
    /// </summary>
    private static MMem? Address(MInstr i) => i.Op == MOp.Lea && i.Width == 4 && i.Operands.Count == 2 && i.Operands[0] is MReg { IsPhys: false }
        && i.Operands[1] is MMem { Index: null } m && (m.Base is null ? m.Symbol is not null && m.Reloc == RelocKind.Abs32 : m.Base.Id == (int)Gpr.Ebp && m.Symbol is null)
        && m.Label is null && !m.IsSpill ? m : null;

    public int NewVRegLike(int like)
    {
        int v = _m.NewReg().Id;
        if (_m.ByteRegs.Contains(like)) _m.ByteRegs.Add(v);
        if (_m.WideHalves.Contains(like)) _m.WideHalves.Add(v);
        return v;
    }

    public string Text(AInstr i) => i.Tag is MInstr m ? AsmText.Line(_m, m)
        : i.Made == Made.Copy ? $"copy v{i.CopyDst}, v{i.CopySrc}" : $"jmp {i.Targets![0].Name}";

    /// <summary>The function in the allocator's form.</summary>
    public AFunction Lower()
    {
        AFunction f = new() { Name = _m.Source.Name, Registers = Registers, Target = this, NextVReg = _m.NextVReg };
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
                {
                    if (o is MImm { Label: { } l } && blocks.TryGetValue(l, out ABlock? li)) li.AddressTaken = true;
                    if (o is MMem { Label: { } ml } && blocks.TryGetValue(ml, out ABlock? mi)) mi.AddressTaken = true;
                }
            }
            f.Blocks.Add(ab);
        }
        return f;
    }

    private AInstr LowerInstr(MInstr i, Dictionary<MBlock, ABlock> blocks)
    {
        List<(MReg Reg, Roles.Role Role, int Operand, bool InMem)> regs = Allocator.RegsOf(i, _regs);
        // In a leaf the frame's base is no use of the register EBP holds:
        // FramePointerFree makes it ESP (Leaf: EBP is named nowhere else).
        if (_leaf) regs.RemoveAll(r => r.Reg.Id == (int)Gpr.Ebp);
        Named[] named = new Named[regs.Count];
        for (int k = 0; k < regs.Count; k++)
            named[k] = new Named(regs[k].Reg.Id, (Role)(byte)regs[k].Role, regs[k].Operand, regs[k].InMem);
        uint mayMemory = 0, isMemory = 0, mayImmediate = 0;
        for (int k = 0; k < i.Operands.Count && k < 32; k++)
        {
            if (Allocator.Foldable(i.Op, k)) mayMemory |= 1u << k;
            if (Allocator.FoldableImm(i.Op, k)) mayImmediate |= 1u << k;
            if (i.Operands[k] is MMem) isMemory |= 1u << k;
        }
        bool copy = i.Op == MOp.Mov && i.Operands.Count == 2 && i.Operands[0] is MReg && i.Operands[1] is MReg;
        List<ABlock>? targets = null;
        if (i.Op is MOp.Jmp or MOp.Jcc && i.Operands[0] is MLabel label) targets = new() { blocks[label.Target] };
        else if (i.Op == MOp.JmpTable) targets = i.Table!.Select(t => blocks[t]).ToList();
        return new AInstr
        {
            Tag = i,
            Regs = named,
            IsCall = i.Op is MOp.Call or MOp.CallInd,
            IsCopy = copy,
            WholeCopy = copy && i.Width == 4 && !i.Lock,
            DropsSelf = copy,
            Constant = i.Op == MOp.Mov && i.Width == 4 && i.Operands.Count == 2 && i.Operands[0] is MReg && i.Operands[1] is MImm imm ? imm : Address(i),
            OperandCount = i.Operands.Count,
            MayMemory = mayMemory,
            MayMemoryWrite = mayMemory,
            IsMemory = isMemory,
            MayImmediate = mayImmediate,
            MayImmediateWide = i.Width == 4 ? mayImmediate : 0,
            Targets = targets,
            Unconditional = i.Op is MOp.Jmp or MOp.Ret or MOp.JmpTable or MOp.JmpInd or MOp.Epilogue or MOp.Int3,
            JumpsAnywhere = i.Op == MOp.JmpInd,
            HomeSlot = _homes.Contains(i) ? ((MMem)i.Operands[1]).Disp : null,
            Line = i.Line,
            Effects = Effects(i),
        };
    }

    /// <summary>
    /// What an instruction does besides its registers, for the scheduler
    /// (Effect): the flags and memory of the plain integer instructions it may
    /// move; anything else -- a call, a branch, a push, a divide (it may fault
    /// on its own), x87 and packed state, a locked or device access -- a
    /// barrier. INC, DEC, a shift by CL (a count of 0 changes nothing) and
    /// ADC/SBB read the flags they leave partly or wholly as they were.
    /// </summary>
    private static Effect Effects(MInstr i)
    {
        if (i.Lock) return Effect.Barrier;
        bool countImm = i.Operands.Count >= 2 && i.Operands[^1] is MImm { Value: >= 1 };
        Effect e = i.Op switch
        {
            MOp.Mov or MOp.Movzx or MOp.Movsx or MOp.Lea or MOp.Not or MOp.Bswap or MOp.Cdq => Effect.None,
            MOp.Add or MOp.Sub or MOp.And or MOp.Or or MOp.Xor or MOp.Cmp or MOp.Test or MOp.Neg
                or MOp.Imul or MOp.Imul3 or MOp.Mul or MOp.ImulWide => Effect.WritesFlags,
            MOp.Adc or MOp.Sbb or MOp.Inc or MOp.Dec => Effect.ReadsFlags | Effect.WritesFlags,
            MOp.Shl or MOp.Shr or MOp.Sar or MOp.Ror or MOp.Shld or MOp.Shrd
                => countImm ? Effect.WritesFlags : Effect.ReadsFlags | Effect.WritesFlags,
            MOp.Setcc or MOp.Cmov => Effect.ReadsFlags,
            _ => Effect.Barrier,
        };
        if (e == Effect.Barrier) return e;
        for (int k = 0; k < i.Operands.Count; k++)
        {
            if (i.Operands[k] is not MMem m || i.Op == MOp.Lea) continue;
            if (m.Device) return Effect.Barrier;
            Roles.Role role = Roles.Of(i.Op, k);
            if ((role & Roles.Role.Use) != 0) e |= Effect.ReadsMemory;
            if ((role & Roles.Role.Def) != 0) e |= Effect.WritesMemory;
        }
        return e;
    }

    private HashSet<MInstr> _homes = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// THE LOADS THAT ARE HOMES (AInstr.HomeSlot): `mov v, [ebp+d]` of an
    /// incoming argument's word, where it is the only thing in the function
    /// that touches any byte of that word, and nothing takes an address off
    /// EBP -- through one, any argument could be read or written: no LEA of
    /// it, no indexed access above it. EBP as a value (Sys.FramePointer) is
    /// read through, never written above the return address -- its contract
    /// (Prelude), held by every use in the runtime and the kernel, which walk
    /// the chain of saved frame pointers and return addresses -- and a home
    /// is never written by the allocator either (Allocator.HomeOf). Any other access at or above the
    /// frame pointer (a local below it reaches no argument) is taken as sixteen bytes
    /// wide, whatever its Width says: a packed store writes eight with the
    /// default four (Peephole.ParameterHomes counts as much). The frame
    /// outside reads that word as one it is pushing, so no stack map need.
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
                    if (mem.Index?.Id == (int)Gpr.Ebp) return homes;
                    if (mem.Base?.Id != (int)Gpr.Ebp) continue;
                    if (i.Op == MOp.Lea || mem.Index is not null && mem.Disp >= 0) return homes;
                    // The saved frame pointer and the return address, between the locals
                    // and the arguments: an access there, sixteen bytes at most, may reach
                    // one. A local below the frame pointer reaches none without writing
                    // over the return address first.
                    if (mem.Disp < 8 || mem.Symbol is not null || mem.Label is not null)
                    {
                        if (mem.Disp >= 0) touched.Add((mem.Disp, mem.Disp + 16));
                        continue;
                    }
                    if (i.Op == MOp.Mov && k == 1 && i.Width == 4 && !i.Lock && i.Operands[0] is MReg { IsPhys: false } && (mem.Disp & 3) == 0)
                    {
                        // A second load of the word makes it no home.
                        load[mem.Disp] = load.ContainsKey(mem.Disp) ? null : i;
                    }
                    else touched.Add((mem.Disp, mem.Disp + 16));
                }
        foreach ((int disp, MInstr? i) in load)
            if (i is not null && !touched.Any(t => t.From < disp + 4 && disp < t.To)) homes.Add(i);
        return homes;
    }

    /// <summary>
    /// THE FUNCTION REBUILT from the allocation: each instruction from the
    /// places of the registers it names, in the order it named them -- a
    /// register, its spill slot as a memory operand, or its constant as an
    /// immediate -- with the loads and constants before it and the stores
    /// after; the copies and jumps the allocator made, made; a call's live
    /// values written into its stack map; the callee-saved registers the
    /// function touches saved.
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
                if (n is not null && p.LiveAcross(index) is { } live)
                {
                    Safepoint map = new();
                    map.SlotOffsets.AddRange(live.Slots);
                    foreach (int r in live.Registers) map.Registers |= 1u << r;
                    _m.Safepoints[n] = map;
                }
                foreach ((int reg, int slot, bool load) in p.MovesBefore(index))
                    outList.Add(load
                        ? new MInstr(MOp.Mov, Out(reg), Slot(slot)) { Line = ai.Line }
                        : new MInstr(MOp.Mov, Slot(slot), Out(reg)) { Line = ai.Line });
                foreach ((int reg, int slot, object? constant) in p.Before(index))
                    outList.Add(constant is MMem address
                        ? new MInstr(MOp.Lea, Out(reg), new MMem(address.Base, address.Disp) { Symbol = address.Symbol, Reloc = address.Reloc, Device = address.Device }) { Line = ai.Line }
                        : new MInstr(MOp.Mov, Out(reg), constant is MImm imm ? imm : Slot(slot)) { Line = ai.Line });
                if (n is not null) outList.Add(n);
                foreach ((int reg, int slot) in p.After(index))
                    outList.Add(new MInstr(MOp.Mov, Slot(slot), Out(reg)) { Line = ai.Line });
                index++;
            }
            b.Instrs.Clear();
            b.Instrs.AddRange(outList);
            laid.Add(b);
        }
        _m.Blocks.Clear();
        _m.Blocks.AddRange(laid);
        _m.NextVReg = Math.Max(_m.NextVReg, f.NextVReg);
        // A FUNCTION THAT CATCHES SAVES THEM ALL: a throw restores only ESP
        // and EBP, so the registers the unwound frames saved and used reach
        // its landing pad as they left them, and only an epilogue that
        // restores every one gives its caller back what it had.
        bool catches = f.Blocks.Any(b => b.LandingPad);
        foreach (int g in File.Classes[0].CalleeSaved)
            if (catches || p.Touched.Contains(g)) _m.SavedRegs.Add((Gpr)g);
        // EBP given a value: saved by the prologue's own push, not among these.
        _m.FramePointerFree = _leaf && p.Touched.Contains((int)Gpr.Ebp);
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
            _ => original.IsPhys ? original : Out(at.Reg),
        };

    /// <summary>A register the allocator placed a value in: EBP as the value it holds (MReg.EbpData), no frame's base.</summary>
    private static MReg Out(int reg) => reg == (int)Gpr.Ebp ? MReg.EbpData : MReg.Of(reg);

    private MInstr Build(AInstr ai, int index, IPlacement p, Dictionary<ABlock, MBlock> blocks)
    {
        if (ai.Tag is not MInstr i)
        {
            if (ai.Made == Made.Jump) return new MInstr(MOp.Jmp, new MLabel(blocks[ai.Targets![0]])) { Line = ai.Line };
            return new MInstr(MOp.Mov, PlaceOf(p.Of(index, 0), MReg.Of(ai.Regs[0].Reg)), PlaceOf(p.Of(index, 1), MReg.Of(ai.Regs[1].Reg))) { Line = ai.Line };
        }
        // The table as it is where no pass moved a target, so nothing that holds it sees another list.
        List<MBlock>? table = i.Table;
        if (i.Op == MOp.JmpTable && ai.Targets is { } targets && targets.Where((t, k) => !ReferenceEquals(blocks[t], i.Table![k])).Any())
            table = targets.Select(t => blocks[t]).ToList();
        MInstr n = new(i.Op) { Width = i.Width, Cond = i.Cond, Lock = i.Lock, Table = table, CallReloc = i.CallReloc, Line = i.Line, Native = i.Native, Probed = i.Probed, OwnsState = i.OwnsState };
        // The registers named, walked in the order Allocator.RegsOf named them.
        int k = 0;
        for (int o = 0; o < i.Operands.Count; o++)
        {
            switch (i.Operands[o])
            {
                case MReg r:
                    n.Operands.Add(PlaceOf(p.Of(index, k), r));
                    k++;
                    break;
                case MMem m:
                    // Everything about the operand but its registers survives.
                    // Losing Reloc here turned a `[got + sym@GOTOFF]` back into
                    // an absolute address, which a shared object can only
                    // honour with a text relocation -- and not needing one is
                    // the whole point of position-independent code.
                    // A leaf's frame base is named nowhere (LowerInstr): as it is.
                    MReg? baseReg = m.Base is null ? null : _leaf && m.Base.Id == (int)Gpr.Ebp ? m.Base : (MReg)PlaceOf(p.Of(index, k++), m.Base);
                    MReg? indexReg = m.Index is null ? null : (MReg)PlaceOf(p.Of(index, k++), m.Index);
                    n.Operands.Add(new MMem(baseReg, m.Disp)
                    {
                        Index = indexReg, Scale = m.Scale, Symbol = m.Symbol, Reloc = m.Reloc, Label = m.Label, IsSpill = m.IsSpill, Device = m.Device,
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
