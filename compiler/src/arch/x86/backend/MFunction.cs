#nullable enable
using Corsac.Lang.Frames;
using Corsac.Lang.Ir;

namespace Corsac.Lang.X86;

using Block = Corsac.Lang.Ir.Block;

/// <summary>A function after selection: blocks, frame, and what the allocator decided.</summary>
public sealed class MFunction
{
    /// <summary>
    /// Machine registers holding half of a 64-bit integer or a pair the
    /// selector made for one. Never references, whatever the interim rule
    /// says about every other word.
    /// </summary>
    public HashSet<int> WideHalves { get; } = new();

    /// <summary>
    /// The live-reference description of each call in this function, keyed
    /// by the instruction the encoder will emit -- which is the instruction
    /// the ALLOCATOR produced, since it rebuilds every one it rewrites.
    /// </summary>
    public Dictionary<MInstr, Safepoint> Safepoints { get; } = new(ReferenceEqualityComparer.Instance);

    /// <summary>Which IR frame slot words each call's map names (FrameSlotLiveness), or null: all of them at every call.</summary>
    internal Corsac.Lang.Frames.FrameSlotLiveness.Result? SlotLiveness { get; set; }

    public Function Source { get; }
    public List<MBlock> Blocks { get; } = new();
    public Frame Frame { get; }
    /// <summary>The next virtual register's number. Past the first: that one is the base register holding a value, in both modes (MReg.EbpData).</summary>
    public int NextVReg { get; set; } = MReg.FirstVirtual + 1;

    /// <summary>Whether a card mark calls the object's stub (X86Backend.CardStub).</summary>
    public bool UsesCardStub { get; set; }

    /// <summary>Whether a barrier's slow path calls the object's stub (X86Backend.BarrierStub; long mode's written by X64Backend.EmitBarrierStub).</summary>
    public bool UsesBarrierStub { get; set; }

    /// <summary>
    /// Whether this function was selected with the hosted ABI's leave to use
    /// the MMX registers (X86Backend.AutomaticPacked). Recorded here because
    /// the emitted-code audit refuses a packed instruction in a function
    /// selected without it: freestanding code does not own the x87 state the
    /// MMX registers alias, except inside a packed block (MInstr.OwnsState).
    /// </summary>
    public bool AutomaticPacked { get; init; }

    /// <summary>
    /// Whether it may use the SSE registers anywhere: hosted code, whose
    /// operating system enables them. Freestanding, the instructions inside
    /// a state FXSAVE keeps say so themselves (MInstr.OwnsState 2).
    /// </summary>
    public bool SseState { get; init; }

    /// <summary>
    /// Virtual registers of the XMM class: scalar floats where --cpu has SSE
    /// (Selector's _xmm32, _xmm64), allocated among XMM0 up to XmmClassCount.
    /// </summary>
    public HashSet<int> XmmRegs { get; } = new();

    /// <summary>
    /// The bytes a spilled XMM value is moved by: 8 (MOVSD) where --cpu has
    /// SSE2, a float's as well as a double's; 4 (MOVSS) where it has SSE
    /// alone, which has floats in the XMM registers and no doubles; 16
    /// (MOVUPS) in a function whose Packed128 values are the class's
    /// (Select.PackedValues).
    /// </summary>
    public int XmmSpillWidth { get; set; } = 8;

    /// <summary>
    /// How many XMM registers the XMM class has, from XMM0: all eight, or
    /// XmmShared in a function whose packed operations hold the rest
    /// (Selector.PackedFree).
    /// </summary>
    public int XmmClassCount { get; set; } = 8;
    public const int XmmShared = 4;

    /// <summary>Virtual registers that must land in a register with an 8-bit form (EAX..EBX).</summary>
    public HashSet<int> ByteRegs { get; } = new();

    /// <summary>Callee-saved registers the allocator handed out; the prologue saves exactly these.</summary>
    public List<Gpr> SavedRegs { get; } = new();

