#nullable enable
namespace Corsac.Lang;

public sealed class FieldDecl : MemberDecl
{
    public required TypeRef Type { get; init; }
    /// <summary>Declared with the event keyword: a delegate-typed field whose += and -= combine and remove.</summary>
    public bool IsEvent { get; init; }

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
    /// A STATIC ARRAY OF CONSTANTS, laid down in the image instead of built at
    /// run time (Binder.StaticArrayOf): lowering writes the array as an object
    /// in the data section and this field as its address, so the table exists
    /// before any code runs and the heap never holds it. Set in place of Init,
    /// which is then null; kept on the declaration because binding runs more
    /// than once and only the first pass sees the initialiser.
    /// </summary>
    public StaticArray? StaticData { get; set; }

    /// <summary>
    /// What it was WRITTEN as, which is not the same question as what code
    /// runs. <see cref="Init"/> is moved into a constructor or a StaticInit$
    /// and then cleared, so anything asking about the declaration rather than
    /// about the program -- a registry default, which the compiler both bakes
    /// into read sites and writes into the schema section -- has to read it
    /// from somewhere the move does not touch.
    /// </summary>
    public Expr? DeclaredInit { get; set; }

    /// <summary>
    /// The other names of a declaration that wrote several: the B and C of
    /// `const int A = 0, B = 1, C = 2;`. Each is a field in its own right with
    /// this one's type and modifiers, and whoever declares the members takes
    /// them along with it.
    /// </summary>
    // To read: one shared empty list until written (WritableMore), never
    // written through. The parser hands the extras to the type and lets the
    // list go (ForgetMore), so every field declaration -- and every copy the
    // monomorphiser makes of one -- held an empty list for nothing.
    public List<FieldDecl> More => _more ?? NoMore;
    /// <summary>To write: made on first use.</summary>
    public List<FieldDecl> WritableMore => _more ??= new();
    /// <summary>The extras, once the type has taken them as members of its own.</summary>
    internal void ForgetMore() => _more = null;
    private List<FieldDecl>? _more;
    private static readonly List<FieldDecl> NoMore = new();
}
