#nullable enable
namespace Corsac.Lang.X64;

/// <summary>
/// The control-flow tidying after allocation, where the blocks are final:
///
///   - a jump to a block that holds nothing but another jump goes straight
///     to that jump's target (threading), however long the chain;
///   - `jcc next; jmp other` becomes `j!cc other` and falls through;
///   - a jump to the very next block is dropped: control falls into it.
///
/// The selector writes every branch as a conditional jump and an
/// unconditional one, so that nothing depends on block order; this is where
/// block order is known and the jumps it makes redundant go.
///
/// And one move: `mov r32, r32` onto itself, where the register's last
/// write in the block was already a 32-bit one (ZeroExtended), or where
/// nothing reads its upper half before it is written again (UpperRead).
/// </summary>
internal static class Peephole
{
    public static void Run(MFunction m)
    {
        foreach (MBlock b in m.Blocks)
        {
            DropZeroExtensions(b.Instrs);
        }
        DropUnreadZeroExtensions(m);
        Thread(m);
        for (int i = 0; i < m.Blocks.Count; i++)
        {
            MBlock b = m.Blocks[i];
            MBlock? next = i + 1 < m.Blocks.Count ? m.Blocks[i + 1] : null;
            List<MInstr> s = b.Instrs;
            if (next is null || s.Count == 0)
            {
                continue;
            }
            MInstr last = s[^1];
            if (last.Op == MOp.Jmp && ((MLabel)last.Operands[0]).Target == next)
            {
                s.RemoveAt(s.Count - 1);
                if (s.Count == 0)
                {
                    continue;
                }
                last = s[^1];
            }
            if (s.Count >= 2 && last.Op == MOp.Jmp && s[^2].Op == MOp.Jcc
                && ((MLabel)s[^2].Operands[0]).Target == next)
            {
                MInstr jcc = s[^2];
                MBlock other = ((MLabel)last.Operands[0]).Target;
                s.RemoveRange(s.Count - 2, 2);
                s.Add(new MInstr(MOp.Jcc, new MLabel(other)) { Cond = jcc.Cond.Negate(), Line = jcc.Line });
            }
        }
    }

    /// <summary>
    /// A 4-BYTE MOVE ONTO ITSELF CLEARS THE UPPER HALF, and that is all it
    /// does: the allocator keeps one where a coalesced copy was written
    /// `mov r32, r32`. Where the register's last write before it in the
    /// block was already a 32-bit result, the hardware zeroed that half then
    /// -- every 32-bit write to a general register does -- and the move
    /// goes: `movzx eax, word [m]` followed by `mov eax, eax` was every char
    /// load. After MOVSX, whose destination is 64 bits, the move is the
    /// zero-extension and stays.
    /// </summary>
    private static void DropZeroExtensions(List<MInstr> s)
    {
        for (int k = s.Count - 1; k >= 1; k--)
        {
            MInstr i = s[k];
            if (i.Op != MOp.Mov || i.Width != 4 || i.Operands.Count != 2
                || i.Operands[0] is not MReg { IsPhys: true, IsFloat: false } r || i.Operands[1] is not MReg c || c.Id != r.Id)
            {
                continue;
            }
            for (int j = k - 1; j >= 0; j--)
            {
                if (!WritesRegister(s[j], r.Id))
                {
                    continue;
                }
                // CMPXCHG WRITES ITS ACCUMULATOR ONLY WHERE THE COMPARE
                // FAILS, a 32-bit result; where it succeeds EAX is what it
                // was. Zero-extended after it, then, exactly when before.
                if (s[j] is { Op: MOp.Cmpxchg, Width: 4 } && r.Id == (int)Gpr.Rax && !NamesRegister(s[j], r.Id))
                {
                    continue;
                }
                if (ZeroExtended(s[j], r.Id))
                {
                    s.RemoveAt(k);
                }
                break;
            }
        }
    }

