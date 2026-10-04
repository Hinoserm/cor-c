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
    public bool Boxed => (Captured && Written || ForcedCell) && !ByRef;

    /// <summary>
    /// A generic local function captured it (Binder.PassCaptures): its
    /// address goes to the hoisted method, which may hand it on to a lambda
    /// of its own, so it lives in a cell whatever writes it.
    /// </summary>
    public bool ForcedCell { get; set; }

    /// <summary>
    /// A hoisted generic local function's captured variable (MethodDecl.
    /// Captures): by reference, and what it refers to is the enclosing
    /// method's CELL, so a lambda inside may hold that cell and share the
    /// one variable, as a lambda over the enclosing local itself does.
    /// </summary>
    public bool Cell { get; init; }

    /// <summary>Any captured variable of a generic local function (MethodDecl.Captures); each is a Cell.</summary>
    public bool CapturedVariable { get; init; }

    /// <summary>Which parameter this is; the flags are set later, as LocalSym's Boxed is.</summary>
    public bool Equals(ParamSym? other)
        => other is not null && Index == other.Index && Name == other.Name && ByRef == other.ByRef
           && ReadOnly == other.ReadOnly && Type.Equals(other.Type);

    public override int GetHashCode() => HashCode.Combine(Index, Name);
}
