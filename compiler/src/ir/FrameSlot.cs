#nullable enable
using System.Text;

namespace Corsac.Lang.Ir;

/// <summary>A block of frame memory, sized and aligned; the backend places it.</summary>
public sealed class FrameSlot
{
    public int Id { get; }
    public int Bytes { get; }
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
