#nullable enable
using Corsac.Lang.Frames;
using Corsac.Lang.Ir;
using Corsac.Lang.RegAlloc;

namespace Corsac.Lang.X86;

/// <summary>
/// i386 AS THE ONE ALLOCATOR SEES IT (Corsac.Lang.RegAlloc): one class of
/// six registers -- EAX, ECX, EDX first, free across no call, then the
/// callee-saved EBX, ESI, EDI -- of which EAX to EBX have byte halves; ESP
/// and EBP never touched. Each instruction lowered once into the registers
/// it names (Roles.RegsOf), which operands may be memory or an
/// immediate (Roles.Foldable, FoldableImm), its jumps; and the function
/// rebuilt from what was decided (FamilyTarget.Rebuild), the loads, stores
/// and constants around each instruction made here (Move, Remade).
/// </summary>
internal sealed class X86Target : FamilyTarget, ITarget
{
    /// <summary>
    /// THE XMM CLASS: scalar floats where --cpu has SSE and the code owns its
    /// state (Selector's _xmm32, _xmm64). Every one caller-saved, as i386's C
    /// has them; none ever a reference.
    /// </summary>
    private static readonly RegClass XmmClass = new()
    {
        Name = "xmm",
        Preference = Enumerable.Range(MReg.XmmBase, 8).ToArray(),
        CalleeSaved = Array.Empty<int>(),
        HoldsReferences = false,
    };

