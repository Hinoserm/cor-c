#nullable enable
namespace Corsac.Lang;

// ---- declarations -----------------------------------------------------

public sealed class CompilationUnit : Node
{
    public List<string> Usings { get; } = new();
    public List<TypeDecl> Types { get; } = new();

    /// <summary>
    /// Every TUPLE TYPE WITH NAMED ELEMENTS that was written anywhere, kept
    /// because a specialisation carries its arguments in its NAME and the
    /// argument list does not survive: `List&lt;(int A, int B)&gt;` becomes
    /// `List$ValueTuple_int_int`, and what the elements were called went with
    /// the brackets.
    ///
    /// The checker reads these before it checks anything, so the class it
    /// writes for a shape knows every naming that shape was ever given.
    /// </summary>
    public List<TypeRef> TupleNamings { get; } = new();
}
