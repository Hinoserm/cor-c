#nullable enable
using Corsac.Lang.Ir;

namespace Corsac.Lang.Opt;

using Block = Corsac.Lang.Ir.Block;

/// <summary>
/// A STRUCT ARGUMENT'S COPY THE CALLEE CANNOT TELL FROM THE ORIGINAL, NOT
/// MADE. C# hands a struct parameter a copy of its own, and lowering makes
/// it: the argument's bytes copied into a slot of the frame, that slot's
/// address passed. A loop `acc = Step(acc, i)` copied acc's eight bytes
/// every lap -- four moves on a 486 -- for a callee that only read them.
///
/// The copy is the original's address instead where nobody could see the
/// difference, as LLVM drops a byval copy into a readonly, nocapture
/// parameter and GCC's mod-ref does the same:
///   - THE CALLEE ONLY READS THE PARAMETER (ReadOnly): in its body the
///     parameter's address, and anything copied or offset from it, is only
///     loaded through, copied from, or handed on to another callee's
///     parameter that is itself only read. It is never written through,
///     stored anywhere, returned or given to an unknown callee -- so the
///     callee can neither change the original nor keep its address. Solved
///     over the module to its greatest fixed point: a parameter is read only
///     until something is found writing or keeping it, cycles included.
///   - THE ORIGINAL IS A SLOT OF THIS FRAME WHOSE ADDRESS GOES NOWHERE ELSE
///     (Private): loaded, stored, filled and copied here, and handed only to
///     read-only parameters. Nothing the callee can reach writes it during
///     the call. A pointer into the heap is not such an original: another
///     path to its bytes may write them while the callee reads them.
///   - OR THIS FUNCTION'S OWN STRUCT PARAMETER PASSED BY VALUE
///     (Function.ValueParams): its caller's copy made for this call, or a
///     fresh value of the caller's own, nothing writes while this call
///     runs -- never rewritten as a register, its storage only read,
///     written, filled or copied here or handed to parameters only read
///     (ParamPrivate), and at least as large as the copy.
///   - NOTHING BETWEEN THE COPY AND THE CALL WRITES MEMORY OR CALLS, and no
///     other operand of the call is the original's address (`F(acc, ref
///     acc)` must still copy).
///   - THE COPY'S SLOT IS USED BY NOTHING ELSE: the copy and the one operand.
/// Run late (Pipeline.RunLate), after the last inliner and before
/// SlotScalars, over what is left as real calls.
/// </summary>
public static class ArgumentCopies
{
    /// <summary>The copies dropped (argument copies and call slots both).</summary>
    public static int Run(Module m)
    {
        Dictionary<string, Function> byName = new(StringComparer.Ordinal);
        HashSet<string> twice = new(StringComparer.Ordinal);
        foreach (Function f in m.Functions)
            if (!byName.TryAdd(f.Name, f)) twice.Add(f.Name);
        // A NAME TWO BODIES HAVE says nothing of which one runs: neither is asked.
        foreach (string name in twice) byName.Remove(name);
        Dictionary<Function, bool[]> readOnly = ReadOnly(m, byName);
        int dropped = 0;
        foreach (Function f in m.Functions) dropped += Drop(f, byName, readOnly);
        return dropped;
    }

    // ---- which parameters a function only reads --------------------------

    private static Dictionary<Function, bool[]> ReadOnly(Module m, Dictionary<string, Function> byName)
    {
        Dictionary<Function, bool[]> answer = new(ReferenceEqualityComparer.Instance);
        foreach (Function f in m.Functions)
        {
            bool[] r = new bool[f.Params.Count];
            // Never assumed: an async or iterator body, whose frame is a
            // block of the heap and what it reads may be read after a
            // suspension; a body this compile does not have (a header); and
            // a function its provider exports (Module.GoesElsewhere) -- the
            // provider's body is what runs, and nothing records that this
            // image took its copy for it, as a body inlined across images is
            // recorded (KernelAssumptions): a kernel whose copy writes its
            // parameter would write the caller's original.
            if (f.Async is null && f.Blocks.Count > 0 && !m.GoesElsewhere(f)) Array.Fill(r, true);
            answer[f] = r;
        }
        for (bool changed = true; changed;)
        {
            changed = false;
            foreach (Function f in m.Functions)
            {
                bool[] r = answer[f];
                if (!r.Any(x => x)) continue;
                Dictionary<VReg, int> writes = Writes(f);
                for (int p = 0; p < r.Length; p++)
                    if (r[p] && !OnlyRead(f, f.Params[p], writes, byName, answer)) { r[p] = false; changed = true; }
            }
        }
        return answer;
    }

