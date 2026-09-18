#nullable enable
using Corsac.Lang.Ir;

namespace Corsac.Lang.Opt;

/// <summary>Closed-executable call graph, following the same entry/export
/// boundary as inlining. Library bodies and address-visible functions keep
/// their ABI. Staged large-batch infrastructure, not enabled by default.</summary>
internal sealed class ClosedFunctionCalls
{
    public Dictionary<string, List<Instr>> Calls { get; } = new(StringComparer.Ordinal);
    private readonly HashSet<string> _addressed;
    private readonly Module _module;

    public ClosedFunctionCalls(Module module)
    {
        _module = module;
        _addressed = Inline.AddressTaken(module);
        foreach (Instr instruction in module.Functions.SelectMany(f => f.Blocks).SelectMany(b => b.Instrs))
        {
            if (instruction.Op != Opcode.Call || instruction.Callee is not { } name) continue;
            if (!Calls.TryGetValue(name, out List<Instr>? uses)) Calls[name] = uses = new();
            uses.Add(instruction);
        }
    }

    public bool CanChange(Function function, out List<Instr> calls)
    {
        calls = Calls.GetValueOrDefault(function.Name) ?? new();
        return _module.Entry is not null && !function.FromLibrary && function.Async is null
            && !_addressed.Contains(function.Name) && Inline.Inlineable(function, _addressed)
            && calls.Count != 0 && calls.All(c => c.Operands.Count == function.Params.Count);
    }
}
