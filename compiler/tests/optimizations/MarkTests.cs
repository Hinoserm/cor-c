#nullable enable
using Corsac.Lang.Ir;
using Corsac.Lang.Opt;

namespace Corsac.Tests.Opt;

using Block = Corsac.Lang.Ir.Block;

public static partial class Program
{
    /// <summary>
    /// MarkVerifier (--verify-marks): an instruction a pass replaced one for
    /// one without its marks is said, with the pass and every mark lost; one
    /// replaced with them, or gone altogether, is not; a loop header merged
    /// away with its region is.
    /// </summary>
    private static void MarksVerifierSeesLosses()
    {
        Function f = new("marks", IrType.I32);
        VReg at = f.NewReg(IrTypes.Word); f.Params.Add(at);
        VReg count = f.NewReg(IrType.I32); count.Number = true; f.Params.Add(count);
        Block entry = f.NewBlock("entry");
        VReg read = f.NewReg(IrType.I32);
        Instr load = new() { Op = Opcode.Load, Dest = read, Size = 4, Operands = { new RegOperand(at) }, Number = true, Field = "T::count", Family = "T" };
        entry.Instrs.Add(load);
        Instr store = new() { Op = Opcode.Store, Size = 4, Operands = { new RegOperand(at), new RegOperand(read) }, Field = "T::count", Family = "T" };
        entry.Instrs.Add(store);
        new Builder(f, entry).Ret(new RegOperand(read));

        MarkVerifier before = MarkVerifier.Snapshot(f);
        entry.Instrs[0] = new Instr { Op = Opcode.Load, Dest = read, Size = 4, Operands = { new RegOperand(at) } };
        entry.Instrs[1] = new Instr { Op = Opcode.Store, Size = 4, Operands = { new RegOperand(at), new RegOperand(read) }, Field = "T::count" };
        count.Number = false;
        List<string> lost = before.Check(f, "careless");
        Assert(lost.Count == 3, "a load, a store and a parameter each said: " + string.Join(" | ", lost));
        Assert(lost.All(line => line.StartsWith("verify-marks: careless lost ", StringComparison.Ordinal)), "the pass named: " + string.Join(" | ", lost));
        string Said(int n) => lost[n][..lost[n].IndexOf(" in marks", StringComparison.Ordinal)];
        Assert(Said(0).Contains("Number") && Said(0).Contains("Family T") && Said(0).Contains("Field T::count"), "every mark the load lost: " + lost[0]);
        Assert(Said(1).Contains("Family T") && !Said(1).Contains("Field"), "only the mark the store lost: " + lost[1]);
        Assert(Said(2).EndsWith("lost Number", StringComparison.Ordinal), "the parameter's mark: " + lost[2]);

        count.Number = true;
        before = MarkVerifier.Snapshot(f);
        entry.Instrs[0] = new Instr { Op = Opcode.Load, Dest = read, Size = 4, Operands = { new RegOperand(at) }, Number = true, Field = "T::count", Family = "T" };
        entry.Instrs[1] = new Instr { Op = Opcode.Copy, Dest = f.NewReg(IrType.I32), Operands = { new RegOperand(read) } };
        Assert(before.Check(f, "careful").Count == 0, "a replacement with its marks, and a store gone, lose nothing");

        // A loop header merged into the block before it, its region not.
        Function loop = new("loop", IrType.Void);
        VReg go = loop.NewReg(IrType.I32); loop.Params.Add(go);
        Block start = loop.NewBlock("start"), head = loop.NewBlock("head"), done = loop.NewBlock("done");
        new Builder(loop, start).Jump(head);
        head.RegionLoop = true; head.RegionLoopBytes = 64;
        VReg made = new Builder(loop, head).Call(Escape.Allocator, IrTypes.Word, new ImmOperand(16, IrTypes.Word))!;
        new Builder(loop, head).Branch(go, head, done);
        new Builder(loop, done).Ret();
        MarkVerifier loops = MarkVerifier.Snapshot(loop);
        start.Instrs.RemoveAt(start.Instrs.Count - 1);
        start.Instrs.AddRange(head.Instrs);
        loop.Blocks.Remove(head);
        lost = loops.Check(loop, "merge");
        Assert(lost.Count == 1 && lost[0].Contains("RegionLoop") && lost[0].Contains("merged away"), "the merged header said: " + string.Join(" | ", lost));
        Assert(made is not null, "the lap's allocation");
        Assert(loops.Check(loop, "region-points-to").Count == 0, "the pass that opens loop regions consumes the mark");
    }