    /// <summary>
    /// A 4-BYTE MOVE ONTO ITSELF WHOSE UPPER HALF NOTHING READS goes too:
    /// before anything reads the register whole -- an address, a 64-bit
    /// operation, a call or a return reading it -- it is written again or the
    /// function returns, along every way on from the move, its next readers
    /// till then taking the low 32 bits alone (UpperRead). `mov edi, edi`
    /// where a function starts, ahead of a `movsxd rax, edi`, an `xchg [m],
    /// edi` or a `cmp ecx, esi` and a branch, was one. An int's result is one
    /// too, at the return: whatever calls the function takes eax alone -- C's
    /// code as the System V ABI has it, this compiler's as the move after each
    /// of its calls says, the caller's own zero-extension, which `call f;
    /// mov eax, eax; leave; ret` repeated.
    /// </summary>
    private static void DropUnreadZeroExtensions(MFunction m)
    {
        bool intResult = m.Source.Returns == Corsac.Lang.Ir.IrType.I32;
        // WHERE EACH BLOCK GOES: its jumps' and table's targets, and the next
        // block where it falls through; null where that is not known (an
        // indirect jump), which every register's upper half is taken to be
        // read past.
        Dictionary<MBlock, List<MBlock>?> next = new();
        for (int b = 0; b < m.Blocks.Count; b++)
        {
            MBlock block = m.Blocks[b];
            List<MBlock>? to = new();
            foreach (MInstr i in block.Instrs)
            {
                if (i.Op is MOp.Jmp or MOp.Jcc && i.Operands.Count > 0 && i.Operands[0] is MLabel label) to.Add(label.Target);
                else if (i.Op == MOp.JmpTable && i.Table is { } table) to.AddRange(table);
                else if (i.Op == MOp.JmpInd) to = null;
                if (to is null) break;
            }
            if (to is not null && !block.EndsUnconditionally && b + 1 < m.Blocks.Count) to.Add(m.Blocks[b + 1]);
            next[block] = to;
        }
        // Which registers' upper halves may be read from each block's entry
        // on, to the fixpoint: a backward walk of every block from what its
        // successors read.
        Dictionary<MBlock, ulong> entry = m.Blocks.ToDictionary(block => block, _ => 0UL);
        ulong Out(MBlock block)
        {
            if (next[block] is not { } to) return ulong.MaxValue;
            ulong read = 0;
            foreach (MBlock t in to) read |= entry.GetValueOrDefault(t, ulong.MaxValue);
            return read;
        }
        for (bool changed = true; changed;)
        {
            changed = false;
            for (int b = m.Blocks.Count - 1; b >= 0; b--)
            {
                MBlock block = m.Blocks[b];
                ulong read = Out(block);
                for (int j = block.Instrs.Count - 1; j >= 0; j--) read = UpperRead(block.Instrs[j], read, intResult);
                if (read != entry[block]) { entry[block] = read; changed = true; }
            }
        }
        foreach (MBlock block in m.Blocks)
        {
            List<MInstr> s = block.Instrs;
            ulong read = Out(block);
            for (int k = s.Count - 1; k >= 0; k--)
            {
                MInstr i = s[k];
                if (i.Op == MOp.Mov && i.Width == 4 && i.Operands.Count == 2
                    && i.Operands[0] is MReg { IsPhys: true, IsFloat: false } r && i.Operands[1] is MReg c && c.Id == r.Id
                    && (read & (1UL << r.Id)) == 0)
                {
                    // Nothing after reads more than its low half: the move
                    // goes, and what is read before it is what was read after.
                    s.RemoveAt(k);
                    continue;
                }
                read = UpperRead(i, read, intResult);
            }
        }
    }

    /// <summary>
    /// The registers whose upper halves may be read just before the
    /// instruction, given those read just after it: a whole write of one --
    /// a 32-bit result, or a 64-bit write of it alone -- means nothing after
    /// reads its old upper half; a read of more than the low half adds it.
    /// </summary>
    private static ulong UpperRead(MInstr i, ulong after, bool intResult)
    {
        ulong read = after;
        // A call takes every register it clobbers whole.
        if (i.Op is MOp.Call or MOp.CallInd or MOp.CallKeepRax or MOp.CallKeepCas)
        {
            foreach (int clobbered in Roles.ImplicitDefs(i))
                if (clobbered < MReg.XmmBase) read &= ~(1UL << clobbered);
        }
        for (int k = 0; k < i.Operands.Count; k++)
        {
            if (i.Operands[k] is MReg { IsFloat: false } r && r.Id < MReg.XmmBase && (Roles.Of(i, k) & Roles.Role.Def) != 0
                && WritesWhole(i, r.Id))
            {
                read &= ~(1UL << r.Id);
            }
        }
        for (int k = 0; k < i.Operands.Count; k++)
        {
            switch (i.Operands[k])
            {
                case MMem mem:
                    if (mem.Base is { IsFloat: false } b && b.Id < MReg.XmmBase) read |= 1UL << b.Id;
                    if (mem.Index is { IsFloat: false } x && x.Id < MReg.XmmBase) read |= 1UL << x.Id;
                    break;
                case MReg { IsFloat: false } r when r.Id < MReg.XmmBase && (Roles.Of(i, k) & Roles.Role.Use) != 0 && !ReadsLowHalf(i, k):
                    read |= 1UL << r.Id;
                    break;
            }
        }
        foreach (int used in Roles.ImplicitUses(i))
        {
            if (used >= MReg.XmmBase) continue;
            // What a function returning an int hands back is eax alone.
            if (i.Op == MOp.Epilogue && intResult && used == (int)Gpr.Rax) continue;
            read |= 1UL << used;
        }
        return read;
    }

    /// <summary>
    /// Whether the instruction's write of the register replaces all 64 bits
    /// of it: a 32-bit result (ZeroExtended), or a 64-bit write of it alone
    /// (MOVSX's destination is always 64 bits). Not a partial or a
    /// conditional one -- a byte's SETcc, CMOVcc, CMPXCHG's accumulator.
    /// </summary>
    private static bool WritesWhole(MInstr i, int reg)
        => ZeroExtended(i, reg) || i.Operands.Count > 0 && i.Operands[0] is MReg { Id: var d } && d == reg
            && (i.Op == MOp.Movsx || i is { Op: MOp.Mov or MOp.Lea or MOp.Pop, Width: 8 });

