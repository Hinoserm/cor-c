#nullable enable
using Corsac.Lang.Ir;

namespace Corsac.Lang.Opt;

using Block = Corsac.Lang.Ir.Block;

/// <summary>
/// OWNED VARIABLES: a variable that is assigned again and again -- `s = s +
/// part`, `x = Grow(x)` -- gives back what it held each time it is assigned.
///
/// A fresh object copied into a variable that has other definitions too is
/// lost to the fresh-return and allocation rules, which follow only single
/// definitions: the register may hold another object on another path, so
/// nothing about "this" object can be said through it. But when EVERY value
/// the variable is ever given is null, static data (a string literal, which is
/// no heap block), an object this pass put in the frame (no heap block either),
/// or a fresh object -- an allocation or a fresh function's result -- and nothing the variable holds escapes, then the variable owns
/// whatever it holds: at each assignment the previous value is given back
/// (Runtime.FreeReplaced, which ignores the same object assigned twice, and
/// Free ignores a null or a literal), and on every return the last one.
///
/// Where the previous value dies must be where the assignment is: nothing
/// that could still hold it may be live just after the assignment -- only
/// the variable and the register the new value came from. A loop that keeps
/// last time's value in another register is refused.
/// </summary>
public sealed partial class Escape
{
    /// <summary>Runtime.FreeReplaced(long old, long current).</summary>
    public const string ReplacedFreer = Corsac.Lang.Lto.RuntimeAbi.FreeReplaced;

    /// <summary>How many reassigned variables this pass took ownership of.</summary>
    public int VariablesOwned { get; private set; }

