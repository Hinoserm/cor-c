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
        builder.Ret(new RegOperand(builder.Binary(Opcode.Mul, argument, 2)));
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
        // A lowered async body keeps its frame record through the archive; one
        // still carrying its suspension markers is refused.
        Function stepped = new("stepped", IrType.I32);
        VReg machine = stepped.NewReg(IrType.I32); stepped.Params.Add(machine);
        new Builder(stepped, stepped.NewBlock()).Ret(new RegOperand(machine));
        stepped.Async = new AsyncFrame { StateMachine = machine, StateOffset = 8, FieldsStart = 12, SizeSymbol = "stepped$size" };
        Check(Throws(() => IrFunctionCodec.Write(stepped)), "An unlowered async body was archived");
        stepped.Async.Lowered = true;
        bytes = IrFunctionCodec.Write(stepped);
        budget = new(65536);
        Function resumed = IrFunctionCodec.Read(bytes, budget);
        Check(resumed.Async is { Lowered: true, StateOffset: 8, FieldsStart: 12, SizeSymbol: "stepped$size" }
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
        Console.WriteLine("IR codecs: complete function/data round trips, async frames, corruption and allocation budgets passed");
    }
}