    private static readonly RegisterFile File = new()
    {
        PhysCount = MReg.XmmBase + 8,
        FirstVirtual = MReg.FirstVirtual,
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
            XmmClass,
        },
    };


    public X86Target(MFunction m) : base(m, Leaf(m))
    {
    }

    /// <summary>
    /// A LEAF'S REGISTERS: EBP besides, callee-saved (pushed first of those
    /// it saves, MFunction.FramePointerFree), in a function with no frame of
    /// its own (MFunction.Frameless), where nothing needs one -- no call, a store
    /// sequence's stub's included (a callee's prologue pushes EBP as the link
    /// to this frame; the collector and every unwinder walk that chain), no
    /// landing pad, nothing that names
    /// EBP but as the frame's base (Sys.FramePointer, a six-argument system
    /// call) and nothing that moves ESP after the prologue, off which the frame
    /// is addressed instead (FramePointerFree). A fault in such a function
    /// finds its caller by its first bytes (Runtime.FrameAt).
    /// </summary>
    private static readonly RegisterFile LeafFile = new()
    {
        PhysCount = MReg.XmmBase + 8,
        FirstVirtual = MReg.FirstVirtual,
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
            XmmClass,
        },
    };

    /// <summary>
    /// A THUNK'S REGISTERS (Function.KeepsCallerRegisters): EAX, ECX and EDX
    /// alone, so EBX, ESI and EDI keep what its caller left in them, and a
    /// value it holds across a call is held in its frame.
    /// </summary>
    private static readonly RegisterFile CallerOnlyFile = new()
    {
        PhysCount = MReg.XmmBase + 8,
        FirstVirtual = MReg.FirstVirtual,
        Untracked = new[] { (int)Gpr.Esp, (int)Gpr.Ebp },
        Classes = new[]
        {
            new RegClass
            {
                Name = "gpr",
                Preference = new[] { (int)Gpr.Eax, (int)Gpr.Ecx, (int)Gpr.Edx },
                CalleeSaved = Array.Empty<int>(),
                ByteCapable = new[] { (int)Gpr.Eax, (int)Gpr.Ecx, (int)Gpr.Edx },
            },
            XmmClass,
        },
    };

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
                // A caller's register is read off the frame pointer (Encoder's CallerReg).
                // An asm block that pushes, names ESP or EBP or a slot off EBP.
                if (i.Op is MOp.Call or MOp.CallInd or MOp.CallKeep or MOp.CallKeepInd or MOp.CallKeepEax or MOp.CallKeepEaxInd
                    or MOp.CallSafe or MOp.Push or MOp.Pop or MOp.Pushf or MOp.Popf or MOp.CallerReg) return false;
                if (i.Op == MOp.InlineAsm && i.Asm!.NeedsFrame) return false;
                foreach (MOperand o in i.Operands)
                    if (o is MMem { Index: { Id: (int)Gpr.Ebp } }) return false;
                foreach ((MReg r, Roles.Role role, int operand, bool inMem) in Roles.RegsOf(i, regs))
                {
                    if (r.Id == (int)Gpr.Ebp && !inMem) return false;
                    if (r.Id == (int)Gpr.Esp && (role & Roles.Role.Def) != 0) return false;
                }
            }
        }
        return true;
    }

    public override RegisterFile Registers => _registers ??= Halved(_m.Source.KeepsCallerRegisters ? CallerOnlyFile : _leaf ? LeafFile : File);

    private RegisterFile? _registers;

    /// <summary>
    /// The file with the XMM class XMM0 to XMM3 alone, where the function's
    /// packed operations hold XMM4 to XMM7 (MFunction.XmmClassCount).
    /// </summary>
    private RegisterFile Halved(RegisterFile f) => _m.XmmClassCount == 8 ? f : new RegisterFile
    {
        PhysCount = f.PhysCount, FirstVirtual = f.FirstVirtual, Untracked = f.Untracked,
        Classes = new[]
        {
            f.Classes[0],
            new RegClass
            {
                Name = XmmClass.Name, CalleeSaved = XmmClass.CalleeSaved, HoldsReferences = false,
                Preference = Enumerable.Range(MReg.XmmBase, _m.XmmClassCount).ToArray(),
            },
        },
    };
    public bool ByteOnly(int vreg) => _m.ByteRegs.Contains(vreg);
    /// <summary>A slot of a word, or one an XMM value's moves fill -- eight bytes, sixteen for a Packed128's (MFunction.XmmSpillWidth).</summary>
    public int NewSlot(int vreg) => _m.XmmRegs.Contains(vreg) ? _m.Frame.Allocate(Math.Max(8, _m.XmmSpillWidth), 8) : _m.Frame.Spill();
    public string RegName(int reg) => reg >= MReg.XmmBase ? $"xmm{reg - MReg.XmmBase}" : X86Mode.GprName(reg);
    public bool PlainConstant(object constant) => constant is MImm { IsPlain: true };

    /// <summary>
    /// An address an LEA makes from nothing the allocator places -- off EBP,
    /// which holds still through the function, or a symbol -- remade where it
    /// is wanted (AInstr.Constant); not off ESP, which every push moves. EBP
    /// is not the frame only in a six-argument system call's window (Select:
    /// `push a6; ...; push ebp; mov ebp, [esp+4]; int 0x80; pop ebp; lea esp,
    /// [esp+4]`, or with a constant `push ebp; mov ebp, imm`), where nothing
    /// is remade: the window names no value the allocator places, and the
    /// sixth argument's own remaking lands ahead of its push; and in a leaf giving EBP
    /// a value, where the frame address it makes becomes ESP's
    /// (FramePointerFree).
    /// </summary>
    private static MMem? Address(MInstr i) => i.Op == MOp.Lea && i.Width == 4 && i.Operands.Count == 2 && i.Operands[0] is MReg { IsPhys: false }
        && i.Operands[1] is MMem { Index: null } m && (m.Base is null ? m.Symbol is not null && m.Reloc == RelocKind.Abs32 : m.Base.Id == (int)Gpr.Ebp && m.Symbol is null)
        && m.Label is null && !m.IsSpill ? m : null;

    /// <summary>
    /// A FLOAT CONSTANT LOADED FROM THE POOL (X86Backend.FloatPoolName), remade
    /// where it is wanted -- one load from read-only memory, as a spill's
    /// reload is, with no store before it. Not in position-independent code,
    /// whose load is off the GOT's register (Pic) and no constant's.
    /// </summary>
    internal sealed record FloatPoolLoad(MMem Pool, int Width);

    private static FloatPoolLoad? PoolLoad(MInstr i)
        => i.Op == MOp.MovF && i.Operands.Count == 2 && i.Operands[0] is MReg { IsPhys: false, IsXmm: false }
           && i.Operands[1] is MMem { Base: null, Index: null, Symbol: { } s } m && s.StartsWith(X86Backend.FloatPoolPrefix, StringComparison.Ordinal)
           ? new FloatPoolLoad(m, i.Width) : null;

    public int NewVRegLike(int like)
    {
        int v = _m.NewReg().Id;
        if (_m.XmmRegs.Contains(like)) _m.XmmRegs.Add(v);
        if (_m.ByteRegs.Contains(like)) _m.ByteRegs.Add(v);
        if (_m.WideHalves.Contains(like)) _m.WideHalves.Add(v);
        return v;
    }

    public string Text(AInstr i) => i.Tag is MInstr m ? AsmText.Line(_m, m)
        : i.Made == Made.Copy ? $"copy v{i.CopyDst}, v{i.CopySrc}" : $"jmp {i.Targets![0].Name}";

    /// <summary>
    /// A CALL CLOBBERS EVERY XMM REGISTER, all caller-saved in i386's C: said
    /// only in a function that has the XMM class, every other one's calls
    /// left as they were. AN ASM BLOCK CLOBBERS THE XMM REGISTERS IT WRITES,
    /// by number.
    /// </summary>
    protected override void AlsoWritten(MInstr i, List<Named> named)
    {
        if (_m.XmmRegs.Count == 0) return;
        bool call = i.Op is MOp.Call or MOp.CallInd or MOp.CallKeep or MOp.CallKeepInd
            or MOp.CallKeepEax or MOp.CallKeepEaxInd or MOp.CallSafe;
        uint xmmWritten = call ? 0xFFu : i.Op == MOp.InlineAsm ? i.Asm!.XmmClobbers & 0xFF : 0;
        for (int x = 0; x < 8; x++)
            if ((xmmWritten & (1u << x)) != 0) named.Add(new Named(MReg.XmmBase + x, Role.Def, -1, false));
    }

    /// <summary>A slot holds what a register does, at any width: a narrow write leaves the rest of either as it was.</summary>
    protected override bool MayWriteMemory(MInstr i, int operand) => true;

    /// <summary>A whole word's constant, a float pool's word, or an address made from nothing placed (Address).</summary>
    protected override object? ConstantOf(MInstr i)
        => i.Op == MOp.Mov && i.Width == 4 && i.Operands.Count == 2 && i.Operands[0] is MReg && i.Operands[1] is MImm imm ? imm
            : PoolLoad(i) is { } pool ? pool : Address(i);

    protected override int? HomeOf(MInstr i) => _homes.Contains(i) ? ((MMem)i.Operands[1]).Disp : null;

    private HashSet<MInstr> _homes = new(ReferenceEqualityComparer.Instance);

    protected override void BeforeLowering() => _homes = Homes();

    /// <summary>A float home's width, by its offset: reloaded as it was loaded, never past its argument's bytes.</summary>
    private readonly Dictionary<int, int> _homeWidths = new();

    /// <summary>
    /// THE LOADS THAT ARE HOMES (AInstr.HomeSlot): `mov v, [ebp+d]` of an
    /// incoming argument's word, where it is the only thing in the function
    /// that touches any byte of that word, and nothing takes an address at
    /// or above EBP -- through one, any argument could be read or written:
    /// no LEA there, no indexed access there (a local's address, below, is
    /// a local's). EBP as a value (Sys.FramePointer) is
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
            {
                // An asm block that names the stack in its own text may read
                // or write an argument's word with no operand to show it.
                if (i.Op == MOp.InlineAsm && i.Asm!.Analysis.UsesStack) return homes;
                for (int k = 0; k < i.Operands.Count; k++)
                {
                    if (i.Operands[k] is not MMem mem) continue;
                    if (mem.Index?.Id == (int)Gpr.Ebp) return homes;
                    if (mem.Base?.Id != (int)Gpr.Ebp) continue;
                    // An address taken AT OR ABOVE the frame pointer, or an
                    // index from there: any argument may be reached through
                    // it. One taken below is a local's -- a struct's value in
                    // its frame slot, an out argument's word, a buffer --
                    // and what is done through it stays in the local, as an
                    // indexed access below does: taken for a reach of every
                    // argument (as any LEA off EBP was), every function that
                    // kept a struct in its frame loaded each argument it
                    // could not keep in a register and stored it again in a
                    // slot of its own -- thirteen loads and twelve stores at
                    // the top of Tcp.ReceiveOwned, once a segment.
                    if ((i.Op == MOp.Lea || mem.Index is not null) && mem.Disp >= 0) return homes;
                    if (i.Op == MOp.Lea) continue;
                    // The saved frame pointer and the return address, between the locals
                    // and the arguments: an access there, sixteen bytes at most, may reach
                    // one. A local below the frame pointer reaches none without writing
                    // over the return address first.
                    if (mem.Disp < 8 || mem.Symbol is not null || mem.Label is not null)
                    {
                        if (mem.Disp >= 0) touched.Add((mem.Disp, mem.Disp + 16));
                        continue;
                    }
                    // A word's MOV, or a float's MOVSS or a double's MOVSD into the XMM class.
                    if ((i.Op == MOp.Mov && i.Width == 4 || i.Op == MOp.MovF) && k == 1 && !i.Lock
                        && i.Operands[0] is MReg { IsPhys: false, IsXmm: false } && (mem.Disp & 3) == 0)
                    {
                        // A second load of the word makes it no home.
                        load[mem.Disp] = load.ContainsKey(mem.Disp) ? null : i;
                    }
                    else touched.Add((mem.Disp, mem.Disp + 16));
                }
            }
        foreach ((int disp, MInstr? i) in load)
            if (i is not null && !touched.Any(t => t.From < disp + i.Width && disp < t.To))
            {
                homes.Add(i);
                if (i.Op == MOp.MovF) _homeWidths[disp] = i.Width;
            }
        return homes;
    }

    protected override MInstr Remade(int reg, int slot, object? constant, int line)
        => constant is MMem address
            ? new MInstr(MOp.Lea, Out(reg), new MMem(address.Base, address.Disp) { Symbol = address.Symbol, Reloc = address.Reloc, Device = address.Device }) { Line = line }
            : constant is MImm imm ? new MInstr(MOp.Mov, Out(reg), imm) { Line = line }
            : constant is FloatPoolLoad pool ? new MInstr(MOp.MovF, Out(reg), pool.Pool) { Width = pool.Width, Line = line }
            : Move(Out(reg), Slot(slot), line);

    /// <summary>
    /// The callee-saved registers the function touches, or all of them where
    /// it catches (a landing pad is entered with them as the thrower left
    /// them); a leaf has no frame, and one that gave EBP a value saves it
    /// first, as the prologue's push would have.
    /// </summary>
    protected override void Saved(AFunction f, IPlacement p)
    {
        bool catches = f.Blocks.Any(b => b.LandingPad);
        foreach (int g in File.Classes[0].CalleeSaved)
            if (catches || p.Touched.Contains(g)) _m.SavedRegs.Add((Gpr)g);
        _m.Frameless = _leaf;
        _m.FramePointerFree = _leaf && p.Touched.Contains((int)Gpr.Ebp);
        if (_m.FramePointerFree) _m.SavedRegs.Insert(0, Gpr.Ebp);
    }

    protected override bool Fixed(MReg original) => original.IsPhys;

    /// <summary>An XMM value by MOVSS/MOVSD at the width its home or its class's spill slot has; a general one by MOV.</summary>
    protected override MInstr Move(MOperand to, MOperand from, int line)
        => to is MReg { IsXmm: true } || from is MReg { IsXmm: true }
            ? new MInstr(MOp.MovF, to, from)
            {
                Width = from is MMem { Disp: > 0 } home && _homeWidths.TryGetValue(home.Disp, out int width) ? width : _m.XmmSpillWidth,
                Line = line,
            }
            : new MInstr(MOp.Mov, to, from) { Line = line };

    /// <summary>A register the allocator placed a value in: EBP as the value it holds (MReg.EbpData), no frame's base.</summary>
    protected override MReg Out(int reg) => reg == (int)Gpr.Ebp ? MReg.EbpData : MReg.Of(reg);
}
