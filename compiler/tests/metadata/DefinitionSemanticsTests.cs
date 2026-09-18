using Corsac.Lang.Ir;
using Corsac.Lang.Metadata;

namespace Corsac.Tests.Metadata;

public static class DefinitionSemanticsTests
{
    public static void Run()
    {
        Module Make(string local, byte value, int line)
        {
            Module module = new("unit");
            module.Data.Add(new DataItem(local, new[] { value }) { ReadOnly = true, Exported = false });
            Function function = new("generic", IrType.I32) { Coalescible = true, SourceFile = "file" + line, Line = line };
            Block block = function.NewBlock();
            VReg result = function.NewReg(IrType.I32);
            block.Instrs.Add(new Instr { Op = Opcode.Load, Dest = result, Size = 1, Operands = { new SymOperand(local) } });
            block.Instrs.Add(new Instr { Op = Opcode.Ret, Operands = { new RegOperand(result) } });
            module.Functions.Add(function);
            module.Functions.Add(new Function("ordinary", IrType.Void));
            return module;
        }
        byte[] first = DefinitionSemantics.Capture(Make("str_1", 42, 1))["generic"];
        byte[] renamed = DefinitionSemantics.Capture(Make("str_99", 42, 99))["generic"];
        byte[] changed = DefinitionSemantics.Capture(Make("str_1", 43, 1))["generic"];
        if (!first.SequenceEqual(renamed)) throw new Exception("Local spelling or source diagnostics changed semantic identity");
        if (first.SequenceEqual(changed)) throw new Exception("Different private constant has the same identity");
        if (DefinitionSemantics.Capture(Make("x", 42, 1)).Count != 1) throw new Exception("Ordinary strong definition was certified");
        Console.WriteLine("definition semantics: local normalization, content and explicit provenance passed");
    }
}