    /// <summary>
    /// Whether a struct parameter's storage (its register and those made of
    /// it, `derived`) is this function's alone: loaded, stored, filled or
    /// copied through, or handed to a parameter only read -- never kept,
    /// returned or given where it could be written from elsewhere.
    /// </summary>
    private static bool ParamPrivate(Function f, HashSet<VReg> derived, Dictionary<string, Function> byName, Dictionary<Function, bool[]> readOnly)
    {
        foreach (Block b in f.Blocks)
            foreach (Instr i in b.Instrs)
                for (int k = 0; k < i.Operands.Count; k++)
                {
                    if (i.Operands[k] is not RegOperand { Reg: var r } || !derived.Contains(r)) continue;
                    bool fine = i.Op switch
                    {
                        Opcode.Load or Opcode.Store or Opcode.MemSet => k == 0,
                        Opcode.MemCopy => k is 0 or 1,
                        Opcode.Copy or Opcode.Add => i.Dest is { } d && derived.Contains(d),
                        Opcode.Call => i.Callee is { } name && byName.TryGetValue(name, out Function? callee)
                                       && readOnly.TryGetValue(callee, out bool[]? ro) && k < ro.Length && ro[k],
                        _ => false,
                    };
                    if (!fine) return false;
                }
        return true;
    }

    /// <summary>Whether a function's register `root` -- a parameter, never written again -- is only read through.</summary>
    private static bool OnlyRead(Function f, VReg root, Dictionary<VReg, int> writes, Dictionary<string, Function> byName, Dictionary<Function, bool[]> readOnly)
    {
        HashSet<VReg> derived = Derived(f, root, writes, out bool rewritten);
        if (rewritten) return false;
        foreach (Block b in f.Blocks)
            foreach (Instr i in b.Instrs)
                for (int k = 0; k < i.Operands.Count; k++)
                {
                    if (i.Operands[k] is not RegOperand { Reg: var r } || !derived.Contains(r)) continue;
                    if (!ReadUse(i, k, derived, byName, readOnly)) return false;
                }
        return true;
    }

    /// <summary>
    /// The registers holding `root` or a constant offset of it: copies and
    /// adds of a constant, each written once. `rewritten` when root, or one
    /// of them, is also written by anything else.
    /// </summary>
    private static Dictionary<VReg, int> Writes(Function f)
    {
        Dictionary<VReg, int> writes = new();
        foreach (Block b in f.Blocks)
            foreach (Instr i in b.Instrs)
                if (i.Dest is { } d) writes[d] = writes.GetValueOrDefault(d) + 1;
        return writes;
    }

    private static HashSet<VReg> Derived(Function f, VReg root, Dictionary<VReg, int> writes, out bool rewritten)
    {
        HashSet<VReg> derived = new() { root };
        rewritten = writes.ContainsKey(root);
        for (bool grew = true; grew;)
        {
            grew = false;
            foreach (Block b in f.Blocks)
                foreach (Instr i in b.Instrs)
                {
                    if (i.Dest is not { } d || derived.Contains(d)) continue;
                    bool from = i.Op switch
                    {
                        Opcode.Copy => i.Operands is [RegOperand { Reg: var s }] && derived.Contains(s),
                        Opcode.Add => i.Operands is [RegOperand { Reg: var s }, ImmOperand] && derived.Contains(s)
                                   || i.Operands is [ImmOperand, RegOperand { Reg: var t }] && derived.Contains(t),
                        _ => false,
                    };
                    if (!from) continue;
                    if (writes[d] != 1) rewritten = true;
                    derived.Add(d);
                    grew = true;
                }
        }
        return derived;
    }