    private void OwnVariables(Function f, Dictionary<string, bool[]> summaries)
    {
        if (f.Async is not null) return;
        int word = IrTypes.Word.Bytes();
        Dictionary<VReg, List<(Block Block, Instr Def)>> writers = new();
        foreach (Block b in f.Blocks)
            foreach (Instr i in b.Instrs)
                if (i.Dest is not null)
                {
                    if (!writers.TryGetValue(i.Dest, out var list)) writers[i.Dest] = list = new();
                    list.Add((b, i));
                }
        Defs defs = new(f, buildCfg: false);
        Liveness? liveness = null;

        foreach ((VReg v, List<(Block Block, Instr Def)> list) in writers)
        {
            if (list.Count < 2 || v.Type != IrTypes.Word || f.Params.Contains(v)) continue;
            // Every assignment a copy of null, static data, or a fresh object.
            List<Instr> origins = new();
            // Origins made by functions whose answer waits on another unit:
            // owned at the link if those are fresh (EscapeHints).
            List<string> waiting = new();
            Dictionary<Instr, HashSet<VReg>> carried = new(ReferenceEqualityComparer.Instance);
            // Assignments of an object this pass put in the frame (a promoted
            // allocation): no heap block, so nothing to give back -- the
            // shadow is told null there, and the next assignment frees nothing.
            HashSet<Instr> framed = new(ReferenceEqualityComparer.Instance);
            bool ok = true;
            foreach ((Block _, Instr d) in list)
            {
                if (d.Op != Opcode.Copy || d.Operands.Count != 1 || _bookkeeping.Contains(d)) { ok = false; break; }
                HashSet<VReg> chain = new() { v };
                Operand o = d.Operands[0];
                for (int hops = 0; hops < 8 && ok; hops++)
                {
                    if (o is ImmOperand { Value: 0 } || o is SymOperand) break;
                    if (o is not RegOperand r || !defs.IsSingle(r.Reg) || defs.Definition(r.Reg) is not Instr from) { ok = false; break; }
                    chain.Add(r.Reg);
                    if (from.Op == Opcode.Call && (IsAllocator(from.Callee) || IsFreshCall(from)))
                    {
                        if (_owned.Contains(from) || _ownedCalls.Contains(from)) ok = false;
                        else origins.Add(from);
                        break;
                    }
                    if (_hinting && from.Op == Opcode.Call && from.Callee is not null && from.Dest is not null
                        && !IsCollectorNote(from.Callee) && !_bookkeeping.Contains(from))
                    {
                        if (_owned.Contains(from) || _ownedCalls.Contains(from)) ok = false;
                        else { origins.Add(from); waiting.Add(from.Callee); }
                        break;
                    }
                    if (from.Op is not (Opcode.Copy or Opcode.Trunc64 or Opcode.ZExt32) || from.Operands.Count != 1) { ok = false; break; }
                    // A PROMOTED OBJECT: `x = new long[1]` before a loop of `x =
                    // Grow(x)`, proved to live no longer than the frame once x
                    // is followed as a borrow of it. The heap objects assigned
                    // after it are owned all the same.
                    if (from.Operands[0] is SlotOperand)
                    {
                        if (_promotedMade.Contains(from)) framed.Add(d);
                        else ok = false;
                        break;
                    }
                    o = from.Operands[0];
                }
                carried[d] = chain;
                if (!ok) break;
            }
            if (!ok || origins.Count == 0) continue;

            // Nothing it holds escapes -- asked for the link as well, when the
            // unit leaves hints: calls into other units become conditions.
            Needs? needs = _hinting ? new(this) : null;
            foreach (string callee in waiting)
                if (needs is null || !needs.AllowFresh(callee)) { needs = null; break; }
            if (waiting.Count > 0 && needs is null) continue;
            List<VReg> roots = new() { v };
            foreach (Instr origin in origins) roots.Add(origin.Dest!);
            Flow flow = Analyse(f, roots, summaries, null, joinable: new HashSet<VReg> { v }, needs: needs);
            if (flow.Escapes) continue;
            bool waits = needs is { Condition.IsTrue: false };

            // The previous value dead at every assignment -- and never read
            // by a handler, which an exception can reach from anywhere after
            // the assignment (Escape.PadLive).
            liveness ??= new Liveness(f);
            HashSet<VReg> pads = PadLive(liveness);
            bool live = false;
            foreach (VReg r in flow.Derived)
                if (pads.Contains(r) && !ReferenceEquals(r, v)) { live = true; break; }
            foreach ((Block b, Instr d) in list)
            {
                HashSet<VReg> allowed = carried[d];
                foreach ((Instr i, ulong[] after) in liveness.WalkBackwards(b))
                {
                    if (!ReferenceEquals(i, d)) continue;
                    foreach (VReg r in flow.Derived)
                        if (!allowed.Contains(r) && Liveness.Test(after, r.Id)) { live = true; break; }
                    break;
                }
                if (live) break;
            }
            if (live) continue;
            // Every proof this unit can give holds, but some of what it rests
            // on is another unit's: the link decides.
            if (waits)
            {
                AddPending(needs!.Condition);
                continue;
            }

            // The variable's shadow: what it held, for the free at the next
            // assignment and at every return.
            FrameSlot slot = f.NewSlot(word, word, "ownedvar");
            int line = EntryLine(f, list[0].Def.Line);
            VReg entryAddr = f.NewReg(IrTypes.Word, "ownedvarp");
            List<Instr> entry = new()
            {
                new Instr { Op = Opcode.Copy, Dest = entryAddr, Operands = { new SlotOperand(slot) }, Line = line },
                new Instr { Op = Opcode.Store, Size = word, Operands = { new RegOperand(entryAddr), new ImmOperand(0, IrTypes.Word) }, Line = line },
            };
            f.Entry.Instrs.InsertRange(0, entry);
            _bookkeeping.UnionWith(entry);

            foreach ((Block b, Instr d) in list)
            {
                int at = b.Instrs.IndexOf(d);
                VReg addr = f.NewReg(IrTypes.Word, "ownedvarp");
                VReg old = f.NewReg(IrTypes.Word, "ownedvar");
                List<Instr> after = new()
                {
                    new Instr { Op = Opcode.Copy, Dest = addr, Operands = { new SlotOperand(slot) }, Line = d.Line },
                    new Instr { Op = Opcode.Load, Size = word, Dest = old, Operands = { new RegOperand(addr) }, Line = d.Line },
                };
                VReg oldArg = Widen(f, after, old, d.Line), curArg = Widen(f, after, v, d.Line);
                after.Add(new Instr { Op = Opcode.Call, Callee = ReplacedFreer, Operands = { new RegOperand(oldArg), new RegOperand(curArg) }, Line = d.Line });
                Operand shadow = framed.Contains(d) ? new ImmOperand(0, IrTypes.Word) : new RegOperand(v);
                after.Add(new Instr { Op = Opcode.Store, Size = word, Operands = { new RegOperand(addr), shadow }, Line = d.Line });
                b.Instrs.InsertRange(at + 1, after);
                _bookkeeping.UnionWith(after);
            }

            foreach (Block b in f.Blocks)
            {
                if (b.Terminator is not { Op: Opcode.Ret }) continue;
                VReg a = f.NewReg(IrTypes.Word, "ownedvarp");
                VReg p = f.NewReg(IrTypes.Word, "ownedvar");
                int exitLine = b.Instrs[^1].Line;
                List<Instr> release = new()
                {
                    new Instr { Op = Opcode.Copy, Dest = a, Operands = { new SlotOperand(slot) }, Line = exitLine },
                    new Instr { Op = Opcode.Load, Size = word, Dest = p, Operands = { new RegOperand(a) }, Line = exitLine },
                };
                AppendFree(f, release, p, exitLine);
                b.Instrs.InsertRange(b.Instrs.Count - 1, release);
                _bookkeeping.UnionWith(release);
            }

            Owned++;
            VariablesOwned++;
            liveness = null;
            defs = new(f, buildCfg: false);
        }
    }

    private static VReg Widen(Function f, List<Instr> output, VReg r, int line) => Word(f, output, r, line, "freeAddress");
}
