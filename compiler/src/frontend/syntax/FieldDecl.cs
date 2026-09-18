#nullable enable
namespace Corsac.Lang;

public sealed class FieldDecl : MemberDecl
{
    public required TypeRef Type { get; init; }

    /// <summary>
    /// What it starts as, until the binder moves it.
    ///
    /// Settable rather than init-only because moving it is exactly what
    /// happens: an instance initialiser goes into every constructor and a
    /// static one into the type's StaticInit$, and the field is then cleared
    /// so nothing runs it twice.
    /// </summary>
    public Expr? Init { get; set; }

    /// <summary>
    /// The other names of a declaration that wrote several: the B and C of
    /// `const int A = 0, B = 1, C = 2;`. Each is a field in its own right with
    /// this one's type and modifiers, and whoever declares the members takes
    /// them along with it.
    /// </summary>
    public List<FieldDecl> More { get; } = new();
}
