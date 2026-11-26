#nullable enable
using Corsac.Lang.Frames;
using Corsac.Lang.Ir;
using Corsac.Lang.RegAlloc;

namespace Corsac.Lang.X86;

/// <summary>
/// WHAT BOTH MODES' ALLOCATOR TARGETS DO ALIKE (X86Target, LongTarget): the
/// function rebuilt from what the allocator decided. Each instruction is made
/// again whole (MInstr.Rebuilt) with every register it names in its place --
/// a register, its spill slot as a memory operand, its constant as an
/// immediate -- its memory operands whole too (MMem.WithRegisters); a
/// spilled value's loads go before it and its stores after; the copies and
/// jumps the allocator made are made; a call's live values go into its stack
/// map; and the moves on an edge go into a block of the edge's own. And the
/// function lowered for the allocator in the first place (Lower,
/// LowerInstr), its argument homes, its leaf test and its register files'
/// making. What a mode does its own way it is asked: its registers, which
/// constant a move carries (ConstantMoved), where a slot may be written
/// (MayWriteMemory), registers written that no table names (AlsoWritten),
/// and i386's fewer XMM registers under packed operations (Fitted).
/// </summary>
internal abstract class FamilyTarget
{
    protected readonly MFunction _m;
    /// <summary>A leaf whose frame base is a register like any other (each mode's Leaf says when).</summary>
    protected readonly bool _leaf;

    protected FamilyTarget(MFunction m)
    {
        _m = m;
        _leaf = Leaf(m);
    }

