using Corsac.Lang.Ir;
using Corsac.Lang.Opt;

namespace Corsac.Tests.Opt;

public static partial class Program
{
    private static void ArrayLengthConstructionFacts()
    {
        foreach (string mode in new[] { "known", "call", "missing", "duplicate", "late" })
        {
            Function function = new("array-length-" + mode, IrType.I32);
            Builder b = new(function, function.NewBlock());
            VReg array = b.SlotAddress(function.NewSlot(48, 4));
            if (mode is not ("missing" or "late")) b.Emit(Opcode.InitArrayLength, null, new RegOperand(array), new ImmOperand(8, IrType.I32));
            if (mode == "duplicate") b.Emit(Opcode.InitArrayLength, null, new RegOperand(array), new ImmOperand(4, IrType.I32));
            if (mode == "call") b.Call("ordinary-array-consumer", IrType.Void, new RegOperand(array));
            VReg length = b.Unary(Opcode.ArrayLength, new RegOperand(array), IrType.I32);
            if (mode == "late") b.Emit(Opcode.InitArrayLength, null, new RegOperand(array), new ImmOperand(8, IrType.I32));
            b.Ret(new RegOperand(length));
            new ArrayLengthFacts().Run(function);
            Instr value = function.Blocks[0].Instrs.Single(instruction => instruction.Dest == length);
            bool folded = mode is "known" or "call";
            Assert((value.Op == Opcode.Copy && value.Operands[0] is ImmOperand { Value: 8 }) == folded, mode + " array length folding");
        }
        Function unknown = new("nullable-array", IrType.Void);
        VReg parameter = unknown.NewReg(IrType.I32); unknown.Params.Add(parameter);
        Builder u = new(unknown, unknown.NewBlock());
        u.Unary(Opcode.ArrayLength, new RegOperand(parameter), IrType.I32); u.Ret();
        new ArrayLengthFacts().Run(unknown); new DeadCodeElimination().Run(unknown);
        Assert(unknown.Blocks[0].Instrs.Any(instruction => instruction.Op == Opcode.ArrayLength), "unused unknown length retains null fault");

        Function branch = new("conditional-construction", IrType.I32);
        var entry = branch.NewBlock(); var construct = branch.NewBlock(); var join = branch.NewBlock();
        Builder e = new(branch, entry); VReg frame = e.SlotAddress(branch.NewSlot(48, 4));
        e.Branch(new ImmOperand(1, IrType.I32), construct, join);
        Builder c = new(branch, construct); c.Emit(Opcode.InitArrayLength, null, new RegOperand(frame), new ImmOperand(8, IrType.I32)); c.Jump(join);
        Builder j = new(branch, join); VReg answer = j.Unary(Opcode.ArrayLength, new RegOperand(frame), IrType.I32); j.Ret(new RegOperand(answer));
        new ArrayLengthFacts().Run(branch);
        Assert(join.Instrs[0].Op == Opcode.ArrayLength, "non-dominating initialization is not a fact");
    }
}