    /// <summary>FrameAddressFold: a load folded onto its slot keeps the field it reads.</summary>
    private static void MarksFrameAddressFold()
    {
        Function f = new("frame-marks", IrType.I32);
        var slot = f.NewSlot(16, 4); var block = f.NewBlock("entry");
        VReg pointer = f.NewReg(IrTypes.Word), value = f.NewReg(IrType.I32);
        block.Instrs.Add(new Instr { Op = Opcode.Copy, Dest = pointer, Operands = { new SlotOperand(slot) } });
        block.Instrs.Add(new Instr { Op = Opcode.Load, Dest = value, Size = 4, Offset = 4, Operands = { new RegOperand(pointer) },
            Number = true, Field = "Point::y", Family = "Point" });
        new Builder(f, block).Ret(new RegOperand(value));
        MarkVerifier before = MarkVerifier.Snapshot(f);
        new FrameAddressFold().Run(f);
        Instr read = block.Instrs.Single(i => i.Op == Opcode.Load);
        Assert(read.Operands[0] is SlotOperand, "the load folded onto its slot");
        Assert(read is { Number: true, Field: "Point::y", Family: "Point" }, "the folded load keeps its marks");
        Assert(before.Check(f, new FrameAddressFold().Name).Count == 0, "the verifier finds nothing lost");
    }

    /// <summary>
    /// ConstantSpecialize: a call sent to a copy of its callee made for its
    /// constant keeps a struct made for it and a site the link chose.
    /// </summary>
    private static void MarksConstantSpecialize()
    {
        Module module = new("specialize-marks");
        Function callee = new("choose", IrTypes.Word);
        VReg condition = callee.NewReg(IrType.I32); callee.Params.Add(condition);
        Block entry = callee.NewBlock("entry"), yes = callee.NewBlock("yes"), no = callee.NewBlock("no");
        new Builder(callee, entry).Branch(condition, yes, no);
        new Builder(callee, yes).Ret(new ImmOperand(1, IrTypes.Word));
        new Builder(callee, no).Ret(new ImmOperand(2, IrTypes.Word));
        Function caller = new("main", IrTypes.Word);
        Block start = caller.NewBlock("entry");
        VReg result = caller.NewReg(IrTypes.Word);
        start.Instrs.Add(new Instr { Op = Opcode.Call, Dest = result, Callee = "choose", Operands = { new ImmOperand(1, IrType.I32) },
            Field = Instr.FreshStruct, RegionSite = true });
        new Builder(caller, start).Ret(new RegOperand(result));
        module.Functions.Add(caller); module.Functions.Add(callee); module.Entry = "main";
        ConstantSpecialize pass = new();
        Dictionary<Function, MarkVerifier> before = MarkVerifier.Snapshot(module);
        pass.Run(module);
        Instr call = start.Instrs.Single(i => i.Op == Opcode.Call);
        Assert(call.Callee != "choose", "the call sent to the constant's copy");
        Assert(call is { ReturnsFreshStruct: true, RegionSite: true }, "the copy's call keeps its marks");
        Assert(before[caller].Check(caller, pass.Name).Count == 0, "the verifier finds nothing lost");
    }

    /// <summary>
    /// Devirtualize: a call through a slot made direct keeps a struct made
    /// for it; a delegate's Invoke mark, of a call through a slot, goes.
    /// </summary>
    private static void MarksDevirtualize()
    {
        foreach (string mark in new[] { Instr.FreshStruct, Instr.DelegateInvoke })
        {
            Module module = new("devirtualize-marks");
            module.Data.Add(new DataItem("t_Table", new byte[8]) { ReadOnly = true });
            Function f = new("main", IrTypes.Word);
            VReg receiver = f.NewReg(IrTypes.Word); f.Params.Add(receiver);
            Block entry = f.NewBlock("entry");
            VReg target = f.NewReg(IrTypes.Word), result = f.NewReg(IrTypes.Word);
            entry.Instrs.Add(new Instr { Op = Opcode.Copy, Dest = target, Operands = { new SymOperand("m_Make", 0) } });
            entry.Instrs.Add(new Instr { Op = Opcode.CallIndirect, Dest = result, Operands = { new RegOperand(target), new RegOperand(receiver) },
                Field = mark, DispatchType = "t_Table" });
            new Builder(f, entry).Ret(new RegOperand(result));
            module.Functions.Add(f); module.Entry = "main";
            Devirtualize pass = new();
            Dictionary<Function, MarkVerifier> before = MarkVerifier.Snapshot(module);
            pass.Run(module);
            Instr call = entry.Instrs.Single(i => i.Dest == result);
            Assert(call is { Op: Opcode.Call, Callee: "m_Make" }, "the call made direct");
            Assert(call.Field == (mark == Instr.FreshStruct ? mark : null), "a fresh struct kept, a delegate's Invoke not: " + call.Field);
            Assert(before[f].Check(f, pass.Name).Count == 0, "the verifier finds nothing lost");
        }
    }