    /// <summary>
    /// Whether operand `k` of `i`, an address in `derived`, is only read
    /// there: the address of a load, the source of a copy, the address a
    /// copy or offset of it is made from (Derived follows those), or a
    /// direct call's operand whose parameter is only read.
    /// </summary>
    private static bool ReadUse(Instr i, int k, HashSet<VReg> derived, Dictionary<string, Function> byName, Dictionary<Function, bool[]> readOnly)
    {
        switch (i.Op)
        {
            case Opcode.Load: return k == 0;
            case Opcode.MemCopy: return k == 1;
            case Opcode.Copy:
            case Opcode.Add: return i.Dest is { } d && derived.Contains(d);
            case Opcode.Call:
                return i.Callee is { } name && byName.TryGetValue(name, out Function? callee)
                       && readOnly.TryGetValue(callee, out bool[]? r) && k < r.Length && r[k];
            default: return false;
        }
    }

    // ---- the copies dropped ---------------------------------------------

    private static int Drop(Function f, Dictionary<string, Function> byName, Dictionary<Function, bool[]> readOnly)
    {
        if (f.Async is not null || f.Slots.Count == 0) return 0;
        // Registers that are a slot's address, or that address and a
        // constant, each written once (as SlotScalars reads them).
        Dictionary<VReg, int> writes = new();
        foreach (Block b in f.Blocks)
            foreach (Instr i in b.Instrs)
                if (i.Dest is { } d) writes[d] = writes.GetValueOrDefault(d) + 1;
        Dictionary<VReg, (FrameSlot Slot, long At)> alias = new();
        for (bool more = true; more;)
        {
            more = false;
            foreach (Block b in f.Blocks)
                foreach (Instr i in b.Instrs)
                {
                    if (i.Dest is not { } d || writes[d] != 1 || f.Params.Contains(d) || alias.ContainsKey(d)) continue;
                    if (i.Op == Opcode.Copy && i.Operands is [SlotOperand { Slot: var s }])
                    { alias[d] = (s, 0); more = true; }
                    else if (i.Op == Opcode.Add && i.Operands is [RegOperand { Reg: var r }, ImmOperand { Value: var n }] && alias.TryGetValue(r, out var at))
                    { alias[d] = (at.Slot, at.At + n); more = true; }
                }
        }
        (FrameSlot Slot, long At)? SlotOf(Operand o) => o switch
        {
            SlotOperand { Slot: var s } => (s, 0),
            RegOperand { Reg: var r } when alias.TryGetValue(r, out var at) => at,
            _ => null,
        };

        // Every use of each slot's address, other than making an alias of it.
        Dictionary<FrameSlot, List<(Instr I, int K)>> uses = new(ReferenceEqualityComparer.Instance);
        foreach (Block b in f.Blocks)
            foreach (Instr i in b.Instrs)
            {
                if (i.Dest is { } d && alias.ContainsKey(d)) continue;
                for (int k = 0; k < i.Operands.Count; k++)
                    if (SlotOf(i.Operands[k]) is { } s)
                    {
                        if (!uses.TryGetValue(s.Slot, out var list)) uses[s.Slot] = list = new();
                        list.Add((i, k));
                    }
            }

        // PRIVATE: every use of the slot's address a load, a store into it,
        // a fill, a copy, or a read-only parameter's operand.
        bool Private(FrameSlot s)
        {
            if (!uses.TryGetValue(s, out var list)) return true;
            foreach ((Instr i, int k) in list)
            {
                bool fine = i.Op switch
                {
                    Opcode.Load => k == 0,
                    Opcode.Store => k == 0,
                    Opcode.MemSet => k == 0,
                    Opcode.MemCopy => k is 0 or 1,
                    Opcode.Call => i.Callee is { } name && byName.TryGetValue(name, out Function? callee)
                                   && readOnly.TryGetValue(callee, out bool[]? r) && k < r.Length && r[k],
                    _ => false,
                };
                if (!fine) return false;
            }
            return true;
        }

        int dropped = 0;
        Dictionary<VReg, (HashSet<VReg> Derived, bool Fine)> paramVerdicts = new();
        foreach (Block b in f.Blocks)
        {
            for (int at = 0; at < b.Instrs.Count; at++)
            {
                Instr copy = b.Instrs[at];
                if (copy.Op != Opcode.MemCopy || copy.Offset != 0 || copy.Operands.Count != 3
                    || SlotOf(copy.Operands[0]) is not { At: 0 } to)
                {
                    continue;
                }
                // THE ORIGINAL: a slot of this frame, or this function's own
                // struct parameter passed by value (ValueParams) -- its
                // caller's copy for this call, or a value of the caller's own
                // nothing writes while this call runs.
                (FrameSlot Slot, long At)? fromSlot = SlotOf(copy.Operands[1]);
                // At least as many bytes of it as the copy reads and the
                // callee was handed (a struct of a byte or two in a heap
                // block of its own size, the callee's slot a word).
                VReg? fromParam = copy.Operands[1] is RegOperand { Reg: var pr } && f.ValueParams?.TryGetValue(pr, out int bytes) == true
                    && copy.Operands[2] is ImmOperand { Value: var copied } && copied <= bytes && to.Slot.Bytes <= bytes ? pr : null;
                if (fromSlot is not { At: 0 } && fromParam is null) continue;
                if (fromSlot is { } fs && ReferenceEquals(to.Slot, fs.Slot)) continue;
                // The call it was made for: the next one, with nothing that
                // writes memory or calls in between.
                int c = at + 1;
                while (c < b.Instrs.Count && Quiet(b.Instrs[c])) c++;
                if (c >= b.Instrs.Count || b.Instrs[c] is not { Op: Opcode.Call } call) continue;
                // The copy's slot: this copy and one operand of that call, nothing more.
                if (!uses.TryGetValue(to.Slot, out var toUses) || toUses.Count != 2
                    || !toUses.Any(u => ReferenceEquals(u.I, copy) && u.K == 0))
                {
                    continue;
                }
                int argument = -1;
                foreach ((Instr i, int k) in toUses)
                    if (ReferenceEquals(i, call)) argument = k;
                if (argument < 0 || SlotOf(call.Operands[argument]) is not { At: 0 }) continue;
                if (call.Callee is not { } name || !byName.TryGetValue(name, out Function? callee)
                    || !readOnly.TryGetValue(callee, out bool[]? r) || argument >= r.Length || !r[argument])
                {
                    continue;
                }
                if (fromSlot is { } from)
                {
                    // The callee was made for the copy's alignment (an aligned
                    // slot's MOVAPS): the original at least as aligned.
                    if (from.Slot.Align < to.Slot.Align || from.Slot.Bytes < to.Slot.Bytes) continue;
                    if (!Private(from.Slot)) continue;
                    // No other operand of the call reaches the original.
                    bool other = false;
                    for (int k = 0; k < call.Operands.Count; k++)
                        if (k != argument && SlotOf(call.Operands[k]) is { } o && ReferenceEquals(o.Slot, from.Slot)) other = true;
                    if (other) continue;
                }
                else
                {
                    // A parameter's storage: only read, written, filled or
                    // copied here, or handed to a parameter only read; never
                    // written as a register; no other operand of the call
                    // made of it.
                    // Judged once a parameter: handing it to a parameter only
                    // read leaves the verdict as it was.
                    if (!paramVerdicts.TryGetValue(fromParam!, out var verdict))
                    {
                        HashSet<VReg> d = Derived(f, fromParam!, writes, out bool rewritten);
                        verdict = (d, !rewritten && ParamPrivate(f, d, byName, readOnly));
                        paramVerdicts[fromParam!] = verdict;
                    }
                    if (!verdict.Fine) continue;
                    HashSet<VReg> derived = verdict.Derived;
                    bool other = false;
                    for (int k = 0; k < call.Operands.Count; k++)
                        if (k != argument && call.Operands[k] is RegOperand { Reg: var or } && derived.Contains(or)) other = true;
                    if (other) continue;
                }

                call.Operands[argument] = copy.Operands[1];
                b.Instrs.RemoveAt(at);
                at--;
                dropped++;
                // The uses changed: the copy's slot is gone from them, the
                // original has one more (still a read-only operand).
                uses.Remove(to.Slot);
                if (fromSlot is { } kept)
                {
                    if (!uses.TryGetValue(kept.Slot, out var fromUses)) uses[kept.Slot] = fromUses = new();
                    fromUses.RemoveAll(u => ReferenceEquals(u.I, copy));
                    fromUses.Add((call, argument));
                }
            }
        }
        // Before the call slots, which hand a destination to a callee as its
        // buffer: no longer private after, though built in place before.
        dropped += Forwarded(f, alias, uses, Private);
        dropped += CallSlots(f, byName, readOnly, SlotOf, uses, Private);
        if (dropped > 0) Unnamed(f);
        return dropped;
    }

