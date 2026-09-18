using Corsac.Lang.Ir;
using Corsac.Lang.Opt;

namespace Corsac.Tests.Opt;

public static partial class Program
{
    private static void BooleanMaskDiamondSafety()
    {
        foreach (string mode in new[] { "mask", "effect", "address", "other-value", "nonboolean" })
        {
            Function f = new("mask-" + mode, IrType.I32);
            var entry = f.NewBlock(); var yes = f.NewBlock(); var no = f.NewBlock(); var join = f.NewBlock();
            Builder b = new(f, entry);
            VReg x = f.NewReg(IrType.I32); f.Params.Add(x);
            VReg condition = mode == "nonboolean" ? x : b.Binary(Opcode.GtS, x, 0);
            if (mode == "address") b.LabelAddress(yes);
            b.Branch(condition, yes, no);
            VReg result = f.NewReg(IrType.I32);
            b.SetBlock(yes);
            if (mode == "effect") b.Call("side_effect", IrType.Void);
            b.CopyTo(result, new ImmOperand(mode == "other-value" ? 1 : -1, IrType.I32)); b.Jump(join);
            b.SetBlock(no); b.CopyTo(result, new ImmOperand(0, IrType.I32)); b.Jump(join);
            b.SetBlock(join); b.Ret(new RegOperand(result));
            new BooleanMaskDiamonds().Run(f);
            Assert(entry.Instrs.Any(i => i.Op == Opcode.Neg && i.Dest == result) == (mode == "mask"), mode + " diamond safety");
        }
    }
}
