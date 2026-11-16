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
///     the call. A parameter or a pointer into the heap is not such a slot:
///     another path to its bytes may write them while the callee reads them.
///   - NOTHING BETWEEN THE COPY AND THE CALL WRITES MEMORY OR CALLS, and no
///     other operand of the call is the original's address (`F(acc, ref
///     acc)` must still copy).
///   - THE COPY'S SLOT IS USED BY NOTHING ELSE: the copy and the one operand.
/// Run late (Pipeline.RunLate), after the last inliner and before
/// SlotScalars, over what is left as real calls.
/// </summary>
public static class ArgumentCopies
{
    /// <summary>The copies dropped in the last run, for the tests.</summary>
    public static int Dropped { get; private set; }

    public static void Run(Module m)
    {
        Dropped = 0;
        Dictionary<string, Function> byName = new(StringComparer.Ordinal);
        foreach (Function f in m.Functions) byName.TryAdd(f.Name, f);
        Dictionary<Function, bool[]> readOnly = ReadOnly(m, byName);
        foreach (Function f in m.Functions) Dropped += Drop(f, byName, readOnly);
    }

    // ---- which parameters a function only reads --------------------------

    private static Dictionary<Function, bool[]> ReadOnly(Module m, Dictionary<string, Function> byName)
    {
        Dictionary<Function, bool[]> answer = new(ReferenceEqualityComparer.Instance);
        foreach (Function f in m.Functions)
        {
            bool[] r = new bool[f.Params.Count];
            // An async or iterator body's frame is a block of the heap, and
            // what it reads may be read after a suspension: never assumed.
            if (f.Async is null) Array.Fill(r, true);
            answer[f] = r;
        }
        for (bool changed = true; changed;)
        {
            changed = false;
            foreach (Function f in m.Functions)
            {
                bool[] r = answer[f];
                for (int p = 0; p < r.Length; p++)
                    if (r[p] && !OnlyRead(f, f.Params[p], byName, answer)) { r[p] = false; changed = true; }
            }
        }
        return answer;
    }

    /// <summary>Whether a function's register `root` -- a parameter, never written again -- is only read through.</summary>
    private static bool OnlyRead(Function f, VReg root, Dictionary<string, Function> byName, Dictionary<Function, bool[]> readOnly)
    {
        HashSet<VReg> derived = Derived(f, root, out bool rewritten);
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
    private static HashSet<VReg> Derived(Function f, VReg root, out bool rewritten)
    {
        HashSet<VReg> derived = new() { root };
        Dictionary<VReg, int> writes = new();
        foreach (Block b in f.Blocks)
            foreach (Instr i in b.Instrs)
                if (i.Dest is { } d) writes[d] = writes.GetValueOrDefault(d) + 1;
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
        if (f.Async is not null || f.Slots.Count < 2) return 0;
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
        foreach (Block b in f.Blocks)
        {
            for (int at = 0; at < b.Instrs.Count; at++)
            {
                Instr copy = b.Instrs[at];
                if (copy.Op != Opcode.MemCopy || copy.Offset != 0 || copy.Operands.Count != 3
                    || SlotOf(copy.Operands[0]) is not { At: 0 } to || SlotOf(copy.Operands[1]) is not { At: 0 } from
                    || ReferenceEquals(to.Slot, from.Slot))
                {
                    continue;
                }
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
                if (!Private(from.Slot)) continue;
                // No other operand of the call reaches the original.
                bool other = false;
                for (int k = 0; k < call.Operands.Count; k++)
                    if (k != argument && SlotOf(call.Operands[k]) is { } o && ReferenceEquals(o.Slot, from.Slot)) other = true;
                if (other) continue;

                call.Operands[argument] = copy.Operands[1];
                b.Instrs.RemoveAt(at);
                at--;
                dropped++;
                // The uses changed: the copy's slot is gone from them, the
                // original has one more (still a read-only operand).
                uses.Remove(to.Slot);
                if (!uses.TryGetValue(from.Slot, out var fromUses)) uses[from.Slot] = fromUses = new();
                fromUses.RemoveAll(u => ReferenceEquals(u.I, copy));
                fromUses.Add((call, argument));
            }
        }
        // The copy's slot, named now only by an alias nothing reads, goes
        // with the cleanup after (SlotScalars.ApplyAndClean's dead code),
        // and a slot nothing names takes no bytes of the frame.
        return dropped;
    }

    /// <summary>An instruction that neither writes memory nor calls: arithmetic, a comparison, a conversion, a copy, a load.</summary>
    private static bool Quiet(Instr i) => i.Op is >= Opcode.Copy and <= Opcode.Bits or Opcode.Load;
}