    /// <summary>
    /// THE RESULT WRITTEN WHERE IT GOES (LLVM's call slot optimisation): a
    /// struct a call answers is written into the caller's result buffer and
    /// then copied to where it goes, `d = F(..)` or `P d = F(..)`; the
    /// destination's own storage is handed over as the buffer instead, and
    /// the copy is not made. Only where
    ///   - the callee answers its buffer (every `ret` is its last parameter),
    ///     so what the copy read is what the callee wrote;
    ///   - the destination is a slot of this frame whose address goes
    ///     nowhere else (Private), and no other operand of the call reaches
    ///     it -- the callee writes the buffer while it may still read its
    ///     arguments;
    ///   - the buffer's slot is named by the call alone, and the call's
    ///     answer only by the copy;
    ///   - between the call and the copy nothing writes memory, calls or
    ///     names the destination;
    ///   - the function has no handler: a call that throws after writing
    ///     part of its buffer would have changed a variable C# leaves alone,
    ///     and a handler may read it.
    /// </summary>
    private static int CallSlots(Function f, Dictionary<string, Function> byName, Dictionary<Function, bool[]> readOnly,
                                 Func<Operand, (FrameSlot Slot, long At)?> slotOf, Dictionary<FrameSlot, List<(Instr I, int K)>> uses,
                                 Func<FrameSlot, bool> privateSlot)
    {
        if (f.Blocks.Any(b => b.IsLandingPad)) return 0;
        Dictionary<VReg, int> reads = new();
        Dictionary<VReg, int> written = Writes(f);
        foreach (Block b in f.Blocks)
            foreach (Instr i in b.Instrs)
                foreach (Operand o in i.Operands)
                    if (o is RegOperand { Reg: var r }) reads[r] = reads.GetValueOrDefault(r) + 1;
        int done = 0;
        foreach (Block b in f.Blocks)
        {
            for (int at = 0; at < b.Instrs.Count; at++)
            {
                Instr call = b.Instrs[at];
                if (call.Op != Opcode.Call || call.Dest is not { } answer || call.Operands.Count == 0
                    || call.Callee is not { } name || !byName.TryGetValue(name, out Function? callee)
                    || callee.Params.Count != call.Operands.Count || !AnswersBuffer(callee))
                {
                    continue;
                }
                int last = call.Operands.Count - 1;
                if (slotOf(call.Operands[last]) is not { At: 0 } buffer
                    || !uses.TryGetValue(buffer.Slot, out var bufferUses) || bufferUses.Count != 1 || reads.GetValueOrDefault(answer) != 1
                    || written.GetValueOrDefault(answer) != 1)
                {
                    continue;
                }
                // The copy out of the answer, after nothing that writes, calls or names a slot.
                int c = at + 1;
                while (c < b.Instrs.Count && Quiet(b.Instrs[c]) && !b.Instrs[c].Operands.Any(o => slotOf(o) is not null)) c++;
                if (c >= b.Instrs.Count || b.Instrs[c] is not { Op: Opcode.MemCopy, Offset: 0 } copy || copy.Operands.Count != 3
                    || copy.Operands[1] is not RegOperand { Reg: var source } || !ReferenceEquals(source, answer)
                    || slotOf(copy.Operands[0]) is not { At: 0 } into || ReferenceEquals(into.Slot, buffer.Slot)
                    || copy.Operands[2] is not ImmOperand { Value: var length } || length > into.Slot.Bytes || into.Slot.Bytes < buffer.Slot.Bytes)
                {
                    continue;
                }
                if (!privateSlot(into.Slot) || into.Slot.Align < buffer.Slot.Align) continue;
                bool other = false;
                for (int k = 0; k < last; k++)
                    if (slotOf(call.Operands[k]) is { } o && ReferenceEquals(o.Slot, into.Slot)) other = true;
                if (other) continue;

                call.Operands[last] = copy.Operands[0];
                b.Instrs.RemoveAt(c);
                done++;
                uses.Remove(buffer.Slot);
                if (uses.TryGetValue(into.Slot, out var intoUses))
                {
                    intoUses.RemoveAll(u => ReferenceEquals(u.I, copy));
                    // Written by the callee now: no longer a read-only use,
                    // so a later copy here does not take it for private.
                    intoUses.Add((call, last));
                }
            }
        }
        return done;
    }