    /// <summary>
    /// THE BASE REGISTER HOLDS A VALUE IN THIS FUNCTION (FamilyTarget, for a
    /// leaf, which is Frameless), both modes: saved first of the registers
    /// it saves (SavedRegs[0]), so the caller's base register lies just
    /// below the return address -- a frame link as a prologue's would be,
    /// which a fault's walk reads (Runtime.FrameAt).
    /// </summary>
    public bool FramePointerFree { get; set; }

    /// <summary>
    /// A FUNCTION WITH NO FRAME OF ITS OWN -- a leaf (FamilyTarget.Leaf, both
    /// modes), or on i386 one whose only calls never return (ColdFrames): no
    /// `push ebp; mov ebp, esp` and no `leave`. It pushes the registers it saves, then reserves
    /// its frame (`sub esp, n`), and every slot and argument is addressed off
    /// ESP, which nothing moves between that and the epilogue -- `add esp, n`,
    /// the pops, `ret` (FramePointerFree.Run, Encoder) -- a reserved frame
    /// (ReservedFrame) the other way round. It calls nothing that
    /// returns, so no frame links to it but the one it makes for a call that
    /// does not (ColdFrames); a thread stopped in it is found by its first
    /// bytes, which are not a framed function's `push ebp; mov ebp, esp`
    /// (Runtime.FrameAt). A leaf given 16-byte aligned XMM slots has its frame
    /// again (AlignedSlots turns this off). On a 486 the three instructions are seven clocks of
    /// every call of a leaf, `leave` five of them, and four bytes; an `add
    /// esp, n` and an address generation stall or two (ESP written, then
    /// used as a base) are paid back from them where the leaf has a frame.
    /// </summary>
    public bool Frameless { get; set; }

    /// <summary>
    /// A FRAMELESS FUNCTION THAT CALLS, BUT ONLY WHAT NEVER RETURNS (a bounds
    /// check's IndexOutOfRange, a throw moved out of it -- each call followed
    /// by the selector's unreachable, i386's INT3 or long mode's UD2;
    /// FramePointerFree.Run decides), both modes: its frame is made on the
    /// way to such a call alone (Encoder.ColdFrame, ColdFrameL) -- `push ebp
    /// ; mov ebp, esp` where it saves no register and keeps no slot, else
    /// (ReservedFrame) `mov [esp + X], ebp ; lea ebp, [esp + X]` -- and is
    /// then a frame as any prologue's, the return address just above the
    /// saved base register: what the callee's frame links to, what a trace
    /// and the collector walk. The way that returns pays nothing for a frame
    /// it never needs. In long mode the plain form's push is also what makes
    /// the stack a multiple of sixteen at the call.
    /// </summary>
    public bool ColdFrames { get; set; }

    /// <summary>
    /// A COLD-FRAMED FUNCTION THAT SAVES REGISTERS OR KEEPS SLOTS (ColdFrames):
    /// laid out as a framed function is, the word its saved base register
    /// would hold kept and not written -- `sub esp, N + word` then the saves
    /// on the way in, the pops then `add esp, N + word` on the way out -- so
    /// on the way to a call that never returns `mov [esp + X], ebp ; lea
    /// ebp, [esp + X]` (X the frame and the saves) makes the very frame a
    /// prologue would have: every slot and save where the collector's map of
    /// the call says. Only with at most two such calls (eight bytes each on
    /// i386, ten in long mode; the plain form's three and four) and X within
    /// a byte's displacement; else the function is framed. The base
    /// register's word alone is reserved by `push eax` and dropped by `pop
    /// ecx`. LONG MODE pushes one word more below the saves where the slots
    /// and saves are an odd number of words (Encoder.ReservedPad): the
    /// stack a multiple of sixteen at the call.
    /// </summary>
    public bool ReservedFrame { get; set; }

