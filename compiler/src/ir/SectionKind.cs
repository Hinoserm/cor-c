#nullable enable

namespace Corsac.Lang.Ir;

public enum SectionKind : byte
{
    Code,
    ReadOnlyData,
    Data,
    /// <summary>Zero-filled, occupies no bytes in the file.</summary>
    Uninitialised,
    /// <summary>Metadata a loader may read; not mapped for execution.</summary>
    Note,
}