    /// <summary>
    /// A VALUE BUILT WHERE IT GOES: `P p = new P(a, b)`, `p = default`, a
    /// struct built up in a temporary of the frame (StructInFrame: zeroed,
    /// its constructor inlined into it -- a constructor still called is
    /// handed the temporary, a parameter it writes, and the temporary is
    /// then not private) and then copied whole into the variable's own
    /// storage. The temporary is the destination instead, and the copy
    /// is not made, where
    ///   - the temporary is a frame value (Lowering.FrameValueSlot) whose
    ///     address goes nowhere else (Private), every use of it in one
    ///     block before the copy (its aliases' too), so nothing reads it
    ///     after, and the first of them a fill or copy of all its bytes;
    ///   - the destination is a slot of this frame whose address goes
    ///     nowhere else (Private), of the temporary's size, copied over all
    ///     of it, and no less aligned, and nothing from the temporary's
    ///     first use to the copy names it but the making of an alias -- its
    ///     old value is not read in between;
    ///   - the function has no handler: a constructor that throws after
    ///     writing part of the value would have changed a variable C#
    ///     leaves alone.
    /// </summary>
    private static int Forwarded(Function f, Dictionary<VReg, (FrameSlot Slot, long At)> alias,
                                 Dictionary<FrameSlot, List<(Instr I, int K)>> uses, Func<FrameSlot, bool> privateSlot)
    {
        if (f.Blocks.Any(b => b.IsLandingPad)) return 0;
        (FrameSlot Slot, long At)? SlotOf(Operand o) => o switch
        {
            SlotOperand { Slot: var s } => (s, 0),
            RegOperand { Reg: var r } when alias.TryGetValue(r, out var at) => at,
            _ => null,
        };
        // Nothing to look at unless some copy is out of a temporary.
        if (!f.Blocks.Any(b => b.Instrs.Any(i => i.Op == Opcode.MemCopy && i.Operands.Count == 3
                && SlotOf(i.Operands[1]) is { Slot.Name: Corsac.Lang.Lower.Lowering.FrameValueSlot })))
            return 0;
        // Every instruction that reads an alias register: an alias of the
        // temporary used anywhere but the window keeps it.
        Dictionary<VReg, List<Instr>> readers = new();
        foreach (Block b in f.Blocks)
            foreach (Instr i in b.Instrs)
                foreach (Operand o in i.Operands)
                    if (o is RegOperand { Reg: var r } && alias.ContainsKey(r))
                    {
                        if (!readers.TryGetValue(r, out var list)) readers[r] = list = new();
                        list.Add(i);
                    }
        int done = 0;
        foreach (Block b in f.Blocks)
        {
            for (int at = 0; at < b.Instrs.Count; at++)
            {
                Instr copy = b.Instrs[at];
                if (copy.Op != Opcode.MemCopy || copy.Offset != 0 || copy.Operands.Count != 3
                    || SlotOf(copy.Operands[0]) is not { At: 0 } into || SlotOf(copy.Operands[1]) is not { At: 0 } from
                    || ReferenceEquals(into.Slot, from.Slot) || from.Slot.Name != Corsac.Lang.Lower.Lowering.FrameValueSlot
                    || copy.Operands[2] is not ImmOperand { Value: var length }
                    || into.Slot.Bytes != from.Slot.Bytes || into.Slot.Align < from.Slot.Align || length != into.Slot.Bytes)
                {
                    continue;
                }
                if (!uses.TryGetValue(from.Slot, out var fromUses)) continue;
                // Every use of the temporary in this block, before the copy.
                int first = at;
                bool here = true;
                foreach ((Instr i, int _) in fromUses)
                {
                    int position = ReferenceEquals(i, copy) ? at : b.Instrs.IndexOf(i);
                    if (position < 0 || position > at) { here = false; break; }
                    first = Math.Min(first, position);
                }
                if (!here) continue;
                // A TEMPORARY, MADE WHOLE AT ITS FIRST USE: zeroed or copied
                // into over all its bytes -- not a variable's own storage
                // (StructHome, of the same name) whose old value a read
                // before it is written would want.
                Instr made = b.Instrs[first];
                if (made.Op is not (Opcode.MemSet or Opcode.MemCopy) || made.Operands.Count != 3
                    || SlotOf(made.Operands[0]) is not { At: 0 } madeInto || !ReferenceEquals(madeInto.Slot, from.Slot)
                    || made.Operands[2] is not ImmOperand { Value: var madeLength } || madeLength != from.Slot.Bytes)
                {
                    continue;
                }
                // The temporary's alias registers read only in that span.
                bool aliasesHere = true;
                foreach ((VReg r, var target) in alias)
                {
                    if (!ReferenceEquals(target.Slot, from.Slot) || !readers.TryGetValue(r, out var readBy)) continue;
                    foreach (Instr i in readBy)
                    {
                        if (alias.ContainsKey(i.Dest ?? r) && i.Dest is not null && i.Op is Opcode.Copy or Opcode.Add) continue;
                        int position = b.Instrs.IndexOf(i);
                        if (position < first || position > at) { aliasesHere = false; break; }
                    }
                    if (!aliasesHere) break;
                }
                if (!aliasesHere) continue;
                // The destination: private, and untouched in the span but
                // for the making of an alias of it.
                // Both private: a temporary whose address went elsewhere would
                // leave whoever kept it pointing at the destination.
                if (!privateSlot(into.Slot) || !privateSlot(from.Slot)) continue;
                bool touched = false;
                for (int k = first; k < at && !touched; k++)
                {
                    Instr i = b.Instrs[k];
                    if (i.Dest is { } d && alias.ContainsKey(d)) continue;
                    foreach (Operand o in i.Operands)
                        if (SlotOf(o) is { } named && ReferenceEquals(named.Slot, into.Slot)) touched = true;
                }
                if (touched) continue;

                // The temporary becomes the destination: every naming of its
                // slot, and the alias registers made of it, now the other's.
                foreach (Block c in f.Blocks)
                    foreach (Instr i in c.Instrs)
                        for (int k = 0; k < i.Operands.Count; k++)
                            if (i.Operands[k] is SlotOperand { Slot: var s } && ReferenceEquals(s, from.Slot))
                                i.Operands[k] = new SlotOperand(into.Slot);
                foreach (VReg r in alias.Keys.ToList())
                    if (ReferenceEquals(alias[r].Slot, from.Slot)) alias[r] = (into.Slot, alias[r].At);
                b.Instrs.RemoveAt(at);
                at--;
                done++;
                uses.Remove(from.Slot);
                if (uses.TryGetValue(into.Slot, out var intoUses))
                {
                    intoUses.RemoveAll(u => ReferenceEquals(u.I, copy));
                    intoUses.AddRange(fromUses.Where(u => !ReferenceEquals(u.I, copy)));
                }
            }
        }
        return done;
    }

