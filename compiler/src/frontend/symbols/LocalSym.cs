#nullable enable
namespace Corsac.Lang;

public sealed record LocalSym(int Slot, Type Type, string Name) : Sym
{
    /// <summary>
    /// This local lives in a HEAP CELL, because a lambda captured it.
    ///
    /// C# captures by reference: a captured variable stops being a stack slot
    /// and becomes storage both the enclosing method and the closure reach. So
    /// the frame slot holds a POINTER to a one-word cell, every read and write
    /// on either side goes through it, and there is exactly one copy of the
    /// value however many lambdas took it.
    ///
    /// Set during checking, read during code generation, which is safe because
    /// nothing is emitted until every body has been checked -- a lambda written
    /// after the last use of a local still boxes it.
    ///
    /// Only captured locals pay. Everything else stays in a frame slot, so a
    /// method with no lambda in it is unchanged.
    /// </summary>
    public bool Boxed { get; set; }

    /// <summary>
    /// WHICH LOCAL THIS IS, and not where it happens to live.
    ///
    /// A record compares every field it holds, and <see cref="Boxed"/> is set
    /// LATER -- capturing a local is what boxes it. A symbol already in a set
    /// then hashed to a different bucket and was never found again, so
    /// everything proved about a local was lost the moment a lambda captured
    /// it: `if (t is null) return false;` followed by a lambda reading t was
    /// told inside the lambda that t may be null.
    /// </summary>
    public bool Equals(LocalSym? other)
        => other is not null && Slot == other.Slot && Name == other.Name && Type.Equals(other.Type);

    public override int GetHashCode() => HashCode.Combine(Slot, Name);
}
