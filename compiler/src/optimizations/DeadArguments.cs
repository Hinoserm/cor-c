#nullable enable
using Corsac.Lang.Ir;

namespace Corsac.Lang.Opt;

/// <summary>Remove unread formal arguments and their direct-call operands.
/// Argument-producing effects remain in the caller for ordinary DCE to handle.</summary>
public sealed class DeadArguments : IParallelModulePass
{
    public string Name => "dead-arguments";
    public int Workers { get; set; } = 1;
    public void Run(Module module)
    {
        if (Workers < 1 || Workers > 64) throw new ArgumentOutOfRangeException(nameof(Workers));
        ClosedFunctionCalls graph = new(module);
        List<int>?[] plans = new List<int>?[module.Functions.Count];
        if (Workers > 1 && module.Functions.Count > 1) AnalyzeParallel(module, graph, plans);
        else for (int i = 0; i < plans.Length; i++) plans[i] = Analyze(module.Functions[i], graph);
        // Calls belong to their callers, not to the callee being analyzed.
        // Freeze all bodies until every read-set has been computed, then
        // apply signature/call changes in module order. This same boundary
        // applies to one worker, so scheduling cannot affect pass results.
        for (int i = 0; i < plans.Length; i++)
        {
            if (plans[i] is not List<int> removed) continue;
            Function function = module.Functions[i];
            foreach (int index in removed)
            {
                function.Params.RemoveAt(index);
                foreach (Instr call in graph.Calls[function.Name]) call.Operands.RemoveAt(index);
            }
        }
    }

    private void AnalyzeParallel(Module module, ClosedFunctionCalls graph, List<int>?[] plans)
        => FunctionWorkers.Run(module, Workers, (function, index) => plans[index] = Analyze(function, graph));

    private static List<int>? Analyze(Function function, ClosedFunctionCalls graph)
    {
        if (function.Params.Count == 0 || !graph.CanChange(function, out _)) return null;
        HashSet<VReg> read = new(function.Blocks.SelectMany(b => b.Instrs).SelectMany(IrInfo.Uses));
        List<int>? removed = null;
        for (int index = function.Params.Count - 1; index >= 0; index--)
        {
            if (read.Contains(function.Params[index])) continue;
            removed ??= new();
            removed.Add(index);
        }
        return removed;
    }
}
