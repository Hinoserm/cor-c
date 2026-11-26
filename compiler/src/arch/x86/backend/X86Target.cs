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
    /// i386's registers: EAX, ECX, EDX free across no call, then the
    /// callee-saved EBX, ESI, EDI; EAX to EBX have byte halves. The XMM
    /// class holds scalar floats where --cpu has SSE and the code owns its
    /// state (Selector's _xmm32, _xmm64), every one caller-saved, as i386's
    /// C has them. A leaf's file has EBP besides, pushed first of those it
    /// saves (MFunction.FramePointerFree), in a function with no frame of
    /// its own (MFunction.Frameless); a fault in such a function finds its
    /// caller by its first bytes (Runtime.FrameAt).
    /// </summary>
    private static readonly (RegisterFile Whole, RegisterFile Leaf, RegisterFile CallerOnly) Of = Files(
        new[] { (int)Gpr.Eax, (int)Gpr.Ecx, (int)Gpr.Edx }, new[] { (int)Gpr.Ebx, (int)Gpr.Esi, (int)Gpr.Edi },
        new[] { (int)Gpr.Eax, (int)Gpr.Ecx, (int)Gpr.Edx, (int)Gpr.Ebx }, 8);

    public X86Target(MFunction m) : base(m)
    {
    }

    public override RegisterFile Registers => _registers ??= Halved(_m.Source.KeepsCallerRegisters ? Of.CallerOnly : _leaf ? Of.Leaf : Of.Whole);

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
                Name = f.Classes[1].Name, CalleeSaved = f.Classes[1].CalleeSaved, HoldsReferences = false,
                Preference = Enumerable.Range(MReg.XmmBase, _m.XmmClassCount).ToArray(),
            },
        },
    };

    protected override string InstrText(MInstr i) => AsmText.Line(_m, i);

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

    /// <summary>A whole word's constant: any, a symbol's address among them.</summary>
    protected override MImm? ConstantMoved(MInstr i)
        => i.Op == MOp.Mov && i.Width == 4 && i.Operands.Count == 2 && i.Operands[0] is MReg && i.Operands[1] is MImm imm ? imm : null;

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
    protected override HashSet<MInstr> Homes()
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

    /// <summary>
    /// The callee-saved registers the function touches, or all of them where
    /// it catches (a landing pad is entered with them as the thrower left
    /// them); a leaf has no frame, and one that gave EBP a value saves it
    /// first, as the prologue's push would have.
    /// </summary>
    protected override void Saved(AFunction f, IPlacement p)
    {
        bool catches = f.Blocks.Any(b => b.LandingPad);
        foreach (int g in Of.Whole.Classes[0].CalleeSaved)
            if (catches || p.Touched.Contains(g)) _m.SavedRegs.Add((Gpr)g);
        _m.Frameless = _leaf;
        _m.FramePointerFree = _leaf && p.Touched.Contains((int)Gpr.Ebp);
        if (_m.FramePointerFree) _m.SavedRegs.Insert(0, Gpr.Ebp);
    }

    /// <summary>An XMM value by MOVSS/MOVSD at the width its home or its class's spill slot has; a general one by MOV.</summary>
    protected override MInstr Move(MOperand to, MOperand from, int line)
        => to is MReg { IsXmm: true } || from is MReg { IsXmm: true }
            ? new MInstr(MOp.MovF, to, from)
            {
                Width = from is MMem { Disp: > 0 } home && _homeWidths.TryGetValue(home.Disp, out int width) ? width : _m.XmmSpillWidth,
                Line = line,
            }
            : new MInstr(MOp.Mov, to, from) { Line = line };
}
