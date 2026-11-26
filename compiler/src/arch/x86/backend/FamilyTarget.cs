#nullable enable
using Corsac.Lang.Frames;
using Corsac.Lang.RegAlloc;

namespace Corsac.Lang.X86;

/// <summary>
/// WHAT BOTH MODES' ALLOCATOR TARGETS DO ALIKE (X86Target, X64Target): the
/// function rebuilt from what the allocator decided. Each instruction is made
/// again whole (MInstr.Rebuilt) with every register it names in its place --
/// a register, its spill slot as a memory operand, its constant as an
/// immediate -- its memory operands whole too (MMem.WithRegisters); a
/// spilled value's loads go before it and its stores after; the copies and
/// jumps the allocator made are made; a call's live values go into its stack
/// map; and the moves on an edge go into a block of the edge's own. What a
/// mode does its own way it is asked: the move of a class's value (Move), a
/// constant made again (Remade), which registers are no allocation's
/// (Fixed), the register a place is (Out), and what the function saves
/// (Saved).
/// </summary>
internal abstract class FamilyTarget
{
    protected readonly MFunction _m;
    /// <summary>A leaf whose frame base is a register like any other (each mode's Leaf says when).</summary>
    protected readonly bool _leaf;

    protected FamilyTarget(MFunction m, bool leaf)
    {
        _m = m;
        _leaf = leaf;
    }

    /// <summary>A move of a value of the class the operands say, between a register and a register or a slot.</summary>
    protected abstract MInstr Move(MOperand to, MOperand from, int line);

    /// <summary>The copy the allocator made of one virtual register into another, each where it was placed.</summary>
    protected virtual MInstr Copy(int destination, MOperand to, MOperand from, int line) => Move(to, from, line);

    /// <summary>A value made again in a register before an instruction: its constant, its address, or its slot's load.</summary>
    protected abstract MInstr Remade(int reg, int slot, object? constant, int line);

    /// <summary>A register an instruction names that is the machine's own, and so no allocation's to place.</summary>
    protected abstract bool Fixed(MReg original);

    /// <summary>The register a value was placed in, as an operand: the frame's base register as the value it holds there.</summary>
    protected abstract MReg Out(int reg);

    /// <summary>What the function saves and how its frame stands, from the registers the allocation touched.</summary>
    protected abstract void Saved(AFunction f, IPlacement p);

    /// <summary>The mode's registers as the allocator has them, for this function.</summary>
    public abstract RegisterFile Registers { get; }

    /// <summary>The class of a register: the general ones 0, the XMM ones 1 -- a machine register by its number, a virtual one by the function's record.</summary>
    public int ClassOf(int reg) => reg < MReg.FirstVirtual ? (reg >= MReg.XmmBase ? 1 : 0) : _m.XmmRegs.Contains(reg) ? 1 : 0;

    public bool Immediate(object constant) => constant is MImm;

    /// <summary>What a mode finds out about the whole function before its instructions are lowered (the argument homes).</summary>
    protected abstract void BeforeLowering();

    /// <summary>One instruction as the allocator sees it: the registers it names, what may be memory or a constant, its jumps, its effects.</summary>
    protected abstract AInstr LowerInstr(MInstr i, Dictionary<MBlock, ABlock> blocks);

    /// <summary>
    /// What an instruction does besides its registers, for the scheduler
    /// (Effect): the flags and memory of the plain integer and scalar SSE
    /// instructions it may move; anything else -- a call, a branch, a push, a
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
                or MOp.XmmOpV or MOp.XmmOpVDef or MOp.XmmShiftV or MOp.XmmExtractV or MOp.XmmInsertV => Effect.None,
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
        BeforeLowering();
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
        // what it calls on its way out reads them for C's. Refused, never a
        // reference the collector would not find.
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
            return Copy(destination, PlaceOf(p.Of(index, 0), new MReg(destination)), PlaceOf(p.Of(index, 1), new MReg(ai.Regs[1].Reg)), ai.Line);
        }
        // A jump table whose blocks the allocator laid out again: the blocks they are now.
        List<MBlock>? table = i.Table;
        if (i.Op == MOp.JmpTable && ai.Targets is { } targets && targets.Where((t, k) => !ReferenceEquals(blocks[t], i.Table![k])).Any())
            table = targets.Select(t => blocks[t]).ToList();
        MInstr n = i.Rebuilt(table);
        // The registers named, walked in the order each mode's RegsOf named them.
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
                    // A leaf's frame base is named nowhere (each mode's LowerInstr): as it is.
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
