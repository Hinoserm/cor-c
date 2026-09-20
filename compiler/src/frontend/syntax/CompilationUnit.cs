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

    /// <summary>
    /// The settings this program declares, one schema per domain, worked out
    /// before anything is bound -- because a declared setting stops being a
    /// field at that point and becomes a property. The driver writes them
    /// into the object's .corsac.registry section.
    /// </summary>
    public List<Metadata.RegistrySchema> RegistrySchemas { get; } = new();

    /// <summary>
    /// Where each declared setting lives, by the path it is written with:
    /// "Settings.Canvas.Width" to "/corsac/paint/canvas/width".
    ///
    /// `Registry.IsSet(Settings.Canvas.Width)` must not EVALUATE its
    /// argument -- asking whether a value is set is not a question about a
    /// value -- so the compiler resolves the shape of the call instead, and
    /// this is what it resolves it against.
    /// </summary>
    public Dictionary<string, string> RegistryKeys { get; } = new(StringComparer.Ordinal);
}
