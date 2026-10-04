#nullable enable
namespace Corsac.Lang;

public sealed record ParamSym(int Index, Type Type, string Name, bool ByRef = false,
                             bool ReadOnly = false) : Sym
{
    /// <summary>A lambda or a local function reads it (Binder.Lookup).</summary>
    public bool Captured { get; set; }

    /// <summary>Something assigns it, increments it, or passes it by reference.</summary>
    public bool Written { get; set; }

    /// <summary>
    /// IT LIVES IN A HEAP CELL, as a captured local does (LocalSym.Boxed):
    /// captured and written, so the method and its closures must see one
    /// variable. A copy taken into the closure missed every later write --
    /// `archive ??= Read(...)` before the local functions that read it, made
    /// at the top of the block, left them holding null -- and missed every
    /// write the lambda made. One never written is the same as its copy, and
    /// pays nothing.
    /// </summary>
    public bool Boxed => Captured && Written && !ByRef;

    /// <summary>Which parameter this is; the flags are set later, as LocalSym's Boxed is.</summary>
    public bool Equals(ParamSym? other)
        => other is not null && Index == other.Index && Name == other.Name && ByRef == other.ByRef
           && ReadOnly == other.ReadOnly && Type.Equals(other.Type);

    public override int GetHashCode() => HashCode.Combine(Index, Name);
}
