#nullable enable
using Corsac.Lang.Ir;

namespace Corsac.Lang.Opt;

/// <summary>Erase unused integer return transport, retaining calls and all
/// effectful evaluation in the body. Floating return-stack conventions stay.</summary>
public sealed class DeadReturns : IParallelModulePass
{
    public string Name => "dead-returns";
    public int Workers { get; set; } = 1;
    public void Run(Module module)
    {
        if (Workers < 1 || Workers > 64) throw new ArgumentOutOfRangeException(nameof(Workers));
        ClosedFunctionCalls graph = new(module);
        if (Workers > 1 && module.Functions.Count > 1) RunParallel(module, graph);
        else foreach (Function function in module.Functions) Fold(function, graph);
    }

    private void RunParallel(Module module, ClosedFunctionCalls graph)
        => FunctionWorkers.Run(module, Workers, (function, index) => Fold(function, graph));

    private static void Fold(Function function, ClosedFunctionCalls graph)
    {
        // The call graph and call destinations remain frozen. Each worker
        // changes only its own return signature and return instructions.
        if (!function.Returns.IsInt() || !graph.CanChange(function, out List<Instr> calls)
            || calls.Any(c => c.Dest is not null)) return;
        function.Returns = IrType.Void;
        foreach (var block in function.Blocks)
            foreach (Instr instruction in block.Instrs)
                if (instruction.Op == Opcode.Ret) instruction.Operands.Clear();
    }
}
