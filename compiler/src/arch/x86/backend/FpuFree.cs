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
    [ThreadStatic] private static HashSet<string>? _known;

    public static bool Known(string callee) => _known?.Contains(callee) == true || Elsewhere.Contains(callee);

    /// <summary>The module's free functions, for the selection of its functions on this thread (X86Backend.Compile: workers select on threads of their own).</summary>
    public static void Use(HashSet<string>? free) => _known = free;

    /// <summary>
    /// This module's free functions. A
    /// function with a loop is none where the back edges poll (polls: the
    /// collector's Runtime.Poll, which is no free function).
    /// </summary>
    public static HashSet<string> Compute(Module module, bool polls)
    {
        Dictionary<string, List<string>> calls = new(StringComparer.Ordinal);
        foreach (Function f in module.Functions)
        {
            if (f.Async is not null || f.Blocks.Count == 0) continue;
            List<string>? callees = new();
            Dictionary<Block, int> order = new(ReferenceEqualityComparer.Instance);
            for (int k = 0; k < f.Blocks.Count; k++) order[f.Blocks[k]] = k;
            for (int k = 0; k < f.Blocks.Count && callees is not null; k++)
                foreach (Instr i in f.Blocks[k].Instrs)
                {
                    if (Touches(i, callees)) { callees = null; break; }
                    // A loop's back edge, where they poll.
                    if (polls)
                        foreach (Block t in i.Targets)
                            if (order.TryGetValue(t, out int at) && at <= k) { callees = null; break; }
                    if (callees is null) break;
                }
            if (callees is not null) calls[f.Name] = callees;
        }
        HashSet<string> free = new(calls.Keys, StringComparer.Ordinal);
        for (bool changed = true; changed;)
        {
            changed = false;
            foreach ((string name, List<string> callees) in calls)
                if (free.Contains(name) && callees.Any(c => !free.Contains(c) && !Elsewhere.Contains(c)))
                {
                    free.Remove(name);
                    changed = true;
                }
        }
        return free;
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
