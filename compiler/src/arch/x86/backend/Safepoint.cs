#nullable enable
using Corsac.Lang.Ir;

namespace Corsac.Lang.X86;

using Block = Corsac.Lang.Ir.Block;

/// <summary>
/// What the collector must know at one call site: where, among the things
/// this frame owns, the live references are while the callee runs.
///
/// Registers are a mask by hardware number, and only the callee-saved
/// three can appear: a value live across a call is never left in a
/// caller-saved register, because the selector marks those busy at every
/// call. The slots are EBP-relative byte offsets, all negative.
/// </summary>
public sealed class Safepoint
{
    public uint Registers { get; set; }
    public List<int> SlotOffsets { get; } = new();
}
