#nullable enable
namespace Corsac.Lang;

/// <summary>How an interface's type parameter may vary, as C# declares it.</summary>
public enum Variance : byte
{
    /// <summary>Neither: the argument must be exactly what was asked for.</summary>
    None = 0,

    /// <summary>
    /// `out T`. The parameter is only ever handed OUT, so an
    /// `IEnumerable&lt;FieldDecl&gt;` is an `IEnumerable&lt;MemberDecl&gt;`:
    /// everything read from it is a MemberDecl, which is true of every
    /// FieldDecl.
    /// </summary>
    Out = 1,

    /// <summary>
    /// `in T`. The parameter is only ever taken IN, so an
    /// `IComparer&lt;MemberDecl&gt;` is an `IComparer&lt;FieldDecl&gt;`: it
    /// can compare anything a MemberDecl can be, FieldDecls among them.
    /// </summary>
    In = 2,
}
