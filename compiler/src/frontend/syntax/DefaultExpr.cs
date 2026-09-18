#nullable enable
namespace Corsac.Lang;

/// `default(T)` -- the zero of a type.
///
/// Needed by generic code above all, which is where it earns its keep: a
/// Dictionary answering "nothing was there" has to produce a V without knowing
/// what V is. On this machine every value is a word, so the zero of a type is
/// the zero word -- null for a reference, false for a bool, 0 for a number --
/// and the type is carried only so the checker knows what came out.
public sealed class DefaultExpr : Expr
{
    public required TypeRef Type { get; init; }

    /// <summary>
    /// The type written was a TYPE PARAMETER, before anything was substituted
    /// into it.
    ///
    /// `default(T)` on an unconstrained T is oblivious in C#: it may be null
    /// when T turns out to be a reference type and it is still a T, which is
    /// what lets `out T value; value = default(T);` compile without a
    /// complaint -- .NET says the same thing on the outside with
    /// [MaybeNullWhen(false)]. Substitution turns the T into whatever it was
    /// bound to and the checker, meeting `default(string)`, would say the zero
    /// of a reference type is null and refuse the assignment -- in a library
    /// file whose author wrote nothing of the kind. So the fact that it WAS a
    /// parameter is recorded before the name is replaced.
    /// </summary>
    public bool OfTypeParameter { get; init; }
}
