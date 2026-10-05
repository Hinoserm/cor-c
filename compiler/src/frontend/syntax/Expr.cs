#nullable enable
namespace Corsac.Lang;

// ---- expressions ------------------------------------------------------

public abstract class Expr : Node
{
    /// <summary>
    /// A lambda's or a method group's NATURAL TYPE (C# 10), where nothing
    /// says which delegate it is -- `var f = (int x) => x;`, `object o =
    /// Twice;` -- as the checker worked it out and spelt it, so the next
    /// expansion makes it when no source names it (Binder.NaturalTypes).
    /// </summary>
    public TypeRef? NaturalType { get; set; }
}
