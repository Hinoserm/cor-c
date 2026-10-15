using Corsac.Lang.Ir;
using Corsac.Lang.Metadata;

namespace Corsac.Tests.Metadata;

public static class IrCodecTests
{
    public static void Run()
    {
        void Check(bool value, string message) { if (!value) throw new Exception(message); }
        void Reject(Action action)
        { try { action(); } catch (InvalidDataException) { return; } throw new Exception("Invalid compiler IR accepted"); }
        bool Throws(Action action) { try { action(); } catch (InvalidDataException) { return true; } return false; }
        Function function = new("twice", IrType.I32) { Coalescible = true, SourceFile = "test.cor", Line = 7, Display = "Test.Twice" };
        VReg argument = function.NewReg(IrType.I32); function.Params.Add(argument);
        Builder builder = new(function, function.NewBlock());
        builder.Ret(RegOperand.Of(builder.Binary(Opcode.Mul, argument, 2)));
        byte[] bytes = IrFunctionCodec.Write(function);
        Function restored = IrFunctionCodec.Read(bytes);
        Reject(() => IrFunctionCodec.Read(bytes, new IrReadBudget(512)));
        IrReadBudget budget = new(65536);
        _ = IrFunctionCodec.Read(bytes, budget);
        Check(budget.Used == IrFunctionCodec.DecodeCost(function, bytes.Length), "Deferred decode cost differs from actual accounting");
        Check(budget.Used > bytes.Length && budget.Used < budget.Limit, "Decoded node accounting is missing");
        Check(bytes.SequenceEqual(IrFunctionCodec.Write(restored)), "IR function reserialization differs");
        Check(restored.Params.Count == 1 && restored.SourceFile == "test.cor" && restored.Coalescible, "IR function fields lost");
        byte[] damaged = (byte[])bytes.Clone(); damaged[0] = 1;
        Reject(() => IrFunctionCodec.Read(damaged));
        Reject(() => IrFunctionCodec.Read(bytes[..^1]));
        Reject(() => IrFunctionCodec.Read(bytes.Concat(new byte[] { 0 }).ToArray()));
        // An async body keeps its frame record through the archive, lowered or
        // not: a unit's IR for the link is taken before the late passes and
        // the async transform, which read whether it ran and what a
        // suspension hands back. One handing back what is not a constant is
        // refused.
        Function stepped = new("stepped", IrType.I32);
        VReg machine = stepped.NewReg(IrType.I32); stepped.Params.Add(machine);
        new Builder(stepped, stepped.NewBlock()).Ret(RegOperand.Of(machine));
        stepped.Async = new AsyncFrame { Kind = CoroutineKind.Iterator, StateMachine = machine, StateOffset = 8, FieldsStart = 12, SizeSymbol = "stepped$size", SuspendResult = RegOperand.Of(machine) };
        Check(Throws(() => IrFunctionCodec.Write(stepped)), "An async body handing back a register at a suspension was archived");
        stepped.Async = new AsyncFrame { Kind = CoroutineKind.Iterator, StateMachine = machine, StateOffset = 8, FieldsStart = 12, SizeSymbol = "stepped$size" };
        Check(IrFunctionCodec.Read(IrFunctionCodec.Write(stepped)).Async is { Kind: CoroutineKind.Iterator, Lowered: false, StateOffset: 8 }, "An unlowered iterator body lost its frame or its kind in the archive");
        stepped.Async = new AsyncFrame { Kind = CoroutineKind.Async, StateMachine = machine, StateOffset = 8, FieldsStart = 12, SizeSymbol = "stepped$size" };
        Check(IrFunctionCodec.Read(IrFunctionCodec.Write(stepped)).Async is { Kind: CoroutineKind.Async, Lowered: false, StateOffset: 8 }, "An unlowered async body lost its frame or its kind in the archive");
        stepped.Async.Lowered = true;
        bytes = IrFunctionCodec.Write(stepped);
        budget = new(65536);
        Function resumed = IrFunctionCodec.Read(bytes, budget);
        Check(resumed.Async is { Kind: CoroutineKind.Async, Lowered: true, StateOffset: 8, FieldsStart: 12, SizeSymbol: "stepped$size" }
              && resumed.Async.StateMachine == resumed.Params[0], "Async frame lost in the archive");
        Check(budget.Used == IrFunctionCodec.DecodeCost(stepped, bytes.Length), "Async decode cost differs from actual accounting");
        Check(bytes.SequenceEqual(IrFunctionCodec.Write(resumed)), "Async IR reserialization differs");
        // The calls kept from the inliner go with the body, by their place
        // among its instructions, and only calls are taken for them.
        Function keeper = new("keeper", IrType.Void);
        var entry = keeper.NewBlock();
        Instr first = new() { Op = Opcode.Call, Callee = "first" }, second = new() { Op = Opcode.Call, Callee = "second" };
        entry.Instrs.Add(first); entry.Instrs.Add(second); entry.Instrs.Add(new Instr { Op = Opcode.Ret });
        bytes = IrFunctionCodec.Write(keeper, new HashSet<Instr>(ReferenceEqualityComparer.Instance) { second });
        List<Instr> keptBack = new();
        Function kept = IrFunctionCodec.Read(bytes, null, keptBack);
        Check(keptBack.Count == 1 && ReferenceEquals(keptBack[0], kept.Blocks[0].Instrs[1]), "Kept calls lost in the archive");
        Check(bytes.SequenceEqual(IrFunctionCodec.Write(kept, new HashSet<Instr>(keptBack, ReferenceEqualityComparer.Instance))), "Kept calls reserialization differs");
        damaged = (byte[])bytes.Clone(); damaged[^4] = 2;
        Reject(() => IrFunctionCodec.Read(damaged));
        // EVERY MARK THE ANALYSES PUT ON THE IR goes with it: a
        // parameter's Number, a load's and a character copy's Number, a site
        // the link chose for a region, a loop header given one and its lap's
        // bytes, a virtual call's declaring type, a field and a fresh struct's
        // Field mark, and an access's Family, which typed aliasing needs when
        // the link summarises a function again. The link rebuilds from the archive, and one lost read a
        // number as an address again.
        Function marked = new("marked", IrType.Void);
        VReg count = marked.NewReg(IrType.I32); count.Number = true; marked.Params.Add(count);
        VReg at = marked.NewReg(IrTypes.Word); marked.Params.Add(at);
        var head = marked.NewBlock(); var lap = marked.NewBlock();
        VReg read = marked.NewReg(IrType.I32);
        head.Instrs.Add(new Instr { Op = Opcode.Load, Dest = read, Size = 4, Operands = { RegOperand.Of(at) }, Number = true, Field = "T::count", Family = "T" });
        head.Instrs.Add(new Instr { Op = Opcode.MemCopy, Operands = { RegOperand.Of(at), RegOperand.Of(at), RegOperand.Of(count) }, Number = true });
        VReg made = marked.NewReg(IrTypes.Word);
        head.Instrs.Add(new Instr { Op = Opcode.Call, Callee = "alloc", Dest = made, Operands = { RegOperand.Of(count) }, RegionSite = true, Field = Instr.FreshStruct });
        head.Instrs.Add(new Instr { Op = Opcode.CallIndirect, Operands = { RegOperand.Of(made), RegOperand.Of(made) }, DispatchType = "t_Node" });
        head.Instrs.Add(new Instr { Op = Opcode.Load, Dest = marked.NewReg(IrTypes.Word), Size = IrTypes.Word.Bytes(), Operands = { RegOperand.Of(at) } });
        head.Instrs.Add(new Instr { Op = Opcode.Jump, InitialTargets = new[] { lap } });
        lap.RegionLoop = true; lap.RegionLoopBytes = 96;
        lap.Instrs.Add(new Instr { Op = Opcode.Ret });
        bytes = IrFunctionCodec.Write(marked);
        budget = new(65536);
        Function back = IrFunctionCodec.Read(bytes, budget);
        Instr[] all = back.Blocks.SelectMany(block => block.Instrs).ToArray();
        Check(back.Params[0].Number && !back.Params[1].Number, "A parameter's Number lost in the archive");
        Check(all[0].Number && all[0].Field == "T::count" && all[0].Family == "T" && all[1].Family is null && all[1].Number && !all[4].Number, "A load's or a copy's Number lost in the archive");
        Check(all[2].RegionSite && all[2].ReturnsFreshStruct && !all[3].RegionSite, "A region site or a fresh struct's mark lost in the archive");
        Check(all[3].DispatchType == "t_Node", "A virtual call's declaring type lost in the archive");
        Check(!back.Blocks[0].RegionLoop && back.Blocks[1] is { RegionLoop: true, RegionLoopBytes: 96 }, "A loop's region lost in the archive");
        Check(budget.Used == IrFunctionCodec.DecodeCost(marked, bytes.Length), "Marked decode cost differs from actual accounting");
        Check(bytes.SequenceEqual(IrFunctionCodec.Write(back)), "Marked IR reserialization differs");
        DataItem data = new("table", new byte[8]) { ReadOnly = true, Coalescible = true };
        data.Relocs.Add(new(0, "target", 4));
        bytes = IrDataCodec.Write(data);
        DataItem decoded = IrDataCodec.Read(bytes);
        Check(bytes.SequenceEqual(IrDataCodec.Write(decoded)) && decoded.Relocs.Single().Symbol == "target", "IR data round trip");
        Reject(() => IrDataCodec.Read(bytes[..^1]));
        DataItem bss = new("large-zero", new byte[4096]) { Zero = true };
        bytes = IrDataCodec.Write(bss);
        Check(bytes.Length < 100 && IrDataCodec.Read(bytes).Bytes.Length == 4096, "BSS encoding is not compact");
        Reject(() => IrDataCodec.Read(bytes, maximumBytes: 4095));
        // THE UNIT RECORD: what the unit was compiled with follows its imports
        // where a flag says it does (an archive taken before the late passes),
        // and reads back as written; one taken after them has none.
        Module unit = new("unit") { Entry = "Main", NeedsHeap = false };
        unit.Imports.Add("imported");
        IrUnitCodec.Settings settings = new(true, false, true, true, false, true, new[] { "Helper" }, PositionIndependent: true);
        foreach (IrUnitCodec.Settings? taken in new[] { settings, null })
        {
            ObjectFile obj = new();
            Corsac.Lang.Lto.IrArchive.Attach(obj, IrUnitCodec.Snapshot(unit, true, taken));
            Corsac.Lang.Lto.IrArchive archive = Corsac.Lang.Lto.IrArchive.Read(obj)!;
            (IrUnitCodec.Settings? alone, bool maps) = IrUnitCodec.ReadSettings(archive);
            (Module module, bool mapsToo, _, IrUnitCodec.Settings? withModule) = IrUnitCodec.ReadWithSettings(archive);
            bool Same(IrUnitCodec.Settings? read) => taken is null ? read is null
                : read is { PreLate: true, NoCollector: false, CallsCollector: true, LeavesLinkHints: true, OptimizeSize: false,
                    ExperimentalBatch: true, PositionIndependent: true } && read.RuntimeHelpers.SequenceEqual(new[] { "Helper" });
            Check(maps && mapsToo && Same(alone) && Same(withModule), "Unit settings lost in the archive (" + (taken is null ? "none" : "taken") + ")");
            Check(module is { Name: "unit", Entry: "Main", NeedsHeap: false } && module.Imports.SetEquals(new[] { "imported" })
                && module.CallsCollector == (taken is not null) && module.RuntimeHelpers.Contains("Helper") == (taken is not null), "Unit record lost in the archive");
        }
        Console.WriteLine("IR codecs: complete function/data/unit round trips, async frames, analysis marks, corruption and allocation budgets passed");
    }
}