    /// <summary>
    /// Bytes of Packed128 spill slots below the saved registers, at ESP after
    /// the prologue aligns it down to sixteen (AlignedSlots): read and
    /// written by MOVAPS. Zero in every other function.
    /// </summary>
    public int AlignedArea { get; set; }

    public MFunction(Function source)
    {
        Source = source;
        Frame = new Frame();
    }

    public MReg NewReg() => new(NextVReg++);

    // ---- long mode's (Select.Long.cs, LongTarget and the encoder's long forms) -----------

    /// <summary>
    /// A new virtual register of the XMM class: in XmmRegs, the one record
    /// both modes keep of which virtual registers are (an operand's own
    /// IsFloat is a note for a listing, and a pass that renames a register
    /// makes operands without it).
    /// </summary>
    public MReg NewFloat()
    {
        MReg r = new(NextVReg++, true);
        XmmRegs.Add(r.Id);
        return r;
    }

    /// <summary>
    /// Every callee-saved register is saved, used or not. A function that
    /// enters the collector needs this: a caller's reference may live only in
    /// one of them, and the scan reads memory. See GcRoots.Enter.
    /// </summary>
    public bool SavesEverything { get; set; }

    /// <summary>
    /// The process entry: the loader jumps to it with RSP sixteen-byte aligned
    /// and no return address pushed, so after `push rbp` the frame is eight
    /// bytes off the alignment every call assumes. Its prologue realigns.
    /// </summary>
    public bool RealignsStack { get; set; }

    /// <summary>
    /// Long mode: whether the function is entered with the stack at sixteen
    /// before its call, as System V says -- hosted code's, every way into
    /// which keeps it (X64Backend.Hosted). Freestanding code is entered by
    /// its image's own stubs (an interrupt's frame, a context switch) and
    /// is promised nothing. What a packed spill slot's MOVAPS stands on
    /// (FamilyPasses.AlignedSpills).
    /// </summary>
    public bool StackAtSixteen { get; set; }

    /// <summary>Bytes of arguments this function passes on the stack at its widest call.</summary>
    public int OutgoingBytes { get; set; }

    /// <summary>
    /// Successors(index) into a list the caller keeps (emptied first): the
    /// iterator was an object for every block of every function the allocator
    /// and the peephole walked.
    /// </summary>
    public void SuccessorsInto(int index, List<MBlock> into)
    {
        into.Clear();
        MBlock b = Blocks[index];
        for (int k = 0; k < b.Instrs.Count; k++)
        {
            MInstr i = b.Instrs[k];
            if (i.Op is MOp.Jmp or MOp.Jcc) into.Add(((MLabel)i.Operands[0]).Target);
            else if (i.Op == MOp.JmpTable) into.AddRange(i.Table!);
        }
        if (!b.EndsUnconditionally && index + 1 < Blocks.Count) into.Add(Blocks[index + 1]);
    }

    /// <summary>The blocks control can reach from the one at <paramref name="index"/>: jump targets, then the next block unless it never falls through.</summary>
    public IEnumerable<MBlock> Successors(int index)
    {
        MBlock b = Blocks[index];
        foreach (MInstr i in b.Instrs)
        {
            switch (i.Op)
            {
                case MOp.Jmp:
                case MOp.Jcc:
                    yield return ((MLabel)i.Operands[0]).Target;
                    break;
                case MOp.JmpTable:
                    foreach (MBlock t in i.Table!)
                    {
                        yield return t;
                    }
                    break;
            }
        }
        if (!b.EndsUnconditionally && index + 1 < Blocks.Count)
        {
            yield return Blocks[index + 1];
        }
    }

    public MBlock NewBlock(string name, MBlock? after = null, Block? source = null)
    {
        MBlock b = new(name) { Source = source };
        if (after is null)
        {
            Blocks.Add(b);
        }
        else
        {
            Blocks.Insert(Blocks.IndexOf(after) + 1, b);
        }
        return b;
    }
}
