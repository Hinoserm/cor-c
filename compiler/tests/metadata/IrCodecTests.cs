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
