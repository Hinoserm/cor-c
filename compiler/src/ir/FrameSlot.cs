#nullable enable
using System.Text;

namespace Corsac.Lang.Ir;

/// <summary>A block of frame memory, sized and aligned; the backend places it.</summary>
public sealed class FrameSlot
{
    public int Id { get; }
    /// <summary>Its size: set again only before the backend places it -- a packed block's save area shrinks to the x87's 112 bytes where its save settles so (Selector.SettlePackedSaves).</summary>
    public int Bytes { get; internal set; }
    public int Align { get; }
    public string? Name { get; init; }

    internal FrameSlot(int id, int bytes, int align)
    {
        Id = id;
        Bytes = bytes;
        Align = align;
    }

    public override string ToString() => Name is null ? $"slot{Id}" : $"slot{Id}.{Name}";
}