    /// <summary>
    /// THE SLOTS NO LONGER NAMED, AND THEIR ALIASES, GONE: a dropped copy's
    /// slot and a dropped buffer are named after it only by the `r = copy
    /// &slot` (or offsets of it) nothing reads, which is no cleanup's to
    /// remove where SlotScalars made no slot registers.
    /// </summary>
    private static void Unnamed(Function f)
    {
        for (bool more = true; more;)
        {
            more = false;
            Dictionary<VReg, int> reads = new();
        Dictionary<VReg, int> written = Writes(f);
            foreach (Block b in f.Blocks)
                foreach (Instr i in b.Instrs)
                    foreach (Operand o in i.Operands)
                        if (o is RegOperand { Reg: var r }) reads[r] = reads.GetValueOrDefault(r) + 1;
            foreach (Block b in f.Blocks)
                more |= b.Instrs.RemoveAll(i => i.Dest is { } d && !reads.ContainsKey(d) && !f.Params.Contains(d)
                    && (i.Op == Opcode.Copy && i.Operands is [SlotOperand] || i.Op == Opcode.Add && i.Operands is [RegOperand, ImmOperand])) > 0;
        }
        HashSet<FrameSlot> named = new(ReferenceEqualityComparer.Instance);
        foreach (Block b in f.Blocks)
            foreach (Instr i in b.Instrs)
                foreach (Operand o in i.Operands)
                    if (o is SlotOperand { Slot: var s }) named.Add(s);
        f.Slots.RemoveAll(s => !named.Contains(s));
    }

