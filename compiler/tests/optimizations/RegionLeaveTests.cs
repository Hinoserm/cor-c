using Corsac.Lang.Ir;
using Corsac.Lang.Opt;

namespace Corsac.Tests.Opt;

public static partial class Program
{
    /// <summary>
    /// RegionPointsTo.LeaveLast: the link's lifetime run puts its frees just
    /// before each return, after the leaves the region pass put there; every
    /// leave a return passes -- the function's and each loop's -- goes back
    /// behind them, in its order, and none moves past an allocation.
    /// </summary>
    private static void RegionLeavesAfterFrees()
    {
        const string Free = "m_Runtime_FreeStorageInFrame_1_V$Any";
        string Shape(Function f) => string.Join(" ", f.Blocks[0].Instrs.Select(i =>
            i.Op == Opcode.Ret ? "ret" : i.Callee == RegionPointsTo.Leave ? "leave" + ((RegOperand)i.Operands[0]).Reg.Id
            : i.Callee == Free ? "free" : i.Callee == Escape.Allocator ? "alloc" : i.Op.ToString()));
        Function Make(params string[] steps)
        {
            Function f = new("leaves", IrType.Void);
            Builder b = new(f, f.NewBlock());
            VReg own = f.NewReg(IrTypes.Word), lap = f.NewReg(IrTypes.Word), held = f.NewReg(IrTypes.Word);
            f.Params.Add(own); f.Params.Add(lap); f.Params.Add(held);
            foreach (string step in steps)
                switch (step)
                {
                    case "leave1": b.Call(RegionPointsTo.Leave, IrType.Void, new RegOperand(own)); break;
                    case "leave2": b.Call(RegionPointsTo.Leave, IrType.Void, new RegOperand(lap)); break;
                    case "free": b.Call(Free, IrType.Void, new RegOperand(held)); break;
                    case "alloc": b.Call(Escape.Allocator, IrTypes.Word, new ImmOperand(16, IrTypes.Word)); break;
                }
            b.Ret();
            return f;
        }
        Function both = Make("leave1", "leave2", "free");
        Assert(RegionPointsTo.LeaveLast(both) == 2 && Shape(both) == "free leave0 leave1 ret", "both leaves after the free: " + Shape(both));
        Function apart = Make("leave1", "free", "leave2", "free");
        RegionPointsTo.LeaveLast(apart);
        Assert(Shape(apart) == "free free leave0 leave1 ret", "leaves apart gathered after the frees: " + Shape(apart));
        Function placed = Make("free", "leave1", "leave2");
        Assert(RegionPointsTo.LeaveLast(placed) == 0 && Shape(placed) == "free leave0 leave1 ret", "leaves already last stay: " + Shape(placed));
        Function making = Make("leave1", "alloc", "free");
        Assert(RegionPointsTo.LeaveLast(making) == 0 && Shape(making) == "leave0 alloc free ret", "a leave before an allocation stays: " + Shape(making));
    }
}
