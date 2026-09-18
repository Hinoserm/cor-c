using Corsac.Lang.Ir;
using Corsac.Lang.Opt;

namespace Corsac.Tests.Metadata;

public static class ExportBoundaryTests
{
    public static void Run()
    {
        Module module = new("open-unit") { Entry = "entry", PreserveExports = true };
        Function helper = new("helper", IrType.I32);
        helper.NewBlock().Instrs.Add(new Instr { Op = Opcode.Ret, Operands = { new ImmOperand(42, IrType.I32) } });
        Function entry = new("entry", IrType.Void);
        var block = entry.NewBlock();
        block.Instrs.Add(new Instr { Op = Opcode.Store, Size = 4, Operands = { new SymOperand("state"), new ImmOperand(42, IrType.I32) } });
        block.Instrs.Add(new Instr { Op = Opcode.Call, Callee = "helper", Dest = entry.NewReg(IrType.I32) });
        block.Instrs.Add(new Instr { Op = Opcode.Ret });
        module.Functions.Add(entry); module.Functions.Add(helper);
        module.Data.Add(new DataItem("state", new byte[4]) { Zero = true });
        if (new ClosedFunctionCalls(module).CanChange(helper, out _)) throw new Exception("Exported ABI treated as closed world");
        new DeadStatics().Run(module);
        if (module.Data.Count != 1 || !block.Instrs.Any(i => i.Op == Opcode.Store)) throw new Exception("External reader's state removed");
        new Inline().Run(module);
        if (!module.Functions.Contains(helper)) throw new Exception("External caller's exported definition removed");
        Console.WriteLine("export boundaries: external callers, readers and ABI preserved");
    }
}