    /// <summary>
    /// Whether operand <paramref name="k"/> of the instruction reads no more
    /// than a register's low 32 bits: an operand of a 4-byte operation (a
    /// 32-bit divisor or multiplier among them), the source of a MOVZX or
    /// MOVSX of four bytes or fewer, a shift's count.
    /// Anything not named here counts as reading all 64.
    /// </summary>
    private static bool ReadsLowHalf(MInstr i, int k) => i.Op switch
    {
        MOp.Movzx or MOp.Movsx => k == 1 && i.Width <= 4,
        MOp.Shl or MOp.Shr or MOp.Sar or MOp.Rol or MOp.Ror => k == 1 || i.Width == 4,
        MOp.Mov or MOp.Add or MOp.Sub or MOp.And or MOp.Or or MOp.Xor or MOp.Cmp or MOp.Test or MOp.Adc or MOp.Sbb
            or MOp.Imul or MOp.Imul3 or MOp.Neg or MOp.Not or MOp.Xchg or MOp.Xadd or MOp.Cmpxchg
            or MOp.Div or MOp.Idiv or MOp.MulWide or MOp.ImulWide => i.Width == 4,
        _ => false,
    };

    /// <summary>Whether the register is one of the instruction's named operands.</summary>
    private static bool NamesRegister(MInstr i, int reg) => i.Operands.Any(o => o is MReg r && r.Id == reg);

    /// <summary>Whether an instruction writes the register, named or not.</summary>
    private static bool WritesRegister(MInstr i, int reg)
    {
        for (int k = 0; k < i.Operands.Count; k++)
        {
            if (i.Operands[k] is MReg r && r.Id == reg && (Roles.Of(i, k) & Roles.Role.Def) != 0)
            {
                return true;
            }
        }
        return Roles.ImplicitDefs(i).Contains(reg);
    }

    /// <summary>
    /// Whether an instruction's write of the register is a 32-bit result,
    /// whose upper half the hardware zeroes: a 4-byte move or ALU operation
    /// into it, a shift by a count that is not zero, or MOVZX, whose
    /// destination is 32 bits. A shift by zero, CMOV, SETcc, a 64-bit form
    /// and anything not named here count as not.
    /// </summary>
    private static bool ZeroExtended(MInstr i, int reg)
    {
        // A 4-byte XCHG or XADD writes its register operands as any 32-bit
        // result is written: `lock xadd [m], ecx` left the `mov ecx, ecx`
        // after every Interlocked.Increment.
        if (i.Op is MOp.Xchg or MOp.Xadd && i.Width == 4 && !Roles.ImplicitDefs(i).Contains(reg))
        {
            return NamesRegister(i, reg);
        }
        if (i.Operands.Count == 0 || i.Operands[0] is not MReg d || d.Id != reg || Roles.ImplicitDefs(i).Contains(reg))
        {
            return false;
        }
        for (int k = 1; k < i.Operands.Count; k++)
        {
            if (i.Operands[k] is MReg other && other.Id == reg && (Roles.Of(i, k) & Roles.Role.Def) != 0)
            {
                return false;
            }
        }
        return i.Op switch
        {
            MOp.Movzx => true,
            MOp.Mov or MOp.Add or MOp.Sub or MOp.And or MOp.Or or MOp.Xor or MOp.Adc or MOp.Sbb
                or MOp.Imul or MOp.Imul3 or MOp.Neg or MOp.Not => i.Width == 4,
            MOp.Shl or MOp.Shr or MOp.Sar => i.Width == 4 && i.Operands.Count == 2 && i.Operands[1] is MImm { IsPlain: true } count && (count.Value & 31) != 0,
            _ => false,
        };
    }

    /// <summary>Retargets every jump past blocks that only jump on.</summary>
    private static void Thread(MFunction m)
    {
        MBlock Final(MBlock target)
        {
            // Bounded: a cycle of empty jumps (an infinite loop written
            // `while (true) { }`) is left where it is.
            for (int hops = 0; hops < 16; hops++)
            {
                if (target.Instrs.Count != 1 || target.Instrs[0].Op != MOp.Jmp)
                {
                    break;
                }
                MBlock onward = ((MLabel)target.Instrs[0].Operands[0]).Target;
                if (onward == target)
                {
                    break;
                }
                target = onward;
            }
            return target;
        }

        foreach (MBlock b in m.Blocks)
        {
            for (int k = 0; k < b.Instrs.Count; k++)
            {
                MInstr i = b.Instrs[k];
                if (i.Op is MOp.Jmp or MOp.Jcc)
                {
                    MBlock target = ((MLabel)i.Operands[0]).Target;
                    MBlock final = Final(target);
                    if (final != target)
                    {
                        i.Operands[0] = new MLabel(final);
                    }
                }
                else if (i.Op == MOp.JmpTable)
                {
                    for (int t = 0; t < i.Table!.Count; t++)
                    {
                        i.Table[t] = Final(i.Table[t]);
                    }
                }
            }
        }
    }
}
