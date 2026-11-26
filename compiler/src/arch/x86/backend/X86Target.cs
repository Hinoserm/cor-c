#nullable enable
using Corsac.Lang.RegAlloc;

namespace Corsac.Lang.X86;

/// <summary>
/// i386 AS THE ONE ALLOCATOR SEES IT (Corsac.Lang.RegAlloc), what is its
/// own beside the family's (FamilyTarget): one general class of six
/// registers -- EAX, ECX, EDX first, free across no call, then the
/// callee-saved EBX, ESI, EDI -- of which EAX to EBX have byte halves, and
/// the XMM class where the function has one; ESP and EBP never touched.
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
}
