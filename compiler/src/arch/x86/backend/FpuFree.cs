#nullable enable
using Corsac.Lang.Ir;

namespace Corsac.Lang.X86;

using Block = Corsac.Lang.Ir.Block;

/// <summary>
/// THE FUNCTIONS THAT NEVER TOUCH THE FPU'S STATE -- no x87 or MMX
/// instruction, no float value, no packed operation or packed block of their
/// own, no asm that keeps float state, no call through a pointer or to the
/// operating system, and no call but to another such function -- and so can
/// be called with the MMX registers full and the x87's state the caller's
/// own: no EMMS before the call, and no save of the x87's state in a packed
/// block on its account (Select.WantsX87, SettleWantsX87). What GCC's
/// -fipa-ra does for the registers a callee clobbers, for the FPU's state:
/// a fact computed bottom-up over the call graph, the callers' saves
/// relaxed where the callee is known to need none.
///
/// The greatest fixed point: every candidate free at first, then each one
/// that calls a function not free taken away until none is -- recursion
/// among free functions stays free, which is what it is.
///
/// OF THIS UNIT, AND OF THE OTHER UNITS OF ITS IMAGE (Elsewhere): each unit
/// marks its free functions with a local symbol NAME$fpufree (Marker), and a
/// project build hands every unit the markers its neighbours' objects define
/// (ProjectCompile's --inside-facts rounds, which compile again a unit whose
/// calls now answer otherwise). A name no one has said is free is not: a
/// call into another image, or a unit not yet compiled, keeps its saves.
/// </summary>
internal static class FpuFree
{
    public const string Suffix = "$fpufree";

    /// <summary>The marker of a function this unit found free: a local symbol at its start.</summary>
    public static string Marker(string function) => function + Suffix;

    /// <summary>The free functions of other units of this image (the facts' markers, the suffix taken off): Driver, from --inside-facts.</summary>
    public static HashSet<string> Elsewhere { get; set; } = new(StringComparer.Ordinal);

    /// <summary>The free functions this module's selection asks about: its own and Elsewhere's (X86Backend.Generate sets it, Select reads it).</summary>
    [ThreadStatic] private static HashSet<string>? _known, _defined;

    /// <summary>
    /// Whether a call's callee is free: this module's own by this module's
    /// verdict alone -- never by a fact another object, or this unit's last
    /// build, said of a name it defines now -- and another's by the facts.
    /// </summary>
    public static bool Known(string callee) => _known?.Contains(callee) == true
        || (_defined?.Contains(callee) == true ? _judged?.Invoke(callee) == true : Elsewhere.Contains(callee));

    /// <summary>
    /// The module's free functions and the names it defines, for the
    /// selection of its functions on this thread (X86Backend.Compile:
    /// workers select on threads of their own). `judged` answers for a
    /// function of the module whose body was not there to judge beforehand
    /// (the link's regeneration, which loads each body as it compiles it:
    /// X86Backend.DeferredFacts), when a call to it is first asked about.
    /// </summary>
    public static void Use(HashSet<string>? free, HashSet<string>? defined, Func<string, bool>? judged = null)
    {
        _known = free;
        _defined = defined;
        _judged = judged;
    }

    [ThreadStatic] private static Func<string, bool>? _judged;

    /// <summary>
    /// This module's free functions, and the names it defines (`defined`).
    /// A function with any cycle in its flow graph is none where the back
    /// edges poll (`polls`): the collector's Runtime.Poll, which BackEdgePolls
    /// puts on the loops after this, is no free function.
    /// </summary>
    public static HashSet<string> Compute(Module module, bool polls, out HashSet<string> defined)
    {
        defined = new(module.Functions.Select(f => f.Name), StringComparer.Ordinal);
        Dictionary<string, List<string>> calls = new(StringComparer.Ordinal);
        foreach (Function f in module.Functions)
            if (Summarise(f, polls) is { } callees) calls[f.Name] = callees;
        return Settle(calls, defined);
    }

    /// <summary>
    /// What one function says of itself: the functions it calls, or null
    /// where it touches the FPU's state itself, has no body to read, or has
    /// a loop the collector's poll will be put on (`polls`).
    /// </summary>
    public static List<string>? Summarise(Function f, bool polls)
    {
        if (f.Async is not null || f.Blocks.Count == 0) return null;
        List<string> callees = new();
        foreach (Block b in f.Blocks)
            foreach (Instr i in b.Instrs)
                if (Touches(i, callees)) return null;
        return polls && HasCycle(f) ? null : callees;
    }