    /// <summary>The name lowering gives a method's result buffer parameter.</summary>
    private const string ResultBufferName = "retbuf";

    /// <summary>Whether every `ret` of the function answers its last parameter, the result buffer.</summary>
    private static bool AnswersBuffer(Function f)
    {
        // THE RESULT BUFFER LOWERING GAVE IT (Lowering.Method: `retbuf`), not
        // any last parameter a function happens to answer -- a pointer
        // passed in and handed back is not a buffer the callee fills.
        if (f.Params.Count == 0 || f.Async is not null || f.Params[^1].Name != ResultBufferName) return false;
        // The buffer itself and plain copies of it, each written once --
        // not an offset of it, which Derived would follow too.
        VReg buffer = f.Params[^1];
        Dictionary<VReg, int> writes = new();
        foreach (Block b in f.Blocks)
            foreach (Instr i in b.Instrs)
                if (i.Dest is { } d) writes[d] = writes.GetValueOrDefault(d) + 1;
        if (writes.ContainsKey(buffer)) return false;
        HashSet<VReg> same = new() { buffer };
        for (bool grew = true; grew;)
        {
            grew = false;
            foreach (Block b in f.Blocks)
                foreach (Instr i in b.Instrs)
                    if (i.Op == Opcode.Copy && i.Dest is { } d && !same.Contains(d) && writes[d] == 1
                        && i.Operands is [RegOperand { Reg: var s }] && same.Contains(s))
                    {
                        same.Add(d);
                        grew = true;
                    }
        }
        bool any = false;
        foreach (Block b in f.Blocks)
            foreach (Instr i in b.Instrs)
                if (i.Op == Opcode.Ret)
                {
                    if (i.Operands is not [RegOperand { Reg: var r }] || !same.Contains(r)) return false;
                    any = true;
                }
        return any;
    }

    /// <summary>An instruction that neither writes memory nor calls: arithmetic, a comparison, a conversion, a copy, a load.</summary>
    private static bool Quiet(Instr i) => i.Op is >= Opcode.Copy and <= Opcode.Bits or Opcode.Load;
}