    /// <summary>
    /// WHETHER THE FUNCTION MAY RUN WITHOUT A FRAME POINTER, its base
    /// register one more for the allocator (Files' leaf file): where
    /// nothing needs the frame -- no call, a store sequence's stub's included
    /// (a callee's prologue pushes the base register as the link to this
    /// frame; the collector and every unwinder walk that chain), no landing
    /// pad, nothing that names the base register but as the frame's base
    /// (Sys.FramePointer, i386's six-argument system call), nothing that
    /// reads a caller's register off it (i386's CallerReg), no asm block that needs
    /// the frame, and nothing that moves the stack pointer after the
    /// prologue, off which the frame is addressed instead
    /// (FramePointerFree). Long mode keeps the frame too where the function
    /// saves every register (a system call, a software interrupt) or
    /// realigns its stack.
    /// </summary>
    private static bool Leaf(MFunction m)
    {
        if (X86Mode.Long && (m.SavesEverything || m.RealignsStack)) return false;
        List<(MReg Reg, Roles.Role Role, int Operand, bool InMem)> regs = new();
        foreach (MBlock b in m.Blocks)
        {
            if (b.Source?.IsLandingPad == true) return false;
            foreach (MInstr i in b.Instrs)
            {
                if (i.Op is MOp.Call or MOp.CallInd or MOp.CallSafe or MOp.CallKeep or MOp.CallKeepInd or MOp.CallKeepEax or MOp.CallKeepEaxInd
                    or MOp.CallKeepRax or MOp.CallKeepCas or MOp.Push or MOp.Pop or MOp.Pushf or MOp.Popf) return false;
                // i386 reads a caller's register off the frame pointer
                // (Encoder's CallerReg); long mode from its save slot or the
                // register itself, with or without one.
                if (i.Op == MOp.CallerReg && !X86Mode.Long) return false;
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

    /// <summary>
    /// A MODE'S THREE REGISTER FILES, from its registers by what a call does
    /// to them (caller-saved first: a value that crosses no call costs
    /// nothing there, a callee-saved register a push and a pop), those of
    /// them with byte forms (null: all) and how many XMM registers it has --
    /// every one caller-saved, none ever a reference:
    ///
    ///   Whole       the stack pointer and the frame's base never touched
    ///   Leaf        the base register besides, callee-saved (Leaf)
    ///   CallerOnly  a thunk's (Function.KeepsCallerRegisters): the
    ///               caller-saved alone, so the callee-saved keep what its
    ///               caller left in them, and a value it holds across a
    ///               call is held in its frame
    /// </summary>
    protected static (RegisterFile Whole, RegisterFile Leaf, RegisterFile CallerOnly) Files(int[] callerSaved, int[] calleeSaved, int[]? byteCapable, int xmm)
    {
        RegClass xmmClass = new()
        {
            Name = "xmm",
            Preference = Enumerable.Range(MReg.XmmBase, xmm).ToArray(),
            CalleeSaved = Array.Empty<int>(),
            HoldsReferences = false,
        };
        RegisterFile File(int[] preference, int[] saved, int[] untracked) => new()
        {
            PhysCount = MReg.XmmBase + xmm,
            FirstVirtual = MReg.FirstVirtual,
            Untracked = untracked,
            Classes = new[]
            {
                new RegClass { Name = "gpr", Preference = preference, CalleeSaved = saved, ByteCapable = byteCapable?.Intersect(preference).ToArray() },
                xmmClass,
            },
        };
        int sp = (int)Gpr.Esp, bp = (int)Gpr.Ebp;
        return (File(callerSaved.Concat(calleeSaved).ToArray(), calleeSaved, new[] { sp, bp }),
            File(callerSaved.Concat(calleeSaved).Append(bp).ToArray(), calleeSaved.Append(bp).ToArray(), new[] { sp }),
            File(callerSaved, Array.Empty<int>(), new[] { sp, bp }));
    }

    /// <summary>A value limited to the registers with byte forms: i386's alone -- every general register has one under REX.</summary>
    public bool ByteOnly(int vreg) => !X86Mode.Long && _m.ByteRegs.Contains(vreg);

    /// <summary>
    /// A slot of a word; on i386 an XMM value's is the one its moves fill --
    /// eight bytes, sixteen for a Packed128's (MFunction.XmmSpillWidth).
    /// Long mode's XMM values are scalars, a word each, but in a function
    /// that keeps packed values in the class: sixteen bytes there, at
    /// sixteen from the frame's base, which a framed function's prologue
    /// leaves at sixteen (FamilyPasses.AlignedSpills marks them for MOVAPS).
    /// </summary>
    public int NewSlot(int vreg)
        => !_m.XmmRegs.Contains(vreg) ? _m.Frame.Spill()
            : X86Mode.Long ? (_m.XmmSpillWidth == 16 ? _m.Frame.Allocate(16, 16) : _m.Frame.Spill())
            : _m.Frame.Allocate(Math.Max(8, _m.XmmSpillWidth), 8);

    /// <summary>A new virtual register of the kind `like` is: its class, its byte limit, its being half of a wide value.</summary>
    public int NewVRegLike(int like)
    {
        int v = _m.NewReg().Id;
        if (_m.XmmRegs.Contains(like)) _m.XmmRegs.Add(v);
        if (_m.ByteRegs.Contains(like)) _m.ByteRegs.Add(v);
        if (_m.WideHalves.Contains(like)) _m.WideHalves.Add(v);
        return v;
    }

    public string RegName(int reg) => MReg.Of(reg).ToString();

    /// <summary>A constant any operand carries: i386's any plain one, long mode's one that fits 32 bits signed (an instruction's immediate, sign-extended).</summary>
    public bool PlainConstant(object constant) => constant is MImm imm && (X86Mode.Long ? imm.FitsInt32 : imm.IsPlain);

    public string Text(AInstr i) => i.Tag is MInstr m ? InstrText(m)
        : i.Made == Made.Copy ? $"copy v{i.CopyDst}, v{i.CopySrc}" : $"jmp {i.Targets![0].Name}";

    /// <summary>An instruction as the allocator's dumps show it.</summary>
    protected abstract string InstrText(MInstr i);

    /// <summary>
    /// A MOVE OF A VALUE BY ITS CLASS'S MOVE, between a register and a
    /// register, a slot or a constant: the class the register operand is of
    /// (a constant is a general register's). A general one by MOV of a
    /// word; an XMM one by MOVSS or MOVSD at the width its home has
    /// (Homes), else its class's spill slot's -- i386's by what the function
    /// keeps there (MFunction.XmmSpillWidth), long mode's a scalar's eight
    /// bytes or, where the function keeps packed values, the whole sixteen. The copy the allocator made of one virtual register into
    /// another, each where it was placed, is this move too.
    /// </summary>
    private MInstr Move(MOperand to, MOperand from, int line)
        => to is MReg r && ClassOf(r.Id) == 1 || from is MReg s && ClassOf(s.Id) == 1
            ? new MInstr(MOp.MovF, to, from)
            {
                Width = from is MMem { Disp: > 0 } home && _homeWidths.TryGetValue(home.Disp, out int width) ? width : X86Mode.Long ? Math.Max(8, _m.XmmSpillWidth) : _m.XmmSpillWidth,
                Line = line,
            }
            : new MInstr(MOp.Mov, to, from) { Width = X86Mode.Word, Line = line };

    /// <summary>
    /// A value made again in a register before an instruction: a constant
    /// moved whole, an address by LEA, a float constant by its load from the
    /// pool, any other value loaded from its slot.
    /// </summary>
    private MInstr Remade(int reg, int slot, object? constant, int line)
        => constant is MMem address
            ? new MInstr(MOp.Lea, Out(reg), new MMem(address.Base, address.Disp) { Symbol = address.Symbol, Reloc = address.Reloc, Device = address.Device })
                { Width = X86Mode.Word, Line = line }
            : constant is MImm imm ? new MInstr(MOp.Mov, Out(reg), imm) { Width = X86Mode.Word, Line = line }
            : constant is FloatPoolLoad pool ? new MInstr(MOp.MovF, Out(reg), pool.Pool) { Width = pool.Width, Line = line }
            : Move(Out(reg), Slot(slot), line);

    /// <summary>A register an instruction names that is the machine's own, and so no allocation's to place.</summary>
    private static bool Fixed(MReg original) => original.IsMachine;

    /// <summary>The register a value was placed in, as an operand: the frame's base register as the value it holds there (MReg.EbpData), no frame's base.</summary>
    protected static MReg Out(int reg) => reg == (int)Gpr.Ebp ? MReg.EbpData : MReg.Of(reg);

    /// <summary>
    /// WHAT THE FUNCTION SAVES AND HOW ITS FRAME STANDS, from the registers
    /// the allocation touched, in both modes: the callee-saved registers it
    /// touches, or all of them where it must put every one back -- one that
    /// catches (a landing pad is entered with them as the thrower left
    /// them), and in long mode one that enters the collector through a
    /// system call (MFunction.SavesEverything, which says both). A leaf has
    /// no frame (MFunction.Frameless), and one that gave the base register a
    /// value saves it first, as the prologue's push would have
    /// (MFunction.FramePointerFree): the caller's base register just below
    /// the return address, a frame link a fault's walk reads
    /// (Runtime.FrameAt).
    /// </summary>
    private void Saved(AFunction f, IPlacement p)
    {
        bool all = X86Mode.Long ? _m.SavesEverything : f.Blocks.Any(b => b.LandingPad);
        foreach (int g in ModeFiles.Whole.Classes[0].CalleeSaved)
            if (all || p.Touched.Contains(g)) _m.SavedRegs.Add((Gpr)g);
        _m.Frameless = _leaf;
        _m.FramePointerFree = _leaf && p.Touched.Contains((int)Gpr.Ebp);
        if (_m.FramePointerFree) _m.SavedRegs.Insert(0, Gpr.Ebp);
    }

    /// <summary>The mode's three register files (Files).</summary>
    protected abstract (RegisterFile Whole, RegisterFile Leaf, RegisterFile CallerOnly) ModeFiles { get; }

    /// <summary>A file as this function has it: i386's with fewer XMM registers where its packed operations hold the rest.</summary>
    protected virtual RegisterFile Fitted(RegisterFile file) => file;

    /// <summary>The mode's registers as the allocator has them, for this function: a thunk's, a leaf's, or the whole file.</summary>
    // (A long mode leaf that keeps packed values leaves the base register
    // alone: a spill of one asks a slot at sixteen, for which the leaf has
    // its frame again -- FramePointerFree.Run -- and the base must be free
    // to be it.)
    public RegisterFile Registers => _registers ??= Fitted(_m.Source.KeepsCallerRegisters ? ModeFiles.CallerOnly
        : _leaf && !(X86Mode.Long && _m.XmmSpillWidth == 16) ? ModeFiles.Leaf : ModeFiles.Whole);

    private RegisterFile? _registers;

    /// <summary>
    /// The class of a register: the general ones 0, the XMM ones 1 -- a
    /// machine register by its number, a virtual one by the function's
    /// record. The base register holding a value (MReg.EbpData, the first
    /// virtual number and no virtual register's) is a general one.
    /// </summary>
    public int ClassOf(int reg) => reg < MReg.FirstVirtual ? (reg >= MReg.XmmBase ? 1 : 0)
        : reg != MReg.EbpDataId && _m.XmmRegs.Contains(reg) ? 1 : 0;

    public bool Immediate(object constant) => constant is MImm;

    /// <summary>
    /// THE LOADS THAT ARE HOMES (AInstr.HomeSlot), found over the whole
    /// function before its instructions are lowered: the load of a
    /// stack-passed argument's word off the frame's base -- a word's MOV, or
    /// a float's MOVSS or a double's MOVSD into the XMM class -- where it is
    /// the only thing in the function that touches any byte of that word,
    /// and nothing takes an address at or above the base register: through
    /// one, any argument could be read or written. No LEA there, no indexed
    /// access there (a local's address, below, is a local's). The base
    /// register as a value (Sys.FramePointer) is read through, never
    /// written above the return address -- its contract (Prelude), held by
    /// every use in the runtime and the kernel, which walk the chain of
    /// saved frame pointers and return addresses -- and a home is never
    /// written by the allocator either (Allocator.HomeOf). Any other access
    /// at or above the frame pointer (a local below it reaches no argument)
    /// is taken as sixteen bytes wide, whatever its Width says: a packed
    /// store writes eight with the default four (Peephole.ParameterHomes
    /// counts as much). i386's frame outside reads that word as one it is
    /// pushing, so no stack map need; long mode's collector reads a stack
    /// as memory, with no map to name it in.
    /// </summary>
    private HashSet<MInstr> Homes()
    {
        HashSet<MInstr> homes = new(ReferenceEqualityComparer.Instance);
        Dictionary<int, MInstr?> load = new();
        List<(int From, int To)> touched = new();
        int word = X86Mode.Word;
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
                    // argument (as any LEA off the base was), every function
                    // that kept a struct in its frame loaded each argument it
                    // could not keep in a register and stored it again in a
                    // slot of its own -- thirteen loads and twelve stores at
                    // the top of Tcp.ReceiveOwned, once a segment.
                    if ((i.Op == MOp.Lea || mem.Index is not null) && mem.Disp >= 0) return homes;
                    if (i.Op == MOp.Lea) continue;
                    // The saved frame pointer and the return address, two
                    // words between the locals and the arguments: an access
                    // there, sixteen bytes at most, may reach one. A local
                    // below the frame pointer reaches none without writing
                    // over the return address first.
                    if (mem.Disp < 2 * word || mem.Symbol is not null || mem.Label is not null)
                    {
                        if (mem.Disp >= 0) touched.Add((mem.Disp, mem.Disp + 16));
                        continue;
                    }
                    if ((i.Op == MOp.Mov && i.Width == word || i.Op == MOp.MovF) && k == 1 && !i.Lock
                        && i.Operands[0] is MReg { IsMachine: false } && (mem.Disp & (word - 1)) == 0)
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

    private HashSet<MInstr> _homes = new(ReferenceEqualityComparer.Instance);
    // The width an XMM value's home is loaded at, by the home's offset (Move).
    private readonly Dictionary<int, int> _homeWidths = new();

    /// <summary>Registers an instruction writes that neither its operands nor the family's table of unnamed ones say (i386's XMM registers at a call).</summary>
    protected virtual void AlsoWritten(MInstr i, List<Named> named) { }

    /// <summary>Whether an operand that may be memory may be WRITTEN there: a mode whose register write does more than the slot's would says no.</summary>
    protected abstract bool MayWriteMemory(MInstr i, int operand);

    /// <summary>
    /// The value an instruction makes from nothing the allocator places,
    /// which may be made again where wanted (Remade) rather than kept: a
    /// constant (the mode's ConstantMoved), a float pool's word, an address.
    /// </summary>
    private object? ConstantOf(MInstr i) => ConstantMoved(i) is { } imm ? imm : PoolLoad(i) is { } pool ? pool : Address(i);

    /// <summary>The constant a move puts in a general register, as it is to be moved again into a whole register; null for any other instruction.</summary>
    protected abstract MImm? ConstantMoved(MInstr i);

    /// <summary>
    /// An address an LEA makes from nothing the allocator places -- off the
    /// frame's base register, which holds still through the function, or a
    /// symbol (i386's absolute, long mode's RIP-relative) -- remade where it
    /// is wanted (AInstr.Constant); not off the stack pointer, which every
    /// push moves, nor through the GOT or a segment. On i386 EBP is not the
    /// frame only in a six-argument system call's window (Select: `push a6;
    /// ...; push ebp; mov ebp, [esp+4]; int 0x80; pop ebp; lea esp,
    /// [esp+4]`, or with a constant `push ebp; mov ebp, imm`), where nothing
    /// is remade: the window names no value the allocator places, and the
    /// sixth argument's own remaking lands ahead of its push; and in a leaf
    /// giving the base register a value, where the frame address it makes
    /// becomes the stack pointer's (FramePointerFree).
    /// </summary>
    private static MMem? Address(MInstr i)
        => i.Op == MOp.Lea && i.Width == X86Mode.Word && i.Operands.Count == 2 && i.Operands[0] is MReg { IsMachine: false, IsFloat: false }
           && i.Operands[1] is MMem { Index: null, Label: null, IsSpill: false, Got: false, Segment: 0 } m
           && (m.Base is null ? m.Symbol is not null && (X86Mode.Long || m.Reloc == RelocKind.Abs32) : m.Base.Id == (int)Gpr.Ebp && m.Symbol is null)
            ? m : null;

    /// <summary>
    /// A FLOAT CONSTANT LOADED FROM THE POOL (X86Backend.FloatPoolName), remade
    /// where it is wanted -- one load from read-only memory, as a spill's
    /// reload is, with no store before it. Not in i386's position-independent
    /// code, whose load is off the GOT's register (Pic) and no constant's;
    /// long mode's is RIP-relative and always one.
    /// </summary>
    internal sealed record FloatPoolLoad(MMem Pool, int Width);

    private static FloatPoolLoad? PoolLoad(MInstr i)
        => i.Op == MOp.MovF && i.Operands.Count == 2 && i.Operands[0] is MReg { IsMachine: false }
           && i.Operands[1] is MMem { Base: null, Index: null, Symbol: { } s } m && s.StartsWith(X86Backend.FloatPoolPrefix, StringComparison.Ordinal)
            ? new FloatPoolLoad(m, i.Width) : null;

    // One list each for the whole function's lowering.
    private readonly List<(MReg Reg, Roles.Role Role, int Operand, bool InMem)> _regs = new();
    private readonly List<Named> _named = new();

    /// <summary>
    /// ONE INSTRUCTION AS THE ALLOCATOR SEES IT, the same reading in both
    /// modes: the registers it names (Roles.RegsOf), which operands may be
    /// memory (Roles.Foldable) or an immediate (FoldableImm: any constant
    /// where the operation is thirty-two bits wide, else one that fits
    /// thirty-two bits signed), whether it is a copy, its jumps, its
    /// effects (Effects).
    /// </summary>
    private AInstr LowerInstr(MInstr i, Dictionary<MBlock, ABlock> blocks)
    {
        _named.Clear();
        foreach ((MReg r, Roles.Role role, int operand, bool inMem) in Roles.RegsOf(i, _regs))
        {
            // In a leaf the frame's base is no use of the register the base
            // register holds: FramePointerFree makes it the stack pointer
            // (Leaf: the base register is named nowhere else).
            if (_leaf && r.Id == (int)Gpr.Ebp) continue;
            _named.Add(new Named(r.Id, (Role)(byte)role, operand, inMem));
        }
        AlsoWritten(i, _named);
        uint mayMemory = 0, mayMemoryWrite = 0, isMemory = 0, mayImmediate = 0;
        for (int k = 0; k < i.Operands.Count && k < 32; k++)
        {
            if (Roles.Foldable(_m, i, k))
            {
                mayMemory |= 1u << k;
                if (MayWriteMemory(i, k)) mayMemoryWrite |= 1u << k;
            }
            if (Roles.FoldableImm(i.Op, k)) mayImmediate |= 1u << k;
            if (i.Operands[k] is MMem) isMemory |= 1u << k;
        }
        bool copy = i.Op is MOp.Mov or MOp.MovF && i.Operands.Count == 2 && i.Operands[0] is MReg && i.Operands[1] is MReg;
        List<ABlock>? targets = null;
        if (i.Op is MOp.Jmp or MOp.Jcc && i.Operands[0] is MLabel label) targets = new() { blocks[label.Target] };
        else if (i.Op == MOp.JmpTable) targets = i.Table!.Select(t => blocks[t]).ToList();
        // A copy of the whole register: the mode's word, or an XMM move
        // (MOVAPS between two XMM registers is the whole register, whatever
        // it holds).
        bool whole = i.Op == MOp.MovF || i.Width == X86Mode.Word;
        return new AInstr
        {
            Tag = i,
            Regs = _named.ToArray(),
            IsCall = i.Op is MOp.Call or MOp.CallInd,
            IsSafepoint = i.Op == MOp.CallSafe,
            IsCopy = copy,
            WholeCopy = copy && whole && !i.Lock,
            // A register moved onto itself is nothing -- but long mode's
            // 32-bit move, which clears the upper half: that is its meaning
            // when written so.
            DropsSelf = copy && (whole || !X86Mode.Long || i.Width != 4),
            Constant = ConstantOf(i),
            OperandCount = i.Operands.Count,
            MayMemory = mayMemory,
            MayMemoryWrite = mayMemoryWrite,
            IsMemory = isMemory,
            MayImmediate = mayImmediate,
            MayImmediateWide = i.Width == 4 ? mayImmediate : 0,
            Targets = targets,
            Unconditional = i.EndsFlow,
            JumpsAnywhere = i.Op == MOp.JmpInd,
            HomeSlot = _homes.Contains(i) ? ((MMem)i.Operands[1]).Disp : null,
            Line = i.Line,
            Effects = Effects(i),
        };
    }

    /// <summary>
    /// What an instruction does besides its registers, for the scheduler
    /// (Effect): the flags and memory of the plain integer and SSE instructions
    /// it may move (scalar, packed, and the moves between the two files); anything else -- a call, a branch, a push, a
    /// divide (it may fault on its own), x87 and MMX state, a fence, a locked,
    /// device or segment-relative access -- a barrier. INC, DEC, a rotate
    /// (SF, ZF and PF kept), a shift by CL or by a constant the processor
    /// masks to 0 (the flags all kept) and ADC/SBB read the flags they leave
    /// partly or wholly as they were. One table for both modes: an opcode
    /// only one of them makes is in it for that one.
    /// </summary>
    protected static Effect Effects(MInstr i)
    {
        if (i.Lock) return Effect.Barrier;
        // (The count the processor masks: to six bits for a 64-bit shift, five for any other.)
        bool countImm = i.Operands.Count >= 2 && i.Operands[^1] is MImm { Value: var c } && (c & (i.Width == 8 ? 63 : 31)) != 0;
        Effect e = i.Op switch
        {
            MOp.Mov or MOp.Movzx or MOp.Movsx or MOp.Lea or MOp.Not or MOp.Bswap or MOp.Cdq or MOp.Cwd => Effect.None,
            MOp.Add or MOp.Sub or MOp.And or MOp.Or or MOp.Xor or MOp.Cmp or MOp.Test or MOp.Neg
                or MOp.Imul or MOp.Imul3 or MOp.Mul or MOp.MulWide or MOp.ImulWide => Effect.WritesFlags,
            MOp.Adc or MOp.Sbb or MOp.Inc or MOp.Dec => Effect.ReadsFlags | Effect.WritesFlags,
            MOp.Shl or MOp.Shr or MOp.Sar or MOp.Shld or MOp.Shrd
                => countImm ? Effect.WritesFlags : Effect.ReadsFlags | Effect.WritesFlags,
            MOp.Rol or MOp.Ror => Effect.ReadsFlags | Effect.WritesFlags,
            MOp.Setcc or MOp.Cmov => Effect.ReadsFlags,
            MOp.MovF or MOp.MovGx or MOp.AddF or MOp.SubF or MOp.MulF or MOp.DivF or MOp.SqrtF or MOp.XorF or MOp.XorPs
                or MOp.CvtFToF or MOp.CvtIntToF or MOp.CvtFToInt or MOp.Cvttss2si or MOp.Cvttsd2si
                or MOp.Punpckldq or MOp.MovdToX or MOp.MovdFromX
                or MOp.XmmOpV or MOp.XmmOpVDef or MOp.XmmShiftV or MOp.XmmExtractV or MOp.XmmInsertV or MOp.XmmMaskV => Effect.None,
            MOp.UcomiF => Effect.WritesFlags,
            _ => Effect.Barrier,
        };
        if (e == Effect.Barrier) return e;
        for (int k = 0; k < i.Operands.Count; k++)
        {
            if (i.Operands[k] is not MMem m || i.Op == MOp.Lea) continue;
            // Device memory is read and written when and as often as the source
            // says, in its order; a segment-relative word is another thread's
            // or processor's at any instruction.
            if (m.Segment != 0 || m.Device) return Effect.Barrier;
            Roles.Role role = Roles.Of(i.Op, k);
            if ((role & Roles.Role.Use) != 0) e |= Effect.ReadsMemory;
            if ((role & Roles.Role.Def) != 0) e |= Effect.WritesMemory;
        }
        return e;
    }

    /// <summary>
    /// THE FUNCTION AS THE ALLOCATOR SEES IT: a block for a block, each
    /// instruction lowered once, and the blocks whose address something
    /// holds (a label as an immediate or in a memory operand) marked.
    /// </summary>
    public AFunction Lower()
    {
        AFunction f = new() { Name = _m.Source.Name, Registers = Registers, Target = (ITarget)this, NextVReg = _m.NextVReg };
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
                // WHAT IS LIVE ACROSS A CALL, for its stack map: the spill
                // slots and the callee-saved registers holding values the
                // callee must not lose sight of.
                if (n is not null && p.LiveAcross(index) is { } live)
                {
                    Safepoint map = new();
                    map.SlotOffsets.AddRange(live.Slots);
                    foreach (int r in live.Registers) map.Registers |= 1u << r;
                    _m.Safepoints[n] = map;
                }
                foreach ((int reg, int slot, bool load) in p.MovesBefore(index))
                    outList.Add(load ? Move(Out(reg), Slot(slot), ai.Line) : Move(Slot(slot), Out(reg), ai.Line));
                foreach ((int reg, int slot, object? constant) in p.Before(index))
                    outList.Add(Remade(reg, slot, constant, ai.Line));
                if (n is not null) outList.Add(n);
                foreach ((int reg, int slot) in p.After(index))
                    outList.Add(Move(Slot(slot), Out(reg), ai.Line));
                index++;
            }
            b.Instrs.Clear();
            b.Instrs.AddRange(outList);
            laid.Add(b);
        }
        // THE MOVES ON AN EDGE, in a block of the edge's own: after its
        // source where the source falls into its target, else at the end
        // with a jump on. The block says which block it is the edge of
        // (EdgeFrom): a loop's edge is the loop's where its source is.
        if (p.StoredEdges.Count > 0)
        {
            Dictionary<int, List<MBlock>> fallingInto = new();
            List<MBlock> jumpingTo = new();
            foreach ((int from, int to) in p.StoredEdges)
            {
                MBlock source = blocks[f.Blocks[from]], target = blocks[f.Blocks[to]];
                MBlock edge = new(source.Name + "_to_" + target.Name) { EdgeFrom = source };
                int line = source.Instrs.Count > 0 ? source.Instrs[^1].Line : 0;
                foreach ((int reg, int slot, bool load) in p.EdgeMoves(from, to))
                    edge.Instrs.Add(load ? Move(Out(reg), Slot(slot), line) : Move(Slot(slot), Out(reg), line));
                foreach (MInstr branch in source.Instrs)
                    for (int k = 0; k < branch.Operands.Count; k++)
                        if (branch.Operands[k] is MLabel label && ReferenceEquals(label.Target, target)) branch.Operands[k] = new MLabel(edge);
                if (to == from + 1 && !source.EndsUnconditionally)
                {
                    if (!fallingInto.TryGetValue(from, out List<MBlock>? after)) fallingInto[from] = after = new();
                    after.Add(edge);
                }
                else
                {
                    edge.Instrs.Add(new MInstr(MOp.Jmp, new MLabel(target)) { Line = line });
                    if (source.EndsUnconditionally)
                    {
                        if (!fallingInto.TryGetValue(from, out List<MBlock>? after)) fallingInto[from] = after = new();
                        after.Add(edge);
                    }
                    else jumpingTo.Add(edge);
                }
            }
            List<MBlock> withEdges = new(laid.Count + fallingInto.Count + jumpingTo.Count);
            for (int i = 0; i < laid.Count; i++)
            {
                withEdges.Add(laid[i]);
                if (fallingInto.TryGetValue(i, out List<MBlock>? after)) withEdges.AddRange(after);
            }
            withEdges.AddRange(jumpingTo);
            laid = withEdges;
        }
        _m.Blocks.Clear();
        _m.Blocks.AddRange(laid);
        _m.NextVReg = Math.Max(_m.NextVReg, f.NextVReg);
        Saved(f, p);
        // A THUNK C ENTERS GIVES NONE OF THEM A VALUE (Function.KeepsCallerRegisters):
        // what it calls on its way out reads them for C's, and one it saved it
        // wrote -- an instruction's own register (CPUID's EBX, REP MOVS's ESI
        // and EDI) the register file does not see. Refused, never a reference
        // the collector would not find.
        if (_m.Source.KeepsCallerRegisters && _m.SavedRegs.Count > 0)
            throw new InvalidOperationException($"{_m.Source.Name}: the way in from C writes {string.Join(", ", _m.SavedRegs)}, which it must leave as C left them");
    }

    /// <summary>
    /// A value's slot as an operand: the allocator's own below the frame
    /// pointer, or a home above it -- an argument's word, read as the
    /// argument load it is, not counted as a spill slot's traffic.
    /// </summary>
    protected static MMem Slot(int offset) => offset > 0 ? MMem.Frame(offset) : MMem.Spill(offset);

    protected MOperand PlaceOf((Place Place, int Reg, int Slot, object? Constant) at, MReg original)
        => at.Place switch
        {
            Place.Slot => Slot(at.Slot),
            Place.Constant => (MImm)at.Constant!,
            _ => Fixed(original) ? original : Out(at.Reg),
        };

    private MInstr Build(AInstr ai, int index, IPlacement p, Dictionary<ABlock, MBlock> blocks)
    {
        if (ai.Tag is not MInstr i)
        {
            if (ai.Made == Made.Jump) return new MInstr(MOp.Jmp, new MLabel(blocks[ai.Targets![0]])) { Line = ai.Line };
            int destination = ai.Regs[0].Reg;
            return Move(PlaceOf(p.Of(index, 0), MReg.Of(destination)), PlaceOf(p.Of(index, 1), MReg.Of(ai.Regs[1].Reg)), ai.Line);
        }
        // A jump table whose blocks the allocator laid out again: the blocks they are now.
        List<MBlock>? table = i.Table;
        if (i.Op == MOp.JmpTable && ai.Targets is { } targets && targets.Where((t, k) => !ReferenceEquals(blocks[t], i.Table![k])).Any())
            table = targets.Select(t => blocks[t]).ToList();
        MInstr n = i.Rebuilt(table);
        // The registers named, walked in the order Roles.RegsOf named them.
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
                    // A leaf's frame base is named nowhere (LowerInstr): as it is.
                    MReg? baseReg = m.Base is null ? null : _leaf && m.Base.Id == (int)Gpr.Ebp ? m.Base : (MReg)PlaceOf(p.Of(index, k++), m.Base);
                    MReg? indexReg = m.Index is null ? null : (MReg)PlaceOf(p.Of(index, k++), m.Index);
                    // Everything else of the operand as it was (WithRegisters):
                    // rebuilt field by field, a device load lost its mark here
                    // in long mode and the passes after took it for memory.
                    n.Operands.Add(m.WithRegisters(baseReg, indexReg));
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