    /// <summary>
    /// The greatest fixed point over the functions that say something of
    /// themselves (`calls`): each free until it calls one that is not --
    /// one of this module's (`here`) that is not free, or another's no
    /// fact calls free.
    /// </summary>
    public static HashSet<string> Settle(Dictionary<string, List<string>> calls, HashSet<string> here)
    {
        HashSet<string> free = new(calls.Keys, StringComparer.Ordinal);
        for (bool changed = true; changed;)
        {
            changed = false;
            foreach ((string name, List<string> callees) in calls)
                if (free.Contains(name) && callees.Any(c => !free.Contains(c) && (here.Contains(c) || !Elsewhere.Contains(c))))
                {
                    free.Remove(name);
                    changed = true;
                }
        }
        return free;
    }

    /// <summary>
    /// Whether the flow graph has a cycle: a retreating edge of a depth-first
    /// walk from the entry and every landing pad -- every loop BackEdgePolls
    /// may poll (it polls natural loops' back edges and every other retreating
    /// edge), whatever the blocks' order.
    /// </summary>
    private static bool HasCycle(Function f)
    {
        Dictionary<Block, int> state = new(ReferenceEqualityComparer.Instance);   // 1 on the path, 2 done
        Stack<(Block Block, int Next)> path = new();
        foreach (Block root in f.Blocks.Where(b => ReferenceEquals(b, f.Entry) || b.IsLandingPad))
        {
            if (state.ContainsKey(root)) continue;
            state[root] = 1;
            path.Push((root, 0));
            Dictionary<Block, List<Block>> succsOf = new(ReferenceEqualityComparer.Instance);
            while (path.Count > 0)
            {
                (Block at, int next) = path.Pop();
                if (!succsOf.TryGetValue(at, out List<Block>? succs)) succsOf[at] = succs = at.Successors.ToList();
                if (next < succs.Count)
                {
                    path.Push((at, next + 1));
                    Block s = succs[next];
                    if (!state.TryGetValue(s, out int seen)) { state[s] = 1; path.Push((s, 0)); }
                    else if (seen == 1) return true;
                }
                else state[at] = 2;
            }
        }
        return false;
    }

    /// <summary>Whether an IR instruction touches the FPU's state itself; a direct call's callee added to `callees` for the fixed point.</summary>
    private static bool Touches(Instr i, List<string> callees)
    {
        if (i.Op is Opcode.CallIndirect or Opcode.Syscall) return true;
        if (i.Dest is { Type: IrType.F32 or IrType.F64 }) return true;
        foreach (Operand o in i.Operands) if (o.Type is IrType.F32 or IrType.F64) return true;
        if (i.Op != Opcode.Call || i.Callee is not { } c) return false;
        if (!c.StartsWith(MachineIntrinsics.Prefix, StringComparison.Ordinal))
        {
            if (c != "__exception") callees.Add(c);
            return false;
        }
        if (c.StartsWith(MachineIntrinsics.Asm, StringComparison.Ordinal)) return Selector.AsmUsesFloatState(i);
        return c.StartsWith(MachineIntrinsics.PackedOpPrefix, StringComparison.Ordinal)
            || c.StartsWith(MachineIntrinsics.PackedSavePrefix, StringComparison.Ordinal)
            || c.StartsWith(MachineIntrinsics.PackedRestorePrefix, StringComparison.Ordinal)
            || c is MachineIntrinsics.StreamCopy64 or MachineIntrinsics.StreamFill64 or MachineIntrinsics.StreamEnd
                 or MachineIntrinsics.PackedFindChar4 or MachineIntrinsics.PackedFindChar8 or MachineIntrinsics.PackedEqual8
                 or MachineIntrinsics.PackedEnd or MachineIntrinsics.PackedEndFast
                 or MachineIntrinsics.PackedSaveX87 or MachineIntrinsics.PackedRestoreX87
                 or MachineIntrinsics.PackedSaveFxsr or MachineIntrinsics.PackedRestoreFxsr
                 or MachineIntrinsics.DoublePrecision or MachineIntrinsics.LoaderFini or MachineIntrinsics.SaveRegisters;
    }
}
