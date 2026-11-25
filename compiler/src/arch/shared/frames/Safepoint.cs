#nullable enable
namespace Corsac.Lang.Frames;

/// <summary>
/// What the collector must know at one call site, on any machine: where,
/// among the things this frame owns, the live references are while the
/// callee runs.
///
/// Registers are a mask by hardware number, and only callee-saved ones can
/// appear (those the target's StackMapLayout names): a value live across a
/// call is never left in a caller-saved register, because the selector
/// marks those busy at every call. The slots are byte offsets from the
/// frame's base register, all negative.
/// </summary>
public sealed class Safepoint
{
    public uint Registers { get; set; }
    public List<int> SlotOffsets { get; } = new();
}

/// <summary>
/// A FUNCTION'S STACK-MAP RECORD, built the same way for every target as
/// its calls are encoded: the function once, at its first call site, then
/// each site with the words its map names -- its live spill slots
/// (Safepoint.SlotOffsets) and the IR frame slot words live or exposed at
/// it (FrameSlotLiveness), or every IR slot word where the liveness left
/// the function as it was or never met the call (a map must not lack).
/// </summary>
public static class StackMapRecords
{
    /// <summary>The function's record: its start from the table's base, its frame, the registers its prologue saves, and its IR slots -- none where each call names its own, all of them otherwise.</summary>
    public static Corsac.Lang.Ir.StackMapTable.Function Function(Corsac.Lang.Ir.StackMapLayout layout, int start, int frameSize, uint saved,
        IEnumerable<int> slotWordOffsets, FrameSlotLiveness.Result? liveness)
        => new()
        {
            Start = start, FrameSize = frameSize, Saved = saved,
            Objects = liveness is null ? Corsac.Lang.Ir.StackMapTable.Words(slotWordOffsets, layout.WordBytes) : Array.Empty<int>(),
        };

    /// <summary>One call's site: its return address from the function's start, its live registers and the words its map names; a call with no safepoint has no map (its frame is read whole).</summary>
    public static Corsac.Lang.Ir.StackMapTable.Site Site(Corsac.Lang.Ir.StackMapLayout layout, object call, int ret, Safepoint? map,
        IEnumerable<int> slotWordOffsets, FrameSlotLiveness.Result? liveness)
    {
        if (map is null) return new(ret, true, 0, Array.Empty<int>());
        IEnumerable<int> named = map.SlotOffsets;
        if (liveness is not null) named = named.Concat(liveness.At.TryGetValue(call, out int[]? words) ? words : slotWordOffsets);
        int[] site = Corsac.Lang.Ir.StackMapTable.Words(named, layout.WordBytes);
        // No live slot is one shared empty list of words.
        return new(ret, false, map.Registers, site.Length == 0 ? Array.Empty<int>() : site);
    }
}
