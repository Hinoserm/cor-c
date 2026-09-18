#nullable enable
namespace Corsac.Lang;

/// One name a deconstruction binds, and the type written in front of it if
/// there was one.
public sealed class Binding : Node
{
    public TypeRef? Type { get; init; }
    public required string Name { get; init; }

    /// <summary>
    /// What to ASSIGN to, when the target is something that already exists
    /// rather than a name being declared: the `a` and `b` of `(a, b) = (b, a)`,
    /// and the `keys[0]` of `(keys[0], keys[1]) = (keys[1], keys[0])`.
    ///
    /// C# calls it a deconstructing assignment and allows any assignable
    /// expression in each position. Name is a hidden one in that case, so
    /// everything that counts positions still counts the same.
    /// </summary>
    public Expr? Target { get; init; }

    /// <summary>
    /// The names INSIDE this one, when a target was written as a list of its
    /// own: the `(Block block, Instr at)` of
    /// <c>foreach ((long index, (Block block, Instr at)) in suspends)</c>.
    ///
    /// C# takes a value apart into targets, and a target may be another list --
    /// which comes apart the same way, out of the element this one binds. Name
    /// is then a hidden local holding that element, so everything below reads
    /// one name per position as it always did.
    /// </summary>
    public List<Binding>? Nested { get; init; }
}
