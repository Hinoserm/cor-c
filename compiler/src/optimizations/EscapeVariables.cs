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
///
/// What a lap makes for the variable and then drops -- `t = r.Copy(); if
/// (done) break; r = t;` -- is given back where it dies (FreeDropped): the
/// variable's shadow says whether it was the variable's to give back.
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
        // ONLY THE REGISTERS THE LOOP BELOW TAKES: a word written twice or
        // more that is no parameter. Every register's writes were gathered,
        // a list each, for a loop that passes over all but a few -- a
        // tenth of what the lifetime pass allocated at the link. Counted
        // first by number, the few are gathered in the order they are first
        // written, the order the loop took them in.
        int[] written = new int[f.RegCount];
        foreach (Block b in f.Blocks)
            foreach (Instr i in b.Instrs)
                if (i.Dest is { } d && d.Id < written.Length) written[d.Id]++;
        Dictionary<VReg, List<(Block Block, Instr Def)>> writers = new();
        foreach (Block b in f.Blocks)
            foreach (Instr i in b.Instrs)
                if (i.Dest is { } d && (d.Id >= written.Length || written[d.Id] >= 2) && d.Type == IrTypes.Word && !f.Params.Contains(d))
                {
                    if (!writers.TryGetValue(d, out var list)) writers[d] = list = new();
                    list.Add((b, i));
                }
        Defs defs = AnalysisCache.DefsOf(f, buildCfg: false);
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
            liveness ??= AnalysisCache.LivenessOf(f);
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
                new Instr { Op = Opcode.Store, Size = word, Operands = { RegOperand.Of(entryAddr), new ImmOperand(0, IrTypes.Word) }, Line = line },
            };
            f.Entry.Instrs.InsertRange(0, entry);
            _bookkeeping.UnionWith(entry);

            // Each assignment's bookkeeping, until whose store the shadow
            // still names the variable's previous object (FreeDropped).
            HashSet<Instr> told = new(ReferenceEqualityComparer.Instance);
            foreach ((Block b, Instr d) in list)
            {
                int at = b.Instrs.IndexOf(d);
                VReg addr = f.NewReg(IrTypes.Word, "ownedvarp");
                VReg old = f.NewReg(IrTypes.Word, "ownedvar");
                List<Instr> after = new()
                {
                    new Instr { Op = Opcode.Copy, Dest = addr, Operands = { new SlotOperand(slot) }, Line = d.Line },
                    new Instr { Op = Opcode.Load, Size = word, Dest = old, Operands = { RegOperand.Of(addr) }, Line = d.Line },
                };
                VReg oldArg = Widen(f, after, old, d.Line), curArg = Widen(f, after, v, d.Line);
                after.Add(new Instr { Op = Opcode.Call, Callee = ReplacedFreer, Operands = { RegOperand.Of(oldArg), RegOperand.Of(curArg) }, Line = d.Line });
                Operand shadow = framed.Contains(d) ? new ImmOperand(0, IrTypes.Word) : RegOperand.Of(v);
                after.Add(new Instr { Op = Opcode.Store, Size = word, Operands = { RegOperand.Of(addr), shadow }, Line = d.Line });
                b.Instrs.InsertRange(at + 1, after);
                _bookkeeping.UnionWith(after);
                told.UnionWith(after);
            }

            OneReturn(f);
            foreach (Block b in f.Blocks)
            {
                if (b.Terminator is not { Op: Opcode.Ret }) continue;
                VReg a = f.NewReg(IrTypes.Word, "ownedvarp");
                VReg p = f.NewReg(IrTypes.Word, "ownedvar");
                int exitLine = b.Instrs[^1].Line;
                List<Instr> release = new()
                {
                    new Instr { Op = Opcode.Copy, Dest = a, Operands = { new SlotOperand(slot) }, Line = exitLine },
                    new Instr { Op = Opcode.Load, Size = word, Dest = p, Operands = { RegOperand.Of(a) }, Line = exitLine },
                };
                AppendFree(f, release, p, exitLine);
                b.Instrs.InsertRange(b.Instrs.Count - 1, release);
                _bookkeeping.UnionWith(release);
            }

            FreeDropped(f, v, slot, origins, carried, told, flow.Derived);
            Owned++;
            VariablesOwned++;
            liveness = null;
            defs = AnalysisCache.DefsOf(f, buildCfg: false);
        }
    }

    /// <summary>
    /// ONE WAY OUT FOR THE FREES AT A RETURN: a function with three returns or
    /// more has them jump to one block that returns, its value carried in a
    /// register, before a variable's frees are put at each. Every owned
    /// variable's slot is freed at every return; at each of them, a
    /// dispatcher of a hundred and thirty-seven returns and forty-five owned
    /// variables was six thousand frees, 180 KB of the GUI module's code
    /// apiece (EmfPlayer.Play and Run). Once, it is forty-five, and a return
    /// is a jump longer.
    /// </summary>
    private static void OneReturn(Function f)
    {
        List<Block> returning = f.Blocks.Where(b => b.Terminator is { Op: Opcode.Ret }).ToList();
        if (returning.Count < 3 || returning.Any(b => b.Terminator!.Operands.Count > 1)) return;
        bool valued = returning[0].Terminator!.Operands.Count == 1;
        if (returning.Any(b => (b.Terminator!.Operands.Count == 1) != valued)) return;
        VReg? result = valued ? f.NewReg(f.Returns, "returned") : null;
        Block exit = f.NewBlock("return");
        int line = returning[^1].Terminator!.Line;
        Instr ret = new() { Op = Opcode.Ret, Line = line };
        if (result is not null) ret.Operands.Add(RegOperand.Of(result));
        exit.Instrs.Add(ret);
        foreach (Block b in returning)
        {
            Instr old = b.Instrs[^1];
            b.Instrs.RemoveAt(b.Instrs.Count - 1);
            if (result is not null) b.Instrs.Add(new Instr { Op = Opcode.Copy, Dest = result, Operands = { old.Operands[0] }, Line = old.Line });
            b.Instrs.Add(new Instr { Op = Opcode.Jump, InitialTargets = new[] { exit }, Line = old.Line });
        }
    }

    private static VReg Widen(Function f, List<Instr> output, VReg r, int line) => Word(f, output, r, line, "freeAddress");

    /// <summary>
    /// The allocations and fresh results PromoteIn and OwnFreshResults left
    /// alone only because an earlier lap's object was still live where they
    /// are made: what a variable carrying that earlier object may own
    /// (FreeDropped).
    /// </summary>
    private readonly HashSet<Instr> _liveAtMaking = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// A FRESH OBJECT A LAP MAKES FOR AN OWNED VARIABLE AND DROPS, given back
    /// where it dies. FloatDecimal.Digits climbs `tenfold = r.Copy(); ...; if
    /// (Compare(tenfold, s) >= 0) break; r = tenfold;`: the variable owns
    /// what it is given, but the lap that breaks never gives it its object,
    /// and the allocation itself was refused -- the last lap's object, the
    /// variable's now, is live where this lap's is made. Every double
    /// formatted with F, N or E left one to the collector that way.
    ///
    /// So each such origin of `v` is given back where it and every register
    /// that is it -- its copies, width changes, addresses inside it, what
    /// hands it back (ReturnsFirst) -- are dead, the variable apart:
    /// Runtime.FreeReplaced(it, shadow), which frees it unless the variable's
    /// shadow names it. Where `v = t` ran the variable holds it, and gives it
    /// back itself at its next assignment or a return; where it was dropped
    /// the shadow names an earlier object or nothing, and it goes, with what
    /// it owns. A death is after the last use in a block, or on an edge it
    /// is live out of and not into: at the top of the block the edge enters
    /// when nothing else enters it, else in a block of its own. Never between
    /// an assignment and its shadow's store, which still name the previous
    /// object there.
    ///
    /// Sound because nothing else reaches it: the variable's analysis, over
    /// its origins and this one among them, found nothing that keeps it, and
    /// every register that is it (DroppedRegisters) is written once, from
    /// another of them or by its making, and stored nowhere as a value. Once
    /// all are dead none is live again until it is made anew, so each lap
    /// gives it back once at most, and the register it was made in still
    /// holds that lap's object. None may be live where it is made (an
    /// earlier lap's, still held, would be given back for this one), on the
    /// function's entry, or into a handler (PadLive), which an exception
    /// reaches from anywhere. A throw between its making and its death skips
    /// the free: a leak, as for every owned object. And none of them is live
    /// across the variable's other assignments (OwnVariables' own rule), so
    /// the shadow names it only while the variable holds it.
    /// </summary>
    private void FreeDropped(Function f, VReg v, FrameSlot slot, List<Instr> origins, Dictionary<Instr, HashSet<VReg>> carried, HashSet<Instr> told,
        HashSet<VReg> derived)
    {
        List<Instr> dropped = new();
        foreach (Instr o in origins)
            if (_liveAtMaking.Contains(o) && o.Dest is not null && (IsAllocator(o.Callee) || IsFreshCall(o)) && !dropped.Contains(o))
                dropped.Add(o);
        if (dropped.Count == 0) return;
        bool tracing = PromoteTrace is { } pt && f.Name.Contains(pt, StringComparison.Ordinal);
        int word = IrTypes.Word.Bytes();
        Defs defs = AnalysisCache.DefsOf(f, buildCfg: false);
        Liveness liveness = AnalysisCache.LivenessOf(f);
        HashSet<VReg> pads = PadLive(liveness);
        // Where each origin's object dies: after an instruction, or on an edge.
        List<(Instr Made, Block Block, Instr After)> inBlock = new();
        List<(Instr Made, Block From, Block To)> onEdge = new();
        List<Instr> freed = new();
        foreach (Instr made in dropped)
        {
            HashSet<VReg>? it = DroppedRegisters(f, made, v, defs, out Instr? why);
            string? refusal = it is null ? $"goes on through {why}" : null;
            if (it is not null)
                foreach (VReg r in it)
                {
                    // ONLY WHAT THE VARIABLE'S ANALYSIS FOLLOWED, whose rule
                    // kept each of them dead across the variable's other
                    // assignments: one it did not follow could hold the object
                    // past an assignment that gives it back.
                    if (!derived.Contains(r)) { refusal = $"{r} not followed with the variable"; break; }
                    if (!liveness.Tracks(r) || pads.Contains(r) || liveness.IsLiveIn(f.Entry, r)) { refusal = $"{r} live into a handler or on entry"; break; }
                }
            List<(Instr Made, Block Block, Instr After)> here = new();
            List<(Instr Made, Block From, Block To)> edges = new();
            foreach (Block b in f.Blocks)
            {
                if (refusal is not null) break;
                int count = b.Instrs.Count;
                if (count == 0) continue;
                // Whether any of it is live just after each instruction.
                bool[] liveAfter = new bool[count];
                int k = count;
                foreach ((Instr i, ulong[] after) in liveness.WalkBackwards(b))
                {
                    k--;
                    foreach (VReg r in it!)
                    {
                        if (!Liveness.Test(after, r.Id)) continue;
                        liveAfter[k] = true;
                        // Live where it is made: an earlier lap's, still held.
                        if (ReferenceEquals(i, made) && !ReferenceEquals(r, made.Dest)) refusal = $"{r} live where it is made";
                    }
                }
                if (refusal is not null) break;
                bool before = false;
                foreach (VReg r in it!) if (liveness.IsLiveIn(b, r)) { before = true; break; }
                for (k = 0; k < count; k++)
                {
                    Instr i = b.Instrs[k];
                    if (i.IsTerminator)
                    {
                        if (!before) break;
                        bool any = false;
                        foreach (Block s in b.Successors)
                        {
                            any = true;
                            bool into = false;
                            foreach (VReg r in it!) if (liveness.IsLiveIn(s, r)) { into = true; break; }
                            if (!into && !edges.Any(e => ReferenceEquals(e.To, s) && ReferenceEquals(e.From, b))) edges.Add((made, b, s));
                        }
                        if (!any) refusal = $"live into {i}";
                        break;
                    }
                    if (!liveAfter[k] && (before || i.Dest is not null && it!.Contains(i.Dest))) here.Add((made, b, i));
                    before = liveAfter[k];
                }
            }
            if (refusal is not null)
            {
                if (tracing) Console.Error.WriteLine($"dropped {f.Name}: {made} kept for the collector: {refusal}");
                continue;
            }
            if (tracing) Console.Error.WriteLine($"dropped {f.Name}: {made} given back where it dies, {here.Count} in blocks and {edges.Count} on edges");
            inBlock.AddRange(here);
            onEdge.AddRange(edges);
            freed.Add(made);
        }
        if (freed.Count == 0) return;

        List<Instr> Free(Instr made, int line)
        {
            VReg addr = f.NewReg(IrTypes.Word, "droppedp");
            VReg shadow = f.NewReg(IrTypes.Word, "ownedvar");
            List<Instr> code = new()
            {
                new Instr { Op = Opcode.Copy, Dest = addr, Operands = { new SlotOperand(slot) }, Line = line },
                new Instr { Op = Opcode.Load, Size = word, Dest = shadow, Operands = { RegOperand.Of(addr) }, Line = line },
            };
            VReg dying = Widen(f, code, made.Dest!, line);
            code.Add(new Instr { Op = Opcode.Call, Callee = ReplacedFreer, Operands = { RegOperand.Of(dying), RegOperand.Of(shadow) }, Line = line });
            _bookkeeping.UnionWith(code);
            return code;
        }

        foreach ((Instr made, Block b, Instr after) in inBlock)
        {
            // JUST GIVEN TO THE VARIABLE, which holds it: the free would find it there.
            if (carried.TryGetValue(after, out HashSet<VReg>? chain) && chain.Contains(made.Dest!)) continue;
            int at = b.Instrs.IndexOf(after) + 1;
            while (at < b.Instrs.Count && told.Contains(b.Instrs[at])) at++;
            b.Instrs.InsertRange(at, Free(made, after.Line));
        }
        Dictionary<(Block, Block), Block> splits = new();
        foreach ((Instr made, Block from, Block to) in onEdge)
        {
            int line = from.Terminator?.Line ?? made.Line;
            bool alone = !ReferenceEquals(to, f.Entry);
            foreach (Block p in liveness.Cfg.Preds(to)) if (!ReferenceEquals(p, from)) { alone = false; break; }
            if (alone)
            {
                int at = 0;
                while (at < to.Instrs.Count && to.Instrs[at].Op == Opcode.Phi) at++;
                to.Instrs.InsertRange(at, Free(made, line));
            }
            else PutOnEdge(f, from, to, Free(made, line), null, splits);
        }
        foreach (Instr made in freed)
        {
            if (IsAllocator(made.Callee)) _owned.Add(made);
            else { _ownedCalls.Add(made); OwnedReturns++; }
            Owned++;
        }
        f.Edited();
    }

    /// <summary>
    /// The registers that are `made`'s object, the variable `v` apart: its
    /// result, and what copies it, changes its width, takes an address inside
    /// it or hands it back (ReturnsFirst), each written once. Null, with the
    /// instruction, where it may go anywhere else: stored as a value, written
    /// into a register written more than once, kept in the box a call hands
    /// back (Held), walked in the frame (OwnedElements), freed, caught,
    /// returned, thrown, or used by anything not named here.
    /// </summary>
    private HashSet<VReg>? DroppedRegisters(Function f, Instr made, VReg v, Defs defs, out Instr? why)
    {
        why = null;
        HashSet<VReg> it = new() { made.Dest! };
        for (bool grew = true; grew;)
        {
            grew = false;
            foreach (Block b in f.Blocks)
                foreach (Instr i in b.Instrs)
                {
                    bool uses = false;
                    foreach (Operand o in i.Operands)
                        if (o is RegOperand { Reg: var r } && it.Contains(r)) { uses = true; break; }
                    if (!uses) continue;
                    VReg? same = null;
                    bool known = true;
                    switch (i.Op)
                    {
                        case Opcode.Copy or Opcode.Trunc64 or Opcode.ZExt32 or Opcode.SExt32:
                            // The variable's own assignment: its shadow says whether it holds the object.
                            if (!ReferenceEquals(i.Dest, v)) same = i.Dest;
                            break;
                        case Opcode.Add or Opcode.Sub:
                            same = i.Dest;
                            break;
                        case Opcode.Load or Opcode.ArrayLength or Opcode.MemCopy or Opcode.MemSet:
                        case Opcode.Eq or Opcode.Ne or Opcode.LtS or Opcode.LeS or Opcode.GtS or Opcode.GeS
                            or Opcode.LtU or Opcode.LeU or Opcode.GtU or Opcode.GeU or Opcode.Branch or Opcode.Switch:
                        case Opcode.Mul or Opcode.ShrS or Opcode.ShrU or Opcode.Xor or Opcode.DivS or Opcode.DivU or Opcode.RemS or Opcode.RemU:
                            break;
                        case Opcode.Store or Opcode.InitArrayLength:
                            // Written into, not written anywhere.
                            for (int k = 1; k < i.Operands.Count; k++)
                                if (i.Operands[k] is RegOperand { Reg: var stored } && it.Contains(stored)) known = false;
                            break;
                        case Opcode.Call when i.Callee is { } callee && !IsCatchEnd(callee) && callee != Unhandled && !IsFreeCall(callee)
                            && OwnedElements.WalkerOf(f, i, _inserted) is null && OwnedElements.ViewOf(f, i, _inserted) is null:
                            // Handed to a call that keeps nothing (the variable's analysis):
                            // its result is the object only where it hands its first back.
                            for (int a = 0; a < i.Operands.Count; a++)
                            {
                                if (i.Dest is null || i.Operands[a] is not RegOperand { Reg: var arg } || !it.Contains(arg)) continue;
                                if (a == 0 && ReturnsFirst(callee)) same = i.Dest;
                                else if (Held(callee, a) is not null) known = false;
                            }
                            break;
                        case Opcode.CallIndirect when i.Dest is null && !(i.Operands[0] is RegOperand { Reg: var through } && it.Contains(through)):
                            break;
                        // A VIRTUAL CALL THAT ANSWERS A VALUE, its every target
                        // known (the devirtualiser's IndirectTargets): the
                        // answer is the object only where a target hands its
                        // `this` back, as a direct call's is; kept in a box a
                        // target hands back, it may be anywhere.
                        case Opcode.CallIndirect when i.Dest is not null && !(i.Operands[0] is RegOperand { Reg: var through } && it.Contains(through))
                            && _indirect is not null && _indirect.TryGetValue(i, out string[]? targets) && targets.Length > 0:
                            for (int a = 1; a < i.Operands.Count; a++)
                            {
                                if (i.Operands[a] is not RegOperand { Reg: var arg } || !it.Contains(arg)) continue;
                                if (a == 1 && targets.Any(ReturnsFirst)) same = i.Dest;
                                else if (targets.Any(t => Held(t, a - 1) is not null)) known = false;
                            }
                            break;
                        default:
                            known = false;
                            break;
                    }
                    if (!known) { why = i; return null; }
                    if (same is null || it.Contains(same)) continue;
                    if (!defs.IsSingle(same)) { why = i; return null; }
                    it.Add(same);
                    grew = true;
                }
        }
        return it;
    }
}