    /// <summary>
    /// RegionPointsTo.CloneBody: a body copied for a region keeps the link's
    /// sites and loop regions.
    /// </summary>
    private static void MarksRegionClone()
    {
        Function f = new("lap", IrType.Void);
        VReg go = f.NewReg(IrType.I32); go.Number = true; f.Params.Add(go);
        Block start = f.NewBlock("start"), head = f.NewBlock("head"), done = f.NewBlock("done");
        new Builder(f, start).Jump(head);
        head.RegionLoop = true; head.RegionLoopBytes = 48;
        Builder lap = new(f, head);
        lap.Call(Escape.Allocator, IrTypes.Word, new ImmOperand(16, IrTypes.Word));
        head.Instrs[^1].RegionSite = true;
        lap.Branch(go, head, done);
        new Builder(f, done).Ret();
        HashSet<Instr> keep = new(ReferenceEqualityComparer.Instance);
        (Function copy, Dictionary<Instr, Instr> from) = RegionPointsTo.CloneBody(f, "lap$region$0", keep);
        Instr made = from[head.Instrs[0]];
        Assert(made.RegionSite, "the copy's allocation is the link's site");
        Assert(copy.Blocks.Single(b => b.Instrs.Contains(made)) is { RegionLoop: true, RegionLoopBytes: 48 }, "the copy's loop keeps its region");
        Assert(copy.Params[0].Number, "the copy's parameter is a number");
    }

    /// <summary>Inline: a loop the link gave a region keeps it in the caller.</summary>
    private static void MarksInlineLoop()
    {
        Module module = new("inline-marks");
        Function callee = new("spin", IrType.Void);
        VReg go = callee.NewReg(IrType.I32); callee.Params.Add(go);
        Block start = callee.NewBlock("start"), head = callee.NewBlock("head"), done = callee.NewBlock("done");
        new Builder(callee, start).Jump(head);
        head.RegionLoop = true; head.RegionLoopBytes = 32;
        new Builder(callee, head).Branch(go, head, done);
        new Builder(callee, done).Ret();
        Function caller = new("main", IrType.Void);
        VReg input = caller.NewReg(IrType.I32); caller.Params.Add(input);
        Builder b = new(caller, caller.NewBlock("entry"));
        b.Call("spin", IrType.Void, new RegOperand(input));
        b.Ret();
        module.Functions.Add(caller); module.Functions.Add(callee); module.Entry = "main";
        new Inline { SmallBody = 3, GrowthLimit = 100 }.Run(module);
        Verifier.Check(caller, "inline marks");
        Assert(!caller.Blocks.SelectMany(block => block.Instrs).Any(i => i.Op == Opcode.Call && i.Callee == "spin"), "the one call inlined");
        Assert(caller.Blocks.Any(block => block is { RegionLoop: true, RegionLoopBytes: 32 }), "the inlined loop keeps its region");
    }

    /// <summary>
    /// CommonTailMerge: two tails alike but for a mark -- an allocation the
    /// link chose for a region and one it did not -- are not shared.
    /// </summary>
    private static void MarksCommonTailMerge()
    {
        foreach (bool alike in new[] { false, true })
        {
            Function f = new("tails", IrType.Void);
            VReg go = f.NewReg(IrType.I32); f.Params.Add(go);
            Block entry = f.NewBlock("entry"), left = f.NewBlock("left"), right = f.NewBlock("right");
            new Builder(f, entry).Branch(go, left, right);
            foreach (Block side in new[] { left, right })
            {
                for (int n = 0; n < 3; n++)
                    side.Instrs.Add(new Instr { Op = Opcode.Call, Callee = Escape.Allocator, Operands = { new ImmOperand(16, IrTypes.Word) },
                        RegionSite = side == left || alike, Line = 7 });
                side.Instrs.Add(new Instr { Op = Opcode.Ret, Line = 7 });
            }
            MarkVerifier before = MarkVerifier.Snapshot(f);
            CommonTailMerge pass = new();
            pass.Run(f);
            bool merged = !f.Blocks.Contains(left) || !f.Blocks.Contains(right) || f.Blocks.Any(b => b.Label.StartsWith("shared_tail", StringComparison.Ordinal));
            Assert(merged == alike, $"tails marked {(alike ? "alike" : "apart")} merged={merged}");
            int sites = f.Blocks.SelectMany(b => b.Instrs).Count(i => i.RegionSite);
            Assert(alike || sites == 3, "every site the link chose is still one: " + sites);
            Assert(before.Check(f, pass.Name).Count == 0, "the verifier finds nothing lost");
        }
    }
}
