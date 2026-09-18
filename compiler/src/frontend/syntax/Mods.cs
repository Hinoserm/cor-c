#nullable enable
namespace Corsac.Lang;

[Flags]
public enum Mods
{
    None      = 0,
    Public    = 1 << 0,
    Private   = 1 << 1,
    Protected = 1 << 2,
    Internal  = 1 << 3,
    Static    = 1 << 4,
    Abstract  = 1 << 5,
    Virtual   = 1 << 6,
    Override  = 1 << 7,
    Sealed    = 1 << 8,
    Readonly  = 1 << 9,
    Const     = 1 << 10,
    Async     = 1 << 11,

    /// <summary>
    /// This member must be set by whoever builds the object.
    ///
    /// A contract about the CALL SITE rather than about the member, which is
    /// what makes it worth having at all: a field that must be filled in is one
    /// the type can stop checking for itself, and the check moves to the one
    /// place that knows whether it was.
    /// </summary>
    Required  = 1 << 12,

    /// <summary>
    /// <c>unsafe</c>. Carried so that C# source compiles, and not enforced.
    ///
    /// In C# it gates pointers behind a project switch, which is a policy about
    /// projects rather than a fact about a machine. This machine's standard
    /// library touches raw memory throughout -- that is what a standard library
    /// on a real computer does -- so a gate would mean the word appearing in
    /// every file for no benefit.
    /// </summary>
    Unsafe    = 1 << 13,
    Volatile  = 1 << 14,
    Extern    = 1 << 15,

    /// <summary>
    /// One part of a type whose members may be written in another source
    /// declaration. The compilation-unit merge consumes this distinction
    /// before binding so a legal C# partial type is not a duplicate type.
    /// </summary>
    Partial   = 1 << 16,
}
